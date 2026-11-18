using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KlavaPult;

/// <summary>Мозг из klava.json — только то, что нужно пульту.</summary>
internal sealed record BrainInfo(string Id, string Name, string Kind, string Model, string? ChatModel, string? Fallback, string[] Aliases);

/// <summary>
/// Где живёт Клава и что с ней сейчас: папка, klava.json, процесс, службы на портах, ключи (только «есть/нет» —
/// значения пульт не показывает и никуда не пишет), квоты аккаунтов AG Tools.
/// </summary>
internal sealed class KlavaHome
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string SavedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "KlavaPult", "root.txt");

    public string Root { get; }
    public string Desk => Path.Combine(Root, "bin", "KlavaDesk.exe");
    public string ConfigFile => Path.Combine(Root, "klava.json");
    public string LogFile => Path.Combine(Root, "data", "logs", "klava.log");

    private KlavaHome(string root) => Root = root;

    private static bool LooksLikeRoot(string? d) =>
        !string.IsNullOrWhiteSpace(d) && Directory.Exists(d)
        && (File.Exists(Path.Combine(d, "START_KLAVA.ps1")) || File.Exists(Path.Combine(d, "Klava.sln")) || File.Exists(Path.Combine(d, "bin", "KlavaDesk.exe")));

    /// <summary>KLAVA_HOME → папка над pult_bin (и выше, если пульт запущен из app\KlavaPult\bin\…) → сохранённый выбор.</summary>
    public static KlavaHome? Find()
    {
        var env = Environment.GetEnvironmentVariable("KLAVA_HOME");
        if (LooksLikeRoot(env)) return new KlavaHome(Path.GetFullPath(env!));
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 7 && dir is not null; i++, dir = dir.Parent)
            if (LooksLikeRoot(dir.FullName)) return new KlavaHome(dir.FullName);
        try
        {
            if (File.Exists(SavedRoot) && File.ReadAllText(SavedRoot).Trim() is var saved && LooksLikeRoot(saved)) return new KlavaHome(saved);
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>Друг сам показал папку — запомнить.</summary>
    public static KlavaHome? Use(string dir)
    {
        if (!LooksLikeRoot(dir)) return null;
        Directory.CreateDirectory(Path.GetDirectoryName(SavedRoot)!);
        File.WriteAllText(SavedRoot, dir);
        return new KlavaHome(dir);
    }

    // ---------------------------------------------------------------- klava.json

    public JsonObject? Config()
    {
        try { return File.Exists(ConfigFile) ? JsonNode.Parse(File.ReadAllText(ConfigFile)) as JsonObject : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    public static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public List<BrainInfo> Brains(JsonObject? cfg)
    {
        var list = new List<BrainInfo>();
        if (cfg?["brains"] is not JsonArray arr) return list;
        foreach (var b in arr.OfType<JsonObject>())
            list.Add(new BrainInfo(Str(b["id"]) ?? "?", Str(b["name"]) ?? "?", Str(b["kind"]) ?? "openai", Str(b["model"]) ?? "",
                Str(b["chatModel"]), Str(b["fallback"]),
                b["aliases"] is JsonArray a ? a.Select(Str).OfType<string>().ToArray() : []));
        return list;
    }

    /// <summary>Порт из адреса вида http://127.0.0.1:8095 (null — не адрес).</summary>
    public static int? PortOf(string? url) => Uri.TryCreate(url ?? "", UriKind.Absolute, out var u) ? u.Port : null;

    /// <summary>Порт прокси AG Tools (~/.antigravity_tools/gui_config.json → proxy.port).</summary>
    public static int AgPort()
    {
        try
        {
            var f = Path.Combine(Home, ".antigravity_tools", "gui_config.json");
            if (File.Exists(f) && JsonNode.Parse(File.ReadAllText(f))?["proxy"]?["port"] is JsonValue v && v.TryGetValue<int>(out var p)) return p;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { }
        return 8045;
    }

    // ---------------------------------------------------------------- процесс

    public Process? Running()
    {
        foreach (var p in Process.GetProcessesByName("KlavaDesk"))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is null || path.StartsWith(Path.Combine(Root, "bin"), StringComparison.OrdinalIgnoreCase)) return p;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return p; }
            p.Dispose();
        }
        return null;
    }

    // ---------------------------------------------------------------- службы и ключи

    public static async Task<bool> PortOpenAsync(int port, int ms = 350)
    {
        using var tcp = new TcpClient();
        using var cts = new CancellationTokenSource(ms);
        try { await tcp.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false); return true; }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
    }

    /// <summary>Есть ли ключ: переменная окружения (процесса и пользователя) или строка в ~/.dsh/.env — как ищет сама Клава.</summary>
    public static bool HasKey(string name) => KeyValue(name) is not null;

    /// <summary>Значение ключа — только для запроса к своему провайдеру; в вывод и файлы не попадает.</summary>
    public static string? KeyValue(string name)
    {
        var v = Environment.GetEnvironmentVariable(name) ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        var file = Path.Combine(Home, ".dsh", ".env");
        if (!File.Exists(file)) return null;
        try
        {
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
                var eq = line.IndexOf('=');
                if (eq <= 0 || line.StartsWith('#') || line[..eq].Trim() != name) continue;
                var val = line[(eq + 1)..].Trim().Trim('"', '\'');
                return val.Length > 0 ? val : null;
            }
        }
        catch (IOException) { }
        return null;
    }

    public static string? AgKey()
    {
        try
        {
            var f = Path.Combine(Home, ".antigravity_tools", "gui_config.json");
            return File.Exists(f) ? Str(JsonNode.Parse(File.ReadAllText(f))?["proxy"]?["api_key"]) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    public static string? AgExe()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Antigravity Tools", "antigravity-tools.exe");
        return File.Exists(exe) ? exe : null;
    }

    /// <summary>Codex для Windows (%LOCALAPPDATA%\OpenAI\Codex\bin\хэш\codex.exe) или codex.exe из PATH.</summary>
    public static string? CodexExe()
    {
        try
        {
            var bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
            if (Directory.Exists(bin))
            {
                var fresh = Directory.EnumerateDirectories(bin).Select(d => Path.Combine(d, "codex.exe")).Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (fresh is not null) return fresh;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { var p = Path.Combine(dir.Trim(), "codex.exe"); if (File.Exists(p)) return p; }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>Claude Code CLI залогинен? (claude auth status → loggedIn). null — CLI нет или не ответил за 20 с.</summary>
    public static async Task<bool?> ClaudeLoggedInAsync()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.exe");
        if (!File.Exists(exe)) return null;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                                              StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Path.GetTempPath() };
        psi.ArgumentList.Add("auth");
        psi.ArgumentList.Add("status");
        using var p = Process.Start(psi);
        if (p is null) return null;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var outTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            _ = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return JsonNode.Parse(await outTask.ConfigureAwait(false))?["loggedIn"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or JsonException or InvalidOperationException)
        {
            try { p.Kill(); } catch (InvalidOperationException) { }
            return null;
        }
    }

    public static bool CodexLoggedIn()
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h : Path.Combine(Home, ".codex");
        var f = Path.Combine(home, "auth.json");
        try { return File.Exists(f) && JsonNode.Parse(File.ReadAllText(f))?["tokens"] is JsonObject; }
        catch (Exception ex) when (ex is JsonException or IOException) { return false; }
    }

    // ---------------------------------------------------------------- квоты AG Tools

    /// <summary>По каждой модели: на скольких аккаунтах она есть и жива, лучший процент. Имена вида gemini-4-argon-high.</summary>
    public static (List<QuotaView.Row> Rows, int Accounts) AgQuotas()
    {
        var dir = Path.Combine(Home, ".antigravity_tools", "accounts");
        var per = new Dictionary<string, (int Live, int Total, int Best)>(StringComparer.OrdinalIgnoreCase);
        var accounts = 0;
        if (!Directory.Exists(dir)) return ([], 0);
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            try
            {
                var n = JsonNode.Parse(File.ReadAllText(file));
                if (n is null || n["disabled"] is JsonValue d && d.TryGetValue<bool>(out var dd) && dd) continue;
                if (n["quota"]?["models"] is not JsonArray ms) continue;
                accounts++;
                foreach (var m in ms)
                {
                    var name = Str(m?["name"]);
                    if (string.IsNullOrEmpty(name)) continue;
                    var pct = m!["percentage"] is JsonValue pv && pv.TryGetValue<int>(out var pi) ? pi
                        : m["percentage"] is JsonValue pd && pd.TryGetValue<double>(out var pdd) ? (int)pdd : 0;
                    per.TryGetValue(name, out var cur);
                    per[name] = (cur.Live + (pct > 0 ? 1 : 0), cur.Total + 1, Math.Max(cur.Best, pct));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException) { }
        }
        var rows = per.Where(kv => !kv.Key.Contains("image", StringComparison.OrdinalIgnoreCase))
            .Select(kv => new QuotaView.Row(kv.Key, kv.Value.Live, kv.Value.Total, kv.Value.Best))
            .OrderByDescending(r => r.Live).ThenByDescending(r => r.Best).ThenBy(r => r.Model).ToList();
        return (rows, accounts);
    }

    // ---------------------------------------------------------------- проверка связи

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    /// <summary>Живая проверка: AG Tools /v1/models, баланс DeepSeek, вход Codex, ключ Жмини. Ключи в отчёт не попадают.</summary>
    public static async Task<List<string>> CheckLinksAsync()
    {
        var lines = new List<string>();
        var sw = Stopwatch.StartNew();
        var port = AgPort();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/v1/models");
            if (AgKey() is { } k) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", k);
            using var r = await Http.SendAsync(req).ConfigureAwait(false);
            var body = await r.Content.ReadAsStringAsync().ConfigureAwait(false);
            var count = r.IsSuccessStatusCode && JsonNode.Parse(body)?["data"] is JsonArray a ? a.Count : 0;
            lines.Add(r.IsSuccessStatusCode
                ? $"✓ Antigravity Tools (порт {port}): {count} моделей, {sw.ElapsedMilliseconds} мс"
                : $"✗ Antigravity Tools (порт {port}): HTTP {(int)r.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            lines.Add($"✗ Antigravity Tools (порт {port}) не отвечает: {ex.Message}");
        }
        if (KeyValue("DEEPSEEK_API_KEY") is { } ds)
        {
            sw.Restart();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ds);
                using var r = await Http.SendAsync(req).ConfigureAwait(false);
                var n = JsonNode.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false));
                var bal = n?["balance_infos"] is JsonArray bi
                    ? string.Join(", ", bi.Select(x => $"{Str(x?["total_balance"])} {Str(x?["currency"])}"))
                    : "?";
                var ok = n?["is_available"] is JsonValue av && av.TryGetValue<bool>(out var avb) && avb;
                lines.Add(r.IsSuccessStatusCode
                    ? $"{(ok ? "✓" : "✗")} DeepSeek: баланс {bal}{(ok ? "" : " — пополни счёт")}, {sw.ElapsedMilliseconds} мс"
                    : $"✗ DeepSeek: HTTP {(int)r.StatusCode} (ключ не принят?)");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                lines.Add($"✗ DeepSeek не отвечает: {ex.Message}");
            }
        }
        else lines.Add("· DeepSeek: ключа нет (DEEPSEEK_API_KEY в окружении или в ~/.dsh/.env)");
        lines.Add(CodexExe() is null ? "· Codex: не установлен (Codex для Windows или npm i -g @openai/codex)"
            : CodexLoggedIn() ? "✓ Codex: установлен, вход ChatGPT есть" : "✗ Codex: установлен, но не залогинен — в терминале codex login");
        lines.Add(HasKey("GEMINI_API_KEY") ? "· Жминя: ключ есть (квоту видно только по ответам)" : "· Жминя: ключа нет");
        return lines;
    }

    // ---------------------------------------------------------------- журнал

    /// <summary>Хвост журнала: последние строки (журнал открыт Клавой — читаем с общим доступом).</summary>
    public static (List<string> Lines, long Pos) Tail(string file, int maxLines, long from = -1)
    {
        if (!File.Exists(file)) return ([], 0);
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = fs.Length;
        var start = from >= 0 && from <= len ? from : Math.Max(0, len - 96 * 1024);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs, Encoding.UTF8);
        var text = sr.ReadToEnd();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (from < 0 && start > 0 && lines.Count > 0) lines.RemoveAt(0);   // первая строка обрезана посередине
        return (lines.Count > maxLines ? lines[^maxLines..] : lines, len);
    }
}
