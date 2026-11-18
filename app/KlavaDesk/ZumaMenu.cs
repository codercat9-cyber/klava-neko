using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Media.Ocr;

namespace KlavaDesk;

/// <summary>
/// Меню Zuma Deluxe (PopCap, окно 640×480): довести игру от любого экрана до уровня с шарами. Экраны по порядку:
/// заставка «CLICK HERE TO PLAY!» → главное меню (ADVENTURE) → «CONTINUE?» (CONTINUE) → INSTRUCTIONS (OK) →
/// «GET READY!» (GO) → уровень; после уровня — итоги и снова «GET READY». Все диалоги — зелёные кнопки-плашки:
/// ищутся по цвету, надпись читает OCR Windows, жмётся «вперёд» (GO, CONTINUE, OK, PLAY, NEXT…), никогда QUIT/MENU.
/// Щелчки — сообщениями окну игры (WM_LBUTTONDOWN/UP): мышь друга не дёргается, окно не разворачивается
/// (раньше автопилот делал «развернуть» — PopCap-игра от этого уходила в полноэкранный 640×480 и ломалась).
/// </summary>
internal static class ZumaMenu
{
    internal sealed record Button(Rectangle Box, string Label)
    {
        public Point Center => new(Box.X + Box.Width / 2, Box.Y + Box.Height / 2);
    }

    internal enum Kind { Level, Dialog, MainMenu, Title, AdventureMap, Gauntlet, Unknown }

    /// <param name="Target">Куда щёлкнуть на этом экране (главное меню — ADVENTURE, заставка — «CLICK HERE», карта — PLAY).</param>
    internal sealed record Screen(Kind Kind, List<Button> Buttons, string Text, Point? Target = null, double Score = 0);


    private static OcrEngine? _ocr;
    private static OcrEngine? Ocr => _ocr ??= OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"))
                                             ?? OcrEngine.TryCreateFromUserProfileLanguages();

    // ---------------- снимок клиентской области ----------------

    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Native.RECT r);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref Point p);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);

    /// <summary>Где картинка игры в клиентской области окна (последний снимок) — для щелчков.</summary>
    private static readonly Dictionary<IntPtr, Rectangle> GameRects = new();

    /// <summary>Картинка игры, как её видит сама игра (без рамки, заголовка и чёрных полей) — см. <see cref="GrabGame"/>.</summary>
    public static Bitmap? GrabClient(IntPtr win) => GrabGame(win, out _);

    /// <summary>
    /// Снимок игры. У друга Зума идёт через cnc-ddraw в окне 1920×1032 с «сохранять 4:3»: картинка 1376×1032 посередине,
    /// по бокам чёрные поля. Раньше кадром считалась вся клиентская область — координаты меню (320,452 на 640×480)
    /// и лягушка из levels.xml уезжали на ширину поля (272 px), клики шли мимо. <paramref name="game"/> — прямоугольник
    /// картинки в клиентских координатах.
    /// </summary>
    public static Bitmap? GrabGame(IntPtr win, out Rectangle game)
    {
        game = Rectangle.Empty;
        if (!Native.GetWindowRect(win, out var wr) || !GetClientRect(win, out var cr)) return null;
        int cw = cr.Right - cr.Left, ch = cr.Bottom - cr.Top;
        if (cw < 100 || ch < 100) return null;
        var o = Point.Empty;
        ClientToScreen(win, ref o);
        using var full = new Bitmap(Math.Max(1, wr.Right - wr.Left), Math.Max(1, wr.Bottom - wr.Top), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            var hdc = g.GetHdc();
            var ok = PrintWindow(win, hdc, 2);
            g.ReleaseHdc(hdc);
            if (!ok) g.CopyFromScreen(wr.Left, wr.Top, 0, 0, full.Size);
        }
        var client = new Rectangle(o.X - wr.Left, o.Y - wr.Top, cw, ch);
        client.Intersect(new Rectangle(Point.Empty, full.Size));
        if (client.Width < 100 || client.Height < 100) return null;
        var inClient = GameRect(full, client);
        lock (GameRects) GameRects[win] = inClient;
        game = inClient;
        return full.Clone(new Rectangle(client.X + inClient.X, client.Y + inClient.Y, inClient.Width, inClient.Height), PixelFormat.Format24bppRgb);
    }

    /// <summary>
    /// Картинка игры внутри клиентской области: 4:3 посередине, если лишнее по краям — чёрные поля; иначе игра растянута
    /// на всё окно (или окно само 4:3, как родное 640×480).
    /// </summary>
    internal static Rectangle GameRect(Bitmap bmp, Rectangle client)
    {
        int w = client.Width, h = client.Height;
        var whole = new Rectangle(0, 0, w, h);
        int gw = w, gh = h;
        if (w * 3 > h * 4 + 6) gw = (int)Math.Round(h * 4 / 3.0);
        else if (h * 4 > w * 3 + 6) gh = (int)Math.Round(w * 3 / 4.0);
        if (gw == w && gh == h) return whole;
        var r = new Rectangle((w - gw) / 2, (h - gh) / 2, gw, gh);
        var d = bmp.LockBits(client, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[d.Stride];
            bool Dark(int x, int y)
            {
                Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, d.Stride);
                return Math.Max(row[x * 3], Math.Max(row[x * 3 + 1], row[x * 3 + 2])) <= 24;
            }
            for (var j = 1; j < 12; j++)
                for (var i = 0; i < 4; i++)
                {
                    if (gw < w)
                    {
                        int y = h * j / 12, xl = Math.Max(0, r.X - 2) * i / 4, xr = w - 1 - Math.Max(0, r.X - 2) * i / 4;
                        if (!Dark(xl, y) || !Dark(xr, y)) return whole;
                    }
                    else
                    {
                        int x = w * j / 12, yt = Math.Max(0, r.Y - 2) * i / 4, yb = h - 1 - Math.Max(0, r.Y - 2) * i / 4;
                        if (!Dark(x, yt) || !Dark(x, yb)) return whole;
                    }
                }
        }
        finally { bmp.UnlockBits(d); }
        return r;
    }

    /// <summary>Точка картинки игры → клиентские координаты окна (через поля cnc-ddraw).</summary>
    public static Point ToClient(IntPtr win, Point p)
    {
        Rectangle g;
        lock (GameRects) GameRects.TryGetValue(win, out g);
        return new Point(p.X + g.X, p.Y + g.Y);
    }

    /// <summary>lParam мышиного сообщения: x, y — знаковые 16 бит (за краем окна — отрицательные).</summary>
    public static IntPtr MouseLParam(Point client) => (IntPtr)(((client.Y & 0xFFFF) << 16) | (client.X & 0xFFFF));

    // ---------------- что на экране ----------------

    /// <summary>
    /// Разобрать кадр клиентской области: зелёные кнопки диалога (надписи — OCR), иначе — какой экран, по картинкам
    /// самой игры из её папки images: кнопка ADVENTURE (главное меню, совпадение 0,81 против 0,41 на заставке),
    /// полоса загрузки (заставка, 0,75 против 0,30), кнопка PLAY карты приключения. Шрифт игры OCR не читает.
    /// </summary>
    public static async Task<Screen> ReadAsync(Bitmap client, string? gameDir = null)
    {
        var buttons = new List<Button>();
        foreach (var box in GreenButtons(client))
            buttons.Add(new Button(box, await OcrAsync(client, Rectangle.Inflate(box, 6, 5), 4, yellowText: true)));
        if (buttons.Count > 0) return new Screen(Kind.Dialog, buttons, "");
        var k = client.Height / 480.0;
        Point Logical(int x, int y) => new((int)(x * k), (int)(y * k));
        if (gameDir is not null)
        {
            var menu = Match(client, gameDir, "mmarcadebutton.jpg", 3, new Rectangle(300, 0, 340, 300));
            if (menu.Score >= 0.6) return new Screen(Kind.MainMenu, buttons, "", menu.Center, menu.Score);
            var play = Match(client, gameDir, "advplaybutton.jpg", 3, new Rectangle(0, 240, 640, 240));
            if (play.Score >= 0.7) return new Screen(Kind.AdventureMap, buttons, "", play.Center, play.Score);
            // Гаунтлет (тренировка одной доски, ранги Rabbit → Eagle → Jaguar → Sun God): PLAY внизу посередине, режим
            // (Practice/Survival) — какой друг выбрал; 28.09 бот на этом экране «щёлкал в центр» и до игры не доходил
            var gplay = Match(client, gameDir, "gauntplaybutton.jpg", 3, new Rectangle(120, 400, 400, 80));
            if (gplay.Score >= 0.7) return new Screen(Kind.Gauntlet, buttons, "", gplay.Center, gplay.Score);
            var title = Match(client, gameDir, "loaderbar.gif", 1, new Rectangle(0, 240, 640, 240));
            if (title.Score >= 0.6) return new Screen(Kind.Title, buttons, "", Logical(320, 452), title.Score);
        }
        var text = await OcrAsync(client, new Rectangle(Point.Empty, client.Size), 1);
        var up = text.ToUpperInvariant();
        return up.Contains("CLICK HERE") || up.Contains("TO PLAY") ? new Screen(Kind.Title, buttons, text, Logical(320, 452))
            : Has(up, "ADVENTURE") && (Has(up, "GAUNTLET") || Has(up, "OPTIONS")) ? new Screen(Kind.MainMenu, buttons, text, Logical(522, 102))
            : new Screen(Kind.Unknown, buttons, text);
    }

    // ---------------- картинки игры: поиск на кадре ----------------

    private static readonly Dictionary<string, (float[] Px, int W, int H)?> Templates = new();

    /// <summary>
    /// Где на кадре картинка из папки игры (первый кадр полоски состояний «обычная/под мышью/нажата»): нормированная
    /// корреляция яркости на половинном разрешении в области <paramref name="logical"/> (координаты 640×480).
    /// </summary>
    internal static (double Score, Point Center) Match(Bitmap client, string gameDir, string file, int frames, Rectangle logical)
    {
        var k = client.Height / 480.0;
        var t = Template(gameDir, file, frames, k / 2);
        if (t is not { } tpl || tpl.W < 4 || tpl.H < 4) return (0, Point.Empty);
        var (img, iw, ih) = Gray(client, 2);
        var area = new Rectangle((int)(logical.X * k / 2), (int)(logical.Y * k / 2), (int)(logical.Width * k / 2), (int)(logical.Height * k / 2));
        area.Intersect(new Rectangle(0, 0, iw, ih));
        double tMean = tpl.Px.Average(), tNorm = Math.Sqrt(tpl.Px.Sum(v => (v - tMean) * (v - tMean)));
        if (tNorm < 1e-3) return (0, Point.Empty);
        var tc = tpl.Px.Select(v => v - tMean).ToArray();
        double best = -2;
        var at = Point.Empty;
        for (var y = area.Top; y + tpl.H <= area.Bottom; y++)
        {
            for (var x = area.Left; x + tpl.W <= area.Right; x++)
            {
                double sum = 0, sum2 = 0, cross = 0;
                for (var ty = 0; ty < tpl.H; ty++)
                {
                    var row = (y + ty) * iw + x;
                    var trow = ty * tpl.W;
                    for (var tx = 0; tx < tpl.W; tx++)
                    {
                        double v = img[row + tx];
                        sum += v;
                        sum2 += v * v;
                        cross += v * tc[trow + tx];
                    }
                }
                var n = tpl.W * tpl.H;
                var varW = sum2 - sum * sum / n;
                if (varW < 1e-3) continue;
                var score = cross / (Math.Sqrt(varW) * tNorm);
                if (score > best) { best = score; at = new Point(x, y); }
            }
        }
        return (best, new Point((int)((at.X + tpl.W / 2.0) * 2), (int)((at.Y + tpl.H / 2.0) * 2)));
    }

    private static (float[] Px, int W, int H)? Template(string gameDir, string file, int frames, double scale)
    {
        var key = $"{gameDir}|{file}|{frames}|{scale:F3}";
        lock (Templates)
        {
            if (Templates.TryGetValue(key, out var cached)) return cached;
            (float[] Px, int W, int H)? res = null;
            var path = Path.Combine(gameDir, "images", file);
            if (File.Exists(path))
            {
                try
                {
                    using var src = new Bitmap(path);
                    var fw = src.Width / Math.Max(1, frames);
                    int w = Math.Max(1, (int)(fw * scale)), h = Math.Max(1, (int)(src.Height * scale));
                    using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                    using (var g = Graphics.FromImage(small))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(0, 0, fw, src.Height), GraphicsUnit.Pixel);
                    }
                    var (px, _, _) = Gray(small, 1);
                    res = (px, w, h);
                }
                catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException) { }
            }
            Templates[key] = res;
            return res;
        }
    }

    /// <summary>Яркость картинки, уменьшенной в <paramref name="step"/> раз (среднее по квадрату).</summary>
    private static (float[] Px, int W, int H) Gray(Bitmap bmp, int step)
    {
        int w = bmp.Width / step, h = bmp.Height / step;
        var px = new float[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            for (var y = 0; y < h * step; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (var x = 0; x < w * step; x++)
                {
                    var v = 0.114f * row[x * 3] + 0.587f * row[x * 3 + 1] + 0.299f * row[x * 3 + 2];
                    px[y / step * w + x / step] += v / (step * step);
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return (px, w, h);
    }

    // ---------------- уровни: фон → где лягушка ----------------

    internal sealed record Level(string Id, string Name, Point Gun, float[] Thumb);

    private const char Q = '"';

    private static readonly Dictionary<string, List<Level>> LevelCache = new();
    private const int ThumbW = 40, ThumbH = 30;

    /// <summary>
    /// Уровни игры из levels\levels.xml: у каждого &lt;Graphics id image gx gy dispname&gt; — gx, gy — центр лягушки в
    /// координатах 640×480, фон — levels\id\image.jpg. У PopCap-версии нет пунктира прицела, по которому автопилот
    /// искал лягушку в браузерной, — зато её место записано в файлах самой игры.
    /// </summary>
    private static List<Level> Levels(string gameDir)
    {
        lock (LevelCache)
        {
            if (LevelCache.TryGetValue(gameDir, out var cached)) return cached;
            var list = new List<Level>();
            var xml = Path.Combine(gameDir, "levels", "levels.xml");
            if (File.Exists(xml))
            {
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(xml), @"<Graphics\s[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    string? A(string name) => System.Text.RegularExpressions.Regex.Match(m.Value, $@"\b{name}\s*=\s*{Q}([^{Q}]*){Q}", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } a ? a.Groups[1].Value : null;
                    var id = A("id");
                    var image = A("image") ?? id;
                    if (id is null || !int.TryParse(A("gx"), out var gx) || !int.TryParse(A("gy"), out var gy)) continue;
                    var jpg = Path.Combine(gameDir, "levels", id, image + ".jpg");
                    if (!File.Exists(jpg)) jpg = Directory.Exists(Path.Combine(gameDir, "levels", id))
                        ? Directory.GetFiles(Path.Combine(gameDir, "levels", id), "*.jpg").FirstOrDefault() ?? jpg : jpg;
                    if (!File.Exists(jpg)) continue;
                    try
                    {
                        using var bmp = new Bitmap(jpg);
                        list.Add(new Level(id, A("dispname") ?? id, new Point(gx, gy), Thumb(bmp)));
                    }
                    catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException) { }
                }
            }
            LevelCache[gameDir] = list;
            return list;
        }
    }

    /// <summary>Уменьшенная яркость кадра для сравнения фонов (шары и лягушка закрывают малую долю).</summary>
    private static float[] Thumb(Bitmap src)
    {
        using var small = new Bitmap(ThumbW, ThumbH, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, new Rectangle(0, 0, ThumbW, ThumbH));
        }
        var (px, _, _) = Gray(small, 1);
        var mean = px.Average();
        var norm = Math.Sqrt(px.Sum(v => (v - mean) * (v - mean)));
        return px.Select(v => (float)((v - mean) / Math.Max(1e-3, norm))).ToArray();
    }

    /// <summary>Какой уровень на кадре клиентской области (по фону) и где на кадре лягушка; null — не узнала.</summary>
    public static (Level Level, PointF Frog, double Score)? IdentifyLevel(Bitmap client, string gameDir)
    {
        var levels = Levels(gameDir);
        if (levels.Count == 0) return null;
        var t = Thumb(client);
        var best = levels.Select(l => (L: l, S: l.Thumb.Zip(t, (a, b) => (double)a * b).Sum())).OrderByDescending(x => x.S).First();
        var kx = client.Width / 640.0;
        var ky = client.Height / 480.0;
        return best.S >= 0.5 ? (best.L, new PointF((float)(best.L.Gun.X * kx), (float)(best.L.Gun.Y * ky)), best.S) : null;
    }

    /// <summary>Папка игры — рядом с её exe (картинки меню берутся оттуда).</summary>
    public static string? GameDir(IntPtr win)
    {
        try
        {
            Native.GetWindowThreadProcessId(win, out var pid);
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            var exe = p.MainModule?.FileName;
            var dir = exe is null ? null : Path.GetDirectoryName(exe);
            return dir is not null && Directory.Exists(Path.Combine(dir, "images")) ? dir : null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static bool Has(string up, string word) => up.Contains(word, StringComparison.Ordinal);

    /// <summary>
    /// Зелёные плашки кнопок диалогов PopCap: яркий жёлто-зелёный (≈ 210,226,32), вытянутые по горизонтали,
    /// плотно залитые. Замер 27.09: CONTINUE/NEW GAME 98×19, OK 114×19, GO 240×19 — на заставке и в меню ни одной.
    /// </summary>
    public static List<Rectangle> GreenButtons(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var k = Math.Max(1.0, h / 480.0);   // окно больше 640×480 — кнопки крупнее
        var mask = new bool[w * h];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                for (var x = 0; x < w; x++)
                {
                    int b = row[x * 3], g = row[x * 3 + 1], r = row[x * 3 + 2];
                    mask[y * w + x] = g > 130 && g - r > 30 && g - b > 70;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        var seen = new bool[w * h];
        var res = new List<Rectangle>();
        var stack = new Stack<int>();
        for (var i = 0; i < mask.Length; i++)
        {
            if (!mask[i] || seen[i]) continue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
            stack.Push(i);
            seen[i] = true;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                int px = p % w, py = p / w;
                count++;
                if (px < minX) minX = px;
                if (px > maxX) maxX = px;
                if (py < minY) minY = py;
                if (py > maxY) maxY = py;
                foreach (var q in new[] { p - 1, p + 1, p - w, p + w })
                {
                    if (q < 0 || q >= mask.Length || seen[q] || !mask[q]) continue;
                    if ((q == p - 1 || q == p + 1) && q / w != py) continue;
                    seen[q] = true;
                    stack.Push(q);
                }
            }
            int bw = maxX - minX + 1, bh = maxY - minY + 1;
            // верхняя панель уровня (полоска прогресса — такая же ярко-зелёная плашка) — не кнопка диалога
            if (minY < 40 * k) continue;
            // от 80 в ширину (на 640×480): ярко-зелёная листва карты приключения (53×13) сходила за кнопку и жалась вечно;
            // не выше 26: три зелёных шара подряд (32 в высоту) сходили за «окно итогов» и обрывали уровень
            if (bw >= 80 * k && bh >= 12 * k && bh <= 26 * k && bw / (double)bh > 2 && count / (double)(bw * bh) > 0.45)
                res.Add(new Rectangle(minX, minY, bw, bh));
        }
        return res.OrderBy(r => r.Y).ThenBy(r => r.X).ToList();
    }

    /// <summary>Текст куска кадра (OCR Windows, английский), увеличенного для мелкого шрифта игры.</summary>
    /// <param name="yellowText">Кнопка PopCap: надпись жёлтая (≈210,226,32) с тёмной обводкой на зелёном — перекрасить
    /// в чёрное на белом, иначе OCR её не видит.</param>
    private static async Task<string> OcrAsync(Bitmap src, Rectangle area, int zoom, bool yellowText = false)
    {
        if (Ocr is not { } engine) return "";
        area.Intersect(new Rectangle(Point.Empty, src.Size));
        if (area.Width < 4 || area.Height < 4) return "";
        using var crop = new Bitmap(area.Width * zoom, area.Height * zoom, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(crop))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, new Rectangle(0, 0, crop.Width, crop.Height), area, GraphicsUnit.Pixel);
        }
        if (yellowText)
        {
            var d = crop.LockBits(new Rectangle(0, 0, crop.Width, crop.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[d.Stride];
                for (var y = 0; y < crop.Height; y++)
                {
                    Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, d.Stride);
                    for (var x = 0; x < crop.Width; x++)
                    {
                        int b = row[x * 3], g = row[x * 3 + 1], r = row[x * 3 + 2];
                        var ink = r > 140 && g > 150 && b < 110 && r > b + 60;
                        row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = (byte)(ink ? 0 : 255);
                    }
                    Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, d.Stride);
                }
            }
            finally { crop.UnlockBits(d); }
        }
        try
        {
            using var soft = await Eyes.ToSoftwareBitmapAsync(crop);
            var r = await engine.RecognizeAsync(soft);
            return string.Join(" ", r.Lines.Select(l => l.Text)).Trim();
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException) { return ""; }
    }

    /// <summary>
    /// Какую кнопку жать. Шрифт PopCap OCR читает с ошибками («CACK TO mAIN MENO» вместо BACK TO MAIN MENU),
    /// поэтому слова сравниваются нечётко (Левенштейн ≤ 1 для слов от 4 букв). Никогда: MAIN, MENU, QUIT, EXIT,
    /// HELP, CANCEL, NO, OPTIONS. Вперёд по важности: BACK TO GAME / RESUME (пауза), GO, CONTINUE, OK, PLAY, NEXT,
    /// YES, RETRY, AGAIN, DONE; NEW GAME — только если нет CONTINUE. Надпись не прочиталась совсем — левая/верхняя
    /// из непрочитанных (у PopCap «да/продолжить» слева), но только если среди прочитанных нет опасных соседей
    /// в том же столбце (вертикальное меню паузы: там решает только надпись).
    /// </summary>
    public static Button? Choose(List<Button> buttons)
    {
        var info = buttons.Select(b => (B: b, W: Words(b.Label))).ToList();
        bool Back((Button B, string[] W) x) => Near(x.W, "MAIN") || Near(x.W, "MENU") || Near(x.W, "QUIT") || Near(x.W, "EXIT")
            || Near(x.W, "HELP") || Near(x.W, "CANCEL") || Near(x.W, "OPTIONS") || x.W.Contains("NO") || Near(x.W, "REGISTER") || x.W.Contains("BUY");
        var safe = info.Where(x => !Back(x)).ToList();
        (Button B, string[] W)? Pick(Func<string[], bool> test) => safe.FirstOrDefault(x => test(x.W)) is { B: not null } hit ? hit : null;
        var order = new Func<string[], bool>[]
        {
            w => Near(w, "GAME") && (Near(w, "BACK") || w.Contains("TO")) && !Near(w, "NEW"),   // BACK TO GAME
            w => Near(w, "RESUME"),
            w => w.Contains("GO"),
            w => Near(w, "CONTINUE"),
            w => w.Contains("OK") || w.Length == 1 && w[0].Length == 2 && w[0][0] is 'O' or 'D' or 'Q' && w[0][1] is 'K' or 'R' or 'X',   // OCR: OK → OR/DK/0K
            w => Near(w, "PLAY"),
            w => Near(w, "NEXT"),
            w => w.Contains("YES"),
            w => Near(w, "RETRY") || Near(w, "AGAIN"),
            w => Near(w, "DONE") || Near(w, "START"),
        };
        foreach (var t in order)
            if (Pick(t) is { } hit) return hit.B;
        var newGame = safe.FirstOrDefault(x => Near(x.W, "NEW") && Near(x.W, "GAME"));
        // единственная кнопка диалога и она не «выход» — это OK/GO/CONTINUE, как бы OCR её ни прочитал
        if (buttons.Count == 1 && safe.Count == 1) return safe[0].B;
        var unread = safe.Where(x => x.W.Length == 0 || x.W.All(w => w.Length <= 1)).Select(x => x.B).ToList();
        if (unread.Count > 0)
        {
            // вертикальный столбец кнопок, где хоть одна опасная, — меню паузы/настроек: вслепую не жмём
            var column = info.Where(x => Back(x)).Any(x => unread.Any(u => Math.Abs(u.Box.X - x.B.Box.X) < 8));
            if (!column) return unread.OrderBy(b => b.Box.X).ThenBy(b => b.Box.Y).First();
        }
        return newGame.B;
    }

    /// <summary>
    /// Меню паузы PopCap: столбец HELP → BACK TO MAIN MENU → BACK TO GAME. Где «вернуться в игру» — кнопка ниже MAIN MENU,
    /// а не видна (подсвечена курсором) — на том же шаге столбца. null — это не меню паузы.
    /// </summary>
    public static Point? PauseResume(List<Button> buttons)
    {
        var help = buttons.FirstOrDefault(x => Near(Words(x.Label), "HELP"));
        var main = buttons.FirstOrDefault(x => Near(Words(x.Label), "MAIN"));
        if (help is null || main is null) return null;
        int step = main.Center.Y - help.Center.Y;
        if (step < 20 || Math.Abs(main.Center.X - help.Center.X) > 40) return null;
        var below = buttons.Where(x => x.Center.Y > main.Center.Y + step / 2 && Math.Abs(x.Center.X - main.Center.X) < 40)
            .OrderBy(x => x.Center.Y).FirstOrDefault();
        return below?.Center ?? new Point(main.Center.X, main.Center.Y + step);
    }

    /// <summary>Слова надписи заглавными, с поправками OCR: 0→O, 1/|→I, «•» и прочий мусор — пробел.</summary>
    private static string[] Words(string label)
    {
        var up = label.ToUpperInvariant().Replace('0', 'O').Replace('1', 'I').Replace('|', 'I');
        var clean = new string(up.Select(c => char.IsLetter(c) ? c : ' ').ToArray());
        return clean.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Есть слово, похожее на <paramref name="word"/>: короткие — точно, от 4 букв — с одной ошибкой OCR.</summary>
    private static bool Near(string[] words, string word) =>
        words.Any(w => w == word || word.Length >= 4 && Math.Abs(w.Length - word.Length) <= 1 && Lev(w, word) <= 1);

    private static int Lev(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    // ---------------- руки: щелчок сообщением окну ----------------

    /// <summary>Щелчок по точке картинки игры сообщениями окну — мышь друга остаётся на месте.</summary>
    public static async Task ClickAsync(IntPtr win, Point p, CancellationToken ct)
    {
        var lp = MouseLParam(ToClient(win, p));
        PostMessage(win, 0x0200, IntPtr.Zero, lp);            // WM_MOUSEMOVE — кнопка подсвечивается, как от мыши
        await Task.Delay(120, ct);
        PostMessage(win, 0x0201, (IntPtr)1, lp);              // WM_LBUTTONDOWN
        await Task.Delay(90, ct);
        PostMessage(win, 0x0202, IntPtr.Zero, lp);            // WM_LBUTTONUP
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
    [DllImport("user32.dll")] private static extern bool ClipCursor(IntPtr rect);

    /// <summary>
    /// Поставить настоящий курсор в точку экрана. cnc-ddraw зажимает курсор в области картинки (ClipCursor) и после
    /// смены размера окна оставляет старую область (27.09: окно 1920×1080, зажим 160..1120×0..720 от прежнего 1280×720 —
    /// до PLAY курсор не доставал). Не встал куда надо — зажим снимается и ставим снова.
    /// </summary>
    public static void PlaceCursor(Point screen)
    {
        SetCursorPos(screen.X, screen.Y);
        if (GetCursorPos(out var now) && Math.Abs(now.X - screen.X) + Math.Abs(now.Y - screen.Y) > 2)
        {
            ClipCursor(IntPtr.Zero);
            SetCursorPos(screen.X, screen.Y);
            if (DateTime.UtcNow - _clipLogged > TimeSpan.FromMinutes(1))
            {
                _clipLogged = DateTime.UtcNow;
                Log.Write($"зума: курсор был зажат игрой (встал в {now.X},{now.Y} вместо {screen.X},{screen.Y}) — сняла зажим");
            }
        }
    }

    private static DateTime _clipLogged = DateTime.MinValue;

    /// <summary>
    /// Щелчок настоящей мышью по точке картинки игры (курсор ставится мгновенно). Нужен, когда щелчок сообщением
    /// не доходит: в полноэкранном режиме cnc-ddraw игра слушает только настоящую мышь (27.09: 90 с жала PLAY).
    /// </summary>
    public static async Task RealClickAsync(IntPtr win, Point p, CancellationToken ct)
    {
        var c = ToClient(win, p);
        ClientToScreen(win, ref c);
        Uia.Focus(win);
        PlaceCursor(c);
        await Task.Delay(60, ct);
        foreach (var flags in new[] { Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP })
        {
            var input = new[] { new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { dwFlags = flags } } } };
            Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
            await Task.Delay(70, ct);
        }
    }

    // ---------------- от любого экрана до уровня ----------------

    /// <summary>
    /// Довести до уровня: null — поле с шарами на экране и диалогов нет; иначе — почему не вышло. <paramref name="ready"/>
    /// — проверка «это уже уровень» (поле с шарами, её делает ZumaBot по своему кадру).
    /// </summary>
    public static async Task<string?> ToLevelAsync(IntPtr win, Func<Task<bool>> ready, Func<string?> interrupted, Action<string>? log, CancellationToken ct, int maxSeconds = 60)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var sameScreen = 0;
        var lastText = "";
        var gameDir = GameDir(win);
        while (clock.Elapsed.TotalSeconds < maxSeconds)
        {
            ct.ThrowIfCancellationRequested();
            if (interrupted() is { } why) return why;
            if (!IsWindow(win)) return "окно Зумы закрылось";
            if (Native.IsIconic(win)) { Native.ShowWindow(win, Native.SW_RESTORE); await Task.Delay(600, ct); }
            using var bmp = GrabClient(win);
            if (bmp is null) { await Task.Delay(500, ct); continue; }
            var s = await ReadAsync(bmp, gameDir);
            if (s.Kind != Kind.Dialog && await ready()) return null;
            var key = $"{s.Kind}|{string.Join(",", s.Buttons.Select(b => b.Label))}";
            sameScreen = key == lastText ? sameScreen + 1 : 0;
            lastText = key;
            var k = bmp.Height / 480.0;
            // первый щелчок — сообщением (мышь друга не дёргается); экран не сменился — настоящей мышью
            Task Press(Point p) => sameScreen >= 1 ? RealClickAsync(win, p, ct) : ClickAsync(win, p, ct);
            var how = sameScreen >= 1 ? " мышью" : "";
            switch (s.Kind)
            {
                case Kind.Dialog:
                    if (Choose(s.Buttons) is { } b)
                    {
                        log?.Invoke($"зума: диалог [{string.Join(" | ", s.Buttons.Select(x => x.Label.Length > 0 ? x.Label : "?"))}] → жму «{b.Label}»{how}");
                        await Press(b.Center);
                    }
                    else if (PauseResume(s.Buttons) is { } resume)
                    {
                        // меню паузы (MENU): HELP, BACK TO MAIN MENU и под ними BACK TO GAME. 28.09: щелчок сообщением
                        // полноэкранная игра не приняла, а под курсором кнопка подсвечена — зелёной её больше не видно и
                        // OCR прочёл только две. BACK TO GAME — всегда следующая в столбце, тем же шагом; жмём мышью
                        log?.Invoke($"зума: меню паузы → BACK TO GAME мышью ({resume.X},{resume.Y})");
                        await RealClickAsync(win, resume, ct);
                    }
                    else return $"в игре диалог только с кнопками «{string.Join(", ", s.Buttons.Select(x => x.Label))}» — жать их не стану";
                    break;
                case Kind.MainMenu:
                    log?.Invoke($"зума: главное меню → ADVENTURE ({s.Score:F2}){how}");
                    await Press(s.Target ?? new Point((int)(522 * k), (int)(102 * k)));
                    break;
                case Kind.AdventureMap:
                    log?.Invoke($"зума: карта приключения → PLAY ({s.Score:F2}){how}");
                    await Press(s.Target!.Value);
                    break;
                case Kind.Gauntlet:
                    log?.Invoke($"зума: гаунтлет → PLAY ({s.Score:F2}){how}");
                    await Press(s.Target!.Value);
                    break;
                case Kind.Title:
                    log?.Invoke($"зума: заставка → «CLICK HERE TO PLAY» ({s.Score:F2}){how}");
                    await Press(s.Target ?? new Point((int)(320 * k), (int)(452 * k)));
                    break;
                default:
                    // загрузка, затемнение между экранами, начало уровня — ждём; тот же непонятный экран долго — щелчок в центр
                    // (заставки PopCap пропускаются щелчком), но не по верхней полосе, где кнопка MENU
                    if (sameScreen == 6)
                    {
                        log?.Invoke("зума: непонятный экран — щелчок в центр");
                        await ClickAsync(win, new Point(bmp.Width / 2, bmp.Height * 2 / 3), ct);
                    }
                    break;
            }
            // тот же экран, жму уже и мышью, а он не уходит — не долбим бесконечно
            if (s.Kind != Kind.Unknown && sameScreen >= 6) return $"кнопка в игре не нажимается ни сообщением, ни мышью ({s.Kind})";
            await Task.Delay(s.Kind == Kind.Unknown ? 700 : 1300, ct);
        }
        return $"за {maxSeconds} с не дошла до уровня";
    }

    /// <summary>
    /// KlavaDesk.exe --zuma-menu кадр.png… [crop=x,y,w,h] — разобрать сохранённые кадры (кнопки, надписи, что нажала бы);
    /// KlavaDesk.exe --zuma-menu live — пройти меню запущенной Zuma Deluxe до уровня (без стрельбы).
    /// </summary>
    public static int Test(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Contains("live"))
        {
            var win = Uia.ZumaWindow();
            if (win == IntPtr.Zero) { Console.WriteLine("окна Zuma Deluxe нет"); return 1; }
            var gameDir = GameDir(win) ?? @"D:\Games\Zuma Deluxe";
            Task<bool> Ready() => Task.FromResult(ZumaBot.OnLocalLevel(win, gameDir));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var r = ToLevelAsync(win, Ready, () => (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0 ? "Esc" : null, Console.WriteLine, CancellationToken.None, 90)
                .GetAwaiter().GetResult();
            Console.WriteLine(r is null ? $"на уровне за {sw.Elapsed.TotalSeconds:F0} с" : $"не дошла: {r}");
            return r is null ? 0 : 1;
        }
        var crop = args.FirstOrDefault(a => a.StartsWith("crop=", StringComparison.Ordinal))?[5..].Split(',').Select(int.Parse).ToArray();
        foreach (var file in args.Where(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
        {
            using var src = new Bitmap(file);
            // снимок окна целиком — поля cnc-ddraw отрезаются так же, как в живой игре
            using var bmp = crop is { Length: 4 } c ? src.Clone(new Rectangle(c[0], c[1], c[2], c[3]), PixelFormat.Format24bppRgb)
                : src.Clone(GameRect(src, new Rectangle(0, 0, src.Width, src.Height)), PixelFormat.Format24bppRgb);
            var dir = args.FirstOrDefault(a => a.StartsWith("game=", StringComparison.Ordinal))?[5..] ?? @"D:\Games\Zuma Deluxe";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var s = ReadAsync(bmp, Directory.Exists(dir) ? dir : null).GetAwaiter().GetResult();
            Console.WriteLine($"   ({sw.ElapsedMilliseconds} мс, совпадение {s.Score:F2}, цель {s.Target})");
            if (Directory.Exists(dir) && IdentifyLevel(bmp, dir) is { } lv)
                Console.WriteLine($"   уровень «{lv.Level.Name}» ({lv.Level.Id}), лягушка ({lv.Frog.X:F0},{lv.Frog.Y:F0}), сходство фона {lv.Score:F2}");
            var pick = Choose(s.Buttons);
            Console.WriteLine($"{Path.GetFileName(file)}: {s.Kind}, кнопки [{string.Join(" | ", s.Buttons.Select(b => $"{b.Label} @{b.Box}"))}]" +
                              $"{(pick is null ? "" : $" → жму «{pick.Label}» {pick.Center}")}");
            Console.WriteLine($"   текст: {(s.Text.Length > 140 ? s.Text[..140] + "…" : s.Text)}");
        }
        return 0;
    }
}

