using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Мозг «ChatGPT через Codex»: официальный Codex CLI по подписке ChatGPT друга (вход — «codex login», он же у Codex
/// для Windows: ~/.codex/auth.json, auth_mode chatgpt). На каждую реплику — «codex exec --json»: ответ приходит
/// событием item.completed с item.type = agent_message. Замер 18.11: ~6 с на короткую фразу (gpt-5.6-luna, gpt-6-luna).
///
/// Ключи и токены Клава не трогает — их держит сам Codex. --ignore-user-config: без MCP, плагинов и модели «ultra» из
/// ~/.codex/config.toml (иначе каждая фраза думала бы минуту); --sandbox read-only и пустая рабочая папка: на компьютере
/// Codex в роли Клавы ничего не делает (руки у неё — свои инструменты, через другие мозги); --ephemeral — без следов
/// сессий. Codex не помнит беседу между вызовами, поэтому недавняя история едет в каждой реплике.
/// </summary>
internal static class CodexCli
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Где Codex держит вход: $CODEX_HOME или ~/.codex.</summary>
    private static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h : Path.Combine(Home, ".codex");

    /// <summary>
    /// codex.exe: свежий из Codex для Windows (%LOCALAPPDATA%\OpenAI\Codex\bin\&lt;хэш&gt;\ — хэш меняется с обновлениями),
    /// иначе из PATH (npm i -g @openai/codex кладёт codex.exe рядом с codex.cmd в vendor — ищем exe по PATH).
    /// </summary>
    public static string? FindExe()
    {
        try
        {
            var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(bin))
            {
                var fresh = Directory.EnumerateDirectories(bin)
                    .Select(d => Path.Combine(d, "codex.exe"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
                if (fresh is not null) return fresh;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim(), "codex.exe");
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Вошёл ли друг в Codex: есть auth.json с токенами ChatGPT или ключом. Только проверка файла — без сети.</summary>
    public static bool LoggedIn()
    {
        var file = Path.Combine(CodexHome, "auth.json");
        if (!File.Exists(file)) return false;
        try
        {
            var n = JsonNode.Parse(File.ReadAllText(file));
            return n?["tokens"] is JsonObject || n?["OPENAI_API_KEY"] is JsonValue v && v.TryGetValue<string>(out var k) && k.Length > 0;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Модели для меню: та, что стоит в ~/.codex/config.toml (model = "…"), и все, что Codex видел у подписки
    /// (models_cache.json — его обновляет сам Codex, так что новые модели появятся без правок Клавы).
    /// </summary>
    public static List<string> Models()
    {
        var list = new List<string>();
        var toml = Path.Combine(CodexHome, "config.toml");
        try
        {
            if (File.Exists(toml))
                foreach (var raw in File.ReadLines(toml))
                {
                    var line = raw.Trim();
                    if (line.StartsWith('[')) break;   // только верхний уровень, не профили
                    if (!line.StartsWith("model", StringComparison.Ordinal)) continue;
                    var eq = line.IndexOf('=');
                    if (eq < 0 || line[..eq].Trim() != "model") continue;
                    var v = line[(eq + 1)..].Trim().Trim('"', '\'');
                    if (v.Length > 0) list.Add(v);
                }
        }
        catch (IOException) { }
        foreach (var m in CachedModels()) if (!list.Contains(m, StringComparer.OrdinalIgnoreCase)) list.Add(m);
        return list;
    }

    /// <summary>Модели, которые Codex сам видел у подписки (~/.codex/models_cache.json) — для меню. Пусто — кэша нет.</summary>
    public static List<string> CachedModels()
    {
        var file = Path.Combine(CodexHome, "models_cache.json");
        if (!File.Exists(file)) return [];
        try
        {
            var n = JsonNode.Parse(File.ReadAllText(file));
            var arr = n as JsonArray ?? n?["models"] as JsonArray ?? n?["data"] as JsonArray;
            return (arr ?? []).Select(m => (m?["slug"] ?? m?["id"])?.GetValue<string>()).OfType<string>()
                .Where(s => !s.Contains("review", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { return []; }
    }

    /// <summary>
    /// Одна реплика. Текст ответа целиком (Codex exec отдаёт сообщение одним событием). Ошибка — InvalidOperationException
    /// с понятной причиной: нет Codex, не вошёл, лимит подписки, таймаут.
    /// </summary>
    /// <param name="model">Модель подписки; пусто — та, что Codex берёт по умолчанию.</param>
    /// <param name="effort">Уровень размышлений: minimal/low/medium/high (для голоса — low).</param>
    public static async IAsyncEnumerable<string> AskAsync(string model, string effort, string prompt, byte[]? image, TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var exe = FindExe() ?? throw new InvalidOperationException("Codex не установлен: поставь Codex для Windows или «npm i -g @openai/codex»");
        if (!LoggedIn()) throw new InvalidOperationException("Codex не залогинен: в терминале «codex login» (вход аккаунтом ChatGPT)");
        var work = Path.Combine(Path.GetTempPath(), "klava-codex");
        Directory.CreateDirectory(work);
        string? imageFile = null;
        if (image is not null)
        {
            imageFile = Path.Combine(work, $"screen_{Environment.ProcessId}_{Environment.TickCount64}.jpg");
            await File.WriteAllBytesAsync(imageFile, image, ct).ConfigureAwait(false);
        }

        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // по умолчанию stdin идёт в кодировке консоли (cp866/1251), и Codex отвергает кириллицу: «input is not valid UTF-8»
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = work,
        };
        foreach (var a in new[] { "exec", "--json", "--skip-git-repo-check", "--ephemeral", "--ignore-user-config", "--color", "never",
                                   "-s", "read-only", "-C", work, "-c", $"model_reasoning_effort=\"{(effort.Length > 0 ? effort : "low")}\"" })
            psi.ArgumentList.Add(a);
        if (!string.IsNullOrWhiteSpace(model)) { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(model.Trim()); }
        if (imageFile is not null) { psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(imageFile); }
        psi.ArgumentList.Add("-");   // текст — со stdin: длинный характер не лезет в командную строку

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Codex не запустился");
        using var life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        life.CancelAfter(timeout);
        using var kill = life.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        var errTail = new StringBuilder();
        var errTask = Task.Run(async () =>
        {
            while (await proc.StandardError.ReadLineAsync().ConfigureAwait(false) is { } l)
                lock (errTail) { errTail.AppendLine(l); if (errTail.Length > 4000) errTail.Remove(0, errTail.Length - 4000); }
        }, CancellationToken.None);
        try
        {
            proc.StandardInput.NewLine = "\n";
            await proc.StandardInput.WriteAsync(prompt.AsMemory(), life.Token).ConfigureAwait(false);
            proc.StandardInput.Close();

            var said = false;
            string? failure = null;
            while (true)
            {
                string? line;
                try { line = await proc.StandardOutput.ReadLineAsync(life.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new InvalidOperationException($"Codex не ответил за {(int)timeout.TotalSeconds} с");
                }
                if (line is null) break;
                if (!line.StartsWith('{')) continue;
                JsonNode? ev;
                try { ev = JsonNode.Parse(line); }
                catch (JsonException) { continue; }
                if (ev is null) continue;
                var type = ev["type"]?.GetValue<string>();
                var item = ev["item"];
                if (type == "item.completed" && item?["type"]?.GetValue<string>() == "agent_message")
                {
                    var text = item["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text)) { said = true; yield return text; }
                }
                else if (type is "turn.failed" or "error")
                {
                    var errNode = ev["error"];
                    failure = (errNode?["message"] ?? ev["message"])?.ToString() ?? ev.ToJsonString();
                }
            }
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            if (!said)
            {
                string tail;
                lock (errTail) tail = errTail.ToString().Trim();
                var why = failure ?? (tail.Length > 0 ? tail[Math.Max(0, tail.Length - 300)..] : $"код выхода {proc.ExitCode}, ответа нет");
                throw new InvalidOperationException($"Codex: {why.Replace('\n', ' ')}");
            }
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { await errTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
            if (imageFile is not null) try { File.Delete(imageFile); } catch (IOException) { }
        }
    }
}
