using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Клава играет по выученному: нейронка (gamer\train\serve.py, обучена на игре друга) 16 раз в секунду получает кадр
/// игры — тот же GameFrames.Grab, что и при записи, — и отвечает, какие клавиши держать, куда повернуть мышь и стрелять
/// ли. Жмёт сама Клава — её игровыми лапками (Hands, скан-коды, относительная мышь): никаких инъекций в игру.
/// Защита: окно игры не впереди — пауза, всё отпущено; друг нажал Backspace или Esc — стоп, управление
/// его. В фоне, разговор свободен; итог — событием Finished.
/// </summary>
internal static class GamePlayer
{
    public const int Port = 47300;
    private const double Press = 0.5, Release = 0.35;   // зажать уверенно, отпустить с запасом — клавиши не дребезжат

    private static CancellationTokenSource? _cts;
    private static Process? _server;
    private static volatile string _status = "не играю";
    private static volatile string? _takeover;

    public static event Action<string>? Finished;

    public static bool Playing => _cts is not null;
    public static string Status() => _status;

    /// <summary>Начать: окно игры (часть заголовка), папка модели (gamer\models\&lt;игра&gt;), python из gamer\.venv.</summary>
    public static async Task<string> StartAsync(string gamerDir, string window, string game, int seconds)
    {
        if (_cts is not null) return $"Уже играю: {_status}";
        var hwnd = Uia.FindWindow(window);
        if (hwnd == IntPtr.Zero || string.IsNullOrWhiteSpace(window)) return $"Окна игры «{window}» не нашла — list_windows покажет, как оно называется.";
        var pid = Hands.PidOf(hwnd);
        if (pid == Environment.ProcessId) return "Впереди окно самой Клавы — укажи окно игры.";
        if (Hands.BlockedByElevation(pid))
            return "Не удалось: игра запущена от имени администратора, а я — нет: Windows не пропустит нажатия. Сними у игры «Запуск от имени администратора».";
        var model = Path.Combine(gamerDir, "models", game);
        if (!File.Exists(Path.Combine(model, "policy.pt")))
            return $"Нейронки для «{game}» ещё нет ({model}): сначала запиши, как друг играет (record_start), потом обучи (train).";
        var python = Path.Combine(gamerDir, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python)) return $"Нет окружения нейронки: {python}.";

        _status = "поднимаю нейронку";
        var ready = await StartServerAsync(python, Path.Combine(gamerDir, "train", "serve.py"), model);
        if (ready is not null) return $"Не удалось поднять нейронку: {ready}";

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var limit = TimeSpan.FromSeconds(Math.Clamp(seconds <= 0 ? 600 : seconds, 30, 7200));
        GameMode.Enter("gamer");
        _ = Task.Run(async () =>
        {
            string result;
            try { result = await PlayAsync(hwnd, pid, limit, token); }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidOperationException or ObjectDisposedException) { result = $"сломалось: {ex.Message}"; }
            finally { GameMode.Leave("gamer"); }
            StopServer();
            _cts?.Dispose();
            _cts = null;
            _status = $"не играю (прошлый раз: {result})";
            Log.Write($"игрок: {result}");
            Finished?.Invoke(result);
        }, CancellationToken.None);
        return $"Играю по выученному в «{Uia.WindowTitle(hwnd)}»: нейронка смотрит на кадры и жмёт клаву с мышью. Стоп — Backspace (или Esc), сказать «стоп».";
    }

    public static string Stop()
    {
        if (_cts is null) return "И так не играю.";
        _cts.Cancel();
        return "Остановилась.";
    }

    private static async Task<string?> StartServerAsync(string python, string script, string model)
    {
        StopServer();
        var psi = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(script)!,
        };
        foreach (var a in new[] { "-u", script, "--model", model, "--port", Port.ToString(CultureInfo.InvariantCulture) }) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        _server = Process.Start(psi);
        if (_server is null) return "python не запустился";
        try { Native.TieToLifetime(_server); } catch (InvalidOperationException) { }
        var err = new StringBuilder();
        _server.ErrorDataReceived += (_, e) => { if (e.Data is { } l) lock (err) err.AppendLine(l); };
        _server.BeginErrorReadLine();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(90))
        {
            var line = await _server.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(90) - clock.Elapsed).ConfigureAwait(false);
            if (line is null) break;
            if (line.StartsWith("READY", StringComparison.Ordinal)) { Log.Write($"игрок: нейронка готова — {line}"); return null; }
        }
        string tail;
        lock (err) tail = err.ToString();
        StopServer();
        return tail.Length > 0 ? tail[Math.Max(0, tail.Length - 400)..] : "нейронка не ответила READY за 90 с";
    }

    private static void StopServer()
    {
        try { if (_server is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _server?.Dispose();
        _server = null;
    }

    private static async Task<string> PlayAsync(IntPtr hwnd, uint pid, TimeSpan limit, CancellationToken ct)
    {
        // без Control: созданный в фоновом потоке, он ставит WinForms-контекст синхронизации, а очереди сообщений тут
        // нет — первый же await не возвращался, и игрок молча висел
        var paws = new Hands(null) { Standalone = true };
        _takeover = null;
        using var watch = RawInput.Subscribe(e =>
        {
            // живой друг забирает управление кнопкой — Backspace или Esc (мышь можно задеть случайно, её не слушаем)
            if (!e.Mouse && !e.KeyUp && e.Vk is 0x08 or 0x1B) _takeover ??= e.Vk == 0x08 ? "друг нажал Backspace" : "друг нажал Esc";
        });
        using var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", Port, ct);
        tcp.NoDelay = true;
        var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        var held = new HashSet<ushort>();
        bool lmb = false, rmb = false, paused = false;
        long frames = 0, pressed = 0;
        var clock = Stopwatch.StartNew();
        var next = 0.0;
        const double step = 1000.0 / GameRecorder.Fps;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (clock.Elapsed > limit) return $"время вышло. Кадров {frames}, нажатий {pressed}";
                if (_takeover is { } who) return $"{who} — остановилась. Кадров {frames}, нажатий {pressed}";
                next += step;
                var wait = next - clock.Elapsed.TotalMilliseconds;
                if (wait > 1) await Task.Delay((int)wait, ct);
                else if (wait < -step * 4) next = clock.Elapsed.TotalMilliseconds;

                if (!Hands.InFront(pid))
                {
                    if (!paused) { ReleaseAll(paws, held, ref lmb, ref rmb); await SendAsync(stream, null, ct); paused = true; _status = "пауза: игра не впереди"; }
                    if (!Native.IsWindow(hwnd)) return $"окно игры закрылось. Кадров {frames}, нажатий {pressed}";
                    continue;
                }
                paused = false;
                var jpeg = GameFrames.Grab(hwnd);
                if (jpeg is null) continue;
                await SendAsync(stream, jpeg, ct);
                var line = await reader.ReadLineAsync(ct);
                if (line is null) return $"нейронка отвалилась. Кадров {frames}";
                frames++;
                if (JsonNode.Parse(line) is not JsonObject o || o["keys"] is not JsonObject keys) continue;

                foreach (var (name, v) in keys)
                {
                    if (VkOf(name) is not { } vk || v is null) continue;
                    var p = v.GetValue<double>();
                    if (p >= Press && !held.Contains(vk)) { if (paws.PadDown(pid, [vk], 3000)) { held.Add(vk); pressed++; } }
                    else if (p < Release && held.Remove(vk)) paws.PadUp([vk]);
                }
                // держим зажатое дальше: сторож лапок отпустит всё сам, если перестанем продлевать (завис python)
                if (held.Count > 0) paws.PadDown(pid, [.. held], 3000);
                Button(paws, pid, MouseButtons.Left, o["lmb"]?.GetValue<double>() ?? 0, ref lmb, ref pressed);
                Button(paws, pid, MouseButtons.Right, o["rmb"]?.GetValue<double>() ?? 0, ref rmb, ref pressed);
                var dx = (int)Math.Round(o["dx"]?.GetValue<double>() ?? 0);
                var dy = (int)Math.Round(o["dy"]?.GetValue<double>() ?? 0);
                if (dx != 0 || dy != 0) MoveMouse(dx, dy);
                if (frames % GameRecorder.Fps == 0)
                    _status = $"играю по выученному: {frames / GameRecorder.Fps} с, держу {string.Join("+", held.Select(k => ((char)k).ToString()))}{(lmb ? " +огонь" : "")}";
            }
            return $"остановили. Кадров {frames}, нажатий {pressed}";
        }
        finally
        {
            ReleaseAll(paws, held, ref lmb, ref rmb);
            paws.PadReleaseAll("игрок закончил");
        }
    }

    private static void Button(Hands paws, uint pid, MouseButtons b, double p, ref bool down, ref long pressed)
    {
        if (p >= Press && !down) { if (paws.PadMouseDown(pid, b, 3000)) { down = true; pressed++; } }
        else if (p < Release && down) { paws.PadMouseUp(b); down = false; }
        else if (down) paws.PadMouseDown(pid, b, 3000);   // продлить срок
    }

    private static void ReleaseAll(Hands paws, HashSet<ushort> held, ref bool lmb, ref bool rmb)
    {
        paws.PadReleaseAll("пауза игрока");
        held.Clear();
        lmb = rmb = false;
    }

    private static async Task SendAsync(NetworkStream s, byte[]? jpeg, CancellationToken ct)
    {
        var head = BitConverter.GetBytes(jpeg?.Length ?? 0);
        await s.WriteAsync(head, ct);
        if (jpeg is not null) await s.WriteAsync(jpeg, ct);
        else
        {
            // сброс истории: сервер отвечает «{}» — прочитать, чтобы ответы не съехали на кадр
            var one = new byte[1];
            while (await s.ReadAsync(one, ct) == 1 && one[0] != (byte)'\n') { }
        }
    }

    /// <summary>Поворот мышью: относительное движение, как у настоящей мыши (шутеры читают его через Raw Input).</summary>
    private static void MoveMouse(int dx, int dy)
    {
        var input = new[] { new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { dx = dx, dy = dy, dwFlags = Native.MOUSEEVENTF_MOVE } } } };
        Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
    }

    /// <summary>
    /// KlavaDesk.exe --gamer-test — сквозная проверка без живой игры. Окно-«игра» в отдельном процессе (powershell)
    /// показывает сцену «квадрат слева / справа / пусто» и пишет в файл, какие клавиши получило. 1) запись: кадры идут,
    /// а нажатия самой Клавы (SendInput) в запись не попадают; 2) игра по выученному (нейронка klava_synthetic из
    /// gamer\train\selftest.py): слева жмёт A, справа — D, не наоборот.
    /// </summary>
    public static int SelfTest(string gamerDir)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ApplicationConfiguration.Initialize();
        var failed = 0;
        void Check(bool ok, string what) { if (!ok) failed++; Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {what}"); }
        var log = Path.Combine(Path.GetTempPath(), $"klava_gamer_test_{Environment.ProcessId}.txt");
        File.Delete(log);
        const string title = "Klava gamer test";
        var form =
            "Add-Type -AssemblyName System.Windows.Forms, System.Drawing; " +
            "$f = New-Object Windows.Forms.Form; $f.Text = '" + title + "'; $f.ClientSize = New-Object Drawing.Size(768, 432); " +
            "$f.StartPosition = 'Manual'; $f.Location = New-Object Drawing.Point(60, 60); $f.TopMost = $true; $f.KeyPreview = $true; " +
            "$f.FormBorderStyle = 'FixedSingle'; $f.BackColor = [Drawing.Color]::FromArgb(128, 128, 128); " +
            "$script:scene = 'none'; $sw = [Diagnostics.Stopwatch]::StartNew(); $red = New-Object Drawing.SolidBrush([Drawing.Color]::FromArgb(220, 30, 30)); " +
            "$f.Add_Paint({ param($s, $e) if ($script:scene -eq 'left') { $e.Graphics.FillRectangle($red, 96, 144, 144, 144) } elseif ($script:scene -eq 'right') { $e.Graphics.FillRectangle($red, 528, 144, 144, 144) } }); " +
            "$t = New-Object Windows.Forms.Timer; $t.Interval = 50; " +
            "$t.Add_Tick({ $ms = $sw.ElapsedMilliseconds % 6500; $n = if ($ms -lt 2500) { 'left' } elseif ($ms -lt 5000) { 'right' } else { 'none' }; " +
            "  if ($n -ne $script:scene) { $script:scene = $n; $f.Invalidate(); Add-Content -LiteralPath '" + log + "' -Value \"scene $n $($sw.ElapsedMilliseconds)\" } }); $t.Start(); " +
            "$f.Add_KeyDown({ param($s, $e) Add-Content -LiteralPath '" + log + "' -Value \"down $($e.KeyCode) $($script:scene) $($sw.ElapsedMilliseconds)\" }); " +
            "$f.Add_KeyUp({ param($s, $e) Add-Content -LiteralPath '" + log + "' -Value \"up $($e.KeyCode) $($script:scene) $($sw.ElapsedMilliseconds)\" }); " +
            "[void]$f.ShowDialog()";
        using var game = Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "-NoProfile", "-Command", form } });
        var mouse = Cursor.Position;
        try
        {
            var hwnd = IntPtr.Zero;
            for (var i = 0; i < 100 && hwnd == IntPtr.Zero; i++) { Thread.Sleep(100); hwnd = Uia.FindWindow(title); }
            Check(hwnd != IntPtr.Zero, "окно-«игра» поднялось");
            if (hwnd == IntPtr.Zero) return 1;
            Uia.Focus(hwnd);
            Thread.Sleep(500);

            // 1) запись: кадры есть, а свои (SendInput) нажатия не пишутся
            var root = Path.Combine(Path.GetTempPath(), $"klava_gamer_rec_{Environment.ProcessId}");
            Console.WriteLine(GameRecorder.Start(root, title));
            Thread.Sleep(600);
            Hands.GameKey((ushort)'W', true);
            Thread.Sleep(300);
            Hands.GameKey((ushort)'W', false);
            Thread.Sleep(1400);
            Console.WriteLine(GameRecorder.Stop());
            var csv = Directory.Exists(root) ? Directory.GetFiles(root, "actions.csv", SearchOption.AllDirectories).FirstOrDefault() : null;
            var lines = csv is null ? [] : File.ReadAllLines(csv).Skip(1).ToArray();
            var jpgs = csv is null ? 0 : Directory.GetFiles(Path.GetDirectoryName(csv)!, "*.jpg").Length;
            Check(lines.Length >= 20 && jpgs == lines.Length, $"запись: {lines.Length} строк, {jpgs} кадров за ~2,3 с (16 в секунду)");
            Check(lines.All(l => l.Split(',')[3].Length == 0), "свои нажатия (W через SendInput) в запись не попали — пишется только живой друг");
            Check(File.Exists(log) && File.ReadAllText(log).Contains("down W"), "а до окна игры W дошла");
            try { Directory.Delete(root, true); } catch (IOException) { }

            // 2) игра по выученному
            if (!File.Exists(Path.Combine(gamerDir, "models", "klava_synthetic", "policy.pt")))
            {
                Check(false, "нет нейронки klava_synthetic — сначала gamer\\train\\selftest.py");
                return 1;
            }
            // 2а) глаза + нейронка без лапок: снимок окна тем же Grab → ответ; сцену читаем прямо с кадра
            var model = Path.Combine(gamerDir, "models", "klava_synthetic");
            var python = Path.Combine(gamerDir, ".venv", "Scripts", "python.exe");
            var err = StartServerAsync(python, Path.Combine(gamerDir, "train", "serve.py"), model).GetAwaiter().GetResult();
            Check(err is null, $"нейронка поднялась{(err is null ? "" : ": " + err)}");
            if (err is not null) return 1;
            int probeOk = 0, probeAll = 0;
            using (var tcp = new TcpClient("127.0.0.1", Port))
            {
                var s = tcp.GetStream();
                using var rd = new StreamReader(s, Encoding.UTF8, false, 4096, leaveOpen: true);
                var probeClock = Stopwatch.StartNew();
                while (probeClock.ElapsedMilliseconds < 7000)
                {
                    var jpeg = GameFrames.Grab(hwnd);
                    if (jpeg is null) continue;
                    string scene;
                    using (var bmp = new Bitmap(new MemoryStream(jpeg)))
                        scene = bmp.GetPixel(56, 72).R > 180 ? "left" : bmp.GetPixel(200, 72).R > 180 ? "right" : "none";
                    s.Write(BitConverter.GetBytes(jpeg.Length));
                    s.Write(jpeg);
                    var o = JsonNode.Parse(rd.ReadLine() ?? "{}") as JsonObject;
                    double a = o?["keys"]?["A"]?.GetValue<double>() ?? 0, d = o?["keys"]?["D"]?.GetValue<double>() ?? 0;
                    var want = scene == "left" ? (a > 0.5 && d < 0.5) : scene == "right" ? (d > 0.5 && a < 0.5) : (a < 0.5 && d < 0.5);
                    probeAll++;
                    if (want) probeOk++;
                    Thread.Sleep(60);
                }
            }
            StopServer();
            Console.WriteLine($"глаза+нейронка: верных ответов {probeOk} из {probeAll} (сразу после смены сцены нейронке нужна пара кадров)");
            Check(probeAll > 30 && probeOk >= probeAll * 0.75, "по снимку окна нейронка отвечает верно");

            // 2б) целиком: нейронка смотрит, лапки жмут; окно пишет нажатия и отпускания
            File.Delete(log);
            var start = StartAsync(gamerDir, title, "klava_synthetic", 60).GetAwaiter().GetResult();
            Console.WriteLine(start);
            Check(Playing, "игрок запустился");
            Thread.Sleep(13000);
            Console.WriteLine(Stop());
            for (var i = 0; i < 50 && Playing; i++) Thread.Sleep(100);
            var events = File.Exists(log) ? File.ReadAllLines(log) : [];
            // по времени: сколько миллисекунд A и D были зажаты в своей сцене и в чужой (первые 0,5 с сцены — реакция, не судим)
            var scenes = new List<(long T, string Scene)>();
            var downAt = new Dictionary<string, long>();
            var spans = new List<(string Key, long From, long To)>();
            long last = 0;
            foreach (var e in events)
            {
                var p = e.Split(' ');
                if (p.Length < 3) continue;
                var t = long.Parse(p[^1], CultureInfo.InvariantCulture);
                last = Math.Max(last, t);
                if (p[0] == "scene") scenes.Add((t, p[1]));
                else if (p[0] == "down") downAt.TryAdd(p[1], t);
                else if (p[0] == "up" && downAt.Remove(p[1], out var from)) spans.Add((p[1], from, t));
            }
            foreach (var (k, from) in downAt) spans.Add((k, from, last));
            long good = 0, bad = 0;
            foreach (var (k, from, to) in spans.Where(s => s.Key is "A" or "D"))
                for (var t = from; t < to; t += 20)
                {
                    var cur = scenes.LastOrDefault(s => s.T <= t);
                    if (cur.Scene is null || t - cur.T < 500) continue;
                    if ((k == "A" && cur.Scene == "left") || (k == "D" && cur.Scene == "right")) good += 20; else bad += 20;
                }
            Console.WriteLine($"зажато верно {good} мс, невпопад {bad} мс; нажатий {spans.Count}; {Status()}");
            Check(good >= 1500, "нейронка держала A на «слева» и D на «справа»");
            Check(bad <= good / 4, "невпопад почти не держит");
            Check((Native.GetAsyncKeyState('A') & 0x8000) == 0 && (Native.GetAsyncKeyState('D') & 0x8000) == 0, "после стопа ничего не зажато");
        }
        finally
        {
            try { game?.Kill(); } catch (InvalidOperationException) { }
            Cursor.Position = mouse;
            File.Delete(log);
            Console.WriteLine(failed == 0 ? "учёба в играх: всё верно" : $"учёба в играх: не прошло {failed}");
        }
        return failed == 0 ? 0 : 1;
    }

    /// <summary>Имя клавиши из записи (GameRecorder.KeyName) → виртуальный код.</summary>
    private static ushort? VkOf(string name) => name switch
    {
        { Length: 1 } when char.IsAsciiLetterOrDigit(name[0]) => char.ToUpperInvariant(name[0]),
        "SPACE" => 0x20, "SHIFT" => 0x10, "CTRL" => 0x11, "ALT" => 0x12,
        "LEFT" => 0x25, "UP" => 0x26, "RIGHT" => 0x27, "DOWN" => 0x28, "TILDE" => 0xC0,
        ['F', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 12 => (ushort)(0x6F + f),
        _ => null,
    };
}

internal static partial class Native
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(IntPtr hWnd);
}
