using System.Text;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Разметка страницы для чтения вслух: ударения (знак U+0301 после ударной гласной, ё — как есть), стрелки интонации
/// перед словом-центром (↗ голос вверх — вопрос без вопросительного слова, перечисление; ↘ нажим и вниз — «кто/что/где?»,
/// восклицание, приказ) и пометки в квадратных скобках, как читать: громче, тише, с улыбкой… — по словам автора
/// («закричал», «прошептал», «сердито») и так, чтобы на каждой странице были и громкое, и тихое место.
/// Разная манера нужна профилю голоса: он узнаёт человека и уставшим, и кричащим, и шепчущим.
/// </summary>
internal static partial class ReadingProsody
{
    public const char Acute = '́';
    public const char Rise = '↗';
    public const char Fall = '↘';

    /// <summary>Манера страницы по кругу: на 20 страниц — весь набор, профиль слышит голос во всех видах.</summary>
    private static readonly string[] PageManner =
    [
        "обычным голосом, как рассказываешь другу",
        "бодро, чуть быстрее обычного",
        "медленно и чётко, как диктор",
        "с выражением, как актёр",
        "спокойно, чуть тише обычного",
        "уверенно, чуть громче обычного",
        "с улыбкой",
        "таинственно, как страшилку у костра",
        "как ведущий новостей",
        "устало, но разборчиво",
    ];

    private static readonly (Regex Rx, string Mark, int Kind)[] Cues =
    [
        (new(@"\b(закрич|крикн|крича|кричал|заор|орал|воскликн|восклица|рявкн|гаркн|завоп|вопил|взревел|заревел|громко\b|громогласн|во весь голос|во вс[её] горло|прикрикн)", RegexOptions.IgnoreCase), "громче", 1),
        (new(@"\b(шепн|шепот|шёпот|шептал|шепча|прошепт|вполголоса|еле слышно|чуть слышно|тихо\b|тихонько|негромко|пробормот|бормот|буркн|прошелест)", RegexOptions.IgnoreCase), "тише", -1),
        (new(@"\b(сердит|сердясь|гневн|в гневе|злобн|раздраж[её]нн|вспылил|рассердил|строго\b|сурово\b|грозно\b)", RegexOptions.IgnoreCase), "строго", 0),
        (new(@"\b(смеясь|засмеял|рассмеял|усмехн|улыбн|улыбая|хохоч|хохот|весело\b|шутливо\b)", RegexOptions.IgnoreCase), "с улыбкой", 0),
        (new(@"\b(насмешлив|язвительн|с усмешк|ехидн)", RegexOptions.IgnoreCase), "насмешливо", 0),
        (new(@"\b(удивл[её]н|изумл[её]н|в изумлении|с удивлением|ахнул)", RegexOptions.IgnoreCase), "удивлённо", 0),
        (new(@"\b(печальн|грустн|с горест|со слезами|в слезах|заплакал|плача|жалобн|жалким голосом|дрожащ)", RegexOptions.IgnoreCase), "с грустью", 0),
        (new(@"\b(испуганн?о\b|испугал|в страхе|со страхом|робко\b|оробел)", RegexOptions.IgnoreCase), "робко", 0),
        (new(@"\b(ласково\b|нежно\b|с нежностью)", RegexOptions.IgnoreCase), "ласково", 0),
        (new(@"\b(медленно\b|протяжно\b|задумчиво\b|в задумчивости|нараспев)", RegexOptions.IgnoreCase), "медленно", 0),
        (new(@"\b(быстро\b|торопливо\b|поспешно\b|скороговоркой)", RegexOptions.IgnoreCase), "быстрее", 0),
        (new(@"\b(торжественн|важно\b)", RegexOptions.IgnoreCase), "торжественно", 0),
    ];

    private static readonly HashSet<string> WhWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "кто", "кого", "кому", "кем", "ком", "что", "чего", "чему", "чем", "чём", "где", "куда", "откуда", "когда", "почему",
        "зачем", "отчего", "как", "каков", "какова", "каково", "каковы", "какой", "какая", "какое", "какие", "какого", "какую",
        "каким", "каких", "какому", "какими", "который", "которая", "которое", "которые", "которого", "сколько", "скольких",
        "чей", "чья", "чьё", "чье", "чьи", "чьего", "доколе",
    };

    /// <summary>Восклицание с «как/какой/сколько/что за» — подъём на этом слове (ИК-5), остальные — нажим вниз.</summary>
    private static readonly HashSet<string> ExclaimRise = new(StringComparer.OrdinalIgnoreCase)
    {
        "как", "какой", "какая", "какое", "какие", "какого", "каким", "сколько", "столько", "такой", "такая", "такое",
    };

    private static readonly HashSet<string> Function = new(StringComparer.OrdinalIgnoreCase)
    {
        "а", "и", "но", "да", "ну", "же", "ж", "ли", "ль", "бы", "б", "то", "ведь", "уж", "вот", "вон", "ах", "ох", "эх", "эй",
        "ой", "о", "в", "во", "на", "с", "со", "к", "ко", "по", "за", "из", "у", "от", "до", "для", "при", "про", "об", "обо",
        "не", "ни", "я", "ты", "он", "она", "оно", "мы", "вы", "они", "мне", "меня", "тебе", "тебя", "его", "её", "ее", "ему",
        "ей", "нам", "вам", "им", "их", "нас", "вас", "себе", "себя", "это", "там", "тут", "так", "же", "мой", "твой", "свой",
    };

    [GeneratedRegex(@"\[[^\]\n]*\]")]
    private static partial Regex Bracket();
    [GeneratedRegex(@"[А-Яа-яЁё+]+")]
    private static partial Regex MarkedWord();

    private sealed record Tok(int Start, int Length, bool Word, bool Mark);

    public static string StripBrackets(string text) => Bracket().Replace(text, " ");

    /// <summary>
    /// Разметить страницу. <paramref name="plusMarked"/> — ответ RUAccent («+» перед ударной гласной, ё восстановлены);
    /// null — ударения по словарю accents.bin (без нейросети). <paramref name="page"/> — номер для манеры страницы,
    /// −1 — без манеры (встроенные отрывки размечены вручную).
    /// </summary>
    /// <param name="verse">Стихи: перенос строки — не конец предложения.</param>
    public static string Annotate(string plain, string? plusMarked, int page, bool verse = false)
    {
        var toks = Tokenize(plain);
        var words = toks.Where(t => t.Word).ToList();
        var shown = words.Select(w => plain.Substring(w.Start, w.Length)).ToArray();
        var stress = new int[words.Count];
        Array.Fill(stress, -1);
        if (plusMarked is not null) AlignAccents(shown, stress, plusMarked);
        for (var i = 0; i < shown.Length; i++)
            if (stress[i] < 0 && !shown[i].Contains('ё', StringComparison.OrdinalIgnoreCase)) stress[i] = LiteStress.StressIndex(shown[i]);

        var arrows = new Dictionary<int, char>();                    // индекс слова → стрелка
        var marks = new SortedDictionary<int, List<string>>();       // смещение начала предложения → пометки
        var sentences = Sentences(plain, lines: !verse);
        var info = new List<(int Start, int End, int Loud, bool Speech, bool Exclaim, bool Question, int Words)>();
        foreach (var (s, e) in sentences)
        {
            var text = plain[s..e];
            var sw = WordRange(words, s, e);
            if (sw.Count == 0) continue;
            var speech = text.Contains('«') || text.TrimStart().StartsWith('—') || LineStartsWithDash(plain, s) || QuoteDepth(plain, s) > 0;
            var loud = 0;
            var own = new List<string>();
            var existing = Bracket().Matches(text).Select(m => m.Value.ToLowerInvariant()).ToList();
            if (existing.Count > 0)
            {
                if (existing.Any(x => x.Contains("громч") || x.Contains("громко"))) loud = 1;
                else if (existing.Any(x => x.Contains("тиш") || x.Contains("тихо") || x.Contains("шёпот") || x.Contains("шепот"))) loud = -1;
            }
            else
            {
                var norm = StripBrackets(text);
                foreach (var (rx, mark, kind) in Cues)
                {
                    if (own.Count >= 2 || !rx.IsMatch(norm)) continue;
                    if (kind != 0 && loud != 0) continue;      // и «закричал», и «шёпотом» — оставить первое
                    own.Add(mark);
                    if (kind != 0) loud = kind;
                }
                if (own.Count > 0) marks[s] = own;
            }
            foreach (var (ps, pe) in Phrases(plain, s, e)) PhraseArrows(plain, words, shown, ps, pe, arrows);
            info.Add((s, e, loud, speech, text.Contains('!'), text.Contains('?'), sw.Count));
        }

        // громкое место на страницу: авторских «закричал» нет — восклицание в речи, потом вопрос, потом любая реплика
        if (info.Count > 0 && info.All(x => x.Loud <= 0))
        {
            var pick = info.FindIndex(x => x.Speech && x.Exclaim && x.Loud == 0);
            if (pick < 0) pick = info.FindIndex(x => x.Exclaim && x.Loud == 0);
            if (pick < 0) pick = info.FindIndex(x => x.Speech && x.Loud == 0 && x.Words >= 3);
            if (pick < 0 && info.Count >= 3) pick = info.Count / 3;
            if (pick >= 0)
            {
                AddMark(marks, info[pick].Start, "громче, чётко");
                info[pick] = info[pick] with { Loud = 1 };
            }
        }
        // ещё одно восклицание в речи — тоже громче (не подряд с первым)
        var lastLoud = info.FindLastIndex(x => x.Loud > 0);
        for (var i = 0; i < info.Count; i++)
        {
            if (info.Count(x => x.Loud > 0) >= 3) break;
            if (info[i].Loud != 0 || !info[i].Speech || !info[i].Exclaim || Math.Abs(i - lastLoud) < 2 || marks.ContainsKey(info[i].Start)) continue;
            AddMark(marks, info[i].Start, "громче");
            info[i] = info[i] with { Loud = 1 };
            lastLoud = i;
        }
        // тихое место: повествование (не реплика), лучше с многоточием или сразу после громкого, иначе последнее
        if (info.Count > 1 && info.All(x => x.Loud >= 0))
        {
            var candidates = Enumerable.Range(0, info.Count).Where(i => info[i].Loud == 0 && !marks.ContainsKey(info[i].Start) && info[i].Words >= 5).ToList();
            var pick = candidates.FirstOrDefault(i => plain[info[i].Start..info[i].End].Contains('…') && !info[i].Speech, -1);
            if (pick < 0) pick = candidates.FirstOrDefault(i => i > 0 && info[i - 1].Loud > 0 && !info[i].Speech, -1);
            if (pick < 0) pick = candidates.LastOrDefault(i => !info[i].Speech, -1);
            if (pick < 0 && candidates.Count > 0) pick = candidates[^1];
            if (pick >= 0) AddMark(marks, info[pick].Start, "тише, спокойно");
        }

        // сборка
        var sb = new StringBuilder(plain.Length + plain.Length / 3 + 64);
        if (page >= 0) sb.Append('[').Append("читать: ").Append(PageManner[page % PageManner.Length]).Append("]\n");
        var wi = 0;
        foreach (var t in toks)
        {
            // пометка — в начале предложения; оно может начинаться посреди «не-слова» («\n— Молчи…»)
            var pos = t.Start;
            var end = t.Start + t.Length;
            foreach (var (at, m) in marks)
            {
                if (at < t.Start || at >= end || m.Count == 0) continue;
                sb.Append(plain, pos, at - pos).Append('[').Append(string.Join(", ", m)).Append("] ");
                m.Clear();
                pos = at;
            }
            if (!t.Word)
            {
                sb.Append(plain, pos, end - pos);
                continue;
            }
            if (arrows.TryGetValue(wi, out var arrow)) sb.Append(arrow);
            sb.Append(WithStress(shown[wi], stress[wi]));
            wi++;
        }
        return sb.ToString();
    }

    private static bool LineStartsWithDash(string s, int at)
    {
        var line = s.LastIndexOf('\n', Math.Max(0, at - 1)) + 1;
        return s.AsSpan(line).TrimStart().StartsWith("—");
    }

    private static int QuoteDepth(string s, int at)
    {
        var d = 0;
        for (var i = 0; i < at; i++)
        {
            if (s[i] == '«') d++;
            else if (s[i] == '»' && d > 0) d--;
        }
        return d;
    }

    private static void AddMark(SortedDictionary<int, List<string>> marks, int at, string mark)
    {
        if (!marks.TryGetValue(at, out var l)) marks[at] = l = [];
        if (l.Count < 2) l.Add(mark);
    }

    /// <summary>Слова и прочее; всё в [квадратных скобках] — пометка, не слово (ни ударения, ни стрелки).</summary>
    private static List<Tok> Tokenize(string s)
    {
        var res = new List<Tok>();
        var i = 0;
        while (i < s.Length)
        {
            if (s[i] == '[')
            {
                var close = s.IndexOf(']', i);
                var nl = s.IndexOf('\n', i);
                if (close > i && (nl < 0 || close < nl))
                {
                    res.Add(new Tok(i, close - i + 1, false, true));
                    i = close + 1;
                    continue;
                }
            }
            var start = i;
            if (IsCyr(s[i]))
            {
                while (i < s.Length && IsCyr(s[i])) i++;
                res.Add(new Tok(start, i - start, true, false));
            }
            else
            {
                while (i < s.Length && !IsCyr(s[i]) && s[i] != '[') i++;
                if (i == start) i++;
                res.Add(new Tok(start, i - start, false, false));
            }
        }
        return res;
    }

    public static bool IsCyr(char c) => c is >= 'А' and <= 'я' or 'Ё' or 'ё';

    private static bool IsVowel(char c) => "аеёиоуыэюяАЕЁИОУЫЭЮЯ".IndexOf(c) >= 0;

    private static string Norm(string w) => w.Replace("+", "").ToLowerInvariant().Replace('ё', 'е');

    /// <summary>
    /// Перенести ударения и ё из ответа RUAccent на слова страницы. Он выкидывает кавычки и часть знаков, поэтому
    /// сверка по словам, с догонялкой на пропуски в обе стороны.
    /// </summary>
    private static void AlignAccents(string[] shown, int[] stress, string plusMarked)
    {
        var marked = MarkedWord().Matches(StripBrackets(plusMarked)).Select(m => m.Value).Where(v => v.Replace("+", "").Length > 0).ToList();
        var mn = marked.Select(Norm).ToList();
        int i = 0, j = 0;
        while (i < shown.Length && j < marked.Count)
        {
            var sn = Norm(shown[i]);
            if (sn != mn[j])
            {
                var k = mn.FindIndex(j + 1, Math.Min(6, mn.Count - j - 1), x => x == sn);
                if (k >= 0) j = k;
                else
                {
                    var m = Array.FindIndex(shown, i + 1, Math.Min(6, shown.Length - i - 1), x => Norm(x) == mn[j]);
                    if (m >= 0) i = m;
                    else { i++; j++; continue; }
                }
            }
            var (letters, idx) = Unplus(marked[j]);
            // ё — из RUAccent (в книгах её часто нет), регистр — как на странице
            if (letters.Length == shown[i].Length)
            {
                var chars = shown[i].ToCharArray();
                for (var c = 0; c < chars.Length; c++)
                    if (char.ToLowerInvariant(letters[c]) == 'ё' && char.ToLowerInvariant(chars[c]) == 'е') chars[c] = char.IsUpper(chars[c]) ? 'Ё' : 'ё';
                shown[i] = new string(chars);
            }
            stress[i] = idx;
            i++;
            j++;
        }
    }

    private static (string Letters, int Index) Unplus(string w)
    {
        var sb = new StringBuilder(w.Length);
        var idx = -1;
        for (var k = 0; k < w.Length; k++)
        {
            if (w[k] == '+') { if (idx < 0) idx = sb.Length; continue; }
            sb.Append(w[k]);
        }
        return (sb.ToString(), idx);
    }

    /// <summary>Знак ударения после ударной гласной — только если гласных две и больше и нет ё (она всегда ударная).</summary>
    private static string WithStress(string w, int idx)
    {
        if (idx < 0 || idx >= w.Length || !IsVowel(w[idx])) return w;
        if (w.Count(IsVowel) < 2 || w.Contains('ё', StringComparison.OrdinalIgnoreCase)) return w;
        return string.Concat(w.AsSpan(0, idx + 1), Acute.ToString(), w.AsSpan(idx + 1));
    }

    private static List<int> WordRange(List<Tok> words, int s, int e)
    {
        var r = new List<int>();
        for (var i = 0; i < words.Count; i++)
            if (words[i].Start >= s && words[i].Start < e) r.Add(i);
        return r;
    }

    // ---------------- предложения и фразы ----------------

    private static readonly HashSet<string> Abbrev = new(StringComparer.OrdinalIgnoreCase)
    { "т", "г", "гг", "см", "стр", "им", "ул", "д", "др", "пр", "св", "тов", "ст", "с", "н", "в", "вв", "руб", "коп", "тыс", "млн", "e" };

    /// <summary>
    /// Предложения страницы (начало, конец). Конец — .!?… (и закрывающие кавычки) перед заглавной буквой, кавычкой
    /// или тире, или конец абзаца. Инициалы («И. И. Зурин») и сокращения («т. е.») предложение не рвут.
    /// </summary>
    /// <param name="lines">Перенос строки — конец предложения (проза: абзац); у стихов — нет.</param>
    public static List<(int Start, int End)> Sentences(string s, bool lines = true)
    {
        var res = new List<(int, int)>();
        var start = 0;
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (c == '\n' && lines)
            {
                if (s[start..i].Trim().Length > 0) res.Add(Trim(s, start, i));
                start = i + 1;
                i++;
                continue;
            }
            if (c is '.' or '!' or '?' or '…')
            {
                var j = i;
                while (j < s.Length && s[j] is '.' or '!' or '?' or '…') j++;
                while (j < s.Length && s[j] is '»' or '"' or ')' or '”') j++;
                if (j >= s.Length) break;
                if (s[j] is ' ' or '\t' || s[j] == '\n' && !lines)
                {
                    var k = j;
                    while (k < s.Length && (s[k] is ' ' or '\t' || s[k] == '\n' && !lines)) k++;
                    var next = k < s.Length ? s[k] : '\n';
                    var startsNew = char.IsUpper(next) || next is '«' or '"' or '(' or '[' or '\n' || next == '—' && !AuthorWords(s, k);
                    if (startsNew && !(c == '.' && IsAbbrev(s, i)))
                    {
                        res.Add(Trim(s, start, j));
                        start = k;
                        i = k;
                        continue;
                    }
                }
                i = j;
                continue;
            }
            i++;
        }
        if (start < s.Length && s[start..].Trim().Length > 0) res.Add(Trim(s, start, s.Length));
        return res;
    }

    /// <summary>После тире — слова автора со строчной буквы («— сказал он»): продолжение того же предложения.</summary>
    private static bool AuthorWords(string s, int dash)
    {
        var k = dash + 1;
        while (k < s.Length && s[k] == ' ') k++;
        return k < s.Length && char.IsLower(s[k]);
    }

    private static (int, int) Trim(string s, int a, int b)
    {
        while (a < b && char.IsWhiteSpace(s[a])) a++;
        while (b > a && char.IsWhiteSpace(s[b - 1])) b--;
        return (a, b);
    }

    private static bool IsAbbrev(string s, int dot)
    {
        var k = dot;
        while (k > 0 && char.IsLetter(s[k - 1])) k--;
        var w = s[k..dot];
        return w.Length == 1 && char.IsUpper(w[0]) || Abbrev.Contains(w);
    }

    /// <summary>
    /// Фразы внутри предложения — кусок речи со своей интонацией: границы у кавычек, у тире слов автора
    /// («…годок, — отвечала матушка») и у внутренних ! ? («Как! зачем?»).
    /// </summary>
    private static List<(int Start, int End)> Phrases(string s, int a, int b)
    {
        var cuts = new List<int> { a };
        for (var i = a; i < b; i++)
        {
            var c = s[i];
            if (c is '«' or '»') cuts.Add(c == '«' ? i : i + 1);
            else if (c == '—' && i > a + 1 && s[i - 1] == ' ' && ",!?…".Contains(s[i - 2])) cuts.Add(i);
            else if (c is '!' or '?' && i + 1 < b && s[i + 1] == ' ') cuts.Add(i + 1);
        }
        cuts.Add(b);
        var res = new List<(int, int)>();
        cuts = cuts.Distinct().Order().ToList();
        for (var k = 0; k + 1 < cuts.Count; k++)
            if (cuts[k + 1] > cuts[k]) res.Add((cuts[k], cuts[k + 1]));
        return res;
    }

    /// <summary>Стрелки для фразы: вопрос, восклицание, перечисление.</summary>
    private static void PhraseArrows(string s, List<Tok> words, string[] shown, int a, int b, Dictionary<int, char> arrows)
    {
        var idx = WordRange(words, a, b);
        if (idx.Count == 0) return;
        var body = s[a..b].TrimEnd(' ', '»', '"', ')', '—', '\n');
        var end = body.Length > 0 ? body[^1] : ' ';
        var question = body.Contains('?') && body.TrimEnd('.', '…', '!').EndsWith('?') || end == '?';
        var exclaim = !question && (end == '!' || body.EndsWith("!…") || body.EndsWith("!.."));
        string W(int i) => shown[i].ToLowerInvariant();
        int FirstContent() => idx.FirstOrDefault(i => !Function.Contains(W(i)) || WhWords.Contains(W(i)), -1);

        if (question)
        {
            var first = idx.FirstOrDefault(i => !Function.Contains(W(i)) || WhWords.Contains(W(i)), -1);
            if (first >= 0 && WhWords.Contains(W(first))) { arrows[first] = Fall; return; }         // ИК-2: «Где его пашпорт?»
            var li = idx.FindIndex(i => W(i) is "ли" or "ль");
            if (li > 0) { arrows[idx[li - 1]] = Rise; return; }                                       // «Не прикажешь ли?»
            var last = idx.LastOrDefault(i => !Function.Contains(W(i)), -1);
            if (last < 0) last = idx[^1];
            arrows[last] = Rise;                                                                      // ИК-3: «Вы до конечной?»
            return;
        }
        if (exclaim && idx.Count <= 7)
        {
            var first = FirstContent();
            if (first < 0) first = idx[0];
            var byWord = idx.FirstOrDefault(i => ExclaimRise.Contains(W(i)), -1);
            if (byWord >= 0 && idx.IndexOf(byWord) <= 1) arrows[byWord] = Rise;                       // ИК-5: «Какой вид!»
            else arrows[first] = Fall;                                                                // ИК-2: «Молчи, хрыч!»
            return;
        }
        // перечисление: три и больше коротких частей через запятую — подъём в конце каждой, кроме последней
        var items = new List<List<int>>();
        var cur = new List<int>();
        var p = a;
        foreach (var w in idx)
        {
            if (s.IndexOf(',', p, words[w].Start - p) >= 0 && cur.Count > 0) { items.Add(cur); cur = []; }
            cur.Add(w);
            p = words[w].Start + words[w].Length;
        }
        if (cur.Count > 0) items.Add(cur);
        var run = new List<List<int>>();
        void Close()
        {
            if (run.Count >= 3)
                foreach (var it in run.Take(run.Count - 1))
                    if (!arrows.ContainsKey(it[^1])) arrows[it[^1]] = Rise;
            run.Clear();
        }
        void Flush(bool closedByConj)
        {
            if (run.Count >= 4 || run.Count >= 3 && closedByConj) Close();
            run.Clear();
        }
        foreach (var it in items)
        {
            var conj = W(it[0]) is "и" or "да" or "или";
            if (it.Count <= 2 && !conj) run.Add(it);
            else if (conj && it.Count <= 4 && run.Count >= 2) { run.Add(it); Flush(true); }
            else Flush(false);
        }
        Flush(false);
    }
}
