using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Кто ушёл из комнаты. Друг 02.10: «ей 200 раз сказали, что Паши сегодня нет, а Лис ушёл на работу, — а распознавалка
/// всё лепит их имена тем, кто с ней базарит». Отметка «ушёл» ставится словами людей («Лис ушёл на работу», «Паши
/// сегодня нет», «в комнате сейчас Аксюта, Настя и Кот») или её инструментом room; снимается ТОЛЬКО словами («Лис
/// вернулся» — прямо, без «когда/если/скажу», от узнанного по голосу человека из комнаты) или инструментом, по голосу —
/// никогда (друг 10.10: логика «как солдат»). Голос ушедшего — фраза игнорируется (OverlayForm). Отметка живёт 16 часов (назавтра —
/// с чистого листа). data\memory\presence.json.
/// </summary>
internal sealed partial class Presence
{
    internal sealed record Away(DateTime Since, string Why);

    private static readonly TimeSpan Life = TimeSpan.FromHours(16);
    private readonly Dictionary<string, Away> _away = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string? _file;

    /// <summary>Что поменялось (для мозга одной строкой в следующем ходе: «Лис вернулся — голос узнан трижды»).</summary>
    public string? PendingNote { get; private set; }

    public Presence(string? file)
    {
        _file = file;
        if (file is null) return;
        var saved = SafeFile.Load(file, t => JsonSerializer.Deserialize<Dictionary<string, Away>>(t));
        if (saved is null) return;
        foreach (var (k, v) in saved)
            if (DateTime.UtcNow - v.Since < Life) _away[k] = v;
    }

    public bool IsAway(string? profile)
    {
        if (profile is null) return false;
        lock (_gate)
        {
            if (!_away.TryGetValue(profile, out var a)) return false;
            if (DateTime.UtcNow - a.Since < Life) return true;
            _away.Remove(profile);
            Save();
            return false;
        }
    }

    public List<(string Profile, Away A)> AwayList()
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            return _away.Where(kv => now - kv.Value.Since < Life).OrderBy(kv => kv.Value.Since).Select(kv => (kv.Key, kv.Value)).ToList();
        }
    }

    public string? TakeNote()
    {
        lock (_gate) { var n = PendingNote; PendingNote = null; return n; }
    }

    /// <returns>true — отметка поставлена заново (раньше человек был тут).</returns>
    public bool SetAway(string profile, string why)
    {
        lock (_gate)
        {
            var fresh = !_away.ContainsKey(profile);
            _away[profile] = new Away(DateTime.UtcNow, why.Length > 80 ? why[..80] : why);
            Save();
            Log.Write($"присутствие: {profile.Replace('_', ' ')} — нет в комнате ({why})");
            return fresh;
        }
    }

    /// <returns>true — человек был отмечен ушедшим.</returns>
    public bool SetHere(string profile, string why)
    {
        lock (_gate)
        {
            if (!_away.Remove(profile)) return false;
            Save();
            Log.Write($"присутствие: {profile.Replace('_', ' ')} снова в комнате ({why})");
            return true;
        }
    }

    private void Save()
    {
        if (_file is null) return;
        try { SafeFile.Write(_file, JsonSerializer.Serialize(_away, new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping })); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("присутствие: presence.json не записался", ex); }
    }

    // ---------- слова людей: кто ушёл, кто вернулся ----------

    // «ушёл / уехал / свалил / на работе / на работу / не будет / не было / нет / нету / спать пошёл»
    [GeneratedRegex(@"(?<![а-яё])(уш[её]л|ушла|ушли|уехал[аи]?|свалил[аи]?|умотал[аи]?|укатил[аи]?|убежал[аи]?|смылс[яь]|слил(ся|ась|ись)|на\s+работ[уеы]|на\s+работке|на\s+смен[уеы]|на\s+учёб[уеы]|на\s+учеб[уеы]|в\s+универ|не\s+будет|не\s+было|не\s+приходил[аи]?|нет[уь]?|спать\s+пош[её]л|спать\s+пошла|спит)(?![а-яё])", RegexOptions.IgnoreCase)]
    private static partial Regex AwayCueRx();

    // «вернулся / пришёл / пришла / зашёл / тут / здесь / подошёл / с нами»
    [GeneratedRegex(@"(?<![а-яё])(вернул(ся|ась|ись)|приш[её]л|пришла|пришли|заш[её]л|зашла|подош[её]л|подошла|приехал[аи]?|тут|здесь|с\s+нами|в\s+комнате)(?![а-яё])", RegexOptions.IgnoreCase)]
    private static partial Regex HereCueRx();

    // перекличка: «в комнате сейчас Аксюта, Настя и Киберкот», «сейчас в комнате только Кот и Настя» (просто «тут
    // Аксюта и Настя спорят» — не перекличка: остальных это не выгоняет)
    [GeneratedRegex(@"(?i:(?:сейчас\s+|щас\s+)?в\s+комнате(?:\s+(?:сейчас|щас|только|сидят|сидим|есть|остались))*)\s+(?<list>[А-ЯЁ][^.!?;:]*?(?:,|\s+и\s+)[^.!?;:]+)", RegexOptions.None)]
    private static partial Regex RollCallRx();

    [GeneratedRegex(@"[А-ЯЁA-Z][а-яёa-z]+|я(?![а-яё])", RegexOptions.None)]
    private static partial Regex NameWordRx();

    [GeneratedRegex(@"[^.!?…]+[.!?…]*")]
    private static partial Regex SentenceRx();

    /// <summary>
    /// Разобрать реплику: кто ушёл, кто вернулся, перекличка. Вопросы («Лис тут?») не считаются. Обращение по имени
    /// через запятую («Лис, нет, я не про то») — не про то, что Лиса нет. Возвращает, что поменялось (для журнала).
    /// </summary>
    /// <param name="speaker">Профиль говорящего (для «я ушёл»), может быть null.</param>
    /// <param name="known">Все известные профили (компания и база голосов) — для переклички.</param>
    public List<string> Hear(string text, string? speaker, IReadOnlyCollection<string> known)
    {
        var changes = new List<string>();
        // уговор, а не факт: «когда он зайдёт, я тебе скажу: Паша пришёл» (10.10 16:30 — whisper разбил фразу, и «О, Паша
        // пришёл» вернуло в комнату того, кто на работе). Вернуть в комнату — только прямым «пришёл» без условий, будущего
        // и пересказа; отметить ушедшим — без условий («когда уйдёт», «если»), а «будет вечером» как раз значит «сейчас нет»
        // Условие действует на своё предложение и на следующие (после «я тебе скажу» идёт цитата), но не на прежние:
        // «Лиса тоже нету, он ушёл. А я говорил, что когда кровь сдаю…» — Лиса нет
        bool blockHere = false, blockAway = false;
        // мат-связки и «короче» не меняют смысла, а запятые вокруг них рвут «Паши в комнате, блять, нету» на куски
        text = FillerRx().Replace(text, " ");
        foreach (System.Text.RegularExpressions.Match sm in SentenceRx().Matches(text))
        {
            // «Паши в комнате, нету вообще» — whisper ставит запятую перед «нет»: склеить, если до неё имя и наречия места
            var sentence = CommaNoRx().Replace(sm.Value.Trim(), m => JoinsNo(m.Groups["frag"].Value, known) ? m.Groups["frag"].Value + " " : m.Value);
            blockHere |= HereDealRx().IsMatch(sentence);
            blockAway |= AwayDealRx().IsMatch(sentence);
            if (sentence.Length == 0 || sentence.EndsWith('?')) continue;
            if ((blockHere || blockAway) && (AwayCueRx().IsMatch(sentence) || HereCueRx().IsMatch(sentence)))
                Log.Write($"присутствие: «{(sentence.Length > 80 ? sentence[..80] + "…" : sentence)}» — условие/уговор: " +
                          (blockHere && blockAway ? "состав комнаты не меняю" : blockHere ? "вернувшихся не отмечаю" : "ушедших не отмечаю"));
            // перекличка: названы двое и больше — они тут, остальные из известных — нет
            if (!blockHere && RollCallRx().Match(sentence) is { Success: true } roll)
            {
                var here = new HashSet<string>(StringComparer.Ordinal);
                foreach (var part in Regex.Split(roll.Groups["list"].Value, @",|\s+и\s+|\s+да\s+"))
                {
                    var w = part.Trim();
                    if (w.Equals("я", StringComparison.OrdinalIgnoreCase) && speaker is not null) { here.Add(speaker); continue; }
                    if (Resolve(w, known) is { } p) here.Add(p);
                }
                if (here.Count >= 2)
                {
                    foreach (var p in here)
                        if (SetHere(p, "назван в перекличке")) changes.Add($"{Human(p)} тут");
                    foreach (var p in known.Where(k => !here.Contains(k)))
                        if (!IsAway(p) && SetAway(p, "не назван в перекличке «кто в комнате»")) changes.Add($"{Human(p)} нет");
                    continue;
                }
            }
            foreach (var clause in Regex.Split(sentence, @"[,;—–]|\s-\s|\sа\s(?=[А-ЯЁ])"))
            {
                var c = clause.Trim();
                if (c.Length < 3) continue;
                var away = AwayCueRx().Match(c);
                var back = HereCueRx().Match(c);
                if (!away.Success && !back.Success) continue;
                foreach (System.Text.RegularExpressions.Match nm in NameWordRx().Matches(c))
                {
                    string? p;
                    if (nm.Value.Equals("я", StringComparison.OrdinalIgnoreCase))
                    {
                        // «я ушёл / я на работу» — сам говорящий (кроме «я тут» — это и так понятно по голосу)
                        if (speaker is null || !away.Success || !Regex.IsMatch(c, @"(?<![а-яё])я\s+(уже\s+)?(уш[её]л|ушла|ухожу|пош[её]л\s+(на\s+работ|спать|домой)|пошла\s+(на\s+работ|спать|домой)|на\s+работ|уезжаю|сваливаю)", RegexOptions.IgnoreCase)) continue;
                        p = speaker;
                    }
                    else p = Resolve(nm.Value, known);
                    if (p is null) continue;
                    // «нет» только сразу после имени (с наречиями) — «Паши сегодня нет», «Лиса в комнате тоже нету»;
                    // «Паша сказал, что нет» и «нет, Лис» — не про отсутствие
                    if (away.Success && (!away.Value.StartsWith("нет", StringComparison.OrdinalIgnoreCase) || NoAfterName(c, nm, away)))
                    {
                        if (!blockAway && SetAway(p, c.Length > 60 ? c[..60] : c)) changes.Add($"{Human(p)} нет");
                    }
                    else if (!blockHere && back.Success && !away.Success && Regex.IsMatch(back.Value, @"верн|приш|заш|подош|приех", RegexOptions.IgnoreCase))
                    {
                        // вернуть в комнату может только узнанный по голосу человек из комнаты: «пришёл» от неузнанного
                        // голоса (видео, телевизор, кто-то за дверью) — не факт
                        if (speaker is null) { Log.Write($"присутствие: «{c}» — голос не узнан, вернувшимся {Human(p)} не считаю"); continue; }
                        if (SetHere(p, c.Length > 60 ? c[..60] : c)) changes.Add($"{Human(p)} вернулся");
                    }
                }
            }
        }
        return changes;
    }

    [GeneratedRegex(@"[,\s]*(?<![а-яё])(блять|бля|блин|нахуй|сука|ёпта|епта|короче|типа|ну)(?![а-яё])[,]?", RegexOptions.IgnoreCase)]
    private static partial Regex FillerRx();

    // против «вернулся»: уговор, условие, будущее, пересказ, догадка — «когда он зайдёт», «если Паша придёт», «я тебе
    // скажу», «маякну», «скажи ей, что…», «как только вернётся», «должен прийти», «придёт к пяти», «может, пришёл»
    [GeneratedRegex(@"(?<![а-яё])(когда|если|как\s+только|пока\s+не|вдруг|может|наверно|наверное|кажется|скажу|скажем|скажет|скажи|сообщу|маякну|маякни|напишу|предупрежу|предупреди|говорил[аи]?\s+что|сказал[аи]?\s+что|должен|должна|собирается|придёт|придет|придут|вернётся|вернется|зайдёт|зайдет|приедет|будет)(?![а-яё])", RegexOptions.IgnoreCase)]
    private static partial Regex HereDealRx();

    // против «ушёл»: только условие и уговор — «когда уйдёт», «если Лис уедет», «я скажу, что ушёл»; а «будет вечером»,
    // «придёт к пяти» — как раз значит «сейчас нет»
    [GeneratedRegex(@"(?<![а-яё])(когда|если|как\s+только|вдруг|скажу|скажем|скажет|скажи|сообщу|маякну|маякни|предупрежу|предупреди|уйдёт|уйдет|уедет|свалит)(?![а-яё])", RegexOptions.IgnoreCase)]
    private static partial Regex AwayDealRx();

    [GeneratedRegex(@"(?<frag>[^,]+),\s*(?=(?:нет|нету)(?![а-яё]))", RegexOptions.IgnoreCase)]
    private static partial Regex CommaNoRx();

    private const string PlaceWords = @"^(сегодня|сейчас|щас|вообще|тоже|уже|ещё|еще|тут|здесь|в|комнате|дома|с|нами|со|вчерашнего|дня|пока|давно|больше)$";

    /// <summary>Кусок до запятой перед «нет» — имя и только наречия места/времени («Паши в комнате»), а не «Лис» в обращении.</summary>
    private static bool JoinsNo(string frag, IReadOnlyCollection<string> known)
    {
        var words = frag.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.FindLastIndex(words, w => char.IsUpper(w[0]) && Resolve(w, known) is not null);
        return at >= 0 && at < words.Length - 1 && words.Skip(at + 1).All(w => Regex.IsMatch(w, PlaceWords, RegexOptions.IgnoreCase));
    }

    /// <summary>«Паши (сегодня|вообще|в комнате|тоже)* нет(у)»: между именем и «нет» — только наречия, без запятой.</summary>
    private static bool NoAfterName(string clause, System.Text.RegularExpressions.Match name, System.Text.RegularExpressions.Match no)
    {
        if (no.Index < name.Index + name.Length) return false;
        var between = clause[(name.Index + name.Length)..no.Index];
        var words = between.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 4 && words.All(w => Regex.IsMatch(w, PlaceWords, RegexOptions.IgnoreCase));
    }

    /// <summary>Слово → профиль: через People (склонения, прозвища), иначе «Киберкот» → «Кибер_Кот» по слитному имени.</summary>
    internal static string? Resolve(string word, IReadOnlyCollection<string> known)
    {
        var w = word.Trim().Trim('.', '!', ',', '«', '»', '"');
        if (w.Length < 3) return null;
        if (People.Profile(w) is { } p && known.Contains(p)) return p;
        var stem = MemoryGraph.Stem(w.Replace(" ", ""));
        if (stem.Length < 5) return null;
        var hits = known.Where(k => MemoryGraph.Stem(k.Replace("_", "").Replace(" ", "")).StartsWith(stem, StringComparison.Ordinal)
                                    || k.Replace("_", "").Replace(" ", "").StartsWith(w.Replace(" ", ""), StringComparison.OrdinalIgnoreCase)).ToList();
        return hits.Count == 1 ? hits[0] : null;
    }

    private static string Human(string profile) => profile.Replace('_', ' ');
}
