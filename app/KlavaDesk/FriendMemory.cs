using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KlavaDesk;

/// <summary>
/// Долгая память Клавы о людях (друг 28.09: сделать её живой — компаньон, который помнит). Раньше помнилось только
/// то, что влезло в историю разговора (при переполнении обрезается навсегда), и сегодняшний журнал по прямому
/// «помнишь…» (LightMemory). Здесь:
///  - устойчивые факты о каждом собеседнике (имя голосового профиля): любит/не любит, чем занят, планы, события —
///    их выписывает мозг разовым запросом в фоне каждые <see cref="EveryTurns"/> ходов этого человека и убирает
///    устаревшие;
///  - итог каждого прошедшего дня (3–5 предложений) — по журналу, при первой реплике нового дня.
/// В реплику подмешивается коротко: «(Ты знаешь про Лиса: …)» — самые подходящие к теме факты, и один раз в день
/// итог прошлого дня с его датой (что уже сказано — помнит MemoryState, перезапуск не повторяет). Хранится
/// data/memory/friends.json и days.json — читается глазами и правится руками; запись — SafeFile, с копиями.
/// </summary>
internal sealed class FriendMemory
{
    public const int EveryTurns = 8, MaxFacts = 40, NoteFacts = 6;

    internal sealed class Friend
    {
        public List<string> Facts { get; set; } = [];
        public DateTime Updated { get; set; }
        public int Turns { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly string _dir;
    private readonly Brain? _brain;
    private readonly FlightJournal _journal;
    private readonly object _lock = new();
    private readonly object _disk = new();
    private readonly Dictionary<string, Friend> _friends;
    private readonly Dictionary<string, string> _days;
    private readonly Dictionary<string, int> _since = new(StringComparer.Ordinal);
    private readonly HashSet<string> _busy = new(StringComparer.Ordinal);
    private string _toldYesterday = "";
    private bool _summarizing;

    /// <summary>День эпизодами банка памяти (сырьё итога дня); null — день не разложен, итог соберётся из журнала.</summary>
    public Func<string, string?>? DayDigest { get; set; }

    public FriendMemory(string dir, Brain? brain, FlightJournal journal)
    {
        _dir = dir;
        _brain = brain;
        _journal = journal;
        Directory.CreateDirectory(dir);
        _friends = Load<Dictionary<string, Friend>>("friends.json") ?? new(StringComparer.Ordinal);
        _days = Load<Dictionary<string, string>>("days.json") ?? new(StringComparer.Ordinal);
    }

    private T? Load<T>(string name) where T : class
    {
        return SafeFile.Load(Path.Combine(_dir, name), t => JsonSerializer.Deserialize<T>(t, Json));
    }

    private void Save()
    {
        string friends, days;
        lock (_lock)
        {
            friends = JsonSerializer.Serialize(_friends, Json);
            days = JsonSerializer.Serialize(_days, Json);
        }
        try
        {
            lock (_disk)
            {
                SafeFile.Write(Path.Combine(_dir, "friends.json"), friends);
                SafeFile.Write(Path.Combine(_dir, "days.json"), days);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("память: не записалась", ex); }
    }

    private static string Human(string profile) => profile.Replace('_', ' ').Trim();

    /// <summary>Слова для сравнения с темой: нижний регистр, от 4 букв, обрезаны до 5 (грубая основа).</summary>
    private static HashSet<string> Stems(string s) =>
        s.ToLowerInvariant().Replace('ё', 'е').Split(' ', ',', '.', '!', '?', ':', ';', '-', '«', '»', '(', ')', '"', '\n')
            .Where(w => w.Length >= 4).Select(w => w.Length > 5 ? w[..5] : w).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Подмес к реплике: что она знает о говорящем (подходящее к теме — первым, плюс свежее) и один раз в день итог
    /// последнего прошедшего дня — записью с датой. Итога ещё нет — запускается его сборка (будет к следующим репликам).
    /// </summary>
    public (string? Facts, Recall? LastDay) NoteFor(string? speaker, string userText)
    {
        string? facts = null;
        Recall? last = null;
        var name = string.IsNullOrWhiteSpace(speaker) ? null : speaker;
        lock (_lock)
        {
            if (name is not null && _friends.TryGetValue(name, out var f) && f.Facts.Count > 0)
            {
                var topic = Stems(userText);
                var picked = f.Facts
                    .Select((fact, i) => (fact, i, hit: Stems(fact).Count(topic.Contains)))
                    .OrderByDescending(x => x.hit).ThenByDescending(x => x.i)
                    .Take(NoteFacts).OrderBy(x => x.i).Select(x => x.fact.TrimEnd('.', ' ')).ToList();
                facts = $"(Ты знаешь про {Human(name)} — это знание о человеке, а не события сегодняшнего дня: {string.Join("; ", picked)}. " +
                        "Не пересказывай — просто знай и, если к месту, вспомни.)";
            }
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            if (Yesterday() is { } y && ToldDay != today)
            {
                ToldDay = today;
                last = new Recall(y.Day.AddHours(23).AddMinutes(59), "итог того дня", y.Text, "day:" + y.Day.ToString("yyyy-MM-dd"));
            }
        }
        if (!_summarizing) _ = SummarizePastDaysAsync();
        return (facts, last);
    }

    // день, в который итог прошлого дня уже подмешан, — переживает перезапуск
    private string ToldDay
    {
        get => MemoryState.Current is { } st ? (st.Fresh("lastday:" + DateTime.Today.ToString("yyyy-MM-dd"), TimeSpan.FromDays(2)) ? "" : DateTime.Today.ToString("yyyy-MM-dd")) : _toldYesterday;
        set
        {
            _toldYesterday = value;
            MemoryState.Current?.MarkTold("lastday:" + value);
        }
    }

    /// <summary>День переложен заново — его итог соберётся снова, уже по полной раскладке.</summary>
    public void ForgetDay(string day)
    {
        lock (_lock) if (!_days.Remove(day)) return;
        Save();
    }

    /// <summary>Факты о человеке (копия) — повод самой завести с ним разговор.</summary>
    /// <summary>Все факты о всех (засев графа памяти): (профиль, факт).</summary>
    public List<(string Who, string Fact)> AllFacts()
    {
        lock (_lock) return _friends.SelectMany(kv => kv.Value.Facts.Select(f => (kv.Key, f))).ToList();
    }

    public List<string> FactsOf(string speaker)
    {
        lock (_lock) return _friends.TryGetValue(speaker, out var f) ? [.. f.Facts] : [];
    }

    /// <summary>Итог дня yyyy-MM-dd (для банка памяти: «что было в пятницу»); null — нет или день пустой.</summary>
    public string? DaySummary(string day)
    {
        lock (_lock) return _days.TryGetValue(day, out var t) && t.Length > 0 ? t : null;
    }

    /// <summary>Итог последнего прошедшего дня с разговорами (до недели назад); null — нет.</summary>
    private (DateTime Day, string Text)? Yesterday()
    {
        for (var d = 1; d <= 7; d++)
        {
            var day = DateTime.Today.AddDays(-d);
            if (_days.TryGetValue(day.ToString("yyyy-MM-dd"), out var t) && t.Length > 0) return (day, t);
        }
        return null;
    }

    /// <summary>Ход записан в журнал: копим, каждые EveryTurns ходов человека — выписать о нём факты в фоне.</summary>
    public void OnTurn(string? speaker)
    {
        if (string.IsNullOrWhiteSpace(speaker)) return;
        int n;
        lock (_lock)
        {
            n = _since[speaker] = (_since.TryGetValue(speaker, out var c) ? c : 0) + 1;
            if (!_friends.TryGetValue(speaker, out var f)) _friends[speaker] = f = new Friend();
            f.Turns++;
            if (n < EveryTurns || _busy.Contains(speaker)) return;
            _since[speaker] = 0;
            _busy.Add(speaker);
        }
        _ = ExtractAsync(speaker);
    }

    private async Task ExtractAsync(string speaker)
    {
        try
        {
            var name = People.Canon(speaker);
            // последние реплики разговора, где этот человек говорил: его слова и ответы ему (остальные — для связности)
            var lines = _journal.Transcript(DateTime.Today).Where(u => !u.Hint).TakeLast(EveryTurns * 5).ToList();
            if (lines.Count(u => u.Who == name) < 3) return;
            List<string> known;
            lock (_lock) known = [.. _friends[speaker].Facts];
            var dialog = new StringBuilder();
            foreach (var u in lines)
                dialog.Append(u.Who).Append(": ").AppendLine(Trim(u.Text, 300));
            var system = "Ты — долгая память Клавы (кошкодевочка-компаньон на компьютере друга). Из отрывка разговора выпиши НОВЫЕ " +
                         $"устойчивые факты о человеке «{name}»: что любит и не любит, чем занимается, во что играет, планы, важные события, " +
                         "с кем дружит, привычки, как к нему обращаться. НЕ пиши разовое («поздоровался», «попросил открыть окно»), шутки " +
                         "как факты, то, что Клава сама «вспоминает» про него (она могла напутать), и то, что уже есть в списке. " +
                         "Каждый факт — одна короткая строка от третьего лица с именем, без выдумок. " +
                         "Если старый факт из списка стал неверным — строка «убрать: <текст факта>». Нечего добавить — ответь одним словом «нет».";
            var user = $"Уже известно о {name}:\n{(known.Count > 0 ? string.Join("\n", known.Select(k => "- " + k)) : "(пока ничего)")}\n\nРазговор:\n{dialog}";
            if (_brain is null) return;
            var reply = await _brain.OneShotAsync(system, user, null, 400, CancellationToken.None);
            if (string.IsNullOrWhiteSpace(reply) || MemoryBank.Junk(reply)) return;
            var added = new List<string>();
            var removed = 0;
            lock (_lock)
            {
                var f = _friends[speaker];
                foreach (var raw in reply.Split('\n'))
                {
                    var line = raw.Trim().TrimStart('-', '•', '*', ' ').Trim();
                    if (line.Length < 6 || line.Equals("нет", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.StartsWith("убрать:", StringComparison.OrdinalIgnoreCase))
                    {
                        var what = line[7..].Trim().ToLowerInvariant();
                        removed += f.Facts.RemoveAll(x => x.ToLowerInvariant().Contains(what) || what.Contains(x.ToLowerInvariant()));
                        continue;
                    }
                    var norm = line.ToLowerInvariant();
                    if (f.Facts.Any(x => x.ToLowerInvariant() == norm)) continue;
                    f.Facts.Add(Trim(line, 200));
                    added.Add(line);
                }
                if (f.Facts.Count > MaxFacts) f.Facts.RemoveRange(0, f.Facts.Count - MaxFacts);
                f.Updated = DateTime.Now;
            }
            if (added.Count > 0 || removed > 0)
            {
                Save();
                Log.Write($"память: про {name} +{added.Count} фактов, −{removed}: {string.Join("; ", added.Take(3))}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            Log.Error($"память: факты про {speaker}", ex);
        }
        finally
        {
            lock (_lock) _busy.Remove(speaker);
        }
    }

    /// <summary>Итоги прошедших дней (до недели назад), которых ещё нет в days.json — по одному разовому запросу на день.</summary>
    public async Task SummarizePastDaysAsync()
    {
        if (_summarizing) return;
        _summarizing = true;
        try
        {
            for (var d = 7; d >= 1; d--)
            {
                var day = DateTime.Today.AddDays(-d);
                var key = day.ToString("yyyy-MM-dd");
                lock (_lock) if (_days.ContainsKey(key)) continue;
                var lines = _journal.Transcript(day).Where(u => !u.Hint).ToList();
                if (lines.Count < 3) { lock (_lock) _days[key] = ""; continue; }
                // день целиком — эпизодами банка памяти (с утра до ночи); не разложен — реплики журнала, равномерно по дню
                var digest = DayDigest?.Invoke(key);
                if (digest is null)
                {
                    var step = Math.Max(1, lines.Count / 140);
                    var text = new StringBuilder();
                    for (var i = 0; i < lines.Count; i += step)
                        text.Append($"{lines[i].Time:HH:mm} {lines[i].Who}: ").AppendLine(Trim(lines[i].Text, 180));
                    digest = text.ToString();
                }
                if (_brain is null) return;
                var reply = await _brain.OneShotAsync(
                    "Ты — долгая память Клавы. Перескажи день коротко, 4–6 предложений от третьего лица, ПО ПОРЯДКУ времени (ночью — " +
                    "утром — днём — вечером, с часами, где это важно): кто был, о чём говорили, во что играли и что делали, чем " +
                    "кончилось, что обещали на потом. Без воды и выдумок, без списков.",
                    $"День: {day.ToString("d MMMM yyyy, dddd", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))}.\n{digest}", null, 450, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(reply) || MemoryBank.Junk(reply)) return;   // мозг не ответил — попробуем при следующей реплике
                lock (_lock) _days[key] = Trim(reply.Replace('\n', ' '), 900);
                Save();
                Log.Write($"память: итог дня {key} — {Trim(reply, 120)}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
        {
            Log.Error("память: итог дня", ex);
        }
        finally { _summarizing = false; }
    }

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Что помнит — для окна настроек и команды «что ты обо мне помнишь».</summary>
    public string Describe(string? speaker)
    {
        lock (_lock)
        {
            if (speaker is not null && _friends.TryGetValue(speaker, out var f) && f.Facts.Count > 0)
                return $"Про {Human(speaker)} помню: {string.Join("; ", f.Facts)}.";
            return $"Помню людей: {string.Join(", ", _friends.Where(x => x.Value.Facts.Count > 0).Select(x => $"{Human(x.Key)} ({x.Value.Facts.Count})"))}; итогов дней: {_days.Count(x => x.Value.Length > 0)}.";
        }
    }
}
