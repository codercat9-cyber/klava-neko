using System.Diagnostics;
using System.Text;

namespace KlavaDesk;

/// <summary>
/// Проверка мозгов без окна и без голоса — тем же кодом, что в разговоре:
///   KlavaDesk.exe --probe gemini "Здарова, чё как?"
///   KlavaDesk.exe --probe all "Глянь, чё у меня на экране" --look
/// all — все облачные мозги и Claude Code (свои llama-server не трогает: их долго поднимать).
/// История разговора читается, но не пишется. Вывод — в консоль (при запуске из терминала с перенаправлением).
/// </summary>
internal static class Probe
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var look = args.Contains("--look");
        // --autopilot — с настоящими инструментами (shell, файлы, ярлыки, браузер, лапки) на автопилоте
        Autopilot = args.Contains("--autopilot");
        // --task — долгая задача (с --autopilot): доска задач во временном файле и тот же мотор возвратов, что в живой
        // Клаве; ожидания task_wait проматываются, не больше 8 подходов
        TaskMode = args.Contains("--task");
        // --nocheck — без предпроверки мозга (как в живом разговоре): проверить цепочку запасных, когда выбранный лежит
        NoCheck = args.Contains("--nocheck");
        args = args.Where(a => a != "--autopilot" && a != "--task" && a != "--nocheck").ToArray();
        // new Control() для лапок иначе ставит на поток пула WinForms-контекст — и await-ы ждут цикла сообщений, которого нет
        WindowsFormsSynchronizationContext.AutoInstall = false;
        // --image файл.jpg — вместо снимка экрана готовая картинка (проверить зрение, не отправляя свой экран)
        var imgAt = Array.IndexOf(args, "--image");
        string? imageFile = imgAt >= 0 && imgAt + 1 < args.Length ? args[imgAt + 1] : null;
        var rest = args.Where((a, i) => a != "--look" && (imgAt < 0 || (i != imgAt && i != imgAt + 1))).ToArray();
        if (rest.Length < 2)
        {
            Console.WriteLine("KlavaDesk.exe --probe <id мозга|all> \"реплика\" [--look | --image файл.jpg]");
            return 2;
        }
        if (imageFile is not null) { ProbeImage = File.ReadAllBytes(imageFile); look = true; }
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\probe.log"));
        var ids = rest[0] == "all"
            ? cfg.Brains.Where(b => !b.IsLocal).Select(b => b.Id).ToList()
            : rest[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return Task.Run(() => RunAsync(cfg, ids, rest[1], look)).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(KlavaConfig cfg, List<string> ids, string text, bool look)
    {
        var failed = 0;
        var services = new Services(cfg);
        try
        {
            string? screenText = null;
            byte[]? jpeg = null;
            if (ProbeImage is not null) { screenText = ""; jpeg = ProbeImage; }
            else if (look)
            {
                var sw = Stopwatch.StartNew();
                (screenText, jpeg) = await new Eyes(IntPtr.Zero).LookAsync(cfg.ScreenTextMaxChars, 1920, wantImage: true, wantText: true);
                Console.WriteLine($"глаза: снимок {jpeg!.Length / 1024} КБ, текст {screenText.Length} символов, {sw.ElapsedMilliseconds} мс");
            }
            foreach (var spec in ids)
            {
                // id@модель — у мозгов со списком моделей (antigravity@claude-opus-4-6-thinking); models@id — только список
                var at = spec.IndexOf('@');
                var id = at > 0 ? spec[..at] : spec;
                if (id == "models")
                {
                    var mp = cfg.Brains.FirstOrDefault(b => b.Id == spec[(at + 1)..]);
                    if (mp is null) { Console.WriteLine($"== {spec}: нет такого мозга"); failed++; continue; }
                    var list = await services.ListModelsAsync(mp);
                    Console.WriteLine($"== модели {mp.Id}: {(list.Count == 0 ? "сервер не отдал список" : string.Join(' ', list))}");
                    if (list.Count == 0) failed++;
                    continue;
                }
                var p = cfg.Brains.FirstOrDefault(b => b.Id == id);
                if (p is null) { Console.WriteLine($"== {id}: нет такого мозга"); failed++; continue; }
                if (at > 0) p.Model = spec[(at + 1)..];
                cfg.Brain = p.Id;   // только в памяти — klava.json не пишем
                Console.WriteLine($"== {p.Id} ({p.DisplayName})");
                var sw = Stopwatch.StartNew();
                var ok = await services.EnsureLlmAsync(p, TimeSpan.FromMinutes(3), new SyncProgress(s => Console.WriteLine($"   {s}")));
                Console.WriteLine($"   проверка: {(ok ? "ок" : "НЕ ГОТОВ")} за {sw.ElapsedMilliseconds} мс");
                if (!ok && !NoCheck) { failed++; continue; }

                string? context = null;
                byte[]? image = null;
                if (look)
                {
                    image = p.Vision && !p.IsLocal ? jpeg : null;
                    var ocr = image is null && screenText!.Length > 0 ? $" Текст с экрана, распознанный автоматически:\n{screenText}\n" : "";
                    context = $"(Ты посмотрела на экран друга.{(image is not null ? " Снимок экрана приложен — смотри на него." : "")}{ocr})\n" +
                              "Скажи коротко и по-свойски, что там происходит, своими словами — не зачитывай текст подряд.";
                }
                using var brain = new Brain(cfg, saveHistory: false);
                using var shell = new Shell();
                Hands? hands = null;
                if (Autopilot)
                {
                    var eyes = new Eyes(IntPtr.Zero);
                    // AutoInstall у WinForms — [ThreadStatic]: запрещаем на этом (пуловом) потоке, иначе new Control() поставит сюда
                    // свой контекст синхронизации и все await-ы будут ждать цикла сообщений, которого у пула нет
                    WindowsFormsSynchronizationContext.AutoInstall = false;
                    hands = new Hands(new Control());
                    SynchronizationContext.SetSynchronizationContext(null);
                    shell.CommandStarted += (c, d) => Console.WriteLine($"   $ [{d}] {c}");
                    brain.Toolbox = new ToolBox
                    {
                        Hands = hands, Shell = shell, Autopilot = true, WorkDir = cfg.Abs("workspace"), HandsAllowed = false,
                        Capture = () => eyes.CaptureAsync(1600, cfg.ScreenTextMaxChars),
                        CaptureRegion = r => eyes.CaptureAsync(1600, cfg.ScreenTextMaxChars, r),
                        FindText = q => eyes.FindTextAsync(q),
                        SaveScreenshot = (p, all) => eyes.SaveScreenshotAsync(p, all),
                        Tasks = TaskMode ? new TaskBoard(Path.Combine(Path.GetTempPath(), $"klava_probe_task_{Guid.NewGuid():N}.json")) : null,
                    };
                }
                Func<ToolRequest, Task<bool>> approve = req =>
                {
                    Console.WriteLine($"   [окно подтверждения] {req.Title}: {req.Details.Split('\n')[0]} → {(Autopilot ? "разрешено (probe)" : "запрещено")}");
                    return Task.FromResult(Autopilot);
                };
                sw.Restart();
                try
                {
                    var phrases = 0;
                    await foreach (var ph in brain.AskAsync(text, context, image, approve, CancellationToken.None))
                    {
                        phrases++;
                        Console.WriteLine($"   {sw.ElapsedMilliseconds,6} мс  [{ph.Emotion}] {ph.Text}");
                    }
                    Console.WriteLine($"   готово за {sw.ElapsedMilliseconds} мс");
                    if (phrases == 0) { Console.WriteLine("   ОШИБКА: мозг промолчал"); failed++; }
                    // мотор долгой задачи — как TaskTick в живой Клаве, только без ожиданий
                    if (brain.Toolbox?.Tasks is { } tb)
                    {
                        for (var n = 1; tb.Active && n <= 8; n++)
                        {
                            tb.MarkContinued();
                            Console.WriteLine($"   --- подход {n + 1} к задаче «{tb.Goal}»\n{tb.Describe()}");
                            var ctx = "(Система: у тебя незаконченная долгая задача — друг ждёт результат, не бросай её.\n" + tb.Describe() + "\n" +
                                      "Сделай СЛЕДУЮЩИЙ шаг инструментами, затем отметь его task_step с заметкой. Ждать — task_wait. " +
                                      "Цель достигнута и ПРОВЕРЕНА — task_done. Сделать нельзя — task_cancel. Вслух — одна короткая фраза.)";
                            await foreach (var ph in brain.AskAsync($"{Brain.TaskPrefix} продолжаю задачу «{tb.Goal}»", ctx, null, approve, CancellationToken.None))
                                Console.WriteLine($"   {sw.ElapsedMilliseconds,6} мс  [{ph.Emotion}] {ph.Text}");
                        }
                        Console.WriteLine(tb.Active ? $"   ЗАДАЧА НЕ ЗАКРЫТА за 8 подходов:\n{tb.Describe()}" : $"   задача закрыта за {sw.ElapsedMilliseconds / 1000} с");
                        if (tb.Active) failed++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"   ОШИБКА: {ex.GetType().Name}: {ex.Message}");
                    failed++;
                }
                finally { hands?.Dispose(); }
            }
        }
        finally
        {
            services.StopAll();
        }
        return failed == 0 ? 0 : 1;
    }

    private static byte[]? ProbeImage;
    private static bool Autopilot;
    private static bool TaskMode;
    private static bool NoCheck;

    /// <summary>Progress&lt;T&gt; шлёт отчёты в пул потоков и они перемешиваются с выводом — здесь сразу.</summary>
    private sealed class SyncProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
