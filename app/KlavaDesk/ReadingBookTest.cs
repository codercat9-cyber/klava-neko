using System.IO.Compression;
using System.Text;

namespace KlavaDesk;

/// <summary>
/// Проверка разбора книг без окна:
///   KlavaDesk.exe --reading-book &lt;книга&gt; [--force] [--pages N]  — разобрать, показать страницы и разметку
///   KlavaDesk.exe --reading-book --formats &lt;книга.txt&gt;           — та же книга, пересобранная в fb2 (1251), fb2.zip,
///                                                                  epub, docx, odt, html, rtf, UTF-16: абзацы должны совпасть
/// </summary>
internal static class ReadingBookTest
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\reading_test.log"));
        if (args[0] == "--formats") return Formats(args[1]);
        var file = args[0];
        var force = args.Contains("--force");
        var pi = Array.IndexOf(args, "--pages");
        var pages = pi >= 0 && pi + 1 < args.Length && int.TryParse(args[pi + 1], out var n) ? n : ReadingBooks.DefaultPages;
        var progress = new Progress<string>(s => Console.WriteLine($"  … {s}"));
        var book = ReadingBooks.BuildAsync(cfg, file, pages, force, progress, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine($"«{book.Title}»: {book.BookChars} символов текста, кусков {book.Candidates}, выбрано страниц {book.Pages.Count}, " +
                          $"ударения: {book.AccentBy}, {book.Seconds} с");
        var fails = 0;
        int words = 0, stressed = 0, loud = 0, quiet = 0, rise = 0, fall = 0;
        foreach (var p in book.Pages)
        {
            var body = System.Text.RegularExpressions.Regex.Replace(p.Marked, @"\[[^\]]*\]", " ");
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, @"[А-Яа-яЁё\u0301]+"))
            {
                var w = m.Value;
                if (w.Replace("\u0301", "").Count(c => "аеёиоуыэюяАЕЁИОУЫЭЮЯ".IndexOf(c) >= 0) < 2) continue;
                words++;
                if (w.Contains('\u0301') || w.Contains('ё') || w.Contains('Ё')) stressed++;
            }
            var hasLoud = p.Marked.Contains("[громче") || p.Marked.Contains(", громче");
            var hasQuiet = p.Marked.Contains("[тише") || p.Marked.Contains(", тише");
            loud += hasLoud ? 1 : 0;
            quiet += hasQuiet ? 1 : 0;
            rise += p.Marked.Count(c => c == ReadingProsody.Rise);
            fall += p.Marked.Count(c => c == ReadingProsody.Fall);
            Console.WriteLine($"  стр. {p.Number,2}: с {p.Offset,6}-го символа, {p.Plain.Length} симв., оценка {p.Score:F2}" +
                              $"{(hasLoud ? "" : " · нет «громче»")}{(hasQuiet ? "" : " · нет «тише»")}");
        }
        var cover = words > 0 ? stressed * 100.0 / words : 0;
        Console.WriteLine($"ударения у {cover:F1} % многосложных слов; страниц с «громче» {loud}, с «тише» {quiet}; стрелок ↗ {rise}, ↘ {fall}");
        if (book.Pages.Count == 0 || book.Candidates >= pages && book.Pages.Count < Math.Min(pages, 5)) { fails++; Console.WriteLine("ПЛОХО: мало страниц"); }
        if (cover < 90) { fails++; Console.WriteLine("ПЛОХО: ударения стоят не везде"); }
        if (loud < book.Pages.Count * 0.8 || quiet < book.Pages.Count * 0.8) { fails++; Console.WriteLine("ПЛОХО: мало громких/тихих мест"); }
        foreach (var p in book.Pages.Take(2))
        {
            Console.WriteLine($"\n———— Страница {p.Number} ————");
            Console.WriteLine(p.Marked);
        }
        Console.WriteLine($"\nтекстом: {ReadingBooks.Export(cfg, book)}");
        Console.WriteLine(fails == 0 ? "reading: OK" : $"reading: провалов {fails}");
        return fails == 0 ? 0 : 1;
    }

    /// <summary>Одна книга во всех форматах: разбор каждого должен дать те же абзацы, что и исходный txt.</summary>
    private static int Formats(string txt)
    {
        var src = BookFormats.Load(txt).Where(p => p.Kind == BookParaKind.Text).Select(p => System.Text.RegularExpressions.Regex.Replace(p.Text, @"\s+", " ")).Take(120).ToList();
        var dir = Path.Combine(Path.GetTempPath(), "klava_reading_formats");
        Directory.CreateDirectory(dir);
        static string X(string s) => System.Security.SecurityElement.Escape(s);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var files = new Dictionary<string, byte[]>();

        var fb2 = new StringBuilder("<?xml version=\"1.0\" encoding=\"windows-1251\"?>\n<FictionBook xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\"><body><title><p>Глава первая</p></title>");
        fb2.Append("<epigraph><p>Береги честь смолоду.</p></epigraph><section>");
        foreach (var p in src) fb2.Append("<p>").Append(X(p)).Append("</p>");
        fb2.Append("</section></body><body name=\"notes\"><section><p>Сноска, не читать</p></section></body></FictionBook>");
        files["book.fb2"] = Encoding.GetEncoding(1251).GetBytes(fb2.ToString());
        files["book.fb2.zip"] = Zip(("book.fb2", files["book.fb2"]));

        var html = new StringBuilder("<html><head><meta charset=\"utf-8\"><style>p{}</style></head><body><h1>Глава первая</h1>");
        foreach (var p in src) html.Append("<p>").Append(X(p)).Append("<sup>1</sup></p>\n");
        html.Append("</body></html>");
        files["book.html"] = Encoding.UTF8.GetBytes(html.ToString());
        files["book.epub"] = Zip(
            ("mimetype", Encoding.ASCII.GetBytes("application/epub+zip")),
            ("META-INF/container.xml", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>")),
            ("OEBPS/content.opf", Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><package xmlns=\"http://www.idpf.org/2007/opf\"><manifest><item id=\"c1\" href=\"ch%201.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"c1\"/></spine></package>")),
            ("OEBPS/ch 1.xhtml", files["book.html"]));

        var docx = new StringBuilder("<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        docx.Append("<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Глава первая</w:t></w:r></w:p>");
        foreach (var p in src) docx.Append("<w:p><w:r><w:t xml:space=\"preserve\">").Append(X(p)).Append("</w:t></w:r></w:p>");
        docx.Append("</w:body></w:document>");
        files["book.docx"] = Zip(("word/document.xml", Encoding.UTF8.GetBytes(docx.ToString())));

        var odt = new StringBuilder("<?xml version=\"1.0\"?><office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"><office:body><office:text>");
        odt.Append("<text:h>Глава первая</text:h>");
        foreach (var p in src) odt.Append("<text:p>").Append(X(p)).Append("</text:p>");
        odt.Append("</office:text></office:body></office:document-content>");
        files["book.odt"] = Zip(("mimetype", Encoding.ASCII.GetBytes("application/vnd.oasis.opendocument.text")), ("content.xml", Encoding.UTF8.GetBytes(odt.ToString())));

        var rtf = new StringBuilder(@"{\rtf1\ansi\ansicpg1251\deff0{\fonttbl{\f0 Times New Roman;}}\f0 ");
        foreach (var p in src)
        {
            foreach (var c in p) rtf.Append(c < 128 ? c.ToString() : $"\\u{(short)c}?");
            rtf.Append("\\par\n");
        }
        rtf.Append('}');
        files["book.rtf"] = Encoding.ASCII.GetBytes(rtf.ToString());
        files["book_utf16.txt"] = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(string.Join("\r\n", src.Select(p => " " + p)))).ToArray();
        files["book_1251.txt"] = Encoding.GetEncoding(1251).GetBytes(string.Join("\r\n", src.Select(p => " " + p)));

        var fails = 0;
        foreach (var (name, bytes) in files)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllBytes(path, bytes);
            try
            {
                var got = BookFormats.Load(path);
                var text = got.Where(p => p.Kind == BookParaKind.Text).Select(p => p.Text).ToList();
                var same = text.Count(t => src.Contains(t));
                var heading = got.Any(p => p.Kind == BookParaKind.Heading);
                var junk = text.Any(t => t.Contains("Сноска"));
                var ok = same >= src.Count * 0.98 && !junk;
                if (!ok) fails++;
                Console.WriteLine($"  {name,-16} абзацев {text.Count,4}, совпало {same}/{src.Count}{(heading ? ", заголовок узнан" : "")}{(junk ? ", СНОСКА В ТЕКСТЕ" : "")} {(ok ? "✔" : "✖")}");
            }
            catch (Exception ex)
            {
                fails++;
                Console.WriteLine($"  {name,-16} ОШИБКА: {ex.Message}");
            }
        }
        Directory.Delete(dir, recursive: true);
        Console.WriteLine(fails == 0 ? "formats: OK" : $"formats: провалов {fails}");
        return fails == 0 ? 0 : 1;
    }

    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, data) in entries)
            {
                using var s = z.CreateEntry(name).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }
}
