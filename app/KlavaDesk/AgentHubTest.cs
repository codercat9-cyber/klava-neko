using System.Text;

namespace KlavaDesk;

/// <summary>
/// KlavaDesk.exe --hub-test "задача" [папка [модель [пересадить на]]] — агент Claude Code без Клавы: строки вкладки,
/// субагенты и итог — в консоль. модель — словами или id («джемини 3.8 флеш хай»), только на эту проверку; «пересадить
/// на» — после первых шагов агент пересаживается на вторую модель и продолжает (проверка смены модели на ходу).
/// KlavaDesk.exe --hub-test models — какие модели есть у шлюза и цепочка запасных.
/// Автопилот включён (как у друга); окна подтверждения показать некому — то, что спросило бы друга, запрещается.
/// </summary>
internal static class AgentHubTest
{
    public static int Run(string goal, string? workDir, string? model = null, string? switchTo = null)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\hub_test.log"));
        var models = Task.Run(() => AgentHub.ModelsAsync(cfg, refresh: true)).GetAwaiter().GetResult();
        if (goal == "models")
        {
            Console.WriteLine(AgentHub.ModelsText(cfg, models));
            return models.Count > 0 ? 0 : 1;
        }
        if (!string.IsNullOrWhiteSpace(model))
        {
            if (AgentHub.PickModel(model, models) is not { } picked) { Console.WriteLine($"модели «{model}» у шлюза нет: {string.Join(", ", models)}"); return 2; }
            cfg.AgentModel = picked;   // только в памяти этой проверки, klava.json не трогается
            cfg.AgentEngine = "ag";
        }
        var dir = string.IsNullOrWhiteSpace(workDir) ? Path.Combine(Path.GetTempPath(), "klava_agent_test") : workDir;
        Directory.CreateDirectory(dir);
        using var shell = new Shell();
        using var ui = new Control();
        var box = new ToolBox
        {
            Hands = new Hands(ui),
            Capture = () => Task.FromException<ScreenShot>(new InvalidOperationException("в проверке агента глаз нет")),
            Shell = shell,
            Autopilot = true,
            ShellApproved = true,
            WorkDir = dir,
            HandsAllowed = false,
        };
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        AgentHub.Line += (s, l) => Console.WriteLine($"[{l.At:HH:mm:ss}] {(l.Sub is { } sub ? $"   └ [{sub}] " : "")}{l.Kind} {AgentHub.Short(l.Text.Replace('\n', ' '), 260)}");
        AgentHub.TurnDone += (_, result) => Volatile.Read(ref done).TrySetResult(result);
        Task<bool> Approve(ToolRequest r)
        {
            Console.WriteLine($"  [окно друга: {r.Title} — {r.DangerReason}: запрещаю в проверке]");
            return Task.FromResult(false);
        }
        // на пуле: Control выше поставил потоку контекст WinForms — синхронное ожидание на нём повесило бы продолжения
        var start = Task.Run(() => AgentHub.StartAsync(goal, "проверка", dir, cfg, box, Approve)).GetAwaiter().GetResult();
        Console.WriteLine(start);
        if (!start.StartsWith("Агент #", StringComparison.Ordinal)) return 2;
        if (!string.IsNullOrWhiteSpace(switchTo) && AgentHub.Sessions.LastOrDefault() is { } live)
        {
            // пересадка на ходу: ждём первых шагов, меняем модель — агент должен продолжить в той же вкладке
            for (var i = 0; i < 240 && live.Steps < 2 && !done.Task.IsCompleted; i++) Thread.Sleep(500);
            if (AgentHub.PickModel(switchTo, models) is not { } second) { Console.WriteLine($"модели «{switchTo}» у шлюза нет"); return 2; }
            if (done.Task.IsCompleted) done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            Console.WriteLine("\n=== ПЕРЕСАДКА === " + Task.Run(() => AgentHub.SwitchAsync(live, second, cfg, "проверка смены модели", carryOn: true)).GetAwaiter().GetResult() + "\n");
        }
        var finished = done.Task.Wait(TimeSpan.FromMinutes(Math.Clamp(cfg.AgentMaxMinutes, 5, 60)));
        var s = AgentHub.Sessions.LastOrDefault();
        Console.WriteLine("\n=== ОБЗОР ДЛЯ КЛАВЫ ===\n" + AgentHub.Overview());
        if (s is not null) Console.WriteLine("\n=== ПОДРОБНО ===\n" + s.Details());
        AgentHub.StopAll();
        return finished && s is { Subs.Count: >= 0 } && !done.Task.Result.StartsWith("агент упал", StringComparison.Ordinal) ? 0 : 1;
    }
}
