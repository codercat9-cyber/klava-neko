using System.Diagnostics;
using System.IO.Enumeration;
using System.Text;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Инструменты для настоящей работы с кодом — то, без чего агенту «без рук»: точечная правка файла, чтение по
/// строкам, поиск по содержимому и по именам, Linux (WSL) и фоновые процессы (серверы, обучение, загрузки).
/// Раньше были только read_file (первые 8000 знаков), write_file (переписать целиком) и shell.
/// </summary>
internal static partial class Tools
{
    /// <summary>Папки, в которые поиск не лезет: сборки, зависимости, служебное.</summary>
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", ".git", ".vs", ".idea", "__pycache__", ".venv", "venv", "dist", "build", ".next", "target", ".mypy_cache", ".pytest_cache",
    };

    /// <summary>Строки файла с номерами (с offset, не больше limit).</summary>
    private static async Task<ToolResult> ReadLinesAsync(string path, int offset, int limit, CancellationToken ct)
    {
        var lines = await File.ReadAllLinesAsync(path, Encoding.UTF8, ct);
        var from = Math.Max(1, offset);
        var count = limit > 0 ? Math.Min(limit, 2000) : 400;
        var sb = new StringBuilder();
        for (var i = from - 1; i < Math.Min(lines.Length, from - 1 + count); i++)
            sb.Append(i + 1).Append('\t').AppendLine(lines[i].Length > 500 ? lines[i][..500] + "…" : lines[i]);
        if (sb.Length == 0) return new ToolResult($"В файле {lines.Length} строк — начиная с {from} ничего нет.");
        var rest = lines.Length - (from - 1 + count);
        return new ToolResult(rest > 0 ? $"{sb}…(дальше ещё {rest} строк, всего {lines.Length})" : sb.ToString());
    }

    /// <summary>
    /// Точечная правка: заменить кусок old на new (ровно одно вхождение или все). Переводы строк подгоняются под файл
    /// (модель шлёт \n, а файл в CRLF), BOM сохраняется, прошлая версия — в %TEMP%\klava_edits (в проект .bak не сорим).
    /// </summary>
    private static async Task<ToolResult> EditFileAsync(string path, string oldS, string newS, bool all, Func<ToolRequest, Task<bool>> approve, ToolBox? box, CancellationToken ct)
    {
        if (!File.Exists(path)) return new ToolResult($"Файла нет: {path}. Новый файл — write_file.");
        if (oldS.Length == 0) return new ToolResult("old_string пустой — нечего заменять. Новый файл — write_file.");
        if (oldS == newS) return new ToolResult("old_string и new_string одинаковые — правки нет.");
        var bytes = await File.ReadAllBytesAsync(path, ct);
        var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        string o = oldS, n = newS;
        if (!text.Contains(o, StringComparison.Ordinal) && text.Contains("\r\n") && !o.Contains("\r\n"))
        {
            o = o.Replace("\n", "\r\n");
            n = n.Replace("\r\n", "\n").Replace("\n", "\r\n");
        }
        var count = 0;
        for (var i = text.IndexOf(o, StringComparison.Ordinal); i >= 0; i = text.IndexOf(o, i + o.Length, StringComparison.Ordinal)) count++;
        if (count == 0) return new ToolResult("Кусок old_string в файле не найден — сверь пробелы и отступы (read_file с offset/limit покажет строки как есть).");
        if (count > 1 && !all) return new ToolResult($"Кусок встречается {count} раз — возьми больше окружающих строк, чтобы он стал единственным, или replace_all=true.");
        var sys = SystemPathRx().IsMatch(path);
        var details = $"Правка {path}:\r\n--- было ---\r\n{oldS}\r\n--- стало ---\r\n{newS}";
        if (!await ApproveOrAutoAsync(new ToolRequest("Поправить файл", details, sys, sys ? "Системная папка." : null, "write"), approve, box)) return new ToolResult(Denied);
        var backups = Path.Combine(Path.GetTempPath(), "klava_edits");
        Directory.CreateDirectory(backups);
        try { File.Copy(path, Path.Combine(backups, $"{Path.GetFileName(path)}.{DateTime.Now:yyyyMMdd_HHmmss_fff}.bak"), overwrite: true); }
        catch (IOException) { }
        string result;
        if (all) result = text.Replace(o, n, StringComparison.Ordinal);
        else
        {
            var at = text.IndexOf(o, StringComparison.Ordinal);
            result = string.Concat(text.AsSpan(0, at), n, text.AsSpan(at + o.Length));
        }
        await File.WriteAllTextAsync(path, result, new UTF8Encoding(bom), ct);
        return new ToolResult($"Поправила {path}: заменено {(all ? count : 1)} (прошлая версия — %TEMP%\\klava_edits)");
    }

    /// <summary>Файлы под папкой по маскам имени, мимо сборок и зависимостей (SkipDirs).</summary>
    private static IEnumerable<string> CodeFiles(string root, IReadOnlyList<string> masks)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            List<string> subs, files;
            try
            {
                subs = Directory.EnumerateDirectories(dir).ToList();
                files = Directory.EnumerateFiles(dir).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { continue; }
            foreach (var f in files)
                if (masks.Any(m => FileSystemName.MatchesSimpleExpression(m, Path.GetFileName(f), ignoreCase: true))) yield return f;
            foreach (var s in subs)
                if (!SkipDirs.Contains(Path.GetFileName(s))) stack.Push(s);
        }
    }

    /// <summary>«*.{js,ts}» → [*.js, *.ts]; пусто → [*]; «**/src/*.cs» → [*.cs] (папки не различаем — ищем везде).</summary>
    private static List<string> Masks(string glob)
    {
        if (string.IsNullOrWhiteSpace(glob)) return ["*"];
        var last = glob.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "*";
        var m = Regex.Match(last, @"^(.*)\{([^}]*)\}(.*)$");
        return m.Success ? m.Groups[2].Value.Split(',').Select(x => m.Groups[1].Value + x.Trim() + m.Groups[3].Value).ToList() : [last];
    }

    /// <summary>Поиск по содержимому: «файл:строка: текст», до 200 совпадений; бинарные и файлы больше 2 МБ пропускаются.</summary>
    /// <summary>Заведомо не текст — даже не открывать.</summary>
    private static readonly HashSet<string> BinaryExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".flac", ".ogg", ".m4a", ".opus", ".aac", ".mid", ".sf2", ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp",
        ".ico", ".mp4", ".mkv", ".avi", ".webm", ".npy", ".npz", ".emb", ".pt", ".pth", ".ckpt", ".bin", ".gguf", ".onnx",
        ".safetensors", ".zip", ".7z", ".rar", ".gz", ".exe", ".dll", ".pdb", ".vrm", ".glb", ".fbx", ".blend", ".dmp", ".db", ".sqlite",
    };

    /// <summary>
    /// Текстовый ли файл — по расширению и первым 4 КБ (нулевой байт — двоичный), не читая файл целиком. Замер 30.09:
    /// grep по data\ шёл 110 с — там 31 тыс. мелких файлов, 29,7 тыс. из них wav и векторы, и каждый читался как текст.
    /// </summary>
    private static bool LooksText(string file)
    {
        if (BinaryExt.Contains(Path.GetExtension(file))) return false;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096);
            var head = new byte[4096];
            var n = fs.Read(head, 0, head.Length);
            return Array.IndexOf(head, (byte)0, 0, n) < 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static ToolResult Grep(string pattern, string path, string glob, bool ignoreCase, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pattern)) return new ToolResult("Пустой шаблон поиска.");
        Regex rx;
        try { rx = new Regex(pattern, (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250)); }
        catch (ArgumentException ex) { return new ToolResult($"Плохая регулярка: {ex.Message}"); }
        var isFile = File.Exists(path);
        if (!isFile && !Directory.Exists(path)) return new ToolResult($"Нет такой папки или файла: {path}");
        var root = isFile ? Path.GetDirectoryName(path)! : path;
        var files = isFile ? [path] : CodeFiles(path, Masks(glob));
        var sb = new StringBuilder();
        int hits = 0, seen = 0;
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            if (++seen > 30000) { sb.AppendLine("…(больше 30000 файлов — сузь папку или маску)"); break; }
            string[] lines;
            try
            {
                if (new FileInfo(f).Length > 2_000_000 || !LooksText(f)) continue;
                lines = File.ReadAllLines(f);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            if (lines.Take(8).Any(l => l.Contains('\0'))) continue;   // бинарный
            for (var i = 0; i < lines.Length; i++)
            {
                bool m;
                try { m = rx.IsMatch(lines[i]); }
                catch (RegexMatchTimeoutException) { m = false; }
                if (!m) continue;
                var line = lines[i].Trim();
                sb.AppendLine($"{Path.GetRelativePath(root, f)}:{i + 1}: {(line.Length > 240 ? line[..240] + "…" : line)}");
                if (++hits >= 200) { sb.AppendLine("…(200 совпадений — сузь поиск)"); return new ToolResult(sb.ToString()); }
            }
        }
        return new ToolResult(hits == 0 ? $"Совпадений нет (искала в {root})." : sb.ToString());
    }

    /// <summary>Файлы по маске имени, свежие сверху, до 300.</summary>
    private static ToolResult FindFiles(string pattern, string path, CancellationToken ct)
    {
        if (!Directory.Exists(path)) return new ToolResult($"Папки нет: {path}");
        var list = new List<FileInfo>();
        foreach (var f in CodeFiles(path, Masks(pattern)))
        {
            ct.ThrowIfCancellationRequested();
            list.Add(new FileInfo(f));
            if (list.Count >= 5000) break;
        }
        if (list.Count == 0) return new ToolResult($"Файлов «{pattern}» в {path} нет.");
        var sb = new StringBuilder($"{path} — найдено {list.Count}{(list.Count >= 5000 ? "+" : "")}:\n");
        foreach (var fi in list.OrderByDescending(x => x.LastWriteTime).Take(300))
            sb.AppendLine($"{Path.GetRelativePath(path, fi.FullName)}  ({fi.Length / 1024.0:F1} КБ, {fi.LastWriteTime:dd.MM HH:mm})");
        return new ToolResult(sb.ToString());
    }

    // ---------- Linux (WSL) ----------

    /// <summary>Дистрибутив, который реально поднялся (у друга Ubuntu не монтирует диск — живой Kali).</summary>
    private static string? _linuxDistro;

    /// <summary>
    /// Команда в Linux: названный дистрибутив, иначе тот, что уже поднимался, иначе по списку WSL. Не поднялся
    /// (Wsl/Service/CreateInstance… — сломанный образ) — следующий, и она скажет, куда переключилась.
    /// </summary>
    private static async Task<ToolResult> LinuxAsync(string cmd, double timeoutSec, string distro, string workDir,
        Func<ToolRequest, Task<bool>> approve, ToolBox? box, CancellationToken ct)
    {
        var order = new List<string>();
        if (distro.Length > 0) order.Add(distro);
        else
        {
            if (_linuxDistro is not null) order.Add(_linuxDistro);
            order.AddRange(WslDistros());
        }
        if (order.Count == 0) return new ToolResult("В WSL нет ни одного дистрибутива Linux.");
        var dead = new List<string>();
        ToolResult? last = null;
        foreach (var d in order.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var r = await BashRunAsync(cmd, timeoutSec, workDir, approve, box, ct, d);
            if (!r.Text.Contains("Wsl/Service", StringComparison.OrdinalIgnoreCase))
            {
                if (distro.Length == 0) _linuxDistro = d;
                return dead.Count == 0 ? r : new ToolResult($"({string.Join(", ", dead)} не поднимается — выполнила в {d})\n{r.Text}");
            }
            dead.Add(d);
            last = r;
        }
        return new ToolResult($"Ни один дистрибутив WSL не поднялся ({string.Join(", ", dead)}).\n{last?.Text}");
    }

    private static List<string> WslDistros()
    {
        try
        {
            var psi = new ProcessStartInfo("wsl.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.Unicode };
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("-q");
            using var p = Process.Start(psi);
            if (p is null) return [];
            var s = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return s.Split('\n').Select(x => x.Trim().Trim('\0')).Where(x => x.Length > 0).ToList();
        }
        catch (System.ComponentModel.Win32Exception) { return []; }
    }

    // ---------- фоновые процессы ----------

    private sealed class Job
    {
        public int Id;
        public string Name = "";
        public string Command = "";
        public string LogFile = "";
        public Process Proc = null!;
        public DateTime Started;
    }

    private static readonly List<Job> Jobs = [];
    private static int _jobSeq;

    private static Job? FindJob(string key)
    {
        lock (Jobs)
            return int.TryParse(key.TrimStart('#'), out var id) ? Jobs.FirstOrDefault(j => j.Id == id)
                : Jobs.LastOrDefault(j => j.Name.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase)) ?? (key.Length == 0 ? Jobs.LastOrDefault() : null);
    }

    /// <summary>
    /// Фоновое задание: процесс живёт сам по себе, вывод пишется в data\jobs\*.log, модель не ждёт его в shell (сервер,
    /// обучение, долгая загрузка). Умирает вместе с Клавой (Job-объект), чтобы не оставались сироты.
    /// </summary>
    private static async Task<ToolResult> JobAsync(string action, string command, string shell, string name, int lines, string workDir,
        KlavaConfig cfg, Func<ToolRequest, Task<bool>> approve, ToolBox? box)
    {
        switch (action.Trim().ToLowerInvariant())
        {
            case "start":
            {
                if (string.IsNullOrWhiteSpace(command)) return new ToolResult("Для start нужна команда.");
                if (CatastropheRx().IsMatch(command)) return new ToolResult("❌ Заблокировано защитой: команда может снести систему.");
                var m = DangerCmdRx().Match(command);
                if (!await ApproveOrAutoAsync(new ToolRequest("Фоновый процесс", command, m.Success, m.Success ? $"Опасная команда: {m.Value}" : null, "shell"), approve, box))
                    return new ToolResult(Denied);
                var kind = shell.Trim().ToLowerInvariant();
                ProcessStartInfo psi;
                if (kind is "bash")
                {
                    var gitBash = new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe" }.FirstOrDefault(File.Exists);
                    psi = new ProcessStartInfo(gitBash ?? "wsl.exe");
                    if (gitBash is null) { psi.ArgumentList.Add("-e"); psi.ArgumentList.Add("bash"); }
                    psi.ArgumentList.Add("-lc");
                    psi.ArgumentList.Add(command);
                }
                else if (kind is "linux" or "wsl")
                {
                    // живой дистрибутив — той же пробой, что у linux (сломанный образ пропускается)
                    if (_linuxDistro is null) await LinuxAsync("true", 60, "", workDir, approve, box, CancellationToken.None);
                    if (_linuxDistro is null) return new ToolResult("Ни один дистрибутив WSL не поднялся — Linux-задание не запустить.");
                    psi = new ProcessStartInfo("wsl.exe");
                    foreach (var a in new[] { "-d", _linuxDistro, "--", "bash", "-lc", command }) psi.ArgumentList.Add(a);
                }
                else
                {
                    psi = new ProcessStartInfo(Shell.Exe.Value);
                    foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command }) psi.ArgumentList.Add(a);
                }
                psi.WorkingDirectory = Directory.Exists(workDir) ? workDir : Path.GetTempPath();
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = Encoding.UTF8;
                psi.StandardErrorEncoding = Encoding.UTF8;
                psi.Environment["PYTHONUNBUFFERED"] = "1";   // print в питоне — сразу в лог, а не пачкой в конце
                psi.Environment["PYTHONIOENCODING"] = "utf-8";
                psi.Environment["NO_COLOR"] = "1";
                var id = Interlocked.Increment(ref _jobSeq);
                var nm = string.IsNullOrWhiteSpace(name) ? $"job{id}" : name.Trim();
                var dir = cfg.Abs(@"data\jobs");
                Directory.CreateDirectory(dir);
                var log = Path.Combine(dir, $"{DateTime.Now:yyyyMMdd_HHmmss}_{id}_{Regex.Replace(nm, @"[^\w\-]+", "_")}.log");
                Process proc;
                try { proc = Process.Start(psi) ?? throw new InvalidOperationException("процесс не запустился"); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { return new ToolResult($"Не запустилось: {ex.Message}"); }
                try { Native.TieToLifetime(proc); } catch (InvalidOperationException) { }
                var writer = new StreamWriter(new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
                var gate = new object();
                async Task Pump(StreamReader r)
                {
                    var buf = new char[4096];
                    try
                    {
                        int k;
                        while ((k = await r.ReadAsync(buf, 0, buf.Length)) > 0)
                            lock (gate) writer.Write(buf, 0, k);
                    }
                    catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                }
                var pumps = Task.WhenAll(Pump(proc.StandardOutput), Pump(proc.StandardError));
                _ = pumps.ContinueWith(_ =>
                {
                    lock (gate)
                    {
                        try { writer.WriteLine($"\n[процесс завершился, код {(proc.HasExited ? proc.ExitCode : -1)}]"); } catch (ObjectDisposedException) { }
                        writer.Dispose();
                    }
                }, TaskScheduler.Default);
                var job = new Job { Id = id, Name = nm, Command = command, LogFile = log, Proc = proc, Started = DateTime.Now };
                lock (Jobs) Jobs.Add(job);
                Log.Write($"задание #{id} «{nm}» запущено (PID {proc.Id}): {command}");
                await Task.Delay(1500);   // первые строки или мгновенная ошибка — сразу видно
                var head = Tail(log, 15);
                return new ToolResult($"Задание #{id} «{nm}» {(proc.HasExited ? $"уже завершилось (код {proc.ExitCode})" : "работает")} (PID {proc.Id}). Лог: {log}\n{head}");
            }
            case "output":
            case "status":
            {
                var job = FindJob(name);
                if (job is null) return new ToolResult("Такого задания нет — job list покажет все.");
                var state = job.Proc.HasExited ? $"завершилось, код {job.Proc.ExitCode}" : $"работает {(DateTime.Now - job.Started):hh\\:mm\\:ss}";
                return new ToolResult(action.Trim().ToLowerInvariant() == "status"
                    ? $"#{job.Id} «{job.Name}»: {state}"
                    : $"#{job.Id} «{job.Name}»: {state}\n{Tail(job.LogFile, lines > 0 ? Math.Min(lines, 400) : 60)}");
            }
            case "stop":
            {
                var job = FindJob(name);
                if (job is null) return new ToolResult("Такого задания нет.");
                if (job.Proc.HasExited) return new ToolResult($"#{job.Id} «{job.Name}» уже завершилось (код {job.Proc.ExitCode}).");
                try { job.Proc.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return new ToolResult($"Не остановилось: {ex.Message}"); }
                return new ToolResult($"Остановила #{job.Id} «{job.Name}».");
            }
            default:
            {
                lock (Jobs)
                {
                    if (Jobs.Count == 0) return new ToolResult("Фоновых заданий нет.");
                    return new ToolResult(string.Join("\n", Jobs.Select(j =>
                        $"#{j.Id} «{j.Name}»: {(j.Proc.HasExited ? $"завершилось ({j.Proc.ExitCode})" : "работает")} — {Short(j.Command, 80)}")));
                }
            }
        }
    }

    private static string Short(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Последние строки лога (файл может дописываться прямо сейчас).</summary>
    private static string Tail(string file, int n)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var len = fs.Length;
            var take = (int)Math.Min(len, 64 * 1024);
            fs.Seek(len - take, SeekOrigin.Begin);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            var lines = Encoding.UTF8.GetString(buf, 0, read).Replace("\r", "").Split('\n');
            var tail = string.Join("\n", lines.TakeLast(n)).Trim();
            return tail.Length == 0 ? "(пока пусто)" : tail;
        }
        catch (IOException ex) { return $"(лог не читается: {ex.Message})"; }
    }

    /// <summary>Остановить все фоновые задания (выход из Клавы).</summary>
    public static void StopAllJobs()
    {
        lock (Jobs)
            foreach (var j in Jobs.Where(j => !j.Proc.HasExited))
                try { j.Proc.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
}
