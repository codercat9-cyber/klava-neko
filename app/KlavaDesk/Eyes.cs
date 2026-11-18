using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace KlavaDesk;

/// <summary>Что сейчас на переднем плане: приложение и заголовок окна.</summary>
internal sealed record Glance(string App, string Title)
{
    public string Describe() => Title.Length > 0 ? $"окно «{Title}» ({App})" : $"приложение {App}";
}

/// <summary>Кусок текста, найденный на экране распознаванием: сам текст и где он (координаты экрана).</summary>
internal sealed record ScreenText(string Text, Rectangle Bounds);

/// <summary>
/// Глаза Клавы. Заголовок активного окна — всегда (дёшево). По просьбе «глянь» — снимок экрана:
/// картинкой для мозга с глазами и текстом через встроенное распознавание Windows (Windows.Media.Ocr,
/// русский + английский) — для мелкого текста и для мозга без глаз. Всё локально, никуда не уходит.
/// На время снимка окно Клавы исключается из захвата (SetWindowDisplayAffinity) — она видит то, что под ней,
/// а не свой же силуэт поверх нужной кнопки.
/// </summary>
internal sealed class Eyes
{
    private readonly IntPtr _self;
    private readonly OcrEngine? _ru;
    private readonly OcrEngine? _en;

    /// <summary>Спрятать/вернуть окна Клавы на снимках (ставит OverlayForm; зовётся на время захвата).</summary>
    public Action<bool>? HideSelfFromCapture { get; set; }

    public Eyes(IntPtr self)
    {
        _self = self;
        _ru = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ru"));
        _en = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (_ru is null && _en is null) Log.Write("глаза: в Windows нет движка распознавания текста — буду видеть только заголовки окон");
    }

    /// <summary>Активное окно; null — если на переднем плане сама Клава, её окна или рабочий стол.</summary>
    public Glance? Foreground()
    {
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero || h == _self) return null;
        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid == Environment.ProcessId) return null;
        var sb = new StringBuilder(512);
        Native.GetWindowTextW(h, sb, sb.Capacity);
        var title = sb.ToString().Trim();
        string app;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            app = p.ProcessName;
            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(desc)) app = desc.Trim();
            }
            catch (System.ComponentModel.Win32Exception) { }   // чужой процесс с правами админа — хватит имени
        }
        catch (ArgumentException) { return null; }             // процесс уже закрылся
        if (app is "explorer" or "Проводник" or "Windows Explorer" && title.Length == 0) return null;   // голый рабочий стол
        return new Glance(app, title.Length > 120 ? title[..120] + "…" : title);
    }

    /// <summary>Снять область экрана, спрятав на это время саму Клаву.</summary>
    private async Task<Bitmap> GrabAsync(Rectangle area)
    {
        var hid = false;
        try
        {
            if (HideSelfFromCapture is { } hide) { hide(true); hid = true; await Task.Delay(70); }   // DWM применяет исключение со следующего кадра
            var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            return bmp;
        }
        finally
        {
            if (hid) HideSelfFromCapture!(false);
        }
    }

    /// <summary>Монитор, где сейчас курсор (там работают лапки).</summary>
    private static Rectangle CursorMonitor()
    {
        Native.GetCursorPos(out var c);
        return System.Windows.Forms.Screen.FromPoint(new Point(c.X, c.Y)).Bounds;
    }

    /// <summary>
    /// Снимок монитора с активным окном: распознанный текст (не длиннее maxChars) и, если нужно, уменьшенный JPEG
    /// для мозга с глазами (длинная сторона — maxSide). Мелкий текст на уменьшенной картинке не читается,
    /// поэтому текст идёт отдельно, распознанный с полного разрешения.
    /// </summary>
    public async Task<(string Text, byte[]? Jpeg)> LookAsync(int maxChars, int maxSide, bool wantImage, bool wantText)
    {
        var fg = Native.GetForegroundWindow();
        var area = (fg != IntPtr.Zero ? System.Windows.Forms.Screen.FromHandle(fg) : System.Windows.Forms.Screen.PrimaryScreen!).Bounds;
        using var bmp = await GrabAsync(area);
        var jpeg = wantImage ? ToJpeg(bmp, maxSide, grid: false) : null;
        return (wantText ? await RecognizeAsync(bmp, maxChars) : "", jpeg);
    }

    /// <summary>
    /// Снимок для лапок: весь монитор, где сейчас мышь. Возвращает JPEG (уменьшённый до maxSide) с тонкой сеткой
    /// координат через каждые 100 пикселей снимка — модели по подписанной сетке называют точки в разы точнее,
    /// чем «на глаз». Плюс реальная область на экране и масштаб (пиксель снимка = пиксель экрана × scale).
    /// </summary>
    public Task<ScreenShot> CaptureAsync(int maxSide, int maxChars) => CaptureAsync(maxSide, maxChars, null);

    /// <param name="region">Область экрана (окно или приближение); null — весь монитор под курсором.</param>
    public async Task<ScreenShot> CaptureAsync(int maxSide, int maxChars, Rectangle? region)
    {
        var area = region is { Width: > 8, Height: > 8 } r ? Rectangle.Intersect(r, System.Windows.Forms.SystemInformation.VirtualScreen) : CursorMonitor();
        using var bmp = await GrabAsync(area);
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(area.Width, area.Height));
        var jpeg = ToJpeg(bmp, maxSide, grid: true);
        var ocr = await RecognizeAsync(bmp, maxChars);
        return new ScreenShot(jpeg, area, scale, ocr);
    }

    /// <summary>Сохранить полноразмерный скриншот (PNG) всех мониторов или монитора под курсором.</summary>
    public async Task<string> SaveScreenshotAsync(string? path, bool allMonitors)
    {
        var area = allMonitors ? System.Windows.Forms.SystemInformation.VirtualScreen : CursorMonitor();
        using var bmp = await GrabAsync(area);
        if (string.IsNullOrWhiteSpace(path))
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
            Directory.CreateDirectory(dir);
            path = Path.Combine(dir, $"klava_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");
        }
        else
        {
            if (!Path.HasExtension(path)) path += ".png";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        }
        var fmt = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg" ? ImageFormat.Jpeg : ImageFormat.Png;
        bmp.Save(path, fmt);
        return path;
    }

    /// <summary>
    /// Найти текст на экране (монитор под курсором): строки распознавания, где встречается запрос, и прямоугольник
    /// именно совпавших слов в координатах экрана. Сначала точные совпадения, потом частичные.
    /// </summary>
    public async Task<List<ScreenText>> FindTextAsync(string query)
    {
        var result = new List<ScreenText>();
        if (string.IsNullOrWhiteSpace(query) || (_ru is null && _en is null)) return result;
        var area = CursorMonitor();
        using var bmp = await GrabAsync(area);
        using var soft = await ToSoftwareBitmapAsync(bmp);
        var needle = Norm(query);
        var needleWords = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var engine in new[] { _ru, _en })
        {
            if (engine is null) continue;
            var r = await engine.RecognizeAsync(soft);
            foreach (var line in r.Lines)
            {
                var words = line.Words.ToList();
                for (var i = 0; i < words.Count; i++)
                {
                    // окно из стольких же слов, сколько в запросе: «Сохранить как» ищем парой слов
                    var take = Math.Min(needleWords.Length, words.Count - i);
                    var span = words.Skip(i).Take(take).ToList();
                    var joined = Norm(string.Join(" ", span.Select(w => w.Text)));
                    if (!joined.Contains(needle, StringComparison.Ordinal) && !(needleWords.Length == 1 && joined.StartsWith(needle, StringComparison.Ordinal))) continue;
                    var x0 = span.Min(w => w.BoundingRect.X); var y0 = span.Min(w => w.BoundingRect.Y);
                    var x1 = span.Max(w => w.BoundingRect.X + w.BoundingRect.Width); var y1 = span.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                    var rect = new Rectangle(area.Left + (int)x0, area.Top + (int)y0, Math.Max(1, (int)(x1 - x0)), Math.Max(1, (int)(y1 - y0)));
                    if (result.Any(x => x.Bounds.IntersectsWith(rect))) continue;
                    result.Add(new ScreenText(line.Text, rect));
                }
            }
            if (result.Count > 0) break;   // русский нашёл — английский не нужен
        }
        // точное совпадение строки — выше
        return result.OrderByDescending(x => Norm(x.Text) == needle).ThenBy(x => x.Text.Length).ToList();
    }

    private static string Norm(string s) => s.Trim().ToLowerInvariant().Replace('ё', 'е');

    private static byte[] ToJpeg(Bitmap src, int maxSide, bool grid)
    {
        var k = Math.Min(1.0, maxSide / (double)Math.Max(src.Width, src.Height));
        using var small = new Bitmap(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, small.Width, small.Height);
            if (grid) DrawGrid(g, small.Width, small.Height);
        }
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var prm = new EncoderParameters(1);
        prm.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
        using var ms = new MemoryStream();
        small.Save(ms, codec, prm);
        return ms.ToArray();
    }

    /// <summary>Тонкая полупрозрачная сетка через 100 px и подписи координат по краям и на пересечениях через 200.</summary>
    private static void DrawGrid(Graphics g, int w, int h)
    {
        // сетка бледная: модель по ней считает координаты, но мелкий текст под ней должен читаться
        using var line = new Pen(Color.FromArgb(40, 255, 0, 170), 1);
        using var major = new Pen(Color.FromArgb(80, 255, 0, 170), 1);
        using var font = new Font("Consolas", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        using var fg = new SolidBrush(Color.FromArgb(255, 255, 230, 60));
        for (var x = 100; x < w; x += 100) g.DrawLine(x % 500 == 0 ? major : line, x, 0, x, h);
        for (var y = 100; y < h; y += 100) g.DrawLine(y % 500 == 0 ? major : line, 0, y, w, y);
        void Label(string s, float x, float y)
        {
            var size = g.MeasureString(s, font);
            g.FillRectangle(bg, x, y, size.Width, size.Height);
            g.DrawString(s, font, fg, x, y);
        }
        for (var x = 100; x < w; x += 100) Label(x.ToString(), x + 1, 1);
        for (var y = 100; y < h; y += 100) Label(y.ToString(), 1, y + 1);
        for (var x = 200; x < w; x += 200)
            for (var y = 200; y < h; y += 200)
                Label($"{x},{y}", x + 1, y + 1);
    }

    internal static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(Bitmap bmp)
    {
        byte[] bmpBytes;
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Bmp);
            bmpBytes = ms.ToArray();
        }
        using var ras = new InMemoryRandomAccessStream();
        using (var w = new DataWriter(ras))
        {
            w.WriteBytes(bmpBytes);
            await w.StoreAsync();
            w.DetachStream();
        }
        ras.Seek(0);
        var dec = await BitmapDecoder.CreateAsync(ras);
        return await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>
    /// Кадр окна игры (наблюдатель GameWatch): прямоугольник окна на экране, сама Клава на снимке не видна. null — окно
    /// свёрнуто или за краем экрана.
    /// </summary>
    public async Task<Bitmap?> GrabWindowAsync(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return null;
        // окно игры — постоянным захватом Windows.Graphics.Capture: не тормозит игру и Клаву прятать не надо;
        // не дался — по-старому (снимок экрана с прятками)
        if (!_captureRefused.Contains(hwnd))
        {
            var cap = _capture;
            if (cap?.Hwnd != hwnd)
            {
                Interlocked.Exchange(ref _capture, null)?.Dispose();
                cap = await WindowCapture.StartAsync(hwnd);
                if (cap is null) _captureRefused.Add(hwnd);
                else { _capture = cap; Log.Write("глаза: окно игры снимаю захватом окна (без пряток и без нагрузки на игру)"); }
            }
            if (cap is not null && await cap.GrabAsync() is { } frame) return frame;
        }
        var area = Rectangle.Intersect(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), System.Windows.Forms.SystemInformation.VirtualScreen);
        if (area.Width < 64 || area.Height < 64) return null;
        return await GrabAsync(area);
    }

    private WindowCapture? _capture;
    private readonly HashSet<IntPtr> _captureRefused = [];

    /// <summary>Перестали смотреть игру — отпустить захват окна.</summary>
    public void StopWindowCapture() => Interlocked.Exchange(ref _capture, null)?.Dispose();

    /// <summary>
    /// Текст с готового кадра (строки через перевод строки) — распознавание Windows, локально. ruOnly — только русский
    /// движок: пиксельный капс игр английский читает латиницей («OHM nonAAAYOT B nATPOHHVIK» вместо «ОНИ ПОПАДАЮТ…»).
    /// </summary>
    public Task<string> ReadTextAsync(Bitmap bmp, int maxChars, bool ruOnly = false) => RecognizeAsync(bmp, maxChars, ruOnly);

    private async Task<string> RecognizeAsync(Bitmap bmp, int maxChars, bool ruOnly = false)
    {
        if (_ru is null && _en is null) return "";
        using var soft = await ToSoftwareBitmapAsync(bmp);
        // русский движок читает и латиницу, но английский точнее на коде и интерфейсах — берём тот, что нашёл больше
        var ru = _ru is null ? null : await _ru.RecognizeAsync(soft);
        var en = _en is null || (ruOnly && _ru is not null) ? null : await _en.RecognizeAsync(soft);
        var best = (ru?.Text.Length ?? 0) >= (en?.Text.Length ?? 0) * 0.9 ? ru : en;
        if (best is null) return "";
        var text = new StringBuilder();
        foreach (var line in best.Lines)
        {
            var t = line.Text.Trim();
            if (t.Length < 2) continue;
            if (text.Length + t.Length + 1 > maxChars) { text.Append('…'); break; }
            text.AppendLine(t);
        }
        return text.ToString().Trim();
    }
}
