using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KlavaDesk;

/// <summary>Абзац книги: обычный текст, заголовок (глава, часть) или стихи/эпиграф — последние в чтение не идут.</summary>
internal enum BookParaKind { Text, Heading, Verse }

internal sealed record BookPara(BookParaKind Kind, string Text);

/// <summary>
/// Книга из файла в абзацы. Форматы, в которых книги обычно качают: .txt (UTF-8, UTF-16, Windows-1251, KOI8-R —
/// кодировка определяется сама), .fb2 и .fb2.zip, .epub, .docx, .odt, .rtf, .html. PDF и старый .doc не читаются —
/// об этом честная ошибка с советом, во что пересохранить.
/// </summary>
internal static partial class BookFormats
{
    public const string DialogFilter =
        "Книги (txt, fb2, epub, docx, odt, rtf, html, zip)|*.txt;*.fb2;*.epub;*.docx;*.odt;*.rtf;*.htm;*.html;*.xhtml;*.zip|Все файлы|*.*";

    public static List<BookPara> Load(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        var bytes = File.ReadAllBytes(path);
        return FromBytes(name, bytes);
    }

    private static List<BookPara> FromBytes(string name, byte[] bytes)
    {
        if (name.EndsWith(".pdf"))
            throw new InvalidDataException("PDF не читаю: в нём нет абзацев, только буквы по координатам. Скачай книгу в fb2, epub или txt.");
        if (name.EndsWith(".doc") || name.EndsWith(".mobi") || name.EndsWith(".djvu"))
            throw new InvalidDataException($"Формат {Path.GetExtension(name)} не читаю. Скачай книгу в fb2, epub, docx или txt.");
        var zip = bytes.Length > 4 && bytes[0] == 'P' && bytes[1] == 'K';
        if (name.EndsWith(".epub")) return Epub(bytes);
        if (name.EndsWith(".docx")) return Docx(bytes);
        if (name.EndsWith(".odt")) return Odt(bytes);
        if (zip) return FromZip(bytes);
        if (name.EndsWith(".fb2") || LooksLikeFb2(bytes)) return Fb2(bytes);
        if (name.EndsWith(".rtf") || StartsWith(bytes, "{\\rtf")) return Rtf(bytes);
        if (name.EndsWith(".htm") || name.EndsWith(".html") || name.EndsWith(".xhtml")) return Html(DecodeText(bytes));
        return PlainText(DecodeText(bytes));
    }

    // ---------------- кодировка ----------------

    /// <summary>
    /// Текст из байтов: BOM → UTF-8/UTF-16; без BOM — строгий UTF-8, UTF-16 по нулевым байтам, иначе 1251 или KOI8-R.
    /// У русского текста строчные буквы в 1251 лежат в 0xE0–0xFF, в KOI8-R — в 0xC0–0xDF: чего больше, та и кодировка.
    /// </summary>
    public static string DecodeText(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
        var n = Math.Min(b.Length, 4000);
        int zeroOdd = 0, zeroEven = 0;
        for (var i = 0; i < n; i++) if (b[i] == 0) { if (i % 2 == 1) zeroOdd++; else zeroEven++; }
        if (zeroOdd > n / 8) return Encoding.Unicode.GetString(b);
        if (zeroEven > n / 8) return Encoding.BigEndianUnicode.GetString(b);
        try { return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(b); }
        catch (DecoderFallbackException) { }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        long hi = 0, mid = 0;
        foreach (var x in b)
        {
            if (x >= 0xE0) hi++;
            else if (x >= 0xC0) mid++;
        }
        return Encoding.GetEncoding(mid > hi ? 20866 : 1251).GetString(b);
    }

    private static bool StartsWith(byte[] b, string ascii) =>
        b.Length >= ascii.Length && Encoding.ASCII.GetString(b, 0, ascii.Length) == ascii;

    private static bool LooksLikeFb2(byte[] b)
    {
        var head = Encoding.ASCII.GetString(b, 0, Math.Min(b.Length, 600));
        return head.Contains("<FictionBook", StringComparison.Ordinal);
    }

    // ---------------- zip: fb2.zip и прочие архивы с одной книгой ----------------

    private static List<BookPara> FromZip(byte[] bytes)
    {
        using var z = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        if (z.GetEntry("META-INF/container.xml") is not null) return Epub(bytes);
        if (z.GetEntry("word/document.xml") is not null) return Docx(bytes);
        if (z.GetEntry("content.xml") is not null && z.GetEntry("mimetype") is not null) return Odt(bytes);
        var entry = z.Entries.Where(e => e.Length > 0)
            .OrderByDescending(e => Path.GetExtension(e.Name).ToLowerInvariant() switch { ".fb2" => 3, ".epub" => 2, ".txt" => 1, _ => 0 })
            .ThenByDescending(e => e.Length).FirstOrDefault()
            ?? throw new InvalidDataException("Архив пустой.");
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return FromBytes(entry.Name.ToLowerInvariant(), ms.ToArray());
    }

    // ---------------- fb2 ----------------

    private static List<BookPara> Fb2(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);   // fb2 часто в windows-1251 (объявлено в заголовке)
        var doc = XDocument.Load(new MemoryStream(bytes), LoadOptions.None);
        var res = new List<BookPara>();
        foreach (var body in doc.Descendants().Where(e => e.Name.LocalName == "body"))
        {
            var kind = (string?)body.Attribute("name");
            if (kind is "notes" or "comments") continue;   // сноски — не чтение
            Fb2Walk(body, res, BookParaKind.Text);
        }
        return res;
    }

    private static void Fb2Walk(XElement e, List<BookPara> res, BookParaKind ctx)
    {
        foreach (var c in e.Elements())
        {
            switch (c.Name.LocalName)
            {
                case "title": Fb2Walk(c, res, BookParaKind.Heading); break;
                case "subtitle": Add(res, BookParaKind.Heading, c.Value); break;
                case "epigraph" or "poem" or "text-author": Fb2Walk(c, res, BookParaKind.Verse); break;
                case "p" or "v": Add(res, ctx, Fb2Inline(c)); break;
                case "image" or "binary" or "description": break;
                default: Fb2Walk(c, res, ctx); break;
            }
        }
    }

    /// <summary>Текст абзаца fb2 без ссылок на сноски (&lt;a type="note"&gt;).</summary>
    private static string Fb2Inline(XElement p)
    {
        var sb = new StringBuilder();
        foreach (var n in p.DescendantNodes())
        {
            if (n is not XText t) continue;
            if (t.Parent is { } par && par.Name.LocalName == "a" && ((string?)par.Attribute("type") == "note" || t.Value.Trim().All(ch => char.IsDigit(ch) || ch is '[' or ']' or '*'))) continue;
            sb.Append(t.Value);
        }
        return sb.ToString();
    }

    // ---------------- epub ----------------

    private static List<BookPara> Epub(byte[] bytes)
    {
        using var z = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        string Read(string p)
        {
            var e = z.GetEntry(p) ?? z.Entries.FirstOrDefault(x => x.FullName.Equals(p, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException($"в epub нет {p}");
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return DecodeText(ms.ToArray());
        }
        var container = XDocument.Parse(Read("META-INF/container.xml"));
        var opfPath = container.Descendants().First(e => e.Name.LocalName == "rootfile").Attribute("full-path")!.Value;
        var opf = XDocument.Parse(Read(opfPath));
        var dir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
        var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
            .ToDictionary(e => (string)e.Attribute("id")!, e => (string)e.Attribute("href")!);
        var res = new List<BookPara>();
        foreach (var idref in opf.Descendants().Where(e => e.Name.LocalName == "itemref").Select(e => (string?)e.Attribute("idref")))
        {
            if (idref is null || !manifest.TryGetValue(idref, out var href)) continue;
            var full = (dir.Length > 0 ? dir + "/" : "") + Uri.UnescapeDataString(href.Split('#')[0]);
            try { res.AddRange(Html(Read(full))); }
            catch (InvalidDataException) { }
        }
        return res;
    }

    // ---------------- docx / odt ----------------

    private static List<BookPara> Docx(byte[] bytes)
    {
        using var z = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var s = (z.GetEntry("word/document.xml") ?? throw new InvalidDataException("в docx нет текста")).Open();
        var doc = XDocument.Load(s);
        var res = new List<BookPara>();
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "p"))
        {
            var sb = new StringBuilder();
            foreach (var r in p.Descendants())
            {
                if (r.Name.LocalName == "t") sb.Append(r.Value);
                else if (r.Name.LocalName is "tab" or "br") sb.Append(' ');
            }
            var style = p.Descendants().FirstOrDefault(e => e.Name.LocalName == "pStyle")?.Attributes().FirstOrDefault(a => a.Name.LocalName == "val")?.Value ?? "";
            var heading = style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) || style.StartsWith("Title", StringComparison.OrdinalIgnoreCase)
                          || style.Contains("Заголов", StringComparison.OrdinalIgnoreCase);
            Add(res, heading ? BookParaKind.Heading : BookParaKind.Text, sb.ToString());
        }
        return res;
    }

    private static List<BookPara> Odt(byte[] bytes)
    {
        using var z = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        using var s = (z.GetEntry("content.xml") ?? throw new InvalidDataException("в odt нет текста")).Open();
        var doc = XDocument.Load(s);
        var res = new List<BookPara>();
        foreach (var p in doc.Descendants().Where(e => e.Name.LocalName is "p" or "h"))
        {
            if (p.Ancestors().Any(a => a.Name.LocalName is "p" or "h" or "note")) continue;
            var sb = new StringBuilder();
            foreach (var n in p.DescendantNodes())
            {
                if (n is XText t && !t.Ancestors().Any(a => a.Name.LocalName == "note")) sb.Append(t.Value);
                else if (n is XElement e && e.Name.LocalName is "s" or "tab" or "line-break") sb.Append(' ');
            }
            Add(res, p.Name.LocalName == "h" ? BookParaKind.Heading : BookParaKind.Text, sb.ToString());
        }
        return res;
    }

    // ---------------- rtf ----------------

    /// <summary>RTF разбирает сам RichEdit Windows — ему нужен STA-поток, а книга грузится в фоне.</summary>
    private static List<BookPara> Rtf(byte[] bytes)
    {
        string? text = null;
        Exception? error = null;
        var th = new Thread(() =>
        {
            try
            {
                using var rtb = new RichTextBox();
                rtb.Rtf = Encoding.Latin1.GetString(bytes);
                text = rtb.Text;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { error = ex; }
        });
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        th.Join();
        if (text is null) throw new InvalidDataException($"RTF не разобрался: {error?.Message}");
        return PlainText(text);
    }

    // ---------------- html ----------------

    [GeneratedRegex(@"<(script|style|head)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlJunk();
    [GeneratedRegex(@"<h[1-6]\b[^>]*>(.*?)</h[1-6]\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlHeading();
    [GeneratedRegex(@"<(?:/p|/div|br\s*/?|/li|/blockquote|/tr|p\b[^>]*|div\b[^>]*)>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBreak();
    [GeneratedRegex(@"<sup\b[^>]*>.*?</sup\s*>|<a\b[^>]*(?:noteref|footnote|class=""note)[^>]*>.*?</a\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HtmlNoteRef();
    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();

    private static List<BookPara> Html(string html)
    {
        var s = HtmlJunk().Replace(html, " ");
        s = HtmlNoteRef().Replace(s, "");
        s = HtmlHeading().Replace(s, m => "\n\u0001" + HtmlTag().Replace(m.Groups[1].Value, " ") + "\n");
        s = HtmlBreak().Replace(s, "\n");
        s = WebUtility.HtmlDecode(HtmlTag().Replace(s, " "));
        var res = new List<BookPara>();
        foreach (var line in s.Split('\n'))
        {
            if (line.StartsWith('\u0001')) Add(res, BookParaKind.Heading, line[1..]);
            else Add(res, BookParaKind.Text, line);
        }
        return res;
    }

    // ---------------- txt ----------------

    [GeneratedRegex(@"^\s*(глава|часть|книга|том|пролог|эпилог|предисловие|послесловие|приложение|chapter|part)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HeadingWord();
    [GeneratedRegex(@"^\s*([IVXLC]+|\d{1,3})\s*[.)]?\s*$")]
    private static partial Regex HeadingNumber();

    /// <summary>
    /// Простой текст: абзац — строка с отступом или блок, отделённый пустой строкой. Если строки «жёстко» перенесены
    /// по ~70 символов (старые txt), они склеиваются обратно, переносы «сло-\nво» — тоже.
    /// </summary>
    private static List<BookPara> PlainText(string text)
    {
        // управляющие символы (в старых txt бывают \x14, \x1A и прочие) — в пробел: в тексте им не место
        text = new string(text.Select(c => c < ' ' && c is not '\n' and not '\r' and not '\t' ? ' ' : c).ToArray());
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var nonEmpty = lines.Where(l => l.Trim().Length > 0).ToList();
        // стихи (басня, сборник): строки короткие, почти все с заглавной, многие кончаются запятой или вовсе без знака —
        // их не склеивать, а отдать строками (Verse): чтение стихов собирается отдельно, с переносами строк
        if (nonEmpty.Count >= 4)
        {
            var lens = nonEmpty.Select(l => l.Trim().Length).Order().ToList();
            var median = lens[lens.Count / 2];
            var capital = nonEmpty.Count(l => l.TrimStart(' ', '\t', '—', '-', '«', '"', '(').FirstOrDefault() is var c && char.IsUpper(c));
            var dashes = nonEmpty.Count(l => l.TrimStart().FirstOrDefault() is '—' or '-' or '–');
            var open = nonEmpty.Count(l => l.TrimEnd()[^1] is ',' or ';' or ':' or '—' or '-' || char.IsLetter(l.TrimEnd()[^1]));
            if (median <= 60 && capital >= nonEmpty.Count * 0.7 && dashes < nonEmpty.Count * 0.3 && open >= nonEmpty.Count * 0.3)
                return nonEmpty.Select(l => new BookPara(ClassifyLine(l.Trim()) == BookParaKind.Heading ? BookParaKind.Heading : BookParaKind.Verse,
                    System.Text.RegularExpressions.Regex.Replace(l.Trim(), @"\s+", " "))).ToList();
        }
        // жёсткий перенос: почти все строки короче 90 символов и заметная доля заканчивается не концом фразы
        var wrapped = nonEmpty.Count > 20 && nonEmpty.Count(l => l.TrimEnd().Length <= 90) > nonEmpty.Count * 0.9
                      && nonEmpty.Count(l => !".!?…:»\"".Contains(l.TrimEnd()[^1])) > nonEmpty.Count * 0.4;
        var res = new List<BookPara>();
        var cur = new StringBuilder();
        void Flush()
        {
            var p = cur.ToString().Trim();
            cur.Clear();
            if (p.Length == 0) return;
            res.Add(new BookPara(ClassifyLine(p), p));
        }
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0) { Flush(); continue; }
            var indented = line.Length > 0 && (line[0] == ' ' || line[0] == '\t' || line[0] == ' ');
            var dialogue = line.TrimStart().Length > 0 && line.TrimStart()[0] is '-' or '—' or '–';
            if (!wrapped || indented || dialogue) Flush();
            if (cur.Length > 0)
            {
                // «сло-» + «во» на следующей строке — одно слово
                if (cur[^1] == '-' && cur.Length > 1 && char.IsLower(cur[^2]) && char.IsLower(line.TrimStart().FirstOrDefault()))
                    cur.Length--;
                else cur.Append(' ');
            }
            cur.Append(line.Trim());
            if (!wrapped) Flush();
        }
        Flush();
        return res;
    }

    private static BookParaKind ClassifyLine(string p)
    {
        if (p.Length > 90) return BookParaKind.Text;
        if (HeadingWord().IsMatch(p) || HeadingNumber().IsMatch(p)) return BookParaKind.Heading;
        var letters = p.Where(char.IsLetter).ToList();
        if (letters.Count >= 3 && letters.All(char.IsUpper)) return BookParaKind.Heading;
        return BookParaKind.Text;
    }

    private static void Add(List<BookPara> res, BookParaKind kind, string text)
    {
        var t = Regex.Replace(text, @"\s+", " ").Trim();
        if (t.Length > 0) res.Add(new BookPara(kind, t));
    }
}
