using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Одна запись журнала. Kind: said — человек сказал (услышано); turn — Клава ответила на услышанное (слова людей уже
/// лежат записями said); typed — ответила на написанное или не слышанное (UserText — новое); self — заговорила сама
/// (UserText — повод в скобках); mark — отметка системы (запуск, выход). null — запись старого журнала (до 01.10).
/// Brain — каким мозгом она это сказала (id профиля и модель): по журналу потом учится её собственный мозг (mind\),
/// и ему надо знать, чьи ответы брать в пример, а чьи нет (01.10 полдня отвечал залипший локальный).
/// </summary>
internal sealed record JournalEntry(
    DateTime Timestamp,
    string Speaker,
    string UserText,
    string KlavaReply,
    string? ScreenContext,
    string? Kind = null,
    string? Brain = null);

/// <summary>Одна строка чистой хронологии: когда, кто, что сказал. Hint — подсказка системы самой Клаве, не слова людей.</summary>
internal sealed record Utterance(DateTime Time, string Who, string Text, bool Klava = false, bool Hint = false);

/// <summary>
/// Журнал Клавы — сырьё всей её памяти (друг 01.10: «журнал структурирован так, что в нём путаешься; она должна по датам
/// и по часам всё помнить как часовой»). Что было не так: один файл на день в куче других в data\, в каждой строке —
/// простыня подмешанной памяти (750 КБ за день: память о памяти), треть строк — фон и «(неразборчивая речь)», слова
/// людей дважды (услышано + та же реплика в ответе), ответы на общий разговор нескольких человек и то, что она говорила
/// сама, не писались вовсе, а кнопки «сброс» и «AI-сводка» затирали день (25–27.09 остались кусками по архивам).
/// Теперь: data\journal\ГГГГ-ММ-ДД.jsonl — только дописывается, по записи на событие с видом (Kind) и точным
/// временем; рядом ГГГГ-ММ-ДД.md для глаз, по часам; фон — отдельно в journal\noise. Всё остальное (банк, граф, итоги)
/// читает журнал через <see cref="Transcript(IReadOnlyList{JournalEntry})"/> — чистую хронологию без повторов.
/// </summary>
internal sealed partial class FlightJournal
{
    public const string Said = "said", Turn = "turn", Typed = "typed", Self = "self", Mark = "mark";

    private readonly string _dir;
    private readonly object _lock = new();
    private string _mdHour = "";
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <param name="dataDir">Папка data: журнал живёт в data\journal, старые flight_log_* из data переезжают туда сами.</param>
    public FlightJournal(string dataDir)
    {
        _dir = Path.Combine(dataDir, "journal");
        Directory.CreateDirectory(_dir);
        try { MigrateV1(dataDir); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Error("журнал: переезд старых журналов", ex);
        }
    }

    public string Dir => _dir;
    private string Jsonl(DateTime day) => Path.Combine(_dir, $"{day:yyyy-MM-dd}.jsonl");
    public string MdPath(DateTime day) => Path.Combine(_dir, $"{day:yyyy-MM-dd}.md");

    // ---------------- запись ----------------

    /// <summary>Человек сказал (свой голос, слова разобраны). Обрубки короче 3 знаков — мимо.</summary>
    public JournalEntry? LogHeard(string speaker, string text)
    {
        var s = text?.Trim() ?? "";
        if (s.Length < 3 || s.All(c => char.IsPunctuation(c) || char.IsWhiteSpace(c))) return null;
        return Append(new JournalEntry(DateTime.Now, Who(speaker), s, "", null, Said));
    }

    /// <summary>Клава ответила. heard — слова людей уже записаны как услышанные; нет — реплику написали или сказали мимо слуха.</summary>
    public JournalEntry? LogTurn(string speaker, string userText, string klavaReply, string? screen, bool heard, string? brain = null)
    {
        var user = userText?.Trim() ?? "";
        var reply = klavaReply?.Trim() ?? "";
        if (user.Length < 2 && reply.Length < 2) return null;
        var note = string.IsNullOrWhiteSpace(screen) ? null : Short(OneLine(screen), 400);
        return Append(new JournalEntry(DateTime.Now, Who(speaker), user, reply, note, heard ? Turn : Typed, string.IsNullOrWhiteSpace(brain) ? null : brain));
    }

    /// <summary>Клава заговорила сама (событие, своя затея, итог агента): повод — в скобках, как он ей пришёл.</summary>
    public JournalEntry? LogSelf(string hint, string klavaReply, string? brain = null)
    {
        var reply = klavaReply?.Trim() ?? "";
        if (reply.Length < 2) return null;
        return Append(new JournalEntry(DateTime.Now, "Клава", Short(OneLine(hint ?? ""), 240), reply, null, Self, string.IsNullOrWhiteSpace(brain) ? null : brain));
    }

    /// <summary>Отметка системы: «запуск», «выход».</summary>
    public void LogMark(string what) => Append(new JournalEntry(DateTime.Now, "СИСТЕМА", what, "", null, Mark));

    /// <summary>Чужой голос, телевизор, неразборчивое — не память, а отладка слуха: journal\noise\ДАТА.log.</summary>
    public void LogNoise(string who, string text)
    {
        lock (_lock)
        {
            try
            {
                var dir = Path.Combine(_dir, "noise");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}.log"), $"{DateTime.Now:HH:mm:ss} {who}: {OneLine(text)}{Environment.NewLine}");
            }
            catch (IOException ex) { Log.Error("журнал: фон", ex); }
        }
    }

    private static string Who(string? speaker) => string.IsNullOrWhiteSpace(speaker) ? "Друг" : speaker.Trim();
    private static string OneLine(string s) => SpaceRx().Replace(s, " ").Trim();
    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private JournalEntry? Append(JournalEntry e)
    {
        lock (_lock)
        {
            try
            {
                File.AppendAllText(Jsonl(e.Timestamp), JsonSerializer.Serialize(e, JsonOpts) + Environment.NewLine);
                var md = MdPath(e.Timestamp);
                using var sw = new StreamWriter(md, append: true, new UTF8Encoding(false));
                if (new FileInfo(md).Length == 0) sw.Write(MdTitle(e.Timestamp));
                var hour = e.Timestamp.ToString("yyyy-MM-dd HH");
                if (hour != _mdHour)
                {
                    _mdHour = hour;
                    sw.WriteLine();
                    sw.WriteLine($"## {e.Timestamp:HH}:00");
                    sw.WriteLine();
                }
                sw.Write(MdLines(e, covered: e.Kind == Turn));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("журнал: запись", ex);
                return null;
            }
        }
        return e;
    }

    private static readonly System.Globalization.CultureInfo Ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");

    private static string MdTitle(DateTime day) =>
        $"# Журнал Клавы — {day.ToString("d MMMM yyyy, dddd", Ru)}{Environment.NewLine}{Environment.NewLine}" +
        $"> Всё по порядку и по часам: кто что сказал и что ответила Клава. Только дописывается.{Environment.NewLine}";

    private static string MdLines(JournalEntry e, bool covered)
    {
        var sb = new StringBuilder();
        var t = e.Timestamp.ToString("HH:mm:ss");
        switch (KindOf(e))
        {
            case Mark:
                sb.AppendLine().AppendLine($"— **{t}** ▶ {e.UserText} —").AppendLine();
                break;
            case Said:
                sb.AppendLine($"**{t}** {People.Canon(e.Speaker)}: {OneLine(e.UserText)}  ");
                break;
            case Self:
                sb.AppendLine($"**{t}** 🐱 Клава (сама): {OneLine(e.KlavaReply)}  ");
                if (e.UserText.Length > 0) sb.AppendLine($"<sub>повод: {e.UserText}</sub>  ");
                break;
            default:
                if (!covered && e.UserText.Length > 0)
                    sb.AppendLine(e.UserText.StartsWith('(')
                        ? $"<sub>**{t}** подсказка: {Short(OneLine(e.UserText), 200)}</sub>  "
                        : $"**{t}** ⌨ {People.Canon(e.Speaker)}: {OneLine(StripName(e.UserText, e.Speaker))}  ");
                if (e.KlavaReply.Length > 0) sb.AppendLine($"**{t}** 🐱 Клава: {OneLine(e.KlavaReply)}  ");
                if (!string.IsNullOrEmpty(e.ScreenContext)) sb.AppendLine($"<sub>🔍 {Short(e.ScreenContext, 200)}</sub>  ");
                break;
        }
        return sb.ToString();
    }

    // ---------------- чтение ----------------

    public List<JournalEntry> GetTodayEntries() => GetEntries(DateTime.Now);

    public List<JournalEntry> GetEntries(DateTime day)
    {
        var file = Jsonl(day);
        lock (_lock) return ReadFile(file);
    }

    private static List<JournalEntry> ReadFile(string file)
    {
        var result = new List<JournalEntry>();
        if (!File.Exists(file)) return result;
        try
        {
            foreach (var line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                // обрезанная последняя строка (комп выключили посреди записи) не должна уносить весь день
                try { if (JsonSerializer.Deserialize<JournalEntry>(line, JsonOpts) is { } e) result.Add(e); }
                catch (JsonException) { }
            }
        }
        catch (IOException ex) { Log.Error("журнал: чтение", ex); }
        return result;
    }

    /// <summary>Дни, за которые есть журнал, по возрастанию.</summary>
    public List<DateTime> Days()
    {
        var days = new List<DateTime>();
        foreach (var f in Directory.GetFiles(_dir, "????-??-??.jsonl"))
            if (DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var d))
                days.Add(d);
        days.Sort();
        return days;
    }

    /// <summary>Время последней записи разговора (не отметки системы) до сейчас: «прошлый раз говорили…». null — журнал пуст.</summary>
    public DateTime? LastTalk()
    {
        foreach (var d in Days().AsEnumerable().Reverse().Take(10))
            if (GetEntries(d).LastOrDefault(e => KindOf(e) != Mark) is { } e)
                return e.Timestamp;
        return null;
    }

    /// <summary>Вид записи; у старых (без Kind) — по содержимому.</summary>
    public static string KindOf(JournalEntry e) =>
        e.Kind ?? (e.Speaker == "СИСТЕМА" ? Mark : e.KlavaReply.Length == 0 ? Said : "");

    /// <summary>Чистая хронология дня (см. <see cref="Transcript(IReadOnlyList{JournalEntry})"/>).</summary>
    public List<Utterance> Transcript(DateTime day) => Transcript(GetEntries(day));

    /// <summary>
    /// Журнал → чистая хронология: по строке на то, что кто-то сказал. Слова людей — один раз (у ответа Клавы они
    /// повторены, если она отвечала на услышанное), имена — в одном виде, подсказки системы помечены, отметки запуска
    /// пропущены.
    /// </summary>
    public static List<Utterance> Transcript(IReadOnlyList<JournalEntry> entries)
    {
        var list = new List<Utterance>();
        var recent = new List<(DateTime At, string Norm)>();
        foreach (var e in entries)
        {
            var kind = KindOf(e);
            var user = e.UserText.Trim();
            switch (kind)
            {
                case Mark:
                    continue;
                case Said:
                    if (user.Length == 0 || user.StartsWith('(')) continue;
                    list.Add(new Utterance(e.Timestamp, People.Canon(e.Speaker), Unwrap(user)));
                    recent.Add((e.Timestamp, Norm(user)));
                    continue;
                case Self:
                    if (user.Length > 0) list.Add(new Utterance(e.Timestamp, "", user, Hint: true));
                    list.Add(new Utterance(e.Timestamp, "Клава", Unwrap(e.KlavaReply), Klava: true));
                    continue;
            }
            var covered = kind == Turn || kind == "" && Covered(user, recent, e.Timestamp);
            if (!covered && user.Length > 0)
                list.Add(user.StartsWith('(')
                    ? new Utterance(e.Timestamp, "", OneLine(user), Hint: true)
                    : new Utterance(e.Timestamp, People.Canon(e.Speaker), Unwrap(StripName(user, e.Speaker))));
            if (e.KlavaReply.Trim().Length > 0) list.Add(new Utterance(e.Timestamp, "Клава", Unwrap(e.KlavaReply.Trim()), Klava: true));
        }
        return list;
    }

    /// <summary>«Кибер Кот: слова» → «слова», если это имя самого говорящего (оно и так стоит в записи).</summary>
    private static string StripName(string text, string speaker)
    {
        var name = speaker.Replace('_', ' ').Trim();
        return name.Length > 0 && text.StartsWith(name + ":", StringComparison.Ordinal) ? text[(name.Length + 1)..].TrimStart() : text;
    }

    /// <summary>Старый слух клеил куски whisper переводом строки, бывало посреди слова; перед знаком — без пробела.</summary>
    private static string Unwrap(string s) => SpaceRx().Replace(BreakBeforeMarkRx().Replace(s, "$1"), " ").Trim();

    private static string Norm(string s) => new(s.ToLowerInvariant().Replace('ё', 'е').Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Старая запись-ответ: её слова людей уже есть услышанными за последние минуты? (тогда повторять их не надо)</summary>
    private static bool Covered(string userText, List<(DateTime At, string Norm)> recent, DateTime now)
    {
        recent.RemoveAll(r => now - r.At > TimeSpan.FromMinutes(6));
        if (userText.StartsWith('(') && !userText.StartsWith("(Говорят несколько", StringComparison.Ordinal)) return false;
        var rest = Norm(BatchNamesRx().Replace(userText, " "));
        var full = rest.Length;
        if (full == 0) return true;
        foreach (var (_, n) in recent.OrderByDescending(r => r.Norm.Length))
            if (n.Length >= 3) rest = rest.Replace(n, "");
        return rest.Length <= Math.Max(6, full / 4);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpaceRx();
    [GeneratedRegex(@"\s*\n\s*([.,!?…:;])")]
    private static partial Regex BreakBeforeMarkRx();
    // «(Говорят несколько человек — 2. …):» и «Имя: » в начале каждой строки общей реплики
    [GeneratedRegex(@"\(Говорят несколько[^)]*\):?|(?m)^[\p{Lu}][\p{L}\d ]{1,40}:\s")]
    private static partial Regex BatchNamesRx();

    // ---------------- переезд старого журнала ----------------

    /// <summary>
    /// data\flight_log_ДАТА.jsonl (+ куски дней, уведённые «сбросом» и «AI-сводкой» в data\flight_backup) → полные дни в
    /// data\journal: записи всех кусков дня вместе, без повторов, по времени; фон — в noise; подмешанная память из
    /// контекста вычищена (остаётся только то, что было на экране). Оригиналы не стираются — уезжают как есть в
    /// data\journal\archive_v1. Делается один раз: когда в data больше нет flight_log_*.
    /// </summary>
    private void MigrateV1(string dataDir)
    {
        var old = Directory.GetFiles(dataDir, "flight_log_*.jsonl");
        var backup = Path.Combine(dataDir, "flight_backup");
        if (old.Length == 0 && !Directory.Exists(backup)) return;
        var pieces = old.ToList();
        if (Directory.Exists(backup)) pieces.AddRange(Directory.GetFiles(backup, "flight_log_*.jsonl"));
        var days = new SortedDictionary<DateTime, Dictionary<string, JournalEntry>>();
        foreach (var f in pieces)
            foreach (var e in ReadFile(f))
            {
                // сводка, которой «AI-сжатие» подменяло день, — не событие дня
                if (e.Speaker == "СИСТЕМА") continue;
                if (!days.TryGetValue(e.Timestamp.Date, out var day)) days[e.Timestamp.Date] = day = new(StringComparer.Ordinal);
                day[$"{e.Timestamp.Ticks}|{e.Speaker}|{e.UserText}"] = e;
            }
        int total = 0, noise = 0;
        foreach (var (date, rows) in days)
        {
            var target = Jsonl(date);
            // день уже пишется по-новому (переезд посреди дня) — старое встаёт перед новым
            var fresh = ReadFile(target);
            var clean = new List<JournalEntry>();
            var recent = new List<(DateTime At, string Norm)>();
            var noiseLines = new StringBuilder();
            foreach (var e in rows.Values.OrderBy(x => x.Timestamp))
            {
                if (e.Speaker.StartsWith("фон", StringComparison.Ordinal) || e.UserText == "(неразборчивая речь)")
                {
                    noiseLines.Append($"{e.Timestamp:HH:mm:ss} {e.Speaker}: {OneLine(e.UserText)}").AppendLine();
                    noise++;
                    continue;
                }
                var kind = e.KlavaReply.Length == 0 ? Said : Covered(e.UserText.Trim(), recent, e.Timestamp) ? Turn : Typed;
                if (kind == Said) recent.Add((e.Timestamp, Norm(e.UserText)));
                clean.Add(e with { ScreenContext = ScreenOnly(e.ScreenContext), Kind = kind });
            }
            clean.AddRange(fresh.Where(x => x.Kind is not null));
            clean.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
            var jsonl = new StringBuilder();
            foreach (var e in clean) jsonl.Append(JsonSerializer.Serialize(e, JsonOpts)).AppendLine();
            SafeFile.Write(target, jsonl.ToString(), snapshot: false);
            File.WriteAllText(MdPath(date), RenderMd(date, clean), new UTF8Encoding(false));
            if (noiseLines.Length > 0)
            {
                Directory.CreateDirectory(Path.Combine(_dir, "noise"));
                File.AppendAllText(Path.Combine(_dir, "noise", $"{date:yyyy-MM-dd}.log"), noiseLines.ToString());
            }
            total += clean.Count;
        }
        var archive = Path.Combine(_dir, "archive_v1");
        Directory.CreateDirectory(archive);
        foreach (var f in Directory.GetFiles(dataDir, "flight_log_*.*"))
            File.Move(f, Path.Combine(archive, Path.GetFileName(f)), overwrite: true);
        if (Directory.Exists(backup)) Directory.Move(backup, Path.Combine(archive, "flight_backup"));
        Log.Write($"журнал: старые журналы собраны в data\\journal — дней {days.Count}, записей {total}, фона убрано {noise}; оригиналы в journal\\archive_v1");
    }

    /// <summary>Из старого контекста записи — только то, что было на экране (всё остальное там — подмешанная память).</summary>
    private static string? ScreenOnly(string? context)
    {
        if (string.IsNullOrEmpty(context)) return null;
        foreach (var head in new[] { "(Ты посмотрела на экран друга.", "(Краем глаза:" })
        {
            var at = context.IndexOf(head, StringComparison.Ordinal);
            if (at >= 0) return Short(OneLine(context[at..].TrimStart('(')), 300);
        }
        return null;
    }

    /// <summary>Весь день в виде для глаз: по часам.</summary>
    private static string RenderMd(DateTime day, List<JournalEntry> entries)
    {
        var sb = new StringBuilder(MdTitle(day));
        var recent = new List<(DateTime At, string Norm)>();
        var hour = -1;
        foreach (var e in entries)
        {
            if (e.Timestamp.Hour != hour)
            {
                hour = e.Timestamp.Hour;
                sb.AppendLine().AppendLine($"## {hour:D2}:00").AppendLine();
            }
            var kind = KindOf(e);
            if (kind == Said) recent.Add((e.Timestamp, Norm(e.UserText)));
            sb.Append(MdLines(e, covered: kind == Turn || kind == "" && Covered(e.UserText.Trim(), recent, e.Timestamp)));
        }
        return sb.ToString();
    }
}
