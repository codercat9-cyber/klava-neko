using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>Одно воспоминание в подмес: когда было (null — знание без даты), что за запись, текст, ключ от повторов.</summary>
internal sealed record Recall(DateTime? When, string Kind, string Text, string Key);

/// <summary>
/// Ось времени памяти (друг 01.10: «должна по датам и по часам всё помнить как часовой, а у неё воспоминания вразнобой
/// всплывают как у шизофреника»). Причина разнобоя была простая: мозгу никто не говорил, который час и какое число, а
/// воспоминания пяти разных хранилищ подмешивались каждое своим абзацем и без «когда» — факт трёхдневной давности
/// выглядел так же, как сказанное минуту назад. Здесь — общие часы и общий вид: у каждой записи метка времени
/// («сегодня 13:05», «вчера 21:40», «пн 28.09 15:10»), всё к слову — одним списком от давнего к свежему.
/// </summary>
internal static class Chronicle
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Метка времени относительно «сейчас»: сегодня 13:05 · вчера 21:40 · позавчера 02:10 · пн 28.09 15:10.</summary>
    public static string Stamp(DateTime t, DateTime now) => $"{DayStamp(t, now)} {t:HH:mm}";

    public static string DayStamp(DateTime t, DateTime now) => (now.Date - t.Date).Days switch
    {
        0 => "сегодня",
        1 => "вчера",
        2 => "позавчера",
        _ => t.Year == now.Year ? t.ToString("ddd dd.MM", Ru) : t.ToString("ddd dd.MM.yyyy", Ru),
    };

    /// <summary>День полностью: «вчера, 30 сентября (ср)».</summary>
    public static string DayFull(DateTime day, DateTime now)
    {
        var date = day.ToString("d MMMM", Ru) + $" ({day.ToString("ddd", Ru)})";
        return (now.Date - day.Date).Days switch { 0 => "сегодня, " + date, 1 => "вчера, " + date, 2 => "позавчера, " + date, _ => date };
    }

    public static string Span(TimeSpan s)
    {
        if (s < TimeSpan.Zero) s = TimeSpan.Zero;
        if (s.TotalMinutes < 1) return "меньше минуты";
        if (s.TotalHours < 1) return $"{(int)s.TotalMinutes} мин";
        if (s.TotalHours < 24) return s.Minutes > 0 ? $"{(int)s.TotalHours} ч {s.Minutes} мин" : $"{(int)s.TotalHours} ч";
        return s.Hours > 0 ? $"{(int)s.TotalDays} дн {s.Hours} ч" : $"{(int)s.TotalDays} дн";
    }

    /// <summary>Сейчас словами: «четверг, 1 октября 2026, 14:35».</summary>
    public static string NowText(DateTime now) => $"{now.ToString("dddd, d MMMM yyyy", Ru)}, {now:HH:mm}";

    /// <summary>
    /// Воспоминания к слову одним блоком: с датой — по времени от давнего к свежему, без даты (знание о человеке) — в
    /// конце; одинаковые не повторяются. null — нечего.
    /// </summary>
    public static string? Compose(IEnumerable<Recall> items, DateTime now)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<Recall>();
        foreach (var r in items)
        {
            var text = r.Text.Trim();
            if (text.Length == 0) continue;
            if (!seen.Add(r.Key) || !seen.Add("t:" + (text.Length > 60 ? text[..60] : text).ToLowerInvariant())) continue;
            list.Add(r with { Text = text });
        }
        if (list.Count == 0) return null;
        var sb = new StringBuilder("(Память к слову. У каждой записи стоит, КОГДА это было, — не путай дни и не выдавай давнее за " +
                                   "сегодняшнее. Не пересказывай, просто знай; вспомни вслух, только если к месту:\n");
        foreach (var r in list.Where(x => x.When is not null).OrderBy(x => x.When))
            sb.Append("• ").Append(Stamp(r.When!.Value, now)).Append(" — ").Append(r.Kind.Length > 0 ? r.Kind + ": " : "").AppendLine(r.Text);
        foreach (var r in list.Where(x => x.When is null))
            sb.Append("• ").Append(r.Kind.Length > 0 ? r.Kind + ": " : "").AppendLine(r.Text);
        return sb.Append(')').ToString();
    }
}

/// <summary>Отрезок времени, о котором спрашивают. Narrow — узкий (час-два): можно показать сами реплики журнала.</summary>
internal sealed record TimeWindow(DateTime From, DateTime To, bool Narrow, bool WholeDays)
{
    private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");

    /// <summary>Дни, которые отрезок задевает.</summary>
    public IEnumerable<DateTime> Days()
    {
        for (var d = From.Date; d < To; d = d.AddDays(1)) yield return d;
    }

    public bool Overlaps(DateTime from, DateTime to) => from < To && to >= From;

    public string Label(DateTime now)
    {
        if (WholeDays)
            return To - From <= TimeSpan.FromDays(1) ? Chronicle.DayFull(From, now)
                : $"{From.ToString("d MMMM", Ru)} — {To.AddMinutes(-1).ToString("d MMMM", Ru)}";
        var end = To.Date > From.Date && To.TimeOfDay == TimeSpan.Zero ? "24:00"
            : To.Date > From.Date ? $"{To:HH:mm} ({Chronicle.DayStamp(To, now)})" : $"{To:HH:mm}";
        return $"{Chronicle.DayFull(From, now)}, {From:HH:mm}–{end}";
    }
}

/// <summary>
/// Слова о времени → отрезок: «вчера», «в пятницу», «28 сентября», «27.09», «утром», «вчера вечером», «в три часа дня»,
/// «около двух», «в 15:30», «с двух до четырёх», «час назад», «минут двадцать назад», «только что». null — про время
/// не сказано. Часы без «дня/ночи» понимаются как ближайшие прошедшие.
/// </summary>
internal static partial class TimeWords
{
    private static readonly string[] WeekDays = [@"воскресень\w*", @"понедельник\w*", @"вторник\w*", @"сред[уыае]\b", @"четверг\w*", @"пятниц\w*", @"суббот\w*"];
    private static readonly string[] Months = ["январ", "феврал", "март", "апрел", "ма", "июн", "июл", "август", "сентябр", "октябр", "ноябр", "декабр"];

    private static readonly Dictionary<string, double> Numbers = new(StringComparer.Ordinal)
    {
        ["пол"] = 0.5, ["полтора"] = 1.5, ["полторы"] = 1.5, ["пару"] = 2, ["пара"] = 2, ["один"] = 1, ["одну"] = 1, ["одного"] = 1,
        ["час"] = 1, ["часа"] = 1, ["часу"] = 1, ["два"] = 2, ["две"] = 2, ["двух"] = 2, ["три"] = 3, ["трех"] = 3, ["четыре"] = 4,
        ["четырех"] = 4, ["пять"] = 5, ["пяти"] = 5, ["шесть"] = 6, ["шести"] = 6, ["семь"] = 7, ["семи"] = 7, ["восемь"] = 8,
        ["восьми"] = 8, ["девять"] = 9, ["девяти"] = 9, ["десять"] = 10, ["десяти"] = 10, ["одиннадцать"] = 11, ["одиннадцати"] = 11,
        ["двенадцать"] = 12, ["двенадцати"] = 12, ["пятнадцать"] = 15, ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40,
    };

    private const string Num = @"\d{1,3}|пол|полтора|полторы|пару|пара|один|одну|два|две|три|четыре|пять|шесть|семь|восемь|девять|десять|пятнадцать|двадцать|тридцать|сорок";
    private const string Hour = @"\d{1,2}|часа|часу|час|два|двух|три|трех|четыре|четырех|пять|пяти|шесть|шести|семь|семи|восемь|восьми|девять|девяти|десять|десяти|одиннадцать|одиннадцати|двенадцать|двенадцати";
    private const string Part = @"утра|дня|вечера|ночи";

    private static double? Number(string s) =>
        s.Length == 0 ? null : double.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : Numbers.TryGetValue(s, out var w) ? w : null;

    public static TimeWindow? Parse(string text, DateTime now)
    {
        var t = text.ToLowerInvariant().Replace('ё', 'е');

        // «час назад», «2 часа назад», «минут двадцать назад», «полчаса назад»
        if (AgoRx().Match(t) is { Success: true } ago)
        {
            var unit = ago.Groups["u"].Value;
            var n = Number(ago.Groups["n"].Value) ?? Number(ago.Groups["n2"].Value) ?? 1;
            var span = unit.StartsWith("полчас", StringComparison.Ordinal) || unit.StartsWith("получас", StringComparison.Ordinal) ? TimeSpan.FromMinutes(30)
                : unit.StartsWith("час", StringComparison.Ordinal) ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n);
            if (span <= TimeSpan.FromHours(36))
            {
                var half = TimeSpan.FromMinutes(Math.Clamp(span.TotalMinutes * 0.3, 10, 45));
                var center = now - span;
                var to = center + half;
                return new TimeWindow(center - half, to > now ? now : to, Narrow: true, WholeDays: false);
            }
        }
        if (JustNowRx().IsMatch(t)) return new TimeWindow(now.AddMinutes(-20), now, Narrow: true, WholeDays: false);

        var (dayFrom, dayTo, daySaid) = DaysOf(t, now.Date);
        var single = dayTo - dayFrom == TimeSpan.FromDays(1);

        // «с двух до четырёх», «с 14 до 16:30»
        if (single && RangeRx().Match(t) is { Success: true } rg && Number(rg.Groups["a"].Value) is { } ha && Number(rg.Groups["b"].Value) is { } hb && ha <= 24 && hb <= 24)
        {
            var part = rg.Groups["p"].Value;
            var b = Resolve((int)hb, Minutes(rg.Groups["bm"].Value), part, dayFrom, now, daySaid);
            var a = dayFrom.AddHours(((int)ha + (b.Hour >= 12 && ha < 12 && ha + 12 <= b.Hour ? 12 : 0)) % 24).AddMinutes(Minutes(rg.Groups["am"].Value));
            if (a < b) return Shift(new TimeWindow(a, b, Narrow: b - a <= TimeSpan.FromHours(3), WholeDays: false), now, daySaid);
        }
        // «в 15:30», «около трёх часов дня», «часа в три», «в полдень»
        if (single && ClockOf(t) is var (h, m, p))
        {
            var at = Resolve(h, m, p, dayFrom, now, daySaid);
            return Shift(new TimeWindow(at.AddMinutes(-30), at.AddMinutes(60), Narrow: true, WholeDays: false), now, daySaid);
        }
        if (single)
        {
            (int From, int To)? hours = MorningRx().IsMatch(t) ? (5, 12) : LunchRx().IsMatch(t) ? (12, 15) : AfterLunchRx().IsMatch(t) ? (14, 18)
                : NoonRx().IsMatch(t) ? (12, 18) : EveningRx().IsMatch(t) ? (17, 24) : null;
            if (hours is { } hr)
                return Shift(new TimeWindow(dayFrom.AddHours(hr.From), dayFrom.AddHours(hr.To), Narrow: false, WholeDays: false), now, daySaid);
            if (NightRx().IsMatch(t))
            {
                // «сегодня ночью» — с полуночи; «вчера ночью» и ночь другого дня — ночь после того вечера
                if (dayFrom == now.Date) return new TimeWindow(dayFrom.AddHours(-2), dayFrom.AddHours(6), Narrow: false, WholeDays: false);
                return new TimeWindow(dayFrom.AddHours(22), dayFrom.AddHours(30), Narrow: false, WholeDays: false);
            }
        }
        return daySaid ? new TimeWindow(dayFrom, dayTo, Narrow: false, WholeDays: true) : null;
    }

    private static int Minutes(string s) => int.TryParse(s, out var m) && m < 60 ? m : 0;

    /// <summary>День не назван, а время ещё не наступило («утром» в три ночи) — значит, это было вчера.</summary>
    private static TimeWindow Shift(TimeWindow w, DateTime now, bool daySaid) =>
        !daySaid && w.From > now ? w with { From = w.From.AddDays(-1), To = w.To.AddDays(-1) } : w;

    private static (int H, int M, string Part)? ClockOf(string t)
    {
        if (t.Contains("в полдень", StringComparison.Ordinal)) return (12, 0, "дня");
        if (t.Contains("в полночь", StringComparison.Ordinal)) return (0, 0, "ночи");
        if (HhMmRx().Match(t) is { Success: true } hm && int.Parse(hm.Groups["h"].Value) < 24)
            return (int.Parse(hm.Groups["h"].Value), Minutes(hm.Groups["m"].Value), hm.Groups["p"].Value);
        foreach (var rx in new[] { HourRx(), HourBackRx(), AboutRx() })
            if (rx.Match(t) is { Success: true } m && Number(m.Groups["h"].Value) is { } h && h <= 24)
                return ((int)h % 24, 0, m.Groups["p"].Value);
        return null;
    }

    /// <summary>Час → момент в этом дне. Без «дня/ночи»: сегодня — последний такой час, что уже прошёл; в другой день — дневной.</summary>
    private static DateTime Resolve(int h, int m, string part, DateTime day, DateTime now, bool daySaid)
    {
        switch (part)
        {
            case "утра": return day.AddHours(h == 12 ? 0 : h).AddMinutes(m);
            case "дня": return day.AddHours(h < 12 && h <= 6 ? h + 12 : h).AddMinutes(m);
            case "вечера": return day.AddHours(h < 12 ? h + 12 : h).AddMinutes(m);
            case "ночи": return day.AddHours(h == 12 ? 0 : h >= 8 && h < 12 ? h + 12 : h).AddMinutes(m);
        }
        if (h >= 13 || h == 0) return day.AddHours(h).AddMinutes(m);
        var early = day.AddHours(h == 12 ? 0 : h).AddMinutes(m);
        var late = day.AddHours(h == 12 ? 12 : h + 12).AddMinutes(m);
        if (day == now.Date) return late <= now ? late : early <= now || daySaid ? early : late;
        return h <= 6 || h == 12 ? late : early;
    }

    /// <summary>О каких днях речь: (с, по, сказано ли). Не сказано — сегодня.</summary>
    internal static (DateTime From, DateTime To, bool Said) DaysOf(string t, DateTime today)
    {
        (DateTime, DateTime, bool) One(DateTime d) => (d, d.AddDays(1), true);
        if (t.Contains("позавчера", StringComparison.Ordinal)) return One(today.AddDays(-2));
        if (t.Contains("вчера", StringComparison.Ordinal)) return One(today.AddDays(-1));
        if (t.Contains("на прошлой неделе", StringComparison.Ordinal)) return (today.AddDays(-7), today, true);
        if (LatelyRx().IsMatch(t)) return (today.AddDays(-3), today.AddDays(1), true);
        if (DayMonthRx().Match(t) is { Success: true } dm)
        {
            var month = Array.FindIndex(Months, x => dm.Groups["m"].Value.StartsWith(x, StringComparison.Ordinal)) + 1;
            var d = int.Parse(dm.Groups["d"].Value);
            if (month > 0 && d >= 1 && d <= DateTime.DaysInMonth(today.Year, month))
            {
                var date = new DateTime(today.Year, month, d);
                return One(date > today ? date.AddYears(-1) : date);
            }
        }
        if (DotDateRx().Match(t) is { Success: true } dd && int.Parse(dd.Groups["m"].Value) is var mm and >= 1 and <= 12
            && int.Parse(dd.Groups["d"].Value) is var d2 and >= 1 && d2 <= DateTime.DaysInMonth(today.Year, mm))
        {
            var date = new DateTime(today.Year, mm, d2);
            return One(date > today ? date.AddYears(-1) : date);
        }
        if (NthRx().Match(t) is { Success: true } nth && int.Parse(nth.Groups["d"].Value) is var d3 and >= 1 and <= 31)
        {
            var date = today;
            for (var back = 0; back < 62 && date.Day != d3; back++) date = date.AddDays(-1);
            if (date.Day == d3) return One(date);
        }
        for (var i = 0; i < 7; i++)
            if (Regex.Match(t, $@"\b(?:в|во)\s+(?<last>прошл\w+\s+)?{WeekDays[i]}") is { Success: true } wd)
            {
                var back = ((int)today.DayOfWeek - i + 7) % 7;
                if (back == 0 && wd.Groups["last"].Success) back = 7;
                return One(today.AddDays(-back));
            }
        return (today, today.AddDays(1), t.Contains("сегодня", StringComparison.Ordinal));
    }

    [GeneratedRegex($@"(?:\b(?<n>{Num})\s+)?\b(?<u>полчаса|получас\w*|час\w*|минут\w*)(?:\s+(?<n2>{Num}))?\s+(?:тому\s+)?назад")]
    private static partial Regex AgoRx();
    [GeneratedRegex(@"только что|только-только|минуту назад|секунду назад|перед этим|до этого|до того|сейчас вот")]
    private static partial Regex JustNowRx();
    [GeneratedRegex($@"\bс\s+(?<a>{Hour})(?:[:.](?<am>\d{{2}}))?(?:\s+час\w*)?\s+(?:до|по)\s+(?<b>{Hour})(?:[:.](?<bm>\d{{2}}))?(?:\s+час\w*)?(?:\s+(?<p>{Part}))?")]
    private static partial Regex RangeRx();
    [GeneratedRegex($@"\b(?:в|около|к|после)\s+(?<h>\d{{1,2}})[:.](?<m>\d{{2}})\b(?:\s+(?<p>{Part}))?")]
    private static partial Regex HhMmRx();
    // «в три часа», «в 3 часа дня», «в пять вечера», «к двум часам ночи»
    [GeneratedRegex($@"\b(?:в|к|около|после)\s+(?<h>{Hour})(?:\s+час\w*(?:\s+(?<p>{Part}))?|\s+(?<p>{Part}))\b")]
    private static partial Regex HourRx();
    // «часа в три», «часов в пять вечера», «часа в 3»
    [GeneratedRegex($@"\bчас\w*\s+(?:в|около)\s+(?<h>{Hour})\b(?:\s+(?<p>{Part}))?")]
    private static partial Regex HourBackRx();
    // «около трёх», «около двух ночи»
    [GeneratedRegex($@"\bоколо\s+(?<h>часа|часу|двух|трех|четырех|пяти|шести|семи|восьми|девяти|десяти|одиннадцати|двенадцати)\b(?:\s+(?<p>{Part}))?")]
    private static partial Regex AboutRx();
    [GeneratedRegex(@"\bутром\b|\bс утра\b|\bпод утро\b|\bпоутру\b")]
    private static partial Regex MorningRx();
    [GeneratedRegex(@"\bв обед\b")]
    private static partial Regex LunchRx();
    [GeneratedRegex(@"\bпосле обеда\b")]
    private static partial Regex AfterLunchRx();
    [GeneratedRegex(@"\bднем\b")]
    private static partial Regex NoonRx();
    [GeneratedRegex(@"\bвечером\b|\bпод вечер\b|\bк вечеру\b")]
    private static partial Regex EveningRx();
    [GeneratedRegex(@"\bночью\b|\bза ночь\b")]
    private static partial Regex NightRx();
    [GeneratedRegex(@"\bна днях\b|\bнедавно\b|\bв последние дни\b")]
    private static partial Regex LatelyRx();
    [GeneratedRegex(@"\b(?<d>\d{1,2})(?:-?го)?\s+(?<m>январ|феврал|март|апрел|ма[яй]|июн|июл|август|сентябр|октябр|ноябр|декабр)")]
    private static partial Regex DayMonthRx();
    [GeneratedRegex(@"\b(?<d>\d{1,2})\.(?<m>\d{1,2})\b(?!\.\d)")]
    private static partial Regex DotDateRx();
    [GeneratedRegex(@"\b(?<d>\d{1,2})-?го(?:\s+числа)?\b|\b(?<d>\d{1,2})\s+числа\b")]
    private static partial Regex NthRx();
}

/// <summary>
/// Имена людей в одном виде. В памяти один и тот же человек лежал под пятью именами («Серый Волк», «Волк», «Гена»,
/// «Волк (Гена)»; «Маруся», «Баруся»), и воспоминания о нём не сходились. Канон — имя голосового профиля
/// (data\speakers); к нему ведут слова имени («Волк»), склонения, ослышки whisper в одну букву и прозвища из
/// data\memory\people.json (она дописывает их сама: memory action:'alias', или из записи вида «Волк (Гена)»).
/// </summary>
internal static class People
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> Skip = new(StringComparer.Ordinal) { "или", "она", "они", "нахуй", "бля", "блять", "the", "and" };
    private static List<string> _profiles = [];
    private static Dictionary<string, List<string>> _aliases = new(StringComparer.Ordinal);
    private static Dictionary<string, string> _byKey = new(StringComparer.Ordinal);
    private static string? _file;

    public static void Load(KlavaConfig cfg)
    {
        var names = new List<string>(cfg.RoomMembers);
        var dir = cfg.Abs(@"data\speakers");
        if (Directory.Exists(dir))
        {
            names.AddRange(Directory.GetDirectories(dir).Select(Path.GetFileName)!);
            names.AddRange(Directory.GetFiles(dir, "*.wav").Select(Path.GetFileNameWithoutExtension)!);
        }
        Load(names, cfg.Abs(@"data\memory\people.json"));
    }

    /// <param name="file">Файл прозвищ (null — без него: проверки).</param>
    internal static void Load(IEnumerable<string> profiles, string? file)
    {
        lock (Gate)
        {
            _file = file;
            var saved = file is null ? null : SafeFile.Load(file, t => JsonSerializer.Deserialize<Dictionary<string, List<string>>>(t));
            _aliases = saved is null ? new(StringComparer.Ordinal) : new(saved, StringComparer.Ordinal);
            _profiles = profiles.Where(p => !string.IsNullOrWhiteSpace(p) && !p.StartsWith('_') && !p.Contains('.') && p != "Клава")
                .Concat(_aliases.Keys).Select(p => p.Trim()).Distinct(StringComparer.Ordinal).ToList();
            Rebuild();
        }
    }

    private static string Key(string name) =>
        string.Join(' ', name.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(MemoryGraph.Stem));

    private static void Rebuild()
    {
        _byKey = new(StringComparer.Ordinal);
        var words = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var p in _profiles)
        {
            _byKey[Key(p)] = p;
            foreach (var w in p.Split('_', ' ').Where(w => w.Length >= 3 && !Skip.Contains(w.ToLowerInvariant())))
                (words.TryGetValue(Key(w), out var set) ? set : words[Key(w)] = new(StringComparer.Ordinal)).Add(p);
        }
        // слово имени ведёт к человеку, только если оно такое одно на всех («Настя» у двоих — ни к кому)
        foreach (var (k, set) in words)
            if (set.Count == 1) _byKey.TryAdd(k, set.First());
        foreach (var (p, list) in _aliases)
            foreach (var a in list)
                if (Key(a).Length >= 2) _byKey[Key(a)] = p;
    }

    /// <summary>Профиль человека по любому его имени; null — не знаю такого.</summary>
    public static string? Profile(string name)
    {
        var n = name.Replace('_', ' ').Trim();
        if (n.Length == 0) return null;
        lock (Gate)
        {
            // «Волк (Гена)»: одно из имён знакомо — второе запоминается прозвищем
            var open = n.IndexOf('(');
            if (open > 0 && n.EndsWith(')'))
            {
                string a = n[..open].Trim(), b = n[(open + 1)..^1].Trim();
                var pa = Lookup(a);
                var pb = Lookup(b);
                if (pa is not null && pb is null && IsName(b)) LearnLocked(b, pa);
                if (pb is not null && pa is null && IsName(a)) LearnLocked(a, pb);
                return pa ?? pb;
            }
            return Lookup(n);
        }
    }

    private static bool IsName(string s) => s.Length is >= 3 and <= 20 && !s.Contains(' ') && char.IsUpper(s[0]);

    private static string? Lookup(string n)
    {
        var k = Key(n);
        if (_byKey.TryGetValue(k, out var p)) return p;
        if (n.Contains(' '))
        {
            // «Лиса Лис» — часть длинного имени профиля: все слова ведут к одному человеку
            var hits = n.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3 && !Skip.Contains(w.ToLowerInvariant()))
                .Select(w => _byKey.GetValueOrDefault(Key(w))).Distinct().ToList();
            return hits.Count == 1 ? hits[0] : null;
        }
        if (n.Length < 5) return null;
        // ослышка в одну букву: «Баруся» → «Маруся»
        string? found = null;
        foreach (var (key, prof) in _byKey)
        {
            if (key.Contains(' ') || key.Length < 4 || Math.Abs(key.Length - k.Length) > 1 || Distance(key, k) > 1) continue;
            if (found is not null && found != prof) return null;
            found = prof;
        }
        return found;
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1], prev[j]) + 1, prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>Имя в одном виде: знакомый — именем профиля («Кибер Кот»), «фон (…)» — пусто, незнакомый — как есть.</summary>
    public static string Canon(string name)
    {
        var n = name.Replace('_', ' ').Trim();
        if (n.Length == 0 || n.StartsWith("фон", StringComparison.OrdinalIgnoreCase) && (n.Length == 3 || n[3] is ' ' or '(')) return "";
        if (n.Equals("клава", StringComparison.OrdinalIgnoreCase)) return "Клава";
        return Profile(n) is { } p ? p.Replace('_', ' ') : char.ToUpper(n[0]) + n[1..];
    }

    /// <summary>Список людей в одном виде, без повторов и без фона.</summary>
    public static List<string> CanonList(IEnumerable<string> names) =>
        names.Select(Canon).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>Запомнить прозвище: alias → человек who. false — who не знаю.</summary>
    public static bool Learn(string alias, string who)
    {
        lock (Gate)
        {
            if (Lookup(who.Replace('_', ' ').Trim()) is not { } p) return false;
            LearnLocked(alias.Trim(), p);
            return true;
        }
    }

    private static void LearnLocked(string alias, string profile)
    {
        if (alias.Length < 2 || Lookup(alias) == profile) return;
        if (!_aliases.TryGetValue(profile, out var list)) _aliases[profile] = list = [];
        list.Add(alias);
        Rebuild();
        Log.Write($"люди: «{alias}» — это {profile.Replace('_', ' ')}");
        if (_file is null) return;
        try
        {
            SafeFile.Write(_file, JsonSerializer.Serialize(_aliases, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("люди: people.json не записался", ex); }
    }
}

/// <summary>
/// То, что должно переживать перезапуск, кроме самих воспоминаний (друг 01.10: «каждый перезапуск на него влияет»):
/// когда её запускали и выключали (часы: «ты была выключена с 22:40 вчера») и что она уже вспоминала вслух — раньше
/// это жило в оперативке, и после каждого запуска она заново вываливала вчерашний итог и те же воспоминания.
/// data\memory\state.json.
/// </summary>
internal sealed class MemoryState
{
    public static MemoryState? Current { get; set; }

    internal sealed class Session
    {
        public DateTime Start { get; set; }
        public DateTime? End { get; set; }
        /// <summary>Вышла сама (а не убили процесс / погас свет).</summary>
        public bool Clean { get; set; }
    }

    internal sealed class Data
    {
        public List<Session> Sessions { get; set; } = [];
        public Dictionary<string, DateTime> Told { get; set; } = new(StringComparer.Ordinal);
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly string _file;
    private readonly object _lock = new();
    private readonly Data _data;
    private readonly object _disk = new();
    private int _saving;

    public MemoryState(string dir)
    {
        _file = Path.Combine(dir, "state.json");
        _data = SafeFile.Load(_file, t => JsonSerializer.Deserialize<Data>(t, Json)) ?? new Data();
    }

    /// <summary>Запуск: прошлый сеанс без конца — оборвался (конец — последняя запись журнала); начинается новый.</summary>
    public void Begin(DateTime? lastJournal)
    {
        lock (_lock)
        {
            var now = DateTime.Now;
            if (_data.Sessions.LastOrDefault() is { End: null } open)
                open.End = lastJournal is { } lj && lj > open.Start ? lj : open.Start;
            // до появления state.json она тоже жила: прошлый сеанс — по журналу
            else if (_data.Sessions.Count == 0 && lastJournal is { } first)
                _data.Sessions.Add(new Session { Start = first, End = first, Clean = true });
            _data.Sessions.Add(new Session { Start = now });
            if (_data.Sessions.Count > 400) _data.Sessions.RemoveRange(0, _data.Sessions.Count - 400);
            foreach (var k in _data.Told.Where(kv => now - kv.Value > TimeSpan.FromDays(30)).Select(kv => kv.Key).ToList()) _data.Told.Remove(k);
        }
        SaveNow();
    }

    /// <summary>Выход по-хорошему — сразу на диск.</summary>
    public void End()
    {
        lock (_lock)
            if (_data.Sessions.LastOrDefault() is { End: null } s) { s.End = DateTime.Now; s.Clean = true; }
        SaveNow();
    }

    public DateTime Started { get { lock (_lock) return _data.Sessions.LastOrDefault()?.Start ?? DateTime.Now; } }

    /// <summary>Сеанс перед нынешним.</summary>
    public (DateTime Start, DateTime End, bool Clean)? Previous
    {
        get
        {
            lock (_lock)
                return _data.Sessions.Count >= 2 && _data.Sessions[^2] is { End: { } end } p ? (p.Start, end, p.Clean) : null;
        }
    }

    /// <summary>Запуски и выходы за день — для «когда тебя включали».</summary>
    public List<(DateTime Start, DateTime? End, bool Clean)> SessionsOf(DateTime day)
    {
        lock (_lock)
            return _data.Sessions.Where(s => s.Start.Date == day.Date || s.End?.Date == day.Date).Select(s => (s.Start, s.End, s.Clean)).ToList();
    }

    /// <summary>Это ещё не вспоминала (или вспоминала давно)?</summary>
    public bool Fresh(string id, TimeSpan cool)
    {
        lock (_lock) return !_data.Told.TryGetValue(id, out var t) || DateTime.Now - t > cool;
    }

    public void MarkTold(string id)
    {
        lock (_lock) _data.Told[id] = DateTime.Now;
        if (Interlocked.Exchange(ref _saving, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(2000).ConfigureAwait(false);
            Interlocked.Exchange(ref _saving, 0);
            SaveNow();
        });
    }

    private void SaveNow()
    {
        try
        {
            string text;
            lock (_lock) text = JsonSerializer.Serialize(_data, Json);
            lock (_disk) SafeFile.Write(_file, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("память: state.json не записался", ex); }
    }

    /// <summary>
    /// Часы в реплику: число, день недели, время; первые минуты после запуска — когда запустили и сколько была
    /// выключена; после долгой паузы — когда с ней говорили в последний раз.
    /// </summary>
    public string ClockNote(DateTime now, DateTime? lastTalk)
    {
        var sb = new StringBuilder($"(Часы: сейчас {Chronicle.NowText(now)}.");
        var started = Started;
        if (now - started < TimeSpan.FromMinutes(15))
        {
            sb.Append($" Тебя запустили в {started:HH:mm}");
            if (Previous is { } prev)
                sb.Append($"; до этого ты спала (была выключена) с {Chronicle.Stamp(prev.End, now)} — {Chronicle.Span(started - prev.End)}" +
                          (prev.Clean ? "" : ", в тот раз тебя выключили внезапно"));
            sb.Append('.');
        }
        if (lastTalk is { } lt && now - lt > TimeSpan.FromMinutes(20))
            sb.Append($" До этой реплики разговор молчал {Chronicle.Span(now - lt)} — с {Chronicle.Stamp(lt, now)}.");
        return sb.Append(')').ToString();
    }
}
