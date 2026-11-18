namespace KlavaDesk;

/// <summary>
/// Панель настроек живёт в СВОЁМ потоке окон: окно Клавы занято (думает, говорит, двигает силуэт 30 раз в секунду,
/// ждёт WebView2) — панель от этого не тормозит и всегда открывается. Одна на всех: повторный вызов разворачивает
/// и поднимает уже открытую.
/// </summary>
internal static class ControlPanelHost
{
    private static readonly object Gate = new();
    private static ControlPanelForm? _form;
    private static bool _starting;
    private static IntPtr _hwnd;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    public static void Show(Func<ControlPanelForm> create)
    {
        lock (Gate)
        {
            if (_form is { IsDisposed: false } f && f.IsHandleCreated)
            {
                try { f.BeginInvoke(f.Reveal); return; }
                catch (InvalidOperationException) { _form = null; }
            }
            if (_starting) return;   // уже создаётся — покажется сама
            _starting = true;
        }
        var t = new Thread(() =>
        {
            ControlPanelForm? form = null;
            try
            {
                form = create();
                form.HandleCreated += (_, _) => Volatile.Write(ref _hwnd, form.Handle);
                lock (Gate) { _form = form; _starting = false; }
                Application.Run(form);
            }
            catch (Exception ex) { Log.Error("панель настроек", ex); }
            finally
            {
                Volatile.Write(ref _hwnd, IntPtr.Zero);
                lock (Gate)
                {
                    if (_form == form) _form = null;
                    _starting = false;
                }
            }
        }) { IsBackground = true, Name = "Клава: панель настроек" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    public static void Close()
    {
        ControlPanelForm? f;
        lock (Gate) f = _form;
        if (f is { IsDisposed: false, IsHandleCreated: true })
            try { f.BeginInvoke(f.Close); } catch (InvalidOperationException) { }
    }

    /// <summary>
    /// Клава «поверх всех» поднимает себя каждые 2 с — открытая панель поднимается следом, иначе силуэт кошки
    /// закрывал её кнопки и они «не нажимались». Асинхронно: поток Клавы не ждёт поток панели.
    /// </summary>
    public static void KeepAbove()
    {
        var h = Volatile.Read(ref _hwnd);
        if (h == IntPtr.Zero || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return;
        Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);
    }
}

/// <summary>
/// «⚙ Панель настроек Клавы»: всё в одном окне — слева разделы, справа настройки с прокруткой (ничего не обрезается
/// при любом размере). Изменения уходят в окно Клавы через OverlayForm.Ui (без ожидания), значения читаются из
/// klava.json-конфига при каждом возврате в панель — меню в трее и панель не расходятся.
/// </summary>
internal sealed class ControlPanelForm : Form
{
    private readonly OverlayForm? _klava;   // null — снимок окна без Клавы (--ui-shot panel)
    private readonly KlavaConfig _cfg;
    private readonly Ears? _ears;
    private readonly Services _services;
    private readonly Speaker _speaker;
    private readonly ListBox _nav = new()
    {
        Dock = DockStyle.Left, Width = 230, IntegralHeight = false, BorderStyle = BorderStyle.None, DrawMode = DrawMode.OwnerDrawFixed,
        ItemHeight = 38, BackColor = Color.FromArgb(243, 244, 248), Font = new Font("Segoe UI", 11f),
    };
    private readonly Panel _host = new() { Dock = DockStyle.Fill, BackColor = Color.White };
    private readonly List<(string Title, Panel Page, Action? OnShow)> _pages = [];
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 150 };
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private bool _sync;

    // основное
    private readonly CheckBox _handsFree = Check("Слушать без кнопки (иначе — зажимать правый Ctrl, когда говоришь)");
    private readonly CheckBox _muted = Check("Без звука (отвечает только пузырём)");
    private readonly CheckBox _subtitles = Check("Суфлёр: показывать текст её реплик");
    private readonly CheckBox _echo = Check("Не слышать себя из колонок (глохнет, пока говорит; перебить — правый Ctrl)");
    private readonly CheckBox _autopilot = Check("Автопилот: безопасное делает без спроса, опасное всё равно спросит");
    private readonly CheckBox _watchScreen = Check("Подглядывать за экраном и иногда комментировать");
    private readonly ComboBox _mode = Combo(320);
    // режим мозгов: общение / работа / авто
    private readonly (string Id, RadioButton Radio)[] _work =
    [
        ("chat", new RadioButton { Text = "💬 Общение — просто болтаем: быстро, без рук и инструментов", AutoSize = true, Margin = new Padding(3, 4, 3, 4) }),
        ("work", new RadioButton { Text = "🛠 Работа — руки и инструменты всегда наготове (терминал, мышь, браузер, игры)", AutoSize = true, Margin = new Padding(3, 4, 3, 4) }),
        ("auto", new RadioButton { Text = "🤖 Авто — сама решает: инструменты, когда просишь что-то сделать", AutoSize = true, Margin = new Padding(3, 4, 3, 4) }),
    ];
    // вид
    private readonly TrackBar _scale = Track(40, 160, 10), _zoom = Track(0, 100, 10), _yaw = Track(-180, 180, 30);
    private readonly Label _scaleVal = Val(), _zoomVal = Val(), _yawVal = Val();
    // микрофон
    private readonly ComboBox _mic = Combo(420);
    private readonly Meter _meter = new() { Width = 420, Height = 16, Margin = new Padding(3, 6, 3, 3) };
    private readonly Label _micState = Val();
    private readonly TrackBar _vad = Track(2, 60, 5);
    private readonly Label _vadVal = Val();
    private readonly ComboBox _backend = Combo(360);
    private readonly Label _sttState = Val("● проверяю…"), _ttsState = Val("● проверяю…");
    private volatile float _level, _threshold;
    private volatile bool _vadActive;
    private bool _devicesLoaded;
    private int _pinging;
    private DateTime _lastPing = DateTime.MinValue;
    // компания
    private readonly RadioButton[] _people = [.. Enumerable.Range(1, KlavaConfig.MaxRoomPeople).Select(n => new RadioButton { Text = n == 1 ? "1 — соло" : $"{n}", AutoSize = true, Margin = new Padding(3, 3, 10, 3) })];
    private readonly CheckedListBox _members = new() { Width = 420, Height = 150, CheckOnClick = true, IntegralHeight = false };
    private readonly Label _membersNote = Val();
    // мозги
    private readonly Label _brainNow = Val();
    private readonly ListBox _brains = new() { Width = 460, Height = 170, IntegralHeight = false };
    // игры
    private readonly CheckBox _streamer = Check("Режим стримера: держаться поверх CS и игр (Ctrl+Alt+G)");
    private readonly CheckBox _clickThrough = Check("Сквозной проклик мыши в игре (не сбивать прицел)");
    private readonly CheckBox _overFull = Check("Поверх любых полноэкранных окон (видео, игры)");
    private readonly CheckBox _record = Check("🎓 Записывать мою игру (учёба Клавы по моим кадрам, клавиатуре и мыши)");
    // наблюдение
    private readonly Label _watchState = Val();
    private readonly Button _watchToggle = Btn("👁 Начать наблюдение");
    private readonly TextBox _city = new() { Width = 260 };

    private static readonly (string Key, string Text)[] Backends =
    [
        ("auto", "Авто: большой, если установлен"),
        ("full", "Большой: Whisper + Qwen3-TTS (видеокарта)"),
        ("lite", "Лёгкий: GigaAM + Supertonic (процессор)"),
    ];

    public ControlPanelForm(OverlayForm? klava, KlavaConfig cfg, Ears? ears, Services services, Speaker speaker, Icon icon)
    {
        _klava = klava;
        _cfg = cfg;
        _ears = ears;
        _services = services;
        _speaker = speaker;
        Text = "Клава — панель настроек";
        Icon = icon;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;   // потерялась за окнами — есть на панели задач
        TopMost = true;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        ClientSize = new Size(Math.Min(960, wa.Width - 60), Math.Min(660, wa.Height - 80));
        MinimumSize = new Size(Math.Min(640, wa.Width), Math.Min(420, wa.Height));

        BuildPages();
        foreach (var p in _pages) _nav.Items.Add(p.Title);
        _nav.DrawItem += DrawNav;
        _nav.SelectedIndexChanged += (_, _) => ShowPage(_nav.SelectedIndex);
        Controls.Add(_host);
        Controls.Add(new Panel { Dock = DockStyle.Left, Width = 1, BackColor = Color.FromArgb(220, 222, 230) });
        Controls.Add(_nav);

        if (_ears is not null) _ears.LevelTick += OnLevel;
        _timer.Tick += (_, _) => OnTimer();
        _timer.Start();
        Activated += (_, _) => LoadValues();
        FormClosed += (_, _) =>
        {
            if (_ears is not null) _ears.LevelTick -= OnLevel;
            _timer.Stop();
            _timer.Dispose();
        };
        LoadValues();
        _nav.SelectedIndex = 0;
    }

    /// <summary>Показать панель: развернуть, вернуть на экран, поднять над всеми и дать фокус.</summary>
    public void Reveal()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        if (!Visible) Show();
        var wa = Screen.FromControl(this).WorkingArea;
        if (!wa.IntersectsWith(Bounds) || Top < wa.Top) Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        TopMost = true;
        BringToFront();
        Activate();
        Native.SetForegroundWindow(Handle);
        LoadValues();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_klava is not null) Reveal();
    }

    // ---------------- разделы ----------------

    private void BuildPages()
    {
        // 🐾 основное
        _mode.Items.AddRange(PersonaModes.All.Select(m => (object)m.Title).ToArray());
        Page("🐾 Основное", null,
            Group("Режим мозгов (для всех: свой, облачные, запасные)", [.. _work.Select(w => (Control)w.Radio), Note("Голосом тоже: «режим работы», «режим общения», «авторежим».")]),
            Group("Слушать и говорить", _handsFree, _muted, _subtitles, _echo),
            Group("Поведение", _autopilot, _watchScreen, Row(Lbl("Режим общения:"), _mode)),
            Group("Быстрые действия",
                Row(Act("✍ Написать Клаве", k => k.ShowInputPublic()), Act("👀 Глянь на экран", k => k.LookAtScreen()), Act("🖥 Кибер-терминал", k => k.ShowTerminalForm())),
                Row(Act("🐱 Показать Клаву", k => k.ShowOnDesktop()), Btn("🧹 Забыть разговор", (_, _) =>
                {
                    if (Ask("Клава забудет весь текущий разговор. Точно?")) Do(k => k.ForgetConversation());
                }))));
        foreach (var (id, radio) in _work)
            radio.CheckedChanged += (_, _) => { if (!_sync && radio.Checked) Do(k => k.SetWorkMode(id)); };
        _handsFree.CheckedChanged += (_, _) => { if (!_sync) Do(k => k.SetHandsFree(_handsFree.Checked)); };
        _muted.CheckedChanged += (_, _) => { if (!_sync) Do(k => k.SetMuted(_muted.Checked)); };
        _subtitles.CheckedChanged += (_, _) => { if (!_sync) Do(k => k.SetSubtitles(_subtitles.Checked)); };
        _echo.CheckedChanged += (_, _) => { if (!_sync) Do(k => k.SetEchoBlock(_echo.Checked)); };
        _autopilot.CheckedChanged += (_, _) => { if (!_sync) Do(k => k.SetAutopilot(_autopilot.Checked)); };
        _watchScreen.CheckedChanged += (_, _) => { if (!_sync) Do(k => k.SetWatchScreen(_watchScreen.Checked)); };
        _mode.SelectedIndexChanged += (_, _) =>
        {
            if (_sync || _mode.SelectedIndex < 0) return;
            var id = PersonaModes.All[_mode.SelectedIndex].Id;
            Do(k => k.SetPersonaModeById(id));
        };

        // 🎨 вид
        Page("🎨 Вид", null,
            Group("Размер, приближение, поворот (сразу видно на Клаве)",
                Row(Lbl("Размер окна:", 150), _scale, _scaleVal),
                Row(Lbl("Приближение:", 150), _zoom, _zoomVal),
                Row(Lbl("Поворот:", 150), _yaw, _yawVal),
                Row(Btn("↺ Как было по умолчанию", (_, _) => { _scale.Value = 100; _zoom.Value = 0; _yaw.Value = 0; ApplyView(); }))),
            Note("То же самое колесом мыши по Клаве: колесо — приближение, Ctrl+колесо — размер, Shift+колесо — поворот."));
        foreach (var t in new[] { _scale, _zoom, _yaw }) t.ValueChanged += (_, _) => { if (!_sync) ApplyView(); ShowViewValues(); };

        // 🎙 микрофон и слух
        _backend.Items.AddRange(Backends.Select(b => (object)b.Text).ToArray());
        Page("🎙 Микрофон и слух", OnMicPageShown,
            Group("Микрофон",
                Row(Lbl("Устройство:"), _mic),
                Row(Lbl("Уровень:"), _meter, _micState),
                Row(Lbl("Порог речи:"), _vad, _vadVal),
                Row(Act("⚡ Пнуть микрофон", k => k.RestartMic()))),
            Group("Распознавание и голос",
                Row(Lbl("Слух:", 70), _sttState),
                Row(Lbl("Голос:", 70), _ttsState),
                Row(Act("⚡ Перезапустить распознавание", k => _ = k.RestartHearingAsync()), Act("⚡ Перезапустить голос", k => _ = k.RestartVoiceAsync())),
                Row(Lbl("Комплект:"), _backend),
                Note("Смена комплекта заработает после перезапуска Клавы.")),
            Group("Подробнее",
                Row(Act("🎚 Лог распознавания, награда/штраф, фантомы…", k => k.ShowAudioSettingsForm()))));
        _mic.SelectedIndexChanged += (_, _) =>
        {
            if (_sync || _mic.SelectedItem is not DeviceItem d) return;
            Do(k => k.SetMic(d.Index));
        };
        _vad.ValueChanged += (_, _) =>
        {
            _vadVal.Text = $"{_vad.Value / 1000.0:F3}";
            if (!_sync) { var v = _vad.Value / 1000.0; Do(k => k.SetVad(v)); }
        };
        _backend.SelectedIndexChanged += (_, _) =>
        {
            if (_sync || _backend.SelectedIndex < 0) return;
            var key = Backends[_backend.SelectedIndex].Key;
            if (key == "lite" && !Lite.Models(_cfg).CanSpeak) { Say("Лёгкого комплекта нет: запусти tools\\fetch_lite.ps1"); LoadValues(); return; }
            Do(k => k.SetBackend(key));
        };

        // 👥 компания и голоса
        var peopleRow = Row(Lbl("Сколько людей в комнате:"));
        foreach (var rb in _people) peopleRow.Controls.Add(rb);
        for (var i = 0; i < _people.Length; i++)
        {
            var n = i + 1;
            _people[i].CheckedChanged += (_, _) => { if (!_sync && _people[n - 1].Checked) { Do(k => k.SetRoomCount(n)); _ = LoadMembersAsync(); } };
        }
        _members.ItemCheck += (_, e) =>
        {
            if (_sync) return;
            // ItemCheck приходит ДО смены галочки — список собираем с учётом нового состояния
            var names = _members.CheckedItems.Cast<string>().ToList();
            var item = (string)_members.Items[e.Index];
            if (e.NewValue == CheckState.Checked) names.Add(item); else names.Remove(item);
            var n = Math.Max(1, _cfg.RoomPeopleCount);
            _membersNote.Text = names.Count > n ? $"Выбрано {names.Count}, а людей {n} — слушаю первых {n}." : "";
            var chosen = names.Select(x => x.Replace(' ', '_')).ToList();
            Do(k => k.SetMembers(chosen));
        };
        Page("👥 Компания и голоса", () => _ = LoadMembersAsync(),
            Group("Компания", peopleRow, Lbl("Кого слушать (остальные голоса — телевизор, гости — мимо, только в журнал):", 640), _members, _membersNote,
                Row(Btn("Слушать всех", (_, _) => { Do(k => k.SetMembers([])); _ = LoadMembersAsync(); }), Btn("🔄 Обновить список", (_, _) => _ = LoadMembersAsync()),
                    Act("🧹 Сбросить временный буфер", k => k.ClearTempBatch()))),
            Group("Голосовые профили",
                Row(Act("👤 Голосовые профили: запись, удаление, проверка…", k => k.OpenVoiceProfiles())),
                Note("Там же «📖 Хардкорная тренировка»: книга (txt, fb2, epub…) сама режется на ~20 страниц с ударениями и интонацией, " +
                     "читаешь 5–15 минут, устал — «Хватит»: запись сохранится и слух обучится на прочитанном.")));

        // 🧠 мозги
        Page("🧠 Мозги", LoadBrains,
            Group("Кто думает за Клаву", _brainNow, _brains,
                Row(Btn("Переключить на выбранный", (_, _) =>
                {
                    if (_brains.SelectedItem is not BrainItem b) return;
                    Do(k => k.SwitchBrainById(b.Id));
                    Say($"Переключаю на «{b.Title}» — Клава скажет, когда готова.");
                }), Act("🧠 Добавить или изменить мозги…", k => k.ShowBrainProfiles()))));

        // 🎮 игры
        Page("🎮 Игры", null, Group("Поверх игр", _streamer, _clickThrough, _overFull, _record),
            Note("Играть Клава умеет сама: скажи «поиграй в тетрис», «зайди в майнкрафт» или «поиграй в зуму»."));
        _streamer.CheckedChanged += (_, _) => { if (!_sync) Do(k => k.SetStreamer(_streamer.Checked)); };
        _clickThrough.CheckedChanged += (_, _) => { if (!_sync) Do(k => k.SetClickThrough(_clickThrough.Checked)); };
        _overFull.CheckedChanged += (_, _) => { if (!_sync) Do(k => k.SetOverFullscreen(_overFull.Checked)); };
        _record.CheckedChanged += (_, _) => { if (!_sync) Do(k => k.SetGameRecording(_record.Checked)); };

        // 👁 наблюдение и погода
        _watchToggle.Click += (_, _) => { Do(k => k.ToggleWatcher()); _watchState.Text = "…"; };
        Page("👁 Наблюдение и погода", null,
            Group("Наблюдение за экраном (что рисовали и кодили, пока тебя не было)",
                Row(_watchToggle, Act("📝 Отчёт наблюдения", k => _ = k.OpenWatchReportAsync())), _watchState),
            Group("Погода",
                Row(Lbl("Мой город:"), _city, Btn("Сохранить", (_, _) => { var c = _city.Text; Do(k => k.SetHomeCity(c)); })),
                Note("Для «какая погода?» без названия города.")));

        // 🛠 система
        Page("🛠 Система", null,
            Group("Папки",
                Row(Btn("📂 Журналы", (_, _) => Open(_cfg.Abs(@"data\logs"))), Btn("📂 Данные Клавы", (_, _) => Open(_cfg.Abs("data"))),
                    Btn("📂 Книги для чтения", (_, _) => Open(ReadingBooks.BooksDir(_cfg))))),
            Group("Горячие клавиши",
                Note($"Ctrl+Alt+K — эта панель (или щелчок по кошке в трее) · Ctrl+Alt+{_cfg.TextHotkeyLetter} — написать Клаве · " +
                     "Ctrl+Alt+G — режим стримера · правый Ctrl — рация · Esc или движение мыши — забрать мышку у Клавы.")),
            Group("Выключение",
                Row(Btn("⏻ Выключить Клаву", (_, _) => { if (Ask("Выключить Клаву вместе с её мозгом, слухом и голосом?")) Do(k => k.Close()); }))));
    }

    private void Page(string title, Action? onShow, params Control[] items)
    {
        var page = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Visible = false, Padding = new Padding(18, 12, 18, 12), BackColor = Color.White };
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Location = new Point(18, 12) };
        flow.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Segoe UI", 15f, FontStyle.Bold), Margin = new Padding(3, 0, 3, 10) });
        foreach (var c in items) flow.Controls.Add(c);
        page.Controls.Add(flow);
        // группы — во всю ширину страницы (узкое окно — горизонтальная прокрутка, ничего не обрезается)
        void Fit()
        {
            var w = Math.Max(560, page.ClientSize.Width - 40);
            foreach (Control c in flow.Controls) if (c is GroupBox g) g.MinimumSize = new Size(w, 0);
        }
        page.ClientSizeChanged += (_, _) => Fit();
        _host.Controls.Add(page);
        _pages.Add((title, page, onShow));
        Fit();
    }

    private void ShowPage(int i)
    {
        if (i < 0 || i >= _pages.Count) return;
        for (var k = 0; k < _pages.Count; k++) _pages[k].Page.Visible = k == i;
        _pages[i].Page.BringToFront();
        _pages[i].OnShow?.Invoke();
    }

    private void DrawNav(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using var bg = new SolidBrush(selected ? Color.FromArgb(222, 232, 255) : _nav.BackColor);
        e.Graphics.FillRectangle(bg, e.Bounds);
        if (selected) using (var bar = new SolidBrush(Color.FromArgb(70, 110, 230))) e.Graphics.FillRectangle(bar, e.Bounds.Left, e.Bounds.Top + 6, 4, e.Bounds.Height - 12);
        TextRenderer.DrawText(e.Graphics, _nav.Items[e.Index].ToString(), _nav.Font, new Rectangle(e.Bounds.Left + 14, e.Bounds.Top, e.Bounds.Width - 16, e.Bounds.Height),
            Color.FromArgb(30, 30, 40), TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
    }

    // ---------------- значения ----------------

    /// <summary>Галочки и ползунки — по текущему конфигу (меню в трее или голос могли их поменять).</summary>
    private void LoadValues()
    {
        _sync = true;
        try
        {
            var wm = _cfg.WorkMode is "chat" or "work" ? _cfg.WorkMode : "auto";
            foreach (var (id, radio) in _work) radio.Checked = id == wm;
            _handsFree.Checked = _cfg.HandsFree;
            _muted.Checked = _cfg.Muted;
            _subtitles.Checked = _cfg.Subtitles;
            _echo.Checked = _cfg.EchoBlock;
            _autopilot.Checked = _cfg.Autopilot;
            _watchScreen.Checked = _cfg.WatchScreen;
            _mode.SelectedIndex = Math.Max(0, Array.FindIndex(PersonaModes.All, m => m.Id == PersonaModes.Get(_cfg.PersonaMode).Id));
            _scale.Value = Math.Clamp((int)Math.Round(_cfg.WindowScale * 100), _scale.Minimum, _scale.Maximum);
            _zoom.Value = Math.Clamp((int)Math.Round(_cfg.CameraZoom * 100), _zoom.Minimum, _zoom.Maximum);
            _yaw.Value = Math.Clamp((int)Math.Round(_cfg.ModelYaw), _yaw.Minimum, _yaw.Maximum);
            ShowViewValues();
            _vad.Value = Math.Clamp((int)Math.Round(_cfg.VadMinLevel * 1000), _vad.Minimum, _vad.Maximum);
            _vadVal.Text = $"{_cfg.VadMinLevel:F3}";
            var bi = Array.FindIndex(Backends, b => b.Key.Equals(_cfg.SpeechBackend.Trim(), StringComparison.OrdinalIgnoreCase));
            _backend.SelectedIndex = bi < 0 ? 0 : bi;
            var n = Math.Clamp(_cfg.RoomPeopleCount, 1, _people.Length);
            _people[n - 1].Checked = true;
            _streamer.Checked = _cfg.GameStreamerMode;
            _clickThrough.Checked = _cfg.GameClickThrough;
            _overFull.Checked = _cfg.OverFullscreen;
            _record.Checked = GameRecorder.Recording;
            if (!_city.Focused) _city.Text = _cfg.HomeCity;
            _brainNow.Text = $"Сейчас: {_cfg.ActiveBrain.DisplayName}";
        }
        finally { _sync = false; }
    }

    private void ShowViewValues()
    {
        _scaleVal.Text = $"{_scale.Value} %";
        _zoomVal.Text = $"{_zoom.Value} %";
        _yawVal.Text = $"{_yaw.Value}°";
    }

    private void ApplyView()
    {
        double s = _scale.Value / 100.0, z = _zoom.Value / 100.0, y = _yaw.Value;
        Do(k => k.SetView(s, z, y));
    }

    private void OnMicPageShown()
    {
        _lastPing = DateTime.MinValue;
        if (_devicesLoaded) return;
        _devicesLoaded = true;
        // список устройств WinMM с USB-микрофоном бывает долгим — в фоне
        _ = Task.Run(Ears.GetDevices).ContinueWith(t =>
        {
            if (IsDisposed || t.IsFaulted) return;
            BeginInvoke(() =>
            {
                _sync = true;
                _mic.Items.Clear();
                var sel = -1;
                foreach (var (idx, name) in t.Result)
                {
                    var i = _mic.Items.Add(new DeviceItem(idx, name));
                    if (idx == Ears.ResolveDevice(_cfg)) sel = i;
                }
                if (sel >= 0) _mic.SelectedIndex = sel;
                _sync = false;
            });
        }, TaskScheduler.Default);
    }

    private void OnLevel(double rms, double threshold, bool vad)
    {
        _level = (float)rms;
        _threshold = (float)threshold;
        _vadActive = vad;
    }

    /// <summary>Раз в 150 мс: полоска уровня (без потока BeginInvoke на каждый буфер), раз в 5 с — живы ли слух и голос.</summary>
    private void OnTimer()
    {
        var page = _nav.SelectedIndex >= 0 ? _pages[_nav.SelectedIndex].Title : "";
        if (page.Contains("Микрофон"))
        {
            _meter.Value = Math.Clamp(_level * 3.5, 0, 1);
            _meter.Mark = Math.Clamp(_threshold * 3.5, 0, 1);
            var open = _ears?.IsOpen == true;
            _micState.Text = _vadActive ? "● слышу речь" : _ears?.SpeakingBack == true ? "● Клава говорит" : open ? "● тишина" : $"● микрофон закрыт {_ears?.LastError}";
            _micState.ForeColor = _vadActive ? Color.OrangeRed : open ? Color.ForestGreen : Color.Firebrick;
            if (DateTime.UtcNow - _lastPing > TimeSpan.FromSeconds(5)) _ = PingAsync();
        }
        else if (page.Contains("Наблюдение"))
        {
            var on = _klava?.WatcherActive == true;
            _watchToggle.Text = on ? "⏹ Остановить наблюдение" : "👁 Начать наблюдение";
            _watchState.Text = on ? "Идёт наблюдение: журнал экрана пишется." : "Наблюдение выключено.";
        }
    }

    private async Task PingAsync()
    {
        if (Interlocked.Exchange(ref _pinging, 1) == 1) return;
        _lastPing = DateTime.UtcNow;
        try
        {
            var stt = _services.PingSttAsync();
            var tts = _services.PingTtsAsync();
            await Task.WhenAll(stt, tts);
            if (IsDisposed) return;
            Show(_sttState, stt.Result);
            Show(_ttsState, tts.Result);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
        finally { Volatile.Write(ref _pinging, 0); }

        static void Show(Label l, (bool Alive, long PingMs, string Info) r)
        {
            l.Text = r.Alive ? $"● в сети, {r.PingMs} мс ({r.Info})" : $"● не отвечает ({r.Info})";
            l.ForeColor = r.Alive ? Color.ForestGreen : Color.Firebrick;
        }
    }

    private async Task LoadMembersAsync()
    {
        List<string> names = [];
        try { names = (await _speaker.ListAsync()).Select(p => p.Name).Where(n => n != "Клава").Order().ToList(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException) { }
        if (IsDisposed) return;
        _sync = true;
        _members.Items.Clear();
        foreach (var n in names) _members.Items.Add(n.Replace('_', ' '), _cfg.RoomMembers.Contains(n));
        _membersNote.Text = names.Count == 0 ? "Записанных голосов нет — «Голосовые профили»." : _cfg.RoomMembers.Count == 0 ? "Никто не выбран — слушаю всех." : "";
        _sync = false;
    }

    private void LoadBrains()
    {
        _brains.Items.Clear();
        // Claude Code без входа переключиться не даст — в списке его нет, пока не залогинен (ClaudeCode.LastLoggedIn)
        foreach (var b in _cfg.Brains.Where(b => b.Kind != "claude-code" || ClaudeCode.LastLoggedIn == true || b.Id == _cfg.ActiveBrain.Id))
            _brains.Items.Add(new BrainItem(b.Id, b.DisplayName));
        var active = _cfg.ActiveBrain.Id;
        for (var i = 0; i < _brains.Items.Count; i++) if (((BrainItem)_brains.Items[i]!).Id == active) _brains.SelectedIndex = i;
        _brainNow.Text = $"Сейчас: {_cfg.ActiveBrain.DisplayName}";
    }

    // ---------------- мелочи ----------------

    private void Do(Action<OverlayForm> a)
    {
        if (_klava is { } k) k.Ui(() => a(k));
    }

    /// <summary>Для снимка: показать раздел.</summary>
    internal int PageCount => _pages.Count;
    internal void SelectPageForTest(int i) => _nav.SelectedIndex = i;

    private Button Act(string text, Action<OverlayForm> a) => Btn(text, (_, _) => Do(a));

    private bool Ask(string q) => MessageBox.Show(this, q, "Клава", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    private void Say(string s) => MessageBox.Show(this, s, "Клава", MessageBoxButtons.OK, MessageBoxIcon.Information);

    private void Open(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { Say(ex.Message); }
    }

    private static CheckBox Check(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
    private static ComboBox Combo(int w) => new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = w, Margin = new Padding(3, 3, 3, 3) };
    private static TrackBar Track(int min, int max, int tick) => new() { Minimum = min, Maximum = max, TickFrequency = tick, Width = 320, AutoSize = false, Height = 34 };
    private static Label Val(string text = "") => new() { Text = text, AutoSize = true, Margin = new Padding(6, 8, 3, 3), ForeColor = Color.FromArgb(60, 60, 70) };
    private static Label Lbl(string t, int width = 0) => width > 0
        ? new Label { Text = t, AutoSize = false, Width = width, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Margin = new Padding(3, 5, 3, 3) }
        : new Label { Text = t, AutoSize = true, Margin = new Padding(3, 8, 6, 3) };
    private static Label Note(string t) => new() { Text = t, AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = Color.FromArgb(95, 95, 110), Margin = new Padding(3, 6, 3, 6) };
    private static Button Btn(string text, EventHandler? click = null)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 3, 8, 3), Margin = new Padding(3, 4, 8, 4) };
        if (click is not null) b.Click += click;
        return b;
    }

    private static FlowLayoutPanel Row(params Control[] items)
    {
        var f = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 2, 0, 2) };
        foreach (var c in items) f.Controls.Add(c);
        return f;
    }

    private static GroupBox Group(string title, params Control[] items)
    {
        var g = new GroupBox { Text = " " + title + " ", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, Padding = new Padding(10, 6, 10, 8), Margin = new Padding(3, 4, 3, 10) };
        var f = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Dock = DockStyle.Fill };
        foreach (var c in items) f.Controls.Add(c);
        g.Controls.Add(f);
        return g;
    }

    private sealed record DeviceItem(int Index, string Name) { public override string ToString() => Name; }
    private sealed record BrainItem(string Id, string Title) { public override string ToString() => Title; }

    /// <summary>Полоска уровня с меткой порога: рисуется сама, без анимации ProgressBar (та отставала на полсекунды).</summary>
    private sealed class Meter : Control
    {
        private double _value, _mark;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double Value { get => _value; set { if (Math.Abs(value - _value) > 0.002) { _value = value; Invalidate(); } } }
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double Mark { get => _mark; set { if (Math.Abs(value - _mark) > 0.002) { _mark = value; Invalidate(); } } }

        public Meter()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(232, 233, 238));
            using var b = new SolidBrush(_value > 0.9 ? Color.IndianRed : Color.MediumSeaGreen);
            e.Graphics.FillRectangle(b, 0, 0, (int)(Width * _value), Height);
            using var p = new Pen(Color.DarkGoldenrod, 2);
            var x = (int)(Width * _mark);
            e.Graphics.DrawLine(p, x, 0, x, Height);
        }
    }
}

