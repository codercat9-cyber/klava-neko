using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Живая оболочка Клавы: один pwsh на весь разговор. `cd`, переменные окружения, активированное venv —
/// всё сохраняется между командами (в отличие от одноразового `run_powershell`).
///
/// Команда пишется во временный .ps1 и выполняется через dot-source (<c>. 'файл.ps1'</c>) — в текущей области
/// видимости, поэтому `cd` и переменные живут дальше, а многострочные скрипты, here-string и блоки
/// `if {}`/`foreach {}` работают как в обычном файле (раньше перенос строки внутри команды подвешивал stdin).
/// После команды печатается маркер с кодом выхода и папкой; его ищем в накопленном выводе, а не в отдельном
/// куске чтения — кусок в 4 КБ мог разрезать маркер пополам, и команда «висела» до таймаута.
///
/// Зависла команда или истёк таймаут — pwsh убивается деревом и поднимается заново в той же папке:
/// Ctrl+Break в чужую консоль из оконного приложения не доходит.
/// </summary>
internal sealed class Shell : IDisposable
{
    private const string PromptEnd = "___KLAVA_END_";

    private Process? _proc;
    private StreamWriter? _stdin;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly BlockingCollection<string> _pending = new();
    private CancellationTokenSource? _currentCts;
    private readonly string _scriptDir = Path.Combine(Path.GetTempPath(), "klava_shell");

    /// <summary>Событие запуска команды: (команда, cwd).</summary>
    public event Action<string, string>? CommandStarted;
    /// <summary>Событие кусочка вывода команды.</summary>
    public event Action<string>? OutputChunk;
    /// <summary>Событие завершения команды: (код выхода, время в мс, новый cwd).</summary>
    public event Action<int, long, string>? CommandCompleted;

    /// <summary>Уведомить подписчиков (кибер-терминал), что команда отрезана защитой безопасности.</summary>
    public void NotifyBlocked(string command, string reason)
    {
        Raise(() => CommandStarted?.Invoke(command, Cwd));
        Raise(() => OutputChunk?.Invoke($"\n[⛔ БЛОКИРОВКА АВТОПИЛОТА]: {reason}\n"));
        Raise(() => CommandCompleted?.Invoke(-1, 0, Cwd));
    }

    /// <summary>Открыт ли живой pwsh (Клава уже брала терминал в этом разговоре).</summary>
    public bool IsOpen => _proc is { HasExited: false };

    /// <summary>Идёт команда — терминал занят.</summary>
    public bool Busy => _busy.CurrentCount == 0;

    /// <summary>Текущая рабочая папка оболочки (для «где я сейчас»). Обновляется после каждой команды.</summary>
    public string Cwd { get; private set; } = "";

    /// <summary>Тот же exe оболочки — для фоновых заданий (Tools.job).</summary>
    internal static Lazy<string> Exe => ShellExe;

    /// <summary>Какой exe оболочки нашёлся: pwsh 7, иначе Windows PowerShell 5.1.</summary>
    private static readonly Lazy<string> ShellExe = new(() =>
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { if (File.Exists(Path.Combine(dir.Trim(), "pwsh.exe"))) return Path.Combine(dir.Trim(), "pwsh.exe"); }
            catch (ArgumentException) { }
        }
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        return File.Exists(pf) ? pf : "powershell.exe";
    });

    /// <summary>Поднять pwsh с рабочей папкой <paramref name="cwd"/>. Идемпотентно.</summary>
    public async Task StartAsync(string cwd)
    {
        if (IsOpen) return;
        if (!Directory.Exists(cwd)) Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(_scriptDir);
        var psi = new ProcessStartInfo(ShellExe.Value)
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // -NoLogo убирает баннер, -NonInteractive — чтобы никакие интерактивные подсказки не подвесили stdin
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", "-" }) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        // цветной вывод (ANSI) в терминале Клавы превращается в мусор — просим утилиты не краситься
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["TERM"] = "dumb";
        _proc = Process.Start(psi) ?? throw new InvalidOperationException("pwsh не запустился");
        // pwsh из Microsoft Store живёт в своём Job, в наш его не добавить — тогда гасим сами в Dispose
        try { Native.TieToLifetime(_proc); }
        catch (InvalidOperationException) { Log.Write("оболочка: pwsh из Store — закрою её сама при выходе"); }
        _stdin = _proc.StandardInput;
        // UTF-8, чтобы «мур» не превращался в кракозябры. Ошибки идут своим потоком; прогресс скрыт, чтобы
        // ProgressBar не забивал вывод (npm install и подобные). Цвета PSStyle — выключены.
        await _stdin.WriteLineAsync(
            "$OutputEncoding = [Text.UTF8Encoding]::new(); [Console]::OutputEncoding = [Text.UTF8Encoding]::new(); " +
            "$ErrorActionPreference = 'Continue'; $ProgressPreference = 'SilentlyContinue'; " +
            "if ($PSStyle) { $PSStyle.OutputRendering = 'PlainText' }").ConfigureAwait(false);
        // ConfigureAwait(false): после зависшей команды RestartAfterHang ждёт запуск синхронно — возможно, в UI-потоке
        await _stdin.FlushAsync().ConfigureAwait(false);
        var proc = _proc;
        _ = Task.Run(() => PumpAsync(proc.StandardOutput));
        _ = Task.Run(() => PumpAsync(proc.StandardError));
        Cwd = cwd;
        Log.Write($"живая оболочка запущена: {Path.GetFileName(ShellExe.Value)} (PID {_proc.Id}, cwd {cwd})");
    }

    /// <summary>Выполнить команду; вернуть код выхода, stdout+stderr одним куском и новую рабочую папку.</summary>
    /// <param name="timeout">Максимальное время (по умолчанию 5 минут). Только для одной этой команды.</param>
    public async Task<ShellResult> RunAsync(string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        await _busy.WaitAsync(ct);
        try
        {
            if (!IsOpen) await StartAsync(Cwd.Length > 0 && Directory.Exists(Cwd) ? Cwd : Path.GetTempPath());
            while (_pending.TryTake(out _)) { }   // хвост прошлой (прерванной) команды не должен попасть в эту

            var id = Guid.NewGuid().ToString("N")[..10];
            var marker = $"{PromptEnd}{id}_";
            var markerRx = new Regex(Regex.Escape(marker) + @"(-?\d+)_(.*?)\r?\n", RegexOptions.CultureInvariant);
            var sw = Stopwatch.StartNew();
            Raise(() => CommandStarted?.Invoke(command, Cwd));
            _currentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _currentCts.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
            var localCt = _currentCts.Token;

            // Сам текст команды — во временный скрипт (UTF-8 с BOM: Windows PowerShell 5.1 без BOM читает как ANSI).
            var script = Path.Combine(_scriptDir, $"cmd_{id}.ps1");
            await File.WriteAllTextAsync(script, command, new UTF8Encoding(true), CancellationToken.None);
            var quoted = script.Replace("'", "''");
            // Две отдельные строки — два оператора. Раньше маркер печатался в finally ТОГО ЖЕ оператора, а таблицы
            // (Select-Object, свои объекты) PowerShell копит до конца оператора: вывод приходил ПОСЛЕ маркера и
            // доставался следующей команде — Клава читала ответ на прошлую команду, а на свою видела «пустой вывод».
            // Ошибки считаем по потоку ошибок (2>&1): раньше `$?` после dot-source был true даже после ошибки, и
            // «Get-Item нет-файла» давал код 0 — отсюда «готово», когда ничего не готово. Подавленные
            // (-ErrorAction SilentlyContinue) в поток не попадают, stderr внешних программ (прогресс git) — не ошибка:
            // для них есть $LASTEXITCODE.
            var run =
                $"$global:LASTEXITCODE = $null; $__klavaOk = $true; $__klavaErrs = 0; " +
                $"try {{ . '{quoted}' 2>&1 | ForEach-Object {{ if ($_ -is [System.Management.Automation.ErrorRecord] -and $_.FullyQualifiedErrorId -notlike 'NativeCommandError*') {{ $__klavaErrs++ }}; $_ }} }} " +
                $"catch {{ [Console]::Out.WriteLine(($_ | Out-String)); $__klavaOk = $false }}";
            // $__klavaExit всегда число: $LASTEXITCODE последней внешней программы, иначе 0 — без ошибок, 1 — были.
            var done =
                $"$__klavaExit = if ($global:LASTEXITCODE -ne $null -and $global:LASTEXITCODE -ne 0) {{ $global:LASTEXITCODE }} elseif ($__klavaOk -and $__klavaErrs -eq 0) {{ 0 }} else {{ 1 }}; " +
                $"Remove-Item -LiteralPath '{quoted}' -ErrorAction SilentlyContinue; " +
                $"[Console]::Out.WriteLine(\"`n{marker}$($__klavaExit)_$((Get-Location).Path)\"); [Console]::Out.Flush()";
            await _stdin!.WriteLineAsync(run);
            await _stdin.WriteLineAsync(done);
            await _stdin.FlushAsync();

            var all = new StringBuilder();   // всё, что пришло за время команды
            var shown = 0;                    // сколько уже отдали в терминал
            while (true)
            {
                var chunk = await DrainAsync(localCt);
                if (chunk is null)
                {
                    if (_proc is null || _proc.HasExited)
                    {
                        var outText = all.ToString();
                        Raise(() => CommandCompleted?.Invoke(-1, sw.ElapsedMilliseconds, Cwd));
                        return new ShellResult(-1, outText + "\n(оболочка неожиданно закрылась — следующая команда поднимет новую)", Cwd, true);
                    }
                    if (localCt.IsCancellationRequested)
                    {
                        var why = ct.IsCancellationRequested ? "команду прервали" : $"команда не уложилась в {(timeout ?? TimeSpan.FromMinutes(5)).TotalSeconds:F0} с";
                        var outText = all.ToString();
                        RestartAfterHang();
                        Raise(() => CommandCompleted?.Invoke(-2, sw.ElapsedMilliseconds, Cwd));
                        return new ShellResult(-2, outText + $"\n({why} — оболочку перезапустила в той же папке)", Cwd, true);
                    }
                    continue;
                }
                all.Append(chunk);
                var text = all.ToString();
                var m = markerRx.Match(text);
                if (m.Success)
                {
                    var body = text[..m.Index];
                    if (body.Length > shown) { var tail = body[shown..]; Raise(() => OutputChunk?.Invoke(tail)); }
                    var exit = int.TryParse(m.Groups[1].Value, out var code) ? code : 0;
                    var cwd = m.Groups[2].Value.Trim();
                    if (cwd.Length > 0) Cwd = cwd;
                    Raise(() => CommandCompleted?.Invoke(exit, sw.ElapsedMilliseconds, Cwd));
                    return new ShellResult(exit, body.TrimEnd('\r', '\n'), Cwd, false);
                }
                // в терминал — всё, кроме последних символов: там может начинаться маркер
                var safe = Math.Max(shown, text.Length - marker.Length - 16);
                if (safe > shown) { var piece = text[shown..safe]; shown = safe; Raise(() => OutputChunk?.Invoke(piece)); }
            }
        }
        finally
        {
            _currentCts?.Dispose();
            _currentCts = null;
            _busy.Release();
        }
    }

    /// <summary>Прервать текущую команду (пользователь передумал ждать).</summary>
    public void CancelAsync() => _currentCts?.Cancel();

    /// <summary>Команда повисла: оболочку прибиваем вместе с детьми и поднимаем новую в той же папке.</summary>
    private void RestartAfterHang()
    {
        var cwd = Cwd;
        try { if (_proc is { HasExited: false } p) p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        _proc?.Dispose();
        _proc = null;
        _stdin = null;
        while (_pending.TryTake(out _)) { }
        Log.Write($"живая оболочка: зависла — перезапуск в {cwd}");
        try { StartAsync(Directory.Exists(cwd) ? cwd : Path.GetTempPath()).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { Log.Error("перезапуск оболочки", ex); }
    }

    /// <summary>Подписчики (окно терминала) не должны ронять саму команду — их ошибки только в лог.</summary>
    private static void Raise(Action a)
    {
        try { a(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { Log.Error("терминал: событие", ex); }
    }

    private async Task PumpAsync(StreamReader r)
    {
        var buf = new char[4096];
        try
        {
            while (true)
            {
                var n = await r.ReadAsync(buf, 0, buf.Length);
                if (n <= 0) break;
                if (!_pending.IsAddingCompleted) _pending.Add(new string(buf, 0, n));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    private async Task<string?> DrainAsync(CancellationToken ct)
    {
        if (_pending.TryTake(out var chunk)) return chunk;
        try
        {
            // Ждём кусочек с коротким шагом, чтобы вовремя увидеть отмену и смерть процесса.
            return await Task.Run(() => _pending.TryTake(out var c, 200, ct) ? c : null, CancellationToken.None);
        }
        catch (OperationCanceledException) { return null; }
    }

    public void Dispose()
    {
        _currentCts?.Cancel();
        try { _stdin?.WriteLine("exit"); _stdin?.Flush(); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        try { _proc?.WaitForExit(1000); } catch (SystemException) { }
        try { if (_proc is { HasExited: false } p) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        _proc?.Dispose();
        _pending.CompleteAdding();
    }

    /// <summary>
    /// KlavaDesk.exe --shell-test — прогнать живую оболочку как Клава: таблицы Select-Object, свои объекты, коды выхода,
    /// переменные между командами. Каждый случай — что вернулось и совпало ли с ожиданием.
    /// </summary>
    public static int SelfTest()
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var sh = new Shell();
        var cases = new (string Cmd, Func<ShellResult, bool> Ok, string What)[]
        {
            ("Get-Process -Id $PID | Select-Object Id, ProcessName", r => r.Output.Contains("ProcessName"), "таблица Select-Object"),
            ("[pscustomobject]@{ Name = 'мур'; N = 42 }", r => r.Output.Contains("мур") && r.Output.Contains("42"), "свой объект"),
            ("Get-ChildItem $env:WINDIR -Filter *.exe | Select-Object -First 3 Name, Length", r => r.Output.Contains(".exe"), "файлы через Select-Object"),
            ("Get-Item $env:WINDIR | Format-List FullName", r => r.Output.Contains("FullName"), "Format-List"),
            ("$klavaTest = 7", r => r.ExitCode == 0, "присвоить переменную"),
            ("\"x=$klavaTest\"", r => r.Output.Contains("x=7"), "переменная живёт между командами"),
            ("cmd /c exit 3", r => r.ExitCode == 3, "код выхода программы"),
            ("Get-Item 'Z:\\нет-такого-файла'", r => r.ExitCode != 0, "ошибка — код не ноль"),
            ("1..3 | ForEach-Object { $_ * 2 }", r => r.Output.Replace("\r", "").Contains("2\n4\n6"), "конвейер"),
            ("Get-Process -Id $PID | Select-Object Id; 'после таблицы'", r => r.Output.Contains("Id") && r.Output.Contains("после таблицы"), "таблица и текст после"),
            ("Get-Item 'Z:\\нет-такого-файла' -ErrorAction SilentlyContinue; 'дальше'", r => r.ExitCode == 0 && r.Output.Contains("дальше"), "подавленная ошибка — не провал"),
            ("cmd /c \"echo прогресс 1>&2\"", r => r.ExitCode == 0, "stderr программы при коде 0 — не провал"),
            ("Set-Location $env:WINDIR", r => r.Cwd.Equals(Environment.GetEnvironmentVariable("WINDIR"), StringComparison.OrdinalIgnoreCase), "cd запоминается"),
        };
        sh.StartAsync(Path.GetTempPath()).GetAwaiter().GetResult();
        var failed = 0;
        foreach (var (cmd, ok, what) in cases)
        {
            var r = sh.RunAsync(cmd, TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            var pass = ok(r);
            if (!pass) failed++;
            var shown = r.Output.ReplaceLineEndings(" ⏎ ").Trim();
            Console.WriteLine($"{(pass ? "OK  " : "FAIL")} {what}: код {r.ExitCode}, вывод «{(shown.Length > 110 ? shown[..110] + "…" : shown)}»");
        }
        Console.WriteLine(failed == 0 ? "оболочка: всё верно" : $"оболочка: не прошло {failed} из {cases.Length}");
        return failed == 0 ? 0 : 1;
    }
}

/// <summary>Результат одной команды в живой оболочке.</summary>
internal sealed record ShellResult(int ExitCode, string Output, string Cwd, bool Aborted);
