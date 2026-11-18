using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// KlavaDesk.exe --watch-test — проверка «наблюдай» без живой Клавы: своё окно Paint (рисуется домик) и своё окно
/// консоли с кодом, наблюдение с частыми взглядами, потом отчёт мозгом. Журнал и снимки — в data\logs\watch_test
/// (настоящий журнал не трогается), отчёт — в консоль. Свои окна закрываются без сохранения; чужие не трогаются.
/// </summary>
internal static class WatchTest
{
    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\watch_test.log"));
        cfg.WatchIntervalSec = 10;
        cfg.WatchDescribeSec = 15;
        cfg.WatchChangeThreshold = 4;
        var code = 1;
        using var form = new Form
        {
            ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Location = new Point(-3000, -3000), Size = new Size(1, 1), Opacity = 0,
        };
        form.Shown += async (_, _) =>
        {
            try { code = await RunAsync(form, cfg); }
            catch (Exception ex) { Console.WriteLine($"ОШИБКА: {ex.GetType().Name}: {ex.Message}"); }
            finally { form.Close(); }
        };
        Application.Run(form);
        return code;
    }

    private static async Task<int> RunAsync(Form ui, KlavaConfig cfg)
    {
        var eyes = new Eyes(ui.Handle);
        var brain = new Brain(cfg);
        var watcher = new Watcher(cfg, () => eyes, brain, cfg.Abs(@"data\logs\watch_test"));
        var seen = new List<WatchEntry>();
        watcher.Noticed += e => { lock (seen) seen.Add(e); Console.WriteLine($"  заметила {e.Time.ToLocalTime():HH:mm:ss}: {e.What}"); };
        var start = DateTime.UtcNow;
        var procs = new List<Process>();
        var hands = new Hands(ui);
        using var shell = new Shell();
        var box = new ToolBox { Hands = hands, Shell = shell, Autopilot = true, Capture = () => throw new InvalidOperationException() };
        try
        {
            Console.WriteLine(watcher.Start());
            // 1) рисуют в Paint
            var before = Process.GetProcessesByName("mspaint").Select(p => p.Id).ToHashSet();
            Process.Start(new ProcessStartInfo("mspaint.exe") { UseShellExecute = true });
            IntPtr win = IntPtr.Zero;
            for (var i = 0; i < 80 && win == IntPtr.Zero; i++)
            {
                await Task.Delay(250);
                foreach (var p in Process.GetProcessesByName("mspaint"))
                {
                    if (before.Contains(p.Id)) { p.Dispose(); continue; }
                    p.Refresh();
                    if (p.MainWindowHandle != IntPtr.Zero) { win = p.MainWindowHandle; procs.Add(p); break; }
                }
            }
            if (win == IntPtr.Zero) { Console.WriteLine("Paint не открылся"); return 1; }
            await Task.Delay(1500);
            PaintTool.TargetWindow = win;
            hands.Begin();
            var ops = JsonNode.Parse("""
                [{"op":"rect","from":[0,0],"to":[1000,330],"color":"#99D9EA"},{"op":"rect","from":[0,330],"to":[1000,480],"color":"#22B14C"},
                 {"op":"rect","from":[350,200],"to":[650,330],"color":"#EFE4B0"},{"op":"polygon","points":[[320,200],[500,90],[680,200]],"color":"#ED1C24"},
                 {"op":"ellipse","from":[80,40],"to":[200,160],"color":"#FFC90E"}]
                """)!.AsArray();
            await PaintTool.RunAsync(new JsonObject { ["ops"] = ops }, box, CancellationToken.None);
            hands.End();
            PaintTool.TargetWindow = IntPtr.Zero;
            Console.WriteLine("нарисован домик — жду, пока заметит");
            await Task.Delay(TimeSpan.FromSeconds(35));

            // 2) кодят в Блокноте
            var file = Path.Combine(Path.GetTempPath(), "klava_watch_test.py");
            await File.WriteAllTextAsync(file,
                "# Змейка на pygame\nimport pygame\nimport random\n\nWIDTH, HEIGHT = 640, 480\nCELL = 20\n\n" +
                "def main():\n    pygame.init()\n    screen = pygame.display.set_mode((WIDTH, HEIGHT))\n    snake = [(5, 5), (4, 5), (3, 5)]\n" +
                "    food = (random.randrange(WIDTH // CELL), random.randrange(HEIGHT // CELL))\n    clock = pygame.time.Clock()\n" +
                "    while True:\n        for event in pygame.event.get():\n            if event.type == pygame.QUIT:\n                return\n" +
                "        clock.tick(10)\n\nif __name__ == '__main__':\n    main()\n");
            // своё консольное окно с кодом, а не Блокнот: Блокнот Windows 11 открыл бы файл вкладкой в окне друга,
            // и закрыть его после теста значило бы закрыть и его записи
            var con = Process.Start(new ProcessStartInfo("cmd.exe", $"/k title klava_watch_test.py - код & type \"{file}\"") { UseShellExecute = true });
            if (con is not null) procs.Add(con);
            await Task.Delay(2500);
            var cw = Uia.FindWindow("klava_watch_test");
            if (cw != IntPtr.Zero) Uia.Focus(cw);
            Console.WriteLine("открыт код змейки — жду, пока заметит");
            await Task.Delay(TimeSpan.FromSeconds(35));
            Console.WriteLine(watcher.Stop());

            var report = await watcher.ReportAsync(start.ToLocalTime().AddMinutes(-1).ToString("HH:mm"), CancellationToken.None);
            Console.WriteLine("\n===== ОТЧЁТ =====\n" + report);
            var text = report.ToLowerInvariant();
            var drew = seen.Any(e => e.App.Contains("Paint", StringComparison.OrdinalIgnoreCase)) && (text.Contains("paint") || text.Contains("пейнт") || text.Contains("рису"));
            var coded = seen.Any(e => e.Title.Contains("klava_watch_test", StringComparison.OrdinalIgnoreCase) || e.What.Contains("змей", StringComparison.OrdinalIgnoreCase) || e.What.Contains("pygame", StringComparison.OrdinalIgnoreCase));
            Console.WriteLine($"\n[{(drew ? "ок" : "ПЛОХО")}] рисование в Paint попало в журнал и отчёт");
            Console.WriteLine($"[{(coded ? "ок" : "ПЛОХО")}] код в консоли попал в журнал");
            Console.WriteLine(drew && coded ? "watch: OK" : "watch: провал");
            return drew && coded ? 0 : 1;
        }
        finally
        {
            if (watcher.Active) watcher.Stop();
            foreach (var p in procs) { try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { } }
        }
    }
}
