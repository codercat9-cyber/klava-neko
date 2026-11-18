using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace KlavaDesk;

/// <summary>
/// Мозг «Claude»: Claude Code CLI по подписке друга — режим печати с потоковым JSON на входе и выходе.
/// Процесс живёт весь разговор: запуск claude.exe стоит пару секунд, платить их на каждой реплике незачем.
/// Беседу он помнит сам; новому процессу первой репликой пересказывается недавняя история Клавы.
/// --safe-mode: без CLAUDE.md, хуков (хук озвучки иначе прочитал бы её ответ чужим голосом), плагинов и MCP;
/// --tools "": никаких инструментов — на компьютере Claude в роли Клавы ничего не делает.
/// </summary>
internal sealed class ClaudeCode : IDisposable
{
    private readonly KlavaConfig _cfg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _procLock = new();
    private Process? _proc;
    private string _procKey = "";
    private Channel<JsonNode>? _events;
    private Task? _drain;   // дочитывание недослушанного ответа, чтобы его хвост не попал в следующую реплику

    public ClaudeCode(KlavaConfig cfg) => _cfg = cfg;

    /// <summary>Забыть беседу: следующая реплика поднимет новый процесс.</summary>
    public void Reset() => Kill();

    public void Dispose() => Kill();

    /// <summary>Путь к claude.exe: из npm-папки пользователя или из PATH.</summary>
    public static string? FindExe()
    {
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.exe");
        if (File.Exists(npm)) return npm;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "claude.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }   // кривой элемент PATH
        }
        return null;
    }

    /// <summary>Последний известный ответ LoggedInAsync (null — ещё не спрашивали): меню и панель прячут Claude без входа.</summary>
    public static bool? LastLoggedIn { get; private set; }

    /// <summary>Залогинен ли CLI (claude auth status). null — CLI не нашёлся или не ответил.</summary>
    public static async Task<bool?> LoggedInAsync()
    {
        var exe = FindExe();
        if (exe is null) return null;
        var psi = NewPsi(exe, Path.GetTempPath());
        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("status");
        using var p = Process.Start(psi);
        if (p is null) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            _ = p.StandardError.ReadToEndAsync(cts.Token);
            // ConfigureAwait(false): зовут и с UI-потока (агенты), и из проверок, что ждут синхронно
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            LastLoggedIn = JsonNode.Parse(await outTask.ConfigureAwait(false))?["loggedIn"]?.GetValue<bool>();
            return LastLoggedIn;
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException or InvalidOperationException)
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            return null;
        }
    }

    /// <summary>
    /// Одна реплика: текст ответа кусками по мере генерации. <paramref name="seed"/> зовётся, только если
    /// процесс новый, — пересказ недавней истории. Бросает InvalidOperationException с понятной причиной.
    /// </summary>
    public async IAsyncEnumerable<string> AskAsync(string model, string system, Func<string> seed, string userText, byte[]? image,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var finished = false;
        ChannelReader<JsonNode>? reader = null;
        try
        {
            if (_drain is not null)
            {
                try { await _drain.WaitAsync(TimeSpan.FromSeconds(35), ct); }
                catch (TimeoutException) { Kill(); }
                _drain = null;
            }

            var fresh = false;
            var key = model + "\n" + system;
            lock (_procLock)
            {
                if (_proc is null || _proc.HasExited || _procKey != key)
                {
                    KillLocked();
                    Start(model, system);
                    _procKey = key;
                    fresh = true;
                }
                reader = _events!.Reader;
            }

            var content = new JsonArray();
            if (image is not null)
                content.Add(new JsonObject
                {
                    ["type"] = "image",
                    ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(image) },
                });
            content.Add(new JsonObject { ["type"] = "text", ["text"] = fresh ? seed() + userText : userText });
            var line = new JsonObject
            {
                ["type"] = "user",
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = content },
            }.ToJsonString();
            Process proc;
            lock (_procLock) proc = _proc ?? throw new InvalidOperationException("Claude Code не запустился");
            await proc.StandardInput.WriteLineAsync(line.AsMemory(), ct);
            await proc.StandardInput.FlushAsync(ct);

            var gotDelta = false;
            await foreach (var ev in reader.ReadAllAsync(ct))
            {
                switch (ev["type"]?.GetValue<string>())
                {
                    case "stream_event":
                        var e = ev["event"];
                        if (e?["type"]?.GetValue<string>() == "content_block_delta" && e["delta"]?["type"]?.GetValue<string>() == "text_delta")
                        {
                            var t = e["delta"]?["text"]?.GetValue<string>();
                            if (!string.IsNullOrEmpty(t)) { gotDelta = true; yield return t; }
                        }
                        break;
                    case "assistant" when !gotDelta:
                        // частичных сообщений не было (старый CLI) — берём текст целого сообщения
                        if (ev["message"]?["content"] is JsonArray blocks)
                            foreach (var b in blocks)
                                if (b?["type"]?.GetValue<string>() == "text" && b["text"]?.GetValue<string>() is { Length: > 0 } whole)
                                {
                                    gotDelta = true;
                                    yield return whole;
                                }
                        break;
                    case "result":
                        finished = true;
                        if (ev["is_error"]?.GetValue<bool>() == true)
                            throw new InvalidOperationException(Explain(ev["result"]?.GetValue<string>() ?? ev["subtype"]?.GetValue<string>() ?? "ошибка"));
                        break;
                }
                if (finished) break;
            }
            if (!finished) throw new InvalidOperationException($"Claude Code завершился на полуслове — глянь {LogFile}");
        }
        finally
        {
            if (!finished)
            {
                // перебили — процесс с недоговорённым ответом не нужен; просто недослушали — дочитать хвост в фоне
                if (ct.IsCancellationRequested || reader is null) Kill();
                else _drain = DrainAsync(reader);
            }
            _gate.Release();
        }
    }

    private static string Explain(string raw) =>
        raw.Contains("Not logged in", StringComparison.OrdinalIgnoreCase) || raw.Contains("/login", StringComparison.Ordinal)
            ? "Claude Code не залогинен — в терминале набери: claude auth login"
            : raw.Length > 300 ? raw[..300] : raw;

    private async Task DrainAsync(ChannelReader<JsonNode> reader)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await foreach (var ev in reader.ReadAllAsync(cts.Token))
                if (ev["type"]?.GetValue<string>() == "result") return;
        }
        catch (OperationCanceledException) { }
        Kill();
    }

    private string LogFile => _cfg.Abs(@"data\logs\claude-code.log");

    private static ProcessStartInfo NewPsi(string exe, string workDir)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // Клаву запустили изнутри сессии Claude (терминал десктопа): переменные хоста уводят CLI на чужую авторизацию
        if (psi.Environment.ContainsKey("CLAUDECODE"))
        {
            foreach (var k in psi.Environment.Keys.ToList())
                if (k is "CLAUDECODE" or "ANTHROPIC_BASE_URL" or "CLAUDE_PID" or "CLAUDE_EFFORT" or "CLAUDE_AGENT_SDK_VERSION"
                    || k.StartsWith("CLAUDE_CODE_", StringComparison.Ordinal))
                    psi.Environment.Remove(k);
        }
        return psi;
    }

    private void Start(string model, string system)
    {
        var exe = FindExe() ?? throw new InvalidOperationException("не нашла claude.exe — Claude Code не установлен");
        var work = _cfg.Abs(@"data\claude");   // пустая папка: никаких чужих CLAUDE.md и настроек проекта
        Directory.CreateDirectory(work);
        var psi = NewPsi(exe, work);
        foreach (var a in new[]
                 {
                     "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                     "--include-partial-messages", "--safe-mode", "--tools", "", "--no-session-persistence",
                     "--model", model, "--effort", "low", "--system-prompt", system,
                 })
            psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("claude.exe не запустился");
        try { Native.TieToLifetime(p); }
        catch (InvalidOperationException ex) { Log.Error("claude.exe не привязан к жизни Клавы", ex); }

        var events = Channel.CreateUnbounded<JsonNode>(new UnboundedChannelOptions { SingleWriter = true });
        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
        var sink = new StreamWriter(LogFile, append: false) { AutoFlush = true };
        var gate = new object();
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (gate) sink.WriteLine(e.Data); };
        p.BeginErrorReadLine();
        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await p.StandardOutput.ReadLineAsync()) is not null)
                {
                    JsonNode? node;
                    try { node = JsonNode.Parse(line); }
                    catch (JsonException) { lock (gate) sink.WriteLine(line); continue; }   // не JSON — в лог
                    if (node is null) continue;
                    var type = node["type"]?.GetValue<string>();
                    if (type is "system" or "result") lock (gate) sink.WriteLine(line.Length > 600 ? line[..600] : line);
                    events.Writer.TryWrite(node);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            finally
            {
                events.Writer.TryComplete();
                lock (gate) sink.Dispose();
            }
        });
        _proc = p;
        _events = events;
        Log.Write($"запущен Claude Code (модель {model}, PID {p.Id})");
    }

    private void Kill()
    {
        lock (_procLock) KillLocked();
    }

    private void KillLocked()
    {
        var p = _proc;
        _proc = null;
        _procKey = "";
        if (p is null) return;
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception ex) { Log.Error("не смогла погасить claude.exe", ex); }
        p.Dispose();
    }
}
