using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace KlavaDesk;

/// <summary>
/// Школа у ботов (идея друга 29.09: «боты играют лучше неё — их логику догрузить в нейронки»). Честно, как человек,
/// который учится по чужой игре: Клава заходит в CS наблюдателем (M → «6 НАБЛЮДЕНИЕ»), пробелом ставит вид «От первого
/// лица» и пишет, что видит бот, — 16 кадров в секунду, тем же форматом, что запись друга, только без нажатий
/// (frames.csv + idm.txt). Нажатия бота потом восстанавливает по картинке нейронка обратной динамики
/// (gamer\train\idm.py, учится на записях друга), и ноги переучиваются на всём сразу: часы ботовской игры —
/// маршруты по карте, куда смотреть, как выходить из тупиков. Ни консоли, ни влезания в сервер.
/// Стоп: Backspace/Esc, «стоп», время. В конце — обратно в команду (M → команда → класс).
/// </summary>
internal static class BotSchool
{
    private static CancellationTokenSource? _cts;
    private static volatile string _status = "не учусь у ботов";

    public static event Action<string>? Finished;
    public static bool Running => _cts is not null;
    public static string Status() => _status;

    public static string Start(KlavaConfig cfg, int seconds, CsMenu.Plan plan)
    {
        if (_cts is not null) return $"Уже смотрю ботов: {_status}";
        if (ArenaPlayer.Playing) return "Сейчас играю сама (Арена) — сначала стоп.";
        var hwnd = Uia.FindWindow("Counter-Strike");
        if (hwnd == IntPtr.Zero) return "CS не открыта — сначала arena play (создам игру с ботами), потом школа.";
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var limit = TimeSpan.FromSeconds(Math.Clamp(seconds <= 0 ? 600 : seconds, 60, 3 * 3600));
        GameMode.Enter("school");
        _ = Task.Run(async () =>
        {
            string result;
            try { result = await RunAsync(cfg, hwnd, limit, plan, token); }
            catch (OperationCanceledException) { result = "остановили"; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException) { result = $"сломалось: {ex.Message}"; }
            finally { GameMode.Leave("school"); }
            var c = _cts;
            _cts = null;
            c?.Dispose();
            _status = $"не учусь у ботов (прошлый раз: {result})";
            Log.Write($"школа у ботов: {result}");
            Finished?.Invoke(result);
        }, CancellationToken.None);
        return $"Иду смотреть ботов наблюдателем от первого лица — {limit.TotalMinutes:F0} мин, потом разметка и учёба. Стоп — Backspace/Esc или «стоп».";
    }

    public static string Stop()
    {
        var c = _cts;
        if (c is null) return "Я и так не смотрю ботов.";
        try { c.Cancel(); } catch (ObjectDisposedException) { }
        return "Хватит смотреть ботов — дальше разметка и учёба.";
    }

    private static async Task<string> RunAsync(KlavaConfig cfg, IntPtr hwnd, TimeSpan limit, CsMenu.Plan plan, CancellationToken ct)
    {
        string? stopWhy = null;
        using var watch = RawInput.Subscribe(e =>
        {
            if (!e.Mouse && !e.KeyUp && e.Vk is 0x08 or 0x1B) stopWhy ??= e.Vk == 0x08 ? "друг нажал Backspace" : "друг нажал Esc";
        });
        Uia.Focus(hwnd);
        await Task.Delay(500, ct);
        _status = "ухожу в наблюдатели";
        if (!await SpectateAsync(hwnd, ct)) return "в наблюдатели не вышло (меню команды не открылось)";
        if (!await InEyeAsync(hwnd, ct)) return "вид «от первого лица» не включился";

        var dir = Path.Combine(cfg.Abs(Path.Combine("gamer", "data")), "Counter-Strike", DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + "_bots");
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "idm.txt"), "запись ботов (наблюдатель от первого лица) — нажатия восстановит gamer\\train\\idm.py", ct);
        long frames = 0;
        var seg = 0;
        var clock = Stopwatch.StartNew();
        long lastCheck = 0;
        var pid = Hands.PidOf(hwnd);
        var startTick = Environment.TickCount;
        await using (var csv = new StreamWriter(Path.Combine(dir, "frames.csv"), false, new UTF8Encoding(false)))
        {
            await csv.WriteLineAsync("frame,t_ms,seg,tick");
            var next = 0.0;
            const double step = 1000.0 / GameRecorder.Fps;
            var inEye = true;
            while (!ct.IsCancellationRequested)
            {
                if (clock.Elapsed > limit) break;
                if (stopWhy is not null) break;
                if (!Native.IsWindow(hwnd)) { stopWhy = "окно игры закрылось"; break; }
                next += step;
                var wait = next - clock.Elapsed.TotalMilliseconds;
                if (wait > 1) await Task.Delay((int)wait, ct);
                else if (wait < -step * 4) next = clock.Elapsed.TotalMilliseconds;
                if (!Hands.InFront(pid)) { if (inEye) seg++; inEye = false; continue; }
                // раз в 2 с — тот ли вид (бот умер — камера могла переключиться)
                if (clock.ElapsedMilliseconds - lastCheck > 2000)
                {
                    lastCheck = clock.ElapsedMilliseconds;
                    var ok = await IsInEyeAsync(hwnd);
                    if (!ok)
                    {
                        if (inEye) seg++;
                        inEye = false;
                        await CsMenu.TapAsync(hwnd, 0x20, ct);   // пробел — следующий режим камеры
                        continue;
                    }
                    inEye = true;
                }
                if (!inEye) continue;
                if (GameFrames.Grab(hwnd) is not { } jpeg) continue;
                frames++;
                await File.WriteAllBytesAsync(Path.Combine(dir, $"f{frames:D7}.jpg"), jpeg, ct);
                await csv.WriteLineAsync($"{frames},{clock.ElapsedMilliseconds},{seg},{Environment.TickCount}");
                if (frames % 160 == 0) _status = $"смотрю ботов: {frames / GameRecorder.Fps / 60.0:F1} мин записи";
            }
        }
        var minutes = frames / (double)GameRecorder.Fps / 60;
        _status = "возвращаюсь в команду";
        try { await BackToTeamAsync(hwnd, plan, CancellationToken.None); }
        catch (OperationCanceledException) { }
        if (frames < 200)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            return $"{stopWhy ?? "время вышло"}: записала мало ({frames} кадров) — не учусь на этом";
        }
        // точные нажатия от учителя на сервере (klava_teacher), нет его строк — восстановит нейронка обратной динамики
        var exact = TeacherLabels(cfg, dir, startTick, Environment.TickCount);
        string how;
        if (exact > 0)
        {
            File.Move(Path.Combine(dir, "idm.txt"), Path.Combine(dir, "teacher.txt"));
            how = $"нажатия бота — точные, от учителя на сервере ({exact} кадров)";
        }
        else
        {
            _status = "размечаю записи ботов (обратная динамика)";
            var labeled = await RunPythonAsync(cfg, ["-u", "idm.py", "label", "--game", "Counter-Strike"], cfg.Abs(Path.Combine("gamer", "models", "Counter-Strike", "idm_label.log")));
            if (labeled != 0) return $"{stopWhy ?? "время вышло"}: записала {minutes:F1} мин ботов, но разметка не вышла (нет idm.pt? — gamer\\train\\idm.py train)";
            how = "нажатия бота восстановлены по картинке (учителя на сервере не было — CS запущена не из Клавы)";
        }
        var train = GamerTrainer.Start(cfg, "Counter-Strike", finetune: false);
        return $"{stopWhy ?? "время вышло"}: посмотрела ботов {minutes:F1} мин, {how}; " +
               (train is null ? "учусь на всём в фоне — ноги подхватят новую модель сами" : $"учёбу не запустила: {train}");
    }

    /// <summary>Строка учителя: ход бота на сервере.</summary>
    private readonly record struct Cmd(int Tick, int Bot, int Buttons, float Fmove, float Smove, float Pitch, float Yaw, bool Alive);

    // кнопки GoldSrc (usercmd.buttons)
    private const int InAttack = 1, InJump = 2, InDuck = 4, InForward = 8, InBack = 16, InLeft = 512, InRight = 1024, InAttack2 = 2048, InRun = 4096;

    /// <summary>
    /// Строки учителя (addons\\amxmodx\\logs\\klava_teacher.csv: tick, кнопки, скорости хода, углы бота) за время записи →
    /// actions.csv того же вида, что пишет GameRecorder за другом: метка кадра i — что нажато между кадрами i-1 и i
    /// (зажатое и нажатое мельком), мышь — поворот бота в отсчётах мыши друга (sensitivity × m_yaw из его config.cfg).
    /// Кадры, где бот мёртв или сменился, — новым куском. Возвращает, сколько кадров размечено (0 — строк учителя нет).
    /// </summary>
    private static int TeacherLabels(KlavaConfig cfg, string dir, int startTick, int endTick)
    {
        var log = Path.Combine(cfg.CsDir, "cstrike", "addons", "amxmodx", "logs", "klava_teacher.csv");
        if (!File.Exists(log)) return 0;
        var rows = new List<Cmd>();
        using (var fs = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs))
        {
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                var p = line.Split(',');
                if (p.Length < 9 || !int.TryParse(p[0], out var tick) || tick < startTick - 2000 || tick > endTick + 2000) continue;
                var ci = CultureInfo.InvariantCulture;
                rows.Add(new Cmd(tick, int.Parse(p[2], ci), int.Parse(p[3], ci), float.Parse(p[4], ci), float.Parse(p[5], ci),
                    float.Parse(p[6], ci), float.Parse(p[7], ci), p[8].Trim() == "1"));
            }
        }
        if (rows.Count < 100) return 0;
        rows.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        var (sens, yawK, pitchK) = MouseOf(cfg);
        var frames = File.ReadAllLines(Path.Combine(dir, "frames.csv")).Skip(1).Select(l => l.Split(','))
            .Where(p => p.Length >= 4).Select(p => (Frame: int.Parse(p[0]), Tms: long.Parse(p[1]), Seg: int.Parse(p[2]), Tick: int.Parse(p[3]))).ToList();
        var sb = new StringBuilder("frame,t_ms,seg,keys,dx,dy,lmb,rmb,mmb,wheel,cx,cy\n");
        var labeled = 0;
        var r = 0;
        Cmd? prev = null;
        for (var i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            var from = r;
            while (r < rows.Count && rows[r].Tick <= f.Tick) r++;
            var span = rows.GetRange(from, r - from);
            Cmd? last = r > 0 ? rows[r - 1] : null;
            var ok = last is { } l0 && l0.Alive && f.Tick - l0.Tick < 300 && prev is { } p0 && p0.Bot == l0.Bot && span.All(x => x.Bot == l0.Bot && x.Alive);
            if (!ok || i == 0 || frames[i - 1].Seg != f.Seg)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{f.Frame},{f.Tms},{100000 + i},,0,0,0,0,0,0,,\n");
                prev = last;
                continue;
            }
            var btn = span.Aggregate(last!.Value.Buttons, (a, x) => a | x.Buttons);
            var fm = span.Count > 0 ? span.Average(x => x.Fmove) : last.Value.Fmove;
            var sm = span.Count > 0 ? span.Average(x => x.Smove) : last.Value.Smove;
            var keys = new List<string>();
            if ((btn & InForward) != 0 || fm > 50) keys.Add("W");
            if ((btn & InLeft) != 0 || sm < -50) keys.Add("A");
            if ((btn & InBack) != 0 || fm < -50) keys.Add("S");
            if ((btn & InRight) != 0 || sm > 50) keys.Add("D");
            if ((btn & InJump) != 0) keys.Add("SPACE");
            if ((btn & InDuck) != 0) keys.Add("CTRL");
            if ((btn & InRun) != 0) keys.Add("SHIFT");
            var dyaw = last.Value.Yaw - prev!.Value.Yaw;
            while (dyaw > 180) dyaw -= 360;
            while (dyaw < -180) dyaw += 360;
            var dpitch = last.Value.Pitch - prev.Value.Pitch;
            // вправо — угол рыскания уменьшается; вниз — тангаж растёт (GoldSrc)
            var dx = (int)Math.Round(-dyaw / (sens * yawK));
            var dy = (int)Math.Round(dpitch / (sens * pitchK));
            sb.Append(CultureInfo.InvariantCulture,
                $"{f.Frame},{f.Tms},{f.Seg},{string.Join('+', keys)},{dx},{dy},{((btn & InAttack) != 0 ? 1 : 0)},{((btn & InAttack2) != 0 ? 1 : 0)},0,0,,\n");
            prev = last;
            labeled++;
        }
        if (labeled < 100) return 0;
        File.WriteAllText(Path.Combine(dir, "actions.csv"), sb.ToString(), new UTF8Encoding(false));
        return labeled;
    }

    /// <summary>Мышь друга из его config.cfg: sensitivity, m_yaw, m_pitch (градусов на отсчёт — sens × m_yaw).</summary>
    private static (float Sens, float Yaw, float Pitch) MouseOf(KlavaConfig cfg)
    {
        float sens = 3f, yaw = 0.022f, pitch = 0.022f;
        var file = Path.Combine(cfg.CsDir, "cstrike", "config.cfg");
        if (!File.Exists(file)) return (sens, yaw, pitch);
        foreach (var line in File.ReadLines(file))
        {
            var p = line.Split('"');
            if (p.Length < 2 || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
            switch (p[0].Trim())
            {
                case "sensitivity": sens = v; break;
                case "m_yaw": yaw = v; break;
                case "m_pitch": pitch = Math.Abs(v) > 0 ? v : pitch; break;
            }
        }
        return (sens, yaw, pitch);
    }

    /// <summary>M (меню команды) → 6 «НАБЛЮДЕНИЕ».</summary>
    private static async Task<bool> SpectateAsync(IntPtr hwnd, CancellationToken ct)
    {
        for (var i = 0; i < 3; i++)
        {
            await CsMenu.TapAsync(hwnd, 'M', ct);
            await Task.Delay(700, ct);
            using var frame = CsMenu.Grab(hwnd);
            if (frame is null) continue;
            if ((await CsMenu.ReadAsync(frame)).Kind == CsMenu.Kind.Team)
            {
                await CsMenu.TapAsync(hwnd, '6', ct);
                await Task.Delay(1200, ct);
                return true;
            }
        }
        return false;
    }

    private static async Task<bool> InEyeAsync(IntPtr hwnd, CancellationToken ct)
    {
        for (var i = 0; i < 7; i++)
        {
            if (await IsInEyeAsync(hwnd)) return true;
            await CsMenu.TapAsync(hwnd, 0x20, ct);   // ПРЫЖОК — смена режима наблюдения
            await Task.Delay(700, ct);
        }
        return false;
    }

    /// <summary>Подпись режима наблюдателя: «От первого лица» / «Режим камеры от первого лица».</summary>
    private static async Task<bool> IsInEyeAsync(IntPtr hwnd)
    {
        using var frame = CsMenu.Grab(hwnd);
        if (frame is null) return false;
        var lines = await CsMenu.ReadLinesAsync(frame);
        return lines.Any(l => l.Norm.Contains("первого лица", StringComparison.Ordinal));
    }

    /// <summary>Из наблюдателей — в свою команду (M → 1/2/5 → класс).</summary>
    private static async Task BackToTeamAsync(IntPtr hwnd, CsMenu.Plan plan, CancellationToken ct)
    {
        if (!Hands.InFront(Hands.PidOf(hwnd))) Uia.Focus(hwnd);
        await CsMenu.TapAsync(hwnd, 'M', ct);
        await Task.Delay(700, ct);
        await CsMenu.EnterGameAsync(hwnd, plan, newGame: false, Log.Write, ct, maxSeconds: 30);
    }

    /// <summary>KlavaDesk.exe --bot-school [секунд] — школа у ботов без Клавы (CS должна быть открыта, в игре). Заберёт клаву.</summary>
    public static int LiveTest(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(Path.Combine("data", "logs", "bot_school.log")));
        var sec = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 120;
        var done = new ManualResetEventSlim();
        var result = "";
        Finished += r => { result = r; done.Set(); };
        Console.WriteLine(Start(cfg, sec, new CsMenu.Plan(Team: "t")));
        if (!Running && !done.IsSet) return 1;
        while (!done.Wait(5000)) Console.WriteLine($"  {Status()}");
        Console.WriteLine($"ИТОГ: {result}");
        return 0;
    }

    private static async Task<int> RunPythonAsync(KlavaConfig cfg, string[] args, string logFile)
    {
        var gamer = cfg.Abs("gamer");
        var psi = new ProcessStartInfo(Path.Combine(gamer, ".venv", "Scripts", "python.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.Combine(gamer, "train"),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        using var p = Process.Start(psi);
        if (p is null) return -1;
        try { Native.TieToLifetime(p); } catch (InvalidOperationException) { }
        var output = p.StandardOutput.ReadToEndAsync();
        var errors = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        try { await File.WriteAllTextAsync(logFile, await output + await errors); } catch (IOException) { }
        return p.ExitCode;
    }
}
