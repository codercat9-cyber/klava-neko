using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace KlavaDesk;

/// <summary>
/// Кадр игры для учёбы и для игры по выученному — ОДИН и тот же путь: клиентская часть окна игры, окна самой Клавы
/// закрашены чёрным (иначе нейронка учит её дыхание вместо игры), уменьшить до 256×144, JPEG 90. Запись и игрок зовут
/// одну функцию — нейронка видит при игре ровно то, на чём училась.
/// </summary>
internal static class GameFrames
{
    public const int Width = 256, Height = 144, Quality = 90;
    private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>Клиентская часть окна на экране (без рамки и заголовка).</summary>
    public static Rectangle ClientOnScreen(IntPtr hwnd)
    {
        if (!Native.GetClientRect(hwnd, out var c)) return Rectangle.Empty;
        var p = new Native.POINT { X = 0, Y = 0 };
        Native.ClientToScreen(hwnd, ref p);
        return new Rectangle(p.X, p.Y, c.Right - c.Left, c.Bottom - c.Top);
    }

    /// <summary>Снять кадр игры и подготовить: JPEG 256×144. null — окна нет или оно свёрнуто.</summary>
    public static byte[]? Grab(IntPtr hwnd)
    {
        var area = ClientOnScreen(hwnd);
        if (area.Width < 64 || area.Height < 64) return null;
        using var full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            foreach (var m in KlavaWindows())
            {
                var o = Rectangle.Intersect(m, area);
                if (!o.IsEmpty) g.FillRectangle(Brushes.Black, o.X - area.X, o.Y - area.Y, o.Width, o.Height);
            }
        }
        using var small = new Bitmap(Width, Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(full, new Rectangle(0, 0, Width, Height));
        }
        using var ms = new MemoryStream();
        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Quality);
        small.Save(ms, Jpeg, ps);
        return ms.ToArray();
    }

    private static HashSet<uint> _klavaPids = [];
    private static long _klavaPidsAt;

    /// <summary>
    /// Видимые окна KlavaDesk (аватар, пузыри, терминал) — прямоугольники на экране. Всех процессов KlavaDesk, не только
    /// своего: тестовый запуск (--arena-live) рядом с живой Клавой иначе видел её аватар в кадре, и нейронка-глаза
    /// приняла бы его за человека. Список процессов обновляется раз в 5 с (кадров — 30 в секунду).
    /// </summary>
    public static List<Rectangle> KlavaWindows()
    {
        var now = Environment.TickCount64;
        if (_klavaPids.Count == 0 || now - _klavaPidsAt > 5000)
        {
            var pids = new HashSet<uint> { (uint)Environment.ProcessId };
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("KlavaDesk"))
                using (p) pids.Add((uint)p.Id);
            _klavaPids = pids;
            _klavaPidsAt = now;
        }
        var mine = _klavaPids;
        var list = new List<Rectangle>();
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out var pid);
            if (mine.Contains(pid) && Native.IsWindowVisible(h) && Native.GetWindowRect(h, out var r) && r.Right > r.Left)
                list.Add(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
            return true;
        }, IntPtr.Zero);
        return list;
    }
}

/// <summary>
/// Запись игры друга для учёбы Клавы (клонирование поведения): 16 раз в секунду — кадр окна игры (GameFrames) и что в
/// этот момент делали клавиатура и мышь: зажатые клавиши (и нажатые между кадрами — короткий тап не теряется), движение
/// мыши через Raw Input (так его видят шутеры с захваченным курсором), кнопки, колесо, курсор в окне (Зума и игры
/// курсором). Нажатия самой Клавы (SendInput — Raw Input без устройства) не пишутся: учится она на игре друга.
/// Пишется только пока впереди игра; «auto» — любая из известных игр, каждая в свою папку:
/// gamer\data\&lt;игра&gt;\&lt;дата_время&gt;\ — f0000001.jpg… и actions.csv (seg — номер непрерывного куска).
/// </summary>
internal static class GameRecorder
{
    public const int Fps = 16;

    /// <summary>Игры, которые «auto» пишет сама (имя процесса без .exe, в нижнем регистре).</summary>
    // Сравнение без регистра: ProcessNameOf отдаёт нижний регистр, а SeriousSam.exe раньше не совпадал с «seriousSam».
    private static readonly HashSet<string> AutoGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "hl", "cstrike", "cs2", "csgo", "sam2", "samhd", "samhd_tse", "sam3", "sam4", "sam2017", "serioussam", "samtfe", "samtse",
        "ut2004", "zuma", "zumasrevenge", "javaw", "minecraft", "quake3", "doom", "gzdoom",
    };

    private static readonly object Gate = new();
    private static Thread? _frameThread;
    private static volatile bool _running;
    private static IDisposable? _input;
    private static string _root = "";
    private static string _want = "";          // часть заголовка окна или "" = auto
    private static string _status = "не пишу";
    private static long _frames;
    private static DateTime _startedAt;
    private static readonly Dictionary<string, Session> Sessions = new(StringComparer.OrdinalIgnoreCase);

    // ввод между кадрами (под Gate)
    private static readonly HashSet<ushort> Held = [];
    private static readonly HashSet<ushort> Tapped = [];
    private static int _dx, _dy, _wheel;
    private static bool _l, _r, _m, _lClick, _rClick, _mClick;

    public static bool Recording => _running;

    /// <summary>Папки записей прошлой остановки (Арена помечает в них показ друга — dagger.txt).</summary>
    public static IReadOnlyList<string> LastDirs { get; private set; } = [];

    public static string Status()
    {
        if (!_running) return _status;
        lock (Gate)
        {
            var mins = (DateTime.Now - _startedAt).TotalMinutes;
            var per = string.Join(", ", Sessions.Values.Select(s => $"{s.Game}: {s.Frames / Fps / 60.0:F1} мин"));
            var elevated = _elevatedWarned is { } g ? $". «{g}» запущена от имени администратора — её не пишу: Windows не показывает мне клаву и мышь в ней (сними у игры «Запуск от имени администратора»)" : "";
            return $"пишу игру ({(_want.Length > 0 ? $"окно «{_want}»" : "любая известная игра")}), идёт {mins:F0} мин, кадров {_frames}{(per.Length > 0 ? $" — {per}" : " — игра ещё не была впереди")}{elevated}";
        }
    }

    /// <summary>Начать запись. window — часть заголовка окна игры или пусто (auto: известные игры).</summary>
    public static string Start(string root, string window)
    {
        lock (Gate)
        {
            if (_running) return $"Уже пишу: {Status()}";
            _root = root;
            _want = window.Trim();
            _frames = 0;
            _elevatedWarned = null;
            _startedAt = DateTime.Now;
            Sessions.Clear();
            _running = true;
        }
        lock (Gate) { Held.Clear(); Tapped.Clear(); _dx = _dy = _wheel = 0; }
        _input = RawInput.Subscribe(OnRaw);
        _frameThread = new Thread(FrameLoop) { IsBackground = true, Name = "gamer-frames", Priority = ThreadPriority.AboveNormal };
        _frameThread.Start();
        Log.Write($"учёба: запись игры началась ({(_want.Length > 0 ? _want : "auto")}) → {root}");
        return $"Пишу, как играет друг: {(_want.Length > 0 ? $"окно «{_want}»" : "CS, Serious Sam, Зума и другие известные игры")} — только пока игра впереди, 16 кадров в секунду и его клава с мышью. Остановить — record_stop.";
    }

    public static string Stop()
    {
        if (!_running) return "Запись и так не идёт.";
        _running = false;
        _frameThread?.Join(2000);
        _input?.Dispose();
        _input = null;
        string summary;
        lock (Gate)
        {
            foreach (var s in Sessions.Values) s.Dispose();
            LastDirs = Sessions.Values.Where(s => s.Frames > 0).Select(s => s.Dir).ToList();
            summary = Sessions.Count == 0
                ? "игра ни разу не была впереди — записать нечего"
                : string.Join("; ", Sessions.Values.Select(s => $"{s.Game}: {s.Frames} кадров ({s.Frames / (double)Fps / 60:F1} мин) → {s.Dir}"));
            _status = $"не пишу (прошлая запись: {summary})";
        }
        Log.Write($"учёба: запись остановлена — {summary}");
        return $"Запись остановила: {summary}.";
    }

    // ---------- кадры ----------

    private static void FrameLoop()
    {
        var clock = Stopwatch.StartNew();
        var next = 0.0;
        const double step = 1000.0 / Fps;
        string? lastGame = null;
        while (_running)
        {
            next += step;
            var wait = next - clock.Elapsed.TotalMilliseconds;
            if (wait > 1) Thread.Sleep((int)wait);
            else if (wait < -step * 4) next = clock.Elapsed.TotalMilliseconds;   // отстали (система думала) — не догонять пачкой

            var fg = Native.GetForegroundWindow();
            var game = GameOf(fg);
            // ввод собираем всегда, а пишем только кадры игры: иначе нажатия в другом окне перетекут в первый кадр
            var act = TakeInput(fg);
            if (game is null)
            {
                lastGame = null;
                continue;
            }
            byte[]? jpeg;
            try { jpeg = GameFrames.Grab(fg); }
            catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException) { jpeg = null; }
            if (jpeg is null) { lastGame = null; continue; }
            lock (Gate)
            {
                if (!Sessions.TryGetValue(game, out var s))
                {
                    s = new Session(_root, game, ProcessNameOf(fg), GameFrames.ClientOnScreen(fg).Size);
                    Sessions[game] = s;
                    Log.Write($"учёба: пишу «{game}» → {s.Dir}");
                }
                if (lastGame != game) s.NewSegment();
                s.Write(jpeg, (long)clock.Elapsed.TotalMilliseconds, act);
                _frames++;
            }
            lastGame = game;
        }
    }

    /// <summary>Игра впереди → её имя для папки; null — впереди не игра (или не та, что просили).</summary>
    private static string? GameOf(IntPtr fg)
    {
        if (fg == IntPtr.Zero) return null;
        Native.GetWindowThreadProcessId(fg, out var pid);
        if (pid == Environment.ProcessId) return null;
        var title = Uia.WindowTitle(fg);
        var proc = ProcessNameOf(fg);
        string? game;
        if (_want.Length > 0)
            game = title.Contains(_want, StringComparison.OrdinalIgnoreCase) || proc.Contains(_want, StringComparison.OrdinalIgnoreCase)
                ? Safe(title.Length > 0 ? title : proc) : null;
        else game = AutoGames.Contains(proc) ? Safe(proc is "hl" && title.Length > 0 ? title : proc) : null;
        // игра от администратора: Windows не отдаёт в неё (и из неё) ввод обычной программе — клава и мышь друга до
        // записи не дойдут, получились бы кадры без действий. Не пишем и говорим почему.
        if (game is not null && Hands.BlockedByElevation(pid))
        {
            if (_elevatedWarned != game)
            {
                _elevatedWarned = game;
                Log.Write($"учёба: «{game}» запущена от имени администратора — ввод друга не виден, не пишу");
            }
            return null;
        }
        return game;
    }

    private static string? _elevatedWarned;

    private static string Safe(string s) =>
        string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');

    private static string ProcessNameOf(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.ToLowerInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ""; }
    }

    /// <summary>Что делали клава и мышь с прошлого кадра; счётчики обнуляются.</summary>
    private static Act TakeInput(IntPtr fg)
    {
        lock (Gate)
        {
            var keys = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var k in Held) keys.Add(KeyName(k));
            foreach (var k in Tapped) keys.Add(KeyName(k));
            Tapped.Clear();
            var cur = PointF.Empty;
            var area = GameFrames.ClientOnScreen(fg);
            if (area.Width > 0 && Native.GetCursorPos(out var p))
                cur = new PointF((p.X - area.X) / (float)area.Width, (p.Y - area.Y) / (float)area.Height);
            var a = new Act(string.Join("+", keys), _dx, _dy, _l || _lClick, _r || _rClick, _m || _mClick, _wheel, cur);
            _dx = _dy = _wheel = 0;
            _lClick = _rClick = _mClick = false;
            return a;
        }
    }

    private static string KeyName(ushort vk) => vk switch
    {
        >= 'A' and <= 'Z' or >= '0' and <= '9' => ((char)vk).ToString(),
        0x20 => "SPACE", 0x10 or 0xA0 or 0xA1 => "SHIFT", 0x11 or 0xA2 or 0xA3 => "CTRL", 0x12 or 0xA4 or 0xA5 => "ALT",
        0x09 => "TAB", 0x1B => "ESC", 0x0D => "ENTER", 0x08 => "BACKSPACE",
        0x25 => "LEFT", 0x26 => "UP", 0x27 => "RIGHT", 0x28 => "DOWN",
        >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",
        0xC0 => "TILDE",
        _ => $"VK{vk:X2}",
    };

    internal readonly record struct Act(string Keys, int Dx, int Dy, bool L, bool R, bool M, int Wheel, PointF Cursor);

    /// <summary>Одна игра в одной записи: папка, кадры по номерам, actions.csv.</summary>
    private sealed class Session : IDisposable
    {
        public string Game { get; }
        public string Dir { get; }
        public long Frames { get; private set; }
        private int _seg;
        private readonly StreamWriter _csv;

        public Session(string root, string game, string process, Size client)
        {
            Game = game;
            Dir = Path.Combine(root, game, DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, "session.json"),
                $"{{\"game\":\"{game.Replace("\"", "'")}\",\"process\":\"{process}\",\"fps\":{Fps},\"frame\":[{GameFrames.Width},{GameFrames.Height}],\"client\":[{client.Width},{client.Height}],\"started\":\"{DateTime.Now:O}\"}}");
            _csv = new StreamWriter(Path.Combine(Dir, "actions.csv"), false, new UTF8Encoding(false)) { AutoFlush = false };
            _csv.WriteLine("frame,t_ms,seg,keys,dx,dy,lmb,rmb,mmb,wheel,cx,cy");
        }

        public void NewSegment() => _seg++;

        public void Write(byte[] jpeg, long tMs, Act a)
        {
            Frames++;
            File.WriteAllBytes(Path.Combine(Dir, $"f{Frames:D7}.jpg"), jpeg);
            _csv.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{Frames},{tMs},{_seg},{a.Keys},{a.Dx},{a.Dy},{(a.L ? 1 : 0)},{(a.R ? 1 : 0)},{(a.M ? 1 : 0)},{a.Wheel},{a.Cursor.X:F4},{a.Cursor.Y:F4}"));
            if (Frames % Fps == 0) _csv.Flush();
        }

        public void Dispose() => _csv.Dispose();
    }

    // ---------- ввод: живой друг через Raw Input ----------

    private static void OnRaw(RawInput.Event e)
    {
        lock (Gate)
        {
            if (e.Mouse)
            {
                _dx += e.Dx;
                _dy += e.Dy;
                var b = e.Buttons;
                if ((b & 0x001) != 0) { _l = true; _lClick = true; }
                if ((b & 0x002) != 0) _l = false;
                if ((b & 0x004) != 0) { _r = true; _rClick = true; }
                if ((b & 0x008) != 0) _r = false;
                if ((b & 0x010) != 0) { _m = true; _mClick = true; }
                if ((b & 0x020) != 0) _m = false;
                if ((b & 0x400) != 0) _wheel += e.Wheel;
            }
            else if (e.KeyUp) Held.Remove(e.Vk);
            else if (Held.Add(e.Vk)) Tapped.Add(e.Vk);
        }
    }
}

internal static partial class Native
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetClientRect(IntPtr hWnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(IntPtr hWnd, ref POINT point);
}
