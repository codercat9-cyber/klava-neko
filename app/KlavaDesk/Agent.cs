using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Фоновый агент — руки Клавы для настоящей работы на компьютере (код, сборка, установка, настройка, скрипты).
///
/// Раньше вся работа шла внутри хода разговора: пока крутилась команда (до 5–30 минут), ход не кончался — Клава
/// не слышала друга (его слова подхватывались только между кругами инструментов) и «тупила», а чат-модель, которой
/// удобно болтать голосом, плохо держала агентную работу и бросала её через пару команд. Теперь разговор отдаёт
/// задачу агенту и сразу свободен: агент работает сам — сильная модель (Claude Sonnet через AG Tools), свой pwsh
/// (не держит общий терминал), нормальные инструменты разработчика (edit_file, grep, find_files, job, linux…).
/// Друг может по ходу добавить указание (tell) или остановить; итог приходит Клаве событием, и она рассказывает.
/// Мышь и клавиатуру агент не трогает. Опасное — через то же окно подтверждения, что и у Клавы.
/// </summary>
internal static class AgentRunner
{
    private static CancellationTokenSource? _cts;
    private static string _status = "агент сейчас ничего не делает";
    private static string _goal = "";
    private static readonly ConcurrentQueue<string> Notes = new();
    private static readonly Stopwatch Clock = new();

    /// <summary>Задача закончилась: (цель, итог).</summary>
    public static event Action<string, string>? Finished;
    /// <summary>Строка для кибер-терминала: что агент думает и делает.</summary>
    public static event Action<string>? Note;

    public static bool Busy => _cts is not null;

    /// <summary>Своя оболочка агента: его долгая команда не держит общий pwsh, в котором работает сама Клава.</summary>
    public static Shell Shell { get; } = new();

    /// <summary>Что агенту можно: файлы, поиск, терминалы, фоновые процессы, веб. Без мыши и без разговорных игрушек.</summary>
    private static readonly HashSet<string> AgentTools =
    [
        "list_dir", "read_file", "write_file", "edit_file", "grep", "find_files", "shell", "bash", "linux", "run_powershell",
        "job", "web_search", "web_fetch", "browser_open", "open_app", "recycle", "create_shortcut",
    ];

    public static string Start(string goal, KlavaConfig cfg, ToolBox box, Func<ToolRequest, Task<bool>> approve)
    {
        if (string.IsNullOrWhiteSpace(goal)) return "Агенту нужна задача: что сделать, где и как проверить.";
        if (_cts is not null) return $"Агент уже занят задачей «{Short(_goal, 80)}» ({_status}). Добавить указание — agent tell, бросить — agent stop.";
        _cts = new CancellationTokenSource(TimeSpan.FromMinutes(Math.Clamp(cfg.AgentMaxMinutes, 5, 240)));
        _goal = goal.Trim();
        while (Notes.TryDequeue(out _)) { }
        _status = "начинаю";
        Clock.Restart();
        // своя коробка инструментов: свой pwsh, без лапок; разрешения и автопилот — как у Клавы
        var agentBox = new ToolBox
        {
            Hands = box.Hands,
            Capture = box.Capture,
            Shell = Shell,
            ShellApproved = box.ShellApproved || box.Autopilot,
            Autopilot = box.Autopilot,
            ShowTerminal = box.ShowTerminal,
            WorkDir = box.Shell.Cwd is { Length: > 0 } cwd ? cwd : box.WorkDir,
            HandsAllowed = false,
        };
        var token = _cts.Token;
        var goalNow = _goal;
        _ = Task.Run(async () =>
        {
            string report;
            try { report = await RunAsync(goalNow, cfg, agentBox, approve, token); }
            catch (OperationCanceledException) { report = Clock.Elapsed.TotalMinutes >= Math.Clamp(cfg.AgentMaxMinutes, 5, 240) - 0.1 ? "время на задачу вышло — остановила агента" : "агента остановили"; }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException or TimeoutException)
            {
                Log.Error("агент", ex);
                report = $"агент сломался: {ex.Message}";
            }
            _cts?.Dispose();
            _cts = null;
            _status = $"агент свободен (прошлая задача: {Short(report, 120)})";
            Log.Write($"агент: итог — {Short(report, 300)}");
            Say($"🏁 итог: {report}");
            Finished?.Invoke(goalNow, report);
        });
        return $"Агент взял задачу и работает в фоне (его команды видно в кибер-терминале). Ты свободна — болтай с другом; итог придёт сам. Задача: {Short(_goal, 200)}";
    }

    /// <summary>
    /// KlavaDesk.exe --agent-test "задача" [папка] — агент без Клавы: задача в консоли, строки агента и итог туда же.
    /// Безопасное разрешается само, опасное — запрещается (окна подтверждения тут некому показать).
    /// </summary>
    public static int Test(string goal, string? workDir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\agent_test.log"));
        var dir = string.IsNullOrWhiteSpace(workDir) ? Path.Combine(Path.GetTempPath(), "klava_agent_test") : workDir;
        Directory.CreateDirectory(dir);
        var shell = new Shell();
        using var ui = new Control();
        var box = new ToolBox
        {
            Hands = new Hands(ui),
            Capture = () => Task.FromException<ScreenShot>(new InvalidOperationException("в проверке агента глаз нет")),
            Shell = shell,
            Autopilot = true,
            ShellApproved = true,
            WorkDir = dir,
            HandsAllowed = false,
        };
        Shell.CommandStarted += (cmd, cwd) => Console.WriteLine($"  PS {cwd}> {Short(cmd.Replace('\n', ' '), 160)}");
        Shell.CommandCompleted += (code, ms, _) => Console.WriteLine($"  [код {code}, {ms} мс]");
        Note += line => Console.WriteLine(line);
        var done = new TaskCompletionSource<string>();
        Finished += (_, report) => done.TrySetResult(report);
        Task<bool> Approve(ToolRequest r)
        {
            Console.WriteLine($"  [разрешение: {r.Title}{(r.Dangerous ? " — ОПАСНО, запрещаю" : "")}]");
            return Task.FromResult(!r.Dangerous);
        }
        Console.WriteLine(Start(goal, cfg, box, Approve));
        var report = done.Task.GetAwaiter().GetResult();
        Console.WriteLine($"\n=== ИТОГ ===\n{report}");
        Shell.Dispose();
        shell.Dispose();
        return report.StartsWith("агент сломался", StringComparison.Ordinal) ? 1 : 0;
    }

    public static string Status() => _cts is null ? _status : $"агент работает {Clock.Elapsed:mm\\:ss}: {_status}. Задача: {Short(_goal, 150)}";

    public static string Tell(string note)
    {
        if (_cts is null) return "Агент сейчас ничего не делает — дай задачу через agent start.";
        if (string.IsNullOrWhiteSpace(note)) return "Пустое указание.";
        Notes.Enqueue(note.Trim());
        return "Передала агенту — учтёт на следующем шаге.";
    }

    public static string Stop()
    {
        if (_cts is null) return "Агент и так ничего не делает.";
        _cts.Cancel();
        Shell.CancelAsync();
        return "Останавливаю агента.";
    }

    private static void Say(string line)
    {
        try { Note?.Invoke(line); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string SystemPrompt(ToolBox box) =>
        $"""
        Ты — руки Клавы: фоновый агент на Windows-компьютере её друга. Делаешь задачу сам, инструментами, до конца. Друга не спрашиваешь — он занят разговором с Клавой; если чего-то не хватает, выбираешь разумный вариант и говоришь об этом в итоге.
        Среда: Windows 11. shell — живой PowerShell 7 (cd и переменные сохраняются), bash — Git Bash, linux — настоящий Linux в WSL (Ubuntu, есть kali-linux), Python 3.11 (python), git, node, dotnet. Текущая папка: {box.WorkDir}. Сейчас {DateTime.Now:dd.MM.yyyy HH:mm}.
        Как работать:
        - Сначала разберись: list_dir / find_files / grep / read_file (с offset/limit — строки с номерами). Потом меняй.
        - Код правь edit_file (точная замена куска), новые файлы — write_file. Не переписывай большие файлы целиком.
        - После изменений проверяй сама: собери, запусти, прогони тест (dotnet build, python -m py_compile, node --check, pytest…). Ошибка — разберись и почини.
        - Долгое (сервер, обучение модели, большая загрузка) — job start, потом job output / status; не держи это в shell.
        - Ничего не удаляй безвозвратно (recycle — в Корзину), системные папки не трогай, ключи и пароли не выводи.
        - Перед группой действий пиши одну короткую строку, что делаешь, — её видно в кибер-терминале.
        - Сообщение «(Друг добавил по ходу: …)» — учти сразу, это главнее старого плана.
        - Закончила (или упёрлась) — ответь БЕЗ вызова инструментов: итог 3–8 строк по-русски — что сделано, где файлы, как проверила, что не вышло и почему.
        """;

    /// <summary>Цикл агента: модель → инструменты → модель, пока не ответит без инструментов (итог) или не кончится бюджет.</summary>
    private static async Task<string> RunAsync(string goal, KlavaConfig cfg, ToolBox box, Func<ToolRequest, Task<bool>> approve, CancellationToken ct)
    {
        var journalFile = cfg.Abs($@"data\agent\{DateTime.Now:yyyyMMdd_HHmmss}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(journalFile)!);
        await File.WriteAllTextAsync(journalFile, $"# Задача агента\n\n{goal}\n\n", new UTF8Encoding(false), CancellationToken.None);
        void Journal(string s)
        {
            try { File.AppendAllText(journalFile, s + "\n", new UTF8Encoding(false)); }
            catch (IOException) { }
        }

        var tools = new JsonArray(Tools.Schema(false)
            .Where(t => t?["function"]?["name"]?.GetValue<string>() is { } n && AgentTools.Contains(n))
            .Select(t => t!.DeepClone()).ToArray());
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = SystemPrompt(box) },
            new JsonObject { ["role"] = "user", ["content"] = goal },
        };
        box.ShowTerminal?.Invoke();
        Say($"🐱 агент взял задачу: {Short(goal, 160)}");
        var route = new Route(cfg);
        var maxRounds = Math.Clamp(cfg.AgentMaxRounds, 10, 400);
        for (var round = 1; ; round++)
        {
            ct.ThrowIfCancellationRequested();
            while (Notes.TryDequeue(out var n))
            {
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = $"(Друг добавил по ходу: {n})" });
                Say($"📝 друг добавил: {n}");
                Journal($"\n> Друг добавил: {n}\n");
            }
            var last = round > maxRounds;
            if (last) messages.Add(new JsonObject { ["role"] = "user", ["content"] = "(Бюджет шагов кончился. Инструменты больше не вызывай — дай итог: что сделано, что нет и что дальше.)" });
            Shrink(messages);
            _status = $"шаг {round}, думаю";
            var msg = await route.AskAsync(messages, last ? null : tools, ct);
            // content — строка, а у части провайдеров массив кусков [{type:text,text:…}]
            var text = (msg["content"] switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonArray parts => string.Concat(parts.Select(x => x?["text"]?.GetValue<string>() ?? "")),
                _ => "",
            }).Trim();
            var calls = msg["tool_calls"] as JsonArray;
            if (calls is null || calls.Count == 0)
            {
                Journal($"\n## Итог\n\n{text}");
                return text.Length > 0 ? text : "агент закончил молча — итога не дал";
            }
            if (text.Length > 0) { Say($"🐱 {text}"); Journal($"\n{text}"); }
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = text, ["tool_calls"] = calls.DeepClone() });
            foreach (var c in calls)
            {
                ct.ThrowIfCancellationRequested();
                var id = c?["id"]?.GetValue<string>() ?? $"call_{round}";
                var name = c?["function"]?["name"]?.GetValue<string>() ?? "";
                var args = c?["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                _status = $"шаг {round}: {name} {Short(args.Replace('\n', ' '), 80)}";
                Journal($"\n- `{name}` {Short(args.Replace('\n', ' '), 300)}");
                if (name is not ("shell" or "run_powershell")) Say($"🔧 {name} {Short(args.Replace('\n', ' '), 140)}");
                string result;
                if (!AgentTools.Contains(name)) result = $"Инструмента «{name}» у агента нет. Есть: {string.Join(", ", AgentTools)}.";
                else
                {
                    try { result = (await Tools.RunAsync(new ToolCall(id, name, args), approve, cfg, box, ct)).Text; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or HttpRequestException)
                    {
                        result = $"Ошибка инструмента: {ex.Message}";
                    }
                }
                Journal($"  → {Short(result.Replace('\n', ' '), 400)}");
                if (result.Length > 14000) result = result[..9000] + $"\n…(обрезано, всего {result.Length} символов)…\n" + result[^4000..];
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result });
            }
        }
    }

    /// <summary>Разговор агента не должен раздуваться бесконечно: старые выводы инструментов ужимаются до сути.</summary>
    private static void Shrink(JsonArray messages)
    {
        var total = messages.Sum(m => m?["content"]?.ToString().Length ?? 0);
        if (total < 180_000) return;
        foreach (var m in messages.Take(Math.Max(0, messages.Count - 12)))
        {
            if (m?["role"]?.GetValue<string>() != "tool") continue;
            var s = m["content"]?.GetValue<string>() ?? "";
            if (s.Length > 700) m["content"] = s[..500] + "\n…(старый вывод ужат — нужен снова, запроси ещё раз)";
        }
    }

    /// <summary>
    /// Куда слать запросы агента: профиль AgentBrain (по умолчанию AG Tools) с моделью AgentModel (Claude Sonnet);
    /// не ответил — AgentFallbackModel в том же профиле, потом запасной мозг профиля. Раз переключившись, дальше идёт так.
    /// </summary>
    private sealed class Route(KlavaConfig cfg)
    {
        private int _stage;

        public async Task<JsonObject> AskAsync(JsonArray messages, JsonArray? tools, CancellationToken ct)
        {
            Exception? lastError = null;
            for (; _stage < 3; _stage++)
            {
                var (profile, model) = Pick(_stage);
                if (profile is null) continue;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try { return await CallAsync(profile, model, messages, tools, ct); }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        lastError = ex;
                        Log.Write($"агент: {profile.Id}/{model} не ответил ({ex.Message}) — {(attempt == 0 ? "ещё раз" : "дальше запасной")}");
                        await Task.Delay(1500, ct);
                    }
                }
            }
            throw new HttpRequestException($"ни одна модель агента не ответила: {lastError?.Message}");
        }

        private (BrainProfile? Profile, string Model) Pick(int stage)
        {
            var main = cfg.Brains.FirstOrDefault(b => b.Id == cfg.AgentBrain && !b.IsLocal && b.Kind == "openai");
            return stage switch
            {
                0 when main is not null => (main, cfg.AgentModel.Length > 0 ? cfg.AgentModel : main.Model),
                1 when main is not null && cfg.AgentFallbackModel.Length > 0 => (main, cfg.AgentFallbackModel),
                2 => cfg.Brains.FirstOrDefault(b => b.Id == (main?.Fallback ?? "deepseek") && b.Kind == "openai") is { } fb ? (fb, fb.Model) : (null, ""),
                _ => (null, ""),
            };
        }

        private async Task<JsonObject> CallAsync(BrainProfile p, string model, JsonArray messages, JsonArray? tools, CancellationToken ct)
        {
            var url = cfg.ApiUrl(p);
            var key = ApiKeys.Resolve(p.Key);
            var http = NetProxy.ClientFor(url, p.Proxy);
            var body = new JsonObject
            {
                ["model"] = model,
                ["max_tokens"] = 8000,
                ["messages"] = messages.DeepClone(),
            };
            if (tools is not null) { body["tools"] = tools.DeepClone(); body["tool_choice"] = "auto"; }
            using var call = CancellationTokenSource.CreateLinkedTokenSource(ct);
            call.CancelAfter(TimeSpan.FromMinutes(4));   // ответ модели, а не команда: столько не думают
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{url}/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (key is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var resp = await http.SendAsync(req, call.Token);
            var text = await resp.Content.ReadAsStringAsync(call.Token);
            if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"{(int)resp.StatusCode} {Short(text.Replace('\n', ' '), 200)}", null, resp.StatusCode);
            var root = JsonNode.Parse(text);
            if (root?["choices"] is not JsonArray { Count: > 0 } choices || choices[0]?["message"] is not JsonObject message)
                throw new HttpRequestException($"пустой ответ модели: {Short(text, 200)}");
            return (JsonObject)message.DeepClone();
        }
    }
}
