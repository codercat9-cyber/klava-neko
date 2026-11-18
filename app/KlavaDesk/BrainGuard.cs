using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Сторож мозгов — чтобы «мозг не ответил» больше не было (друг 18.11: «мозг должен отвечать всегда»).
///
/// Что ломало ответы (логи 01.11):
///  - Клава сидела на claude-opus-5-5-high и gemini-4-argon — обе модели есть только на ОДНОМ аккаунте AG Tools из пяти.
///    Argon на нуле квоты сыпал ошибки, circuit breaker AG запер весь аккаунт (60→300→1800→7200 с), и прокси отвечал
///    «All accounts rate-limited, cooldown 5300s» / 502 «нет доступных аккаунтов, все на охлаждении», хотя у остальных
///    моделей (Claude 4.6, Gemini 3.x, GPT-OSS) на четырёх-пяти аккаунтах было 77–100 % квоты;
///  - на такой отказ она трижды долбила ту же модель, потом прыгала на единственный запасной (Жминя по ключу с нулевой
///    квотой) — и всё;
///  - прокси AG Tools падал посреди разговора (01.11 00:39–01:31, сотня «подключение отвергнуто»), а поднимала его
///    Клава только при старте и ручной смене мозга.
///
/// Теперь: отказ разбирается (лимит / нет модели / нет денег / перегруз / сбой), модель или весь профиль отдыхают ровно
/// столько, сколько сказал сервер; на лимите — пересадка на соседнюю модель того же сервера (список живой, с /models:
/// модели у AG появляются и пропадают сами), упавший прокси поднимается сразу.
/// Все await здесь с ConfigureAwait(false): мозг ходит с UI-потока (см. память про дедлок).
/// </summary>
internal static partial class BrainGuard
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, (DateTime Until, string Why)> Cooled = new(StringComparer.OrdinalIgnoreCase);

    private static string Slot(BrainProfile p, string? model) => $"{p.Id}|{model ?? "*"}";

    /// <summary>Отправить модель (model = null — весь профиль) отдыхать. Не дольше 3 часов, не меньше 15 секунд.</summary>
    public static void Cool(BrainProfile p, string? model, TimeSpan span, string why)
    {
        if (span < TimeSpan.FromSeconds(15)) span = TimeSpan.FromSeconds(15);
        if (span > TimeSpan.FromHours(3)) span = TimeSpan.FromHours(3);
        var until = DateTime.UtcNow + span;
        lock (Gate)
        {
            var key = Slot(p, model);
            if (Cooled.TryGetValue(key, out var old) && old.Until > until) return;
            Cooled[key] = (until, why);
        }
        Log.Write($"сторож мозгов: {p.Id}{(model is null ? "" : "/" + model)} отдыхает {Human(span)} — {Short(why, 120)}");
    }

    /// <summary>Сколько ещё отдыхает модель (или весь профиль). null — свободна.</summary>
    public static TimeSpan? CooledFor(BrainProfile p, string? model)
    {
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            TimeSpan? left = null;
            foreach (var key in model is null ? [Slot(p, null)] : new[] { Slot(p, null), Slot(p, model) })
                if (Cooled.TryGetValue(key, out var c))
                {
                    if (c.Until <= now) { Cooled.Remove(key); continue; }
                    var l = c.Until - now;
                    if (left is null || l > left) left = l;
                }
            return left;
        }
    }

    /// <summary>Ответила — снять отдых с модели (и с профиля: раз модель отвечает, сервер жив).</summary>
    public static void Clear(BrainProfile p, string? model)
    {
        lock (Gate)
        {
            Cooled.Remove(Slot(p, null));
            if (model is not null) Cooled.Remove(Slot(p, model));
        }
    }

    /// <summary>Сводка для пульта и пузыря: кто сейчас отдыхает.</summary>
    public static List<string> Snapshot()
    {
        var now = DateTime.UtcNow;
        lock (Gate)
            return Cooled.Where(c => c.Value.Until > now)
                .Select(c => $"{c.Key.Replace("|*", "").Replace('|', '/')}: ещё {Human(c.Value.Until - now)} ({Short(c.Value.Why, 60)})")
                .ToList();
    }

    // ---------------------------------------------------------------- разбор отказа

    /// <summary>Что значит отказ сервера.</summary>
    internal enum FailKind { Other, Limit, NoModel, NoMoney, BadKey, Busy }

    /// <param name="Kind">Вид отказа.</param>
    /// <param name="Span">Сколько не трогать.</param>
    /// <param name="WholeProfile">Отдыхает весь профиль (ключ, деньги), а не одна модель.</param>
    internal sealed record Verdict(FailKind Kind, TimeSpan Span, bool WholeProfile, string Text);

    [GeneratedRegex(@"cooldown\s*(?:wait)?\s*(\d+)\s*s", RegexOptions.IgnoreCase)]
    private static partial Regex CooldownRx();

    [GeneratedRegex(@"(rate.?limit|resource.?exhausted|exhausted|quota|too many requests|охлажд|нет доступных аккаунт|лимит|cooldown)", RegexOptions.IgnoreCase)]
    private static partial Regex LimitRx();

    [GeneratedRegex(@"(model_not_found|model not exist|model .{0,40}not found|не найдена|unknown model|no such model|does not exist)", RegexOptions.IgnoreCase)]
    private static partial Regex NoModelRx();

    // «billing» сюда не брать: у Жмини лимит ключа звучит «exceeded your current quota, please check your plan and billing»
    [GeneratedRegex(@"(insufficient.?balance|insufficient_quota|payment required|недостаточно средств)", RegexOptions.IgnoreCase)]
    private static partial Regex NoMoneyRx();

    [GeneratedRegex(@"(high demand|overloaded|unavailable|try again later|capacity|temporarily)", RegexOptions.IgnoreCase)]
    private static partial Regex BusyRx();

    [GeneratedRegex(@"quota exceeded for metric:.{0,160}\bmodel:\s*[\w.\-]+", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex GoogleModelQuotaRx();

    [GeneratedRegex(@"""retryDelay""\s*:\s*""(\d+)(?:\.\d+)?s""", RegexOptions.IgnoreCase)]
    private static partial Regex RetryDelayRx();

    /// <summary>
    /// Разобрать отказ: статус + тело. AG Tools кладёт причину в error.message с \u-экранированием и в
    /// gateway_error (error_code, diagnosis) — поэтому тело сначала разворачивается в плоский текст.
    /// </summary>
    public static Verdict Judge(int status, string body, TimeSpan? retryAfter, bool keyBased)
    {
        var text = Flatten(body);
        var cd = CooldownRx().Match(text);
        TimeSpan? told = cd.Success && int.TryParse(cd.Groups[1].Value, out var sec) ? TimeSpan.FromSeconds(sec) : retryAfter;
        // Google (ключ Жмини) считает квоту НА МОДЕЛЬ: «Quota exceeded for metric: …free_tier_requests, limit: 20,
        // model: gemini-4-hydrogen» (18.11). Отдыхать должна эта модель, а не весь ключ — на соседней модели квота своя.
        // Срок: retryDelay из details (минутный лимит), дневной (quotaId …PerDay…) — час, дальше проверим снова.
        if (status == 429 && keyBased && GoogleModelQuotaRx().IsMatch(text))
        {
            var rd = RetryDelayRx().Match(body);
            var span = text.Contains("PerDay", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(1)
                : rd.Success && int.TryParse(rd.Groups[1].Value, out var rs) ? TimeSpan.FromSeconds(rs + 2)
                : told ?? TimeSpan.FromMinutes(2);
            var free = text.Contains("free_tier", StringComparison.OrdinalIgnoreCase) ? " бесплатного уровня" : "";
            return new(FailKind.Limit, span, false, $"лимит модели{free} на ключе");
        }
        if (status == 402 || NoMoneyRx().IsMatch(text))
            return new(FailKind.NoMoney, told ?? TimeSpan.FromMinutes(30), true, "нет денег на счету");
        if (status is 401 or 403 && !LimitRx().IsMatch(text))
            return new(FailKind.BadKey, TimeSpan.FromMinutes(10), true, "ключ не принят");
        if (status == 404 || NoModelRx().IsMatch(text))
            return new(FailKind.NoModel, TimeSpan.FromMinutes(30), false, "такой модели на сервере сейчас нет");
        if (status is 429 or 502 or 503 or 529 && LimitRx().IsMatch(text))
            // у ключа (Жминя, DeepSeek) лимит — на весь ключ; у AG Tools — на модель: другие модели живут на других аккаунтах
            return new(FailKind.Limit, told ?? TimeSpan.FromMinutes(keyBased ? 10 : 5), keyBased, cd.Success ? $"лимит, остывает {Human(told!.Value)}" : "лимит");
        if (status is 429)
            return new(FailKind.Limit, told ?? TimeSpan.FromMinutes(2), keyBased, "слишком много запросов");
        if (status is 500 or 502 or 503 or 504 or 529 || BusyRx().IsMatch(text))
            return new(FailKind.Busy, told ?? TimeSpan.FromSeconds(45), false, "сервер перегружен");
        return new(FailKind.Other, TimeSpan.Zero, false, Short(text, 160));
    }

    /// <summary>Все строки JSON-ответа одной строкой (без \u-экранирования); не JSON — как есть.</summary>
    public static string Flatten(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is null) return body;
            var sb = new StringBuilder();
            void Walk(JsonNode? n)
            {
                switch (n)
                {
                    case JsonObject o: foreach (var (_, v) in o) Walk(v); break;
                    case JsonArray a: foreach (var v in a) Walk(v); break;
                    case JsonValue v when v.TryGetValue<string>(out var s): sb.Append(s).Append(' '); break;
                    case JsonValue v: sb.Append(v.ToJsonString()).Append(' '); break;
                }
            }
            Walk(node);
            return sb.ToString().Trim();
        }
        catch (JsonException) { return body; }
    }

    // ---------------------------------------------------------------- соседние модели (AG Tools)

    private static readonly Dictionary<string, (DateTime At, List<string> Models)> ModelCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Живой список моделей профиля с сервера (/models), с фильтром профиля. Кэш 3 минуты; не ответил — прежний.</summary>
    public static async Task<List<string>> ModelsAsync(KlavaConfig cfg, BrainProfile p, CancellationToken ct)
    {
        lock (ModelCache)
            if (ModelCache.TryGetValue(p.Id, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(3)) return c.Models;
        List<string> list;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            list = await AutoSetup.ModelsAsync(cfg.ApiUrl(p), ApiKeys.Resolve(p.Key), p.Proxy ?? "auto", cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException && !ct.IsCancellationRequested) { list = []; }
        Regex? inc = null, exc = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(p.ModelInclude)) inc = new Regex(p.ModelInclude, RegexOptions.IgnoreCase);
            if (!string.IsNullOrWhiteSpace(p.ModelExclude)) exc = new Regex(p.ModelExclude, RegexOptions.IgnoreCase);
        }
        catch (ArgumentException) { }
        list = list.Where(m => (inc is null || inc.IsMatch(m)) && (exc is null || !exc.IsMatch(m))).ToList();
        lock (ModelCache)
        {
            if (list.Count == 0 && ModelCache.TryGetValue(p.Id, out var old)) return old.Models;
            ModelCache[p.Id] = (DateTime.UtcNow, list);
        }
        return list;
    }

    /// <summary>Покрытие модели квотой AG Tools: на скольких аккаунтах она жива и сколько процентов в сумме.</summary>
    private sealed record Coverage(int Live, int Total, int Score);

    /// <summary>
    /// Квоты аккаунтов AG Tools (~/.antigravity_tools/accounts/*.json, блок quota.models: name + percentage).
    /// Имя квоты бывает длиннее id модели (claude-opus-4-6-thinking ↔ claude-opus-4-6, gemini-4-argon-high ↔ gemini-4-argon).
    /// Кэш 60 с. Файлов нет (не AG) — пусто: тогда пересадка только по живому списку.
    /// </summary>
    private static Dictionary<string, Coverage> AgCoverage(IEnumerable<string> models)
    {
        lock (CoverageCache)
            if (DateTime.UtcNow - _coverageAt < TimeSpan.FromSeconds(60) && models.All(CoverageCache.ContainsKey))
                return new Dictionary<string, Coverage>(CoverageCache, StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".antigravity_tools", "accounts");
        var accounts = new List<Dictionary<string, int>>();
        if (Directory.Exists(dir))
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var n = JsonNode.Parse(File.ReadAllText(file));
                    if (n is null || n["disabled"]?.GetValue<bool>() == true || n["proxy_disabled"]?.GetValue<bool>() == true
                        || n["quota"]?["is_forbidden"]?.GetValue<bool>() == true) continue;
                    var acc = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    if (n["quota"]?["models"] is JsonArray ms)
                        foreach (var m in ms)
                            if (m?["name"]?.GetValue<string>() is { Length: > 0 } name)
                                acc[name] = m["percentage"] is JsonValue pv && pv.TryGetValue<int>(out var pct) ? pct
                                    : m["percentage"] is JsonValue pd && pd.TryGetValue<double>(out var pctd) ? (int)pctd : 0;
                    accounts.Add(acc);
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or UnauthorizedAccessException) { }
            }
        var result = new Dictionary<string, Coverage>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            int live = 0, total = 0, score = 0;
            foreach (var acc in accounts)
            {
                var hits = acc.Where(q => q.Key.Equals(model, StringComparison.OrdinalIgnoreCase)
                                          || q.Key.StartsWith(model + "-", StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 0) continue;
                total++;
                var best = hits.Max(h => h.Value);
                if (best > 0) live++;
                score += best;
            }
            result[model] = new Coverage(live, total, score);
        }
        lock (CoverageCache)
        {
            foreach (var (k, v) in result) CoverageCache[k] = v;
            _coverageAt = DateTime.UtcNow;
        }
        return result;
    }

    private static readonly Dictionary<string, Coverage> CoverageCache = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _coverageAt = DateTime.MinValue;

    /// <summary>
    /// Предпочтения по семействам — только чтобы разбить ничью. Незнакомые новые семейства (у AG они появляются сами:
    /// hydrogen, carbon…) не выкидываются, а идут следом по покрытию квотой.
    /// </summary>
    private static int Tier(string model, bool task)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("thinking") || m.EndsWith("-xhigh") || m.Contains("tiered")) return task ? 6 : 9;   // думает долго — в живом разговоре это тишина
        string[] chat = ["claude-sonnet", "gpt-oss", "claude-opus", "flash", "gemini-4", "gemini"];
        string[] work = ["claude-opus", "claude-sonnet", "gemini-4", "gpt-oss", "flash", "gemini"];
        var order = task ? work : chat;
        for (var i = 0; i < order.Length; i++) if (m.Contains(order[i])) return i;
        return order.Length;
    }

    /// <summary>
    /// Соседняя модель того же сервера, когда текущая отдыхает или пропала: живая (есть в /models), не на отдыхе,
    /// по покрытию квотой — сначала те, что есть на двух и больше аккаунтах (одинокую модель circuit breaker AG запирает
    /// вместе со всем аккаунтом), с картинкой — только если у профиля есть глаза. null — пересаживаться некуда.
    /// </summary>
    public static async Task<string?> SiblingAsync(KlavaConfig cfg, BrainProfile p, string current, bool task, IReadOnlyCollection<string> tried, CancellationToken ct)
    {
        if (!p.ModelsFromServer || p.IsLocal) return null;
        var models = await ModelsAsync(cfg, p, ct).ConfigureAwait(false);
        if (models.Count == 0) return null;
        var cov = AgCoverage(models);
        var pick = models
            .Where(m => !m.Equals(current, StringComparison.OrdinalIgnoreCase) && !tried.Contains(m, StringComparer.OrdinalIgnoreCase))
            .Where(m => CooledFor(p, m) is null)
            .Select(m => (Model: m, Cov: cov.TryGetValue(m, out var c) ? c : null))
            .Where(x => x.Cov is null || x.Cov.Total == 0 || x.Cov.Live > 0)   // известная квота на нуле везде — мимо
            .OrderBy(x => x.Cov is { Live: >= 2 } ? 0 : x.Cov is null || x.Cov.Total == 0 ? 1 : 2)
            .ThenBy(x => Tier(x.Model, task))
            .ThenByDescending(x => x.Cov?.Score ?? 0)
            .Select(x => x.Model)
            .FirstOrDefault();
        return pick;
    }

    // ---------------------------------------------------------------- упавший прокси

    private static readonly Dictionary<string, DateTime> Launched = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Отказ «подключение отвергнуто» — на адресе никто не слушает (прокси или сервер не запущен).</summary>
    public static bool Refused(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
            if (e is SocketException { SocketErrorCode: SocketError.ConnectionRefused }) return true;
        return ex.Message.Contains("отверг", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("refused", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Открыт ли порт адреса (без блокировки потока, 600 мс).</summary>
    public static async Task<bool> PortOpenAsync(string url, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        using var tcp = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(600);
        try { await tcp.ConnectAsync(u.Host, u.Port, cts.Token).ConfigureAwait(false); return true; }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !ct.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Прокси профиля (Antigravity Tools) лёг посреди разговора — поднять и дождаться порта (до 30 с). Повторный запуск
    /// не чаще раза в 90 с: внутри этого окна только ждём порт. true — порт открыт.
    /// </summary>
    public static async Task<bool> RelaunchAsync(KlavaConfig cfg, BrainProfile p, CancellationToken ct)
    {
        var url = cfg.ApiUrl(p);
        if (await PortOpenAsync(url, ct).ConfigureAwait(false)) return true;
        if (string.IsNullOrWhiteSpace(p.Launch)) return false;
        var exe = Environment.ExpandEnvironmentVariables(p.Launch);
        if (!File.Exists(exe)) { Log.Write($"сторож мозгов: «{p.Name}» не отвечает, и нет {exe}"); return false; }
        bool start;
        lock (Launched)
        {
            start = !Launched.TryGetValue(exe, out var at) || DateTime.UtcNow - at > TimeSpan.FromSeconds(90);
            if (start) Launched[exe] = DateTime.UtcNow;
        }
        if (start)
        {
            Log.Write($"сторож мозгов: «{p.Name}» не слушает {url} — поднимаю {Path.GetFileNameWithoutExtension(exe)}");
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception ex) { Log.Write($"сторож мозгов: не запустился {exe}: {ex.Message}"); return false; }
        }
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(p.IsLocalServer ? 150 : 30))
        {
            await Task.Delay(700, ct).ConfigureAwait(false);
            if (await PortOpenAsync(url, ct).ConfigureAwait(false))
            {
                Log.Write($"сторож мозгов: «{p.Name}» снова слушает ({sw.ElapsedMilliseconds} мс)");
                await Task.Delay(800, ct).ConfigureAwait(false);   // порт открылся раньше, чем прокси дочитал аккаунты
                return true;
            }
        }
        Log.Write($"сторож мозгов: «{p.Name}» не открыл порт за 30 с");
        return false;
    }

    // ---------------------------------------------------------------- мелочи

    /// <summary>
    /// Что она скажет вслух, когда отказали все: коротко, по каждому мозгу — почему. Вместо «мозг не ответил».
    /// «Antigravity — AG Tools (…)» → «Антигравити на лимите, Дипсик без денег, локалка не запущена».
    /// </summary>
    public static string SpokenWhy(BrainsDownException down)
    {
        static string Who(string name)
        {
            var n = name.ToLowerInvariant();
            return n.Contains("antigravity") || n.Contains("ag tools") ? "Антигравити"
                : n.Contains("дипсик") || n.Contains("deepseek") ? "Дипсик"
                : n.Contains("жмин") || n.Contains("gemini") ? "Жминя"
                : n.Contains("codex") || n.Contains("chatgpt") ? "Чат-ГПТ"
                : n.Contains("claude") ? "Клод"
                : n.Contains("локал") || n.Contains("qwen") ? "локалка"
                : name.Split(" — ", 2)[0].Split(" (", 2)[0];
        }
        static string What(string why)
        {
            var w = why.ToLowerInvariant();
            return w.Contains("денег") ? "без денег"
                : w.Contains("лимит") || w.Contains("отдыхает") || w.Contains("перегруж") ? "на лимите"
                : w.Contains("не слушает") || w.Contains("не запущен") ? "лежит"
                : w.Contains("ключ") ? "без ключа"
                : w.Contains("молчит") || w.Contains("не ответил") || w.Contains("таймаут") || w.Contains("замолчал") ? "молчит"
                : w.Contains("модели") ? "без нужной модели"
                : w.Contains("промолчал") ? "промолчал"
                : "сбоит";
        }
        var parts = down.Reasons.GroupBy(r => Who(r.Brain)).Select(g => $"{g.Key} {What(g.Last().Why)}").Take(4).ToList();
        return parts.Count == 0 ? "Бля, ни один мозг сейчас не отвечает. Дай минутку и повтори."
            : $"Бля, все мозги в отказе: {string.Join(", ", parts)}. Дай минутку и повтори.";
    }

    public static string Human(TimeSpan t) => t.TotalSeconds < 90 ? $"{Math.Max(1, (int)t.TotalSeconds)} с"
        : t.TotalMinutes < 90 ? $"{(int)Math.Round(t.TotalMinutes)} мин" : $"{t.TotalHours:F1} ч";

    private static string Short(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}

/// <summary>Мозг на отдыхе (лимит, нет денег, нет модели) — не ошибка сети, а повод сразу идти к следующему.</summary>
internal sealed class BrainRestingException(string message) : Exception(message);

/// <summary>Отказали все мозги цепочки: по каждому — короткая причина (для пузыря и для того, что она скажет вслух).</summary>
internal sealed class BrainsDownException(IReadOnlyList<(string Brain, string Why)> reasons)
    : Exception("все мозги отказали: " + string.Join("; ", reasons.Select(r => $"{r.Brain} — {r.Why}")))
{
    public IReadOnlyList<(string Brain, string Why)> Reasons { get; } = reasons;
}
