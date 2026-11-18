using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Автонастройка мозгов одной кнопкой (и лёгкая — при каждом запуске Клавы):
///  1. прокси для облака (NetProxy: переменные, системный, порты Karing/Clash/v2ray — с проверкой);
///  2. Antigravity Tools: порт и ключ из его gui_config.json, запуск программы, список моделей с его аккаунтов,
///     умная модель для задач и быстрая для болтовни, пробный ответ;
///  3. ключи других провайдеров из переменных окружения и ~/.dsh/.env — профиль заводится сам, а путь
///     (напрямую или через прокси) выбирается по реальной проверке /models;
///  4. локальные OpenAI-совместимые серверы (Ollama, LM Studio), если запущены;
///  5. Claude Code — установлен ли и залогинен.
/// Ключи в klava.json не пишутся: в профиле только «env:ИМЯ» или «antigravity-tools».
/// </summary>
internal static class AutoSetup
{
    private sealed record Provider(string Id, string Name, string Url, string[] Env, string Model, string[] Aliases, bool Vision, string? ModelInclude = null, string? ModelExclude = null);

    private static readonly Provider[] Providers =
    [
        new("gemini", "Жминя (ключ API)", "https://generativelanguage.googleapis.com/v1beta/openai", ["GEMINI_API_KEY", "GOOGLE_API_KEY"], "gemini-3.5-flash", ["жмин", "гемин", "джемин", "gemini", "гугл"], true,
            "^gemini-", "tts|image|embed|live|audio|transcribe|omni|robotics|computer|customtools"),
        new("openai", "OpenAI (GPT)", "https://api.openai.com/v1", ["OPENAI_API_KEY"], "gpt-5-mini", ["гпт", "gpt", "опенай", "чатгпт"], true,
            "^(gpt|o\\d|chatgpt)", "audio|realtime|tts|transcribe|image|search|embedding|instruct"),
        new("anthropic", "Anthropic (Claude API)", "https://api.anthropic.com/v1", ["ANTHROPIC_API_KEY"], "claude-sonnet-4-5", ["клод апи", "антропик", "anthropic"], true),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1", ["OPENROUTER_API_KEY"], "openai/gpt-4o-mini", ["роутер", "openrouter"], true),
        new("groq", "Groq (быстрая Llama)", "https://api.groq.com/openai/v1", ["GROQ_API_KEY"], "llama-3.3-70b-versatile", ["грок", "groq"], false),
        new("xai", "xAI Grok", "https://api.x.ai/v1", ["XAI_API_KEY"], "grok-4", ["грок икс", "grok", "маск"], true),
        new("mistral", "Mistral", "https://api.mistral.ai/v1", ["MISTRAL_API_KEY"], "mistral-large-latest", ["мистраль", "mistral"], false),
    ];

    // Кого брать у AG Tools: для задач — думающие (Argon, Opus 5.5, Sonnet 5.5), для болтовни — быстрые (Argon Low, Flash Low)
    private static readonly string[] AgSmart = ["gemini-4-argon", "gemini-4-argon-high", "claude-opus-5-5", "claude-opus-5-5-high", "claude-sonnet-5-5", "gemini-3.8-flash-high", "gpt-oss-120b"];
    private static readonly string[] AgFast = ["gemini-4-argon-low", "gemini-3.8-flash-low", "claude-opus-5-5-low", "claude-sonnet-5-5-low", "gemini-4-argon-medium", "gemini-3.8-flash"];

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Настройки AG Tools: порт прокси, есть ли ключ, включён ли автозапуск прокси.</summary>
    public static (int Port, bool HasKey, bool AutoStart, string? Exe) AgToolsInfo()
    {
        var file = Path.Combine(Home, ".antigravity_tools", "gui_config.json");
        int port = 8045; var hasKey = false; var autoStart = false;
        try
        {
            if (File.Exists(file))
            {
                var j = JsonNode.Parse(File.ReadAllText(file));
                port = j?["proxy"]?["port"]?.GetValue<int>() ?? 8045;
                hasKey = !string.IsNullOrWhiteSpace(j?["proxy"]?["api_key"]?.GetValue<string>());
                autoStart = j?["proxy"]?["auto_start"]?.GetValue<bool>() ?? false;
            }
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or IOException) { }
        string? exe = null;
        foreach (var p in Process.GetProcessesByName("antigravity-tools"))
        {
            try { exe ??= p.MainModule?.FileName; } catch (System.ComponentModel.Win32Exception) { }
            p.Dispose();
        }
        exe ??= new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Antigravity Tools", "antigravity-tools.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Antigravity Tools", "antigravity-tools.exe"),
        }.FirstOrDefault(File.Exists);
        return (port, hasKey, autoStart, exe);
    }

    /// <summary>
    /// Лёгкая настройка при запуске: найти прокси (в фоне), сверить порт AG Tools с профилем. Без запуска программ
    /// и без долгих проверок — Клава не должна просыпаться дольше из-за этого.
    /// </summary>
    public static async Task QuickAsync(KlavaConfig cfg)
    {
        try
        {
            await NetProxy.DetectAsync();
            var (port, _, _, exe) = AgToolsInfo();
            if (cfg.Brains.FirstOrDefault(b => b.Id == "antigravity") is { } ag)
            {
                var url = $"http://127.0.0.1:{port}/v1";
                var changed = false;
                if (!string.Equals(ag.Url, url, StringComparison.OrdinalIgnoreCase)) { Log.Write($"автонастройка: порт AG Tools {ag.Url} → {url}"); ag.Url = url; changed = true; }
                if (exe is not null && string.IsNullOrWhiteSpace(ag.Launch)) { ag.Launch = exe; changed = true; }
                if (changed) cfg.Save();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { Log.Error("автонастройка при запуске", ex); }
    }

    /// <summary>Полная автонастройка. Возвращает число мозгов, которые отвечают.</summary>
    public static async Task<int> RunAsync(KlavaConfig cfg, IProgress<string> log, CancellationToken ct = default)
    {
        var ok = 0;
        log.Report("① Прокси…");
        var proxy = await NetProxy.DetectAsync(log, force: true);

        // ② Antigravity Tools
        log.Report("② Antigravity Tools…");
        var (port, hasKey, autoStart, exe) = AgToolsInfo();
        var agUrl = $"http://127.0.0.1:{port}/v1";
        if (exe is null && !PortOpen(port))
            log.Report("   AG Tools не установлен (нет antigravity-tools.exe) — пропускаю.");
        else
        {
            if (!PortOpen(port) && exe is not null)
            {
                log.Report($"   запускаю {Path.GetFileName(exe)}…");
                try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose(); }
                catch (System.ComponentModel.Win32Exception ex) { log.Report($"   не запустился: {ex.Message}"); }
                for (var i = 0; i < 30 && !PortOpen(port); i++) await Task.Delay(1000, ct);
            }
            if (!PortOpen(port))
                log.Report(autoStart
                    ? "   порт не открылся — в AG Tools открой вкладку API Proxy и нажми «Запустить»."
                    : "   прокси AG Tools выключен: вкладка API Proxy → «Запустить» и включи «Автозапуск».");
            else if (!hasKey)
                log.Report("   в AG Tools нет API-ключа прокси — создай его на вкладке API Proxy.");
            else
            {
                var ag = cfg.Brains.FirstOrDefault(b => b.Id == "antigravity");
                if (ag is null)
                {
                    ag = BrainProfile.Defaults().First(b => b.Id == "antigravity");
                    cfg.Brains.Insert(Math.Min(1, cfg.Brains.Count), ag);
                    log.Report("   завела мозг «antigravity»");
                }
                ag.Url = agUrl;
                ag.Key = "antigravity-tools";
                ag.Kind = "openai";
                ag.ModelsFromServer = true;
                ag.Proxy = "direct";
                if (exe is not null) ag.Launch = exe;
                var models = await ModelsAsync(agUrl, ApiKeys.Resolve("antigravity-tools"), "direct", ct);
                if (models.Count == 0) log.Report("   AG Tools не отдал список моделей — проверь, что аккаунты залогинены.");
                else
                {
                    var exclude = string.IsNullOrWhiteSpace(ag.ModelExclude) ? null : new System.Text.RegularExpressions.Regex(ag.ModelExclude, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    var chat = models.Where(m => exclude is null || !exclude.IsMatch(m)).ToList();
                    if (!models.Contains(ag.Model, StringComparer.OrdinalIgnoreCase)) ag.Model = AgSmart.FirstOrDefault(m => models.Contains(m, StringComparer.OrdinalIgnoreCase)) ?? chat.First();
                    if (string.IsNullOrWhiteSpace(ag.ChatModel) || !models.Contains(ag.ChatModel, StringComparer.OrdinalIgnoreCase))
                        ag.ChatModel = AgFast.FirstOrDefault(m => models.Contains(m, StringComparer.OrdinalIgnoreCase));
                    log.Report($"   моделей на аккаунтах: {models.Count} (для разговора: {chat.Count}). Задачи — {ag.Model}, болтовня — {ag.ChatModel ?? ag.Model}.");
                    var (said, ms, err) = await PingChatAsync(agUrl, ApiKeys.Resolve("antigravity-tools"), ag.ChatModel ?? ag.Model, "direct", ct);
                    log.Report(err is null ? $"   ✓ ответил за {ms} мс: «{said}»" : $"   ✗ {err}");
                    if (err is null) ok++;
                }
            }
        }

        // ③ ключи облачных провайдеров
        log.Report("③ Облачные ключи…");
        foreach (var pv in Providers)
        {
            var envName = pv.Env.FirstOrDefault(n => ApiKeys.Resolve("env:" + n) is not null);
            var existing = cfg.Brains.FirstOrDefault(b => b.Id == pv.Id);
            var spec = existing?.Key is { } k && ApiKeys.Resolve(k) is not null ? k : envName is null ? null : "env:" + envName;
            if (spec is null) continue;
            var key = ApiKeys.Resolve(spec)!;
            var url = existing is { Url.Length: > 0 } ? existing.Url.TrimEnd('/') : pv.Url;
            var model = existing?.Model is { Length: > 0 } m ? m : pv.Model;
            // путь выбираем по факту: напрямую → через прокси; у кого нет /models — пробный ответ
            string? route = null;
            string? error = null;
            var direct = await ModelsAsync(url, key, "direct", ct);
            if (direct.Count > 0) route = "direct";
            else if (proxy is not null && (await ModelsAsync(url, key, "proxy", ct)).Count > 0) route = "proxy";
            else
            {
                foreach (var mode in proxy is null ? new[] { "direct" } : ["direct", "proxy"])
                {
                    var (_, _, err) = await PingChatAsync(url, key, model, mode, ct);
                    if (err is null) { route = mode; break; }
                    error = err;
                }
            }
            if (route is null)
            {
                // нерабочий ключ новый мозг не заводит — иначе в меню висел бы мёртвый пункт
                log.Report(existing is null
                    ? $"   {pv.Name}: ключ {spec} есть, но не работает ({error}) — мозг не завожу"
                    : $"   {existing.Id}: ✗ {error}");
                continue;
            }
            if (existing is null)
            {
                existing = new BrainProfile
                {
                    Id = pv.Id, Name = pv.Name, Kind = "openai", Url = pv.Url, Model = pv.Model, Key = spec, Vision = pv.Vision,
                    MaxTokens = 1500, Aliases = pv.Aliases, Fallback = cfg.Brains.Any(b => b.Id == "deepseek") && pv.Id != "deepseek" ? "deepseek" : null,
                    ModelInclude = pv.ModelInclude, ModelExclude = pv.ModelExclude, ModelsFromServer = pv.ModelInclude is not null,
                };
                cfg.Brains.Add(existing);
                log.Report($"   {pv.Name}: нашла рабочий ключ {spec} — завела мозг «{pv.Id}»");
            }
            existing.Key = spec;
            existing.Proxy = route;
            log.Report($"   {existing.Id}: ✓ {(route == "direct" ? "напрямую" : $"через прокси {proxy}")}{(direct.Count > 0 ? $" ({direct.Count} моделей)" : "")}");
            ok++;
        }

        // ④ локальные серверы
        log.Report("④ Локальные серверы…");
        foreach (var (id, name, url, aliases) in new[]
                 {
                     ("ollama", "Ollama (локально)", "http://127.0.0.1:11434/v1", new[] { "оллама", "ollama" }),
                     ("lmstudio", "LM Studio (локально)", "http://127.0.0.1:1234/v1", new[] { "лм студио", "lm studio" }),
                 })
        {
            if (!PortOpen(new Uri(url).Port)) continue;
            var models = await ModelsAsync(url, null, "direct", ct);
            if (models.Count == 0) continue;
            var b = cfg.Brains.FirstOrDefault(x => x.Id == id);
            if (b is null)
            {
                b = new BrainProfile { Id = id, Name = name, Kind = "openai", Url = url, Model = models[0], Vision = false, MaxTokens = 800, Aliases = aliases, ModelsFromServer = true, Proxy = "direct" };
                cfg.Brains.Add(b);
                log.Report($"   {name}: запущен, моделей {models.Count} — завела мозг «{id}»");
            }
            else if (!models.Contains(b.Model)) b.Model = models[0];
            ok++;
        }

        // ⑤ Claude Code
        log.Report("⑤ Claude Code…");
        if (ClaudeCode.FindExe() is null) log.Report("   не установлен — пропускаю.");
        else
        {
            var logged = await ClaudeCode.LoggedInAsync();
            log.Report(logged switch { true => "   ✓ установлен и залогинен", false => "   установлен, но не залогинен: в терминале «claude auth login»", _ => "   не ответил на проверку" });
            if (logged == true && cfg.Brains.All(b => b.Kind != "claude-code"))
                cfg.Brains.Add(BrainProfile.Defaults().First(b => b.Kind == "claude-code"));
        }

        // запасной у всех облачных — самый быстрый из рабочих (DeepSeek), чтобы никто не «думал и замолкал»
        if (cfg.Brains.Any(b => b.Id == "deepseek"))
            foreach (var b in cfg.Brains.Where(b => !b.IsLocal && b.Id != "deepseek" && string.IsNullOrEmpty(b.Fallback)))
                b.Fallback = "deepseek";
        cfg.Save();
        log.Report($"Готово: рабочих мозгов {ok}. Настройки сохранены в klava.json (ключи — только ссылками env:/antigravity-tools).");
        return ok;
    }

    private static bool PortOpen(int port) => NetProxy.PortOpen("127.0.0.1", port);

    /// <summary>Список моделей сервера (/models) через заданный путь; пусто — не ответил.</summary>
    public static async Task<List<string>> ModelsAsync(string url, string? key, string mode, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{url.TrimEnd('/')}/models");
            if (key is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            using var r = await NetProxy.ClientFor(url, mode).SendAsync(req, cts.Token);
            if (!r.IsSuccessStatusCode) return [];
            var data = JsonNode.Parse(await r.Content.ReadAsStringAsync(cts.Token))?["data"] as JsonArray;
            return (data ?? []).Select(m => m?["id"]?.GetValue<string>()).OfType<string>()
                .Select(id => id.StartsWith("models/", StringComparison.Ordinal) ? id[7..] : id).Distinct().ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException) { return []; }
    }

    /// <summary>Пробный ответ: «скажи ок». Возвращает текст, время до ответа и ошибку (null — ответил).</summary>
    public static async Task<(string Said, long Ms, string? Error)> PingChatAsync(string url, string? key, string model, string? mode, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var body = new JsonObject
            {
                ["model"] = model, ["stream"] = false, ["max_tokens"] = 300,
                ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Ответь одним словом по-русски: ок" } },
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{url.TrimEnd('/')}/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (key is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(40));
            using var r = await NetProxy.ClientFor(url, mode).SendAsync(req, cts.Token);
            var text = await r.Content.ReadAsStringAsync(cts.Token);
            if (!r.IsSuccessStatusCode)
            {
                var msg = text;
                try { msg = JsonNode.Parse(text)?["error"]?["message"]?.GetValue<string>() ?? text; } catch (System.Text.Json.JsonException) { }
                return ("", sw.ElapsedMilliseconds, $"{(int)r.StatusCode}: {(msg.Length > 150 ? msg[..150] + "…" : msg)}");
            }
            var said = JsonNode.Parse(text)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim() ?? "";
            return (said.Length > 60 ? said[..60] + "…" : said, sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return ("", sw.ElapsedMilliseconds, ex is TaskCanceledException ? "не ответил за 40 с" : ex.Message);
        }
    }
}
