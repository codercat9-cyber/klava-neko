using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>Что банк даёт к реплике: хроника (на вопрос про прошлое), воспоминания к слову (с датой), созревший план.</summary>
internal sealed record BankContext(string? Timeline, List<Recall> Items, string? Plan);

/// <summary>
/// Банк памяти Клавы (друг 28.09: «растяпа не помнит, о чём говорили вчера — полётные журналы пишутся, а менеджера,
/// который раскладывает воспоминания по полочкам и достаёт, нету»; 01.10: «должна по датам и по часам всё помнить как
/// часовой»). Журнал — сырой поток реплик; здесь он раскладывается по времени:
///  - ЭПИЗОДЫ: чистая хронология дня (FlightJournal.Transcript) режется по паузам (больше <see cref="GapMinutes"/> мин)
///    и по длине, каждый кусок мозг разовым запросом сжимает в 1–3 эпизода: день, время с–по, кто был, полка, заголовок,
///    пересказ с живыми деталями, ключевые слова;
///  - ПЛАНЫ И ОБЕЩАНИЯ: кто что собирался сделать и когда — чтобы потом спросить «ну как, сходил к врачу?»; её
///    собственные обещания («передай им, чтобы плиту помыли») возвращаются ей самой, пока не выполнит (раньше такие
///    планы записывались и лежали мёртвым грузом: напоминалось только про планы того, кто сейчас говорит);
///  - ЗАМЕТКИ: «запомни, что…» (инструмент memory) и внутренние шутки компании;
///  - факты для графа памяти (MemoryGraph) — из того же ответа мозга; люди и итоги дней — FriendMemory.
/// Достаёт по времени: вопрос про прошлое («помнишь», «вчера вечером», «в три часа», «час назад», «в пятницу»,
/// «28 сентября», «пока я отходил») — хроника этого отрезка: итог дня, эпизоды по порядку (на длинный день — оглавление
/// по часам), на узкий отрезок — сами реплики журнала; просто к слову — одно воспоминание с датой. Поиск свой: основы
/// слов и буквенные триграммы с весом редкости. Хранится data/memory/bank.json (запись — SafeFile, с копиями).
/// </summary>
internal sealed partial class MemoryBank
{
    public static MemoryBank? Current { get; set; }

    public const int GapMinutes = 12, MaxChunk = 40, MaxChunkChars = 7000, LookbackDays = 14, EveryTurns = 10, SchemaNow = 2;

    internal sealed class Episode
    {
        public string Id { get; set; } = "";
        public string Day { get; set; } = "";
        public string From { get; set; } = "";
        public string To { get; set; } = "";
        public List<string> People { get; set; } = [];
        public string Shelf { get; set; } = "";
        public string Title { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<string> Keys { get; set; } = [];
    }

    internal sealed class Plan
    {
        public string Id { get; set; } = "";
        public string Who { get; set; } = "";
        public string What { get; set; } = "";
        public string When { get; set; } = "";
        public DateTime Made { get; set; }
        public bool Done { get; set; }
        public DateTime? Reminded { get; set; }
        /// <summary>Сколько раз ей напоминали про её собственное обещание (после трёх — отстаём).</summary>
        public int Tries { get; set; }
    }

    internal sealed class Note
    {
        public string Id { get; set; } = "";
        public string Who { get; set; } = "";
        public string Text { get; set; } = "";
        public string Kind { get; set; } = "заметка";
        public DateTime Made { get; set; }
    }

    internal sealed class Store
    {
        public int Schema { get; set; }
        public List<Episode> Episodes { get; set; } = [];
        public List<Plan> Plans { get; set; } = [];
        public List<Note> Notes { get; set; } = [];
        /// <summary>День → время последней реплики, уже разложенной по эпизодам.</summary>
        public Dictionary<string, DateTime> Filed { get; set; } = new(StringComparer.Ordinal);
        /// <summary>Дни, которые надо переложить заново целиком (журнал за них собран из кусков или раскладка была грязной).</summary>
        public List<string> Rebuild { get; set; } = [];
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    private readonly string _file;
    private readonly Brain? _brain;
    private readonly FlightJournal _journal;
    private readonly FriendMemory _friends;
    private readonly object _lock = new();
    private readonly object _disk = new();
    private readonly Store _store;
    private readonly Dictionary<string, DateTime> _told = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _fails = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Filing> _rebuilt = new(StringComparer.Ordinal);
    private int _busy;
    private int _turns;
    private List<(object Item, Dictionary<int, float> Vec, float Norm)>? _index;

    /// <summary>Подмена мозга для проверок: (system, user) → ответ.</summary>
    internal Func<string, string, Task<string?>>? AskOverride { get; set; }

    public MemoryBank(string dir, Brain? brain, FlightJournal journal, FriendMemory friends)
    {
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "bank.json");
        _brain = brain;
        _journal = journal;
        _friends = friends;
        _store = SafeFile.Load(_file, t => JsonSerializer.Deserialize<Store>(t, Json)) ?? new Store { Schema = File.Exists(_file) ? 0 : SchemaNow };
        if (_store.Schema < SchemaNow) Upgrade();
    }

    private void Save()
    {
        try
        {
            string text;
            lock (_lock) text = JsonSerializer.Serialize(_store, Json);
            lock (_disk) SafeFile.Write(_file, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("банк памяти: не записался", ex); }
    }

    /// <summary>
    /// Банк до 01.10 → нынешний: копия старого рядом (backups\bank.v1.json), имена людей в одном виде, мусорные эпизоды
    /// (ответ мозга вместо пересказа: текст отказа, сырой JSON) — вон, и все дни с журналом — в очередь на перекладку
    /// начисто: журнал за них теперь полный и без повторов, раскладка по нему выйдет ровнее и вернёт графу факты.
    /// Старые эпизоды дня служат, пока день не переложен.
    /// </summary>
    private void Upgrade()
    {
        try
        {
            var keep = Path.Combine(Path.GetDirectoryName(_file)!, "backups", "bank.v1.json");
            if (File.Exists(_file) && !File.Exists(keep))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(keep)!);
                File.Copy(_file, keep);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("банк памяти: копия перед обновлением", ex); }
        int junk;
        lock (_lock)
        {
            junk = _store.Episodes.RemoveAll(e => Junk(e.Summary));
            foreach (var e in _store.Episodes) e.People = CleanPeople(e.People);
            foreach (var p in _store.Plans) p.Who = People.Canon(p.Who) is { Length: > 0 } w ? w : p.Who;
            _store.Rebuild = _journal.Days().Select(d => d.ToString("yyyy-MM-dd")).OrderByDescending(d => d, StringComparer.Ordinal).ToList();
            _store.Schema = SchemaNow;
        }
        Save();
        Log.Write($"банк памяти: обновлён — мусорных эпизодов убрано {junk}, дней на перекладку начисто {_store.Rebuild.Count}");
    }

    /// <summary>Не пересказ, а ответ мозга как есть: отказ провайдера, сырой JSON.</summary>
    internal static bool Junk(string summary)
    {
        var s = summary.TrimStart();
        return s.StartsWith('`') || s.StartsWith('{') || s.Contains("could not be submitted", StringComparison.OrdinalIgnoreCase)
               || s.Contains("Prohibited Use policy", StringComparison.OrdinalIgnoreCase) || s.Contains("\"episodes\"", StringComparison.Ordinal);
    }

    private static List<string> CleanPeople(IEnumerable<string> names) =>
        People.CanonList(names).Where(n => n != "Клава").ToList();

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static DateTime DayOf(string key) => DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Начало и конец эпизода моментами времени.</summary>
    private static (DateTime From, DateTime To) Times(Episode e)
    {
        var day = DayOf(e.Day);
        var from = TimeSpan.TryParseExact(e.From, @"hh\:mm", CultureInfo.InvariantCulture, out var f) ? f : TimeSpan.Zero;
        var to = TimeSpan.TryParseExact(e.To, @"hh\:mm", CultureInfo.InvariantCulture, out var t) ? t : from;
        return (day + from, day + (to < from ? from : to));
    }

    // ---------------- раскладка журнала по полочкам ----------------

    /// <summary>Ход записан: каждые <see cref="EveryTurns"/> ходов — разложить закрытые куски журнала в фоне.</summary>
    public void OnTurn()
    {
        if (Interlocked.Increment(ref _turns) % EveryTurns == 0) _ = ConsolidateAsync();
    }

    /// <summary>
    /// Куски хронологии: разрыв больше GapMinutes, MaxChunk реплик или MaxChunkChars знаков. Сегодняшний последний кусок
    /// ещё идёт — его раскладывать рано, пока не наступила пауза (или он не дорос до полного).
    /// </summary>
    internal static List<List<Utterance>> Chunks(List<Utterance> lines, bool today)
    {
        var chunks = new List<List<Utterance>>();
        var cur = new List<Utterance>();
        var chars = 0;
        foreach (var u in lines)
        {
            if (cur.Count > 0 && (u.Time - cur[^1].Time > TimeSpan.FromMinutes(GapMinutes) || cur.Count >= MaxChunk || chars >= MaxChunkChars))
            {
                chunks.Add(cur);
                cur = [];
                chars = 0;
            }
            cur.Add(u);
            chars += Math.Min(u.Text.Length, 260) + 20;
        }
        if (cur.Count > 0 && (!today || DateTime.Now - cur[^1].Time > TimeSpan.FromMinutes(GapMinutes) || cur.Count >= MaxChunk || chars >= MaxChunkChars))
            chunks.Add(cur);
        return chunks;
    }

    /// <summary>В куске есть что раскладывать: хотя бы две настоящие реплики (не подсказки системы).</summary>
    private static bool Worth(List<Utterance> chunk) => chunk.Count(u => !u.Hint && u.Text.Length >= 4) >= 2;

    /// <summary>
    /// Разложить всё неразложенное за LookbackDays дней (по куску за запрос к мозгу), потом — дни из очереди на
    /// перекладку начисто. Один проход за раз.
    /// </summary>
    public async Task ConsolidateAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            for (var d = LookbackDays; d >= 0; d--)
            {
                var day = DateTime.Today.AddDays(-d);
                var key = day.ToString("yyyy-MM-dd");
                DateTime filed;
                lock (_lock)
                {
                    if (_store.Rebuild.Contains(key)) continue;   // день переложится целиком ниже
                    filed = _store.Filed.TryGetValue(key, out var f) ? f : DateTime.MinValue;
                }
                var lines = _journal.Transcript(day).Where(u => u.Time > filed).ToList();
                if (lines.Count == 0) continue;
                foreach (var chunk in Chunks(lines, d == 0))
                {
                    if (Worth(chunk))
                    {
                        if (await FileOrFallbackAsync(key, day, chunk).ConfigureAwait(false) is not { } filing) return;   // мозг не ответил — позже
                        Apply(key, filing, plans: true, facts: true);
                    }
                    lock (_lock) _store.Filed[key] = chunk[^1].Time;
                    Save();
                }
            }
            await RebuildAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            Log.Error("банк памяти: раскладка журнала", ex);
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    /// <summary>
    /// Дни из очереди — заново целиком, от свежих к давним: вся хронология дня → эпизоды и факты; старые эпизоды дня
    /// заменяются разом, когда день готов (до того служат они). Планы и шутки при перекладке не трогаются — они уже есть.
    /// Мозг перестал отвечать — остановка; готовые куски дня помнятся до конца сеанса и второй раз не спрашиваются.
    /// </summary>
    private async Task RebuildAsync()
    {
        List<string> queue;
        lock (_lock) queue = [.. _store.Rebuild];
        foreach (var key in queue)
        {
            var day = DayOf(key);
            var today = day == DateTime.Today;
            var chunks = Chunks(_journal.Transcript(day), today);
            var filings = new List<Filing>();
            var worth = chunks.Count(Worth);
            Log.Write($"банк памяти: перекладываю {key} начисто — кусков {worth}");
            foreach (var chunk in chunks.Where(Worth))
            {
                if (filings.Count > 0 && filings.Count % 15 == 0) Log.Write($"банк памяти: перекладка {key} — {filings.Count}/{worth}");
                var id = $"{key} {chunk[0].Time:HH:mm:ss}";
                if (!_rebuilt.TryGetValue(id, out var filing))
                {
                    filing = await FileOrFallbackAsync(key, day, chunk).ConfigureAwait(false);
                    if (filing is null) { Log.Write($"банк памяти: перекладка {key} отложена — мозг не ответил"); return; }
                    _rebuilt[id] = filing;
                    await Task.Delay(700).ConfigureAwait(false);   // не забивать мозг, пока она болтает
                }
                filings.Add(filing);
            }
            lock (_lock)
            {
                _store.Episodes.RemoveAll(e => e.Day == key);
                _store.Rebuild.Remove(key);
                if (chunks.Count > 0) _store.Filed[key] = chunks[^1][^1].Time;
                else _store.Filed.Remove(key);
                _index = null;
            }
            foreach (var f in filings) Apply(key, f, plans: false, facts: false);
            MemoryGraph.Current?.ReplaceDay(key, filings.SelectMany(f => f.Facts).ToList());
            if (!today) _friends.ForgetDay(key);   // итог дня — заново, уже по полной раскладке
            foreach (var id in _rebuilt.Keys.Where(k => k.StartsWith(key, StringComparison.Ordinal)).ToList()) _rebuilt.Remove(id);
            Save();
            Log.Write($"банк памяти: {key} переложен начисто — кусков {filings.Count}, эпизодов {filings.Sum(f => f.Episodes.Count)}, фактов {filings.Sum(f => f.Facts.Count)}");
        }
    }

    /// <summary>Разложенный кусок: что мозг из него вынул.</summary>
    internal sealed class Filing
    {
        public List<Episode> Episodes { get; } = [];
        public List<Plan> Plans { get; } = [];
        public List<string> Jokes { get; } = [];
        public List<MemoryGraph.FactIn> Facts { get; } = [];
        public DateTime End { get; init; }
    }

    private const string FileSystem =
        "Ты — долгая память Клавы (кошкодевочка-компаньон на компьютере друга, болтает с компанией голосом). Разложи кусок журнала " +
        "её разговоров по полочкам. Верни ТОЛЬКО JSON без пояснений:\n" +
        "{\"episodes\":[{\"from\":\"ЧЧ:ММ\",\"to\":\"ЧЧ:ММ\",\"title\":\"3–6 слов\",\"summary\":\"2–4 предложения от третьего лица: кто был, " +
        "о чём говорили и что делали, чем кончилось; живые детали — имена, игры, числа, что кому понравилось\",\"people\":[\"имена участников\"]," +
        "\"shelf\":\"одно из: болтовня, игры, рисование, работа и код, планы, здоровье, отношения, еда, кино и музыка, шутки, учёба, другое\"," +
        "\"keys\":[\"5–8 ключевых слов\"]}],\n" +
        "\"plans\":[{\"who\":\"кто\",\"what\":\"что собирался или обещал сделать\",\"when\":\"yyyy-MM-dd или пусто\"}],\n" +
        "\"jokes\":[\"внутренняя шутка или прозвище компании, если родились\"],\n" +
        "\"facts\":[{\"subject\":\"кто/что\",\"predicate\":\"отношение или действие\",\"object\":\"кого/что\",\"fact\":\"одна фраза от третьего лица\"," +
        "\"entities\":[\"все имена, игры, вещи, места\"],\"emotion\":\"как это для Клавы: радость|нежность|смех|грусть|раздражение|злость|удивление|смущение|спокойствие\"}]}\n" +
        "Эпизодов 1–3 — по сменам темы, не по репликам; from и to — время первой и последней реплики эпизода из журнала. Имена людей " +
        "пиши ровно как в журнале. Строки «(подсказка Клаве: …)» — подсказки системы самой Клаве (наблюдение за экраном, " +
        "«поделись фактом»), а не слова людей. Планы — только настоящие намерения людей и обещания Клавы, не шутки. " +
        "Факты (до 6) — то, что стоит помнить долго: вкусы и привычки людей, события их жизни, отношения, договорённости, что " +
        "Клава сама пережила или полюбила; не пересказ реплик и не мелочи минуты. То, что Клава в этом куске «вспоминает» о " +
        "прошлом, фактом НЕ считай — она могла напутать; факты бери из слов людей и из того, что здесь произошло. Ничего не выдумывай.";

    private static bool LooksFiled(string reply) => reply.Contains('{') && reply.Contains("episodes", StringComparison.Ordinal);

    private Task<string?> AskAsync(string system, string user) =>
        AskOverride is { } ask ? ask(system, user)
        : _brain is null ? Task.FromResult<string?>(null)
        : _brain.OneShotAsync(system, user, null, 1500, CancellationToken.None, LooksFiled);

    /// <summary>
    /// Кусок → раскладка. Мозг не ответил по форме — null (попробуем позже); третий раз подряд на том же куске — кусок
    /// кладётся механически (кто был и первые слова), чтобы очередь не встала навсегда на одном месте.
    /// </summary>
    private async Task<Filing?> FileOrFallbackAsync(string key, DateTime day, List<Utterance> chunk)
    {
        var id = $"{key} {chunk[0].Time:HH:mm:ss}";
        if (await FileChunkAsync(key, day, chunk).ConfigureAwait(false) is { } filing)
        {
            _fails.Remove(id);
            return filing;
        }
        var n = _fails[id] = _fails.GetValueOrDefault(id) + 1;
        if (n < 3) return null;
        _fails.Remove(id);
        Log.Write($"банк памяти: {key} {chunk[0].Time:HH:mm} — мозг трижды не разложил, кладу как есть");
        return Mechanical(key, chunk);
    }

    private static List<string> Speakers(List<Utterance> chunk) =>
        chunk.Where(u => !u.Hint && !u.Klava && u.Who.Length > 0).Select(u => u.Who).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Раскладка без мозга: кто был и что говорили — словами из журнала, ничего от себя.</summary>
    internal static Filing Mechanical(string key, List<Utterance> chunk)
    {
        var people = Speakers(chunk);
        var said = chunk.Where(u => !u.Hint && !u.Klava).OrderByDescending(u => u.Text.Length).Take(4).OrderBy(u => u.Time)
            .Select(u => $"{u.Who}: «{Trim(u.Text, 90)}»");
        var f = new Filing { End = chunk[^1].Time };
        f.Episodes.Add(new Episode
        {
            Day = key, From = chunk[0].Time.ToString("HH:mm"), To = chunk[^1].Time.ToString("HH:mm"), People = people, Shelf = "болтовня",
            Title = "разговор", Summary = Trim($"Разговор ({chunk.Count} реплик). {string.Join(" ", said)}", 600),
        });
        return f;
    }

    private async Task<Filing?> FileChunkAsync(string key, DateTime day, List<Utterance> chunk)
    {
        var text = new StringBuilder();
        foreach (var u in chunk)
            text.AppendLine(u.Hint ? $"{u.Time:HH:mm} (подсказка Клаве: {Trim(u.Text, 120)})" : $"{u.Time:HH:mm} {u.Who}: {Trim(u.Text, u.Klava ? 220 : 260)}");
        var reply = await AskAsync(FileSystem, $"День: {day.ToString("d MMMM yyyy, dddd", Ru)}.\nЖурнал:\n{text}").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(reply)) return null;
        if (ParseLoose(reply) is not { } root)
        {
            Log.Write($"банк памяти: {key} {chunk[0].Time:HH:mm} — ответ мозга не разобрался ({Trim(reply.Replace('\n', ' '), 80)})");
            return null;
        }
        string from = chunk[0].Time.ToString("HH:mm"), to = chunk[^1].Time.ToString("HH:mm");
        string Clock(JsonNode? n, string fallback) =>
            Str(n) is { Length: 5 } s && TimeSpan.TryParseExact(s, @"hh\:mm", CultureInfo.InvariantCulture, out _)
            && string.CompareOrdinal(s, from) >= 0 && string.CompareOrdinal(s, to) <= 0 ? s : fallback;
        var people = Speakers(chunk);
        var filing = new Filing { End = chunk[^1].Time };
        if (root["episodes"] is JsonArray eps)
            foreach (var ep in eps.OfType<JsonObject>())
            {
                var summary = Str(ep["summary"]);
                if (summary.Length < 10 || Junk(summary)) continue;
                var title = Str(ep["title"]);
                var f = Clock(ep["from"], from);
                var t = Clock(ep["to"], to);
                filing.Episodes.Add(new Episode
                {
                    Day = key, From = f, To = string.CompareOrdinal(t, f) < 0 ? f : t,
                    People = CleanPeople((ep["people"] as JsonArray)?.Select(Str) ?? []) is { Count: > 0 } pp ? pp : people,
                    Shelf = Str(ep["shelf"]) is { Length: > 0 } sh ? sh : "болтовня",
                    Title = title.Length > 0 ? Trim(title, 60) : "разговор", Summary = Trim(summary, 600),
                    Keys = (ep["keys"] as JsonArray)?.Select(Str).Where(s => s.Length > 0).Take(10).ToList() ?? [],
                });
            }
        if (filing.Episodes.Count == 0) return null;
        if (root["plans"] is JsonArray plans)
            foreach (var pl in plans.OfType<JsonObject>())
            {
                var what = Str(pl["what"]);
                if (what.Length < 4) continue;
                var when = Str(pl["when"]);
                filing.Plans.Add(new Plan
                {
                    Who = People.Canon(Str(pl["who"])), What = Trim(what, 200),
                    When = DateTime.TryParseExact(when, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? when : "",
                    Made = chunk[^1].Time,
                });
            }
        if (root["jokes"] is JsonArray jokes)
            filing.Jokes.AddRange(jokes.Select(Str).Where(s => s.Length >= 4).Select(s => Trim(s, 200)));
        if (root["facts"] is JsonArray facts)
            filing.Facts.AddRange(facts.OfType<JsonObject>().Select(f => new MemoryGraph.FactIn(Str(f["fact"]), Str(f["subject"]), Str(f["predicate"]),
                Str(f["object"]), (f["entities"] as JsonArray)?.Select(Str).Where(s => s.Length > 0).ToList() ?? [], Str(f["emotion"]),
                $"журнал {key} {from}", chunk[^1].Time)));
        return filing;
    }

    /// <summary>Положить раскладку в банк. plans — планы и шутки тоже (при перекладке дня — нет); facts — факты в граф сразу.</summary>
    private void Apply(string key, Filing filing, bool plans, bool facts)
    {
        var added = new List<string>();
        lock (_lock)
        {
            foreach (var e in filing.Episodes)
            {
                var n = 1;
                string id;
                do id = $"{key}-{e.From.Replace(":", "")}-{n++}"; while (_store.Episodes.Any(x => x.Id == id));
                e.Id = id;
                _store.Episodes.Add(e);
                added.Add(e.Title);
            }
            if (plans)
            {
                foreach (var p in filing.Plans)
                {
                    // то же обещание она могла уже записать сама (memory promise) — другими словами
                    if (_store.Plans.Any(x => !x.Done && (x.What.Equals(p.What, StringComparison.OrdinalIgnoreCase) || Alike(x.What, p.What) >= 0.55f))) continue;
                    p.Id = $"p{DateTime.Now:MMddHHmmss}{_store.Plans.Count}";
                    _store.Plans.Add(p);
                }
                foreach (var j in filing.Jokes)
                    if (!_store.Notes.Any(x => x.Text.Equals(j, StringComparison.OrdinalIgnoreCase)))
                        _store.Notes.Add(new Note { Id = $"n{DateTime.Now:MMddHHmmss}{_store.Notes.Count}", Who = "компания", Text = j, Kind = "шутка", Made = filing.End });
            }
            _index = null;
        }
        if (facts && filing.Facts.Count > 0 && MemoryGraph.Current is { } graph) _ = Task.Run(() => graph.AddFacts(filing.Facts));
        Log.Write($"банк памяти: {key} {filing.Episodes[0].From}–{filing.End:HH:mm} → {string.Join("; ", added)}");
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    /// <summary>
    /// JSON из ответа мозга, даже обрезанного по длине (30.09 22:40: ответ оборвался на 34-й строке, и в банк легло
    /// «```json { "episodes": …» вместо пересказа): берётся всё до последнего целиком закрытого значения, скобки
    /// закрываются. null — JSON там нет.
    /// </summary>
    internal static JsonObject? ParseLoose(string reply)
    {
        var a = reply.IndexOf('{');
        if (a < 0) return null;
        var b = reply.LastIndexOf('}');
        if (b > a)
            try { if (JsonNode.Parse(reply[a..(b + 1)]) is JsonObject whole) return whole; }
            catch (JsonException) { }
        var stack = new Stack<char>();
        var inString = false;
        var escaped = false;
        var safe = -1;
        var closers = "";
        for (var i = a; i < reply.Length; i++)
        {
            var c = reply[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': stack.Push('}'); break;
                case '[': stack.Push(']'); break;
                case '}' or ']':
                    if (stack.Count == 0 || stack.Pop() != c) return null;
                    if (stack.Count == 0) { safe = i + 1; closers = ""; i = reply.Length; break; }
                    safe = i + 1;
                    closers = new string(stack.ToArray());
                    break;
            }
        }
        if (safe < 0) return null;
        try { return JsonNode.Parse(reply[a..safe] + closers) as JsonObject; }
        catch (JsonException) { return null; }
    }

    // ---------------- поиск ----------------

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "это", "что", "как", "так", "вот", "там", "тут", "был", "была", "было", "были", "она", "они", "его", "её", "мне", "меня",
        "тебя", "тебе", "мы", "вы", "ты", "нахуй", "бля", "блять", "ну", "да", "нет", "ага", "мур", "клава", "уже", "еще", "ещё",
        "про", "для", "или", "если", "когда", "потом", "тоже", "очень", "просто", "сейчас", "щас", "чем", "чём", "кто",
        "помнишь", "вчера", "сегодня", "говорили", "было", "назад", "часа", "часов", "утром", "вечером", "днем", "ночью",
    };

    private static uint Hash(string s)
    {
        var h = 2166136261u;
        foreach (var c in s) { h ^= c; h *= 16777619u; }
        return h;
    }

    /// <summary>Основа слова (первые 5 букв) — вес 2, буквенные триграммы — вес 1: «Аксюта/Аксюты/Аксютой» сходятся.</summary>
    private static Dictionary<int, float> Features(string text)
    {
        var f = new Dictionary<int, float>();
        foreach (Match m in WordRx().Matches(text.ToLowerInvariant().Replace('ё', 'е')))
        {
            var w = m.Value;
            if (w.Length < 3 || Stop.Contains(w)) continue;
            var stem = w.Length > 5 ? w[..5] : w;
            var k = (int)Hash("s:" + stem);
            f[k] = f.GetValueOrDefault(k) + 2;
            var p = "^" + w + "$";
            for (var i = 0; i + 3 <= p.Length; i++)
            {
                var t = (int)Hash(p.Substring(i, 3));
                f[t] = f.GetValueOrDefault(t) + 1;
            }
        }
        return f;
    }

    [GeneratedRegex(@"[\p{L}\d]+")]
    private static partial Regex WordRx();

    private static string TextOf(object item) => item switch
    {
        Episode e => $"{e.Title} {e.Summary} {string.Join(' ', e.Keys)} {string.Join(' ', e.People)} {e.Shelf}",
        Note n => $"{n.Who} {n.Text}",
        Plan p => $"{p.Who} {p.What}",
        _ => "",
    };

    /// <summary>Индекс с весом редкости: частое слово («сегодня», «Клава») почти ничего не весит.</summary>
    private List<(object Item, Dictionary<int, float> Vec, float Norm)> Index()
    {
        lock (_lock)
        {
            if (_index is not null) return _index;
            var docs = _store.Episodes.Cast<object>().Concat(_store.Notes).Concat(_store.Plans.Where(p => !p.Done)).ToList();
            var feats = docs.Select(d => Features(TextOf(d))).ToList();
            var df = new Dictionary<int, int>();
            foreach (var f in feats)
                foreach (var k in f.Keys) df[k] = df.GetValueOrDefault(k) + 1;
            var n = Math.Max(1, docs.Count);
            _idf = df.ToDictionary(x => x.Key, x => (float)Math.Log(1 + n / (1.0 + x.Value)));
            _index = [];
            for (var i = 0; i < docs.Count; i++)
            {
                var v = feats[i].ToDictionary(x => x.Key, x => x.Value * _idf.GetValueOrDefault(x.Key, 1f));
                var norm = MathF.Sqrt(v.Values.Sum(x => x * x));
                if (norm > 0) _index.Add((docs[i], v, norm));
            }
            return _index;
        }
    }

    private Dictionary<int, float> _idf = new();

    /// <summary>Что в банке ближе всего к тексту: (запись, сходство 0..1). keep — отбор записей (null — все).</summary>
    private List<(object Item, float Score)> Search(string query, Func<object, bool>? keep, int top)
    {
        var index = Index();
        var q = Features(query);
        Dictionary<int, float> idf;
        lock (_lock) idf = _idf;
        var qv = q.ToDictionary(x => x.Key, x => x.Value * idf.GetValueOrDefault(x.Key, 1f));
        var qn = MathF.Sqrt(qv.Values.Sum(x => x * x));
        if (qn == 0) return [];
        return index
            .Where(d => keep is null || keep(d.Item))
            .Select(d => (d.Item, Score: qv.Sum(x => d.Vec.TryGetValue(x.Key, out var w) ? w * x.Value : 0) / (qn * d.Norm)))
            .Where(x => x.Score > 0.02f)
            .OrderByDescending(x => x.Score).Take(top).ToList();
    }

    /// <summary>Отбор записей по отрезку времени.</summary>
    private static Func<object, bool> In(TimeWindow w) => item => item switch
    {
        Episode e => Times(e) is var (f, t) && w.Overlaps(f, t),
        Note n => n.Made >= w.From && n.Made < w.To,
        Plan p => p.Made >= w.From && p.Made < w.To,
        _ => false,
    };

    // ---------------- вопрос про прошлое ----------------

    [GeneratedRegex(@"помн|вспомн|напомн|о ч[её]м (мы |вы |ты |вы с \w+ |там |тут )*(говорил|болтал|базарил|т[её]р|общал|трепал|спорил)|что(-то)? (ты |вы |мы |он |она |там |тут |было |у нас )*(говорил|рассказывал|обсуждал|делал|сказал|болтал|тер|было|происходил|случил)|пока (я|меня|он|она|мы|вас|тебя)\b.{0,20}(отход|отош|уход|ушл|ушел|ушёл|не было|спал|курил|ел|отсутств|гулял|ходил)|без меня|до того|до этого|вчера|позавчера|на днях|в прошлый раз|на прошлой неделе|с кем (ты )?(говорил|болтал)|\bназад\b|когда (мы|ты|я|он|она|вы|это|тебя|меня)\b|во сколько|в котором часу|какого числа|в какой день|давно ли|сколько (времени )?прошло|чем (мы |вы |ты )?(занимал|занимались)|только что (говорил|сказал|был)",
        RegexOptions.IgnoreCase)]
    private static partial Regex PastRx();

    [GeneratedRegex(@"пока (я|меня)\b.{0,20}(отход|отош|уход|ушл|ушел|ушёл|не было|спал|курил|ел|отсутств|гулял|ходил)|без меня", RegexOptions.IgnoreCase)]
    private static partial Regex AwayRx();

    /// <summary>Это вопрос про прошлое?</summary>
    public static bool IsPastQuestion(string text) => PastRx().IsMatch(text);

    private static string Line(Episode e) =>
        $"• {e.From}–{e.To} «{e.Title}» ({string.Join(", ", e.People.Take(5))}): {Trim(e.Summary, 260)}";

    private static string Raw(Utterance u, int max) =>
        u.Hint ? $"{u.Time:HH:mm} (подсказка тебе)" : $"{u.Time:HH:mm} {(u.Klava ? "ты" : u.Who)}: {Trim(u.Text, max)}";

    /// <summary>
    /// Отлучка говорящего сегодня: последний разрыв между его репликами дольше 4 минут — и что в это время говорили
    /// другие и она сама (сырые реплики: разложить по эпизодам их могли ещё не успеть).
    /// </summary>
    private (DateTime From, DateTime To, string Text)? AwayWindow(string speaker)
    {
        var name = People.Canon(speaker);
        var today = _journal.Transcript(DateTime.Today);
        // сам вопрос в журнал ещё не записан (он пишется после ответа) — «сейчас» замыкает последний разрыв
        var mine = today.Where(u => u.Who == name).Select(u => u.Time).Append(DateTime.Now).ToList();
        if (mine.Count < 2) return null;
        for (var i = mine.Count - 1; i >= 1; i--)
        {
            var (left, back) = (mine[i - 1], mine[i]);
            if (back - left < TimeSpan.FromMinutes(4)) continue;
            var during = today.Where(u => u.Time > left && u.Time < back && !u.Hint).ToList();
            if (during.Count == 0) return null;
            var sb = new StringBuilder($"Пока {name} отходил (сегодня {left:HH:mm}–{back:HH:mm}), было вот что:\n");
            // долгую отлучку целиком не пересказать — последние реплики сырыми, а всё остальное — эпизодами
            foreach (var u in during.TakeLast(20)) sb.AppendLine(Raw(u, 120));
            return (left, back, sb.ToString());
        }
        return null;
    }

    /// <summary>Оглавление по часам: «14:00 — «тема»; «тема» (и ещё 2)».</summary>
    private static void HourIndex(StringBuilder sb, List<Episode> eps)
    {
        foreach (var hour in eps.GroupBy(e => e.From.Length >= 2 ? e.From[..2] : "00").OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var titles = hour.Select(e => $"«{e.Title}»").Distinct().ToList();
            sb.AppendLine($"  {hour.Key}:00 — {string.Join("; ", titles.Take(3))}{(titles.Count > 3 ? $" (и ещё {titles.Count - 3})" : "")}");
        }
    }

    /// <summary>
    /// Что банк даёт к реплике. Вопрос про прошлое — хроника нужного отрезка (итог дня, эпизоды по времени, на узкий
    /// отрезок и на отлучку — сами реплики) плюс подходящее по смыслу из других дней. Иначе — одно воспоминание к слову
    /// (с датой, не чаще раза в 6 часов на запись) и созревший план говорящего («спроси, как вышло»).
    /// </summary>
    public BankContext ContextFor(string? speaker, string userText)
    {
        var now = DateTime.Now;
        var who = string.IsNullOrWhiteSpace(speaker) ? null : speaker;
        var items = new List<Recall>();
        string? timeline = null;
        if (PastRx().IsMatch(userText))
        {
            var sb = new StringBuilder("(Спрашивают про прошлое. Ниже — твоя память по времени. Отвечай по ней своими словами, с живыми " +
                                       "деталями; день и час называй как в записях. Чего здесь нет — честно скажи, что не помнишь: НЕ выдумывай " +
                                       "и не переноси событие с другого дня. Мало — возьми memory (timeline / journal).\n");
            (DateTime From, DateTime To, string Text)? away = who is not null && AwayRx().IsMatch(userText) ? AwayWindow(who) : null;
            if (away is { } aw) sb.Append(aw.Text);
            var window = away is { } a2 ? new TimeWindow(a2.From, a2.To, Narrow: false, WholeDays: false) : TimeWords.Parse(userText, now);
            if (window is not null)
            {
                var found = false;
                foreach (var d in window.Days().Take(8))
                {
                    var k = d.ToString("yyyy-MM-dd");
                    List<Episode> eps;
                    lock (_lock) eps = _store.Episodes.Where(e => e.Day == k && Times(e) is var (f, t) && window.Overlaps(f, t)).OrderBy(e => e.From, StringComparer.Ordinal).ToList();
                    var summary = window.WholeDays ? _friends.DaySummary(k) : null;
                    if (eps.Count == 0 && summary is null) continue;
                    found = true;
                    sb.AppendLine($"▌{Chronicle.DayFull(d, now)}{(window.WholeDays ? "" : $", {(d == window.From.Date ? window.From : d):HH:mm}–{(window.To.Date > d ? "24:00" : window.To.ToString("HH:mm"))}")}:");
                    if (summary is not null) sb.AppendLine($"итог дня: {summary}");
                    if (eps.Count <= 10) foreach (var e in eps) sb.AppendLine(Line(e));
                    else
                    {
                        sb.AppendLine($"по часам (эпизодов {eps.Count}):");
                        HourIndex(sb, eps);
                        var ids = eps.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                        var close = Search(userText, x => x is Episode e && ids.Contains(e.Id), 6).Select(h => (Episode)h.Item).ToList();
                        if (close.Count < 4) close = close.Concat(eps.TakeLast(4)).DistinctBy(e => e.Id).ToList();
                        sb.AppendLine("подробнее:");
                        foreach (var e in close.OrderBy(e => e.From, StringComparer.Ordinal)) sb.AppendLine(Line(e));
                    }
                }
                // узкий отрезок — сами реплики; сегодняшний хвост, ещё не разложенный по эпизодам, — тоже
                if (away is null && (window.Narrow || window.To - window.From <= TimeSpan.FromHours(3)))
                {
                    var raw = window.Days().SelectMany(d => _journal.Transcript(d)).Where(u => u.Time >= window.From && u.Time < window.To && !u.Hint).ToList();
                    if (raw.Count > 0)
                    {
                        found = true;
                        sb.AppendLine($"дословно из журнала ({Chronicle.Stamp(window.From, now)}–{window.To:HH:mm}{(raw.Count > 24 ? $", последние 24 из {raw.Count}" : "")}):");
                        foreach (var u in raw.TakeLast(24)) sb.AppendLine(Raw(u, 140));
                    }
                }
                else if (away is null && window.Overlaps(now.Date, now))
                {
                    DateTime filed;
                    lock (_lock) filed = _store.Filed.TryGetValue(now.ToString("yyyy-MM-dd"), out var f) ? f : DateTime.MinValue;
                    var tail = _journal.Transcript(now.Date).Where(u => u.Time > filed && u.Time >= window.From && !u.Hint).ToList();
                    if (tail.Count > 0)
                    {
                        found = true;
                        sb.AppendLine($"сегодня последнее (ещё не разложено, с {tail[0].Time:HH:mm}):");
                        foreach (var u in tail.TakeLast(14)) sb.AppendLine(Raw(u, 120));
                    }
                }
                if (!found) sb.AppendLine($"За {window.Label(now)} в памяти ничего нет — так и скажи.");
            }
            // время названо («что было вчера вечером») — похожее из других дней только явно по теме, без имени говорящего
            var hits = (window is null
                    ? Search($"{(who is not null ? People.Canon(who) : "")} {userText}", null, 6).Where(h => h.Score > 0.08f)
                    : Search(userText, x => !In(window)(x), 6).Where(h => h.Score > 0.22f))
                .Take(4).ToList();
            if (hits.Count > 0)
            {
                sb.AppendLine(window is null ? "похожее из памяти (по времени):" : "похожее из другого времени:");
                foreach (var line in hits.Select(h => Dated(h.Item, now, ids: false)).OrderBy(x => x.When)) sb.AppendLine(line.Text);
            }
            sb.Append(')');
            timeline = sb.ToString();
            Log.Write($"банк памяти: вопрос про прошлое «{Trim(userText, 60)}» — {(window is null ? "время не названо" : window.Label(now))}, похожего {hits.Count}");
        }
        else
        {
            var today = now.ToString("yyyy-MM-dd");
            var hit = Search(userText, null, 3)
                .FirstOrDefault(h => h.Score >= 0.2f && h.Item is Episode e && e.Day != today && Fresh(e.Id)
                                     || h.Score >= 0.25f && h.Item is Note n && Fresh(n.Id));
            if (hit.Item is Episode ep)
            {
                Told(ep.Id);
                items.Add(new Recall(Times(ep).From, "", $"«{ep.Title}» ({string.Join(", ", ep.People.Take(4))}) — {Trim(ep.Summary, 240)}", ep.Id));
            }
            else if (hit.Item is Note note)
            {
                Told(note.Id);
                items.Add(new Recall(note.Made, note.Kind == "шутка" ? "шутка компании" : "заметка", note.Text, note.Id));
            }
        }
        // своё обещание важнее чужого плана: одно напоминание за реплику
        string? plan = null;
        if (DuePromise() is { } mine) plan = $"({PromiseNote(mine, now)})";
        else if (who is not null && DuePlan(who) is { } due)
            plan = $"(Ты помнишь: {due.Who} {Chronicle.Stamp(due.Made, now)} {(due.When.Length > 0 ? $"на {due.When} " : "")}собирался — {due.What}. " +
                   "Если к месту, спроси, как вышло; сделал — отметь memory action:'done'.)";
        return new BankContext(timeline, items, plan);
    }

    /// <summary>Запись банка строкой с меткой времени (и моментом — для порядка).</summary>
    private static (DateTime When, string Text) Dated(object item, DateTime now, bool ids) => item switch
    {
        Episode e => (Times(e).From, $"• {Chronicle.DayStamp(Times(e).From, now)} {e.From}–{e.To} «{e.Title}» ({string.Join(", ", e.People.Take(5))}): {Trim(e.Summary, 260)}{(ids ? $"  [{e.Shelf}, {e.Id}]" : "")}"),
        Note n => (n.Made, $"• {Chronicle.Stamp(n.Made, now)} {(n.Kind == "шутка" ? "шутка компании" : "заметка")}{(n.Who.Length > 0 && n.Who != "компания" ? $" ({n.Who})" : "")}: {n.Text}{(ids ? $"  [{n.Id}]" : "")}"),
        Plan p => (p.Made, $"• {Chronicle.Stamp(p.Made, now)} план: {p.Who} — {p.What}{(p.When.Length > 0 ? $" (к {p.When})" : "")}{(ids ? $"  [{p.Id}]" : "")}"),
        _ => (now, ""),
    };

    // «уже вспоминала» переживает перезапуск (MemoryState); без него (проверки) — в памяти процесса
    private bool Fresh(string id)
    {
        if (MemoryState.Current is { } st) return st.Fresh("bank:" + id, TimeSpan.FromHours(6));
        lock (_lock) return !_told.TryGetValue(id, out var t) || DateTime.Now - t > TimeSpan.FromHours(6);
    }

    private void Told(string id)
    {
        if (MemoryState.Current is { } st) st.MarkTold("bank:" + id);
        else lock (_lock) _told[id] = DateTime.Now;
    }

    /// <summary>Созревший план человека: срок сегодня или прошёл (без срока — с прошлых дней), не напоминала сегодня.</summary>
    public Plan? DuePlan(string speaker)
    {
        var name = People.Canon(speaker).ToLowerInvariant();
        if (name.Length == 0) return null;
        var first = name.Split(' ')[0];
        Plan? plan;
        lock (_lock)
        {
            plan = _store.Plans.Where(p => !p.Done && p.Made > DateTime.Now.AddDays(-10) && !p.Who.Equals("Клава", StringComparison.OrdinalIgnoreCase)
                                               && (p.Who.ToLowerInvariant().Contains(first) || name.Contains(p.Who.ToLowerInvariant()) && p.Who.Length > 2)
                                               && (p.When.Length > 0 ? string.CompareOrdinal(p.When, DateTime.Today.ToString("yyyy-MM-dd")) <= 0 : p.Made.Date < DateTime.Today)
                                               && (p.Reminded is null || p.Reminded.Value.Date < DateTime.Today))
                .OrderBy(p => p.Made).FirstOrDefault();
            if (plan is null) return null;
            plan.Reminded = DateTime.Now;
        }
        Save();
        return plan;
    }

    private static bool Own(Plan p) => p.Who.Contains("Клава", StringComparison.OrdinalIgnoreCase);

    /// <summary>Похожесть двух коротких текстов по основам слов и буквенным тройкам, 0..1.</summary>
    private static float Alike(string a, string b)
    {
        var (fa, fb) = (Features(a), Features(b));
        var (na, nb) = (MathF.Sqrt(fa.Values.Sum(x => x * x)), MathF.Sqrt(fb.Values.Sum(x => x * x)));
        return na == 0 || nb == 0 ? 0 : fa.Sum(x => fb.TryGetValue(x.Key, out var w) ? w * x.Value : 0) / (na * nb);
    }

    [GeneratedRegex(@"напомн|переда|сказа|попрос|спрос|предупред|сообщ", RegexOptions.IgnoreCase)]
    private static partial Regex ErrandRx();

    private DateTime _promiseAt = DateTime.MinValue;

    /// <summary>
    /// Её собственное обещание, которое пора выполнить (друг 01.10: «просил передать компании, чтоб плиту помыли, —
    /// обещала и забыла»): открытое, дано не только что (20 мин) и не давнее 4 дней, срок не в будущем. Поручения
    /// другим людям («передай», «напомни») — вперёд своих дел. Про одно обещание — не чаще раза в 2 часа и до трёх раз,
    /// про разные — не чаще раза в 10 минут.
    /// </summary>
    public Plan? DuePromise()
    {
        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd");
        Plan? plan;
        lock (_lock)
        {
            if (now - _promiseAt < TimeSpan.FromMinutes(10)) return null;
            plan = _store.Plans.Where(p => !p.Done && Own(p) && p.Tries < 3 && now - p.Made > TimeSpan.FromMinutes(20) && now - p.Made < TimeSpan.FromDays(4)
                                           && (p.When.Length == 0 || string.CompareOrdinal(p.When, today) <= 0)
                                           && (p.Reminded is null || now - p.Reminded > TimeSpan.FromHours(2)))
                .OrderByDescending(p => ErrandRx().IsMatch(p.What)).ThenByDescending(p => p.Made).FirstOrDefault();
            if (plan is null) return null;
            plan.Reminded = _promiseAt = now;
            plan.Tries++;
        }
        Save();
        return plan;
    }

    /// <summary>Подсказка про её обещание — в реплику или поводом самой заговорить.</summary>
    public static string PromiseNote(Plan p, DateTime now) =>
        $"За тобой обещание — {Chronicle.Stamp(p.Made, now)} ты обещала: {p.What}{(p.When.Length > 0 ? $" (к {p.When})" : "")}. " +
        $"Выполни его сейчас, в этой же реплике (передать, напомнить, спросить — скажи это вслух тем, кому надо), и отметь " +
        $"memory action:'done' id:'{p.Id}'. Уже выполнено или потеряло смысл — тоже отметь done.";

    /// <summary>Недавние эпизоды (для «сама заводит разговор»): самые свежие темы прошлых дней и сегодняшние.</summary>
    public List<Episode> Recent(int days, int max)
    {
        var since = DateTime.Today.AddDays(-days).ToString("yyyy-MM-dd");
        lock (_lock)
            return _store.Episodes.Where(e => string.CompareOrdinal(e.Day, since) >= 0)
                .OrderByDescending(e => e.Day, StringComparer.Ordinal).ThenByDescending(e => e.From, StringComparer.Ordinal).Take(max).ToList();
    }

    /// <summary>Все эпизоды (засев графа памяти, дневник).</summary>
    public List<Episode> AllEpisodes()
    {
        lock (_lock) return [.. _store.Episodes];
    }

    /// <summary>День эпизодами по порядку — сырьё для итога дня (FriendMemory); null — день не разложен.</summary>
    public string? DayDigest(string key)
    {
        List<Episode> eps;
        lock (_lock)
        {
            if (_store.Rebuild.Contains(key)) return null;
            eps = _store.Episodes.Where(e => e.Day == key).OrderBy(e => e.From, StringComparer.Ordinal).ToList();
        }
        if (eps.Count == 0) return null;
        var sb = new StringBuilder();
        var budget = Math.Max(120, 9000 / eps.Count - 60);
        foreach (var e in eps) sb.AppendLine($"{e.From}–{e.To} {string.Join(", ", e.People.Take(4))}: {e.Title}. {Trim(e.Summary, budget)}");
        return sb.ToString();
    }

    // ---------------- инструмент memory ----------------

    /// <summary>Отрезок из слов инструмента: day («вчера», «27.09») и time («вечером», «15:00-16:00», «около трёх»).</summary>
    internal static TimeWindow? WindowOf(string day, string time, DateTime now)
    {
        var t = time.Trim();
        if (ClockRangeRx().Match(t) is { Success: true } m) t = $"с {m.Groups[1].Value} до {m.Groups[2].Value}";
        else if (ClockOnlyRx().IsMatch(t)) t = "в " + t;
        var text = $"{day} {t}".Trim();
        return text.Length == 0 ? null : TimeWords.Parse(text, now);
    }

    [GeneratedRegex(@"^(\d{1,2}(?:[:.]\d{2})?)\s*[-–—]\s*(\d{1,2}(?:[:.]\d{2})?)$")]
    private static partial Regex ClockRangeRx();
    [GeneratedRegex(@"^\d{1,2}[:.]\d{2}$")]
    private static partial Regex ClockOnlyRx();

    /// <summary>
    /// Инструмент memory. Асинхронный: инструменты выполняются в UI-потоке (ход мозга крутится в нём через await), и
    /// прежний синхронный Tool ждал RecallAsync через GetResult — продолжение запроса к векторизатору вставало в очередь
    /// того же UI-потока, и Клава висла намертво (дамп 30.09 15:59: UI-поток в MemoryBank.Tool → GetResult).
    /// </summary>
    public async Task<string> ToolAsync(string action, string query, string day, string text, string who, string id, string time = "")
    {
        var now = DateTime.Now;
        switch (action.Trim().ToLowerInvariant())
        {
            case "note" or "remember" or "запомни":
            {
                if (text.Trim().Length < 3) return "Что запомнить? text — сама заметка.";
                var n = new Note { Id = $"n{now:MMddHHmmss}", Who = People.Canon(who), Text = Trim(text.Trim(), 400), Made = now };
                lock (_lock) { _store.Notes.Add(n); _index = null; }
                Save();
                return $"Запомнила ({n.Id}, {now:dd.MM HH:mm}): {n.Text}";
            }
            case "promise" or "обещание":
            {
                if (text.Trim().Length < 4) return "Что обещала? text — что сделать или передать, who — кому или для кого, day — к какому дню (если есть срок).";
                var what = Trim(text.Trim(), 200) + (who.Trim().Length > 0 ? $" (кому: {People.Canon(who)})" : "");
                var due = day.Trim().Length > 0 && TimeWords.DaysOf(day.ToLowerInvariant().Replace('ё', 'е'), now.Date) is { Said: true } d ? d.From.ToString("yyyy-MM-dd") : "";
                Plan p;
                lock (_lock)
                {
                    if (_store.Plans.FirstOrDefault(x => !x.Done && Own(x) && Alike(x.What, what) >= 0.55f) is { } same)
                        return $"Это обещание уже записано ({same.Id}, {Chronicle.Stamp(same.Made, now)}): {same.What}";
                    p = new Plan { Id = $"p{now:MMddHHmmss}{_store.Plans.Count}", Who = "Клава", What = what, When = due, Made = now };
                    _store.Plans.Add(p);
                    _index = null;
                }
                Save();
                return $"Записала обещание ({p.Id}, {now:dd.MM HH:mm}): {p.What}{(due.Length > 0 ? $" — к {due}" : "")}. Напомню тебе сама, когда придёт время; выполнишь — memory action:'done' id:'{p.Id}'.";
            }
            case "alias" or "прозвище":
            {
                if (text.Trim().Length < 2 || who.Trim().Length < 2) return "alias: text — прозвище или второе имя, who — чьё оно (имя человека, которого ты знаешь).";
                return People.Learn(text, who) ? $"Запомнила: «{text.Trim()}» — это {People.Canon(who)}." : $"Не знаю человека «{who}» — назови его так, как он записан в голосовых профилях.";
            }
            case "plans":
            {
                lock (_lock)
                {
                    var open = _store.Plans.Where(p => !p.Done).OrderBy(p => p.Made).ToList();
                    if (open.Count == 0) return "Открытых планов и обещаний нет.";
                    string One(Plan p) => $"{p.Id}: {Chronicle.Stamp(p.Made, now)} — {p.Who}: {p.What}{(p.When.Length > 0 ? $" (к {p.When})" : "")}";
                    var mine = open.Where(Own).ToList();
                    return (mine.Count > 0 ? "ТВОИ обещания (выполни и отметь done):\n" + string.Join("\n", mine.Select(One)) + "\n" : "") +
                           "Планы людей (когда записано):\n" + string.Join("\n", open.Where(p => !Own(p)).Select(One));
                }
            }
            case "done":
            {
                lock (_lock)
                {
                    var p = _store.Plans.FirstOrDefault(x => x.Id == id.Trim());
                    if (p is null) return $"Нет плана {id} — глянь memory action:'plans'.";
                    p.Done = true;
                    _index = null;
                }
                Save();
                return $"Отметила: {id} сделано.";
            }
            case "forget":
            {
                int removed;
                lock (_lock)
                {
                    removed = _store.Notes.RemoveAll(x => x.Id == id.Trim()) + _store.Plans.RemoveAll(x => x.Id == id.Trim()) + _store.Episodes.RemoveAll(x => x.Id == id.Trim());
                    _index = null;
                }
                Save();
                return removed > 0 ? $"Забыла {id}." : $"Нет записи {id}.";
            }
            case "hours" or "часы":
            {
                var d = (WindowOf(day, "", now) ?? new TimeWindow(now.Date, now.Date.AddDays(1), false, true)).From.Date;
                return Hours(d, now);
            }
            case "journal" or "журнал":
            {
                var w = WindowOf(day, time, now);
                if (w is null || w.WholeDays || w.To - w.From > TimeSpan.FromHours(6))
                    return "Дословный журнал — по отрезку до 6 часов: укажи time («15:00-16:00», «вечером», «около трёх», «час назад»).\n" +
                           Hours((w ?? new TimeWindow(now.Date, now.Date.AddDays(1), false, true)).From.Date, now);
                var raw = w.Days().SelectMany(d => _journal.Transcript(d)).Where(u => u.Time >= w.From && u.Time < w.To && !u.Hint).ToList();
                if (raw.Count == 0) return $"За {w.Label(now)} в журнале реплик нет.";
                var sb = new StringBuilder($"Журнал, {w.Label(now)} — реплик {raw.Count}{(raw.Count > 80 ? ", показаны первые 80 (сузь time, чтобы увидеть дальше)" : "")}:\n");
                foreach (var u in raw.Take(80)) sb.AppendLine($"{u.Time:HH:mm} {u.Who}: {Trim(u.Text, 200)}");
                return sb.ToString();
            }
            case "timeline":
            {
                var w = WindowOf(day, time, now) ?? new TimeWindow(now.Date, now.Date.AddDays(1), false, true);
                var sb = new StringBuilder();
                foreach (var d in w.Days().Take(8))
                {
                    var k = d.ToString("yyyy-MM-dd");
                    List<Episode> eps;
                    lock (_lock) eps = _store.Episodes.Where(e => e.Day == k && Times(e) is var (f, t) && w.Overlaps(f, t)).OrderBy(e => e.From, StringComparer.Ordinal).ToList();
                    sb.AppendLine($"▌{Chronicle.DayFull(d, now)}{(w.WholeDays ? "" : $", {w.From:HH:mm}–{(w.To.Date > d ? "24:00" : w.To.ToString("HH:mm"))}")}:");
                    if (MemoryState.Current?.SessionsOf(d) is { Count: > 0 } runs)
                        sb.AppendLine("ты работала: " + string.Join(", ", runs.Select(r => $"{(r.Start.Date == d ? r.Start.ToString("HH:mm") : "с прошлого дня")}–{(r.End is { } end ? (end.Date == d ? end.ToString("HH:mm") : "за полночь") : "сейчас")}")));
                    if (w.WholeDays && _friends.DaySummary(k) is { } s) sb.AppendLine($"итог дня: {s}");
                    if (eps.Count == 0) sb.AppendLine(d == now.Date ? "эпизодов пока нет (сегодняшнее раскладывается после пауз; дословно — action:'journal')." : "эпизодов в банке нет.");
                    else if (eps.Count <= 30) foreach (var e in eps) sb.AppendLine($"{Line(e)}  [{e.Id}]");
                    else
                    {
                        sb.AppendLine($"эпизодов {eps.Count} — оглавление по часам (подробно: time «14:00-16:00» или «вечером»):");
                        HourIndex(sb, eps);
                    }
                }
                return sb.ToString();
            }
            default:
            {
                var q = $"{query} {who}".Trim();
                var w = WindowOf(day, time, now);
                if (q.Length == 0 && w is null) return "Что вспомнить? query — о чём (люди, игра, тема), day — «вчера», «в пятницу», «27.09», time — «вечером», «15:00-16:00».";
                List<(object Item, float Score)> hits;
                if (q.Length == 0 && w is not null)
                    lock (_lock) hits = _store.Episodes.Where(e => In(w)(e)).Select(e => ((object)e, 1f)).ToList();
                else hits = Search(q, w is null ? null : In(w), 8);
                // холодный поиск по графу фактов (по смыслу, с архивом)
                var deep = MemoryGraph.Current is { } g && q.Length > 0 ? await g.RecallAsync(q).ConfigureAwait(false) : [];
                if (hits.Count == 0 && deep.Count == 0) return "В банке памяти ничего подходящего. Скажи честно, что не помнишь.";
                var sb = new StringBuilder("Найдено в памяти (по времени, у каждой записи — когда это было):\n");
                foreach (var line in hits.Take(24).Select(h => Dated(h.Item, now, ids: true)).OrderBy(x => x.When)) sb.AppendLine(line.Text);
                if (deep.Count > 0) sb.AppendLine("факты:").AppendLine(string.Join("\n", deep.Select(x => "• " + x)));
                return sb.ToString();
            }
        }
    }

    /// <summary>День по часам: сколько реплик, кто говорил, какие эпизоды.</summary>
    private string Hours(DateTime day, DateTime now)
    {
        var k = day.ToString("yyyy-MM-dd");
        var lines = _journal.Transcript(day).Where(u => !u.Hint).ToList();
        if (lines.Count == 0) return $"{Chronicle.DayFull(day, now)}: журнала за этот день нет.";
        List<Episode> eps;
        lock (_lock) eps = _store.Episodes.Where(e => e.Day == k).ToList();
        var sb = new StringBuilder($"{Chronicle.DayFull(day, now)} по часам:\n");
        foreach (var hour in lines.GroupBy(u => u.Time.Hour).OrderBy(g => g.Key))
        {
            var people = hour.Where(u => !u.Klava && u.Who.Length > 0).Select(u => u.Who).Distinct().Take(5);
            var titles = eps.Where(e => e.From.StartsWith($"{hour.Key:D2}:", StringComparison.Ordinal)).Select(e => $"«{e.Title}»").Distinct().Take(4).ToList();
            sb.AppendLine($"{hour.Key:D2}:00 — реплик {hour.Count()} ({string.Join(", ", people)}){(titles.Count > 0 ? ": " + string.Join("; ", titles) : "")}");
        }
        return sb.ToString();
    }

    /// <summary>Сколько всего в банке — для журнала при запуске.</summary>
    public string Describe()
    {
        lock (_lock)
            return $"эпизодов {_store.Episodes.Count} (дней {_store.Episodes.Select(e => e.Day).Distinct().Count()}), " +
                   $"планов открыто {_store.Plans.Count(p => !p.Done)}, заметок {_store.Notes.Count}" +
                   (_store.Rebuild.Count > 0 ? $", дней ждёт перекладки {_store.Rebuild.Count}" : "");
    }
}

internal sealed partial class MemoryBank
{
    /// <summary>
    /// KlavaDesk.exe --memory-test [file] ["вопрос" [говорящий]] — разложить журнал (file: закрытые куски и очередь
    /// перекладки, настоящим мозгом; факты — в граф памяти, итоги дней — заново) и показать, что банк подмешает на
    /// вопрос и что вернёт инструмент. Клава при этом должна быть закрыта: память у них общая.
    /// </summary>
    public static int Test(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\memory_test.log"));
        People.Load(cfg);
        using var brain = new Brain(cfg, saveHistory: false);
        var journal = new FlightJournal(cfg.Abs("data"));
        var friends = new FriendMemory(cfg.Abs(@"data\memory"), brain, journal);
        var bank = new MemoryBank(cfg.Abs(@"data\memory"), brain, journal, friends);
        friends.DayDigest = bank.DayDigest;
        Console.WriteLine($"журнал: дней {journal.Days().Count} ({string.Join(", ", journal.Days().Select(d => $"{d:dd.MM} — {journal.Transcript(d).Count} реплик"))})");
        Console.WriteLine($"было: {bank.Describe()}");
        if (args.Contains("file"))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // факты из раскладки идут в граф — он нужен живой (со своим векторизатором)
            var graph = new MemoryGraph(cfg);
            MemoryGraph.Current = graph;
            Task.Run(() => graph.StartAsync(bank, friends)).GetAwaiter().GetResult();
            Console.WriteLine($"граф: {graph.Describe()}");
            Task.Run(bank.ConsolidateAsync).GetAwaiter().GetResult();
            Console.WriteLine($"разложено за {sw.Elapsed.TotalSeconds:F0} с: {bank.Describe()}");
            Task.Run(friends.SummarizePastDaysAsync).GetAwaiter().GetResult();
            Task.Run(graph.FlushAsync).GetAwaiter().GetResult();
            Console.WriteLine($"граф: {graph.Describe()}; итоги дней: {friends.Describe(null)}");
            graph.Stop();
        }
        var rest = args.Where(a => a != "file").ToArray();
        if (rest.Length > 0)
        {
            var speaker = rest.Length > 1 ? rest[1] : cfg.RoomMembers.FirstOrDefault() ?? "Друг";
            var ctx = bank.ContextFor(speaker, rest[0]);
            Console.WriteLine($"\n«{rest[0]}» ({speaker}):\n{ctx.Timeline ?? Chronicle.Compose(ctx.Items, DateTime.Now) ?? "(банку нечего подмешать)"}");
            if (ctx.Plan is not null) Console.WriteLine(ctx.Plan);
        }
        Console.WriteLine($"\nвчера по часам:\n{bank.ToolAsync("hours", "", "вчера", "", "", "").GetAwaiter().GetResult()}");
        return 0;
    }
}
