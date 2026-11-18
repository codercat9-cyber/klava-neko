namespace KlavaDesk;

/// <summary>
/// Окна Клавы без рабочего стола друга — рисуются за краем экрана, результат в картинку и в консоль:
///   --ui-shot profiles [файл.png]      окно голосовых профилей (профили настоящие, ничего не пишется)
///   --ui-shot audio [файл.png]         настройки звука (компания до 8 человек, списки «Кто:»)
///   --ui-shot reading [файл.png]       тренажёр чтения, как его видит человек
///   --ui-shot terminal [файл.png] [сырые_события.jsonl]  мультитерминал: вкладка агента из записанной сессии
///   --ui-shot reading-rec [файл.png]   тренажёр пишет с настоящего микрофона 5 с (ничего не сохраняет)
///   --ui-shot reading-full [файл.png]  «чтение» = записи первого человека вперемешку с чужими голосами → «Готово»:
///                                      разбор, профиль, обучение слуха. Пишет в профили — запускать только с
///                                      KLAVA_HOME на копии (см. tools\test_reading.ps1).
/// </summary>
internal static class UiShot
{
    public static int Run(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\ui_shot.log"));
        var which = args.Length > 0 ? args[0] : "profiles";
        var file = args.Length > 1 ? args[1] : Path.Combine(Path.GetTempPath(), $"klava_{which}.png");
        var speaker = new Speaker(cfg);
        var exit = 0;
        Form f;
        ReadingTrainerForm? reading = null;
        string target = "Кибер_Кот";
        float[]? injected = null;
        if (which == "reading-full")
        {
            if (Environment.GetEnvironmentVariable("KLAVA_HOME") is not { Length: > 0 })
            {
                Console.WriteLine("reading-full пишет в профили — запускай с KLAVA_HOME на копии (tools\\test_reading.ps1)");
                return 2;
            }
            (target, injected) = FakeReading(cfg);
        }
        if (which == "panel") return PanelShots(cfg, speaker, file);
        if (which == "terminal") return TerminalShot(cfg, file, args.Length > 2 ? args[2] : "");
        if (which.StartsWith("reading", StringComparison.Ordinal))
            f = reading = new ReadingTrainerForm(cfg, speaker, target, profileExists: true);
        else if (which == "audio")
            f = new AudioSettingsForm(cfg, new Ears(cfg), new Services(cfg), speaker: speaker);   // микрофон не открывается: Ears только по Start
        else
            f = new VoiceProfilesForm(cfg, speaker);
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(-6000, -6000);
        f.ShowInTaskbar = false;
        f.Shown += async (_, _) =>
        {
            try
            {
                if (which == "reading-rec" && reading is not null)
                {
                    await Task.Delay(800);
                    reading.StartForTest();
                    await Task.Delay(5000);
                    var (rec, speech, rms) = reading.StatsForTest();
                    Console.WriteLine($"микрофон: записано {rec:F1} с, речи {speech:F1} с, громкость сейчас {rms:F4}");
                    if (rec < 4) { Console.WriteLine("ПЛОХО: микрофон не пишет"); exit = 1; }
                    reading.StartForTest();   // пауза — окно закроется без вопроса «запись пропадёт»
                }
                else if (which == "reading-full" && reading is not null && injected is not null)
                {
                    reading.InjectForTest(injected);
                    await reading.FinishForTestAsync();
                    Console.WriteLine(reading.ReportForTest);
                    if (!reading.ReportForTest.StartsWith('✔')) exit = 1;
                }
                else if (which == "reading-book" && reading is not null && args.Length > 2)
                {
                    // --ui-shot reading-book файл.png книга [страница]: книга разбирается (или берётся из кэша), снимок страницы
                    var ok = await reading.LoadBookAsync(args[2]);
                    if (!ok) exit = 1;
                    reading.ShowPageForTest(args.Length > 3 && int.TryParse(args[3], out var pg) ? pg - 1 : 0);
                    Console.WriteLine($"книга: {reading.BookForTest.Title}, страниц {reading.BookForTest.Pages.Count}, ударения: {reading.BookForTest.AccentBy}");
                    await Task.Delay(500);
                }
                else await Task.Delay(3000);   // список профилей подгружается асинхронно
                using var bmp = new Bitmap(f.Width, f.Height);
                f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
                bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
            {
                Console.WriteLine($"ОШИБКА: {ex.Message}");
                exit = 1;
            }
            f.Close();
        };
        Application.Run(f);
        Console.WriteLine(file);
        return exit != 0 ? exit : File.Exists(file) ? 0 : 1;
    }

    /// <summary>Мультитерминал: вкладка Клавы и вкладка агента, проигранного из записи (KLAVA_AGENT_RAW), — две картинки.</summary>
    private static int TerminalShot(KlavaConfig cfg, string file, string raw)
    {
        using var shell = new Shell();
        var f = new TerminalForm(shell, cfg.Abs("workspace"))
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-6000, -6000), ShowInTaskbar = false, TopMost = false,
        };
        var exit = 0;
        f.Shown += async (_, _) =>
        {
            try
            {
                var box = new ToolBox { Hands = new Hands(f), Shell = shell, Capture = () => Task.FromException<ScreenShot>(new InvalidOperationException()) };
                var lines = File.Exists(raw) ? File.ReadAllLines(raw) : [];
                AgentHub.Replay("подсчёт файлов", "Отдай субагенту Explore: сколько файлов *.cs… Сам выполни cmd //c ver.", Path.GetTempPath(), lines, box);
                await Task.Delay(1500);
                for (var i = 0; i < 2; i++)
                {
                    f.SelectTabForTest(i == 0 ? 0 : -1);
                    await Task.Delay(600);
                    using var bmp = new Bitmap(f.Width, f.Height);
                    f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
                    var path = Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileNameWithoutExtension(file)}_{i + 1}.png");
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine(path);
                }
                Console.WriteLine(AgentHub.Overview());
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
            {
                Console.WriteLine($"ОШИБКА: {ex.Message}");
                exit = 1;
            }
            f.Close();
        };
        Application.Run(f);
        return exit;
    }

    /// <summary>Панель настроек: каждый раздел отдельной картинкой (файл_1.png, файл_2.png…). Настройки не меняются.</summary>
    private static int PanelShots(KlavaConfig cfg, Speaker speaker, string file)
    {
        var f = new ControlPanelForm(null, cfg, null, new Services(cfg), speaker, SystemIcons.Application)
        {
            StartPosition = FormStartPosition.Manual, Location = new Point(-6000, -6000), ShowInTaskbar = false, TopMost = false,
        };
        var exit = 0;
        f.Shown += async (_, _) =>
        {
            try
            {
                for (var i = 0; i < f.PageCount; i++)
                {
                    f.SelectPageForTest(i);
                    await Task.Delay(i == 0 ? 1500 : 700);
                    using var bmp = new Bitmap(f.Width, f.Height);
                    f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
                    var path = Path.Combine(Path.GetDirectoryName(file)!, $"{Path.GetFileNameWithoutExtension(file)}_{i + 1}.png");
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    Console.WriteLine(path);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
            {
                Console.WriteLine($"ОШИБКА: {ex.Message}");
                exit = 1;
            }
            f.Close();
        };
        Application.Run(f);
        return exit;
    }

    /// <summary>
    /// «Чтение» из готовых записей: человек с самым большим числом записей (кроме Клавы), между его кусками — записи
    /// других людей (как если бы в комнате кто-то влез в разговор). В профиль должен попасть только он.
    /// </summary>
    private static (string Name, float[] Audio) FakeReading(KlavaConfig cfg)
    {
        var store = cfg.Abs(@"data\speakers");
        var people = Directory.GetDirectories(store).Where(d => !Path.GetFileName(d).StartsWith('_') && Path.GetFileName(d) != "Клава")
            .Select(d => (Name: Path.GetFileName(d), Files: Directory.GetFiles(d, "*.wav").Order(StringComparer.Ordinal).ToList()))
            .OrderByDescending(p => p.Files.Count).ToList();
        var me = people[0];
        var others = people.Skip(1).SelectMany(p => p.Files.Take(3)).ToList();
        var pause = new float[16000 / 2];
        var audio = new List<float>();
        for (var i = 0; i < me.Files.Count; i++)
        {
            audio.AddRange(Lite.ReadWav16k(File.ReadAllBytes(me.Files[i])));
            audio.AddRange(pause);
            if (i % 3 == 2 && i / 3 < others.Count)
            {
                audio.AddRange(Lite.ReadWav16k(File.ReadAllBytes(others[i / 3])));
                audio.AddRange(pause);
            }
        }
        Console.WriteLine($"«чтение» {me.Name}: {audio.Count / 16000.0 / 60:F1} мин, своих записей {me.Files.Count}, чужих вставок {Math.Min(others.Count, me.Files.Count / 3)}");
        return (me.Name, audio.ToArray());
    }
}
