using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Сегодняшние реплики под рукой: пока банк памяти не разложил свежий кусок журнала по эпизодам (он ждёт паузы), на
/// «помнишь, что я говорил про…» отвечает этот индекс — разреженные векторы слов и буквенных троек по каждой реплике
/// чистой хронологии дня (FlightJournal.Transcript), косинусная близость. Без внешних баз, на чистом .NET. Отдаёт
/// найденное записями со временем (<see cref="Recall"/>) — в общий список воспоминаний, а не отдельным абзацем.
/// </summary>
internal sealed class LightMemory
{
    private readonly FlightJournal _journal;
    private readonly List<(Utterance Line, Dictionary<int, float> Vector, double Magnitude)> _index = new();
    private readonly object _lock = new();
    private DateTime _day;

    // вопрос к памяти о сказанном
    private static readonly Regex MemoryIntentRx = new(
        @"(помнишь|вспомни|о ч[её]м (мы )?(базарили|т[её]рли|говорили|болтали)|что (ты |я |он |она )?(говорил|говорила|сказал|сказала)|кто (сказал|говорил)|куда (я |мы |он |она )?(положил|дела|девал)|ранее|раньше|про что мы|повтори что)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public LightMemory(FlightJournal journal)
    {
        _journal = journal;
        RebuildIndex();
    }

    /// <summary>Перестроить индекс по сегодняшней хронологии (запуск, новый день).</summary>
    public void RebuildIndex()
    {
        lock (_lock)
        {
            _index.Clear();
            _day = DateTime.Today;
            foreach (var u in _journal.Transcript(_day)) Add(u);
        }
    }

    /// <summary>Записи журнала только что добавлены — их реплики в индекс.</summary>
    public void IndexTurn(JournalEntry entry)
    {
        lock (_lock)
        {
            if (_day != DateTime.Today) { RebuildIndex(); return; }
            // слова людей к ответу уже проиндексированы, когда их услышали; новое здесь — её ответ и написанное
            foreach (var u in FlightJournal.Transcript([entry])) Add(u);
        }
    }

    private void Add(Utterance u)
    {
        if (u.Hint || u.Text.Length < 6) return;
        var vec = Vectorize($"{u.Who} {u.Text}".ToLowerInvariant());
        var mag = Magnitude(vec);
        if (mag > 0) _index.Add((u, vec, mag));
    }

    /// <summary>Реплика — вопрос к памяти о сказанном?</summary>
    public static bool HasMemoryIntent(string userText) => MemoryIntentRx.IsMatch(userText);

    /// <summary>Самые близкие к вопросу сегодняшние реплики — записями со временем (до topK, по времени).</summary>
    public List<Recall> Search(string query, int topK = 3)
    {
        var qLower = query.ToLowerInvariant();
        var qVec = Vectorize(qLower);
        var qMag = Magnitude(qVec);
        if (qMag <= 0) return [];
        var results = new List<(Utterance Line, double Score)>();
        lock (_lock)
        {
            foreach (var (line, vec, mag) in _index)
            {
                var score = DotProduct(qVec, vec) / (qMag * mag);
                // спросили по имени («что говорила Настя») — реплики этого человека ближе
                if (line.Who.Length > 2 && qLower.Contains(line.Who.Split(' ')[0].ToLowerInvariant())) score += 0.25;
                if (score >= 0.28) results.Add((line, score));
            }
        }
        return results.OrderByDescending(r => r.Score).Take(topK).OrderBy(r => r.Line.Time)
            .Select(r => new Recall(r.Line.Time, r.Line.Klava ? "ты сказала" : $"{r.Line.Who} сказал(а)",
                $"«{(r.Line.Text.Length > 220 ? r.Line.Text[..220] + "…" : r.Line.Text)}»", $"line:{r.Line.Time.Ticks}"))
            .ToList();
    }

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "а", "и", "в", "на", "с", "по", "у", "к", "о", "об", "за", "из", "от", "до", "ну", "да", "нет",
        "что", "это", "как", "так", "я", "ты", "мы", "он", "она", "они", "было", "быть", "бля", "нах", "ща", "чё",
        "помнишь", "вспомни", "говорил", "говорила", "сказал", "сказала",
    };

    // слова и буквенные тройки, хеш в 4096 корзин (свой хеш: string.GetHashCode в каждом запуске другой)
    private const int Dim = 4096;

    private static int Bucket(string s)
    {
        var h = 2166136261u;
        foreach (var c in s) { h ^= c; h *= 16777619u; }
        return (int)(h % Dim);
    }

    private static Dictionary<int, float> Vectorize(string text)
    {
        var dict = new Dictionary<int, float>();
        foreach (var w in Regex.Split(text.Replace('ё', 'е'), @"[^\p{L}\d]+"))
        {
            if (w.Length < 2 || StopWords.Contains(w)) continue;
            var h = Bucket(w);
            dict[h] = dict.GetValueOrDefault(h) + 2.0f;
            // буквенные тройки — устойчивость к падежам и ослышкам («ключ», «ключи»)
            for (var i = 0; i + 3 <= w.Length; i++)
            {
                var th = Bucket(w.Substring(i, 3));
                dict[th] = dict.GetValueOrDefault(th) + 1.0f;
            }
        }
        return dict;
    }

    private static float DotProduct(Dictionary<int, float> a, Dictionary<int, float> b)
    {
        var sum = 0f;
        foreach (var (k, v) in a)
            if (b.TryGetValue(k, out var bv)) sum += v * bv;
        return sum;
    }

    private static double Magnitude(Dictionary<int, float> vec)
    {
        var sum = 0.0;
        foreach (var v in vec.Values) sum += v * v;
        return Math.Sqrt(sum);
    }
}
