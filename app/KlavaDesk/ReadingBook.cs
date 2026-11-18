using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>Страница для чтения: чистый текст и он же с разметкой (ударения, стрелки, [как читать]).</summary>
/// <param name="Verse">Стихи: переносы строк — строки стиха, а не абзацы.</param>
internal sealed record ReadingPage(int Number, string Plain, string Marked, int Offset, double Score, bool Verse = false);

/// <summary>Книга, разобранная для тренировки голоса: ~20 страниц, выбранных по всей книге.</summary>
internal sealed class ReadingBook
{
    public int Version { get; set; }
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string AccentBy { get; set; } = "";
    public int BookChars { get; set; }
    public int Candidates { get; set; }
    public double Seconds { get; set; }
    public DateTime Built { get; set; }
    public List<ReadingPage> Pages { get; set; } = [];

    public override string ToString() => $"{Title} — {Pages.Count} стр.";
}

/// <summary>
/// Книга → страницы для тренировки голоса. Книга чистится (OCR-мусор, сноски, заголовки, эпиграфы и стихи, прямые
/// кавычки и дефисы вместо тире), режется на куски ~650 символов (40–50 с чтения) по границам предложений и абзацев,
/// каждый кусок оценивается: понятные слова (по словарю — меньше устаревших и битых), не слишком длинные фразы, без
/// латиницы и цифр, диалоги, вопросы, восклицания. Из лучших жадно набираются 20 страниц так, чтобы вместе они
/// покрыли как можно больше сочетаний звуков и были из разных мест книги. Потом нейросеть RUAccent ставит ударения
/// (через сервер голоса, а если он не поднят — разовым процессом), ReadingProsody — интонацию и «как читать».
/// Готовое кэшируется в data\reading\cache и выгружается текстом в data\reading.
/// </summary>
internal static partial class ReadingBooks
{
    public const int FormatVersion = 5;
    public const int DefaultPages = 20;
    private const int TargetChars = 650, MinChars = 480, MaxChars = 850;
    public const string BuiltinId = "builtin";

    public static string Dir(KlavaConfig cfg) => cfg.Abs(@"data\reading");
    public static string BooksDir(KlavaConfig cfg) => cfg.Abs(@"data\reading\books");
    private static string CacheDir(KlavaConfig cfg) => cfg.Abs(@"data\reading\cache");
    private static string CacheFile(KlavaConfig cfg, string id) => Path.Combine(CacheDir(cfg), id + ".json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Разобранные книги из кэша (новые сверху).</summary>
    public static List<ReadingBook> Library(KlavaConfig cfg)
    {
        var res = new List<ReadingBook>();
        if (!Directory.Exists(CacheDir(cfg))) return res;
        foreach (var f in Directory.GetFiles(CacheDir(cfg), "*.json"))
        {
            try
            {
                var b = JsonSerializer.Deserialize<ReadingBook>(File.ReadAllText(f), Json);
                if (b is { Version: FormatVersion } && b.Id != BuiltinId && b.Pages.Count > 0) res.Add(b);
            }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }
        return res.OrderByDescending(b => b.Built).ToList();
    }

    public static string? LastChoice(KlavaConfig cfg)
    {
        try { return File.ReadAllText(Path.Combine(Dir(cfg), "last.txt")).Trim(); }
        catch (IOException) { return null; }
    }

    public static void SaveLastChoice(KlavaConfig cfg, string id)
    {
        try
        {
            Directory.CreateDirectory(Dir(cfg));
            File.WriteAllText(Path.Combine(Dir(cfg), "last.txt"), id);
        }
        catch (IOException) { }
    }

    private static string Hash(byte[] b) => Convert.ToHexString(SHA1.HashData(b))[..16].ToLowerInvariant();

    private static ReadingBook? FromCache(KlavaConfig cfg, string id)
    {
        var f = CacheFile(cfg, id);
        if (!File.Exists(f)) return null;
        try
        {
            var b = JsonSerializer.Deserialize<ReadingBook>(File.ReadAllText(f), Json);
            return b is { Version: FormatVersion } && b.Pages.Count > 0 ? b : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { return null; }
    }

    private static void Save(KlavaConfig cfg, ReadingBook b)
    {
        Directory.CreateDirectory(CacheDir(cfg));
        File.WriteAllText(CacheFile(cfg, b.Id), JsonSerializer.Serialize(b, Json));
    }

    /// <summary>Встроенные отрывки с ударениями и стрелками (разметка «как читать» у них ручная).</summary>
    public static async Task<ReadingBook> BuiltinAsync(KlavaConfig cfg, IProgress<string>? progress, CancellationToken ct)
    {
        var id = BuiltinId;
        var hash = Hash(Encoding.UTF8.GetBytes(string.Join("\n\n", ReadingTexts.Builtin)));
        if (FromCache(cfg, id) is { } cached && cached.Source == hash) return cached;
        var sw = Stopwatch.StartNew();
        var plain = ReadingTexts.Builtin.ToList();
        var (acc, by) = await AccentAsync(cfg, plain, progress, ct);
        var book = new ReadingBook
        {
            Version = FormatVersion, Id = id, Title = "Встроенные отрывки", Source = hash, AccentBy = by, Built = DateTime.Now,
            BookChars = plain.Sum(p => p.Length), Candidates = plain.Count,
            Pages = plain.Select((p, i) => new ReadingPage(i + 1, p, ReadingProsody.Annotate(p, acc[i], -1), i, 1)).ToList(),
        };
        book.Seconds = sw.Elapsed.TotalSeconds;
        Save(cfg, book);
        return book;
    }

    /// <summary>Встроенные отрывки без ударений — показать сразу, пока ударения считаются.</summary>
    public static ReadingBook BuiltinPlain() => new()
    {
        Version = FormatVersion, Id = BuiltinId, Title = "Встроенные отрывки",
        Pages = ReadingTexts.Builtin.Select((p, i) => new ReadingPage(i + 1, p, p, i, 1)).ToList(),
    };

    /// <summary>Разобрать книгу (или взять из кэша). Долго только в первый раз: ~5–20 с на ударения.</summary>
    public static async Task<ReadingBook> BuildAsync(KlavaConfig cfg, string path, int pages, bool force, IProgress<string>? progress, CancellationToken ct)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var id = $"{Hash(bytes)}_{pages}";
        if (!force && FromCache(cfg, id) is { } cached) return cached;
        var sw = Stopwatch.StartNew();
        progress?.Report("Читаю книгу…");
        var paras = await Task.Run(() => BookFormats.Load(path), ct);
        progress?.Report($"Чищу и выбираю {pages} страниц из книги…");
        var (picked, candidates, chars) = await Task.Run(() =>
        {
            LiteStress.Init(cfg);
            return Select(paras, pages);
        }, ct);
        if (picked.Count == 0) throw new InvalidDataException("В книге не нашлось связного русского текста для чтения.");
        var plain = picked.Select(p => p.Text).ToList();
        var (acc, by) = await AccentAsync(cfg, plain, progress, ct);
        progress?.Report("Расставляю интонацию и «как читать»…");
        var book = new ReadingBook
        {
            Version = FormatVersion, Id = id, Title = TitleOf(path), Source = path, AccentBy = by, Built = DateTime.Now,
            BookChars = chars, Candidates = candidates,
            Pages = picked.Select((p, i) => new ReadingPage(i + 1, p.Text, ReadingProsody.Annotate(p.Text, acc[i], i, p.Verse), p.Offset, Math.Round(p.Score, 3), p.Verse)).ToList(),
        };
        book.Seconds = Math.Round(sw.Elapsed.TotalSeconds, 1);
        Save(cfg, book);
        try { Export(cfg, book); } catch (IOException) { }
        Log.Write($"чтение: книга «{book.Title}» — {book.Pages.Count} стр. из {candidates} кусков, ударения: {by}, {book.Seconds} с");
        return book;
    }

    private static string TitleOf(string path)
    {
        var t = Path.GetFileName(path);
        foreach (var ext in new[] { ".zip", ".fb2", ".epub", ".txt", ".docx", ".odt", ".rtf", ".html", ".htm", ".xhtml" })
            if (t.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) t = t[..^ext.Length];
        return t.Replace('_', ' ').Trim();
    }

    /// <summary>Страницы текстом: распечатать или читать с телефона. Возвращает путь.</summary>
    public static string Export(KlavaConfig cfg, ReadingBook b)
    {
        Directory.CreateDirectory(Dir(cfg));
        var bad = Path.GetInvalidFileNameChars();
        var name = new string(b.Title.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        var file = Path.Combine(Dir(cfg), $"{name} — страницы для чтения.txt");
        var sb = new StringBuilder();
        sb.AppendLine($"{b.Title} — {b.Pages.Count} страниц для тренировки голоса");
        sb.AppendLine("Знак ́ после гласной — ударение. ↗ — голос вверх на этом слове (вопрос, перечисление), ↘ — нажим и вниз");
        sb.AppendLine("(«кто/что/где?», восклицание, приказ). В [квадратных скобках] — как читать.");
        sb.AppendLine($"Ударения: {b.AccentBy}.");
        foreach (var p in b.Pages)
        {
            sb.AppendLine();
            sb.AppendLine($"———— Страница {p.Number} ————");
            sb.AppendLine(p.Marked);
        }
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(true));
        return file;
    }

    // ---------------- ударения нейросетью ----------------

    /// <summary>
    /// Ударения RUAccent для каждого текста («+» перед ударной гласной). Сначала сервер голоса (модель уже в памяти),
    /// нет сервера — разовый процесс tts\accent_book.py из того же окружения, нет и его — null (словарь accents.bin).
    /// </summary>
    public static async Task<(List<string?> Marked, string By)> AccentAsync(KlavaConfig cfg, IReadOnlyList<string> texts, IProgress<string>? progress, CancellationToken ct)
    {
        var input = texts.Select(ReadingProsody.StripBrackets).ToList();
        var res = new List<string?>(new string?[texts.Count]);
        if (!cfg.LiteSpeech || File.Exists(cfg.Abs(cfg.TtsPython)))
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
                var url = $"{cfg.TtsUrl.TrimEnd('/')}/accent";
                for (var i = 0; i < input.Count; i++)
                {
                    progress?.Report($"Нейросеть ставит ударения: страница {i + 1} из {input.Count}…");
                    using var r = await http.PostAsJsonAsync(url, new { text = input[i] }, ct);
                    if (!r.IsSuccessStatusCode) throw new HttpRequestException($"сервер голоса ответил {(int)r.StatusCode}");
                    res[i] = JsonNode.Parse(await r.Content.ReadAsStringAsync(ct))?["text"]?.GetValue<string>();
                }
                if (res.All(x => x is not null)) return (res, "нейросеть RUAccent (сервер голоса)");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                Log.Write($"чтение: сервер голоса не поставил ударения ({ex.Message}) — запускаю RUAccent отдельно");
            }
        }
        var py = cfg.Abs(cfg.TtsPython);
        var script = cfg.Abs(@"tts\accent_book.py");
        if (File.Exists(py) && File.Exists(script))
        {
            progress?.Report("Загружаю нейросеть ударений RUAccent (≈15 с)…");
            try
            {
                var outp = await RunAccentProcessAsync(py, script, input, ct);
                if (outp is not null && outp.Count == input.Count) return (outp.Cast<string?>().ToList(), "нейросеть RUAccent");
            }
            catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                Log.Write($"чтение: RUAccent не запустился ({ex.Message}) — ударения по словарю");
            }
        }
        LiteStress.Init(cfg);
        return (new List<string?>(new string?[texts.Count]), LiteStress.HasDictionary ? "словарь accents.bin (без нейросети)" : "нет — ни нейросети, ни словаря");
    }

    private static async Task<List<string>?> RunAccentProcessAsync(string py, string script, List<string> input, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(py, $"\"{script}\"")
        {
            WorkingDirectory = Path.GetDirectoryName(script)!,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi) ?? throw new IOException("процесс не запустился");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        await p.StandardInput.WriteAsync(JsonSerializer.Serialize(new { texts = input }));
        p.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(4));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new IOException("RUAccent думал дольше 4 минут");
        }
        var stdout = await outTask;
        if (p.ExitCode != 0)
        {
            var err = await errTask;
            throw new IOException($"код {p.ExitCode}: {err[^Math.Min(err.Length, 300)..]}");
        }
        return (JsonNode.Parse(stdout)?["texts"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").ToList();
    }

    // ---------------- чистка и выбор страниц ----------------

    internal sealed record Chunk(string Text, int Offset, double Base, HashSet<string> Units, int Index, bool Verse = false)
    {
        public double Score { get; set; }
    }

    [GeneratedRegex(@"(?:^|(?<=\s))[A-Za-z]>")]
    private static partial Regex OcrArrow();
    [GeneratedRegex(@"\[\d{1,3}\]|\{\d{1,3}\}|\*\)|(?<=[а-яёА-ЯЁ.,!?»])\*+|(?<=[а-яё])\d{1,2}(?=[\s.,;:!?»]|$)")]
    private static partial Regex Footnote();
    [GeneratedRegex(@"^\s*(?:--?|–|—)\s*")]
    private static partial Regex LeadDash();
    [GeneratedRegex(@"(?<=\S)\s+(?:--?|–|—)\s+")]
    private static partial Regex MidDash();
    [GeneratedRegex(@"[A-Za-zА-Яа-яЁё]+")]
    private static partial Regex AnyWord();
    [GeneratedRegex(@"\s+([,.;:!?…»)])")]
    private static partial Regex SpaceBeforePunct();

    private static readonly Dictionary<char, char> Lookalike = new()
    {
        ['A'] = 'А', ['B'] = 'В', ['C'] = 'С', ['E'] = 'Е', ['H'] = 'Н', ['K'] = 'К', ['M'] = 'М', ['O'] = 'О', ['P'] = 'Р',
        ['T'] = 'Т', ['X'] = 'Х', ['a'] = 'а', ['c'] = 'с', ['e'] = 'е', ['o'] = 'о', ['p'] = 'р', ['x'] = 'х', ['y'] = 'у',
    };

    /// <summary>Абзац к виду для чтения: тире, «ёлочки», многоточие, без сносок и OCR-мусора.</summary>
    public static string Normalize(string s)
    {
        s = s.Replace(' ', ' ').Replace("­", "").Replace('\t', ' ');
        s = OcrArrow().Replace(s, "");
        s = Footnote().Replace(s, "");
        // латиница, похожая на кириллицу, внутри русских слов и в инициалах («Карловичу P.»)
        s = AnyWord().Replace(s, m =>
        {
            var w = m.Value;
            var cyr = w.Count(ReadingProsody.IsCyr);
            var lat = w.Length - cyr;
            if (lat == 0 || (cyr == 0 && !(w.Length == 1 && m.Index + 1 < s.Length && s[m.Index + 1] == '.'))) return w;
            if (!w.All(ch => ReadingProsody.IsCyr(ch) || Lookalike.ContainsKey(ch))) return w;
            return new string(w.Select(ch => Lookalike.TryGetValue(ch, out var r) ? r : ch).ToArray());
        });
        s = LeadDash().Replace(s, "— ", 1);
        s = MidDash().Replace(s, " — ");
        s = Regex.Replace(s, @"\.{3,}", "…");
        s = Quotes(s);
        s = SpaceBeforePunct().Replace(s, "$1");
        s = Regex.Replace(s, @"([«(])\s+", "$1");
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return s;
    }

    /// <summary>Прямые и «лапки» кавычки → «ёлочки»: открывающая — в начале или после пробела/тире/скобки.</summary>
    private static string Quotes(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '"' or '“' or '”' or '„')
            {
                // закрывающая — перед пробелом, знаком или концом строки; открывающая — после пробела, тире, скобки
                var prev = i > 0 ? s[i - 1] : ' ';
                var next = i + 1 < s.Length ? s[i + 1] : ' ';
                var close = char.IsWhiteSpace(next) || next is ',' or '.' or ';' or ':' or '!' or '?' or '…' or ')' or '-' or '—';
                var open = c == '„' || !close && (char.IsWhiteSpace(prev) || prev is '(' or '—' or '-' or '[' or '«' || i == 0);
                sb.Append(open ? '«' : '»');
            }
            else sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool Junk(string t)
    {
        if (!t.Any(ReadingProsody.IsCyr)) return true;
        var low = t.ToLowerInvariant();
        foreach (var bad in new[] { "http", "www.", "@", "ocr:", "origin:", "isbn", "©", "все права", "spellcheck", "fb2", "библиотек", "электронн" })
            if (low.Contains(bad)) return true;
        return t.Trim('-', '*', '.', '=', '_', ' ', '…', '~', '#').Length == 0;
    }

    private static bool Short(string t) => t.Length < 70 && !t.StartsWith('—');

    /// <summary>Абзацы для чтения: без мусора, заголовков, стихов, эпиграфов (короткие строки сразу после заголовка).</summary>
    private static List<(string Text, bool Break)> Clean(List<BookPara> paras)
    {
        var list = paras.Select(p => (p.Kind, Text: Normalize(p.Text))).Where(p => p.Text.Length > 0 && !Junk(p.Text)).ToList();
        var keep = new bool[list.Count];
        for (var i = 0; i < list.Count; i++) keep[i] = list[i].Kind == BookParaKind.Text;
        // эпиграф: после заголовка до первого длинного абзаца — всё короткое (до 12 строк)
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Kind != BookParaKind.Heading) continue;
            var j = i + 1;
            while (j < list.Count && list[j].Kind != BookParaKind.Heading && list[j].Text.Length < 90 && j - i <= 12) j++;
            if (j < list.Count && list[j].Text.Length >= 120 && j - i - 1 <= 12)
                for (var k = i + 1; k < j; k++) keep[k] = false;
        }
        // стихи, подписи, адреса: подряд две и больше коротких строк без тире
        for (var i = 0; i < list.Count; i++)
        {
            if (!Short(list[i].Text)) continue;
            var j = i;
            while (j < list.Count && Short(list[j].Text)) j++;
            if (j - i >= 2) for (var k = i; k < j; k++) keep[k] = false;
            i = j - 1;
        }
        var res = new List<(string, bool)>();
        var gap = false;
        for (var i = 0; i < list.Count; i++)
        {
            if (!keep[i]) { gap = true; continue; }
            res.Add((list[i].Text, gap));   // gap — перед абзацем был пропуск: страница через него не тянется
            gap = false;
        }
        return res;
    }

    /// <summary>Выбрать страницы: куски по ~650 символов, оценка, жадное покрытие сочетаний звуков.</summary>
    internal static (List<Chunk> Picked, int Candidates, int BookChars) Select(List<BookPara> paras, int pages)
    {
        var verseLines = paras.Count(p => p.Kind == BookParaKind.Verse);
        if (verseLines >= 4 && verseLines > paras.Count(p => p.Kind == BookParaKind.Text) * 2) return SelectVerse(paras, pages);
        var clean = Clean(paras);
        var bookChars = clean.Sum(p => p.Text.Length);
        // единицы — предложения; страница не тянется через заголовок или выкинутый кусок
        var chunks = new List<(List<(string S, bool ParaStart)> Parts, int Offset)>();
        var cur = new List<(string, bool)>();
        var curLen = 0;
        var offset = 0;
        var curOffset = 0;
        void Emit(bool force)
        {
            if (cur.Count > 0 && (curLen >= MinChars || force && curLen >= MinChars * 0.7)) chunks.Add((cur, curOffset));
            cur = [];
            curLen = 0;
        }
        foreach (var (text, gap) in clean)
        {
            if (gap) Emit(true);
            var first = true;
            foreach (var (a, b) in ReadingProsody.Sentences(text))
            {
                var sent = text[a..b];
                if (sent.Length > MaxChars) { Emit(true); first = false; continue; }   // фраза на полстраницы — не для чтения вслух
                if (curLen > 0 && curLen + sent.Length > MaxChars && curLen >= MinChars) Emit(false);
                if (cur.Count == 0) curOffset = offset + a;
                cur.Add((sent, first));
                curLen += sent.Length + 1;
                first = false;
            }
            offset += text.Length + 1;
            if (curLen >= TargetChars) Emit(false);   // абзац кончился, страница набрана — режем по абзацу
        }
        Emit(true);

        var scored = new List<Chunk>();
        for (var i = 0; i < chunks.Count; i++)
        {
            var sb = new StringBuilder();
            foreach (var (s, paraStart) in chunks[i].Parts)
            {
                if (sb.Length > 0) sb.Append(paraStart ? '\n' : ' ');
                sb.Append(s);
            }
            var text = BalanceQuotes(sb.ToString());
            var (score, units) = Rate(text);
            if (!chunks[i].Parts[0].ParaStart) score *= 0.9;   // начало посреди абзаца читать чуть хуже
            if (score > 0) scored.Add(new Chunk(text, chunks[i].Offset, score, units, i));
        }
        return Pick(scored, pages, chunks.Count, bookChars);
    }

    /// <summary>
    /// Стихи (басня, сборник): строки не склеиваются. Короткое стихотворение — одна страница целиком, длинное —
    /// страницы по ~650 символов, разрез только в конце предложения.
    /// </summary>
    private static (List<Chunk> Picked, int Candidates, int BookChars) SelectVerse(List<BookPara> paras, int pages)
    {
        var lines = paras.Where(p => p.Kind == BookParaKind.Verse).Select(p => Normalize(p.Text)).Where(t => t.Length > 0 && !Junk(t)).ToList();
        var total = lines.Sum(l => l.Length + 1);
        var parts = new List<(List<string> Lines, int Offset)>();
        var cur = new List<string>();
        int len = 0, offset = 0, start = 0;
        foreach (var l in lines)
        {
            if (cur.Count == 0) start = offset;
            cur.Add(l);
            len += l.Length + 1;
            offset += l.Length + 1;
            var end = l.TrimEnd('»', '"', ')', ' ');
            var sentenceEnd = end.Length > 0 && end[^1] is '.' or '!' or '?' or '…';
            if (total > 1400 && (len >= TargetChars && sentenceEnd || len >= 1100))
            {
                parts.Add((cur, start));
                cur = [];
                len = 0;
            }
        }
        if (cur.Count > 0)
        {
            if (len < 300 && parts.Count > 0) parts[^1].Lines.AddRange(cur);
            else parts.Add((cur, start));
        }
        var scored = new List<Chunk>();
        for (var i = 0; i < parts.Count; i++)
        {
            var text = BalanceQuotes(string.Join('\n', parts[i].Lines));
            var (score, units) = Rate(text);
            if (score <= 0 && text.Count(char.IsLetter) >= 100) (score, units) = (0.3, Units(text));   // короткий стих — тоже читается
            if (score > 0) scored.Add(new Chunk(text, parts[i].Offset, score, units, i, Verse: true));
        }
        return Pick(scored, pages, parts.Count, total);
    }

    /// <summary>Жадный выбор страниц: качество × новые сочетания звуков, из разных мест книги.</summary>
    private static (List<Chunk> Picked, int Candidates, int BookChars) Pick(List<Chunk> scored, int pages, int chunkCount, int bookChars)
    {
        if (scored.Count == 0) return ([], 0, bookChars);
        if (scored.Count <= pages)   // книжка короткая — читаются все куски подряд
        {
            foreach (var c in scored) c.Score = c.Base;
            return (scored.OrderBy(c => c.Index).ToList(), scored.Count, bookChars);
        }

        // вес сочетания звуков: редкое в книге — дороже
        var freq = new Dictionary<string, int>();
        foreach (var c in scored) foreach (var u in c.Units) freq[u] = freq.GetValueOrDefault(u) + 1;
        var covered = new Dictionary<string, int>();
        var picked = new List<Chunk>();
        var pool = scored.OrderByDescending(c => c.Base).Take(Math.Max(pages * 8, 60)).ToList();
        var floor = pool.Count > pages * 2 ? pool[Math.Min(pool.Count - 1, pages * 3)].Base * 0.6 : 0;
        var spacing = Math.Max(2.0, chunkCount / (double)Math.Max(1, pages));
        while (picked.Count < pages && pool.Count > 0)
        {
            double Gain(Chunk c) => c.Units.Sum(u => covered.GetValueOrDefault(u) switch { 0 => 1.0, 1 => 0.35, _ => 0 } / Math.Sqrt(freq[u]));
            var maxGain = pool.Max(Gain);
            Chunk? best = null;
            var bestScore = double.MinValue;
            foreach (var c in pool)
            {
                var near = picked.Count == 0 ? int.MaxValue : picked.Min(p => Math.Abs(p.Index - c.Index));
                if (near <= 1) continue;   // соседние куски — почти одно и то же место книги
                var spread = near < spacing / 2 ? 0.75 : 1.0;
                var s = c.Base * (0.55 + 0.45 * (maxGain > 0 ? Gain(c) / maxGain : 0)) * spread;
                if (c.Base < floor) s *= 0.5;
                if (s > bestScore) { bestScore = s; best = c; }
            }
            if (best is null) break;
            best.Score = bestScore;
            picked.Add(best);
            pool.Remove(best);
            foreach (var u in best.Units) covered[u] = covered.GetValueOrDefault(u) + 1;
        }
        return (picked.OrderBy(c => c.Index).ToList(), scored.Count, bookChars);
    }

    /// <summary>
    /// Страница, начатая или оборванная посреди цитаты: недостающие «ёлочки» — в начало или конец. Смотрятся только
    /// первый и последний абзацы: в OCR-книгах кавычки внутри страницы и так бывают кривыми.
    /// </summary>
    private static string BalanceQuotes(string t)
    {
        var firstEnd = t.IndexOf('\n');
        var first = firstEnd < 0 ? t : t[..firstEnd];
        var q = first.IndexOfAny(['«', '»']);
        if (q >= 0 && first[q] == '»' && !first.StartsWith('—') && !first.StartsWith('«')) t = "«" + t;
        var lastStart = t.LastIndexOf('\n') + 1;
        var last = t[lastStart..];
        var lq = last.LastIndexOfAny(['«', '»']);
        if (lq >= 0 && last[lq] == '«') t += "»";
        return t;
    }

    /// <summary>
    /// Оценка куска для чтения вслух (0 — не брать) и его «звуки» — сочетания букв внутри слов и на стыках
    /// (приблизительно дифоны: «ть», «щу», «ры», «жи»).
    /// </summary>
    private static (double Score, HashSet<string> Units) Rate(string text)
    {
        int letters = 0, cyr = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            if (ReadingProsody.IsCyr(ch)) cyr++;
        }
        if (letters < 200) return (0, []);   // короткие стихи SelectVerse берёт и без оценки
        var latin = (letters - cyr) / (double)letters;
        if (latin > 0.02) return (0, []);
        var digits = Regex.Matches(text, @"\d+").Count;
        if (digits > 3) return (0, []);

        var words = Regex.Matches(text, @"[А-Яа-яЁё]+").Select(m => m.Value).ToList();
        int checkedWords = 0, unknown = 0;
        if (LiteStress.HasDictionary)
        {
            var sentenceStart = true;
            foreach (Match m in Regex.Matches(text, @"[А-Яа-яЁё]+|[.!?…]"))
            {
                var w = m.Value;
                if (w.Length == 1 && ".!?…".Contains(w[0])) { sentenceStart = true; continue; }
                var name = char.IsUpper(w[0]) && !sentenceStart;   // имена собственные в словаре бывают не все
                sentenceStart = false;
                if (name || w.Length < 3 || w.Count(c => "аеёиоуыэюяАЕЁИОУЫЭЮЯ".IndexOf(c) >= 0) < 2) continue;
                checkedWords++;
                if (!LiteStress.Known(w)) unknown++;
            }
        }
        var u = checkedWords > 0 ? unknown / (double)checkedWords : 0;
        var known = Math.Clamp(1 - u * 6, 0, 1);
        if (known <= 0) return (0, []);

        var sentences = ReadingProsody.Sentences(text);
        var lens = sentences.Select(s => Regex.Matches(text[s.Start..s.End], @"[А-Яа-яЁё]+").Count).Where(n => n > 0).ToList();
        var avg = lens.Count > 0 ? lens.Average() : 0;
        var lenFactor = avg <= 22 ? 1 : Math.Max(0.3, 1 - (avg - 22) * 0.04);
        if (lens.Count > 0 && lens.Max() > 50) lenFactor *= 0.6;
        if (lens.Count < 3) lenFactor *= 0.7;   // два длиннющих предложения — тяжело читать

        var speech = 0;
        var inQuote = false;
        foreach (var line in text.Split('\n'))
        {
            var dialogue = line.TrimStart().StartsWith('—');
            foreach (var ch in line)
            {
                if (ch == '«') inQuote = true;
                else if (ch == '»') inQuote = false;
                if (dialogue || inQuote) speech++;
            }
        }
        var d = speech / (double)text.Length;
        var q = text.Count(c => c == '?');
        var e = text.Count(c => c == '!');
        var cues = Regex.Matches(text, @"(закрич|крикн|воскликн|шепн|шепот|шёпот|прошепт|вполголоса|засмеял|сердито|тихо\b|громко)", RegexOptions.IgnoreCase).Count;
        var score = known * (1 - 0.15 * digits) * lenFactor *
                    (1 + 0.4 * Math.Min(d, 0.6) / 0.6 + 0.12 * Math.Min(q, 3) + 0.08 * Math.Min(e, 3) + 0.1 * Math.Min(cues, 2));

        return (score, Units(text));
    }

    /// <summary>«Звуки» текста: сочетания букв внутри слов, начало и конец слова.</summary>
    private static HashSet<string> Units(string text)
    {
        var units = new HashSet<string>();
        foreach (var w in Regex.Matches(text, @"[А-Яа-яЁё]+").Select(m => m.Value))
        {
            var lw = w.ToLowerInvariant();
            units.Add("^" + lw[0]);
            units.Add(lw[^1] + "$");
            for (var k = 0; k + 1 < lw.Length; k++) units.Add(lw.Substring(k, 2));
        }
        return units;
    }
}
