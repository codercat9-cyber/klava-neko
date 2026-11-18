using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Мост в Counter-Strike 1.6. Играть «смотрю в экран — думаю — жму WASD» в шутере нельзя: мозг думает секунды.
/// Поэтому Клава заходит отдельным игроком — встроенным ботом CS (ZBot: реакция и прицел как у живого) с приставкой
/// «Klava|» в нике, а сама комментирует голосом и командует.
///
/// Без Metamod и без правки файлов игры: сборка друга при каждом запуске откатывает свои liblist.gam и mp.dll, так что
/// встроиться туда нельзя. Вместо этого — только свои конфиги в cstrike:
///   userconfig.cfg — строка «bind F11 exec klava_cmd.cfg» (клиент выполняет его при каждом старте);
///   listenserver.cfg — журнал сервера (log on, сброс после каждой строки), когда друг поднимает свою игру;
///   klava_cmd.cfg — команды Клавы (её бот, противники, закуп, кик): пишем файл и жмём F11 в окне игры.
/// События — хвост журнала сервера cstrike\logs\*.log: убийства (с хедшотом), победы раундов со счётом, бомба,
/// кто за какую команду, её ранения. Наружу — те же строки, что понимает OverlayForm.OnCsLine.
/// </summary>
internal static partial class CsBridge
{
    public const string Prefix = "Klava|";
    private const ushort VkF11 = 0x7A;

    private static CancellationTokenSource? _cts;
    private static KlavaConfig? _cfg;
    private static volatile bool _tailing;
    private static readonly object Gate = new();
    private static readonly List<(string Prefix, TaskCompletionSource<string> Tcs)> Waiters = [];

    // состояние игры по журналу
    private static string? _klava;       // полное имя её бота («Klava|Rocky»)
    private static string _klavaTeam = "";
    private static string _hostTeam = "";
    private static int _scoreT, _scoreCT, _kills, _deaths, _hp = 100;
    private static bool _alive;

    // живой игрок на этом компе (друг; им же Клава играет лапками): жив ли, кто убил — чтобы после смерти она не
    // «бежала» мёртвой и не думала, что уже возродилась
    private static string _host = "";
    private static bool _hostAlive = true;
    private static int _hostHp = 100;
    private static DateTime _hostDiedAt = DateTime.MinValue;
    private static string _hostKilledBy = "";

    /// <summary>Строка-событие для Клавы (формат — см. OverlayForm.OnCsLine) и «disconnected», когда игра закрылась.</summary>
    public static event Action<string>? Line;

    /// <summary>Игра идёт и журнал её сервера читается.</summary>
    public static bool Connected => _tailing && Running;

    /// <summary>Процесс игры (hl.exe) запущен — журнал может быть ещё не включён.</summary>
    public static bool Running
    {
        get
        {
            using var hl = HlProcess();
            return hl is not null;
        }
    }

    /// <summary>Карта: из журнала («Started map») или та, с которой её запустила сама Клава (cs start).</summary>
    public static string Map { get; set; } = "";

    public static void Start(KlavaConfig cfg)
    {
        lock (Gate)
        {
            if (_cts is not null) return;
            _cfg = cfg;
            _cts = new CancellationTokenSource();
        }
        EnsureConfigs(cfg);
        var token = _cts.Token;
        _ = Task.Run(() => TailLoopAsync(cfg, token));
        Log.Write("мост CS: слежу за журналом сервера игры");
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    /// <summary>
    /// Ключи запуска — ровно как в рабочем ярлыке друга («Counter-Strike 1.6 RAZER»: рендер -dx12), плюс своя игра с
    /// ботами. Размер и режим окна берутся из его настроек игры (окно развёрнуто — Клава видна поверх). Без -dx12, на
    /// OpenGL окном без рамок во весь монитор, эта сборка висела на заставке RAZER; свои +exec — тоже.
    /// </summary>
    public static string LaunchArgs(string map) => $"{MenuArgs} +sv_lan 1 +maxplayers 16 +map {map}";

    /// <summary>
    /// Как ярлык друга, без карты: игра открывается в главном меню — дальше Клава сама (CsMenu, «Новая игра»). Плюс
    /// учитель для школы у ботов: Metamod ключом -dll (сборка откатывает liblist.gam и mp.dll при запуске — ключ файлы не
    /// трогает) и свои списки плагинов через +localinfo — грузится только AMX Mod X с klava_teacher (пишет нажатия бота,
    /// за которым она смотрит наблюдателем); зомби-мод и прочие плагины сборки не грузятся, игра обычная.
    /// </summary>
    public const string MenuArgs = "-game cstrike -noforcemparms -noforcemaccel -stretchaspect -dx12 " +
        "-dll addons\\metamod\\dlls\\metamod.dll +localinfo mm_pluginsfile addons/metamod/klava_plugins.ini " +
        "+localinfo amxx_cfg addons/amxmodx/klava/core.ini";

    /// <summary>
    /// Дописать свои строки в конфиги, которые игра выполняет сама и которые сборка не запирает: userconfig.cfg (клиент,
    /// при каждом старте — через config.cfg) — F11 на её команды; listenserver.cfg (когда поднимается своя игра) —
    /// журнал сервера со сбросом после каждой строки (без sv_logflush он писался пачками по 8 КБ, и события опаздывали).
    /// autoexec.cfg у сборки только для чтения, liblist.gam и mp.dll она откатывает при запуске.
    /// </summary>
    private static void EnsureConfigs(KlavaConfig cfg)
    {
        var dir = Path.Combine(cfg.CsDir, "cstrike");
        if (!Directory.Exists(dir)) return;
        Append(Path.Combine(dir, "userconfig.cfg"), "bind \"F11\" \"exec klava_cmd.cfg\"");
        Append(Path.Combine(dir, "listenserver.cfg"), "log on", "sv_logflush 1", "mp_logdetail 3", "mp_logmessages 1");
    }

    private static void Append(string file, params string[] lines)
    {
        try
        {
            var text = File.Exists(file) ? File.ReadAllText(file, Encoding.ASCII) : "";
            var missing = lines.Where(l => !text.Contains(l, StringComparison.OrdinalIgnoreCase)).ToList();
            if (missing.Count == 0) return;
            var head = text.Length > 0 && !text.EndsWith('\n') ? "\r\n" : "";
            File.AppendAllText(file, head + "// Klava (KlavaDesk)\r\n" + string.Join("\r\n", missing) + "\r\n", Encoding.ASCII);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error($"мост CS: {Path.GetFileName(file)}", ex); }
    }

    private static Process? HlProcess()
    {
        var all = Process.GetProcessesByName("hl");
        Process? keep = null;
        foreach (var p in all)
        {
            if (keep is null && !p.HasExited) keep = p;
            else p.Dispose();
        }
        return keep;
    }

    private static async Task TailLoopAsync(KlavaConfig cfg, CancellationToken ct)
    {
        var logs = Path.Combine(cfg.CsDir, "cstrike", "logs");
        string? file = null;
        long pos = 0;
        var partial = "";
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(300, ct); }
            catch (OperationCanceledException) { return; }
            using var hl = HlProcess();
            if (hl is null)
            {
                if (_tailing) { _tailing = false; Reset(); Raise("disconnected"); }
                file = null;
                continue;
            }
            DateTime started;
            try { started = hl.StartTime; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
            // журнал этой игры: созданный после её запуска (смена карты — новый файл)
            string? newest = null;
            try
            {
                if (Directory.Exists(logs))
                    newest = new DirectoryInfo(logs).EnumerateFiles("*.log")
                        .Where(f => f.CreationTime >= started.AddSeconds(-5) || f.LastWriteTime >= started.AddSeconds(5))
                        .OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName;
            }
            catch (IOException) { continue; }
            if (newest is null) continue;
            if (newest != file)
            {
                file = newest;
                pos = 0;
                partial = "";
                if (!_tailing) { _tailing = true; Log.Write($"мост CS: читаю {Path.GetFileName(file)}"); }
            }
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length < pos) pos = 0;
                if (fs.Length == pos) continue;
                fs.Seek(pos, SeekOrigin.Begin);
                var buf = new byte[fs.Length - pos];
                var n = await fs.ReadAsync(buf, ct);
                pos += n;
                var text = partial + Encoding.UTF8.GetString(buf, 0, n);
                var lines = text.Split('\n');
                partial = lines[^1];
                foreach (var l in lines[..^1]) Parse(l.TrimEnd('\r'));
            }
            catch (IOException) { }
        }
    }

    private static void Reset()
    {
        _klava = null;
        _klavaTeam = _hostTeam = Map = "";
        _scoreT = _scoreCT = _kills = _deaths = 0;
        _hp = 100;
        _alive = false;
        _host = _hostKilledBy = "";
        _hostAlive = true;
        _hostHp = 100;
        _hostDiedAt = DateTime.MinValue;
    }

    /// <summary>Когда убили живого игрока этого компа (MinValue — не убит в этом раунде).</summary>
    public static DateTime HostDiedAt => _hostAlive ? DateTime.MinValue : _hostDiedAt;

    /// <summary>
    /// Состояние игрока этого компа для Клавы, которая им играет лапками: жив (HP) или мёртв до следующего раунда и кем
    /// убит. null — журнала игры нет.
    /// </summary>
    public static string? HostState() => Connected ? HostText() : null;

    private static string? HostText()
    {
        if (_host.Length == 0) return null;
        var score = $"счёт террористы {_scoreT} : спецназ {_scoreCT}";
        return _hostAlive
            ? $"CS: твой игрок ({_host}{(_hostTeam.Length > 0 ? $", {(_hostTeam == "T" ? "террористы" : "спецназ")}" : "")}) жив, {_hostHp} HP; {score}."
            : $"CS: твой игрок ({_host}) МЁРТВ с {_hostDiedAt:HH:mm:ss} — {_hostKilledBy}. До начала следующего раунда ты не бегаешь и не стреляешь: " +
              $"на экране камера смерти или чужой игрок. Жди нового раунда (возродишься на респе), потом закуп и в бой. {score}.";
    }

    // «L 09/27/2026 - 06:50:12: …» — отрезаем время
    [GeneratedRegex(@"^L \d\d/\d\d/\d{4} - \d\d:\d\d:\d\d: ")]
    private static partial Regex StampRx();
    // «"Имя<3><BOT><CT>"» — игрок: имя, uid, steamid (BOT у ботов), команда
    private const string Pl = @"""(?<{0}>.+?)<\d+><(?<{0}id>[^>]*)><(?<{0}t>[^>]*)>""";
    private static readonly Regex KillRx = new("^" + string.Format(Pl, "k") + " killed " + string.Format(Pl, "v") + @" with ""(?<w>[^""]+)""(?<hs> \(headshot\))?", RegexOptions.Compiled);
    private static readonly Regex JoinRx = new("^" + string.Format(Pl, "p") + @" joined team ""(?<team>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex TeamRx = new(@"^Team ""(?<team>[^""]+)"" triggered ""(?<ev>[^""]+)"" \(CT ""(?<ct>\d+)""\) \(T ""(?<t>\d+)""\)", RegexOptions.Compiled);
    private static readonly Regex TrigRx = new("^" + string.Format(Pl, "p") + @" triggered ""(?<ev>[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex HitRx = new("^" + string.Format(Pl, "a") + " attacked " + string.Format(Pl, "v") + @" with ""[^""]+"" \(damage ""\d+""\) \(damage_armor ""\d+""\) \(health ""(?<hp>-?\d+)""\)", RegexOptions.Compiled);
    private static readonly Regex GoneRx = new("^" + string.Format(Pl, "p") + " disconnected", RegexOptions.Compiled);
    private static readonly Regex MapRx = new(@"^Started map ""(?<m>[^""]+)""", RegexOptions.Compiled);

    private static bool IsKlava(string name) => name.StartsWith(Prefix, StringComparison.Ordinal);
    private static string Team(string t) => t switch { "TERRORIST" => "T", "CT" => "CT", "SPECTATOR" => "SPEC", _ => t };

    /// <summary>Одна строка журнала → событие (или ничего).</summary>
    public static void Parse(string raw)
    {
        var line = StampRx().Replace(raw, "");
        Match m;
        if ((m = MapRx.Match(line)).Success)
        {
            Map = m.Groups["m"].Value;
            Raise($"hello {Map}");
        }
        else if ((m = KillRx.Match(line)).Success)
        {
            string k = m.Groups["k"].Value, v = m.Groups["v"].Value;
            var role = IsKlava(k) && !IsKlava(v) ? "killer" : IsKlava(v) ? "victim" : "none";
            if (role == "killer") _kills++;
            if (role == "victim") { _deaths++; _alive = false; }
            var kh = m.Groups["kid"].Value != "BOT" ? 1 : 0;
            var vh = m.Groups["vid"].Value != "BOT" ? 1 : 0;
            Raise($"kill {role} {(m.Groups["hs"].Success ? 1 : 0)} {m.Groups["w"].Value} {kh} {vh}|{Show(k)}|{Show(v)}");
            if (kh == 1 && vh == 0)
            {
                _host = k;
                Raise($"host_kill {m.Groups["w"].Value}{(m.Groups["hs"].Success ? " hs" : "")}|{Show(v)}");
            }
            if (vh == 1)
            {
                _host = v;
                _hostAlive = false;
                _hostHp = 0;
                _hostDiedAt = DateTime.Now;
                _hostKilledBy = $"убил {Show(k)} из {m.Groups["w"].Value}{(m.Groups["hs"].Success ? " в голову" : "")}";
                Raise($"host_dead {m.Groups["w"].Value}|{Show(k)}");
            }
        }
        else if ((m = HitRx.Match(line)).Success)
        {
            if (IsKlava(m.Groups["v"].Value) && int.TryParse(m.Groups["hp"].Value, out var hp))
            {
                _hp = Math.Max(0, hp);
                if (hp > 0) Raise($"hurt {hp}");
            }
            else if (m.Groups["vid"].Value != "BOT" && int.TryParse(m.Groups["hp"].Value, out var hostHp))
            {
                _host = m.Groups["v"].Value;
                _hostHp = Math.Max(0, hostHp);
            }
        }
        else if ((m = JoinRx.Match(line)).Success)
        {
            var name = m.Groups["p"].Value;
            var team = Team(m.Groups["team"].Value);
            if (IsKlava(name))
            {
                _klava = name;
                _klavaTeam = team;
                _alive = true;
                _hp = 100;
                Raise($"joined {team}");
            }
            else if (m.Groups["pid"].Value != "BOT")
            {
                _hostTeam = team;
                _host = name;
            }
        }
        else if ((m = TeamRx.Match(line)).Success)
        {
            _scoreCT = int.Parse(m.Groups["ct"].Value);
            _scoreT = int.Parse(m.Groups["t"].Value);
            if (m.Groups["ev"].Value == "Target_Bombed") Raise("bomb exploded");
            Raise($"score {_scoreT} {_scoreCT}");
        }
        else if ((m = TrigRx.Match(line)).Success)
        {
            switch (m.Groups["ev"].Value)
            {
                case "Planted_The_Bomb": Raise("bomb planted"); break;
                case "Defused_The_Bomb": Raise("bomb defused"); break;
            }
        }
        else if (line.StartsWith("World triggered \"Round_Start\"", StringComparison.Ordinal))
        {
            if (_klava is not null) { _alive = true; _hp = 100; }
            _hostAlive = true;
            _hostHp = 100;
            Raise("round_start");
        }
        else if ((m = GoneRx.Match(line)).Success && IsKlava(m.Groups["p"].Value))
        {
            _klava = null;
            _klavaTeam = "";
            Raise("left");
        }
    }

    /// <summary>Имя для реплик: её бот — просто «Klava».</summary>
    private static string Show(string name) => IsKlava(name) ? "Klava" : name;

    private static void Raise(string line)
    {
        lock (Waiters)
            for (var i = Waiters.Count - 1; i >= 0; i--)
                if (line.StartsWith(Waiters[i].Prefix, StringComparison.Ordinal)) { Waiters[i].Tcs.TrySetResult(line); Waiters.RemoveAt(i); }
        try { Line?.Invoke(line); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { Log.Error("мост CS: событие", ex); }
    }

    /// <summary>
    /// Выполнить консольные команды в игре: пишем klava_cmd.cfg и жмём F11 (привязан к exec). Окно игры должно быть
    /// впереди — фокус у друга не отнимаем, ждём до 60 с. false — не дождались.
    /// </summary>
    public static async Task<bool> RunAsync(params string[] commands)
    {
        if (_cfg is null) return false;
        var file = Path.Combine(_cfg.CsDir, "cstrike", "klava_cmd.cfg");
        await File.WriteAllTextAsync(file, string.Join("\r\n", commands) + "\r\n", Encoding.ASCII);
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            using var hl = HlProcess();
            if (hl is null) return false;
            var fg = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(fg, out var pid);
            if (pid == hl.Id)
            {
                Hands.GameKey(VkF11, true);
                await Task.Delay(40);
                Hands.GameKey(VkF11, false);
                Log.Write($"мост CS: команды в игру — {string.Join("; ", commands)}");
                return true;
            }
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>Дождаться события с таким началом (например «joined») — или null за timeout.</summary>
    public static async Task<string?> WaitAsync(string prefix, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Waiters) Waiters.Add((prefix, tcs));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        lock (Waiters) Waiters.RemoveAll(w => w.Tcs == tcs);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    /// <summary>Дождаться окна игры (hl.exe с главным окном) — true, если появилось за timeout.</summary>
    public static async Task<bool> WaitWindowAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            using (var hl = HlProcess())
            {
                hl?.Refresh();
                if (hl is not null && hl.MainWindowHandle != IntPtr.Zero) return true;
            }
            await Task.Delay(500);
        }
        return false;
    }

    /// <summary>Сложность ZBot из «скилла» 1–100: 0 — лёгкий … 3 — эксперт.</summary>
    public static int Difficulty(int skill) => skill <= 0 ? 3 : skill < 35 ? 0 : skill < 60 ? 1 : skill < 85 ? 2 : 3;

    /// <summary>Её бот уже в игре — полное имя; команда хозяина (T/CT) — по журналу.</summary>
    public static string? KlavaName => _klava;
    public static string HostTeam => _hostTeam;

    public static string Status()
    {
        var map = Map.Length > 0 ? $"Карта {Map}, " : "";
        return _klava is null
            ? $"{map}счёт террористы {_scoreT} : спецназ {_scoreCT}. Тебя в игре нет."
            : $"{map}счёт террористы {_scoreT} : спецназ {_scoreCT}. Ты за {(_klavaTeam == "T" ? "террористов" : "спецназ")}: фрагов {_kills}, смертей {_deaths}, {(_alive ? $"жива, {_hp} HP" : "сейчас мертва")}.";
    }

    /// <summary>KlavaDesk.exe --cs-parse журнал.log — прогнать журнал сервера через разбор и напечатать события.</summary>
    public static int ParseTest(string file)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Line += l => Console.WriteLine($"  → {l}");
        foreach (var l in File.ReadLines(file))
        {
            Parse(l);
            if (l.Contains("killed", StringComparison.Ordinal) || l.Contains("Round_Start", StringComparison.Ordinal))
                Console.WriteLine($"     игрок компа: {HostText() ?? "—"}");
        }
        Console.WriteLine(Status());
        return 0;
    }
}
