using System.Diagnostics;
using System.Text;

namespace KlavaDesk;

/// <summary>
/// Учёба нейронки игры по записям друга (gamer\train\train.py, в фоне на видеокарте). Одна учёба за раз.
/// Полная — по просьбе (инструмент gamer train); дообучение (finetune) — само, после каждого перехвата в Арене: друг
/// показал, как надо, — пара эпох поверх готовой модели с его показом вчетверо весомее, сервер Арены подхватывает
/// новые веса на лету (динамическое обучение).
/// </summary>
internal static class GamerTrainer
{
    private static readonly object Gate = new();
    private static Process? _proc;

    public static string Game { get; private set; } = "";
    public static bool Busy { get { lock (Gate) return _proc is { HasExited: false }; } }

    /// <summary>Клавиши, которые ногам шутера нужны: остальное (закуп B, «,» «.», цифры меню) — не ходьба.</summary>
    public static string? KeysFor(string game) =>
        game.Contains("counter", StringComparison.OrdinalIgnoreCase) || game.Contains("sam", StringComparison.OrdinalIgnoreCase)
            ? "W,A,S,D,SPACE,CTRL,SHIFT" : null;

    /// <summary>Запустить учёбу. null — запущена; иначе — почему нет.</summary>
    public static string? Start(KlavaConfig cfg, string game, bool finetune)
    {
        var gamer = cfg.Abs("gamer");
        var python = Path.Combine(gamer, ".venv", "Scripts", "python.exe");
        if (!File.Exists(python)) return $"нет окружения нейронки: {python}";
        if (!Directory.Exists(Path.Combine(gamer, "data", game))) return $"записей игры «{game}» нет";
        lock (Gate)
        {
            if (_proc is { HasExited: false }) return $"уже учусь на «{Game}»";
            var outDir = Path.Combine(gamer, "models", game);
            Directory.CreateDirectory(outDir);
            var psi = new ProcessStartInfo(python)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.Combine(gamer, "train"),
            };
            foreach (var a in new[] { "-u", "train.py", "--game", game }) psi.ArgumentList.Add(a);
            if (KeysFor(game) is { } keys) { psi.ArgumentList.Add("--keys"); psi.ArgumentList.Add(keys); }
            if (finetune) psi.ArgumentList.Add("--finetune");
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            var log = new StreamWriter(Path.Combine(outDir, finetune ? "finetune.log" : "train.log"), finetune, new UTF8Encoding(false)) { AutoFlush = true };
            if (finetune) log.WriteLine($"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} дообучение после перехвата");
            var p = Process.Start(psi);
            if (p is null) { log.Dispose(); return "не удалось запустить обучение"; }
            try { Native.TieToLifetime(p); } catch (InvalidOperationException) { }
            // учёба в фоне не должна отнимать процессор у игры и голоса
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            p.OutputDataReceived += (_, e) => { if (e.Data is { } l) lock (log) log.WriteLine(l); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is { } l) lock (log) log.WriteLine(l); };
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                lock (log) log.Dispose();
                Log.Write($"учёба: {(finetune ? "дообучение" : "обучение")} «{game}» закончилось (код {SafeExit(p)})");
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _proc = p;
            Game = game;
        }
        Log.Write($"учёба: {(finetune ? "дообучаю" : "обучаю")} нейронку на «{game}»");
        return null;
    }

    private static int SafeExit(Process p)
    {
        try { return p.ExitCode; }
        catch (InvalidOperationException) { return -1; }
    }
}
