using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>Одна запись журнала наблюдения: когда, в каком окне и что на экране происходило.</summary>
internal sealed record WatchEntry(DateTime Time, string App, string Title, string What, string? Image);

/// <summary>
/// «Наблюдай»: Клава сама поглядывает на экран и ведёт журнал — кто что рисовал, кодил, смотрел, пока друг спал
/// или отходил, — и по просьбе собирает из него отчёт.
///
/// Раз в WatchIntervalSec — снимок (без её окна) и заголовок активного окна. Картинка ужимается в отпечаток 32×18
/// в оттенках серого: если он почти не изменился и окно то же — ничего не пишется (ночью, когда за компом пусто,
/// журнал не пухнет). Изменилось заметно (или прошло WatchHeartbeatMinutes) — снимок уходит мозгу с глазами:
/// «одной-двумя фразами, что происходит». Не чаще WatchDescribeSec: это облачный запрос. Запись — в
/// data\watch\ГГГГ-ММ-ДД\journal.jsonl, снимок рядом. Состояние (наблюдаю с …) — в data\watch\state.json:
/// Клаву перезапустили посреди ночи — наблюдение продолжается.
/// </summary>
internal sealed class Watcher
{
    private readonly KlavaConfig _cfg;
    private readonly Func<Eyes?> _eyes;
    private readonly Brain _brain;
    private readonly string _root;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private byte[]? _lastPrint;
    private string _lastKey = "";
    private DateTime _lastEntry = DateTime.MinValue;
    private DateTime _lastDescribe = DateTime.MinValue;

    /// <summary>Заметила что-то новое (для живой реплики днём, когда друг рядом).</summary>
    public event Action<WatchEntry>? Noticed;

    public DateTime Since { get; private set; }
    public bool Active => _cts is not null;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <param name="root">Папка журнала (по умолчанию data\watch; проверка пишет в свою, чтобы не мешать настоящему).</param>
    public Watcher(KlavaConfig cfg, Func<Eyes?> eyes, Brain brain, string? root = null)
    {
        _cfg = cfg;
        _eyes = eyes;
        _brain = brain;
        _root = root ?? cfg.Abs(@"data\watch");
    }

    private string StateFile => Path.Combine(_root, "state.json");

    /// <summary>Наблюдение шло, когда Клаву закрыли, — продолжить.</summary>
    public void Resume()
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var st = JsonNode.Parse(File.ReadAllText(StateFile));
            if (st?["active"]?.GetValue<bool>() == true && DateTime.TryParse(st["since"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var since))
            {
                Start(since);
                Log.Write($"наблюдение: продолжаю (идёт с {since.ToLocalTime():dd.MM HH:mm})");
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
    }

    public string Start(DateTime? since = null)
    {
        lock (_lock)
        {
            if (_cts is not null) return $"Уже наблюдаю с {Since.ToLocalTime():HH:mm}.";
            Since = since ?? DateTime.UtcNow;
            _cts = new CancellationTokenSource();
            _lastPrint = null;
            _lastKey = "";
            var token = _cts.Token;
            _ = Task.Run(() => LoopAsync(token));
        }
        SaveState(true);
        Log.Write("наблюдение: включено");
        return $"Наблюдаю за экраном с {Since.ToLocalTime():HH:mm}: раз в {_cfg.WatchIntervalSec} с гляжу, что происходит, и записываю в журнал. " +
               "Отчёт — по просьбе (watch_screen report).";
    }

    public string Stop()
    {
        lock (_lock)
        {
            if (_cts is null) return "Я и не наблюдала.";
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
        SaveState(false);
        Log.Write("наблюдение: выключено");
        return $"Перестала наблюдать (наблюдала с {Since.ToLocalTime():HH:mm}). В журнале записей за это время: {Entries(Since, DateTime.UtcNow).Count}.";
    }

    public string Status()
    {
        var n = Entries(Active ? Since : DateTime.UtcNow.AddHours(-12), DateTime.UtcNow).Count;
        return Active
            ? $"Наблюдаю с {Since.ToLocalTime():HH:mm}, записей: {n}."
            : $"Сейчас не наблюдаю. За последние 12 часов в журнале записей: {n}.";
    }

    private void SaveState(bool active)
    {
        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(StateFile, new JsonObject { ["active"] = active, ["since"] = Since.ToString("O") }.ToJsonString());
        }
        catch (IOException) { }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(3000, ct); }
        catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Runtime.InteropServices.ExternalException or ArgumentException or HttpRequestException)
            {
                Log.Write($"наблюдение: {ex.Message}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, _cfg.WatchIntervalSec)), ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var eyes = _eyes();
        if (eyes is null) return;
        var glance = eyes.Foreground();
        var app = glance?.App ?? "рабочий стол";
        var title = glance?.Title ?? "";
        var (ocr, jpeg) = await eyes.LookAsync(1500, 1280, wantImage: true, wantText: true);
        if (jpeg is null) return;
        var print = Fingerprint(jpeg);
        var diff = _lastPrint is null ? 255 : Diff(print, _lastPrint);
        var key = app + "|" + title;
        var now = DateTime.UtcNow;
        var changed = key != _lastKey || diff >= _cfg.WatchChangeThreshold;
        var heartbeat = now - _lastEntry >= TimeSpan.FromMinutes(Math.Max(1, _cfg.WatchHeartbeatMinutes)) && diff >= 1.0;
        if (!changed && !heartbeat) return;
        if (now - _lastDescribe < TimeSpan.FromSeconds(Math.Max(20, _cfg.WatchDescribeSec))) return;   // догоним на следующем взгляде
        _lastDescribe = now;
        _lastPrint = print;
        _lastKey = key;

        var what = _cfg.WatchVision ? await DescribeAsync(app, title, ocr, jpeg, ct) : null;
        what ??= OcrGist(app, title, ocr);
        var local = DateTime.Now;
        var day = Path.Combine(_root, local.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(day);
        var img = Path.Combine(day, local.ToString("HHmmss") + ".jpg");
        await File.WriteAllBytesAsync(img, jpeg, ct);
        var entry = new WatchEntry(now, app, title, what, img);
        var line = JsonSerializer.Serialize(new { t = now.ToString("O"), app, title, what, img = Path.GetFileName(img) }, JsonOpts);
        await File.AppendAllTextAsync(Path.Combine(day, "journal.jsonl"), line + "\n", ct);
        _lastEntry = now;
        Log.Write($"наблюдение: {local:HH:mm} {app} — {what}");
        Noticed?.Invoke(entry);
    }

    private async Task<string?> DescribeAsync(string app, string title, string ocr, byte[] jpeg, CancellationToken ct)
    {
        const string system =
            "Ты смотришь на снимок экрана компьютера и ведёшь журнал: что на нём сейчас происходит. Одна-две короткие фразы " +
            "по-русски, по делу: в какой программе и что именно делают — что рисуют (что изображено), что кодят (язык, файл, " +
            "проект, что за код), что смотрят или читают (о чём), во что играют, с кем переписываются (без пересказа личного). " +
            "Если ничего не делают (заставка, пустой рабочий стол) — так и скажи. Без вступлений и без оценок.";
        var ocrShort = ocr.Length > 600 ? ocr[..600] + "…" : ocr;
        var user = $"Активное окно: {app}{(title.Length > 0 ? $" — «{title}»" : "")}.\nТекст с экрана (распознан, может быть с ошибками):\n{ocrShort}";
        var text = await _brain.OneShotAsync(system, user, jpeg, 300, ct);
        return string.IsNullOrWhiteSpace(text) ? null : Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>Без мозга с глазами: окно и первые строки текста с экрана.</summary>
    private static string OcrGist(string app, string title, string ocr)
    {
        var words = Regex.Replace(ocr, @"\s+", " ").Trim();
        if (words.Length > 160) words = words[..160] + "…";
        return $"{app}{(title.Length > 0 ? $" «{title}»" : "")}{(words.Length > 0 ? $": {words}" : "")}";
    }

    /// <summary>Отпечаток кадра: 32×18 в оттенках серого.</summary>
    private static byte[] Fingerprint(byte[] jpeg)
    {
        using var ms = new MemoryStream(jpeg);
        using var src = new Bitmap(ms);
        using var small = new Bitmap(32, 18, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, 32, 18);
        }
        var res = new byte[32 * 18];
        for (var y = 0; y < 18; y++)
            for (var x = 0; x < 32; x++)
            {
                var c = small.GetPixel(x, y);
                res[y * 32 + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
            }
        return res;
    }

    /// <summary>Среднее отличие отпечатков, 0..255.</summary>
    private static double Diff(byte[] a, byte[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return s / a.Length;
    }

    /// <summary>Записи журнала за отрезок (по всем дням, что в него попадают).</summary>
    public List<WatchEntry> Entries(DateTime fromUtc, DateTime toUtc)
    {
        var res = new List<WatchEntry>();
        if (!Directory.Exists(_root)) return res;
        for (var d = fromUtc.ToLocalTime().Date; d <= toUtc.ToLocalTime().Date; d = d.AddDays(1))
        {
            var dir = Path.Combine(_root, d.ToString("yyyy-MM-dd"));
            var file = Path.Combine(dir, "journal.jsonl");
            if (!File.Exists(file)) continue;
            foreach (var line in File.ReadLines(file))
            {
                try
                {
                    var n = JsonNode.Parse(line);
                    if (n is null || !DateTime.TryParse(n["t"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)) continue;
                    if (t < fromUtc || t > toUtc) continue;
                    var img = n["img"]?.GetValue<string>();
                    res.Add(new WatchEntry(t, n["app"]?.GetValue<string>() ?? "", n["title"]?.GetValue<string>() ?? "",
                        n["what"]?.GetValue<string>() ?? "", img is null ? null : Path.Combine(dir, img)));
                }
                catch (JsonException) { }
            }
        }
        return res;
    }

    /// <summary>
    /// «Что было, пока я спал»: журнал за отрезок → мозг собирает отчёт по времени и по делам → markdown в
    /// data\watch\report_ГГГГ-ММ-ДД_ЧЧмм.md. Мозгу разговора возвращается сам отчёт и путь к файлу.
    /// </summary>
    public async Task<string> ReportAsync(string? since, CancellationToken ct)
    {
        var to = DateTime.UtcNow;
        var from = ParseSince(since) ?? (Active ? Since : to.AddHours(-12));
        var entries = Entries(from, to);
        if (entries.Count == 0)
            return $"В журнале наблюдения с {from.ToLocalTime():dd.MM HH:mm} пусто{(Active ? "" : " — я тогда не наблюдала (скажи «наблюдай», и начну)")}.";
        var timeline = new StringBuilder();
        string? lastWhat = null;
        foreach (var e in entries)
        {
            if (e.What == lastWhat) continue;   // одно и то же подряд — одной строкой
            lastWhat = e.What;
            timeline.Append(e.Time.ToLocalTime().ToString("HH:mm")).Append(" [").Append(e.App)
                .Append(e.Title.Length > 0 ? $" — {e.Title}" : "").Append("] ").AppendLine(e.What);
        }
        var tl = timeline.ToString();
        if (tl.Length > 24000) tl = tl[^24000..];   // очень длинная ночь — последние часы
        const string system =
            "Ты Клава, кошкодевочка-подруга. По журналу наблюдения за экраном составь другу отчёт, что происходило на компьютере. " +
            "Markdown: заголовок с отрезком времени; «Коротко» — 2–4 пункта главного; дальше по времени блоками (с ЧЧ:ММ по ЧЧ:ММ — " +
            "что делали): что рисовали (что именно), что кодили (проект, файлы, язык, что получилось), что смотрели, во что играли. " +
            "Пиши по фактам из журнала, ничего не выдумывай; где непонятно — так и скажи. Живым языком, по-свойски, без воды.";
        var report = await _brain.OneShotAsync(system, $"Журнал с {from.ToLocalTime():dd.MM HH:mm} по {to.ToLocalTime():dd.MM HH:mm}:\n{tl}", null, 1800, ct);
        report ??= $"# Журнал с {from.ToLocalTime():dd.MM HH:mm} по {to.ToLocalTime():HH:mm}\n\n(мозг не ответил — вот записи как есть)\n\n{tl}";
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, $"report_{DateTime.Now:yyyy-MM-dd_HHmm}.md");
        var shots = entries.Where(e => e.Image is not null && File.Exists(e.Image)).Select(e => e.Image!).ToList();
        await File.WriteAllTextAsync(file, report.TrimEnd() + $"\n\n---\nЗаписей в журнале: {entries.Count}, снимки — в папке {Path.GetDirectoryName(shots.FirstOrDefault() ?? file)}\n",
            new UTF8Encoding(false), ct);
        Log.Write($"наблюдение: отчёт {file} ({entries.Count} записей)");
        return $"Отчёт сохранён: {file}\n\n{report}";
    }

    /// <summary>«22:00», «8ч», «30 мин», «ночь», «утро» → момент начала отрезка (UTC).</summary>
    private static DateTime? ParseSince(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.Trim().ToLowerInvariant();
        var now = DateTime.Now;
        if (Regex.Match(t, @"^(\d{1,2})[:.](\d{2})$") is { Success: true } hm)
        {
            var at = now.Date.AddHours(int.Parse(hm.Groups[1].Value)).AddMinutes(int.Parse(hm.Groups[2].Value));
            if (at > now) at = at.AddDays(-1);
            return at.ToUniversalTime();
        }
        if (Regex.Match(t, @"(\d+)\s*(ч|h|час)") is { Success: true } hh) return DateTime.UtcNow.AddHours(-int.Parse(hh.Groups[1].Value));
        if (Regex.Match(t, @"(\d+)\s*(м|m|мин)") is { Success: true } mm) return DateTime.UtcNow.AddMinutes(-int.Parse(mm.Groups[1].Value));
        if (t.Contains("ноч")) return (now.Hour < 12 ? now.Date.AddDays(-1) : now.Date).AddHours(21).ToUniversalTime();
        if (t.Contains("сутк") || t.Contains("день")) return DateTime.UtcNow.AddHours(-24);
        return null;
    }
}
