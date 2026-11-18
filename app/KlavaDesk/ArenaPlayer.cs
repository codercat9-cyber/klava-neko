using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Арена — боевой игрок Клавы для шутеров (CS 1.6 с ботами, Serious Sam…). Сервер gamer\arena\server.py (YOLO11n-pose,
/// прицел как у человека, ноги — нейронка друга или разведчик) ~30 раз в секунду получает кадр игры и отвечает, что
/// держать на клаве, куда вести мышь и когда стрелять. Жмёт сама Клава честными лапками: клавиши сканкодами, мышь —
/// относительным движением SendInput (игра видит его как Raw Input), без консоли, cfg и памяти игры.
///
/// В CS сверх этого: меню входа (MOTD, команда, класс) и при надобности «Новая игра» с картой и ботами — CsMenu;
/// закуп в начале раунда — B, потом A («автоматическая покупка»); убили — до нового раунда ничего не жмёт (на экране
/// чужая камера); свои и чужие — цвет формы и надпись игры под прицелом «Товарищ/Враг» (OCR, шлётся серверу).
/// Друг взялся за мышь или клаву (живой ввод, Raw Input) — она отпускает всё и ждёт 1,5 с тишины; Backspace или
/// Esc — стоп совсем; окно игры ушло с переднего плана — пауза.
/// Протокол 4: [u32 json][u32 кадр 640×360][u32 середина кадра 640×360][u32 кадр 256×144] + байты; ответ — строка JSON.
/// </summary>
internal static class ArenaPlayer
{
    public const int Port = 47310;
    public const int BigWidth = 640, BigHeight = 360;
    public const int SmallWidth = 256, SmallHeight = 144;
    public const int Quality = 85;

    private static readonly ImageCodecInfo Jpeg = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
    private static readonly object ServerGate = new();

    private static CancellationTokenSource? _cts;
    private static Process? _server;
    private static StreamWriter? _serverLog;
    private static volatile string _status = "не играю";

    public static event Action<string>? Finished;

    public static bool Playing => _cts is not null;
    public static string Status() => _status;

    /// <summary>
    /// Кадры для сервера: весь кадр, середина (горизонт в родном разрешении — дальние цели) и маленький для ног, плюс
    /// вырезка под надпись; вырезку забирает TakeLabel (дальше она у фоновых глаз), иначе освобождается с кадром.
    /// </summary>
    internal sealed class Frames(byte[] big, byte[] mid, byte[] small, Bitmap label) : IDisposable
    {
        private Bitmap? _label = label;
        public byte[] Big { get; } = big;
        public byte[] Mid { get; } = mid;
        public byte[] Small { get; } = small;
        public Bitmap? TakeLabel() => Interlocked.Exchange(ref _label, null);
        public void Dispose() => TakeLabel()?.Dispose();
    }

    /// <summary>
    /// Один снимок клиентской части окна (окна Клавы закрашены) → три JPEG и вырезка слева внизу, где игра пишет
    /// «Товарищ : имя Здоровье : 100%» про человека под прицелом (x 0–45 %, y 85–96 % кадра).
    /// </summary>
    public static Frames? GrabFrames(IntPtr hwnd)
    {
        var area = GameFrames.ClientOnScreen(hwnd);
        if (area.Width < 64 || area.Height < 64) return null;
        using var full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            g.CopyFromScreen(area.Location, Point.Empty, area.Size);
            foreach (var m in GameFrames.KlavaWindows())
            {
                var o = Rectangle.Intersect(m, area);
                if (!o.IsEmpty) g.FillRectangle(Brushes.Black, o.X - area.X, o.Y - area.Y, o.Width, o.Height);
            }
        }
        var mid = new Rectangle(full.Width / 4, (int)(full.Height * 0.3), full.Width / 2, (int)(full.Height * 0.5));
        var label = new Rectangle(0, (int)(full.Height * 0.85), (int)(full.Width * 0.45), (int)(full.Height * 0.11));
        // весь кадр уменьшается ОДИН раз (640×360), маленький — уже из него; быстрый билинейный фильтр: «высококачественный»
        // на 1680×1050 брал ~20 мс на каждый из трёх кадров (замер 28.09: снимок целиком 72 мс → 8 кадров/с)
        using var big = Scale(full, new Rectangle(Point.Empty, full.Size), BigWidth, BigHeight);
        using var center = Scale(full, mid, BigWidth, BigHeight);
        using var small = Scale(big, new Rectangle(0, 0, BigWidth, BigHeight), SmallWidth, SmallHeight);
        return new Frames(Encode(big), Encode(center), Encode(small), full.Clone(label, PixelFormat.Format24bppRgb));
    }

    private static Bitmap Scale(Bitmap src, Rectangle from, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.DrawImage(src, new Rectangle(0, 0, w, h), from, GraphicsUnit.Pixel);
        return bmp;
    }

    private static byte[] Encode(Bitmap bmp)
    {
        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Quality);
        using var ms = new MemoryStream();
        bmp.Save(ms, Jpeg, ps);
        return ms.ToArray();
    }

    private static byte[] Encode(Bitmap src, Rectangle from, int w, int h)
    {
        using var bmp = Scale(src, from, w, h);
        return Encode(bmp);
    }

    /// <summary>
    /// Запуск. menu — для CS: пройти меню до игры (newGame — ещё и создать игру с картой и ботами через «Новая игра»).
    /// Возвращает сразу; итог — событием Finished.
    /// </summary>
    public static async Task<string> StartAsync(KlavaConfig cfg, string window, string game, int seconds, string? model = null,
        CsMenu.Plan? menu = null, bool newGame = false)
    {
        if (_cts is not null) return $"Уже играю на Арене: {_status}";
        var hwnd = Uia.FindWindow(window);
        if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(window))
            return $"Окна игры «{window}» не нашла — list_windows покажет, как оно называется.";
        var pid = Hands.PidOf(hwnd);
        if (pid == Environment.ProcessId) return "Впереди окно самой Клавы — укажи окно игры.";
        if (Hands.BlockedByElevation(pid))
            return "Не удалось: игра запущена от имени администратора, а я — нет: Windows не пропустит нажатия. Сними у игры «Запуск от имени администратора».";

        var gamerDir = cfg.Abs("gamer");
        var python = Path.Combine(gamerDir, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python)) return $"Нет окружения нейронки: {python}.";
        var serverScript = Path.Combine(gamerDir, "arena", "server.py");
        if (!File.Exists(serverScript)) return $"Нет боевого сервера Арены: {serverScript}.";
        var detector = Path.Combine(gamerDir, "models", "detect", "yolo11n-pose.pt");
        if (!File.Exists(detector)) return $"Нет весов детектора YOLO: {detector}.";
        var modelDir = string.IsNullOrWhiteSpace(model) ? Path.Combine(gamerDir, "models", game) : model;
        if (!Directory.Exists(modelDir) || !File.Exists(Path.Combine(modelDir, "policy.pt"))) modelDir = null;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _status = "поднимаю боевой сервер Арены";
        var ready = await StartServerAsync(python, serverScript, game, modelDir, detector, Port, cfg.Abs(@"data\logs\arena_server.log"));
        if (ready is not null)
        {
            _cts.Dispose();
            _cts = null;
            _status = "не играю";
            return $"Не удалось поднять сервер Арены: {ready}";
        }
        var limit = TimeSpan.FromSeconds(Math.Clamp(seconds <= 0 ? 600 : seconds, 30, 7200));
        var isCs = game.Contains("counter", StringComparison.OrdinalIgnoreCase) || window.Contains("counter", StringComparison.OrdinalIgnoreCase);
        if (isCs) CsBridge.Start(cfg);
        GameMode.Enter("arena");

        _ = Task.Run(async () =>
        {
            string result;
            try
            {
                string? menuErr = null;
                if (isCs && menu is not null)
                {
                    _status = "иду через меню игры";
                    Uia.Focus(hwnd);
                    menuErr = await CsMenu.EnterGameAsync(hwnd, menu, newGame, Log.Write, token);
                }
                result = menuErr is not null ? $"в игру не зашла: {menuErr}"
                    : await PlayAsync(hwnd, pid, limit, isCs, menu, cfg, game, window, token);
            }
            catch (OperationCanceledException) { result = "остановили"; }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or ObjectDisposedException or System.Text.Json.JsonException)
            {
                result = $"сломалось: {ex.Message}";
            }
            finally
            {
                GameMode.Leave("arena");
                StopServer();
            }
            var cts = _cts;
            _cts = null;
            cts?.Dispose();
            _status = $"не играю (прошлый раз: {result})";
            Log.Write($"арена: {result}");
            Finished?.Invoke(result);
        }, CancellationToken.None);

        return $"Арена запущена в «{Uia.WindowTitle(hwnd)}»{(isCs && menu is not null ? " — сначала меню игры" : "")}: YOLO ищет врагов, прицел по-человечески, " +
               "свои по форме и надписи не трогаю. Стоп — Backspace (или Esc), «стоп» голосом; друг взялся за мышь — отдаю.";
    }

    public static string Stop()
    {
        var cts = _cts;
        if (cts is null) return "Арена и так не запущена.";
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        return "Арена остановлена.";
    }

    // ---------- сервер ----------

    /// <summary>
    /// Поднять server.py и дождаться READY. Весь его вывод (stdout и stderr) всё время читается в журнал: иначе труба
    /// (~4 КБ) забивалась статистикой, сервер вставал на print, а клиент — на чтении ответа (28.09).
    /// </summary>
    private static async Task<string?> StartServerAsync(string python, string script, string game, string? modelDir, string detector, int port, string? logFile)
    {
        StopServer();
        var psi = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        foreach (var a in new[] { "-u", script, "--game", game, "--detector", detector, "--port", port.ToString(CultureInfo.InvariantCulture), "--proto", "4" })
            psi.ArgumentList.Add(a);
        if (modelDir is not null) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(modelDir); }
        // отладка: кадры с разметкой (что видит, куда целится, почему стреляет) — KLAVA_ARENA_DEBUG=папка
        if (Environment.GetEnvironmentVariable("KLAVA_ARENA_DEBUG") is { Length: > 0 } dbg) { psi.ArgumentList.Add("--debug"); psi.ArgumentList.Add(dbg); }
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        var readyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tail = new Queue<string>();
        StreamWriter? log = null;
        if (logFile is not null)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
                log = new StreamWriter(logFile, append: true, Encoding.UTF8) { AutoFlush = true };
                log.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {game}");
            }
            catch (IOException) { log = null; }
        }
        void Line(string? l)
        {
            if (l is null) return;
            lock (ServerGate)
            {
                log?.WriteLine(l);
                tail.Enqueue(l);
                if (tail.Count > 12) tail.Dequeue();
            }
            if (l.StartsWith("READY", StringComparison.Ordinal)) readyTcs.TrySetResult(l);
        }
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => Line(e.Data);
        p.ErrorDataReceived += (_, e) => Line(e.Data);
        p.Exited += (_, _) => readyTcs.TrySetResult("");
        if (!p.Start()) { log?.Dispose(); return "python не запустился"; }
        try { Native.TieToLifetime(p); } catch (InvalidOperationException) { }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        lock (ServerGate) { _server = p; _serverLog = log; }

        var done = await Task.WhenAny(readyTcs.Task, Task.Delay(TimeSpan.FromSeconds(90)));
        if (done == readyTcs.Task && readyTcs.Task.Result.StartsWith("READY", StringComparison.Ordinal))
        {
            Log.Write($"арена: сервер готов — {readyTcs.Task.Result}");
            return null;
        }
        string why;
        lock (ServerGate) why = string.Join(" | ", tail);
        StopServer();
        return why.Length > 0 ? why[Math.Max(0, why.Length - 500)..] : "сервер Арены не ответил READY за 90 с";
    }

    private static void StopServer()
    {
        Process? p;
        StreamWriter? log;
        lock (ServerGate) { p = _server; log = _serverLog; _server = null; _serverLog = null; }
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        p?.Dispose();
        lock (ServerGate) log?.Dispose();
    }

    // ---------- игра ----------

    /// <summary>Общее между циклом кадров и фоновыми глазами (OCR надписи и меню) и событиями CS.</summary>
    private sealed class Shared
    {
        public Bitmap? Label;                   // свежая вырезка под надпись «Товарищ/Враг» (Interlocked.Exchange)
        public volatile string? Tag;            // friend | enemy
        public volatile string? LabelTeam;      // цвет надписи под прицелом: T (красная) | CT (синяя)
        public long LabelAt;
        public long TagAt;                      // Stopwatch-мс, когда надпись прочитана
        public volatile CsMenu.Kind Menu = CsMenu.Kind.InGame;
        public long MenuAt;
        public volatile bool Dead;
        public volatile bool BuyPending;
        public volatile string? Event;          // kill | death | round — одно событие в следующий кадр
        public long UserUntil;                  // до какого мс друг держит управление
        public volatile string? StopWhy;
    }

    /// <param name="recordRoot">Куда писать показ друга (gamer\data): взялся за управление — GameRecorder пишет его игру
    /// (только живой ввод, её нажатия туда не попадают), 4 с тишины — запись закрыта; потом gamer train учит на этом.</param>
    private static async Task<string> PlayAsync(IntPtr hwnd, uint pid, TimeSpan limit, bool isCs, CsMenu.Plan? plan,
        KlavaConfig cfg, string game, string window, CancellationToken ct)
    {
        var recordRoot = cfg.Abs(Path.Combine("gamer", "data"));
        var finetunes = 0;
        // показ друга закрыт: пометить его (учёба весит такие записи вчетверо) и сразу дообучиться поверх готовой модели —
        // сервер Арены сам подхватит новые веса, как только train.py их допишет
        void Learn(string stopped)
        {
            Log.Write($"арена: {stopped}");
            foreach (var dir in GameRecorder.LastDirs)
            {
                try { File.WriteAllText(Path.Combine(dir, "dagger.txt"), $"показ друга: перехват управления в Арене {DateTime.Now:yyyy-MM-dd HH:mm:ss}"); }
                catch (IOException ex) { Log.Error("арена: пометка показа", ex); }
            }
            if (GameRecorder.LastDirs.Count == 0) return;
            if (GamerTrainer.Start(cfg, game, finetune: true) is { } err) Log.Write($"арена: дообучение не запустила — {err}");
            else finetunes++;
        }
        var paws = new Hands(null) { Standalone = true };
        var sh = new Shared();
        var clock = Stopwatch.StartNew();
        long kills = 0, deaths = 0;

        // живой ввод друга (Raw Input; её SendInput сюда не попадает): Backspace/Esc — стоп, остальное — «дай я сам».
        // Мышь — только заметное движение: больше 25 сырых единиц за 0,3 с (рука, лежащая на мыши, дрожит по 1–4)
        long moveFrom = 0, moved = 0;
        using var watch = RawInput.Subscribe(e =>
        {
            if (!e.Mouse && !e.KeyUp && e.Vk is 0x08 or 0x1B) { sh.StopWhy ??= e.Vk == 0x08 ? "друг нажал Backspace" : "друг нажал Esc"; return; }
            var now = clock.ElapsedMilliseconds;
            var take = !e.Mouse ? !e.KeyUp : e.Buttons != 0 || e.Wheel != 0;
            if (e.Mouse && !take)
            {
                if (now - Interlocked.Read(ref moveFrom) > 300) { Interlocked.Exchange(ref moveFrom, now); Interlocked.Exchange(ref moved, 0); }
                take = Interlocked.Add(ref moved, Math.Abs(e.Dx) + Math.Abs(e.Dy)) > 25;
            }
            if (take) Interlocked.Exchange(ref sh.UserUntil, now + 1500);
        });

        // события CS из журнала сервера: смерть, новый раунд (закуп), её фраги
        void OnCs(string line)
        {
            if (line.StartsWith("host_dead", StringComparison.Ordinal)) { sh.Dead = true; sh.Event = "death"; Interlocked.Increment(ref deaths); }
            else if (line.StartsWith("round_start", StringComparison.Ordinal)) { sh.Dead = false; sh.BuyPending = true; sh.Event = "round"; }
            else if (line.StartsWith("host_kill", StringComparison.Ordinal)) { sh.Event = "kill"; Interlocked.Increment(ref kills); }
        }
        if (isCs) CsBridge.Line += OnCs;

        // фоновые глаза: надпись под прицелом (4 раза в секунду) и меню игры (раз в 2 с)
        using var eyesCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var eyes = isCs ? Task.Run(() => EyesLoopAsync(sh, clock, eyesCts.Token), CancellationToken.None) : Task.CompletedTask;

        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", Port, ct);
        tcp.NoDelay = true;
        var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);

        var held = new HashSet<ushort>();
        bool lmb = false, rmb = false, paused = false, yielded = false;
        double accX = 0, accY = 0;
        long frames = 0, shots = 0;
        var next = 0.0;
        const double step = 1000.0 / 30.0;
        var lastWhy = "";
        var targets = 0;
        double grabMs = 0, rttMs = 0;   // скользящие средние: снимок+JPEG и ответ сервера
        Task<Frames?>? pending = null;   // следующий кадр снимается, пока сервер думает над этим (конвейер)
        var recByMe = false;             // запись показа друга начала арена (и она же закроет)
        var takeovers = 0;
        long taughtMs = 0, recFrom = 0;
        async Task DropPendingAsync()
        {
            if (pending is null) return;
            var p = pending;
            pending = null;
            (await p)?.Dispose();
        }
        sh.BuyPending = isCs;   // зашла в игру — сразу закупиться (если ещё время закупа)

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (clock.Elapsed > limit) return Summary("время вышло");
                if (sh.StopWhy is { } who) return Summary($"{who} — остановилась");

                next += step;
                var wait = next - clock.Elapsed.TotalMilliseconds;
                if (wait > 1) await Task.Delay((int)wait, ct);
                else if (wait < -step * 4) next = clock.Elapsed.TotalMilliseconds;

                if (!Hands.InFront(pid))
                {
                    await DropPendingAsync();
                    if (!paused)
                    {
                        ReleaseAll(paws, held, ref lmb, ref rmb);
                        await SendResetAsync(stream, reader, ct);
                        paused = true;
                        _status = "пауза: игра не впереди";
                    }
                    if (!Native.IsWindow(hwnd)) return Summary("окно игры закрылось");
                    continue;
                }
                paused = false;

                // меню игры поверх (смена карты — снова команда и класс; MOTD) — пройти честными клавишами
                if (isCs && sh.Menu is CsMenu.Kind.Motd or CsMenu.Kind.Team or CsMenu.Kind.Class && plan is not null)
                {
                    await DropPendingAsync();
                    ReleaseAll(paws, held, ref lmb, ref rmb);
                    _status = $"меню игры: {sh.Menu}";
                    var err = await CsMenu.EnterGameAsync(hwnd, plan, newGame: false, Log.Write, ct, maxSeconds: 40);
                    if (err is not null) Log.Write($"арена: меню — {err}");
                    sh.Menu = CsMenu.Kind.InGame;
                    sh.BuyPending = true;
                    continue;
                }

                var userActive = Interlocked.Read(ref sh.UserUntil) > clock.ElapsedMilliseconds;
                // показ друга — в записи для учёбы
                if (userActive && !recByMe && !GameRecorder.Recording)
                {
                    GameRecorder.Start(recordRoot, window);
                    recByMe = true;
                    takeovers++;
                    recFrom = clock.ElapsedMilliseconds;
                    Log.Write("арена: друг взял управление — пишу его показ для учёбы");
                }
                else if (recByMe && clock.ElapsedMilliseconds - Interlocked.Read(ref sh.UserUntil) > 2500)
                {
                    Learn($"друг отдал управление — {GameRecorder.Stop()}");
                    recByMe = false;
                    taughtMs += clock.ElapsedMilliseconds - recFrom;
                }
                // начало раунда — закуп: B (меню покупки), A — «автоматическая покупка»; меню осталось — 0
                if (isCs && sh.BuyPending && !sh.Dead && !userActive)
                {
                    sh.BuyPending = false;
                    await DropPendingAsync();
                    ReleaseAll(paws, held, ref lmb, ref rmb);
                    _status = "закупаюсь";
                    await BuyAsync(hwnd, sh, MyTeam(plan), ct);
                }

                var t0 = clock.Elapsed.TotalMilliseconds;
                Frames? got;
                if (pending is not null) { got = await pending; pending = null; }
                else got = GrabFrames(hwnd);
                using var fr = got;
                if (fr is null) continue;
                var t1 = clock.Elapsed.TotalMilliseconds;
                grabMs = grabMs * 0.9 + (t1 - t0) * 0.1;
                if (fr.TakeLabel() is { } lab)
                {
                    // цвет надписи — каждый кадр (дёшево); буквы OCR читает в фоне раз в 250 мс
                    if (isCs && LabelTeam(lab) is { } lt) { sh.LabelTeam = lt; Interlocked.Exchange(ref sh.LabelAt, clock.ElapsedMilliseconds); }
                    Interlocked.Exchange(ref sh.Label, lab)?.Dispose();   // не прочитанная глазами — старая, выбросить
                }
                var labelTeam = sh.LabelTeam is { } lteam && clock.ElapsedMilliseconds - Interlocked.Read(ref sh.LabelAt) < 250 ? lteam : null;
                var tag = sh.Tag is { } t && clock.ElapsedMilliseconds - Interlocked.Read(ref sh.TagAt) < 400 ? t : null;
                var menuNow = isCs && sh.Menu is CsMenu.Kind.MainMenu or CsMenu.Kind.Loading or CsMenu.Kind.Pause or CsMenu.Kind.Buy or CsMenu.Kind.CreateServer;
                if (userActive && !yielded) { ReleaseAll(paws, held, ref lmb, ref rmb); _status = "друг играет сам — жду"; }
                if (sh.Dead && !yielded) ReleaseAll(paws, held, ref lmb, ref rmb);
                yielded = userActive || sh.Dead || menuNow;

                var meta = new JsonObject
                {
                    ["t"] = clock.ElapsedMilliseconds, ["user"] = userActive, ["event"] = sh.Event, ["hold"] = false,
                    ["team"] = isCs ? MyTeam(plan) : "", ["tag"] = tag, ["label_team"] = labelTeam, ["dead"] = sh.Dead, ["menu"] = menuNow,
                };
                sh.Event = null;
                await SendFrameAsync(stream, Encoding.UTF8.GetBytes(meta.ToJsonString()), fr.Big, fr.Mid, fr.Small, ct);
                pending = Task.Run(() => GrabFrames(hwnd), CancellationToken.None);
                var line = await reader.ReadLineAsync(ct);
                if (line is null) return Summary("сервер Арены отвалился");
                rttMs = rttMs * 0.9 + (clock.Elapsed.TotalMilliseconds - t1) * 0.1;
                frames++;
                if (JsonNode.Parse(line) is not JsonObject o) continue;
                targets = o["targets"]?.GetValue<int>() ?? targets;
                lastWhy = o["why"]?.ToString() ?? lastWhy;
                if (frames % 30 == 0)
                    _status = $"Арена: {clock.Elapsed.TotalSeconds:F0} с, целей {targets}, {lastWhy}{(lmb ? " [ОГОНЬ]" : "")}, " +
                              $"фрагов {Interlocked.Read(ref kills)}, смертей {Interlocked.Read(ref deaths)} (кадр {grabMs:F0} мс, сервер {rttMs:F0} мс)";
                if (o["yield"]?.GetValue<bool>() == true || yielded) continue;

                // 1. клавиши: отпустить лишние, зажать новые, продлить зажатые
                var want = new HashSet<ushort>();
                if (o["keys"] is JsonArray keyArr)
                    foreach (var k in keyArr)
                        if (k?.ToString() is { Length: > 0 } kn && VkOf(kn) is { } vk) want.Add(vk);
                var off = held.Where(k => !want.Contains(k)).ToList();
                if (off.Count > 0) { paws.PadUp(off); foreach (var k in off) held.Remove(k); }
                var on = want.Where(k => !held.Contains(k)).ToList();
                if (on.Count > 0 && paws.PadDown(pid, on, 3000)) foreach (var k in on) held.Add(k);
                if (held.Count > 0) paws.PadDown(pid, [.. held], 3000);

                // 2. кнопки мыши
                Button(paws, pid, MouseButtons.Left, (o["lmb"]?.GetValue<int>() ?? 0) == 1, ref lmb, ref shots);
                Button(paws, pid, MouseButtons.Right, (o["rmb"]?.GetValue<int>() ?? 0) == 1, ref rmb, ref shots);

                // 3. поворот: дробные остатки копятся — доводки меньше единицы не пропадают
                accX += o["dx"]?.GetValue<double>() ?? 0;
                accY += o["dy"]?.GetValue<double>() ?? 0;
                var ix = (int)Math.Truncate(accX);
                var iy = (int)Math.Truncate(accY);
                accX -= ix;
                accY -= iy;
                if (ix != 0 || iy != 0) MoveMouse(ix, iy);
            }
        }
        finally
        {
            if (isCs) CsBridge.Line -= OnCs;
            eyesCts.Cancel();
            await DropPendingAsync();
            if (recByMe)
            {
                Learn(GameRecorder.Stop());
                taughtMs += clock.ElapsedMilliseconds - recFrom;
            }
            ReleaseAll(paws, held, ref lmb, ref rmb);
            paws.PadReleaseAll("арена закончила");
            try { await eyes; } catch (OperationCanceledException) { }
            Interlocked.Exchange(ref sh.Label, null)?.Dispose();
        }

        string Summary(string why) =>
            $"{why}. {clock.Elapsed.TotalMinutes:F1} мин, кадров {frames}, выстрелов {shots}, фрагов {Interlocked.Read(ref kills)}, смертей {Interlocked.Read(ref deaths)}" +
            (takeovers > 0 ? $"; друг брал управление {takeovers} раз(а) и показывал {taughtMs / 1000} с — записано, дообучение запускала {finetunes} раз(а) (новые веса подхватываются на лету)" : "");
    }

    /// <summary>
    /// Фоновые глаза для CS: надпись под прицелом (цветной текст «Товарищ : имя» → чёрное на белом, OCR) 4 раза в
    /// секунду и тип экрана (меню/игра) раз в 2 с.
    /// </summary>
    private static async Task EyesLoopAsync(Shared sh, Stopwatch clock, CancellationToken ct)
    {
        long lastMenu = 0;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(250, ct); }
            catch (OperationCanceledException) { return; }
            var label = Interlocked.Exchange(ref sh.Label, null);
            if (label is not null)
            {
                using (label)
                using (var ink = Ink(label))
                {
                    var text = string.Join(' ', (await CsMenu.ReadLinesAsync(ink)).Select(l => l.Norm));
                    var tag = text.Contains("товарищ", StringComparison.Ordinal) || text.Contains("друг", StringComparison.Ordinal) ? "friend"
                        : text.Contains("враг", StringComparison.Ordinal) || text.Contains("противник", StringComparison.Ordinal) ? "enemy" : null;
                    if (tag is not null)
                    {
                        sh.Tag = tag;
                        Interlocked.Exchange(ref sh.TagAt, clock.ElapsedMilliseconds);
                    }
                }
            }
            if (clock.ElapsedMilliseconds - lastMenu < 2000) continue;
            lastMenu = clock.ElapsedMilliseconds;
            var hwnd = Uia.FindWindow("Counter-Strike");
            if (hwnd == IntPtr.Zero || !Hands.InFront(Hands.PidOf(hwnd))) continue;
            using var frame = CsMenu.Grab(hwnd);
            if (frame is null) continue;
            var s = await CsMenu.ReadAsync(frame);
            sh.Menu = s.Kind;
            Interlocked.Exchange(ref sh.MenuAt, clock.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// Её команда: из журнала игры (CsBridge), а пока журнал не отдал вход — из плана (за кого заходила сама). В первые
    /// секунды команда была пустой, и сервер палил по красным тиммейтам (29.09, отладочные кадры).
    /// </summary>
    private static string MyTeam(CsMenu.Plan? plan)
    {
        if (CsBridge.HostTeam is { Length: > 0 } t) return t;
        var p = plan?.Team.Trim().ToLowerInvariant() ?? "";
        return p.StartsWith("ct") || p.StartsWith("спец") || p.StartsWith("кт") ? "CT"
            : p.StartsWith('t') || p.StartsWith("терр") || p.StartsWith('т') ? "T" : "";
    }

    /// <summary>
    /// Цвет надписи под прицелом, без букв: игра пишет её цветом команды того, на кого навели (красная — террорист,
    /// синяя — спецназ) в тонкой полосе внизу слева (~94 % высоты кадра). Одежду (красные штаны тиммейта в том же углу)
    /// отличает то, что она вылезает за полосу, а текст — нет. null — надписи нет.
    /// </summary>
    internal static string? LabelTeam(Bitmap label)
    {
        var d = label.LockBits(new Rectangle(0, 0, label.Width, label.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            int w = label.Width, h = label.Height, x1 = (int)(w * 0.55);
            int bandTop = (int)(h * 0.66), bandBot = (int)(h * 0.97), outBot = (int)(h * 0.6);
            long rb = 0, bb = 0, ro = 0, bo = 0;
            var row = new byte[d.Stride];
            for (var y = 0; y < h; y++)
            {
                if (y >= outBot && y < bandTop) continue;
                Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, d.Stride);
                var band = y >= bandTop && y < bandBot;
                for (var x = 0; x < x1; x++)
                {
                    int b = row[x * 3], g = row[x * 3 + 1], r = row[x * 3 + 2];
                    var red = r - Math.Max(g, b) > 55;
                    var blue = b - Math.Max(r, g) > 55;
                    if (band) { if (red) rb++; if (blue) bb++; }
                    else { if (red) ro++; if (blue) bo++; }
                }
            }
            var min = Math.Max(40L, (long)x1 * (bandBot - bandTop) / 250);
            if (rb >= min && ro * 3 < rb && rb > 2 * bb) return "T";
            if (bb >= min && bo * 3 < bb && bb > 2 * rb) return "CT";
            return null;
        }
        finally { label.UnlockBits(d); }
    }

    /// <summary>
    /// Надпись под прицелом (красная у террористов, синяя у спецназа) — в чёрное на белом, иначе OCR на песке её теряет.
    /// Мерило — «краснина» R − max(G,B) и «синева» B − max(R,G) после увеличения ×3: песок (200,170,120) даёт 30 и не
    /// проходит, края тонких букв проходят (прежний порог «яркое и насыщенное» рвал буквы — tag не читался ни разу, 29.09).
    /// </summary>
    internal static Bitmap Ink(Bitmap src)
    {
        var dst = new Bitmap(src.Width * 3, src.Height * 3, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        }
        var d = dst.LockBits(new Rectangle(0, 0, dst.Width, dst.Height), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[d.Stride];
            for (var y = 0; y < dst.Height; y++)
            {
                Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, d.Stride);
                for (var x = 0; x < dst.Width; x++)
                {
                    int b = row[x * 3], gg = row[x * 3 + 1], r = row[x * 3 + 2];
                    var ink = r - Math.Max(gg, b) > 55 || b - Math.Max(r, gg) > 55;
                    row[x * 3] = row[x * 3 + 1] = row[x * 3 + 2] = (byte)(ink ? 0 : 255);
                }
                Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, d.Stride);
            }
        }
        finally { dst.UnlockBits(d); }
        return dst;
    }

    /// <summary>Закуп в начале раунда: B → A («автоматическая покупка»: броня, ствол, патроны на что хватит); меню осталось — 0.</summary>
    /// <summary>
    /// Закуп цифрами меню, как живой игрок (порядок — из меню сборки друга, BuyRifles_TER/CT.res, BuyEquipment_*.res):
    /// B → 4 винтовки → 2 AK-47 (T) / 3 M4A1 (CT); B → 8 снаряжение → 2 жилет с каской; «,» — патроны к основному;
    /// 1 — взять автомат. «A» в меню ничего не покупала (меню висело, 28.09) — и она бегала с Glock на $16000.
    /// Меню осталось открытым (денег нет, время закупа вышло) — 0.
    /// </summary>
    private static async Task BuyAsync(IntPtr hwnd, Shared sh, string team, CancellationToken ct)
    {
        await Task.Delay(250, ct);
        var rifle = team == "CT" ? '3' : '2';
        const char comma = (char)0xBC;   // VK_OEM_COMMA — «,» (buyammo1); символ ',' дал бы 0x2C — это PrintScreen
        foreach (var (key, pause) in new[] { ('B', 350), ('4', 300), (rifle, 400), ('B', 350), ('8', 300), ('2', 400), (comma, 150), (comma, 150), ('1', 300) })
        {
            if (!await CsMenu.TapAsync(hwnd, key, ct)) return;
            await Task.Delay(pause, ct);
        }
        using var frame = CsMenu.Grab(hwnd);
        if (frame is null) return;
        var top = new Rectangle(0, 0, frame.Width, frame.Height / 4);
        var lines = await CsMenu.ReadLinesAsync(frame, top);
        if (lines.Any(l => l.Norm.Contains("меню покупки", StringComparison.Ordinal) || l.Norm.Contains("покупк", StringComparison.Ordinal)))
        {
            await CsMenu.TapAsync(hwnd, '0', ct);
            Log.Write("арена: закуп — меню осталось (время закупа вышло?), закрыла (0)");
        }
        else Log.Write($"арена: закупилась ({(team == "CT" ? "M4A1" : "AK-47")}, жилет с каской, патроны)");
        sh.Menu = CsMenu.Kind.InGame;
    }

    private static async Task SendFrameAsync(NetworkStream s, byte[] meta, byte[] big, byte[] mid, byte[] small, CancellationToken ct)
    {
        var head = new byte[16];
        BitConverter.TryWriteBytes(head.AsSpan(0, 4), (uint)meta.Length);
        BitConverter.TryWriteBytes(head.AsSpan(4, 4), (uint)big.Length);
        BitConverter.TryWriteBytes(head.AsSpan(8, 4), (uint)mid.Length);
        BitConverter.TryWriteBytes(head.AsSpan(12, 4), (uint)small.Length);
        await s.WriteAsync(head, ct);
        await s.WriteAsync(meta, ct);
        await s.WriteAsync(big, ct);
        await s.WriteAsync(mid, ct);
        await s.WriteAsync(small, ct);
    }

    /// <summary>Сброс (новый раунд, пауза): пакет с нулевой длиной JSON; сервер отвечает «{}».</summary>
    private static async Task SendResetAsync(NetworkStream s, StreamReader reader, CancellationToken ct)
    {
        await s.WriteAsync(new byte[16], ct);
        await reader.ReadLineAsync(ct);
    }

    private static void Button(Hands paws, uint pid, MouseButtons b, bool wantDown, ref bool down, ref long shots)
    {
        if (wantDown && !down)
        {
            if (paws.PadMouseDown(pid, b, 3000)) { down = true; if (b == MouseButtons.Left) shots++; }
        }
        else if (!wantDown && down)
        {
            paws.PadMouseUp(b);
            down = false;
        }
        else if (down) paws.PadMouseDown(pid, b, 3000);   // продлить
    }

    private static void ReleaseAll(Hands paws, HashSet<ushort> held, ref bool lmb, ref bool rmb)
    {
        paws.PadReleaseAll("пауза арены");
        held.Clear();
        lmb = rmb = false;
    }

    private static void MoveMouse(int dx, int dy)
    {
        var input = new[] { new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { dx = dx, dy = dy, dwFlags = Native.MOUSEEVENTF_MOVE } } } };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }

    private static ushort? VkOf(string name) => name switch
    {
        { Length: 1 } when char.IsAsciiLetterOrDigit(name[0]) => char.ToUpperInvariant(name[0]),
        "SPACE" => 0x20, "SHIFT" => 0x10, "CTRL" => 0x11, "ALT" => 0x12,
        "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28, "TILDE" => 0xC0,
        ['F', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 12 => (ushort)(0x6F + f),
        _ => null,
    };

    /// <summary>
    /// KlavaDesk.exe --arena-test — сквозная проверка сервера и протокола 4 без живой игры: сервер на порту 47315,
    /// синтетический кадр с силуэтом, ответ с полями, «мёртв» → yield, «свой» по надписи, сброс раунда.
    /// </summary>
    public static int SelfTest(KlavaConfig cfg)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var failed = 0;
        void Check(bool ok, string what) { if (!ok) failed++; Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {what}"); }

        const int testPort = 47315;
        var gamerDir = cfg.Abs("gamer");
        var python = Path.Combine(gamerDir, ".venv", "Scripts", "python.exe");
        var script = Path.Combine(gamerDir, "arena", "server.py");
        var detector = Path.Combine(gamerDir, "models", "detect", "yolo11n-pose.pt");
        Check(File.Exists(python), $"python окружения: {python}");
        Check(File.Exists(script), $"скрипт Арены: {script}");
        Check(File.Exists(detector), $"веса YOLO11: {detector}");
        if (failed > 0) return 1;

        Console.WriteLine("Запуск сервера Арены на тестовом порту...");
        var err = StartServerAsync(python, script, "TestShooter", null, detector, testPort, cfg.Abs(@"data\logs\arena_server_test.log")).GetAwaiter().GetResult();
        Check(err is null, $"сервер Арены поднялся{(err is null ? "" : ": " + err)}");
        if (err is not null) return 1;
        try
        {
            using var tcp = new TcpClient("127.0.0.1", testPort);
            var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            byte[] Jpg(int w, int h, Action<Graphics> draw)
            {
                using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.FromArgb(50, 50, 50)); draw(g); }
                return Encode(bmp, new Rectangle(0, 0, w, h), w, h);
            }
            var big = Jpg(BigWidth, BigHeight, g => { g.FillEllipse(Brushes.PeachPuff, 310, 100, 20, 25); g.FillRectangle(Brushes.Red, 300, 125, 40, 70); });
            var mid = Jpg(BigWidth, BigHeight, g => g.FillRectangle(Brushes.Red, 300, 125, 40, 70));
            var small = Jpg(SmallWidth, SmallHeight, g => g.FillRectangle(Brushes.DarkRed, 120, 50, 16, 28));
            JsonObject? Ask(string meta)
            {
                SendFrameAsync(stream, Encoding.UTF8.GetBytes(meta), big, mid, small, CancellationToken.None).GetAwaiter().GetResult();
                var line = reader.ReadLine();
                Console.WriteLine($"  {meta} → {line}");
                return line is null ? null : JsonNode.Parse(line) as JsonObject;
            }
            // калибровку мыши для TestShooter сервер делает сам (4 шага) — прогнать её
            for (var i = 0; i < 24; i++) Ask("{\"t\":100,\"user\":false,\"event\":null,\"hold\":false,\"team\":\"CT\"}");
            var o = Ask("{\"t\":900,\"user\":false,\"event\":null,\"hold\":false,\"team\":\"CT\"}");
            Check(o is not null && o.ContainsKey("dx") && o.ContainsKey("dy") && o.ContainsKey("lmb"), "ответ на кадр: dx, dy, lmb, why");
            var dead = Ask("{\"t\":950,\"user\":false,\"event\":\"death\",\"hold\":false,\"team\":\"CT\",\"dead\":true}");
            Check(dead?["yield"]?.GetValue<bool>() == true, "убита — сервер ничего не жмёт (yield)");
            var friend = Ask("{\"t\":1000,\"user\":false,\"event\":null,\"hold\":false,\"team\":\"T\",\"tag\":\"friend\"}");
            Check(friend is not null && (friend["lmb"]?.GetValue<int>() ?? 0) == 0, "свой (красная форма, своя команда T, надпись «Товарищ») — не стреляет");
            SendResetAsync(stream, reader, CancellationToken.None).GetAwaiter().GetResult();
            Check(true, "сброс раунда отправлен и подтверждён");
        }
        finally
        {
            StopServer();
            Console.WriteLine(failed == 0 ? "Арена: самотест пройден успешно" : $"Арена: ошибок {failed}");
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// KlavaDesk.exe --arena-live [секунд] [new] [карта] [команда] — живая игра в CS без Клавы: меню (new — создать игру
    /// с ботами), закуп, бой. Заберёт мышь и клаву; Backspace/Esc — стоп.
    /// </summary>
    public static int LiveTest(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\arena_live.log"));
        var sec = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 90;
        var newGame = args.Length > 1 && args[1] == "new";
        var plan = new CsMenu.Plan(args.Length > 2 ? args[2] : "de_dust2", 9, "средняя", args.Length > 3 ? args[3] : "t");
        Environment.SetEnvironmentVariable("KLAVA_ARENA_DEBUG", cfg.Abs(Path.Combine("data", "logs", "arena_debug")));
        var done = new ManualResetEventSlim();
        string result = "";
        Finished += r => { result = r; done.Set(); };
        Console.WriteLine(StartAsync(cfg, "Counter-Strike", "Counter-Strike", sec, null, plan, newGame).GetAwaiter().GetResult());
        if (!Playing && !done.IsSet) return 1;   // не запустилась (нет окна игры, нет сервера) — причина уже напечатана
        while (!done.Wait(3000)) Console.WriteLine($"  {Status()}");
        Console.WriteLine($"ИТОГ: {result}");
        return 0;
    }
}
