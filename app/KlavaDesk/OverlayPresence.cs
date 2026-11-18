using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Кто в комнате (Presence) и игра, которую она смотрит вместе с другом (GameWatch): инструменты room / game_watch,
/// автозапуск наблюдения за знакомой игрой и её короткие реплики по ходу игры.
/// </summary>
internal sealed partial class OverlayForm
{
    private List<string>? _knownPeople;
    private DateTime _knownPeopleAt = DateTime.MinValue;

    /// <summary>Все, кого она знает по голосу или по компании (имена профилей), кроме неё самой. Кэш на минуту.</summary>
    private List<string> KnownPeople()
    {
        if (_knownPeople is not null && DateTime.UtcNow - _knownPeopleAt < TimeSpan.FromMinutes(1)) return _knownPeople;
        var names = new List<string>(_cfg.RoomMembers.Where(m => !string.IsNullOrWhiteSpace(m)));
        var dir = _cfg.Abs(@"data\speakers");
        try
        {
            if (Directory.Exists(dir))
                names.AddRange(Directory.GetDirectories(dir).Select(d => Path.GetFileName(d) ?? "").Where(n => n.Length > 0 && !n.StartsWith('_')));
        }
        catch (IOException) { }
        _knownPeople = names.Where(n => n != SelfVoiceName).Distinct(StringComparer.Ordinal).ToList();
        _knownPeopleAt = DateTime.UtcNow;
        return _knownPeople;
    }

    /// <summary>Отмеченные ушедшими — строкой для контекста: «Лис (на работу ушёл, с 11:48)».</summary>
    private string? AwayText()
    {
        var away = _presence.AwayList();
        if (away.Count == 0) return null;
        return string.Join(", ", away.Select(a => $"{HumanName(a.Profile)} ({a.A.Why}, с {a.A.Since.ToLocalTime():HH:mm})"));
    }

    /// <summary>Слова людей «Лис ушёл», «Паши нет», «Лис вернулся», перекличка — в отметки присутствия.</summary>
    private void HearPresence(string text, string? who)
    {
        var changes = _presence.Hear(text, who, KnownPeople());
        if (changes.Count == 0) return;
        foreach (var (p, _) in _presence.AwayList()) _lastHeard.Remove(p);
        Log.Write($"присутствие: по словам {(who is null ? "кого-то" : HumanName(who))} — {string.Join(", ", changes)}");
    }

    /// <summary>Инструмент room: away | here | list.</summary>
    private string RoomTool(string action, JsonObject args)
    {
        string Arg(string n) => args[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        var known = KnownPeople();
        var act = action.Trim().ToLowerInvariant();
        if (act is "away" or "here")
        {
            var name = Arg("name");
            var p = Presence.Resolve(name, known)
                    ?? known.FirstOrDefault(k => HumanName(k).Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? known.FirstOrDefault(k => name.Length >= 3 && HumanName(k).Contains(name, StringComparison.OrdinalIgnoreCase));
            if (p is null) return $"[НЕ СРАБОТАЛО] Не знаю «{name}». Знаю по голосу: {string.Join(", ", known.Select(HumanName))}.";
            if (act == "away")
            {
                _presence.SetAway(p, Arg("why") is { Length: > 0 } why ? why : "сказали, что ушёл");
                _lastHeard.Remove(p);
                return $"Отметила: {HumanName(p)} нет в комнате. Чужие фразы его именем больше не подписываю — говори с теми, кто здесь.";
            }
            _presence.SetHere(p, Arg("why") is { Length: > 0 } back ? back : "сказали, что вернулся");
            _lastHeard[p] = DateTime.UtcNow;
            return $"Отметила: {HumanName(p)} снова в комнате.";
        }
        var here = ActiveMembers().Where(m => _lastHeard.TryGetValue(m, out var t) && DateTime.UtcNow - t < TimeSpan.FromMinutes(25)).Select(HumanName).ToList();
        return $"Ушли: {AwayText() ?? "никто не отмечен"}. Слышно за последние 25 минут: {(here.Count > 0 ? string.Join(", ", here) : "никого")}.";
    }

    // ---------------- игра вместе с другом ----------------

    private static readonly HashSet<string> BrowserProcs = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "browser", "yandex", "brave", "vivaldi", "steamwebhelper", "steam", "explorer", "Code", "devenv", "notepad",
    };
    private IntPtr _gameDeclined;          // окно игры, за которой сказали не смотреть — сама не включаю
    private IntPtr _gameFgCache;
    private string _gameFgProc = "";

    /// <summary>Раз в 3 с: впереди знакомая игра (Бакшот, Detroit, DOOM 3) — начать смотреть её вместе с другом.</summary>
    private void GameTick()
    {
        if (!_ready || !_cfg.GameWatchAuto) return;
        var h = Native.GetForegroundWindow();
        if (h == IntPtr.Zero || h == Handle || h == _gameDeclined) return;
        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid == Environment.ProcessId) return;
        if (h != _gameFgCache)
        {
            _gameFgCache = h;
            try { using var p = Process.GetProcessById((int)pid); _gameFgProc = p.ProcessName; }
            catch (ArgumentException) { _gameFgProc = ""; }
        }
        // браузер с гайдом «doom 3 locker codes» и страница игры в Steam — не сама игра; а вот видео во вкладке — смотрим
        if (BrowserProcs.Contains(_gameFgProc)) { VideoTick(h); return; }
        var title = Uia.WindowTitle(h);
        if (GameWatch.Detect(title, _gameFgProc) is not { } game) return;
        if (_gameWatch.Active && _gameWatch.Title == game.Title)
        {
            // смотрю «эту игру», но не это окно (запустили до игры, игру перезапустили) — перецепиться на настоящее
            if (_gameWatch.Hwnd != h) _gameWatch.Rebind(h, title);
            return;
        }
        _gameWatch.Start(game, h, title);
    }

    private DateTime _videoCheckAt = DateTime.MinValue;
    private volatile bool _videoChecking;
    private int _videoMiss;

    /// <summary>
    /// Браузер впереди: раз в 5 с — адрес активной вкладки (UI Automation, в фоне). Видео (YouTube, Twitch, Rutube, VK
    /// Видео…) — смотреть вместе (GameWatch.Video); ушли со страницы видео на 15 с — перестать. Заголовок окна не годится:
    /// у Opera GX он «Opera GX Internet Browser», без названия страницы.
    /// </summary>
    private void VideoTick(IntPtr h)
    {
        if (_videoChecking || DateTime.UtcNow - _videoCheckAt < TimeSpan.FromSeconds(5)) return;
        if (_gameWatch.Active && !_gameWatch.IsVideo) return;   // смотрит игру — браузер с гайдом её не сбивает
        _videoChecking = true;
        _videoCheckAt = DateTime.UtcNow;
        _ = Task.Run(async () =>
        {
            try
            {
                var (url, title, _) = await Uia.BrowserTabAsync(TimeSpan.FromSeconds(2));
                var video = IsVideoPage(url, title);
                BeginInvoke(() => ApplyVideoTick(h, video, title));
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception
                                           or System.Windows.Automation.ElementNotAvailableException)
            {
                Log.Write($"видео: адрес вкладки не прочитался ({ex.Message})");
            }
            finally { _videoChecking = false; }
        });
    }

    private void ApplyVideoTick(IntPtr h, bool video, string title)
    {
        if (IsDisposed || !_cfg.GameWatchAuto) return;
        if (video)
        {
            _videoMiss = 0;
            if (_gameWatch.IsVideo) { if (_gameWatch.Hwnd != h) _gameWatch.Rebind(h, title); return; }
            if (!_gameWatch.Active && h != _gameDeclined) _gameWatch.Start(GameWatch.Video, h, title);
            return;
        }
        if (_gameWatch.IsVideo && _gameWatch.Hwnd == h && ++_videoMiss >= 3) _gameWatch.Stop("со страницы видео ушли");
    }

    /// <summary>Вкладка — видео: адрес плеера (watch, live, shorts, стрим) или заголовок с названием видеосервиса.</summary>
    internal static bool IsVideoPage(string? url, string title) =>
        url is not null && System.Text.RegularExpressions.Regex.IsMatch(url,
            @"youtube\.com/(watch|live|shorts)|youtu\.be/|twitch\.tv/(videos/|[A-Za-z0-9_]{3,}/?$)|rutube\.ru/(video|live)|vk\.com/video|vkvideo\.ru/video|" +
            @"ok\.ru/video|dzen\.ru/video|vimeo\.com/\d|kinopoisk\.ru/(film|series)/.+/watch|kick\.com/[A-Za-z0-9_]+", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
        || System.Text.RegularExpressions.Regex.IsMatch(title, @"[-—–]\s*(YouTube|Twitch|RUTUBE|VK Видео)\s*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Инструмент game_watch: start | stop | status | history | fix.</summary>
    private string GameWatchTool(string action, JsonObject args)
    {
        string Arg(string n) => args[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        switch (action.Trim().ToLowerInvariant())
        {
            case "start" or "on":
            {
                var name = Arg("game");
                var profile = name.Length > 0 ? GameWatch.ById(name) : null;
                var h = IntPtr.Zero;
                // видео — в окне браузера, а не в окне по имени «видео»
                if (profile?.Id == "video")
                {
                    h = Uia.BrowserWindow();
                    if (h == IntPtr.Zero) return "[НЕ СРАБОТАЛО] Не вижу окна браузера с видео — пусть друг выведет его вперёд.";
                    _gameDeclined = IntPtr.Zero;
                    return _gameWatch.Start(profile, h, Uia.WindowTitle(h));
                }
                if (profile is not null) h = Uia.FindWindow(profile.Title);
                if (h == IntPtr.Zero && name.Length > 0) h = Uia.FindWindow(name);
                // знакомая игра ещё не открыта — ждать её окно, а не брать то, что впереди (Steam, браузер)
                if (h == IntPtr.Zero && profile is not null)
                {
                    _gameDeclined = IntPtr.Zero;
                    return _gameWatch.Start(profile, IntPtr.Zero, profile.Title);
                }
                if (h == IntPtr.Zero)
                {
                    var fg = Native.GetForegroundWindow();
                    Native.GetWindowThreadProcessId(fg, out var pid);
                    if (fg != IntPtr.Zero && pid != Environment.ProcessId) h = fg;
                }
                if (h == IntPtr.Zero) return "[НЕ СРАБОТАЛО] Не вижу окна игры — пусть друг откроет её и выведет вперёд.";
                var title = Uia.WindowTitle(h);
                profile ??= GameWatch.Detect(title, "");
                _gameDeclined = IntPtr.Zero;
                return _gameWatch.Start(profile, h, title);
            }
            case "stop" or "off":
                _gameDeclined = Native.GetForegroundWindow();
                return _gameWatch.Stop();
            case "history":
                return _gameWatch.History(Arg("day"));
            case "fix":
                return _gameWatch.Fix(args);
            default:
                return _gameWatch.Status();
        }
    }

    /// <summary>Наблюдатель игры хочет сказать (из своего потока): на UI-поток, там решаем.</summary>
    private bool SayFromGame(string text)
    {
        if (IsDisposed || !IsHandleCreated) return false;
        try { return (bool)Invoke(new Func<bool>(() => OnGameSay(text))); }
        catch (Exception ex) when (ex is ObjectDisposedException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Короткая реплика по игре (патроны показали, монстр, код) — только если она свободна и друг не говорит прямо сейчас;
    /// иначе наблюдатель повторит, пока реплика не устарела.
    /// </summary>
    private bool OnGameSay(string text)
    {
        if (!_ready || _switchBlocks || IsBusy || SongPlaying || _ears.PttRecording || _hands.Active || _profilesRecording || Hushed) return false;
        if (DateTime.UtcNow - _lastTalk < TimeSpan.FromSeconds(3)) return false;
        lock (_batchGate) if (_tempBatch.Count > 0 || _pendingFlush) return false;
        Log.Write($"игра: говорю сама — {text}");
        _ = ConverseAsync(text);
        return true;
    }
}
