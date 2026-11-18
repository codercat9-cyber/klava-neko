using System.Diagnostics;

namespace KlavaDesk;

/// <summary>
/// Переключатели Клавы одним местом: их зовут и меню в трее, и панель настроек (она живёт в своём потоке и приходит
/// сюда через <see cref="Ui"/>). Каждый — меняет настройку, сохраняет klava.json, ставит галочку в меню и говорит пузырём.
/// </summary>
internal sealed partial class OverlayForm
{
    /// <summary>Выполнить на потоке окна Клавы, не дожидаясь (панель не должна висеть, пока Клава занята).</summary>
    public void Ui(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); }
        catch (InvalidOperationException) { }   // окно уже закрывается
    }

    /// <summary>Уже открытое окно: развернуть, если свёрнуто, показать и поднять (Activate свёрнутое не разворачивал).</summary>
    private static void Reveal(Form f)
    {
        if (f.WindowState == FormWindowState.Minimized) f.WindowState = FormWindowState.Normal;
        if (!f.Visible) f.Show();
        f.BringToFront();
        f.Activate();
    }

    /// <summary>Панель настроек: одна на всех, в своём потоке окон — не тормозит, пока Клава думает или говорит.</summary>
    public void ShowControlPanel()
    {
        var icon = MakeTrayIcon();
        ControlPanelHost.Show(() => new ControlPanelForm(this, _cfg, _ears, _services, _speaker, icon));
    }

    /// <summary>Галочки меню — по настройкам (панель могла их поменять).</summary>
    private void SyncMenuChecks()
    {
        _handsFreeItem.Checked = _cfg.HandsFree;
        _muteItem.Checked = _cfg.Muted;
        _echoItem.Checked = _cfg.EchoBlock;
        _autopilotItem.Checked = _cfg.Autopilot;
        _watchItem.Checked = _cfg.WatchScreen;
        _streamerItem.Checked = _cfg.GameStreamerMode;
        _clickThroughItem.Checked = _cfg.GameClickThrough;
        _overFullscreenItem.Checked = _cfg.OverFullscreen;
        foreach (var it in _modeMenu.DropDownItems.OfType<ToolStripMenuItem>()) it.Checked = (string?)it.Tag == _cfg.PersonaMode;
        UpdateBrainMenu();
        SyncWorkMenu();
    }

    public void SetHandsFree(bool on)
    {
        _cfg.HandsFree = on;
        _handsFreeItem.Checked = on;
        _ears.HandsFree = on;
        _cfg.Save();
        Bubble(on ? "Слушаю тебя всегда, мур!" : "Теперь зажимай правый Ctrl, когда говоришь.");
    }

    public void SetMuted(bool on)
    {
        _cfg.Muted = on;
        _muteItem.Checked = on;
        Post(new { type = "bell", volume = on ? 0 : 0.6 });   // колокольчик на чокере — вместе с голосом
        Post(new { type = "subtitles", on = on || _cfg.Subtitles });   // без звука текст — единственный голос
        _cfg.Save();
    }

    public void SetSubtitles(bool on)
    {
        _cfg.Subtitles = on;
        _subtitlesItem.Checked = on;
        _cfg.Save();
        Post(new { type = "subtitles", on = on || _cfg.Muted });   // речь (say) показывает текст на странице — ей и флаг
    }

    public void SetEchoBlock(bool on)
    {
        _cfg.EchoBlock = on;
        _echoItem.Checked = on;
        _cfg.Save();
        UpdateDeaf();
        Bubble(on
            ? "Пока говорю — микрофон глухой (не подслушиваю себя). Перебить: правый Ctrl."
            : "Слушаю всегда, могу услышать своё эхо из колонок.");
    }

    public void SetAutopilot(bool on)
    {
        _cfg.Autopilot = on;
        _autopilotItem.Checked = on;
        if (_brain.Toolbox is { } tb) tb.Autopilot = on;
        _cfg.Save();
        Bubble(on
            ? "Автопилот включён — читаю, кодю и разгребаю без спроса. Опасное всё равно спрошу."
            : "Автопилот выключен — на каждый чих буду спрашивать разрешение.");
    }

    public void SetWatchScreen(bool on)
    {
        _cfg.WatchScreen = on;
        _watchItem.Checked = on;
        _cfg.Save();
        Bubble(on ? "Буду краем глаза поглядывать, чё ты там делаешь." : "Ладно, не подглядываю.");
    }

    public void SetStreamer(bool on)
    {
        if (_cfg.GameStreamerMode == on) { _streamerItem.Checked = on; return; }
        _cfg.GameStreamerMode = on;
        _streamerItem.Checked = on;
        _cfg.Save();
        SyncClickThrough(Native.GetForegroundWindow());
        KeepOnTop();
        Bubble(on ? "🎮 Режим стримера ВКЛ! Сижу поверх CS2 и игр, мур!" : "Режим стримера выключен.");
    }

    public void SetClickThrough(bool on)
    {
        _cfg.GameClickThrough = on;
        _clickThroughItem.Checked = on;
        _cfg.Save();
        SyncClickThrough(Native.GetForegroundWindow());
        Bubble(on ? "Сквозной проклик включён: мышь летит прямо в игру!" : "Сквозной проклик выключен: кошку снова можно трогать.");
    }

    public void SetOverFullscreen(bool on)
    {
        _cfg.OverFullscreen = on;
        _overFullscreenItem.Checked = on;
        _cfg.Save();
        Bubble(on ? "Буду поверх любых игр и видео." : "Только над играми, где играю сама.");
    }

    public void SetPersonaModeById(string id)
    {
        var m = PersonaModes.Get(id);
        if (m.Id != _cfg.PersonaMode) SetPersonaMode(m);
    }

    /// <summary>Кого слушать: ровно эти голоса (не больше, чем людей в компании); пусто — всех.</summary>
    public void SetMembers(IReadOnlyList<string> names)
    {
        var n = Math.Max(1, _cfg.RoomPeopleCount);
        _cfg.RoomMembers = names.Where(x => !string.IsNullOrWhiteSpace(x) && x != SelfVoiceName).Distinct().Take(n).ToList();
        if (_cfg.RoomMembers.Count > 0) _cfg.SpeakerIdEnabled = true;
        _cfg.Save();
        Bubble(_cfg.RoomMembers.Count == 0 ? "Слушаю всех подряд, без фильтра по голосу."
            : $"Слушаю только: {string.Join(", ", _cfg.RoomMembers.Select(HumanName))}.");
        if (_cfg.RoomMembers.Count > 0) _ = EnsureSpeakerRunningAsync();
    }

    public void SetView(double scale, double zoom, double yaw)
    {
        _cfg.WindowScale = Math.Clamp(Math.Round(scale, 2), 0.4, 1.6);
        _cfg.CameraZoom = Math.Clamp(Math.Round(zoom, 2), 0, 1);
        _cfg.ModelYaw = Math.Clamp(yaw, -180, 180);
        ApplyView();
        _viewForm?.LoadValues();
        _saveLater.Stop();
        _saveLater.Start();
    }

    public void SetMic(int deviceIndex)
    {
        _ears.SwitchDevice(deviceIndex);
        Bubble("Микрофон переключён.");
    }

    public void RestartMic()
    {
        _ears.RestartMicrophone();
        Bubble("Пнула микрофон — переподключила.");
    }

    public void SetVad(double level)
    {
        _cfg.VadMinLevel = Math.Clamp(level, 0.002, 0.06);
        _saveLater.Stop();
        _saveLater.Start();
    }

    public void SetBackend(string key)
    {
        if (key == _cfg.SpeechBackend) return;
        _cfg.SpeechBackend = key;
        _cfg.Save();
        Bubble("Комплект слуха и голоса сохранён — заработает после перезапуска.");
    }

    public void SetHomeCity(string city)
    {
        _cfg.HomeCity = city.Trim();
        _cfg.Save();
        Bubble(_cfg.HomeCity.Length > 0 ? $"Запомнила: погоду смотрю для «{_cfg.HomeCity}»." : "Город для погоды забыла — спрошу, когда надо.");
    }

    public void SwitchBrainById(string id)
    {
        if (_cfg.Brains.FirstOrDefault(b => b.Id == id) is { } b) _ = SwitchBrainAsync(b);
    }

    public void LookAtScreen() => _ = ConverseAsync("(Друг просит глянуть на его экран и сказать, что там.)", look: true);

    public void ForgetConversation()
    {
        _brain.Forget();
        Bubble("(забыла всё, что было)");
    }

    public void ToggleWatcher() => Bubble(_watcher.Active ? _watcher.Stop() : _watcher.Start());

    public bool WatcherActive => _watcher.Active;

    public async Task OpenWatchReportAsync()
    {
        Bubble("Собираю отчёт по журналу…");
        var text = await _watcher.ReportAsync(null, CancellationToken.None);
        var file = System.Text.RegularExpressions.Regex.Match(text, @"Отчёт сохранён: (.+)").Groups[1].Value.Trim();
        if (File.Exists(file)) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
        else Bubble(text);
    }

    public void SetGameRecording(bool on)
    {
        if (on == GameRecorder.Recording) return;
        Bubble(on ? GameRecorder.Start(_cfg.Abs(Path.Combine("gamer", "data")), "") : GameRecorder.Stop());
    }

    public async Task RestartVoiceAsync()
    {
        Bubble("Перезапускаю голос…");
        var ok = await _services.RestartTtsAsync(new Progress<string>(s => Log.Write($"голос: {s}")));
        Bubble(ok ? "Голос перезапущен, мур." : "Голос не поднялся — глянь журнал.");
    }

    public async Task RestartHearingAsync()
    {
        Bubble("Перезапускаю распознавание…");
        var ok = await _services.RestartSttAsync(new Progress<string>(s => Log.Write($"слух: {s}")));
        Bubble(ok ? "Слух перезапущен." : "Распознавание не поднялось — глянь журнал.");
    }

    public void OpenVoiceProfiles() => _ = ShowVoiceProfilesAsync();

    private static readonly (string Id, string Title)[] WorkModes =
    [
        ("auto", "🤖 Авто — инструменты, когда просишь что-то сделать"),
        ("chat", "💬 Общение — просто болтаем, быстро, без рук"),
        ("work", "🛠 Работа — руки и инструменты всегда наготове"),
    ];

    private ToolStripMenuItem? _workMenu;

    /// <summary>Подменю «Режим»: общение / работа / авто — для любого мозга.</summary>
    private ToolStripMenuItem BuildWorkModeMenu()
    {
        _workMenu = new ToolStripMenuItem();
        foreach (var (id, title) in WorkModes)
            _workMenu.DropDownItems.Add(new ToolStripMenuItem(title, null, (_, _) => SetWorkMode(id)) { Tag = id });
        SyncWorkMenu();
        return _workMenu;
    }

    private void SyncWorkMenu()
    {
        if (_workMenu is null) return;
        var mode = _cfg.WorkMode is "chat" or "work" ? _cfg.WorkMode : "auto";
        _workMenu.Text = "Режим: " + mode switch { "chat" => "💬 общение", "work" => "🛠 работа", _ => "🤖 авто" };
        foreach (var it in _workMenu.DropDownItems.OfType<ToolStripMenuItem>()) it.Checked = (string?)it.Tag == mode;
    }

    /// <summary>Режим мозгов: «общение» — без инструментов и быстро, «работа» — инструменты всегда, «авто» — по смыслу.</summary>
    public void SetWorkMode(string mode)
    {
        mode = mode is "chat" or "work" ? mode : "auto";
        _cfg.WorkMode = mode;
        _cfg.Save();
        SyncWorkMenu();
        Log.Write($"режим мозгов: {mode}");
        Bubble(mode switch
        {
            "chat" => "💬 Режим «Общение»: просто болтаем, без рук — отвечаю быстрее.",
            "work" => "🛠 Режим «Работа»: руки и инструменты всегда наготове.",
            _ => "🤖 Режим «Авто»: беру инструменты, когда просишь что-то сделать.",
        });
    }

    public void ShowInputPublic() => ShowInput();

    // счётчик общего журнала: читать весь JSONL каждые 2 с на потоке окна (как делало окно звука) — лишний тормоз
    private int _journalCount;
    private DateTime _journalCountAt = DateTime.MinValue;
    private int _journalCounting;

    private int JournalCount()
    {
        if (DateTime.UtcNow - _journalCountAt > TimeSpan.FromSeconds(20) && Interlocked.Exchange(ref _journalCounting, 1) == 0)
        {
            _ = Task.Run(() =>
            {
                try { _journalCount = _flightJournal.GetTodayEntries().Count; }
                finally
                {
                    _journalCountAt = DateTime.UtcNow;
                    Volatile.Write(ref _journalCounting, 0);
                }
            });
        }
        return _journalCount;
    }
}
