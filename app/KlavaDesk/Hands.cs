using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace KlavaDesk;

/// <summary>
/// Лапки Клавы: мышь и клавиатура через SendInput. Работают только внутри «сеанса», который друг разрешил
/// на конкретную задачу (см. Tools). Пока сеанс идёт — у курсора кошачья лапка, а низкоуровневые хуки
/// ловят живого друга: его Backspace или Esc (не синтетические) мгновенно отбирают лапки; мышь — нет, её задевают случайно.
/// Своё синтетическое она отличает по флагу INJECTED, поэтому сама себя не останавливает.
/// </summary>
internal sealed partial class Hands : IDisposable
{
    private readonly Control? _ui;   // null — лапки автопилота (без хуков разговора и без Control в чужом потоке)
    private PawWindow? _paw;
    private IntPtr _mouseHook, _keyHook;
    private Native.HookProc? _mouseProc, _keyProc;   // держим делегаты, иначе сборщик мусора их съест под хуком
    private CancellationTokenSource? _stop;

    /// <summary>Друг забрал руки (нажал Backspace или Esc) — причина для лога и пузыря.</summary>
    public event Action<string>? Interrupted;
    /// <summary>Сеанс начался / закончился — страница поднимает и опускает лапку, окно Клавы становится сквозным.</summary>
    public event Action<bool>? ActiveChanged;

    public bool Active => _stop is not null;

    /// <summary>Отменяется, когда друг перехватил управление.</summary>
    public CancellationToken Token => _stop?.Token ?? CancellationToken.None;

    /// <summary>Последний снимок, по которому модель называет координаты: область экрана и масштаб снимка.</summary>
    public (Rectangle Bounds, double Scale)? Shot { get; set; }

    /// <summary>
    /// «Подвинься»: окно самой Клавы стоит поверх рабочего стола и перехватывает клики, если цель под ней.
    /// OverlayForm на время клика делает себя сквозной в этой области; Dispose — вернуть как было.
    /// </summary>
    public Func<Rectangle, IDisposable?>? MakeWay { get; set; }

    public Hands(Control? ui) => _ui = ui;

    private IDisposable? Way(Rectangle r) => MakeWay?.Invoke(r);

    private static Rectangle Around(params Point[] pts)
    {
        var x0 = pts.Min(p => p.X); var y0 = pts.Min(p => p.Y);
        var x1 = pts.Max(p => p.X); var y1 = pts.Max(p => p.Y);
        return new Rectangle(x0 - 2, y0 - 2, x1 - x0 + 5, y1 - y0 + 5);
    }

    /// <summary>Подвести курсор к точке экрана и кликнуть (цель в экранных координатах, не снимка).</summary>
    public async Task ClickAtAsync(Point screen, MouseButtons button, int count, CancellationToken ct)
    {
        await MoveAsync(screen, ct);
        await ClickAsync(button, count, ct);
    }

    /// <summary>Начать сеанс (звать на UI-потоке). Повторный вызов в том же сеансе ничего не делает.</summary>
    public void Begin()
    {
        if (Active) return;
        _stop = new CancellationTokenSource();
        _mouseProc = MouseHook;
        _keyProc = KeyHook;
        var mod = Native.GetModuleHandle(null);
        _mouseHook = Native.SetWindowsHookExW(Native.WH_MOUSE_LL, _mouseProc, mod, 0);
        _keyHook = Native.SetWindowsHookExW(Native.WH_KEYBOARD_LL, _keyProc, mod, 0);
        if (_mouseHook == IntPtr.Zero || _keyHook == IntPtr.Zero)
            Log.Write($"лапки: хук не встал (код {Marshal.GetLastPInvokeError()}) — стоп только голосом и рацией");
        _paw ??= new PawWindow();
        _paw.ShowAt(Cursor.Position);
        Log.Write("лапки: взяла мышь");
        ActiveChanged?.Invoke(true);
    }

    /// <summary>Закончить сеанс: снять хуки, спрятать лапку.</summary>
    public void End()
    {
        PadReleaseAll("договорила");
        if (!Active) return;
        if (_mouseHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
        _stop?.Dispose();
        _stop = null;
        _paw?.Hide();
        Log.Write("лапки: отпустила мышь");
        ActiveChanged?.Invoke(false);
    }

    /// <summary>Отобрать лапки (друг сказал «стоп», перебил рацией, закрыл Клаву).</summary>
    public void Interrupt(string why)
    {
        PadReleaseAll(why);
        if (!Active || _stop is null || _stop.IsCancellationRequested) return;
        try { _stop!.Cancel(); } catch (ObjectDisposedException) { }
        Log.Write($"лапки: стоп — {why}");
        Interrupted?.Invoke(why);
    }

    public void Dispose()
    {
        End();
        _paw?.Dispose();
    }

    // ---------- хуки ----------

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == Native.WM_MOUSEMOVE && Active)
        {
            // лапка едет за своим (синтетическим) курсором; живую мышь друга не слушаем — руки отбираются кнопкой
            // (Backspace/Esc): мышь можно задеть случайно
            var info = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            if ((info.flags & Native.LLMHF_INJECTED) != 0) _paw?.MoveTo(info.pt);
        }
        return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == Native.WM_KEYDOWN || wParam == Native.WM_SYSKEYDOWN) && Active)
        {
            var info = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            if ((info.flags & Native.LLKHF_INJECTED) == 0 && info.vkCode is 0x08 or 0x1B)   // Backspace/Esc живого друга
                _ui?.BeginInvoke(() => Interrupt(info.vkCode == 0x08 ? "Backspace" : "Esc"));
        }
        return Native.CallNextHookEx(_keyHook, code, wParam, lParam);
    }

    // ---------- мышь ----------

    /// <summary>Координаты снимка → экран.</summary>
    public Point ToScreen(double x, double y)
    {
        var (b, k) = Shot ?? throw new InvalidOperationException("нет снимка экрана");
        return new Point((int)Math.Round(b.Left + x / k), (int)Math.Round(b.Top + y / k));
    }

    /// <summary>Плавно, по-кошачьи: разгон и торможение, 150–450 мс в зависимости от расстояния.</summary>
    public async Task MoveAsync(Point to, CancellationToken ct)
    {
        var from = Cursor.Position;
        var dist = Math.Sqrt(Math.Pow(to.X - from.X, 2) + Math.Pow(to.Y - from.Y, 2));
        var steps = Math.Clamp((int)(dist / 25), 6, 40);
        for (var i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = i / (double)steps;
            var e = t * t * (3 - 2 * t);
            var x = (int)Math.Round(from.X + (to.X - from.X) * e);
            var y = (int)Math.Round(from.Y + (to.Y - from.Y) * e);
            MoveAbsolute(x, y);
            _paw?.MoveTo(new Native.POINT { X = x, Y = y });
            await Task.Delay(11, ct);
        }
    }

    /// <summary>
    /// Курсор — через SendInput, а не SetCursorPos: такое движение хук гарантированно видит с флагом INJECTED
    /// и не путает с рукой друга. Координаты абсолютные, 0…65535 по всему виртуальному рабочему столу.
    /// </summary>
    private static void MoveAbsolute(int x, int y)
    {
        var vs = SystemInformation.VirtualScreen;
        var nx = (int)Math.Round((x - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1));
        var ny = (int)Math.Round((y - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1));
        var input = new[] { new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.INPUTUNION { mi = new Native.MOUSEINPUT
        {
            dx = nx, dy = ny, dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK,
        } } } };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }

    public async Task ClickAsync(MouseButtons button, int count, CancellationToken ct)
    {
        var (down, up) = button switch
        {
            MouseButtons.Right => (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP),
            MouseButtons.Middle => (Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP),
            _ => (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP),
        };
        using var way = Way(Around(Cursor.Position));
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            _paw?.Press(true);
            SendMouse(down, 0);
            await Task.Delay(45, ct);
            SendMouse(up, 0);
            _paw?.Press(false);
            if (i + 1 < count) await Task.Delay(70, ct);
        }
    }

    public async Task DragAsync(Point from, Point to, CancellationToken ct)
    {
        using var way = Way(Around(from, to));
        await MoveAsync(from, ct);
        _paw?.Press(true);
        SendMouse(Native.MOUSEEVENTF_LEFTDOWN, 0);
        try
        {
            await Task.Delay(80, ct);
            await MoveAsync(to, ct);
            await Task.Delay(60, ct);
        }
        finally
        {
            SendMouse(Native.MOUSEEVENTF_LEFTUP, 0);   // даже при стопе кнопку не оставляем зажатой
            _paw?.Press(false);
        }
    }

    /// <summary>Колесо: плюс — вверх, минус — вниз; одна «щёлка» = 120.</summary>
    public async Task ScrollAsync(int clicks, CancellationToken ct)
    {
        var n = Math.Clamp(Math.Abs(clicks), 1, 30);
        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            SendMouse(Native.MOUSEEVENTF_WHEEL, (uint)(Math.Sign(clicks) * 120));
            await Task.Delay(35, ct);
        }
    }

    private static void SendMouse(uint flags, uint data)
    {
        var input = new[] { new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { dwFlags = flags, mouseData = data } } } };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }

    // ---------- клавиатура ----------

    /// <summary>Напечатать текст (юникод, любые буквы — раскладка не важна).</summary>
    public async Task TypeAsync(string text, CancellationToken ct)
    {
        foreach (var ch in text.Replace("\r\n", "\n"))
        {
            ct.ThrowIfCancellationRequested();
            if (ch == '\n') { SendKey(0x0D, true); SendKey(0x0D, false); }
            else
            {
                var inputs = new[]
                {
                    new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wScan = ch, dwFlags = Native.KEYEVENTF_UNICODE } } },
                    new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wScan = ch, dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP } } },
                };
                Native.SendInput(2, inputs, Marshal.SizeOf<Native.INPUT>());
            }
            await Task.Delay(12, ct);
        }
    }

    private static readonly Dictionary<string, ushort> KeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
        ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09, ["esc"] = 0x1B, ["escape"] = 0x1B, ["space"] = 0x20,
        ["backspace"] = 0x08, ["delete"] = 0x2E, ["del"] = 0x2E, ["insert"] = 0x2D, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["up"] = 0x26, ["down"] = 0x28, ["left"] = 0x25, ["right"] = 0x27,
        ["f1"] = 0x70, ["f2"] = 0x71, ["f3"] = 0x72, ["f4"] = 0x73, ["f5"] = 0x74, ["f6"] = 0x75,
        ["f7"] = 0x76, ["f8"] = 0x77, ["f9"] = 0x78, ["f10"] = 0x79, ["f11"] = 0x7A, ["f12"] = 0x7B,
        ["capslock"] = 0x14, ["caps"] = 0x14, ["lshift"] = 0xA0, ["rshift"] = 0xA1, ["lctrl"] = 0xA2, ["rctrl"] = 0xA3,
        ["lalt"] = 0xA4, ["ralt"] = 0xA5, ["menu"] = 0x5D, ["pause"] = 0x13, ["printscreen"] = 0x2C,
        ["tilde"] = 0xC0, ["grave"] = 0xC0, ["`"] = 0xC0, ["ё"] = 0xC0, ["minus"] = 0xBD, ["-"] = 0xBD, ["equals"] = 0xBB, ["="] = 0xBB,
        ["["] = 0xDB, ["]"] = 0xDD, [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE, ["/"] = 0xBF, ["\\"] = 0xDC,
        ["num0"] = 0x60, ["num1"] = 0x61, ["num2"] = 0x62, ["num3"] = 0x63, ["num4"] = 0x64, ["num5"] = 0x65,
        ["num6"] = 0x66, ["num7"] = 0x67, ["num8"] = 0x68, ["num9"] = 0x69,
        ["пробел"] = 0x20, ["шифт"] = 0x10, ["контрол"] = 0x11, ["энтер"] = 0x0D, ["вверх"] = 0x26, ["вниз"] = 0x28,
        ["влево"] = 0x25, ["вправо"] = 0x27,
    };

    // стрелки, Home/End, Insert/Delete, PageUp/Down — «расширенные» клавиши, без флага часть программ их путает с цифровым блоком
    private static readonly HashSet<ushort> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x2D, 0x2E, 0x5B];

    /// <summary>Разобрать «ctrl+shift+s» в коды клавиш; null — есть непонятная клавиша.</summary>
    public static List<ushort>? ParseCombo(string combo)
    {
        var keys = new List<ushort>();
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (KeyNames.TryGetValue(raw, out var vk)) keys.Add(vk);
            else if (raw.Length == 1 && char.ToUpperInvariant(raw[0]) is var c && (c is >= 'A' and <= 'Z' || c is >= '0' and <= '9')) keys.Add(c);
            else return null;
        }
        return keys.Count > 0 ? keys : null;
    }

    public async Task PressAsync(List<ushort> keys, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var k in keys) SendKey(k, true);
        await Task.Delay(40, CancellationToken.None);   // зажатые модификаторы отпускаем в любом случае
        for (var i = keys.Count - 1; i >= 0; i--) SendKey(keys[i], false);
    }

    /// <summary>Плавное рисование линии/мазка по набору точек (для Paint и рисования лапками).</summary>
    public async Task DrawStrokeAsync(List<Point> points, MouseButtons button, int delayMs, CancellationToken ct)
    {
        if (points.Count == 0) return;
        var (down, up) = button switch
        {
            MouseButtons.Right => (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP),
            MouseButtons.Middle => (Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP),
            _ => (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP),
        };

        using var way = Way(Around([.. points]));
        await MoveAsync(points[0], ct);
        _paw?.Press(true);
        SendMouse(down, 0);
        try
        {
            await Task.Delay(20, ct);
            for (var i = 1; i < points.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var p = points[i];
                MoveAbsolute(p.X, p.Y);
                _paw?.MoveTo(new Native.POINT { X = p.X, Y = p.Y });
                if (delayMs > 0) await Task.Delay(delayMs, ct);
            }
            await Task.Delay(20, ct);
        }
        finally
        {
            SendMouse(up, 0);
            _paw?.Press(false);
        }
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    /// <summary>
    /// Тысячи коротких штрихов подряд (рисунок карандашом, PencilPlan): без плавного подвода курсора между штрихами —
    /// перо просто поднимается и ставится в начало следующего. Паузы в 1–2 мс (таймер Windows на это время точный,
    /// иначе каждая пауза — 15 мс и рисунок шёл бы полчаса); каждые 30 штрихов Paint получает передышку дорисовать.
    /// Возвращает, сколько штрихов нарисовано (меньше — друг забрал мышь: ct).
    /// </summary>
    public async Task<int> DrawStrokesAsync(IReadOnlyList<Point[]> strokes, Rectangle area, CancellationToken ct)
    {
        if (strokes.Count == 0) return 0;
        using var way = Way(area);
        await MoveAsync(strokes[0][0], ct);
        var done = 0;
        await Task.Run(() =>
        {
            timeBeginPeriod(1);
            var down = false;
            try
            {
                foreach (var s in strokes)
                {
                    if (ct.IsCancellationRequested || s.Length < 2) { if (ct.IsCancellationRequested) break; continue; }
                    MoveAbsolute(s[0].X, s[0].Y);
                    Thread.Sleep(2);
                    SendMouse(Native.MOUSEEVENTF_LEFTDOWN, 0);
                    down = true;
                    Thread.Sleep(2);
                    for (var i = 1; i < s.Length; i++)
                    {
                        MoveAbsolute(s[i].X, s[i].Y);
                        Thread.Sleep(1);
                    }
                    Thread.Sleep(1);
                    SendMouse(Native.MOUSEEVENTF_LEFTUP, 0);
                    down = false;
                    done++;
                    if (done % 30 == 0)
                    {
                        _paw?.MoveTo(new Native.POINT { X = s[^1].X, Y = s[^1].Y });
                        Thread.Sleep(20);
                    }
                    else Thread.Sleep(2);
                }
            }
            finally
            {
                if (down) SendMouse(Native.MOUSEEVENTF_LEFTUP, 0);
                timeEndPeriod(1);
            }
        }, CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        return done;
    }

    /// <summary>Игровое удержание клавиши на время (бег W, газ, присед, прыжок Space).</summary>
    public async Task HoldKeyAsync(ushort vk, int durationMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SendKey(vk, true);
        try
        {
            await Task.Delay(Math.Clamp(durationMs, 10, 10000), ct);
        }
        finally
        {
            SendKey(vk, false);
        }
    }

    // ---------- игры ----------

    /// <summary>
    /// Клавиша для игры: сканкодом (KEYEVENTF_SCANCODE), как настоящая клавиатура. Игры на DirectInput / Raw Input
    /// (CS 1.6, многие шутеры) читают именно сканкод — нажатие одним виртуальным кодом они не видят.
    /// </summary>
    public static void GameKey(ushort vk, bool down)
    {
        var scan = (ushort)Native.MapVirtualKey(vk, Native.MAPVK_VK_TO_VSC);
        var flags = Native.KEYEVENTF_SCANCODE | (down ? 0u : Native.KEYEVENTF_KEYUP) | (Extended.Contains(vk) ? Native.KEYEVENTF_EXTENDEDKEY : 0u);
        var input = new[] { new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = scan == 0 ? vk : (ushort)0, wScan = scan, dwFlags = scan == 0 ? flags & ~Native.KEYEVENTF_SCANCODE : flags } } } };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }

    /// <summary>Зажать несколько клавиш разом на время (W+Shift — бег, W+A — по диагонали); отпускаются всегда.</summary>
    public async Task GameHoldAsync(List<ushort> keys, int durationMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        foreach (var k in keys) GameKey(k, true);
        try { await Task.Delay(Math.Clamp(durationMs, 20, 15000), ct); }
        finally { for (var i = keys.Count - 1; i >= 0; i--) GameKey(keys[i], false); }
    }

    /// <summary>
    /// Поворот камеры: относительное движение мыши (без ABSOLUTE) — так его видят игры с захваченным курсором.
    /// Размазано по времени мелкими шагами: один рывок на тысячу пикселей игры часто режут.
    /// </summary>
    public async Task LookAsync(int dx, int dy, int durationMs, CancellationToken ct)
    {
        var steps = Math.Clamp(durationMs / 10, 1, 200);
        int sentX = 0, sentY = 0;
        for (var i = 1; i <= steps; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tx = dx * i / steps;
            var ty = dy * i / steps;
            var input = new[] { new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.INPUTUNION { mi = new Native.MOUSEINPUT
            {
                dx = tx - sentX, dy = ty - sentY, dwFlags = Native.MOUSEEVENTF_MOVE,
            } } } };
            Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
            sentX = tx;
            sentY = ty;
            await Task.Delay(10, ct);
        }
    }

    /// <summary>Кнопка мыши на месте курсора: нажать и держать (копать в Minecraft, стрелять очередью).</summary>
    public async Task MouseHoldAsync(MouseButtons button, int durationMs, CancellationToken ct)
    {
        var (down, up) = button switch
        {
            MouseButtons.Right => (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP),
            MouseButtons.Middle => (Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP),
            _ => (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP),
        };
        SendMouse(down, 0);
        try { await Task.Delay(Math.Clamp(durationMs, 20, 15000), ct); }
        finally { SendMouse(up, 0); }
    }

    /// <summary>Найти виртуальный сканкод клавиши по имени.</summary>
    public static ushort? ResolveKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (KeyNames.TryGetValue(name.Trim(), out var vk)) return vk;
        var s = name.Trim().ToUpperInvariant();
        if (s.Length == 1 && (s[0] is >= 'A' and <= 'Z' || s[0] is >= '0' and <= '9')) return s[0];
        return null;
    }

    /// <summary>Ограничить курсор мыши прямоугольником окна игры для безопасного гейминга (чтобы лапки не вылетели на рабочий стол).</summary>
    public static IDisposable? ClipToWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero || !Native.GetWindowRect(hWnd, out var rect)) return null;
        Native.ClipCursor(in rect);
        return new ClipScope();
    }

    private sealed class ClipScope : IDisposable
    {
        public void Dispose() => Native.ResetClipCursor(IntPtr.Zero);
    }

    /// <summary>
    /// Обычное нажатие: виртуальный код ВМЕСТЕ со скан-кодом, как у настоящей клавиатуры. С одним виртуальным кодом
    /// игры на SDL/DirectInput (CS 1.6) нажатия не видели — «B» не открывал закуп; проверено по журналу игры.
    /// </summary>
    private static void SendKey(ushort vk, bool down)
    {
        var flags = (down ? 0u : Native.KEYEVENTF_KEYUP) | (Extended.Contains(vk) ? Native.KEYEVENTF_EXTENDEDKEY : 0u);
        var scan = (ushort)Native.MapVirtualKey(vk, Native.MAPVK_VK_TO_VSC);
        var input = new[] { new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags } } } };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }
}

/// <summary>
/// Кошачья лапка у курсора: слоистое окно с попиксельной прозрачностью, сквозное для мыши, поверх всех.
/// Рисуется кодом — голубой мех под цвет волос, розовые подушечки; при нажатии чуть сжимается.
/// </summary>
internal sealed class PawWindow : Form
{
    private const int Size0 = 46;
    private Bitmap? _up, _down;
    private bool _pressed;
    private Native.POINT _at;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public PawWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // лапка у курсора не должна попадать на снимки, по которым Клава ищет, куда кликнуть
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    public void ShowAt(Point p)
    {
        var k = DeviceDpi / 96.0;
        var s = (int)(Size0 * k);
        _up ??= Draw(s, false);
        _down ??= Draw(s, true);
        if (!Visible) Show();
        _at = new Native.POINT { X = p.X, Y = p.Y };
        Render();
    }

    public void MoveTo(Native.POINT p)
    {
        if (!Visible || (p.X == _at.X && p.Y == _at.Y)) return;
        _at = p;
        if (InvokeRequired) BeginInvoke(Render); else Render();
    }

    public void Press(bool down)
    {
        _pressed = down;
        if (InvokeRequired) BeginInvoke(Render); else Render();
    }

    /// <summary>Лапка стоит чуть правее-ниже острия курсора: подушечки «держат» стрелку, не закрывая цель.</summary>
    private void Render()
    {
        var bmp = _pressed ? _down : _up;
        if (bmp is null || IsDisposed) return;
        var screen = Native.GetDC(IntPtr.Zero);
        var mem = Native.CreateCompatibleDC(screen);
        var hbmp = bmp.GetHbitmap(Color.FromArgb(0));
        var old = Native.SelectObject(mem, hbmp);
        try
        {
            var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height };
            var pos = new Native.POINT { X = _at.X + 6, Y = _at.Y + 10 };
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 235, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            Native.SelectObject(mem, old);
            Native.DeleteObject(hbmp);
            Native.DeleteDC(mem);
            Native.ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private static Bitmap Draw(int s, bool pressed)
    {
        var bmp = new Bitmap(s, s, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        var k = s / 46f * (pressed ? 0.9f : 1f);
        var o = (s - 46 * k) / 2;
        RectangleF R(float x, float y, float w, float h) => new(o + x * k, o + y * k, w * k, h * k);
        using var fur = new SolidBrush(Color.FromArgb(255, 150, 190, 255));
        using var furEdge = new Pen(Color.FromArgb(255, 70, 110, 200), 1.6f * k);
        using var pad = new SolidBrush(Color.FromArgb(255, 255, 160, 190));
        // пальчики и ладошка — мехом
        foreach (var (x, y) in new[] { (3f, 12f), (13f, 3f), (25f, 3f), (35f, 12f) })
        {
            g.FillEllipse(fur, R(x, y, 12, 13));
            g.DrawEllipse(furEdge, R(x, y, 12, 13));
        }
        g.FillEllipse(fur, R(7, 17, 32, 27));
        g.DrawEllipse(furEdge, R(7, 17, 32, 27));
        // розовые подушечки
        foreach (var (x, y) in new[] { (6f, 15f), (15.5f, 6f), (27.5f, 6f), (37f, 15f) })
            g.FillEllipse(pad, R(x, y, 6, 7));
        g.FillEllipse(pad, R(14, 25, 18, 14));
        return bmp;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _up?.Dispose(); _down?.Dispose(); }
        base.Dispose(disposing);
    }
}
