using System.Diagnostics;

namespace KlavaDesk;

/// <summary>Counter-Strike 1.6: Клава — встроенный бот CS с ником «Klava|…», события и команды — через CsBridge.</summary>
internal static partial class Tools
{
    /// <summary>Когда Клава последний раз играла лапками в CS (game_action по hl.exe) — для подсказок о смерти и раунде.</summary>
    public static DateTime LastPawsInCs { get; private set; } = DateTime.MinValue;

    private static string ProcessOf(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName.ToLowerInvariant(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return ""; }
    }

    private static int _unstuckSide = 1;

    /// <summary>
    /// Ждать, пока лапки держат клавиши, — с рефлексом «упёрлась»: если зажато движение (W/A/S/D), а картинка игры
    /// ~0,6 с стоит на месте, значит, она бежит в стену. Тогда сама, той же клавиатурой и мышью: отпустить, шаг назад,
    /// повернуть (по очереди вправо/влево), снова зажать то, что держала. Мозгу на такое нужны секунды — боты убивали,
    /// пока она тыкалась носом в стену. Возвращает, сколько раз пришлось отходить.
    /// </summary>
    private static async Task<int> WaitWatchingAsync(Hands h, uint pid, IntPtr window, int ms)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        byte[]? prev = null;
        var still = 0;
        var fixes = 0;
        while (clock.ElapsedMilliseconds < ms)
        {
            await Task.Delay((int)Math.Min(150, ms - clock.ElapsedMilliseconds), h.Token);
            if (!h.PadMoving || !Hands.InFront(pid)) { prev = null; still = 0; continue; }
            var fp = Fingerprint(window);
            if (fp is null) continue;
            if (prev is not null) still = FingerprintDiff(prev, fp) < 2.0 ? still + 1 : 0;
            prev = fp;
            if (still < 4) continue;
            var held = h.PadKeys;
            h.PadUp(null);
            await h.GameHoldAsync([(ushort)'S'], 300, h.Token);
            await h.LookAsync(450 * _unstuckSide, 0, 200, h.Token);
            _unstuckSide = -_unstuckSide;
            h.PadDown(pid, held, 30000);
            fixes++;
            still = 0;
            prev = null;
        }
        return fixes;
    }

    /// <summary>
    /// Отпечаток кадра окна: серые 32×18 из середины окна (там прицел и дорога) — сравнить, двигается ли картинка.
    /// Края не берём: справа поверх игры стоит сама Клава и дышит — её анимация выдавала бы «движение».
    /// </summary>
    private static byte[]? Fingerprint(IntPtr window)
    {
        if (!Native.GetWindowRect(window, out var r) || r.Right - r.Left < 64 || r.Bottom - r.Top < 64) return null;
        int w = r.Right - r.Left, hgt = r.Bottom - r.Top;
        using var full = new Bitmap(w / 2, hgt * 6 / 10, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full)) g.CopyFromScreen(r.Left + w / 4, r.Top + hgt / 5, 0, 0, full.Size);
        using var small = new Bitmap(32, 18, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, 0, 0, 32, 18);
        }
        var fp = new byte[32 * 18];
        for (var y = 0; y < 18; y++)
            for (var x = 0; x < 32; x++)
            {
                var c = small.GetPixel(x, y);
                fp[y * 32 + x] = (byte)((c.R * 3 + c.G * 6 + c.B) / 10);
            }
        return fp;
    }

    private static double FingerprintDiff(byte[] a, byte[] b)
    {
        long sum = 0;
        for (var i = 0; i < a.Length; i++) sum += Math.Abs(a[i] - b[i]);
        return (double)sum / a.Length;
    }

    /// <summary>
    /// KlavaDesk.exe --paws-test — проверить игровые лапки без игры: своё окно-мишень ловит нажатия. 1) неподвижная
    /// картинка + зажата W → рефлекс «упёрлась» срабатывает, W и S доходят до окна; 2) картинка движется → рефлекса нет;
    /// 3) окно ушло с переднего плана → зажатое отпускается само; 4) после End ничего не зажато.
    /// </summary>
    public static int PawsTest()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        ApplicationConfiguration.Initialize();
        var moving = false;
        var phase = 0;
        using var target = new Form { Text = "Klava paws test", StartPosition = FormStartPosition.Manual, Bounds = new Rectangle(80, 120, 820, 520), KeyPreview = true, TopMost = true };
        target.Paint += (_, e) =>
        {
            using var br = new System.Drawing.Drawing2D.LinearGradientBrush(target.ClientRectangle, Color.FromArgb(40, 60, 90),
                Color.FromArgb(200, 170, 120), moving ? phase * 7 % 360 : 30f);
            e.Graphics.FillRectangle(br, target.ClientRectangle);
            for (var i = 0; i < 12; i++) e.Graphics.FillEllipse(Brushes.White, (i * 70 + (moving ? phase * 13 : 0)) % target.ClientSize.Width, 60 + i * 30, 40, 40);
        };
        var anim = new System.Windows.Forms.Timer { Interval = 40 };
        anim.Tick += (_, _) => { if (moving) { phase++; target.Invalidate(); } };
        anim.Start();
        var failed = 0;
        void Check(bool ok, string what)
        {
            if (!ok) failed++;
            Console.WriteLine($"{(ok ? "OK  " : "FAIL")} {what}");
        }
        target.Shown += async (_, _) =>
        {
            var h = new Hands(target);
            var mouse = Cursor.Position;
            try
            {
                await Task.Delay(400);
                target.Activate();
                await Task.Delay(300);
                var pid = (uint)Environment.ProcessId;
                h.Begin();
                Check(Hands.InFront(pid), "окно-мишень впереди");

                // 1) стена: картинка стоит, W зажата
                Check(h.PadDown(pid, [(ushort)'W'], 10000), "зажала W");
                await Task.Delay(100);
                Check((Native.GetAsyncKeyState('W') & 0x8000) != 0, "W в системе зажата");
                var fixes = await WaitWatchingAsync(h, pid, target.Handle, 2500);
                Check(fixes >= 1, $"неподвижная картинка → рефлекс «упёрлась» сработал ({fixes} раз)");
                Check(h.PadKeys.Contains('W'), "после отхода W снова зажата");

                // 2) бежит по открытому: картинка меняется — рефлекса нет
                moving = true;
                fixes = await WaitWatchingAsync(h, pid, target.Handle, 2000);
                Check(fixes == 0, $"картинка движется → рефлекса нет ({fixes})");
                moving = false;

                // 3) впереди окно другой программы — сторож отпускает всё (сравнивает по процессу игры)
                // окно другой программы пишет в файл коды клавиш, которые получило, — так проверяем доставку, как в игру
                var keyLog = Path.Combine(Path.GetTempPath(), $"klava_paws_keys_{Environment.ProcessId}.txt");
                File.Delete(keyLog);
                var formCmd = "Add-Type -AssemblyName System.Windows.Forms; $f = New-Object Windows.Forms.Form; " +
                    "$f.Text = 'Klava paws test - other'; $f.TopMost = $true; $f.KeyPreview = $true; " +
                    $"$f.Add_KeyDown({{ param($s, $e) Add-Content -LiteralPath '{keyLog}' -Value $e.KeyCode }}); [void]$f.ShowDialog()";
                using (var other = Process.Start(new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "-NoProfile", "-Command", formCmd } }))
                {
                    for (var i = 0; i < 60 && (other is null || other.MainWindowHandle == IntPtr.Zero); i++) { await Task.Delay(100); other?.Refresh(); }
                    if (other is not null && other.MainWindowHandle != IntPtr.Zero) Uia.Focus(other.MainWindowHandle);
                    await Task.Delay(600);
                    Check(!Hands.InFront(pid), "впереди окно другой программы");
                    Check(!h.PadMoving && h.PadHeld.Length == 0, "окно игры ушло → зажатое отпущено само");
                    Check((Native.GetAsyncKeyState('W') & 0x8000) == 0, "W в системе отпущена — в чужое окно не жмётся");
                    Check(!h.PadDown(pid, [(ushort)'W'], 1000), "зажать по окну игры, когда впереди чужое, — отказ");

                    // доставка в другую программу (как в игру): зажать W, нажать S, отпустить
                    var otherPid = other is null ? 0u : (uint)other.Id;
                    Check(h.PadDown(otherPid, [(ushort)'W'], 3000), "зажала W в окне другой программы");
                    await Task.Delay(200);
                    await h.GameHoldAsync([(ushort)'S'], 80, h.Token);
                    h.PadUp(null);
                    await Task.Delay(400);
                    var got = File.Exists(keyLog) ? File.ReadAllLines(keyLog) : [];
                    Check(got.Contains("W") && got.Contains("S"), $"нажатия дошли до другой программы: {string.Join(",", got.Distinct())}");
                    try { other?.Kill(); } catch (InvalidOperationException) { }
                    File.Delete(keyLog);
                }

                // 4) после End ничего не зажато
                target.Activate();
                await Task.Delay(300);
                h.PadDown(pid, [(ushort)'D'], 10000);
                h.End();
                Check(h.PadHeld.Length == 0, "договорила (End) → всё отпущено");
                Check((Native.GetAsyncKeyState('W') & 0x8000) == 0 && (Native.GetAsyncKeyState('D') & 0x8000) == 0, "в системе W и D не висят нажатыми");
            }
            catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
            {
                failed++;
                Console.WriteLine($"FAIL прервано: {ex.Message}");
            }
            finally
            {
                h.PadReleaseAll("конец теста");
                h.Dispose();
                Cursor.Position = mouse;
                Console.WriteLine(failed == 0 ? "лапки: всё верно" : $"лапки: не прошло {failed}");
                target.Close();
            }
        };
        Application.Run(target);
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// Учёба играть по игре друга: запись (кадры + его клава и мышь) → обучение нейронки (gamer\train\train.py, в фоне)
    /// → игра по выученному (GamePlayer: нейронка смотрит, Клава жмёт лапками).
    /// </summary>
    private static async Task<ToolResult> GamerAsync(string action, string window, string game, int seconds, KlavaConfig cfg,
        Func<ToolRequest, Task<bool>> approve, ToolBox? box)
    {
        var gamer = cfg.Abs("gamer");
        var data = Path.Combine(gamer, "data");
        switch (action.Trim().ToLowerInvariant())
        {
            case "record_start" or "record":
                if (!await ApproveOrAutoAsync(new ToolRequest("Записывать, как друг играет (учёба)",
                        "Пока впереди игра: 16 кадров в секунду окна игры и нажатия клавиатуры и мыши друга — в gamer\\data. " +
                        "Другие окна не пишутся. Остановить — сказать «хватит записывать».", false, null, "open"), approve, box))
                    return new ToolResult(Denied);
                return new ToolResult(GameRecorder.Start(data, window));
            case "record_stop":
                return new ToolResult(GameRecorder.Stop());
            case "train":
            {
                if (GamerTrainer.Busy) return new ToolResult($"Уже учусь на «{GamerTrainer.Game}». {TrainStatus(gamer, GamerTrainer.Game)}");
                if (GameRecorder.Recording) return new ToolResult("Сначала останови запись (record_stop) — учиться буду на готовой записи.");
                var g = string.IsNullOrWhiteSpace(game) ? RecordedGames(data).FirstOrDefault() ?? "" : game.Trim();
                if (g.Length == 0 || !Directory.Exists(Path.Combine(data, g)))
                    return new ToolResult($"Записей игры «{g}» нет. Есть: {string.Join(", ", RecordedGames(data))}.");
                if (GamerTrainer.Start(cfg, g, finetune: false) is { } err) return new ToolResult($"Не вышло: {err}.");
                return new ToolResult($"Учусь на записях «{g}» в фоне (видеокарта). Ход учёбы — gamer status, лог — {Path.Combine(gamer, "models", g, "train.log")}.");
            }
            case "play_start" or "play":
            {
                var g = string.IsNullOrWhiteSpace(game) ? TrainedGames(gamer).FirstOrDefault() ?? "" : game.Trim();
                if (box is not { HandsAllowed: true }) return new ToolResult("Мышь и клавиатуру в этом режиме не трогаю (проверка без рук).");
                if (!await ApproveOrAutoAsync(new ToolRequest("Играть по выученному",
                        $"Нейронка «{g}» смотрит на игру, Клава жмёт клавиатуру и мышь. Стоп: Backspace (или Esc), «стоп».",
                        false, null, "hands"), approve, box))
                    return new ToolResult(Denied);
                // окно не назвали — ищем по имени записи: это заголовок (Counter-Strike) или процесс (serioussam)
                return new ToolResult(await GamePlayer.StartAsync(gamer, string.IsNullOrWhiteSpace(window) ? g : window, g, seconds));
            }
            case "play_stop":
                return new ToolResult(GamePlayer.Stop());
            case "arena_start" or "arena":
                return await ArenaAsync("start", window, game, seconds, cfg, approve, box);
            case "arena_stop":
                return await ArenaAsync("stop", window, game, seconds, cfg, approve, box);
            case "arena_status":
                return await ArenaAsync("status", window, game, seconds, cfg, approve, box);
            case "list":
            {
                var rec = RecordedGames(data).Select(g => $"{g}: {RecordedMinutes(Path.Combine(data, g)):F0} мин записи").ToList();
                var models = TrainedGames(gamer).ToList();
                return new ToolResult($"Записи: {(rec.Count > 0 ? string.Join("; ", rec) : "нет")}. Выученные: {(models.Count > 0 ? string.Join(", ", models) : "нет")}.");
            }
            default:
            {
                var parts = new List<string> { $"Запись: {GameRecorder.Status()}", $"Игрок: {GamePlayer.Status()}", $"Арена: {ArenaPlayer.Status()}" };
                if (GamerTrainer.Game.Length > 0) parts.Add($"Учёба «{GamerTrainer.Game}»: {TrainStatus(gamer, GamerTrainer.Game)}{(GamerTrainer.Busy ? "" : " (процесс завершён)")}");
                return new ToolResult(string.Join(". ", parts) + ".");
            }
        }
    }

    /// <summary>
    /// Боевой режим Арены для шутеров (CS 1.6, Serious Sam…):
    /// Детектор YOLO11n-pose находит врагов и головы, прицел наводится плавно, Клава стреляет и бегает.
    /// </summary>
    internal static async Task<ToolResult> ArenaAsync(string action, string window, string game, int seconds, KlavaConfig cfg,
        Func<ToolRequest, Task<bool>> approve, ToolBox? box, string map = "", string team = "", int model = 0, int bots = 0, string difficulty = "")
    {
        var g = string.IsNullOrWhiteSpace(game) ? "Counter-Strike" : game.Trim();
        var win = string.IsNullOrWhiteSpace(window) ? g : window.Trim();
        var act = action.Trim().ToLowerInvariant();

        switch (act)
        {
            case "start" or "on" or "play":
            {
                if (box is not { HandsAllowed: true }) return new ToolResult("Мышь и клавиатуру в этом режиме не трогаю (проверка без рук).");
                var isCs = g.Contains("counter", StringComparison.OrdinalIgnoreCase) || win.Contains("counter", StringComparison.OrdinalIgnoreCase);
                var m = string.IsNullOrWhiteSpace(map) ? "de_dust2" : map.Trim().ToLowerInvariant();
                var plan = new CsMenu.Plan(m, bots > 0 ? Math.Clamp(bots, 1, 31) : 9,
                    string.IsNullOrWhiteSpace(difficulty) ? "средняя" : difficulty.Trim().ToLowerInvariant().Replace('ё', 'е'),
                    string.IsNullOrWhiteSpace(team) ? "auto" : team.Trim(), model is >= 1 and <= 5 ? model : 5);
                var newGame = false;
                if (isCs && act == "play")
                {
                    if (File.Exists(Path.Combine(cfg.CsDir, "cstrike", "maps", m + ".bsp")) is false)
                        return new ToolResult($"Карты {m} нет. Есть: {string.Join(", ", Directory.EnumerateFiles(Path.Combine(cfg.CsDir, "cstrike", "maps"), "de_*.bsp").Select(Path.GetFileNameWithoutExtension).Take(20))}");
                    if (!CsBridge.Running)
                    {
                        var exe = Path.Combine(cfg.CsDir, "hl.exe");
                        if (!File.Exists(exe)) return new ToolResult($"CS 1.6 не нашла: {exe}. Путь к игре — csDir в klava.json.");
                        if (!await ApproveOrAutoAsync(new ToolRequest("Запустить Counter-Strike 1.6", $"{exe} {CsBridge.MenuArgs}", false, null, "open"), approve, box))
                            return new ToolResult(Denied);
                        Process.Start(new ProcessStartInfo(exe, CsBridge.MenuArgs) { WorkingDirectory = cfg.CsDir, UseShellExecute = true })?.Dispose();
                        if (!await CsBridge.WaitWindowAsync(TimeSpan.FromSeconds(60)))
                            return new ToolResult("CS запускаю, но окна игры за минуту так и нет — повтори arena play, когда откроется.");
                        await Task.Delay(TimeSpan.FromSeconds(6));   // заставка RAZER → главное меню
                        newGame = true;
                    }
                    else
                    {
                        // уже открыта: в главном меню — создать игру; в игре — играть там, где друг
                        var h = Uia.FindWindow(win);
                        if (h != IntPtr.Zero && Uia.Focus(h))
                        {
                            await Task.Delay(500);
                            using var frame = CsMenu.Grab(h);
                            if (frame is not null) newGame = (await CsMenu.ReadAsync(frame)).Kind is CsMenu.Kind.MainMenu or CsMenu.Kind.CreateServer or CsMenu.Kind.MapList;
                        }
                    }
                }
                if (!await ApproveOrAutoAsync(new ToolRequest("Играть в шутер самой (Арена)",
                        $"Клава {(newGame ? $"создаёт игру ({m}, ботов {plan.Bots}) и " : "")}играет в «{win}»: нейронка ищет врагов, лапки жмут клаву и мышь. Стоп: Backspace (или Esc), «стоп».",
                        false, null, "hands"), approve, box))
                    return new ToolResult(Denied);
                return new ToolResult(await ArenaPlayer.StartAsync(cfg, win, g, seconds, null, isCs ? plan : null, newGame));
            }
            case "school" or "learn":
            {
                // школа у ботов: наблюдателем от первого лица смотрит, как играют боты, пишет — и учит на этом ноги
                if (box is not { HandsAllowed: true }) return new ToolResult("Мышь и клавиатуру в этом режиме не трогаю (проверка без рук).");
                var planS = new CsMenu.Plan(string.IsNullOrWhiteSpace(map) ? "de_dust2" : map.Trim().ToLowerInvariant(), 9, "средняя",
                    string.IsNullOrWhiteSpace(team) ? "t" : team.Trim(), model is >= 1 and <= 5 ? model : 5);
                if (!await ApproveOrAutoAsync(new ToolRequest("Школа у ботов (CS)",
                        "Клава уходит в наблюдатели и смотрит ботов от первого лица, записывает — потом учит на этом свои ноги. Стоп: Backspace (или Esc), «стоп».",
                        false, null, "hands"), approve, box))
                    return new ToolResult(Denied);
                return new ToolResult(BotSchool.Start(cfg, seconds, planS));
            }
            case "stop" or "off":
                return new ToolResult(BotSchool.Running ? BotSchool.Stop() : ArenaPlayer.Stop());
            default:
                return new ToolResult($"Арена: {ArenaPlayer.Status()}. Школа у ботов: {BotSchool.Status()}.");
        }
    }

    private static IEnumerable<string> RecordedGames(string data) =>
        Directory.Exists(data)
            ? Directory.EnumerateDirectories(data).Where(d => Directory.EnumerateFiles(d, "actions.csv", SearchOption.AllDirectories).Any())
                .OrderByDescending(Directory.GetLastWriteTime).Select(Path.GetFileName).OfType<string>()
            : [];

    private static IEnumerable<string> TrainedGames(string gamer)
    {
        var models = Path.Combine(gamer, "models");
        return Directory.Exists(models)
            ? Directory.EnumerateDirectories(models).Where(d => File.Exists(Path.Combine(d, "policy.pt")))
                .OrderByDescending(d => File.GetLastWriteTime(Path.Combine(d, "policy.pt"))).Select(Path.GetFileName).OfType<string>()
            : [];
    }

    private static double RecordedMinutes(string gameDir) =>
        Directory.EnumerateFiles(gameDir, "actions.csv", SearchOption.AllDirectories)
            .Sum(f => Math.Max(0, File.ReadLines(f).Count() - 1)) / (double)GameRecorder.Fps / 60;

    private static string TrainStatus(string gamer, string game)
    {
        var file = Path.Combine(gamer, "models", game, "status.json");
        if (!File.Exists(file)) return "ещё готовлюсь (читаю записи)";
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) is not System.Text.Json.Nodes.JsonObject s) return "статус не читается";
            string V(string k) => s[k]?.ToString() ?? "?";
            return V("state") switch
            {
                "training" => $"эпоха {V("epoch")}/{V("epochs")}, {V("progress")}%, осталось ~{V("eta_min")} мин, угадываю клавиши {V("key_acc")}",
                "done" => $"выучилась: {V("minutes")} мин, лучшая эпоха {V("best_epoch")}, клавиши угадываю на {V("key_acc")}",
                "failed" => $"не вышло: {V("error")}",
                var other => other,
            };
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return "статус пишется"; }
    }

    private static string _gameNotesDir = "";
    private static readonly Dictionary<string, DateTime> NotesShown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Чья тетрадка: процесс игры; у hl.exe (CS, Half-Life и моды) — по заголовку окна.</summary>
    private static string GameNotesKey(uint pid, string title)
    {
        string proc;
        try { using var p = Process.GetProcessById((int)pid); proc = p.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { proc = ""; }
        var key = proc is "" or "hl" && title.Length > 0 ? title : proc;
        return string.Concat(key.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
    }

    private static string NotesFile(string game) => Path.Combine(_gameNotesDir, game + ".md");

    /// <summary>
    /// Урок из катки — в тетрадку этой игры (data\games\&lt;игра&gt;.md): что сработало, где закуп, куда бежать, что
    /// убивает. Так она учится играть, а не начинает каждый раз с нуля. Держим последние 60 уроков.
    /// </summary>
    private static void AddLesson(string game, string lesson)
    {
        if (game.Length == 0 || _gameNotesDir.Length == 0) return;
        Directory.CreateDirectory(_gameNotesDir);
        var file = NotesFile(game);
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        lines.Add($"- {DateTime.Now:dd.MM HH:mm} {lesson.ReplaceLineEndings(" ").Trim()}");
        File.WriteAllLines(file, lines.TakeLast(60));
        NotesShown.Remove(game);
    }

    /// <summary>Тетрадка по игре — в начале игры и раз в 10 минут, чтобы уроки были перед глазами, а не забивали каждый ход.</summary>
    private static string NotesOnce(string game)
    {
        if (game.Length == 0 || _gameNotesDir.Length == 0 || !File.Exists(NotesFile(game))) return "";
        if (NotesShown.TryGetValue(game, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(10)) return "";
        NotesShown[game] = DateTime.UtcNow;
        var notes = File.ReadAllLines(NotesFile(game)).TakeLast(25);
        return $"\nТвоя тетрадка по «{game}» (уроки прошлых каток):\n{string.Join("\n", notes)}";
    }

    private static async Task<ToolResult> CsAsync(string action, string map, string team, int count, int skill,
        KlavaConfig cfg, Func<ToolRequest, Task<bool>> approve, ToolBox? box)
    {
        CsBridge.Start(cfg);
        var act = action.Trim().ToLowerInvariant();
        if (act == "start")
        {
            var exe = Path.Combine(cfg.CsDir, "hl.exe");
            if (!File.Exists(exe)) return new ToolResult($"CS 1.6 не нашла: {exe}. Путь к игре — csDir в klava.json.");
            if (CsBridge.Running) return new ToolResult("Игра уже идёт. Зайти самой — cs join.");
            var m = string.IsNullOrWhiteSpace(map) ? "de_dust2" : map.Trim();
            var maps = Path.Combine(cfg.CsDir, "cstrike", "maps");
            if (!File.Exists(Path.Combine(maps, m + ".bsp")))
                return new ToolResult($"Карты {m} нет. Есть: {string.Join(", ", Directory.EnumerateFiles(maps, "de_*.bsp").Select(Path.GetFileNameWithoutExtension).Take(20))}");
            var args = CsBridge.LaunchArgs(m);
            if (!await ApproveOrAutoAsync(new ToolRequest("Запустить Counter-Strike 1.6", $"{exe} {args}", false, null, "open"), approve, box)) return new ToolResult(Denied);
            Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = cfg.CsDir, UseShellExecute = true })?.Dispose();
            CsBridge.Map = m;
            var up = await CsBridge.WaitWindowAsync(TimeSpan.FromSeconds(60));
            return new ToolResult(up
                ? $"CS запущена, карта {m}. Друг выбирает команду в меню — потом cs join (зайдёшь к нему) и cs bots (противники)."
                : $"CS запускаю, окна игры пока нет. Как друг будет в игре и выберет команду — cs join.");
        }
        if (!CsBridge.Running)
            return new ToolResult("Игры CS сейчас нет — cs start создаст игру (обычный ярлык друга тоже годится, только окном: -window -noborder).");
        switch (act)
        {
            case "join":
            {
                if (CsBridge.KlavaName is not null) return new ToolResult("Ты уже в игре.");
                var t = string.IsNullOrWhiteSpace(team) || team.Trim().ToLowerInvariant() is "auto" or "к другу"
                    ? CsBridge.HostTeam is "T" ? "t" : "ct"
                    : team.Trim().ToLowerInvariant().StartsWith('t') ? "t" : "ct";
                // журнал включаем и тут — игру могли поднять не своим сервером, и listenserver.cfg не выполнился.
                // Ждать «joined» начинаем до F11 — бот заходит за доли секунды, событие не должно проскочить мимо.
                var joining = CsBridge.WaitAsync("joined", TimeSpan.FromSeconds(75));
                if (!await CsBridge.RunAsync("log on", "sv_logflush 1", "mp_logdetail 3", "mp_logmessages 1",
                        $"bot_difficulty {CsBridge.Difficulty(skill > 0 ? skill : 95)}", $"bot_prefix \"{CsBridge.Prefix}\"", $"bot_add_{t}", "bot_prefix \"\""))
                    return new ToolResult("Не дождалась окна игры впереди — команду в CS отдаю клавишей F11, когда друг в игре. Повтори, когда он переключится в CS.");
                var joined = await Task.WhenAny(joining, Task.Delay(TimeSpan.FromSeconds(15))) == joining ? await joining : null;
                return new ToolResult(joined is null
                    ? "Команду в игру отдала, но в журнале не видно, что бот зашёл. Друг, возможно, ещё в меню — повтори cs join."
                    : $"Ты в игре за {(joined.EndsWith("T") && !joined.EndsWith("CT") ? "террористов" : "спецназ")}. Твои фраги, смерти и раунды будут приходить тебе — комментируй.");
            }
            case "leave":
                if (CsBridge.KlavaName is not { } name) return new ToolResult("Тебя и так нет в игре.");
                await CsBridge.RunAsync($"kick \"{name}\"");
                return new ToolResult("Вышла из игры.");
            case "bots":
            {
                var n = Math.Clamp(count > 0 ? count : 3, 1, 10);
                var side = team.Trim().ToLowerInvariant();
                var enemyOfHost = CsBridge.HostTeam is "T" ? "ct" : "t";
                var t = side.StartsWith("ct") ? "ct" : side.StartsWith('t') ? "t" : enemyOfHost;
                var cmds = new List<string> { $"bot_difficulty {CsBridge.Difficulty(skill > 0 ? skill : 70)}" };
                for (var i = 0; i < n; i++) cmds.Add($"bot_add_{t}");
                return new ToolResult(await CsBridge.RunAsync(cmds.ToArray())
                    ? $"Добавила ботов: {n} за {(t == "t" ? "террористов" : "спецназ")}."
                    : "Не дождалась окна игры впереди — повтори, когда друг будет в CS.");
            }
            default:
                return new ToolResult(CsBridge.Status());
        }
    }
}
