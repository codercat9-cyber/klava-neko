using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Мультиагент Клавы на Claude Code (друг 30.09: «её агенты далеко не двигаются и субагентов запустить не могут,
/// почти каждую команду терминал считает опасной, а мне автопилот нужен; нормальный исследовательский мультитерминал
/// с инструментами разработки, и чтобы Клава всем управляла и видела, что делают субагенты»).
///
/// Каждый агент — живой процесс claude.exe в режиме печати с потоковым JSON в обе стороны: у него весь набор
/// Claude Code (PowerShell/Bash, правка файлов, поиск, веб, субагенты Task/Agent, план TodoWrite), скиллы и агенты
/// друга из ~/.claude, его MCP-серверы (кроме голоса и мыши — это Клавины) и дев-плагины из установленных. Разрешения
/// Claude Code спрашивает у хоста (--permission-prompt-tool stdio) — то есть у Клавы: на автопилоте можно всё, кроме
/// катастроф и безвозвратного удаления (только в Корзину) и потери незакоммиченного (спросит окном); без автопилота
/// опасное — окном, как её собственный терминал. Замер 30.09: без хоста Claude Code в -p молча отказывал каждой
/// команде («requires approval»), агент кружил на месте.
///
/// Модель: подписка друга, если CLI залогинен, иначе Anthropic-вход AG Tools (тот же прокси, что у прежнего агента:
/// ANTHROPIC_BASE_URL, ключ из его настроек — только в окружение процесса, никуда не пишется).
/// Модель меняется на ходу (друг 01.10: 30.09 Сонет упёрся в лимит шлюза, на «переключай» Клава полезла править
/// собственный конфиг, сказала «переключила на джемини», а второй агент ушёл на тот же Сонет и упал с той же 503):
/// agent action:'model' пересаживает агента на любую модель шлюза («джемини 3.8 флеш хай», «опус 4.6 синкинг») —
/// новый процесс в той же вкладке получает задачу, план и сделанное и продолжает; на ошибке API (лимит, 503, нет
/// модели) агент сам идёт по цепочке запасных (agentModel → agentFallbackModel → agentModels).
/// Каждая строка сессии — во вкладку кибер-терминала; итог хода — Клаве событием; Tell дописывает указание в ту же
/// сессию (она помнит всё сделанное), Stop гасит процесс деревом.
/// </summary>
internal static partial class AgentHub
{
    private static readonly object Gate = new();
    private static readonly List<AgentSession> List = [];
    private static int _nextId = 1;
    private static (DateTime At, bool? Ok) _login = (DateTime.MinValue, null);
    private static string? _toolchain;
    private static KlavaConfig? _cfg;
    private static (DateTime At, List<string> List) _models = (DateTime.MinValue, []);

    /// <summary>Новая строка во вкладку агента (из фонового потока).</summary>
    public static event Action<AgentSession, AgentLine>? Line;
    /// <summary>Сессия появилась, сменила состояние или ушла (вкладки, шапка).</summary>
    public static event Action<AgentSession>? Changed;
    /// <summary>Агент закончил ход: (сессия, итог) — Клава расскажет другу.</summary>
    public static event Action<AgentSession, string>? TurnDone;

    public static IReadOnlyList<AgentSession> Sessions { get { lock (Gate) return [.. List]; } }

    /// <summary>Хоть один агент сейчас работает.</summary>
    public static bool Busy => Sessions.Any(s => s.Working);

    /// <summary>Можно ли агентов на Claude Code (есть claude.exe и не выбран прежний агент).</summary>
    public static bool Available(KlavaConfig cfg) => cfg.AgentEngine != "own" && ClaudeCode.FindExe() is not null;

    // ---------- запуск ----------

    public static async Task<string> StartAsync(string goal, string title, string cwd, KlavaConfig cfg, ToolBox box,
        Func<ToolRequest, Task<bool>> approve)
    {
        if (string.IsNullOrWhiteSpace(goal)) return "Агенту нужна задача: что сделать, где и как проверить.";
        var exe = ClaudeCode.FindExe();
        if (exe is null) return "Claude Code не установлен (npm i -g @anthropic-ai/claude-code) — агент не запустится.";
        var alive = Sessions.Count(s => s.Working);
        if (alive >= Math.Clamp(cfg.AgentMaxParallel, 1, 8))
            return $"Уже работают {alive} агента — это предел (agentMaxParallel). Дай указание работающему (tell) или останови лишнего (stop).";
        _cfg = cfg;
        var route = await RouteAsync(cfg).ConfigureAwait(false);
        if (route is null)
            return "Агенту не на чем думать: Claude Code не залогинен (claude → /login), а у профиля агента нет ключа AG Tools.";
        var dir = ResolveDir(cwd, box, cfg);
        Directory.CreateDirectory(dir);
        int id;
        lock (Gate) id = _nextId++;
        var name = string.IsNullOrWhiteSpace(title) ? Short(goal.Trim().Split('\n')[0], 40) : Short(title.Trim(), 40);
        var s = new AgentSession(id, name, goal.Trim(), dir, route.Label, route.Model, box, approve);
        if (Launch(s, exe, route, cfg) is { } failed) return failed;
        lock (Gate)
        {
            List.Add(s);
            // старые законченные вкладки не копятся бесконечно
            foreach (var old in List.Where(x => !x.Alive).OrderBy(x => x.Started).Take(Math.Max(0, List.Count - 10)).ToList())
                List.Remove(old);
        }
        Log.Write($"агент #{id}: «{name}» в {dir} ({route.Label}, {route.Model}) — {Short(goal.Replace('\n', ' '), 200)}");
        s.Say(AgentLine.Sys($"🐱 агент #{id} «{name}» — {route.Label}, модель {route.Model}, папка {dir}"));
        s.Say(AgentLine.You(goal.Trim()));
        s.Send(goal.Trim());
        box.ShowTerminal?.Invoke();
        Raise(() => Changed?.Invoke(s));
        return $"Агент #{id} «{name}» взял задачу и работает в фоне ({route.Label}, модель {route.Model}; его шаги и субагенты видно во вкладке кибер-терминала). " +
               "Ты свободна — болтай с другом, итог придёт сам. Указание по ходу — agent tell, как дела — agent status, другая модель — agent model.";
    }

    /// <summary>Поднять процесс Claude Code для сессии — новой или пересаживаемой на другую модель. null — поднялся.</summary>
    private static string? Launch(AgentSession s, string exe, Route route, KlavaConfig cfg)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = s.Cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[]
                 {
                     "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                     "--permission-mode", "acceptEdits", "--permission-prompt-tool", "stdio", "--forward-subagent-text",
                     "-n", $"Клава #{s.Id}: {s.Title}", "--append-system-prompt", Rules(s),
                     "--disallowedTools", "mcp__voice,mcp__ag-computer-use",
                 })
            psi.ArgumentList.Add(a);
        if (route.Model.Length > 0) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(route.Model); }
        foreach (var plugin in Plugins(cfg)) { psi.ArgumentList.Add("--plugin-dir"); psi.ArgumentList.Add(plugin); }
        foreach (var k in psi.Environment.Keys.Where(k => k.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(k);
        foreach (var (k, v) in route.Env) psi.Environment[k] = v;
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
        Process proc;
        try { proc = Process.Start(psi) ?? throw new InvalidOperationException("claude.exe не запустился"); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return $"Агент не запустился: {ex.Message}";
        }
        try { Native.TieToLifetime(proc); }
        catch (InvalidOperationException) { }
        s.Attach(proc);
        return null;
    }

    // ---------- модели ----------

    [GeneratedRegex(@"image|embed|tts|audio|whisper|internal|tiered|rerank", RegexOptions.IgnoreCase)]
    private static partial Regex NotForAgentsRx();

    [GeneratedRegex(@"API Error|accounts? limited|rate.?limit|overloaded|quota|exhausted|限流|model.{0,20}not (found|supported)", RegexOptions.IgnoreCase)]
    private static partial Regex ApiErrorRx();

    /// <summary>Ошибка API, после которой стоит пересесть на другую модель (лимит аккаунтов, 503, нет такой модели).</summary>
    internal static bool IsApiError(string result) => ApiErrorRx().IsMatch(result);

    /// <summary>Модели шлюза агентов (/models у мозга agentBrain — AG Tools); кэш 5 минут. Пусто — шлюз не ответил.</summary>
    public static async Task<List<string>> ModelsAsync(KlavaConfig cfg, bool refresh = false)
    {
        if (!refresh && _models.List.Count > 0 && DateTime.Now - _models.At < TimeSpan.FromMinutes(5)) return _models.List;
        var p = cfg.Brains.FirstOrDefault(b => b.Id == cfg.AgentBrain && !b.IsLocal && b.Kind == "openai");
        if (p is null) return [];
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{cfg.ApiUrl(p)}/models");
            if (ApiKeys.Resolve(p.Key) is { } key) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var r = await NetProxy.ClientFor(cfg.ApiUrl(p), p.Proxy, TimeSpan.FromSeconds(10)).SendAsync(req).ConfigureAwait(false);
            if (!r.IsSuccessStatusCode) return [];
            var data = JsonNode.Parse(await r.Content.ReadAsStringAsync().ConfigureAwait(false))?["data"] as JsonArray;
            var list = (data ?? []).Select(m => m?["id"]?.GetValue<string>()).OfType<string>()
                .Select(id => id.StartsWith("models/", StringComparison.Ordinal) ? id[7..] : id)
                .Where(id => !NotForAgentsRx().IsMatch(id)).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (list.Count > 0) _models = (DateTime.Now, list);
            return list;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return []; }
    }

    private static readonly (string Rx, string To)[] SpokenModel =
    [
        (@"жмин\w*|джемин\w*|гемин\w*|джимин\w*", "gemini"), (@"опус\w*", "opus"), (@"сон+ет\w*", "sonnet"), (@"хайку", "haiku"),
        (@"клод\w*|клауд\w*", "claude"), (@"гпт|джипити", "gpt"), (@"дипсик\w*", "deepseek"), (@"квен\w*", "qwen"),
        (@"\bпро\b", "pro"), (@"фл[еэ]ш\w*", "flash"), (@"\bхай\b|высок\w*|верхн\w*", "high"), (@"\bлоу\b|низк\w*|нижн\w*", "low"),
        (@"медиум|средн\w*", "medium"), (@"[сфт]инкинг\w*|думающ\w*|размышля\w*", "thinking"), (@"лайт", "lite"), (@"\bмини\b", "mini"),
        (@"\bноль\b", "0"), (@"\bодин\b", "1"), (@"\bдва\b", "2"), (@"\bтри\b", "3"), (@"\bчетыре\b", "4"), (@"\bпять\b", "5"),
        (@"\bшесть\b", "6"), (@"\bсемь\b", "7"), (@"\bвосемь\b", "8"), (@"\bдевять\b", "9"),
    ];

    private static readonly HashSet<string> Families = new(StringComparer.Ordinal) { "gemini", "claude", "opus", "sonnet", "haiku", "gpt", "deepseek", "qwen" };

    private static List<string> ModelTokens(string s) =>
        Regex.Matches(s.ToLowerInvariant(), @"[a-zа-я]+|\d").Select(m => m.Value).ToList();

    /// <summary>
    /// Модель по словам: «джемини три и восемь флеш хай», «3,1 про», «опус 4.6 синкинг», «сонет» или точный id → id из
    /// списка шлюза. Семейство (gemini, opus…), если названо, обязано совпасть; из равных — «high» и короче. null — нет такой.
    /// </summary>
    internal static string? PickModel(string words, IReadOnlyList<string> models)
    {
        var w = words.Trim();
        if (w.Length == 0) return null;
        if (models.FirstOrDefault(m => m.Equals(w, StringComparison.OrdinalIgnoreCase)) is { } exact) return exact;
        var text = w.ToLowerInvariant().Replace('ё', 'е');
        foreach (var (rx, to) in SpokenModel) text = Regex.Replace(text, rx, $" {to} ");
        var known = models.SelectMany(ModelTokens).ToHashSet(StringComparer.Ordinal);
        var want = ModelTokens(text).Where(known.Contains).Distinct().ToList();
        if (want.Count == 0) return null;
        (string? Id, double Score) best = (null, 0);
        foreach (var m in models)
        {
            var have = ModelTokens(m);
            if (want.Any(t => Families.Contains(t) && !have.Contains(t))) continue;
            var hit = want.Count(have.Contains);
            var score = hit * 2 - (want.Count - hit) * 3 - (have.Distinct().Count() - hit) * 0.5
                        + (have.Contains("high") ? 0.2 : 0) - m.Length * 0.001;
            if (hit > 0 && score > best.Score) best = (m, score);
        }
        return best.Id;
    }

    /// <summary>Цепочка моделей агента по порядку: основная, запасная, остальные запасные — именами шлюза.</summary>
    internal static List<string> Chain(KlavaConfig cfg, IReadOnlyList<string> available) =>
        new[] { cfg.AgentModel, cfg.AgentFallbackModel }.Concat(cfg.AgentModels).Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => available.Count == 0 ? m.Trim() : PickModel(m, available)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// Пересадить агента на другую модель: прежний процесс гасится, новый поднимается в той же вкладке и папке.
    /// carryOn — сразу продолжить работу: новому процессу передаётся задача, указания, план и последние шаги прежнего
    /// (его память между моделями не переносится — перенос чужих «мыслей» другой модели ломает протокол).
    /// </summary>
    internal static async Task<string> SwitchAsync(AgentSession s, string model, KlavaConfig cfg, string why, bool carryOn)
    {
        var exe = ClaudeCode.FindExe();
        if (exe is null) return "Claude Code не установлен — агента не пересадить.";
        var route = await RouteAsync(cfg, model).ConfigureAwait(false);
        if (route is null) return "У профиля агента нет ключа шлюза — пересадить не на что.";
        var old = s.Model;
        var handoff = s.Handoff(why);
        s.Rehost(route.Label, route.Model);
        if (Launch(s, exe, route, cfg) is { } failed)
        {
            s.Kill($"не пересел на {route.Model}");
            return failed;
        }
        s.Say(AgentLine.Sys($"🔁 модель: {old} → {route.Model} ({why})"));
        Log.Write($"агент #{s.Id}: модель {old} → {route.Model} ({why}){(carryOn ? ", продолжает" : "")}");
        if (carryOn) s.Send(handoff);
        else s.Idle(handoff);
        Raise(() => Changed?.Invoke(s));
        return $"Агент #{s.Id} «{s.Title}»: {old} → {route.Model}" + (carryOn ? ", продолжает с того же места." : " (ждёт указаний; что было сделано — он узнает со следующим указанием).");
    }

    /// <summary>
    /// Ход кончился ошибкой API (лимит аккаунтов шлюза, 503, нет такой модели): агент сам пересаживается на следующую
    /// модель цепочки и продолжает. true — пересадка пошла (итог хода Клаве пока не нужен). Запасные кончились — Клаве
    /// честный итог: что перепробовано и что делать.
    /// </summary>
    internal static bool TryFailover(AgentSession s, string error)
    {
        if (_cfg is not { } cfg || !ApiErrorRx().IsMatch(error)) return false;
        s.MarkTried(s.Model);
        _ = Task.Run(async () =>
        {
            try
            {
                var available = await ModelsAsync(cfg).ConfigureAwait(false);
                var next = Chain(cfg, available).FirstOrDefault(m => !s.WasTried(m));
                if (next is null)
                {
                    OnTurnDone(s, $"модель {s.Model} не отвечает ({Short(error.Replace('\n', ' '), 160)}), запасные тоже перепробованы: {s.TriedText}. " +
                                  "Скажи другу как есть: ждать сброса лимита или назвать другую модель (agent action:'models' — что есть у шлюза).");
                    return;
                }
                s.Say(AgentLine.Sys($"⚠ {s.Model}: {Short(error.Replace('\n', ' '), 140)} — пересаживаюсь на {next}"));
                await SwitchAsync(s, next, cfg, $"модель {s.Model} упёрлась в лимит или не ответила", carryOn: true).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                Log.Error($"агент #{s.Id}: пересадка на запасную модель", ex);
                OnTurnDone(s, $"модель {s.Model} не отвечает, пересесть на запасную не вышло: {ex.Message}");
            }
        });
        return true;
    }

    /// <summary>
    /// Инструмент agent model. words — модель словами или id; id агента пусто — модель по умолчанию для новых агентов и
    /// все живые агенты разом. Работавший (или упавший на ошибке) продолжает сам, ждавший указаний — ждёт дальше.
    /// </summary>
    public static async Task<string> ModelAsync(string id, string words, KlavaConfig cfg)
    {
        _cfg = cfg;
        var available = await ModelsAsync(cfg, refresh: true).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(words)) return ModelsText(cfg, available);
        var model = PickModel(words, available);
        // шлюз не отдал список — точный id принимаем на веру
        if (model is null && available.Count == 0 && Regex.IsMatch(words.Trim(), @"^[a-z0-9][a-z0-9.\-_]+$", RegexOptions.IgnoreCase)) model = words.Trim();
        if (model is null)
            return available.Count == 0
                ? "Шлюз агентов (AG Tools) не ответил — список моделей не получить. Назови точный id модели или подними шлюз."
                : $"Модели «{words}» у шлюза нет. Есть: {string.Join(", ", available)}.";
        var sb = new StringBuilder();
        List<AgentSession> targets;
        if (id.Trim().Length > 0)
        {
            if (Find(id, preferWorking: true) is not { Alive: true } one) return $"Живого агента «{id}» нет. {Overview()}";
            targets = [one];
        }
        else
        {
            cfg.AgentModel = model;
            cfg.Save();
            sb.Append($"Новые агенты теперь стартуют на {model}.");
            targets = Sessions.Where(s => s.Alive).ToList();
        }
        foreach (var s in targets)
        {
            if (s.Model.Equals(model, StringComparison.OrdinalIgnoreCase)) { sb.Append($" Агент #{s.Id} уже на {model}."); continue; }
            s.ClearTried();
            sb.Append(' ').Append(await SwitchAsync(s, model, cfg, "друг попросил другую модель", carryOn: s.Working || s.Failed).ConfigureAwait(false));
        }
        if (targets.Count == 0 && id.Trim().Length == 0) sb.Append(" Живых агентов сейчас нет — следующий возьмёт её.");
        return sb.ToString().Trim();
    }

    /// <summary>Какие модели есть, какая сейчас и в каком порядке запасные.</summary>
    internal static string ModelsText(KlavaConfig cfg, IReadOnlyList<string> available)
    {
        var sb = new StringBuilder();
        sb.AppendLine(available.Count > 0 ? $"Модели шлюза агентов ({cfg.AgentBrain}): {string.Join(", ", available)}." : "Шлюз агентов не ответил — список моделей неизвестен.");
        sb.AppendLine($"Новые агенты стартуют на: {cfg.AgentModel}. Запасные по порядку (агент пересаживается сам, если модель упёрлась в лимит): {string.Join(" → ", Chain(cfg, available))}.");
        foreach (var s in Sessions.Where(x => x.Alive)) sb.AppendLine($"Агент #{s.Id} «{s.Title}» — на {s.Model} ({s.State}).");
        return sb.Append("Сменить: agent action:'model' model:'название или слова' (id — одному агенту; без id — всем и по умолчанию).").ToString();
    }

    /// <summary>Модели для меню вкладки и пересадка из него (настройки — те, с которыми запускался первый агент).</summary>
    internal static Task<List<string>> UiModelsAsync() => _cfg is { } cfg ? ModelsAsync(cfg) : Task.FromResult(new List<string>());

    internal static Task<string> UiSwitchAsync(AgentSession s, string model) =>
        _cfg is { } cfg ? ModelAsync(s.Id.ToString(), model, cfg) : Task.FromResult("Настройки агентов ещё не загружены.");

    /// <summary>Указание агенту по ходу (или новая задача законченному — он помнит всё сделанное).</summary>
    public static string Tell(string id, string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return "Пустое указание.";
        var s = Find(id, preferWorking: true);
        if (s is null) return Sessions.Count == 0 ? "Агентов нет — дай задачу через agent start." : $"Агента «{id}» нет. {Overview()}";
        if (!s.Alive) return $"Агент #{s.Id} уже закрыт — дай новую задачу через agent start.";
        s.Say(AgentLine.You(note.Trim()));
        s.Remember(note.Trim());
        s.Send($"(Друг через Клаву по ходу: {note.Trim()})");
        return $"Передала агенту #{s.Id} «{s.Title}» — учтёт на ближайшем шаге.";
    }

    public static string Stop(string id)
    {
        if (id.Trim() is "all" or "все" or "всех")
        {
            var alive = Sessions.Where(s => s.Alive).ToList();
            foreach (var x in alive) x.Kill("остановлен");
            return alive.Count == 0 ? "Агентов и так нет." : $"Остановила агентов: {string.Join(", ", alive.Select(x => $"#{x.Id}"))}.";
        }
        var s = Find(id, preferWorking: true);
        if (s is null || !s.Alive) return "Этот агент и так не работает.";
        s.Kill("остановлен");
        return $"Остановила агента #{s.Id} «{s.Title}».";
    }

    /// <summary>Закрыть вкладку: работающего — сначала остановить.</summary>
    public static void Forget(AgentSession s)
    {
        if (s.Alive) s.Kill("закрыт");
        lock (Gate) List.Remove(s);
        Raise(() => Changed?.Invoke(s));
    }

    /// <summary>Все агенты одной-двумя строками на каждого — Клаве в контекст и для agent list.</summary>
    public static string Overview()
    {
        var all = Sessions;
        if (all.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var s in all.OrderBy(x => x.Id)) sb.AppendLine(s.Brief());
        return sb.ToString().TrimEnd();
    }

    /// <summary>Подробно про одного: состояние, план, субагенты, последние шаги.</summary>
    public static string Status(string id)
    {
        var s = Find(id, preferWorking: true);
        if (s is null) return Sessions.Count == 0 ? "Агентов нет." : Overview();
        return s.Details();
    }

    /// <summary>Раз в секунду (TaskTick Клавы): законченные сессии, что давно без дела, закрываются.</summary>
    public static void Tick(KlavaConfig cfg)
    {
        foreach (var s in Sessions.Where(x => x.Alive && !x.Working && DateTime.Now - x.LastActivity > TimeSpan.FromMinutes(Math.Clamp(cfg.AgentIdleMinutes, 5, 240))))
            s.Close();
        foreach (var s in Sessions.Where(x => x.Working && DateTime.Now - x.TurnStarted > TimeSpan.FromMinutes(Math.Clamp(cfg.AgentMaxMinutes, 5, 480))))
            s.Kill("время на задачу вышло");
    }

    /// <summary>
    /// Снимок мультитерминала (--ui-shot terminal): записанные сырые события сессии (KLAVA_AGENT_RAW) проигрываются
    /// в новую сессию без процесса — вкладка, шапка, субагенты и план как вживую.
    /// </summary>
    internal static AgentSession Replay(string title, string goal, string cwd, IEnumerable<string> rawLines, ToolBox box)
    {
        int id;
        lock (Gate) id = _nextId++;
        var s = new AgentSession(id, title, goal, cwd, "запись сессии", "", box, _ => Task.FromResult(false));
        lock (Gate) List.Add(s);
        s.Replay(rawLines);
        return s;
    }

    public static void StopAll()
    {
        foreach (var s in Sessions.Where(x => x.Alive)) s.Kill("Клава закрывается");
    }

    private static AgentSession? Find(string id, bool preferWorking)
    {
        var all = Sessions;
        var t = id.Trim().TrimStart('#');
        if (int.TryParse(t, out var n)) return all.FirstOrDefault(s => s.Id == n);
        if (t.Length > 0)
            return all.Where(s => s.Title.Contains(t, StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.Working).ThenByDescending(s => s.Id).FirstOrDefault();
        // без номера: работающий (последний), иначе последний вообще
        return (preferWorking ? all.Where(s => s.Working).OrderByDescending(s => s.Id).FirstOrDefault() : null)
               ?? all.OrderByDescending(s => s.Id).FirstOrDefault();
    }

    private static string ResolveDir(string cwd, ToolBox box, KlavaConfig cfg)
    {
        var baseDir = box.Shell.Cwd is { Length: > 0 } c && Directory.Exists(c) ? c : box.WorkDir.Length > 0 ? box.WorkDir : cfg.Abs("workspace");
        if (string.IsNullOrWhiteSpace(cwd)) return baseDir;
        var p = Environment.ExpandEnvironmentVariables(cwd.Trim().Trim('"'));
        if (p.StartsWith('~')) p = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + p[1..];
        return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p));
    }

    // ---------- на чём думать ----------

    internal sealed record Route(string Label, string Model, Dictionary<string, string> Env);

    /// <param name="model">Какую модель взять (пересадка): имена подписки (sonnet, opus, haiku) — через подписку, если CLI
    /// залогинен; любая другая — через шлюз. null — как настроено.</param>
    private static async Task<Route?> RouteAsync(KlavaConfig cfg, string? model = null)
    {
        var subscriptionName = model is null || model is "sonnet" or "opus" or "haiku";
        if (cfg.AgentEngine != "ag" && subscriptionName)
        {
            if (DateTime.Now - _login.At > TimeSpan.FromMinutes(10))
                _login = (DateTime.Now, await ClaudeCode.LoggedInAsync().ConfigureAwait(false));
            if (_login.Ok == true) return new Route("Claude Code (подписка)", model ?? cfg.AgentClaudeModel, []);
        }
        var p = cfg.Brains.FirstOrDefault(b => b.Id == cfg.AgentBrain && !b.IsLocal && b.Kind == "openai");
        if (p is null) return null;
        var key = ApiKeys.Resolve(p.Key);
        if (key is null) return null;
        var url = cfg.AgentAnthropicUrl.Length > 0 ? cfg.AgentAnthropicUrl.TrimEnd('/') : V1Rx().Replace(cfg.ApiUrl(p).TrimEnd('/'), "");
        model ??= cfg.AgentModel;
        return new Route($"Claude Code через {p.Id}", model, new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = url,
            ["ANTHROPIC_API_KEY"] = key,
            // субагенты (Explore и др.) зовут «haiku» — у прокси её может не быть: все псевдонимы — на модель агента
            ["ANTHROPIC_DEFAULT_HAIKU_MODEL"] = model,
            ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = model,
            ["ANTHROPIC_DEFAULT_OPUS_MODEL"] = model,
        });
    }

    [GeneratedRegex(@"/v1$")]
    private static partial Regex V1Rx();

    /// <summary>Дев-плагины друга (установлены в ~/.claude/plugins, но не включены) — агентам на сессию.</summary>
    private static IEnumerable<string> Plugins(KlavaConfig cfg)
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "plugins", "installed_plugins.json");
        if (!File.Exists(file)) yield break;
        JsonObject? all;
        try { all = JsonNode.Parse(File.ReadAllText(file))?["plugins"] as JsonObject; }
        catch (Exception ex) when (ex is JsonException or IOException) { yield break; }
        if (all is null) yield break;
        var want = cfg.AgentPlugins.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, v) in all)
        {
            var name = key.Split('@')[0];
            if (!want.Contains(name) || v is not JsonArray { Count: > 0 } arr) continue;
            if (arr[0]?["installPath"]?.GetValue<string>() is { } dir && Directory.Exists(dir)) yield return dir;
        }
    }

    /// <summary>Что из инструментов разработчика стоит на компьютере (по PATH и vswhere) — агенту в правила.</summary>
    private static string Toolchain()
    {
        if (_toolchain is not null) return _toolchain;
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        bool Has(string exe) => path.Any(d => { try { return File.Exists(Path.Combine(d.Trim(), exe)); } catch (ArgumentException) { return false; } });
        var found = new List<string>();
        foreach (var (exe, what) in new[]
                 {
                     ("dotnet.exe", "dotnet (C#)"), ("pwsh.exe", "pwsh 7"), ("git.exe", "git"), ("node.exe", "node"), ("npm.cmd", "npm"),
                     ("python.exe", "python"), ("py.exe", "py"), ("uv.exe", "uv"), ("go.exe", "go"), ("cargo.exe", "cargo (Rust)"),
                     ("gcc.exe", "gcc"), ("g++.exe", "g++"), ("clang.exe", "clang"), ("cmake.exe", "cmake"), ("ninja.exe", "ninja"),
                     ("java.exe", "java"), ("javac.exe", "javac"), ("wsl.exe", "WSL (Linux)"), ("docker.exe", "docker"),
                     ("ffmpeg.exe", "ffmpeg"), ("rg.exe", "ripgrep"), ("gh.exe", "gh (GitHub)"),
                 })
            if (Has(exe)) found.Add(what);
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(vswhere)) found.Add("Visual Studio/MSVC (cl.exe — из Developer PowerShell: Launch-VsDevShell.ps1)");
        return _toolchain = found.Count == 0 ? "нестандартный набор — проверь сам" : string.Join(", ", found);
    }

    private static string Rules(AgentSession s) =>
        $"""
        Ты — агент #{s.Id} Клавы (кошкодевочки-ассистента) на Windows-компьютере её друга. Задачу даёт друг через Клаву; делай её сам, до конца, не спрашивая — друг занят разговором с Клавой, его указания по ходу придут сообщением «(Друг через Клаву по ходу: …)» — они главнее старого плана.
        Среда: Windows 11, папка задачи {s.Cwd}. Установлено: {Toolchain()}. Для Windows-команд бери PowerShell; в Bash (это Git Bash) `cmd /c` превращается в путь — пиши `cmd //c`. Linux — `wsl -d Ubuntu -- …`.
        Как работать:
        - Сначала разберись (поиск, чтение), потом меняй. Большие разведки и независимые куски отдавай субагентам параллельно (Task/Agent: Explore, general-purpose, code-explorer, code-architect, code-reviewer, re-specialist) — не тащи всё в один контекст.
        - Веди план через TodoWrite — Клава видит его и рассказывает другу, как идут дела.
        - Ненулевой код выхода — это сведения, а не конец работы: прочитай вывод, почини и продолжай.
        - После изменений проверяй сам: сборка, запуск, тест (dotnet build, python -m py_compile, node --check, pytest…). Готово — только проверенное.
        - Разрешения даёт Клава. Ничего не удаляй безвозвратно: в Корзину — PowerShell `Add-Type -AssemblyName Microsoft.VisualBasic; [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile('путь','OnlyErrorDialogs','SendToRecycleBin')` (папка — DeleteDirectory). Временные файлы, bin/obj/node_modules/__pycache__ — можно обычным удалением.
        - Мышь и клавиатуру не трогай — это лапки Клавы. Ключи и пароли не выводи и не записывай в файлы.
        - Закончил (или упёрся) — последним сообщением итог 3–8 строк по-русски: что сделано, где файлы, как проверено, что не вышло и почему.
        """;

    internal static void Raise(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { Log.Error("агенты: событие", ex); }
    }

    internal static void OnLine(AgentSession s, AgentLine l) => Raise(() => Line?.Invoke(s, l));
    internal static void OnChanged(AgentSession s) => Raise(() => Changed?.Invoke(s));
    internal static void OnTurnDone(AgentSession s, string result) => Raise(() => TurnDone?.Invoke(s, result));

    internal static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    // ---------- разрешения ----------

    // потеря незакоммиченного и чужой истории — только с согласия друга
    [GeneratedRegex(@"\bgit\s+(reset\s+--hard|clean\s+-\w*f|checkout\s+--\s+\.|restore\s+(--staged\s+)?\.|push\s+(-f|--force)|branch\s+-D|stash\s+(drop|clear))|Clear-RecycleBin", RegexOptions.IgnoreCase)]
    private static partial Regex DataLossRx();

    internal enum Verdict { Allow, Deny, Ask }

    /// <summary>
    /// Решение по запросу Claude Code: инструмент и его вход → можно / нельзя (с причиной — агенту) / спросить друга.
    /// Автопилот: всё, кроме катастроф, безвозвратного удаления вне временного и потери незакоммиченного.
    /// </summary>
    internal static (Verdict V, string Why) Policy(string tool, JsonObject? input, bool autopilot)
    {
        if (tool.StartsWith("mcp__voice", StringComparison.Ordinal) || tool.StartsWith("mcp__ag-computer-use", StringComparison.Ordinal))
            return (Verdict.Deny, "голос и мышь — Клавины, агенту нельзя");
        var command = input?["command"]?.ToString();
        if (command is { Length: > 0 } && tool is "Bash" or "PowerShell")
        {
            if (Tools.IsCatastrophe(command) is { } cat) return (Verdict.Deny, $"катастрофическая команда ({cat}) — не выполняю ни на каком режиме");
            if (Tools.IsHardDelete(command))
                return (Verdict.Deny, "безвозвратно удалять нельзя — перенеси в Корзину: Add-Type -AssemblyName Microsoft.VisualBasic; " +
                                      "[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile('путь','OnlyErrorDialogs','SendToRecycleBin') (папка — DeleteDirectory)");
            if (DataLossRx().IsMatch(command)) return (Verdict.Ask, $"может потерять незакоммиченное: {DataLossRx().Match(command).Value}");
            if (autopilot) return (Verdict.Allow, "");
            return Tools.DangerOf(command) is { } d ? (Verdict.Ask, $"опасная команда: {d}") : (Verdict.Allow, "");
        }
        var file = input?["file_path"]?.ToString() ?? input?["notebook_path"]?.ToString();
        if (file is { Length: > 0 } && tool is "Write" or "Edit" or "MultiEdit" or "NotebookEdit" && Tools.IsSystemPath(file))
            return (Verdict.Ask, "запись в системную папку");
        return (Verdict.Allow, "");
    }
}

/// <summary>Строка вкладки агента: что это (текст, шаг, итог шага, субагент, служебное, указание друга, разрешение).</summary>
internal readonly record struct AgentLine(DateTime At, char Kind, string Text, string? Sub)
{
    /// <summary>Номер строки в сессии (1, 2, …) — вкладка терминала по нему не показывает строку дважды.</summary>
    public long Seq { get; init; }

    public static AgentLine Sys(string t) => new(DateTime.Now, 's', t, null);
    public static AgentLine You(string t) => new(DateTime.Now, 'y', t, null);
}

/// <summary>Субагент внутри сессии (инструмент Task/Agent Claude Code).</summary>
internal sealed class SubAgent(string type, string description)
{
    public string Type { get; } = type;
    public string Description { get; } = description;
    public string State { get; set; } = "работает";
    public string Doing { get; set; } = "";
    public int Steps { get; set; }
    public DateTime Started { get; } = DateTime.Now;
}

/// <summary>Одна сессия Claude Code: процесс, поток событий, состояние для Клавы и вкладки терминала.</summary>
internal sealed class AgentSession(int id, string title, string goal, string cwd, string route, string model, ToolBox box,
    Func<ToolRequest, Task<bool>> approve)
{
    private readonly object _gate = new();
    private readonly List<AgentLine> _lines = [];
    private long _seq;
    private readonly Dictionary<string, string> _toolNames = [];
    private readonly Dictionary<string, SubAgent> _subs = [];
    private readonly SortedDictionary<int, (string Text, string Status)> _tasks = [];   // план TaskCreate/TaskUpdate
    private static readonly JsonSerializerOptions Readable = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly string? RawDump = Environment.GetEnvironmentVariable("KLAVA_AGENT_RAW");
    private Process? _proc;
    private StreamWriter? _in;
    private readonly SemaphoreSlim _write = new(1, 1);

    public int Id { get; } = id;
    public string Title { get; } = title;
    public string Goal { get; } = goal;
    public string Cwd { get; } = cwd;
    public string Route { get; private set; } = route;
    public string Model { get; private set; } = model;
    /// <summary>Последний ход кончился ошибкой (а не итогом) — при смене модели такой агент продолжает сам.</summary>
    public bool Failed { get; private set; }
    private readonly HashSet<string> _tried = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _notes = [];
    private string _lastGood = "";
    private string? _pendingHandoff;

    internal void MarkTried(string model) { lock (_gate) _tried.Add(model); }
    internal bool WasTried(string model) { lock (_gate) return _tried.Contains(model); }
    internal void ClearTried() { lock (_gate) _tried.Clear(); }
    internal string TriedText { get { lock (_gate) return string.Join(", ", _tried); } }
    internal void Remember(string note) { lock (_gate) { _notes.Add(note); if (_notes.Count > 12) _notes.RemoveAt(0); } }

    /// <summary>
    /// Что передать новому процессу при пересадке на другую модель: задача, указания по ходу, план, последние шаги и
    /// последний удачный итог — память прежней сессии к нему не переходит.
    /// </summary>
    internal string Handoff(string why)
    {
        var sb = new StringBuilder($"(Ты продолжаешь задачу, которую вёл другой агент ({Model}): {why}. Его память тебе не передалась — вот главное.\n");
        sb.Append("Задача: ").AppendLine(Goal);
        lock (_gate)
        {
            if (_notes.Count > 0) sb.AppendLine("Указания по ходу:").AppendLine(string.Join("\n", _notes.Select(n => "- " + n)));
            if (Plan.Count > 0)
                sb.AppendLine("План прежнего агента (✓ сделано, ▶ в работе, · не начато):")
                  .AppendLine(string.Join("\n", Plan.Select(x => $"{(x.Status == "completed" ? "✓" : x.Status == "in_progress" ? "▶" : "·")} {x.Text}")));
            var steps = _lines.Where(l => l.Kind is not ('s' or 'y' or 'p')).TakeLast(14).Select(l => AgentHub.Short(l.Text.Replace('\n', ' '), 220)).ToList();
            if (steps.Count > 0) sb.AppendLine("Его последние шаги:").AppendLine(string.Join("\n", steps.Select(x => "· " + x)));
        }
        if (_lastGood.Length > 0) sb.Append("Его последний итог: ").AppendLine(AgentHub.Short(_lastGood, 900));
        return sb.Append($"Рабочая папка та же: {Cwd}. Сначала осмотри, что в ней уже сделано (файлы, сборка, запуск), потом продолжай с того места — не начинай заново.)").ToString();
    }

    /// <summary>Пересадка: прежний процесс гасится, сессия (вкладка, лента, план) остаётся и ждёт новый процесс.</summary>
    internal void Rehost(string routeLabel, string model)
    {
        var old = _proc;
        _proc = null;
        _in = null;
        try { if (old is { HasExited: false }) old.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        Route = routeLabel;
        Model = model;
        Failed = false;
        // план и субагенты были у прежнего процесса — новый заведёт свои (прежний план он получил словами)
        lock (_gate) { _tasks.Clear(); _subs.Clear(); }
        Plan = [];
        State = $"пересаживается на {model}";
        AgentHub.OnChanged(this);
    }

    /// <summary>После пересадки без продолжения: ждёт указаний; что было сделано, уйдёт новому процессу вместе с первым указанием.</summary>
    internal void Idle(string handoff)
    {
        _pendingHandoff = handoff;
        Working = false;
        State = "готово, ждёт указаний";
        AgentHub.OnChanged(this);
    }
    public DateTime Started { get; } = DateTime.Now;
    public DateTime LastActivity { get; private set; } = DateTime.Now;
    public DateTime TurnStarted { get; private set; } = DateTime.Now;
    public bool Alive { get; private set; }
    public bool Working { get; private set; }
    public string State { get; private set; } = "запускается";
    public string Doing { get; private set; } = "";
    public int Steps { get; private set; }
    public double Cost { get; private set; }
    public string LastResult { get; private set; } = "";
    public string Init { get; private set; } = "";
    public List<(string Text, string Status)> Plan { get; private set; } = [];

    public IReadOnlyList<AgentLine> Lines { get { lock (_gate) return [.. _lines]; } }
    public IReadOnlyList<SubAgent> Subs { get { lock (_gate) return [.. _subs.Values]; } }

    internal void Attach(Process p)
    {
        _proc = p;
        _in = p.StandardInput;
        Alive = true;
        Working = true;
        State = "работает";
        _ = Task.Run(() => ReadAsync(p));
        _ = Task.Run(() => ErrAsync(p));
    }

    /// <summary>Проиграть записанные события (без процесса): для снимка вкладки.</summary>
    internal void Replay(IEnumerable<string> rawLines)
    {
        Alive = true;
        Working = true;
        State = "работает";
        Say(AgentLine.Sys($"🐱 агент #{Id} «{Title}» — {Route}, папка {Cwd}"));
        Say(AgentLine.You(Goal));
        foreach (var line in rawLines)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            try
            {
                if (JsonNode.Parse(line) is JsonObject m && m["type"]?.GetValue<string>() != "control_request") Handle(m);
            }
            catch (JsonException) { }
        }
    }

    internal void Say(AgentLine l)
    {
        lock (_gate)
        {
            l = l with { Seq = ++_seq };
            _lines.Add(l);
            if (_lines.Count > 800) _lines.RemoveRange(0, 200);
        }
        LastActivity = DateTime.Now;
        AgentHub.OnLine(this, l);
    }

    /// <summary>Реплика «пользователя» в сессию: задача или указание по ходу.</summary>
    internal void Send(string text)
    {
        if (_pendingHandoff is { } handoff) { _pendingHandoff = null; text = $"{handoff}\n\n{text}"; }
        if (!Working) TurnStarted = DateTime.Now;
        Working = true;
        State = "работает";
        AgentHub.OnChanged(this);
        _ = WriteAsync(new JsonObject { ["type"] = "user", ["message"] = new JsonObject { ["role"] = "user", ["content"] = text } });
    }

    private async Task WriteAsync(JsonObject o)
    {
        if (_in is null) return;
        await _write.WaitAsync().ConfigureAwait(false);
        try
        {
            await _in.WriteLineAsync(o.ToJsonString()).ConfigureAwait(false);
            await _in.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Write($"агент #{Id}: не смогла написать в процесс — {ex.Message}");
        }
        finally { _write.Release(); }
    }

    internal void Kill(string why)
    {
        try { if (_proc is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        Finish(why);
    }

    /// <summary>Закрыть давно простаивающую сессию мягко: конец ввода — процесс сам выходит.</summary>
    internal void Close()
    {
        try { _in?.Close(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        Finish("закрыта за простоем");
    }

    private void Finish(string why)
    {
        if (!Alive) return;
        Alive = false;
        Working = false;
        State = why;
        Say(AgentLine.Sys($"⏹ {why}"));
        Log.Write($"агент #{Id}: {why}");
        AgentHub.OnChanged(this);
    }

    private async Task ErrAsync(Process p)
    {
        try
        {
            while (await p.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
                if (line.Trim().Length > 0) Log.Write($"агент #{Id} stderr: {AgentHub.Short(line, 300)}");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task ReadAsync(Process p)
    {
        try
        {
            while (await p.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                if (RawDump is { Length: > 0 })   // разбор протокола: сырые события в файл (KLAVA_AGENT_RAW)
                {
                    try { await File.AppendAllTextAsync(RawDump, line + "\n").ConfigureAwait(false); }
                    catch (IOException) { }
                }
                JsonObject? m;
                try { m = JsonNode.Parse(line) as JsonObject; }
                catch (JsonException) { continue; }
                if (!ReferenceEquals(_proc, p)) break;   // хвост событий процесса, снятого при пересадке
                if (m is not null) Handle(m);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        int code;
        try { await p.WaitForExitAsync().ConfigureAwait(false); code = p.ExitCode; }
        catch (InvalidOperationException) { code = -1; }
        if (!ReferenceEquals(_proc, p)) return;   // процесс сняли при пересадке на другую модель — сессия живёт дальше
        if (Alive)
        {
            if (Working) AgentHub.OnTurnDone(this, $"агент упал посреди работы (код {code}); последнее: {Doing}");
            Finish(Working ? $"упал (код {code})" : "закрыт");
        }
    }

    private void Handle(JsonObject m)
    {
        switch (m["type"]?.GetValue<string>())
        {
            case "system":
                OnSystem(m);
                break;
            case "assistant":
                OnAssistant(m);
                break;
            case "user":
                OnToolResults(m);
                break;
            case "result":
                OnResult(m);
                break;
            case "control_request":
                _ = OnControlAsync(m);
                break;
        }
    }

    /// <summary>
    /// Служебные события: init, ужатие контекста и фоновые субагенты (task_started / task_progress / task_notification —
    /// их шаги идут своим parent_tool_use_id, а конец приходит здесь), task_summary — чем агент занят прямо сейчас.
    /// </summary>
    private void OnSystem(JsonObject m)
    {
        switch (m["subtype"]?.GetValue<string>())
        {
            case "init":
                OnInit(m);
                break;
            case "compact_boundary":
                Say(AgentLine.Sys("🗜 контекст ужат — работаю дальше"));
                break;
            case "task_summary" when m["detail"]?.GetValue<string>() is { Length: > 0 } detail:
                Doing = detail;
                AgentHub.OnChanged(this);
                break;
            case "task_progress" when TaskSub(m) is { } sub:
                sub.Doing = m["description"]?.GetValue<string>() ?? sub.Doing;
                sub.Steps = m["usage"]?["tool_uses"]?.GetValue<int>() ?? sub.Steps;
                AgentHub.OnChanged(this);
                break;
            case "task_notification" when TaskSub(m) is { } sub:
            {
                var status = m["status"]?.GetValue<string>() ?? "completed";
                sub.State = status == "completed" ? "готово" : status == "failed" ? "ошибка" : status;
                Say(new AgentLine(DateTime.Now, 'a', $"🧩 субагент {sub.Type} {(status == "completed" ? "закончил" : status)}: {AgentHub.Short(OneLine(m["summary"]?.ToString() ?? ""), 240)}", null));
                AgentHub.OnChanged(this);
                break;
            }
        }
    }

    private SubAgent? TaskSub(JsonObject m)
    {
        var id = m["tool_use_id"]?.GetValue<string>();
        if (id is null) return null;
        lock (_gate) return _subs.GetValueOrDefault(id);
    }

    private void OnInit(JsonObject m)
    {
        var mcp = m["mcp_servers"] as JsonArray ?? [];
        var mcpText = string.Join(" ", mcp.Select(x => $"{x?["name"]}{(x?["status"]?.GetValue<string>() == "connected" ? "✓" : "…")}"));
        var agents = m["agents"] as JsonArray ?? [];
        Init = $"модель {m["model"]}, инструментов {(m["tools"] as JsonArray)?.Count ?? 0}, скиллов {(m["skills"] as JsonArray)?.Count ?? 0}, " +
               $"плагинов {(m["plugins"] as JsonArray)?.Count ?? 0}, субагенты: {string.Join(", ", agents.Select(a => a?.ToString()))}; MCP: {mcpText}";
        Say(AgentLine.Sys($"⚙ {Init}"));
        AgentHub.OnChanged(this);
    }

    private SubAgent? SubOf(JsonObject m)
    {
        var parent = m["parent_tool_use_id"]?.GetValue<string>();
        if (parent is null) return null;
        lock (_gate) return _subs.GetValueOrDefault(parent);
    }

    private void OnAssistant(JsonObject m)
    {
        var sub = SubOf(m);
        if (m["message"]?["content"] is not JsonArray parts) return;
        foreach (var c in parts)
        {
            switch (c?["type"]?.GetValue<string>())
            {
                case "text":
                {
                    var t = c["text"]?.GetValue<string>()?.Trim() ?? "";
                    if (t.Length == 0) break;
                    Say(new AgentLine(DateTime.Now, 't', t, sub?.Type));
                    break;
                }
                case "tool_use":
                {
                    var id = c["id"]?.GetValue<string>() ?? "";
                    var name = c["name"]?.GetValue<string>() ?? "?";
                    var input = c["input"] as JsonObject;
                    lock (_gate) _toolNames[id] = name;
                    if (name is "Task" or "Agent")
                    {
                        var s = new SubAgent(input?["subagent_type"]?.GetValue<string>() ?? "general-purpose",
                            input?["description"]?.GetValue<string>() ?? AgentHub.Short(input?["prompt"]?.GetValue<string>() ?? "", 60));
                        lock (_gate) _subs[id] = s;
                        Steps++;
                        Doing = $"субагент {s.Type}: {s.Description}";
                        Say(new AgentLine(DateTime.Now, 'a', $"🧩 запускаю субагента {s.Type}: {s.Description}", null));
                        AgentHub.OnChanged(this);
                        break;
                    }
                    if (name == "TodoWrite" && input?["todos"] is JsonArray todos)
                    {
                        Plan = [.. todos.Select(x => (x?["content"]?.GetValue<string>() ?? "", x?["status"]?.GetValue<string>() ?? ""))];
                        SayPlan(sub);
                        break;
                    }
                    // план новых версий Claude Code: TaskCreate (номер придёт в ответе) и TaskUpdate по номеру
                    if (name is "TaskCreate" or "TaskList" or "TaskGet") break;
                    if (name == "TaskUpdate")
                    {
                        if (int.TryParse(input?["taskId"]?.ToString(), out var tid))
                        {
                            lock (_gate)
                            {
                                var cur = _tasks.GetValueOrDefault(tid, (Text: "", Status: "pending"));
                                _tasks[tid] = (input?["subject"]?.GetValue<string>() ?? cur.Text, input?["status"]?.GetValue<string>() ?? cur.Status);
                                Plan = [.. _tasks.Values];
                            }
                            SayPlan(sub);
                        }
                        break;
                    }
                    var what = Describe(name, input);
                    if (sub is not null) { sub.Steps++; sub.Doing = what; }
                    else { Steps++; Doing = what; }
                    Say(new AgentLine(DateTime.Now, 'c', what, sub?.Type));
                    AgentHub.OnChanged(this);
                    break;
                }
            }
        }
    }

    private void OnToolResults(JsonObject m)
    {
        var sub = SubOf(m);
        if (m["message"]?["content"] is not JsonArray parts) return;
        foreach (var c in parts)
        {
            if (c?["type"]?.GetValue<string>() != "tool_result") continue;
            var id = c["tool_use_id"]?.GetValue<string>() ?? "";
            var err = c["is_error"]?.GetValue<bool>() == true;
            var text = c["content"] switch
            {
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                JsonArray arr => string.Join("\n", arr.Select(x => x?["text"]?.GetValue<string>() ?? "")),
                _ => "",
            };
            SubAgent? done;
            string? name;
            lock (_gate)
            {
                done = _subs.GetValueOrDefault(id);
                name = _toolNames.GetValueOrDefault(id);
            }
            if (done is not null)
            {
                if (!err && text.StartsWith("Async agent launched", StringComparison.Ordinal))
                    continue;   // субагент ушёл работать в фоне — его шаги идут со своим parent_tool_use_id, конец придёт позже
                done.State = err ? "ошибка" : "готово";
                Say(new AgentLine(DateTime.Now, 'a', $"🧩 субагент {done.Type} {(err ? "споткнулся" : "закончил")}: {AgentHub.Short(OneLine(text), 240)}", null));
                AgentHub.OnChanged(this);
                continue;
            }
            if (name is "TodoWrite" or "TaskUpdate" or "TaskList" or "TaskGet") continue;
            if (name is "TaskCreate")
            {
                var tm = Regex.Match(text, @"Task #(\d+) created successfully:\s*(.+)");
                if (tm.Success)
                {
                    lock (_gate)
                    {
                        _tasks[int.Parse(tm.Groups[1].Value)] = (tm.Groups[2].Value.Trim(), "pending");
                        Plan = [.. _tasks.Values];
                    }
                    SayPlan(sub);
                }
                continue;
            }
            Say(new AgentLine(DateTime.Now, err ? 'e' : 'o', Summary(name, text, err), sub?.Type));
        }
    }

    private void SayPlan(SubAgent? sub)
    {
        var plan = Plan;
        var done = plan.Count(x => x.Status == "completed");
        var now = plan.FirstOrDefault(x => x.Status == "in_progress").Text;
        Say(new AgentLine(DateTime.Now, 'p', $"📋 план {done}/{plan.Count}{(now is { Length: > 0 } ? $" — сейчас: {now}" : "")}", sub?.Type));
        AgentHub.OnChanged(this);
    }

    private void OnResult(JsonObject m)
    {
        Working = false;
        var ok = m["subtype"]?.GetValue<string>() == "success" && m["is_error"]?.GetValue<bool>() != true;
        Cost += m["total_cost_usd"]?.GetValue<double>() ?? 0;
        LastResult = m["result"]?.ToString() ?? "";
        State = ok ? "готово, ждёт указаний" : $"закончил с ошибкой ({m["subtype"]})";
        Doing = "";
        Say(new AgentLine(DateTime.Now, 'r', $"🏁 {(ok ? "итог" : "стоп")}: {LastResult}", null));
        Log.Write($"агент #{Id}: итог хода ({(ok ? "ок" : m["subtype"]?.ToString())}, шагов {Steps}) — {AgentHub.Short(LastResult.Replace('\n', ' '), 300)}");
        Failed = !ok;
        if (ok) { _lastGood = LastResult; ClearTried(); }
        AgentHub.OnChanged(this);
        // ошибка API (лимит, 503, нет модели) — не итог: агент сам пересаживается на запасную модель и продолжает
        if (!ok && AgentHub.TryFailover(this, LastResult)) return;
        AgentHub.OnTurnDone(this, ok ? LastResult : $"{State}: {LastResult}");
    }

    /// <summary>Claude Code спрашивает разрешение (can_use_tool) — решает Клава: политика, при надобности окно друга.</summary>
    private async Task OnControlAsync(JsonObject m)
    {
        var reqId = m["request_id"]?.GetValue<string>() ?? "";
        var req = m["request"] as JsonObject;
        if (req?["subtype"]?.GetValue<string>() != "can_use_tool")
        {
            await WriteAsync(new JsonObject
            {
                ["type"] = "control_response",
                ["response"] = new JsonObject { ["subtype"] = "error", ["request_id"] = reqId, ["error"] = "хост Клавы этого не поддерживает" },
            }).ConfigureAwait(false);
            return;
        }
        var tool = req["tool_name"]?.GetValue<string>() ?? "";
        var input = req["input"] as JsonObject;
        var (v, why) = AgentHub.Policy(tool, input, box.Autopilot);
        var what = Describe(tool, input);
        if (v == AgentHub.Verdict.Ask)
        {
            Say(new AgentLine(DateTime.Now, 'w', $"✋ спрашиваю друга: {what} — {why}", null));
            var details = input?["command"]?.ToString() ?? input?["file_path"]?.ToString() ?? input?.ToJsonString() ?? "";
            bool yes;
            try { yes = await approve(new ToolRequest($"Агент #{Id} «{Title}»: {tool}", details, true, why, "danger")).ConfigureAwait(false); }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { yes = false; }
            v = yes ? AgentHub.Verdict.Allow : AgentHub.Verdict.Deny;
            if (!yes) why = "друг не разрешил";
        }
        if (v == AgentHub.Verdict.Deny)
        {
            Say(new AgentLine(DateTime.Now, 'w', $"⛔ нельзя: {what} — {why}", null));
            Log.Write($"агент #{Id}: запрещено {tool} — {why} — {AgentHub.Short(what, 200)}");
        }
        var response = v == AgentHub.Verdict.Allow
            ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input?.DeepClone() ?? new JsonObject() }
            : new JsonObject { ["behavior"] = "deny", ["message"] = $"Клава не разрешила: {why}" };
        await WriteAsync(new JsonObject
        {
            ["type"] = "control_response",
            ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = reqId, ["response"] = response },
        }).ConfigureAwait(false);
    }

    // ---------- как показать ----------

    private static string OneLine(string s) => string.Join(" ", s.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())).Trim();

    internal static string Describe(string name, JsonObject? input)
    {
        string A(string k) => input?[k]?.ToString() ?? "";
        var first = A("command").Replace("\r", "").Split('\n')[0];
        return name switch
        {
            "Bash" or "PowerShell" => $"{(name == "Bash" ? "$" : "PS>")} {AgentHub.Short(first, 160)}{(A("command").Contains('\n') ? " …" : "")}",
            "Read" => $"читаю {A("file_path")}",
            "Edit" or "MultiEdit" => $"правлю {A("file_path")}",
            "Write" => $"пишу {A("file_path")}",
            "NotebookEdit" => $"правлю блокнот {A("notebook_path")}",
            "Grep" => $"ищу «{AgentHub.Short(A("pattern"), 60)}» в {(A("path") is { Length: > 0 } p ? p : ".")}",
            "Glob" => $"файлы {A("pattern")}",
            "WebFetch" => $"читаю {A("url")}",
            "WebSearch" => $"ищу в сети: {A("query")}",
            "Skill" => $"скилл {A("skill")}{A("command")}",
            _ when name.StartsWith("mcp__", StringComparison.Ordinal) => $"MCP {name[5..].Replace("__", ": ")} {AgentHub.Short(input?.ToJsonString(Readable) ?? "", 100)}",
            _ => $"{name} {AgentHub.Short(input?.ToJsonString(Readable) ?? "", 120)}",
        };
    }

    private static string Summary(string? tool, string text, bool err)
    {
        var t = text.Replace("\r", "").Trim();
        var code = "";
        var m = Regex.Match(t, @"^Exit code (-?\d+)\s*");
        if (m.Success) { code = $"код {m.Groups[1].Value}: "; t = t[m.Length..]; }
        var lines = t.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var head = string.Join(" ⏎ ", lines.Take(tool is "Bash" or "PowerShell" ? 3 : 1).Select(x => x.Trim()));
        var more = lines.Length > 3 ? $" (+{lines.Length - 3} строк)" : "";
        return $"{(err ? "✗" : "✓")} {code}{AgentHub.Short(head, 220)}{more}";
    }

    internal string Brief()
    {
        var age = DateTime.Now - (Working ? TurnStarted : LastActivity);
        var plan = Plan.Count > 0 ? $", план {Plan.Count(x => x.Status == "completed")}/{Plan.Count}" : "";
        var subs = Subs;
        var subText = subs.Count == 0 ? "" :
            "; субагенты: " + string.Join(", ", subs.TakeLast(4).Select(x => $"{x.Type} {(x.State == "работает" ? $"работает ({AgentHub.Short(x.Doing, 50)})" : x.State)}"));
        return Working
            ? $"#{Id} «{Title}» — работает {age:mm\\:ss}, шаг {Steps}: {AgentHub.Short(Doing, 90)}{plan}{subText}"
            : $"#{Id} «{Title}» — {State} ({(int)age.TotalMinutes} мин назад){plan}: {AgentHub.Short(OneLine(LastResult), 160)}";
    }

    internal string Details()
    {
        var sb = new StringBuilder();
        sb.AppendLine(Brief());
        sb.AppendLine($"папка {Cwd}; {Route}; {Init}");
        if (Plan.Count > 0) sb.AppendLine("план: " + string.Join("; ", Plan.Select(x => $"{(x.Status == "completed" ? "✓" : x.Status == "in_progress" ? "▶" : "·")} {x.Text}")));
        foreach (var s in Subs) sb.AppendLine($"субагент {s.Type} «{s.Description}» — {s.State}, шагов {s.Steps}{(s.Doing.Length > 0 ? $", последнее: {AgentHub.Short(s.Doing, 80)}" : "")}");
        sb.AppendLine("последние шаги:");
        foreach (var l in Lines.TakeLast(14)) sb.AppendLine($"  {(l.Sub is { } su ? $"[{su}] " : "")}{AgentHub.Short(OneLine(l.Text), 200)}");
        return sb.ToString().TrimEnd();
    }
}
