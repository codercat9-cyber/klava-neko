using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Media.Ocr;

namespace KlavaDesk;

/// <summary>
/// Меню Counter-Strike 1.6 честными руками, как у живого игрока: глаза — OCR окна игры, руки — мышь SendInput (главное
/// меню и окно «Новая игра» — VGUI2 с обычным курсором) и цифры на клавиатуре (меню команды, класса и закупа — VGUI игры,
/// у пунктов цифры «1 ТЕРРОРИСТЫ», «5 СЛУЧАЙНЫЙ ВЫБОР»). Ни консоли, ни cfg.
/// Подписи — из русской сборки друга (cstrike_russian.txt, gameui_russian.txt): «Новая игра», «Запуск», «Карта»,
/// «Включить ботов в эту игру», «Число ботов», «Легкая/Средняя/Тяжелая/Эксперт», «ВЫБРАТЬ КОМАНДУ», «ВЫБЕРИТЕ КЛАСС»,
/// «Меню покупки», «Вернуться в игру», «Загрузка...». MOTD — кнопка «OK».
/// </summary>
internal static partial class CsMenu
{
    internal enum Kind { Unknown, MainMenu, Browser, CreateServer, MapList, Loading, Motd, Team, Class, Buy, Pause, InGame }

    /// <summary>Строка текста на кадре: координаты — в клиентской части окна игры.</summary>
    internal sealed record Line(string Text, string Norm, Rectangle Box);

    internal sealed record Screen(Kind Kind, List<Line> Lines, Size Client)
    {
        public Line? Find(params string[] needles) => Lines.FirstOrDefault(l => needles.Any(n => l.Norm.Contains(n, StringComparison.Ordinal)));
        public bool Has(params string[] needles) => Find(needles) is not null;
        public override string ToString() => $"{Kind}: {string.Join(" | ", Lines.Select(l => l.Text).Take(14))}";
    }

    /// <summary>Что делать в меню: игра с ботами на карте, за какую команду, каким персонажем.</summary>
    internal sealed record Plan(string Map = "de_dust2", int Bots = 9, string Difficulty = "средняя", string Team = "auto", int Model = 5);

    private static OcrEngine? _ocr;
    private static OcrEngine? Ocr => _ocr ??= OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("ru"))
                                              ?? OcrEngine.TryCreateFromUserProfileLanguages();
    private static OcrEngine? _en;
    private static OcrEngine? En => _en ??= OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")) ?? Ocr;

    public static string Norm(string s) => s.Trim().ToLowerInvariant().Replace('ё', 'е').Replace('0', 'о');

    /// <summary>Кадр клиентской части окна игры (игра должна быть впереди — DirectX-окно через PrintWindow чёрное).</summary>
    public static Bitmap? Grab(IntPtr hwnd)
    {
        var area = GameFrames.ClientOnScreen(hwnd);
        if (area.Width < 64 || area.Height < 64) return null;
        var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        foreach (var m in GameFrames.KlavaWindows())
        {
            var o = Rectangle.Intersect(m, area);
            if (!o.IsEmpty) g.FillRectangle(Brushes.Black, o.X - area.X, o.Y - area.Y, o.Width, o.Height);
        }
        return bmp;
    }

    /// <summary>
    /// Строки текста кадра. Мелкий шрифт VGUI на 720p OCR теряет — кадр увеличивается так, чтобы высота была ~1400 px.
    /// area — только кусок кадра (null — весь).
    /// </summary>
    public static async Task<List<Line>> ReadLinesAsync(Bitmap frame, Rectangle? area = null)
    {
        var a = area ?? new Rectangle(Point.Empty, frame.Size);
        a.Intersect(new Rectangle(Point.Empty, frame.Size));
        if (a.Width < 8 || a.Height < 8 || Ocr is not { } engine) return [];
        var k = Math.Clamp(1400.0 / Math.Max(a.Height, frame.Height * 0.5), 1.0, 3.0);
        return await RecognizeAsync(frame, a, k, engine, enhance: false);
    }

    /// <summary>
    /// Мелкий текст поля (карта в списке, число ботов): кусок ×3, серый с растянутым контрастом, тёмный текст на светлом
    /// (VGUI пишет светлым по тёмному — OCR так хуже). english — латиница (названия карт, цифры).
    /// </summary>
    public static async Task<List<Line>> ReadFieldAsync(Bitmap frame, Rectangle area, bool english)
    {
        area.Intersect(new Rectangle(Point.Empty, frame.Size));
        if (area.Width < 4 || area.Height < 4 || (english ? En : Ocr) is not { } engine) return [];
        return await RecognizeAsync(frame, area, 3.0, engine, enhance: true);
    }

    private static async Task<List<Line>> RecognizeAsync(Bitmap frame, Rectangle a, double k, OcrEngine engine, bool enhance)
    {
        var lines = new List<Line>();
        using var big = new Bitmap((int)(a.Width * k), (int)(a.Height * k), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(frame, new Rectangle(0, 0, big.Width, big.Height), a, GraphicsUnit.Pixel);
        }
        if (enhance) Enhance(big);
        try
        {
            using var soft = await Eyes.ToSoftwareBitmapAsync(big);
            var r = await engine.RecognizeAsync(soft);
            foreach (var l in r.Lines)
            {
                var ws = l.Words.ToList();
                if (ws.Count == 0) continue;
                var x0 = ws.Min(w => w.BoundingRect.X); var y0 = ws.Min(w => w.BoundingRect.Y);
                var x1 = ws.Max(w => w.BoundingRect.X + w.BoundingRect.Width); var y1 = ws.Max(w => w.BoundingRect.Y + w.BoundingRect.Height);
                var box = new Rectangle(a.X + (int)(x0 / k), a.Y + (int)(y0 / k), Math.Max(1, (int)((x1 - x0) / k)), Math.Max(1, (int)((y1 - y0) / k)));
                lines.Add(new Line(l.Text, Norm(l.Text), box));
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException) { Log.Error("меню CS: OCR", ex); }
        return lines;
    }

    /// <summary>Серый, контраст на весь диапазон, светлый текст на тёмном → тёмный на светлом.</summary>
    private static void Enhance(Bitmap bmp)
    {
        var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            var buf = new byte[d.Stride * bmp.Height];
            Marshal.Copy(d.Scan0, buf, 0, buf.Length);
            int lo = 255, hi = 0;
            long sum = 0;
            var lum = new byte[bmp.Width * bmp.Height];
            for (var y = 0; y < bmp.Height; y++)
                for (var x = 0; x < bmp.Width; x++)
                {
                    var i = y * d.Stride + x * 3;
                    // максимум каналов, а не яркость: зелёный текст VGUI по яркости почти как фон
                    var v = Math.Max(buf[i], Math.Max(buf[i + 1], buf[i + 2]));
                    lum[y * bmp.Width + x] = (byte)v;
                    lo = Math.Min(lo, v); hi = Math.Max(hi, v); sum += v;
                }
            var dark = sum / (double)lum.Length < 128;
            var span = Math.Max(1, hi - lo);
            for (var y = 0; y < bmp.Height; y++)
                for (var x = 0; x < bmp.Width; x++)
                {
                    var v = (lum[y * bmp.Width + x] - lo) * 255 / span;
                    var o = (byte)(dark ? 255 - v : v);
                    var i = y * d.Stride + x * 3;
                    buf[i] = buf[i + 1] = buf[i + 2] = o;
                }
            Marshal.Copy(buf, 0, d.Scan0, buf.Length);
        }
        finally { bmp.UnlockBits(d); }
    }

    public static async Task<Screen> ReadAsync(Bitmap frame)
    {
        var lines = await ReadLinesAsync(frame);
        return new Screen(Classify(lines, frame), lines, frame.Size);
    }

    [GeneratedRegex(@"^(de|cs|as|aim|awp|fy|he|ka|zm|gg|surf|35hp)_[a-z0-9_]+$")]
    private static partial Regex MapNameRx();

    [GeneratedRegex(@"(de|cs|as|aim|awp|fy|he|ka|zm|gg|surf|35hp)_[a-z0-9_]+")]
    private static partial Regex MapInRx();

    /// <summary>
    /// Названия карт из строк OCR: подчёркивание VGUI рисует пробелом, и «de_inferno» приходит двумя строками «de» и
    /// «inferno» — строки одного ряда склеиваются через «_», мусор вокруг названия отрезается. Ряд → (название, рамка).
    /// </summary>
    private static List<(string Name, Rectangle Box)> MapRows(List<Line> lines)
    {
        var rows = new List<List<Line>>();
        foreach (var l in lines.OrderBy(l => l.Box.Y))
        {
            var row = rows.FirstOrDefault(r => Math.Abs(Center(r[0].Box).Y - Center(l.Box).Y) <= Math.Max(4, l.Box.Height / 2));
            if (row is null) rows.Add([l]);
            else row.Add(l);
        }
        var res = new List<(string, Rectangle)>();
        foreach (var r in rows)
        {
            var text = string.Join("_", r.OrderBy(l => l.Box.X).Select(l => l.Text));
            var norm = MapNorm(text).Replace('х', 'x').Replace('о', 'o').Replace('е', 'e').Replace('а', 'a').Replace('с', 'c');
            if (MapInRx().Match(norm) is { Success: true } m)
            {
                var box = r.Select(l => l.Box).Aggregate(Rectangle.Union);
                res.Add((m.Value.TrimEnd('_'), box));
            }
        }
        return res;
    }

    private static Kind Classify(List<Line> lines, Bitmap frame)
    {
        bool Has(params string[] n) => lines.Any(l => n.Any(x => l.Norm.Contains(x, StringComparison.Ordinal)));
        if (Has("выбрать команду") || (Has("террорист") && Has("спецназ") && Has("наблюден", "случайн"))) return Kind.Team;
        if (Has("выберите класс") || (Has("случайный выбор") && Has("феникс", "элитн", "арктич", "партизан", "seal", "gsg", "sas", "gign")))
            return Kind.Class;
        if (Has("меню покупки", "автоматическая покупка", "магазин по категориям")) return Kind.Buy;
        if (Has("включить ботов", "число ботов") || (Has("запуск") && Has("отмена"))) return Kind.CreateServer;
        if (Has("вернуться в игру", "отсоединиться")) return Kind.Pause;
        // окно «Серверы» (сборка открывает его сама при запуске): в строках серверов тоже названия карт — это не список карт
        if (Has("избранное", "локальная сеть", "обновить все") || lines.Any(l => l.Norm.StartsWith("серверы (", StringComparison.Ordinal)))
            return Kind.Browser;
        if (lines.Count(l => MapNameRx().IsMatch(l.Norm.Replace(' ', '_'))) >= 3) return Kind.MapList;
        if (Has("новая игра") && Has("выйти", "настройки", "поиск серверов", "случайный сервер")) return Kind.MainMenu;
        if (Has("загрузка", "анализ информации", "соединение", "подключение", "загружается")) return Kind.Loading;
        if (Has("you are playing", "counter-strike.net") || lines.Any(l => l.Norm is "ok" or "ок" && l.Box.Y > frame.Height * 0.55))
            return Kind.Motd;
        return Uniform(frame) ? Kind.Loading : Kind.InGame;
    }

    /// <summary>Почти однотонный кадр (чёрный экран загрузки, заставка) — не игра.</summary>
    private static bool Uniform(Bitmap frame)
    {
        using var small = new Bitmap(frame, new Size(32, 18));
        var lum = new List<int>(32 * 18);
        for (var y = 0; y < 18; y++)
            for (var x = 0; x < 32; x++) { var c = small.GetPixel(x, y); lum.Add((c.R * 3 + c.G * 6 + c.B) / 10); }
        var mean = lum.Average();
        return lum.Average(v => Math.Abs(v - mean)) < 6;
    }

    // ---------- руки ----------

    /// <summary>Щелчок по точке клиентской части окна настоящим курсором (SendInput). Только если игра впереди.</summary>
    public static async Task<bool> ClickAsync(IntPtr hwnd, Point client, CancellationToken ct, bool right = false)
    {
        var pid = Hands.PidOf(hwnd);
        if (!Hands.InFront(pid)) return false;
        var p = new Native.POINT { X = client.X, Y = client.Y };
        Native.ClientToScreen(hwnd, ref p);
        MoveTo(p.X, p.Y);
        await Task.Delay(90, ct);
        MoveTo(p.X, p.Y);          // VGUI подсвечивает кнопку по движению — второй толчок на то же место
        await Task.Delay(60, ct);
        if (!Hands.InFront(pid)) return false;
        Mouse(right ? Native.MOUSEEVENTF_RIGHTDOWN : Native.MOUSEEVENTF_LEFTDOWN, 0);
        await Task.Delay(55, ct);
        Mouse(right ? Native.MOUSEEVENTF_RIGHTUP : Native.MOUSEEVENTF_LEFTUP, 0);
        return true;
    }

    /// <summary>Колесо мыши над точкой (notches: + вверх, − вниз).</summary>
    public static async Task WheelAsync(IntPtr hwnd, Point client, int notches, CancellationToken ct)
    {
        var p = new Native.POINT { X = client.X, Y = client.Y };
        Native.ClientToScreen(hwnd, ref p);
        MoveTo(p.X, p.Y);
        await Task.Delay(60, ct);
        for (var i = 0; i < Math.Abs(notches); i++)
        {
            Mouse(Native.MOUSEEVENTF_WHEEL, unchecked((uint)(notches > 0 ? 120 : -120)));
            await Task.Delay(40, ct);
        }
    }

    /// <summary>Нажать и отпустить клавишу сканкодом (цифры меню, Enter, Esc). Только если игра впереди.</summary>
    public static async Task<bool> TapAsync(IntPtr hwnd, ushort vk, CancellationToken ct, int holdMs = 70)
    {
        if (!Hands.InFront(Hands.PidOf(hwnd))) return false;
        Hands.GameKey(vk, true);
        try { await Task.Delay(holdMs, ct); }
        finally { Hands.GameKey(vk, false); }
        return true;
    }

    private static void MoveTo(int x, int y)
    {
        var vs = SystemInformation.VirtualScreen;
        var nx = (int)Math.Round((x - vs.Left) * 65535.0 / Math.Max(1, vs.Width - 1));
        var ny = (int)Math.Round((y - vs.Top) * 65535.0 / Math.Max(1, vs.Height - 1));
        Send(new Native.MOUSEINPUT { dx = nx, dy = ny, dwFlags = Native.MOUSEEVENTF_MOVE | Native.MOUSEEVENTF_ABSOLUTE | Native.MOUSEEVENTF_VIRTUALDESK });
    }

    private static void Mouse(uint flags, uint data) => Send(new Native.MOUSEINPUT { dwFlags = flags, mouseData = data });

    private static void Send(Native.MOUSEINPUT mi)
    {
        var input = new[] { new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.INPUTUNION { mi = mi } } };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }

    private static Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    // ---------- путь в игру ----------

    /// <summary>
    /// Из любого меню — в игру: главное меню → «Новая игра» → карта, боты, сложность → «Запуск» → загрузка → MOTD (Enter)
    /// → команда (1 — террористы, 2 — спецназ, 5 — случайно) → класс (1–4, 5 — случайный) → в игре. Уже в игре — сразу.
    /// newGame=false — только входные меню игры (MOTD, команда, класс, закуп): игра уже создана кем-то.
    /// Возвращает null — в игре; иначе — что не вышло.
    /// </summary>
    public static async Task<string?> EnterGameAsync(IntPtr hwnd, Plan plan, bool newGame, Action<string> log, CancellationToken ct, int maxSeconds = 150)
    {
        var clock = Stopwatch.StartNew();
        var inGame = 0;
        var serverDone = false;
        var browserTries = 0;
        var same = 0;
        var last = "";
        while (clock.Elapsed.TotalSeconds < maxSeconds)
        {
            ct.ThrowIfCancellationRequested();
            if (!Native.IsWindow(hwnd)) return "окно игры закрылось";
            if (!Hands.InFront(Hands.PidOf(hwnd)))
            {
                Uia.Focus(hwnd);
                await Task.Delay(400, ct);
                if (!Hands.InFront(Hands.PidOf(hwnd))) return "окно игры не впереди — меню жать некуда";
            }
            using var frame = Grab(hwnd);
            if (frame is null) { await Task.Delay(500, ct); continue; }
            var s = await ReadAsync(frame);
            var sig = s.ToString();
            same = sig == last ? same + 1 : 0;
            last = sig;
            if (same == 0) log($"меню CS: {sig}");
            if ((same >= 8 || browserTries > 8) && s.Kind is not (Kind.Loading or Kind.InGame)) return $"меню не меняется: {sig}";
            switch (s.Kind)
            {
                case Kind.InGame:
                    if (++inGame >= 2) return null;
                    await Task.Delay(500, ct);
                    continue;
                case Kind.Loading:
                    inGame = 0;
                    await Task.Delay(700, ct);
                    continue;
                case Kind.MainMenu:
                    inGame = 0;
                    if (!newGame) return "в главном меню, а игру создавать не просили";
                    if (s.Find("новая игра") is { } ng) await ClickAsync(hwnd, Center(ng.Box), ct);
                    await Task.Delay(1500, ct);
                    continue;
                case Kind.Browser:
                    // закрыть «Серверы» (сборка открывает его сама при запуске): Esc его не закрывает — крестик в правом
                    // верхнем углу окна; не нашёлся — просто «Новая игра» из главного меню под ним
                    inGame = 0;
                    browserTries++;
                    if (CloseCross(frame, s) is { } cross && browserTries <= 3)
                    {
                        log($"меню CS: закрываю «Серверы» крестиком ({cross.X},{cross.Y})");
                        await ClickAsync(hwnd, cross, ct);
                    }
                    else if (newGame && s.Find("новая игра") is { } ng2) await ClickAsync(hwnd, Center(ng2.Box), ct);
                    else await TapAsync(hwnd, 0x1B, ct);
                    await Task.Delay(700, ct);
                    continue;
                case Kind.Pause:
                    // меню паузы: просили новую игру (и ещё не создали) — «Новая игра» прямо отсюда, иначе — назад в игру
                    inGame = 0;
                    if (newGame && !serverDone && s.Find("новая игра") is { } ng3) await ClickAsync(hwnd, Center(ng3.Box), ct);
                    else if (s.Find("вернуться в игру") is { } back) await ClickAsync(hwnd, Center(back.Box), ct);
                    else await TapAsync(hwnd, 0x1B, ct);
                    await Task.Delay(700, ct);
                    continue;
                case Kind.MapList:
                    // список карт раскрыт посреди окна (прошлый заход оборвался) — закрыть, настройка пойдёт заново
                    await TapAsync(hwnd, 0x1B, ct);
                    await Task.Delay(400, ct);
                    continue;
                case Kind.CreateServer:
                    inGame = 0;
                    if (serverDone)
                    {
                        if (s.Find("запуск") is { } go2) await ClickAsync(hwnd, Center(go2.Box), ct);
                        await Task.Delay(1500, ct);
                        continue;
                    }
                    serverDone = await SetupServerAsync(hwnd, s, frame, plan, log, ct);
                    continue;
                case Kind.Motd:
                    inGame = 0;
                    if (same >= 1 && s.Lines.FirstOrDefault(l => l.Norm is "ok" or "ок") is { } ok) await ClickAsync(hwnd, Center(ok.Box), ct);
                    else await TapAsync(hwnd, 0x0D, ct);
                    await Task.Delay(800, ct);
                    continue;
                case Kind.Team:
                    inGame = 0;
                    var t = plan.Team.Trim().ToLowerInvariant();
                    ushort teamKey = t.StartsWith("ct") || t.StartsWith("спец") || t.StartsWith("кт") ? (ushort)'2'
                        : t.StartsWith('t') || t.StartsWith("терр") || t.StartsWith('т') ? (ushort)'1' : (ushort)'5';
                    await TapAsync(hwnd, teamKey, ct);
                    await Task.Delay(900, ct);
                    continue;
                case Kind.Class:
                    inGame = 0;
                    await TapAsync(hwnd, (ushort)('0' + Math.Clamp(plan.Model, 1, 5)), ct);
                    await Task.Delay(900, ct);
                    continue;
                case Kind.Buy:
                    inGame = 0;
                    await TapAsync(hwnd, (ushort)'0', ct);   // вход в игру — закуп потом, по началу раунда
                    await Task.Delay(500, ct);
                    continue;
                default:
                    await Task.Delay(700, ct);
                    continue;
            }
        }
        return $"за {maxSeconds} с в игру не попала: {last}";
    }

    /// <summary>
    /// Окно «Создать сервер», вкладка «Сервер» (сборка друга): «Карта» [список], «Включить ботов в эту игру», «Число ботов»
    /// [поле], «Сложность» (Легкая/Средняя/Тяжелая/Эксперт), кнопка «Запуск». Всё за один заход, каждый шаг — по свежему
    /// кадру. true — «Запуск» нажат.
    /// </summary>
    private static async Task<bool> SetupServerAsync(IntPtr hwnd, Screen s, Bitmap frame, Plan plan, Action<string> log, CancellationToken ct)
    {
        // вкладка «Сервер» (открыта «Игра» — подписей карты и ботов не видно)
        if (!s.Has("карта") && s.Lines.FirstOrDefault(l => l.Norm.StartsWith("сервер", StringComparison.Ordinal) && !l.Norm.Contains("создать")) is { } tab)
        {
            await ClickAsync(hwnd, new Point(tab.Box.X + tab.Box.Height, Center(tab.Box).Y), ct);
            await Task.Delay(500, ct);
            return false;
        }
        // 1. карта: подпись «Карта» самого окна — над «Включить ботов»; в шапке списка серверов позади тоже есть «Карта»
        var anchor = s.Find("включить ботов") ?? s.Find("создать сервер");
        var mapLabel = s.Lines.Where(l => l.Norm == "карта" || l.Norm.StartsWith("карта ", StringComparison.Ordinal))
            .OrderBy(l => anchor is null ? 0 : Math.Abs(l.Box.Y - anchor.Box.Y) + Math.Abs(l.Box.X - anchor.Box.X)).FirstOrDefault();
        if (mapLabel is not null)
            await PickMapAsync(hwnd, frame, mapLabel, plan.Map.ToLowerInvariant(), log, ct);
        using var f2 = Grab(hwnd);
        if (f2 is null) return false;
        var s2 = await ReadAsync(f2);
        // 2. боты: галочка (щелчок по подписи переключает), число, сложность
        if (plan.Bots > 0 && s2.Find("включить ботов") is { } include)
        {
            if (!Checked(f2, include.Box))
            {
                log("меню CS: включаю ботов");
                await ClickAsync(hwnd, Center(include.Box), ct);
                await Task.Delay(400, ct);
            }
            if (s2.Find("число ботов") is { } countLabel)
            {
                var h = countLabel.Box.Height;
                // само поле: одинокое «12» OCR видит только в узком куске (+5…+13 высот строки правее подписи)
                var area = new Rectangle(countLabel.Box.Right + 5 * h, countLabel.Box.Y - h, 8 * h, 3 * h);
                var digits = (await ReadFieldAsync(f2, area, english: true)).Select(l => new string(l.Text.Where(char.IsAsciiDigit).ToArray())).FirstOrDefault(t => t.Length > 0);
                var want = plan.Bots.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (digits != want)
                {
                    await ClickAsync(hwnd, new Point(countLabel.Box.Right + 9 * h, Center(countLabel.Box).Y), ct);
                    await Task.Delay(200, ct);
                    await TapAsync(hwnd, 0x23, ct);                                   // End
                    for (var i = 0; i < 4; i++) await TapAsync(hwnd, 0x08, ct, 40);   // Backspace
                    foreach (var ch in want) await TapAsync(hwnd, ch, ct, 40);
                    log($"меню CS: ботов было {digits ?? "?"}, ставлю {want}");
                    await Task.Delay(300, ct);
                }
            }
            if (s2.Find(plan.Difficulty.ToLowerInvariant().Replace('ё', 'е')) is { } diff)
            {
                await ClickAsync(hwnd, Center(diff.Box), ct);
                await Task.Delay(200, ct);
            }
        }
        if (s2.Find("запуск") is not { } go) { log("меню CS: кнопки «Запуск» не вижу"); return false; }
        log($"меню CS: «Запуск» — {plan.Map}, ботов {plan.Bots}, {plan.Difficulty}");
        await ClickAsync(hwnd, Center(go.Box), ct);
        await Task.Delay(2000, ct);
        return true;
    }

    private static string MapNorm(string t) => t.Trim().ToLowerInvariant().Replace(' ', '_').Replace("__", "_");

    /// <summary>
    /// Карта в выпадающем списке справа от «Карта»: сейчас нужная — ничего; иначе раскрыть, искать в списке (латиница,
    /// OCR по-английски), нет среди видимых — колесо вниз/вверх по алфавиту; не нашлась — Esc, играем на текущей.
    /// </summary>
    private static async Task PickMapAsync(IntPtr hwnd, Bitmap frame, Line label, string want, Action<string> log, CancellationToken ct)
    {
        var h = label.Box.Height;
        var combo = new Rectangle(label.Box.Right + 2, label.Box.Y - h, 17 * h, 3 * h);
        // оба движка: зелёную латиницу в поле списка русский читает, английский — нет (кадр 28.09)
        var cur = MapRows(await ReadFieldAsync(frame, combo, english: false)).Concat(MapRows(await ReadFieldAsync(frame, combo, english: true)))
            .Select(r => r.Name).FirstOrDefault() ?? "";
        if (cur == want) return;
        log($"меню CS: карта сейчас «{(cur.Length > 0 ? cur : "?")}», нужна {want} — открываю список");
        await ClickAsync(hwnd, new Point(combo.X + combo.Width / 2, combo.Y + combo.Height / 2), ct);
        await Task.Delay(500, ct);
        var list = new Rectangle(combo.X - h, combo.Bottom - h / 2, combo.Width + 2 * h, 40 * h);
        var prevTop = "";
        for (var tries = 0; tries < 16; tries++)
        {
            using var f = Grab(hwnd);
            if (f is null) return;
            // список — латиница: английский движок читает его точно («nuke», «3x3»), русский — запасной
            var items = MapRows(await ReadFieldAsync(f, list, english: true));
            if (items.Count < 3) items = MapRows(await ReadFieldAsync(f, list, english: false));
            if (items.Count == 0) break;
            if (items.FirstOrDefault(r => r.Name == want) is { Name: not null } hit)
            {
                log($"меню CS: карта {want} — щёлкаю");
                await ClickAsync(hwnd, Center(hit.Box), ct);
                await Task.Delay(400, ct);
                return;
            }
            var names = items.Select(r => r.Name).ToList();
            var down = string.CompareOrdinal(want, names.Max()) > 0;
            if (names[0] == prevTop && tries > 0) break;   // список не едет — дошли до края
            prevTop = names[0];
            var mid = items[items.Count / 2].Box;
            log($"меню CS: в списке {names.First()} … {names.Last()}, {want} {(down ? "ниже" : "выше")} — колесо");
            await WheelAsync(hwnd, Center(mid), down ? -3 : 3, ct);
            await Task.Delay(300, ct);
        }
        log($"меню CS: карты {want} в списке не нашла — играю на «{cur}»");
        await TapAsync(hwnd, 0x1B, ct);
        await Task.Delay(300, ct);
    }

    /// <summary>
    /// Крестик окна «Серверы»: в полосе заголовка (высота надписи «Серверы» — самой верхней такой строки) самый правый
    /// светлый значок правее надписи. Фон вокруг — чёрно-зелёный RAZER, окна Клавы закрашены чёрным, так что белое там
    /// одно — крестик (кадр 28.09: окно 0…640, крестик на 625,16).
    /// </summary>
    private static Point? CloseCross(Bitmap frame, Screen s)
    {
        var title = s.Lines.Where(l => l.Norm.StartsWith("серверы", StringComparison.Ordinal)).OrderBy(l => l.Box.Y).FirstOrDefault();
        if (title is null) return null;
        var y0 = Math.Max(0, title.Box.Y - title.Box.Height);
        var y1 = Math.Min(frame.Height - 1, title.Box.Bottom + title.Box.Height);
        for (var x = frame.Width - 1; x > title.Box.Right + title.Box.Height; x--)
            for (var y = y0; y <= y1; y++)
            {
                // светлый и бесцветный: крестик серо-белый (193,191,189), логотип RAZER — насыщенно-зелёный
                var c = frame.GetPixel(x, y);
                if (c.R + c.G + c.B > 3 * 150 && Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B)) < 40)
                    return new Point(x - title.Box.Height / 3, (y0 + y1) / 2);
            }
        return null;
    }

    /// <summary>Галочка слева от подписи: у сборки друга включённая — белый квадратик с птичкой, выключенная — тёмный.</summary>
    private static bool Checked(Bitmap frame, Rectangle label)
    {
        var h = label.Height;
        var box = new Rectangle(label.X - (int)(h * 1.6), label.Y - h / 4, (int)(h * 1.4), (int)(h * 1.5));
        box.Intersect(new Rectangle(Point.Empty, frame.Size));
        if (box.Width < 3 || box.Height < 3) return true;
        int bright = 0, all = 0;
        for (var y = box.Top; y < box.Bottom; y++)
            for (var x = box.Left; x < box.Right; x++)
            {
                var c = frame.GetPixel(x, y);
                all++;
                if (c.R + c.G + c.B > 3 * 170) bright++;
            }
        return bright > all * 0.06;
    }

    /// <summary>
    /// KlavaDesk.exe --cs-menu look [файл.png] — что видит в окне CS (тип экрана и строки OCR с рамками), кадр в файл;
    /// --cs-menu enter [map] [bots] [team] [model] — пройти меню до игры (заберёт мышь и клаву; CS должна быть открыта).
    /// </summary>
    public static int Test(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length >= 7 && args[0] == "field")
        {
            // --cs-menu field кадр.png x y w h en|ru — OCR куска сохранённого кадра, как для полей меню
            using var img = new Bitmap(args[1]);
            var area = new Rectangle(int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]));
            foreach (var l in ReadFieldAsync(img, area, args[6] == "en").GetAwaiter().GetResult())
                Console.WriteLine($"  [{l.Box.X},{l.Box.Y} {l.Box.Width}x{l.Box.Height}] {l.Text}");
            return 0;
        }
        var hwnd = Uia.FindWindow("Counter-Strike");
        if (hwnd == IntPtr.Zero) { Console.WriteLine("окна Counter-Strike нет"); return 2; }
        Uia.Focus(hwnd);
        Thread.Sleep(700);
        var mode = args.Length > 0 ? args[0] : "look";
        if (mode == "key" && args.Length > 1)
        {
            var vk = args[1].Length == 1 ? (ushort)char.ToUpperInvariant(args[1][0]) : Convert.ToUInt16(args[1], 16);
            Console.WriteLine(TapAsync(hwnd, vk, CancellationToken.None).GetAwaiter().GetResult() ? $"нажала {args[1]}" : "игра не впереди");
            return 0;
        }
        if (mode == "click" && args.Length > 2)
        {
            var ok = ClickAsync(hwnd, new Point(int.Parse(args[1]), int.Parse(args[2])), CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine(ok ? "щёлкнула" : "игра не впереди");
            return 0;
        }
        if (mode == "label")
        {
            // надпись под прицелом («Товарищ : имя Здоровье : 100%») — как её читает Арена
            using var frame = args.Length > 1 ? new Bitmap(args[1]) : Grab(hwnd);
            if (frame is null) { Console.WriteLine("кадра нет"); return 1; }
            var area = new Rectangle(0, (int)(frame.Height * 0.85), (int)(frame.Width * 0.45), (int)(frame.Height * 0.11));
            using var crop = frame.Clone(area, PixelFormat.Format24bppRgb);
            using var ink = ArenaPlayer.Ink(crop);
            var dir = Path.GetTempPath();
            crop.Save(Path.Combine(dir, "cs_label.png"), ImageFormat.Png);
            ink.Save(Path.Combine(dir, "cs_label_ink.png"), ImageFormat.Png);
            Console.WriteLine($"  цвет надписи: {ArenaPlayer.LabelTeam(crop) ?? "нет надписи"}");
            foreach (var l in ReadLinesAsync(ink).GetAwaiter().GetResult()) Console.WriteLine($"  ink: {l.Text}");
            foreach (var l in ReadLinesAsync(crop).GetAwaiter().GetResult()) Console.WriteLine($"  raw: {l.Text}");
            Console.WriteLine($"кусок {area} → {dir}cs_label*.png");
            return 0;
        }
        if (mode == "look")
        {
            using var frame = Grab(hwnd);
            if (frame is null) { Console.WriteLine("кадра нет"); return 1; }
            var file = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), "cs_menu.png");
            frame.Save(file, ImageFormat.Png);
            var s = ReadAsync(frame).GetAwaiter().GetResult();
            Console.WriteLine($"экран: {s.Kind}, кадр {frame.Width}x{frame.Height} → {file}");
            Console.WriteLine($"окно игры на экране: {GameFrames.ClientOnScreen(hwnd)}; окна Клавы: {string.Join("; ", GameFrames.KlavaWindows())}; " +
                              $"экран {System.Windows.Forms.Screen.PrimaryScreen?.Bounds}");
            foreach (var l in s.Lines) Console.WriteLine($"  [{l.Box.X},{l.Box.Y} {l.Box.Width}x{l.Box.Height}] {l.Text}");
            return 0;
        }
        var plan = new Plan(
            args.Length > 1 ? args[1] : "de_dust2",
            args.Length > 2 && int.TryParse(args[2], out var b) ? b : 9,
            "средняя",
            args.Length > 3 ? args[3] : "auto",
            args.Length > 4 && int.TryParse(args[4], out var m) ? m : 5);
        var err = EnterGameAsync(hwnd, plan, newGame: true, l => Console.WriteLine(l), CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine(err is null ? "В ИГРЕ" : $"НЕ ВЫШЛО: {err}");
        return err is null ? 0 : 1;
    }
}
