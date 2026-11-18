namespace KlavaDesk;

internal static class Program
{
    private const string WakeEventName = @"Local\KlavaDesk.wakeup";
    private const string AudioEventName = @"Local\KlavaDesk.audio";
    private const string BrainsEventName = @"Local\KlavaDesk.brains";
    private const string TerminalEventName = @"Local\KlavaDesk.terminal";
    private const string SayEventName = @"Local\KlavaDesk.say";
    private const string QuitEventName = @"Local\KlavaDesk.quit";
    private const string MuteEventName = @"Local\KlavaDesk.mute";

    /// <summary>Куда `--mute on|off` кладёт желаемое состояние «Без звука» для уже запущенной Клавы.</summary>
    private static string MuteFile => Path.Combine(Path.GetTempPath(), "klava_mute.txt");

    /// <summary>Куда `--say` кладёт текст для уже запущенной Клавы (одна строка на реплику).</summary>
    private static string SayFile => Path.Combine(Path.GetTempPath(), "klava_say.txt");

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe") { Environment.ExitCode = Probe.Run(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--selftest") { Environment.ExitCode = SelfTest.Run(); return; }
        if (args.Length > 0 && args[0] == "--presence-test") { Environment.ExitCode = GameWatchTest.Presence(); return; }
        if (args.Length > 1 && args[0] == "--game-look") { Environment.ExitCode = GameWatchTest.Look(args[1]); return; }
        if (args.Length > 0 && args[0] == "--game-apply") { Environment.ExitCode = GameWatchTest.Apply(); return; }
        if (args.Length > 0 && args[0] == "--capture-test") { Environment.ExitCode = GameWatchTest.Capture(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--tap-test") { Environment.ExitCode = SpeakerTapTest.Run(); return; }
        if (args.Length > 1 && args[0] == "--game-watch-live") { Environment.ExitCode = GameWatchTest.Live(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--lite-test") { Environment.ExitCode = LiteTest.Run(); return; }
        if (args.Length > 0 && args[0] == "--stress-test")
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            LiteStress.Init(KlavaConfig.Load());
            var input = args.Length > 1 ? string.Join(" ", args[1..]) : "Здарова, братан! Я Клава, твоя кошкодевочка. В 2026 году у нас уже 25 звёзд, пиздец охуенно!";
            var res = LiteText.Normalize(input);
            Console.WriteLine($"Вход:  {input}");
            Console.WriteLine($"Выход: {res}");
            return;
        }
        if (args.Length > 0 && args[0] == "--set") { Environment.ExitCode = SetConfig(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--web-config-merge") { Console.OutputEncoding = System.Text.Encoding.UTF8; Environment.ExitCode = WebConfigMerge.Run(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--paint-test") { ApplicationConfiguration.Initialize(); Environment.ExitCode = PaintTest.Run(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--speakers-test") { Environment.ExitCode = SpeakersTest.Run(); return; }
        if (args.Length > 0 && args[0] == "--speakers-bench") { Environment.ExitCode = SpeakersBench.Run(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--ui-shot") { Environment.ExitCode = UiShot.Run(args[1..]); return; }
        if (args.Length > 1 && args[0] == "--reading-book") { Environment.ExitCode = ReadingBookTest.Run(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--tetris-play")
        {
            Log.Open(KlavaConfig.Load().Abs(@"data\logs\tetris_play.log"));
            Environment.ExitCode = TetrisBot.PlayTest(args.Length > 1 && int.TryParse(args[1], out var sec) ? sec : 45);
            return;
        }
        if (args.Length > 0 && args[0] == "--speakers-train")
        {
            // обучить слух и подобрать пороги по настоящим профилям (записи не трогаются: пишутся калибровка и кэши отпечатков)
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var sc = KlavaConfig.Load();
            Log.Open(sc.Abs(@"data\logs\speakers_train.log"));
            var (ok, report) = Lite.Speakers(sc).TrainEar(new ConsoleProgress());
            Console.WriteLine(report);
            Environment.ExitCode = ok ? 0 : 1;
            return;
        }
        if (args.Length > 1 && args[0] == "--cs-parse") { Environment.ExitCode = CsBridge.ParseTest(args[1]); return; }
        if (args.Length > 1 && args[0] == "--hub-test") { Environment.ExitCode = AgentHubTest.Run(args[1], args.Length > 2 ? args[2] : null, args.Length > 3 ? args[3] : null, args.Length > 4 ? args[4] : null); return; }
        if (args.Length > 1 && args[0] == "--agent-test") { Environment.ExitCode = AgentRunner.Test(args[1], args.Length > 2 ? args[2] : null); return; }
        if (args.Length > 0 && args[0] == "--zuma-look") { Environment.ExitCode = ZumaBot.LookTest(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--shell-test") { Environment.ExitCode = Shell.SelfTest(); return; }
        if (args.Length > 0 && args[0] == "--gamer-test")
        {
            var gcfg = KlavaConfig.Load();
            Log.Open(gcfg.Abs(@"data\logs\gamer_test.log"));
            Environment.ExitCode = GamePlayer.SelfTest(gcfg.Abs("gamer"));
            return;
        }
        if (args.Length > 0 && args[0] == "--arena-test")
        {
            var acfg = KlavaConfig.Load();
            Log.Open(acfg.Abs(@"data\logs\arena_test.log"));
            Environment.ExitCode = ArenaPlayer.SelfTest(acfg);
            return;
        }
        if (args.Length > 0 && args[0] == "--cs-menu") { var mcfg = KlavaConfig.Load(); Log.Open(mcfg.Abs(@"data\logs\cs_menu.log")); Environment.ExitCode = CsMenu.Test(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--arena-live") { Environment.ExitCode = ArenaPlayer.LiveTest(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--bot-school") { Environment.ExitCode = BotSchool.LiveTest(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--paws-test")
        {
            Log.Open(KlavaConfig.Load().Abs(@"data\logs\paws_test.log"));
            Environment.ExitCode = Tools.PawsTest();
            return;
        }
        if (args.Length > 0 && args[0] == "--zuma-mem") { Environment.ExitCode = ZumaMemory.Test(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--zuma-menu") { Environment.ExitCode = ZumaMenu.Test(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--zuma-local") { Environment.ExitCode = ZumaBot.LocalLook(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--web-shot") { Environment.ExitCode = WebShot.Run(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--later-test") { Environment.ExitCode = Later.Test(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--memory-test") { Environment.ExitCode = MemoryBank.Test(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--diary-test") { Environment.ExitCode = Diary.Test(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--graph-test") { Environment.ExitCode = MemoryGraph.Test(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--music-test") { Environment.ExitCode = MusicClient.Test(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--paint-hand") { Environment.ExitCode = PencilPlan.Test(args[1..]); return; }
        if (args.Length > 0 && args[0] == "--paint-art")
        {
            Log.Open(KlavaConfig.Load().Abs(@"data\logs\paint_art.log"));
            Environment.ExitCode = PaintTool.ArtTest(args[1..]);
            return;
        }
        if (args.Length > 0 && args[0] == "--zuma-play")
        {
            Log.Open(KlavaConfig.Load().Abs(@"data\logs\zuma_play.log"));
            Environment.ExitCode = ZumaBot.LocalTest(args[1..]);
            return;
        }
        if (args.Length > 0 && args[0] == "--zuma-cdp")
        {
            Log.Open(KlavaConfig.Load().Abs(@"data\logs\zuma_cdp.log"));
            Environment.ExitCode = ZumaCdp.Run(args[1..]);
            return;
        }
        if (args.Length > 0 && args[0] == "--tetris-sim") { Environment.ExitCode = TetrisBot.SimTest(); return; }
        if (args.Length > 0 && args[0] == "--tetris-look") { Environment.ExitCode = TetrisBot.LookTest(args.Length > 1 ? args[1] : null); return; }
        if (args.Length > 0 && args[0] == "--watch-test") { ApplicationConfiguration.Initialize(); Environment.ExitCode = WatchTest.Run(); return; }
        if (args.Length > 0 && args[0] == "--autosetup")
        {
            // автонастройка мозгов из консоли: прокси, AG Tools, ключи, локальные серверы — отчёт в stdout
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var ac = KlavaConfig.Load();
            Log.Open(ac.Abs(@"data\logs\autosetup.log"));
            var n = Task.Run(() => AutoSetup.RunAsync(ac, new ConsoleProgress())).GetAwaiter().GetResult();
            Environment.ExitCode = n > 0 ? 0 : 1;
            return;
        }

        // Команды уже запущенной Клаве: окно настроек звука, окно мозгов, терминал, реплика текстом, выход (--quit —
        // как пункт «Выход» в трее: память записывается, сервисы гасятся; для перезапуска скриптом после обновления).
        if (args.Length > 0 && args[0] is "--audio" or "--brains" or "--terminal" or "--say" or "--quit" or "--mute")
        {
            if (args[0] == "--mute")
            {
                // «Без звука» у запущенной Клавы, как пункт трея: проверки ночью, не будя друга
                var on = args.Length < 2 || args[1] is "on" or "1" or "true" or "вкл";
                File.WriteAllText(MuteFile, on ? "on" : "off");
            }
            if (args[0] == "--say")
            {
                var text = string.Join(' ', args[1..]).Trim();
                if (text.Length == 0) { Environment.ExitCode = 2; return; }
                File.AppendAllText(SayFile, text.Replace('\n', ' ') + Environment.NewLine);
            }
            var name = args[0] switch
            {
                "--audio" => AudioEventName, "--brains" => BrainsEventName, "--terminal" => TerminalEventName, "--quit" => QuitEventName,
                "--mute" => MuteEventName, _ => SayEventName,
            };
            try
            {
                using var ev = EventWaitHandle.OpenExisting(name);
                ev.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException) { }
            if (args[0] != "--audio") { Environment.ExitCode = 1; return; }   // Клава не запущена
            // Если Клава ещё не запущена — окно звука поднимаем автономно
            ApplicationConfiguration.Initialize();
            var c = KlavaConfig.Load();
            using var e = new Ears(c);
            var s = new Services(c);
            Application.Run(new AudioSettingsForm(c, e, s));
            return;
        }

        using var single = new Mutex(true, @"Local\KlavaDesk.single", out var first);
        if (!first)
        {
            // Сигнализируем работающей Клаве проснуться и показаться на рабочем столе
            try
            {
                using var wake = EventWaitHandle.OpenExisting(WakeEventName);
                wake.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }

        using var wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
        using var audioEvent = new EventWaitHandle(false, EventResetMode.AutoReset, AudioEventName);
        using var brainsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, BrainsEventName);
        using var terminalEvent = new EventWaitHandle(false, EventResetMode.AutoReset, TerminalEventName);
        using var sayEvent = new EventWaitHandle(false, EventResetMode.AutoReset, SayEventName);
        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        using var muteEvent = new EventWaitHandle(false, EventResetMode.AutoReset, MuteEventName);
        ApplicationConfiguration.Initialize();
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\klava.log"));
        Application.ThreadException += (_, e) => Log.Error("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log.Error("необработанное", ex); };
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error("фоновая задача", e.Exception.GetBaseException()); e.SetObserved(); };
        WebConfigMerge.EnsureExists(cfg.Abs(cfg.WebRoot));
        var form = new OverlayForm(cfg);

        // Один поток ждёт все сигналы от второго экземпляра (ярлыки, скрипты, --say).
        var handles = new WaitHandle[] { wakeEvent, audioEvent, brainsEvent, terminalEvent, sayEvent, quitEvent, muteEvent };
        var signals = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    var i = WaitHandle.WaitAny(handles);
                    if (form.IsDisposed) break;
                    switch (i)
                    {
                        case 0: form.BeginInvoke(form.ShowOnDesktop); break;
                        case 1: form.BeginInvoke(form.ShowAudioSettingsForm); break;
                        case 2: form.BeginInvoke(form.ShowBrainProfiles); break;
                        case 3: form.BeginInvoke(form.ShowTerminalForm); break;
                        case 4:
                            string[] lines = [];
                            try { if (File.Exists(SayFile)) { lines = File.ReadAllLines(SayFile); File.Delete(SayFile); } }
                            catch (IOException ex) { Log.Error("--say", ex); }
                            foreach (var line in lines.Where(l => l.Trim().Length > 0))
                                form.BeginInvoke(() => form.SayText(line.Trim()));
                            break;
                        case 5:
                            Log.Write("--quit: выход по команде");
                            form.BeginInvoke(form.Close);
                            break;
                        case 6:
                            string state = "";
                            try { if (File.Exists(MuteFile)) { state = File.ReadAllText(MuteFile).Trim(); File.Delete(MuteFile); } }
                            catch (IOException ex) { Log.Error("--mute", ex); }
                            Log.Write($"--mute: без звука — {state}");
                            form.BeginInvoke(() => form.SetMuted(state != "off"));
                            break;
                    }
                }
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }) { IsBackground = true };
        signals.Start();

        Application.Run(form);
    }

    /// <summary>
    /// KlavaDesk.exe --set brain=local speechBackend=lite brainCtx=16384 — поправить klava.json из скриптов установки.
    /// Значение — JSON (числа, true/false, массивы) или просто строка. Нет klava.json — сначала пишутся умолчания.
    /// </summary>
    private static int SetConfig(string[] pairs)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        var file = Path.Combine(cfg.Root, "klava.json");
        if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file)) is not System.Text.Json.Nodes.JsonObject root)
        {
            Console.WriteLine($"{file}: не объект JSON");
            return 1;
        }
        foreach (var p in pairs)
        {
            var i = p.IndexOf('=');
            if (i <= 0) { Console.WriteLine($"не понял «{p}» — нужно имя=значение"); return 2; }
            var key = p[..i].Trim();
            var val = p[(i + 1)..];
            System.Text.Json.Nodes.JsonNode? node;
            try { node = System.Text.Json.Nodes.JsonNode.Parse(val); }
            catch (System.Text.Json.JsonException) { node = System.Text.Json.Nodes.JsonValue.Create(val); }
            root[key] = node;
            Console.WriteLine($"{key} = {node?.ToJsonString() ?? "null"}");
        }
        var backup = file + ".before-set";
        File.Copy(file, backup, overwrite: true);
        File.WriteAllText(file, root.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        try
        {
            KlavaConfig.Load();   // правка не должна ломать чтение конфига
        }
        catch (System.Text.Json.JsonException ex)
        {
            File.Copy(backup, file, overwrite: true);
            Console.WriteLine($"после правки klava.json не читается ({ex.Message}) — вернула как было");
            return 1;
        }
        File.Delete(backup);
        return 0;
    }

    /// <summary>Отчёт автонастройки сразу в консоль (Progress&lt;T&gt; перемешивал бы строки из пула потоков).</summary>
    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string value) => Console.WriteLine(value);
    }
}
