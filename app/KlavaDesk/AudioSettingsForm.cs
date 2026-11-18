using System.Diagnostics;
using NAudio.Wave;

namespace KlavaDesk;

/// <summary>
/// Нативная панель настроек микрофона, живого VU-метра и контроля службы Whisper.
/// Позволяет выбрать аудиоустройство, пнуть микрофон, пнуть Whisper и видеть распознавание.
/// </summary>
internal sealed class AudioSettingsForm : Form
{
    private readonly KlavaConfig _cfg;
    private readonly Ears _ears;
    private readonly Services _services;

    // Элементы микрофона
    private readonly ComboBox _cbDevices = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly Button _btnRestartMic = new() { Text = "⚡ Пнуть микрофон", AutoSize = true };
    private readonly Label _lblMicStatus = new() { Text = "● Проверка…", AutoSize = true, ForeColor = Color.DarkGray };

    // Живой уровень звука и VAD
    private readonly ProgressBar _prgLevel = new() { Minimum = 0, Maximum = 100, Height = 20, Dock = DockStyle.Fill };
    private readonly Label _lblRms = new() { Text = "Уровень: 0 %", AutoSize = true };
    private readonly Label _lblState = new() { Text = "● Тишина", AutoSize = true, ForeColor = Color.ForestGreen, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
    private readonly TrackBar _trkVad = new() { Minimum = 2, Maximum = 60, TickFrequency = 5, Dock = DockStyle.Fill };
    private readonly Label _lblVadVal = new() { Text = "Порог VAD: 0.010", AutoSize = true };

    // Служба Whisper
    private readonly Label _lblSttUrl = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Label _lblSttStatus = new() { Text = "● Проверка…", AutoSize = true, ForeColor = Color.DarkGray, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
    private readonly Button _btnPingStt = new() { Text = "🔍 Проверить", AutoSize = true };
    private readonly Button _btnRestartStt = new() { Text = "⚡ Пнуть Whisper", AutoSize = true };
    private readonly Label _lblLearnStatus = new() { Text = "● Доучивание…", AutoSize = true, ForeColor = Color.DarkSlateGray };
    private readonly Button _btnOpenDict = new() { Text = "📖 Словарь автозамен", AutoSize = true };

    // Служба синтеза речи (Qwen3-TTS)
    private readonly Label _lblTtsUrl = new() { AutoSize = true, ForeColor = Color.DimGray };
    private readonly Label _lblTtsStatus = new() { Text = "● Проверка…", AutoSize = true, ForeColor = Color.DarkGray, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
    private readonly Button _btnPingTts = new() { Text = "🔍 Проверить", AutoSize = true };
    private readonly Button _btnRestartTts = new() { Text = "⚡ Пнуть TTS", AutoSize = true };

    // Какой комплект слуха и голоса: большой (видеокарта + Python) или лёгкий (процессор, для ноутбука)
    private static readonly (string Key, string Text)[] Backends =
    [
        ("auto", "Авто: большой, если установлен"),
        ("full", "Большой: Whisper + Qwen3-TTS (видеокарта)"),
        ("lite", "Лёгкий: GigaAM + Supertonic (процессор)"),
    ];
    private readonly ComboBox _cbBackend = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 290 };
    private readonly Label _lblBackendNote = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(8, 7, 0, 0) };

    private readonly Action? _onClearTempBatch;
    private readonly Func<(int TempCount, int MaxCount, int TotalJournalCount)>? _getBufferStats;
    private readonly Action? _onOpenJournal;
    private readonly Func<Task<string?>>? _onFileMemory;
    private readonly Func<string, Task<(bool Ok, string Message)>>? _onCorrectSpeaker;
    private readonly Func<string, Task<(bool Ok, string Message)>>? _onRewardSpeaker;
    private readonly Action<string>? _onBanHallucination;
    private readonly Speaker? _speaker;
    private string? _lastHeardText;

    // Награда (верно узнала) и штраф (ошибка — на самом деле это был…) — кнопки строятся из записанных голосов
    private readonly FlowLayoutPanel _rewardPanel = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, Margin = new Padding(0) };
    private readonly FlowLayoutPanel _penaltyPanel = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoScroll = true, Margin = new Padding(0) };
    private readonly Button _btnBanHallucination = new() { Text = "🚫 Ложные слова (Фантом!)", AutoSize = true, BackColor = Color.FromArgb(255, 220, 220), ForeColor = Color.DarkRed, Font = new Font("Segoe UI", 9f, FontStyle.Bold) };

    // Группа компании и буфера диалога
    // сколько людей: 1 (соло) … KlavaConfig.MaxRoomPeople (было четыре кнопки 1–4 — пятый человек не влезал)
    private readonly NumericUpDown _peopleCount = new() { Minimum = 1, Maximum = KlavaConfig.MaxRoomPeople, Width = 56, Margin = new Padding(0, 3, 12, 0) };
    private readonly ComboBox[] _members = Enumerable.Range(0, KlavaConfig.MaxRoomPeople).Select(_ => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 132, Margin = new Padding(0, 2, 6, 2) }).ToArray();
    private readonly Button _btnMembersRefresh = new() { Text = "Обновить список", AutoSize = true, Margin = new Padding(0, 1, 0, 0) };
    private const string AnyVoice = "(не выбран — слушаю всех)";
    private readonly Label _lblBatchStatus = new() { Text = "● Буфер: 0 / 1", AutoSize = true, ForeColor = Color.DarkSlateBlue, Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) };
    private readonly Button _btnClearBatch = new() { Text = "🧹 Сбросить временный буфер", AutoSize = true };
    // прежние «сброс журнала» и «AI-сводка» затирали день (25–27.09 остались кусками по архивам) — журнал теперь только дописывается
    private readonly Button _btnResetJournal = new() { Text = "📖 Открыть журнал дня", AutoSize = true };
    private readonly Button _btnSummarize = new() { Text = "🧠 Разложить память сейчас", AutoSize = true };

    // Лог
    private readonly TextBox _txtLog = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.FromArgb(248, 249, 250), Font = new Font("Consolas", 9.5f) };

    private readonly System.Windows.Forms.Timer _pingTimer = new() { Interval = 2000 };
    // уровень с микрофона приходит на каждый аудиобуфер (десятки раз в секунду) из потока записи: раньше каждый —
    // BeginInvoke в поток окна Клавы, и окна тормозили; теперь поток записи только кладёт числа, окно рисует 10 раз в с
    private readonly System.Windows.Forms.Timer _levelTimer = new() { Interval = 100 };
    private volatile float _levelRms, _levelThreshold;
    private volatile bool _levelVad;
    private int _pinging;
    private double _smoothedRms;
    private bool _loading = true;

    public AudioSettingsForm(KlavaConfig cfg, Ears ears, Services services, Action? onClearTempBatch = null, Func<(int TempCount, int MaxCount, int TotalJournalCount)>? getBufferStats = null, Action? onOpenJournal = null, Func<Task<string?>>? onFileMemory = null, Func<string, Task<(bool Ok, string Message)>>? onCorrectSpeaker = null, Func<string, Task<(bool Ok, string Message)>>? onRewardSpeaker = null, Action<string>? onBanHallucination = null, Speaker? speaker = null)
    {
        _cfg = cfg;
        _ears = ears;
        _services = services;
        _onClearTempBatch = onClearTempBatch;
        _getBufferStats = getBufferStats;
        _onOpenJournal = onOpenJournal;
        _onFileMemory = onFileMemory;
        _onCorrectSpeaker = onCorrectSpeaker;
        _onRewardSpeaker = onRewardSpeaker;
        _onBanHallucination = onBanHallucination;
        _speaker = speaker;

        Text = "Клава — Настройки звука, микрофона и распознавания";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = false;
        MaximizeBox = true;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 1024);
        ClientSize = new Size(Math.Min(820, wa.Width - 40), Math.Min(892, wa.Height - 50));
        MinimumSize = new Size(Math.Min(740, wa.Width), Math.Min(600, wa.Height));
        AutoScrollMinSize = new Size(700, 820);   // меньше — прокрутка, а не сплющенные группы

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7, Padding = new Padding(12) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 95));  // Микрофон
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 125)); // Уровень и VAD
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 190)); // Группа людей и буфера диалога (до 8 человек — списки «Кто:» в две строки)
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120)); // Whisper + Доучивание
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 122)); // TTS + выбор комплекта (большой/лёгкий)
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // Лог распознанных фраз (широкий и просторный)
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));  // Кнопки низа

        // 1. Группа Микрофон
        var grpMic = new GroupBox { Text = " Микрофон (Аудиовход) ", Dock = DockStyle.Fill };
        var micLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Padding = new Padding(8) };
        micLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        micLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        micLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        micLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

        micLayout.Controls.Add(_cbDevices, 0, 0);
        micLayout.Controls.Add(_btnRestartMic, 1, 0);
        micLayout.Controls.Add(_lblMicStatus, 0, 1);
        micLayout.SetColumnSpan(_lblMicStatus, 2);
        grpMic.Controls.Add(micLayout);

        // 2. Группа VU-метр и VAD
        var grpVad = new GroupBox { Text = " Живой уровень звука и детектор речи (VAD) ", Dock = DockStyle.Fill };
        var vadLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4, Padding = new Padding(8) };
        vadLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        vadLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));
        vadLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        vadLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        vadLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        vadLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));

        vadLayout.Controls.Add(_prgLevel, 0, 0);
        vadLayout.Controls.Add(_lblRms, 1, 0);
        vadLayout.Controls.Add(_lblState, 0, 1);
        vadLayout.SetColumnSpan(_lblState, 2);

        vadLayout.Controls.Add(_trkVad, 0, 2);
        vadLayout.Controls.Add(_lblVadVal, 1, 2);
        grpVad.Controls.Add(vadLayout);

        // 3. Группа Людей в комнате и буфер диалога
        var grpPeople = new GroupBox { Text = " 👥 Компания в комнате и буфер диалога ", Dock = DockStyle.Fill };
        var peopleLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 6, RowCount = 4, Padding = new Padding(8, 4, 8, 4) };
        peopleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        peopleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        peopleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        peopleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        peopleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        peopleLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        peopleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        peopleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        peopleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        peopleLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));

        var countRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        countRow.Controls.Add(new Label { Text = "Сколько людей:", AutoSize = true, Margin = new Padding(0, 5, 6, 0) });
        countRow.Controls.Add(_peopleCount);
        countRow.Controls.Add(new Label { Text = "(1 — соло)", AutoSize = true, ForeColor = Color.Gray, Margin = new Padding(0, 5, 0, 0) });
        peopleLayout.Controls.Add(countRow, 0, 0);
        peopleLayout.SetColumnSpan(countRow, 4);
        peopleLayout.Controls.Add(_btnClearBatch, 4, 0);

        // кто именно: столько списков, сколько людей выбрано
        var whoRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0) };
        whoRow.Controls.Add(new Label { Text = "Кто:", AutoSize = true, Margin = new Padding(0, 5, 6, 0), Font = new Font("Segoe UI", 9.5f, FontStyle.Bold) });
        foreach (var cb in _members) whoRow.Controls.Add(cb);
        whoRow.Controls.Add(_btnMembersRefresh);
        peopleLayout.Controls.Add(whoRow, 0, 1);
        peopleLayout.SetColumnSpan(whoRow, 6);

        peopleLayout.Controls.Add(_btnResetJournal, 0, 2);
        peopleLayout.SetColumnSpan(_btnResetJournal, 3);
        peopleLayout.Controls.Add(_btnSummarize, 3, 2);
        peopleLayout.SetColumnSpan(_btnSummarize, 3);

        peopleLayout.Controls.Add(_lblBatchStatus, 0, 3);
        peopleLayout.SetColumnSpan(_lblBatchStatus, 6);
        grpPeople.Controls.Add(peopleLayout);

        // 4. Группа Whisper
        var grpStt = new GroupBox { Text = " Сервис распознавания (Whisper.cpp) ", Dock = DockStyle.Fill };
        var sttLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Padding = new Padding(8) };
        sttLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sttLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sttLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        sttLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        sttLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        sttLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        sttLayout.Controls.Add(_lblSttUrl, 0, 0);
        sttLayout.Controls.Add(_btnPingStt, 1, 0);
        sttLayout.Controls.Add(_btnRestartStt, 2, 0);
        sttLayout.Controls.Add(_lblSttStatus, 0, 1);
        sttLayout.SetColumnSpan(_lblSttStatus, 3);
        sttLayout.Controls.Add(_lblLearnStatus, 0, 2);
        sttLayout.Controls.Add(_btnOpenDict, 1, 2);
        sttLayout.SetColumnSpan(_btnOpenDict, 2);
        grpStt.Controls.Add(sttLayout);

        // 5. Группа TTS
        var grpTts = new GroupBox { Text = _cfg.LiteSpeech ? " Синтез речи (лёгкий: Supertonic-3 на процессоре) " : " Сервис синтеза речи (Qwen3-TTS) ", Dock = DockStyle.Fill };
        var ttsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 3, Padding = new Padding(8) };
        ttsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ttsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ttsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        ttsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        ttsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        ttsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

        ttsLayout.Controls.Add(_lblTtsUrl, 0, 0);
        ttsLayout.Controls.Add(_btnPingTts, 1, 0);
        ttsLayout.Controls.Add(_btnRestartTts, 2, 0);
        ttsLayout.Controls.Add(_lblTtsStatus, 0, 1);
        ttsLayout.SetColumnSpan(_lblTtsStatus, 3);
        var backendRow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = new Padding(0) };
        backendRow.Controls.Add(new Label { Text = "Слух и голос:", AutoSize = true, Margin = new Padding(0, 7, 6, 0) });
        backendRow.Controls.Add(_cbBackend);
        backendRow.Controls.Add(_lblBackendNote);
        ttsLayout.Controls.Add(backendRow, 0, 2);
        ttsLayout.SetColumnSpan(backendRow, 3);
        grpTts.Controls.Add(ttsLayout);
        if (_cfg.LiteSpeech) grpStt.Text = " Распознавание (лёгкое: GigaAM v3 на процессоре) ";

        // 6. Группа Лог
        var grpLog = new GroupBox { Text = " История распознанных фраз (с определением спикера) ", Dock = DockStyle.Fill };
        var logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(6) };
        logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        logLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

        logLayout.Controls.Add(_txtLog, 0, 0);
        logLayout.Controls.Add(_rewardPanel, 0, 1);
        logLayout.Controls.Add(_penaltyPanel, 0, 2);
        grpLog.Controls.Add(logLayout);

        // 7. Низ
        var bottomLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var btnClose = new Button { Text = "Готово", AutoSize = true };
        btnClose.Click += (_, _) => Close();
        bottomLayout.Controls.Add(btnClose);

        root.Controls.Add(grpMic, 0, 0);
        root.Controls.Add(grpVad, 0, 1);
        root.Controls.Add(grpPeople, 0, 2);
        root.Controls.Add(grpStt, 0, 3);
        root.Controls.Add(grpTts, 0, 4);
        root.Controls.Add(grpLog, 0, 5);
        root.Controls.Add(bottomLayout, 0, 6);
        Controls.Add(root);

        InitData();
        HookEvents();
    }

    private void InitData()
    {
        _lblSttUrl.Text = _cfg.LiteSpeech ? "В процессе Клавы, без сервера" : $"Адрес: {_cfg.SttUrl}";
        _lblTtsUrl.Text = _cfg.LiteSpeech ? $"В процессе Клавы: голос F{_cfg.LiteVoiceId + 1}, высота {_cfg.LiteVoicePitch:F2}" : $"Адрес: {_cfg.TtsUrl}";
        _cbBackend.Items.AddRange(Backends.Select(b => (object)b.Text).ToArray());
        var bi = Array.FindIndex(Backends, b => b.Key.Equals(_cfg.SpeechBackend.Trim(), StringComparison.OrdinalIgnoreCase));
        _cbBackend.SelectedIndex = bi < 0 ? 0 : bi;
        _lblBackendNote.Text = $"сейчас: {(_cfg.LiteSpeech ? "лёгкий" : "большой")}";
        _cbBackend.SelectedIndexChanged += (_, _) =>
        {
            var key = Backends[Math.Max(0, _cbBackend.SelectedIndex)].Key;
            if (key == _cfg.SpeechBackend) return;
            if (key == "lite" && !Lite.Models(_cfg).CanSpeak)
            {
                _lblBackendNote.Text = "нет моделей: запусти tools\\fetch_lite.ps1";
                _lblBackendNote.ForeColor = Color.DarkRed;
                return;
            }
            _cfg.SpeechBackend = key;
            _cfg.Save();
            _lblBackendNote.Text = "сохранено — заработает после перезапуска Клавы";
            _lblBackendNote.ForeColor = Color.DarkGoldenrod;
        };
        _trkVad.Value = Math.Clamp((int)Math.Round(_cfg.VadMinLevel * 1000), _trkVad.Minimum, _trkVad.Maximum);
        _lblVadVal.Text = $"Порог VAD: {_cfg.VadMinLevel:F3}";

        // Список аудиоустройств
        var devices = Ears.GetDevices();
        _cbDevices.Items.Clear();
        var selectedIdx = 0;
        for (var i = 0; i < devices.Count; i++)
        {
            var d = devices[i];
            _cbDevices.Items.Add(new DeviceItem(d.DeviceIndex, d.Name));
            if (d.DeviceIndex == _cfg.MicDevice) selectedIdx = i;
        }
        if (_cbDevices.Items.Count > 0) _cbDevices.SelectedIndex = selectedIdx;

        _peopleCount.Value = Math.Clamp(_cfg.RoomPeopleCount, 1, KlavaConfig.MaxRoomPeople);
        UpdateBufferUi();

        UpdateMicStatus();
        _loading = false;
    }

    private void HookEvents()
    {
        void SetPeople(int n)
        {
            if (_loading) return;
            _cfg.RoomPeopleCount = n;
            _cfg.DialogBatchMaxPhrases = n;
            while (_cfg.RoomMembers.Count > n) _cfg.RoomMembers.RemoveAt(_cfg.RoomMembers.Count - 1);
            _cfg.Save();
            ShowMemberBoxes();
            UpdateBufferUi();
            LogAppend($"[Компания] {n} чел. Кого слушать — выбери в «Кто:» ({(n == 1 ? "соло: только его голос" : $"только эти {n} голоса")}; телевизор и чужие — мимо).");
        }
        foreach (var cb in _members) cb.SelectedIndexChanged += (_, _) => { if (!_loading) SaveMembers(); };
        _btnMembersRefresh.Click += async (_, _) => await LoadProfilesAsync();
        _peopleCount.ValueChanged += (_, _) => SetPeople((int)_peopleCount.Value);

        _btnClearBatch.Click += (_, _) =>
        {
            _onClearTempBatch?.Invoke();
            UpdateBufferUi();
            LogAppend("[Буфер] Временный буфер очищен вручную.");
        };

        _btnResetJournal.Click += (_, _) => _onOpenJournal?.Invoke();

        _btnSummarize.Click += async (_, _) =>
        {
            if (_onFileMemory is null) { LogAppend("[Память] Раскладка недоступна."); return; }
            _btnSummarize.Enabled = false;
            _btnSummarize.Text = "⏳ Раскладываю…";
            LogAppend("[Память] Раскладываю журнал по эпизодам и фактам (журнал при этом не меняется)…");
            try
            {
                LogAppend($"[Память] ✅ {await _onFileMemory()}");
                UpdateBufferUi();
            }
            catch (Exception ex)
            {
                LogAppend($"[Память] ❌ Ошибка: {ex.Message}");
            }
            finally
            {
                _btnSummarize.Text = "🧠 Разложить память сейчас";
                _btnSummarize.Enabled = true;
            }
        };

        _cbDevices.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _cbDevices.SelectedItem is not DeviceItem item) return;
            LogAppend($"[Микрофон] Переключение на устройство #{item.Index}: {item.Name}");
            _ears.SwitchDevice(item.Index);
            UpdateMicStatus();
        };

        _btnRestartMic.Click += (_, _) =>
        {
            LogAppend("[Микрофон] Принудительный перезапуск аудиопотока...");
            _ears.RestartMicrophone();
            UpdateMicStatus();
        };

        _trkVad.ValueChanged += (_, _) =>
        {
            if (_loading) return;
            _cfg.VadMinLevel = _trkVad.Value / 1000.0;
            _lblVadVal.Text = $"Порог VAD: {_cfg.VadMinLevel:F3}";
            _cfg.Save();
        };

        _btnPingStt.Click += async (_, _) => await CheckWhisperAsync();

        _btnRestartStt.Click += async (_, _) =>
        {
            _btnRestartStt.Enabled = false;
            LogAppend("[Whisper] Пинаю процесс whisper-server, перезапуск службы…");
            var prg = new Progress<string>(msg => LogAppend($"[Whisper] {msg}"));
            await _services.RestartSttAsync(prg);
            await CheckWhisperAsync();
            _btnRestartStt.Enabled = true;
        };

        _btnOpenDict.Click += (_, _) =>
        {
            var p = _ears.Learn?.CorrectionsPath;
            if (!string.IsNullOrEmpty(p))
            {
                if (!File.Exists(p)) File.WriteAllText(p, "{\n}\n");
                try { Process.Start(new ProcessStartInfo("notepad.exe", p) { UseShellExecute = true }); }
                catch (Exception ex) { Log.Error("open dict", ex); }
            }
        };

        _btnPingTts.Click += async (_, _) => await CheckTtsAsync();

        _btnRestartTts.Click += async (_, _) =>
        {
            _btnRestartTts.Enabled = false;
            LogAppend("[TTS] Пинаю процесс Qwen3-TTS, принудительный перезапуск службы…");
            var prg = new Progress<string>(msg => LogAppend($"[TTS] {msg}"));
            await _services.RestartTtsAsync(prg);
            await CheckTtsAsync();
            _btnRestartTts.Enabled = true;
        };

        _ears.LevelTick += OnLevelTick;
        _ears.TranscribeResult += OnTranscribeResult;

        _pingTimer.Tick += async (_, _) =>
        {
            UpdateBufferUi();
            // голос занят синтезом — проверка может ждать дольше 2 с: не копить их пачкой
            if (Interlocked.Exchange(ref _pinging, 1) == 1) return;
            try { await Task.WhenAll(CheckWhisperAsync(), CheckTtsAsync()); }
            finally { Volatile.Write(ref _pinging, 0); }
        };
        _pingTimer.Start();
        _levelTimer.Tick += (_, _) => DrawLevel();
        _levelTimer.Start();

        FormClosed += (_, _) =>
        {
            _pingTimer.Stop();
            _levelTimer.Stop();
            _levelTimer.Dispose();
            _ears.LevelTick -= OnLevelTick;
            _ears.TranscribeResult -= OnTranscribeResult;
            _cfg.Save();
        };

        _ = Task.WhenAll(CheckWhisperAsync(), CheckTtsAsync());
        Shown += async (_, _) => await LoadProfilesAsync();

        _btnBanHallucination.Click += (_, _) =>
        {
            var target = _txtLog.SelectedText.Trim();
            if (string.IsNullOrWhiteSpace(target))
                target = _lastHeardText?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(target))
            {
                LogAppend("[Фантом] Нет текста для штрафа (выделите фразу в логе или дождитесь распознавания).");
                return;
            }

            _onBanHallucination?.Invoke(target);
            LogAppend($"[Фантом] 🚫 Фраза «{target}» отправлена в чёрный список фантомов!");
        };
    }

    /// <summary>Списки «Кто» и кнопки награды/штрафа — из записанных голосов (сервер speaker_id).</summary>
    private async Task LoadProfilesAsync()
    {
        List<string> names = [];
        if (_speaker is not null && await _speaker.AliveAsync())
            names = (await _speaker.ListAsync()).Select(p => p.Name).Where(n => n != "Клава").Order().ToList();
        if (IsDisposed) return;
        _loading = true;
        foreach (var cb in _members)
        {
            cb.Items.Clear();
            cb.Items.Add(AnyVoice);
            foreach (var n in names) cb.Items.Add(n);
        }
        for (var i = 0; i < _members.Length; i++)
            _members[i].SelectedItem = i < _cfg.RoomMembers.Count && names.Contains(_cfg.RoomMembers[i]) ? _cfg.RoomMembers[i] : AnyVoice;
        _loading = false;
        ShowMemberBoxes();

        _rewardPanel.Controls.Clear();
        _penaltyPanel.Controls.Clear();
        _rewardPanel.Controls.Add(new Label { Text = "🌟 Верно узнала:", AutoSize = true, Margin = new Padding(0, 6, 6, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold), ForeColor = Color.DarkGreen });
        _penaltyPanel.Controls.Add(new Label { Text = "🎯 Ошибка, это был(а):", AutoSize = true, Margin = new Padding(0, 6, 6, 0), Font = new Font("Segoe UI", 9f, FontStyle.Bold), ForeColor = Color.Firebrick });
        if (names.Count == 0)
            _rewardPanel.Controls.Add(new Label { Text = "(сервер голосов не запущен или нет профилей — «Голосовые профили…» в меню Клавы)", AutoSize = true, Margin = new Padding(0, 6, 0, 0), ForeColor = Color.DimGray });
        foreach (var n in names)
        {
            var label = n.Replace('_', ' ');
            if (label.Length > 16) label = label[..16] + "…";
            var rb = new Button { Text = label, AutoSize = true, BackColor = Color.FromArgb(220, 255, 220), ForeColor = Color.DarkGreen };
            HookReward(rb, n);
            _rewardPanel.Controls.Add(rb);
            var pb = new Button { Text = label, AutoSize = true, BackColor = Color.FromArgb(255, 235, 235), ForeColor = Color.Firebrick };
            HookPenalty(pb, n);
            _penaltyPanel.Controls.Add(pb);
        }
        _penaltyPanel.Controls.Add(_btnBanHallucination);
    }

    /// <summary>Видно столько списков «Кто», сколько людей в компании.</summary>
    private void ShowMemberBoxes()
    {
        var n = Math.Clamp(_cfg.RoomPeopleCount, 1, _members.Length);
        for (var i = 0; i < _members.Length; i++) _members[i].Visible = i < n;
    }

    private void SaveMembers()
    {
        var n = Math.Clamp(_cfg.RoomPeopleCount, 1, _members.Length);
        _cfg.RoomMembers = _members.Take(n).Select(cb => cb.SelectedItem as string).Where(s => s is not null && s != AnyVoice).Select(s => s!).Distinct().ToList();
        if (_cfg.RoomMembers.Count > 0) _cfg.SpeakerIdEnabled = true;
        _cfg.Save();
        LogAppend(_cfg.RoomMembers.Count == 0
            ? "[Компания] Никто не выбран — слушаю всех без фильтра по голосу."
            : $"[Компания] Слушаю только: {string.Join(", ", _cfg.RoomMembers.Select(m => m.Replace('_', ' ')))}. Остальные голоса (телевизор, гости) — мимо, только в журнал.");
    }

    private void UpdateMicStatus()
    {
        if (_ears.IsOpen && _ears.LastError is null)
        {
            _lblMicStatus.Text = $"● Микрофон активен (устройство {_cfg.MicDevice})";
            _lblMicStatus.ForeColor = Color.ForestGreen;
        }
        else
        {
            _lblMicStatus.Text = $"● Ошибка микрофона: {_ears.LastError ?? "закрыт"}";
            _lblMicStatus.ForeColor = Color.Firebrick;
        }
    }

    private void OnLevelTick(double rms, double threshold, bool vadActive)
    {
        _levelRms = (float)rms;
        _levelThreshold = (float)threshold;
        _levelVad = vadActive;
    }

    private void DrawLevel()
    {
        if (IsDisposed || !Visible) return;
        double rms = _levelRms, threshold = _levelThreshold;
        var vadActive = _levelVad;
        {
            // Сглаживание индикатора
            _smoothedRms = _smoothedRms * 0.7 + rms * 0.3;
            var pct = Math.Clamp((int)(_smoothedRms * 350.0), 0, 100);
            _prgLevel.Value = pct;
            _lblRms.Text = $"Уровень: {pct} % (RMS {rms:F3})";

            if (vadActive)
            {
                _lblState.Text = "● Слышу речь (VAD активен, идёт запись)";
                _lblState.ForeColor = Color.OrangeRed;
            }
            else if (_ears.SpeakingBack)
            {
                _lblState.Text = "● Клава говорит (режим перебивания речи)";
                _lblState.ForeColor = Color.DodgerBlue;
            }
            else if (rms > threshold)
            {
                _lblState.Text = "● Начало голоса…";
                _lblState.ForeColor = Color.DarkGoldenrod;
            }
            else
            {
                _lblState.Text = "● Тишина (фон в норме)";
                _lblState.ForeColor = Color.ForestGreen;
            }
        }
    }

    private void HookReward(Button btn, string speaker)
    {
        btn.Click += async (_, _) =>
        {
            if (_onRewardSpeaker is null) { LogAppend("[Награда] Функция награды недоступна."); return; }
            btn.Enabled = false;
            LogAppend($"[Награда] Закрепляю верный голос «{speaker}» (награда модели)...");
            try
            {
                var (ok, msg) = await _onRewardSpeaker(speaker);
                LogAppend($"[Награда] {(ok ? "🌟" : "❌")} {msg}");
            }
            catch (Exception ex)
            {
                LogAppend($"[Награда] ❌ Ошибка: {ex.Message}");
            }
            finally
            {
                btn.Enabled = true;
            }
        };
    }

    private void HookPenalty(Button btn, string speaker)
    {
        btn.Click += async (_, _) =>
        {
            if (_onCorrectSpeaker is null) { LogAppend("[Коррекция] Функция штрафа недоступна."); return; }
            btn.Enabled = false;
            LogAppend($"[Коррекция] Штрафую ошибку модели и доучиваю профиль «{speaker}»...");
            try
            {
                var (ok, msg) = await _onCorrectSpeaker(speaker);
                LogAppend($"[Коррекция] {(ok ? "✅" : "❌")} {msg}");
            }
            catch (Exception ex)
            {
                LogAppend($"[Коррекция] ❌ Ошибка: {ex.Message}");
            }
            finally
            {
                btn.Enabled = true;
            }
        };
    }

    private void OnTranscribeResult(string text, double seconds, string? error)
    {
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            if (!string.IsNullOrEmpty(error))
                LogAppend($"[{time}] ❌ Ошибка распознавания ({seconds:F1} с): {error}");
            else if (text == "[неразборчиво]")
                LogAppend($"[{time}] ⚠️ Речь не разобрана ({seconds:F1} с) — звук сохранён, спикер зафиксирован!");
            else if (string.IsNullOrWhiteSpace(text))
            {
                if (seconds >= 0.8)
                    LogAppend($"[{time}] ⚠️ Слышал звук ({seconds:F1} с), но слова не разобраны");
                else
                    LogAppend($"[{time}] 🔇 Короткий шум отсечён ({seconds:F1} с)");
            }
        });
    }

    /// <summary>Добавить в лог распознанную фразу с указанием распознанного спикера и сходства.</summary>
    /// <param name="canBan">Это настоящие слова whisper (не служебная пометка) — их можно забанить как фантом.</param>
    public void AppendHeardLog(string? speaker, double similarity, string text, double seconds, bool canBan = true)
    {
        if (canBan) _lastHeardText = text;
        if (IsDisposed) return;
        BeginInvoke(() =>
        {
            var time = DateTime.Now.ToString("HH:mm:ss");
            var who = string.IsNullOrWhiteSpace(speaker) ? "Не опознан" : $"{speaker} ({similarity:F2})";
            LogAppend($"[{time}] 🎙️ [{who}] ({seconds:F1} с): «{text}»");
        });
    }

    private async Task CheckWhisperAsync()
    {
        var (alive, ping, info) = await _services.PingSttAsync();
        if (IsDisposed) return;
        if (alive)
        {
            _lblSttStatus.Text = $"● В сети: пинг {ping} мс ({info})";
            _lblSttStatus.ForeColor = Color.ForestGreen;
        }
        else
        {
            _lblSttStatus.Text = $"● Не отвечает ({info})";
            _lblSttStatus.ForeColor = Color.Firebrick;
        }

        if (_ears.Learn is { } l)
        {
            _lblLearnStatus.Text = $"● Доучивание: {l.CorrectionsCount} правил, {l.BannedCount} фантомов в бане, {l.RecentCount} фраз в адаптивном контексте";
            _lblLearnStatus.ForeColor = Color.ForestGreen;
        }
        else
        {
            _lblLearnStatus.Text = "● Доучивание: выключено";
            _lblLearnStatus.ForeColor = Color.DarkGray;
        }
    }

    private async Task CheckTtsAsync()
    {
        var (alive, ping, info) = await _services.PingTtsAsync();
        if (IsDisposed) return;
        if (alive)
        {
            _lblTtsStatus.Text = $"● В сети: пинг {ping} мс ({info})";
            _lblTtsStatus.ForeColor = Color.ForestGreen;
        }
        else
        {
            _lblTtsStatus.Text = $"● Не отвечает ({info})";
            _lblTtsStatus.ForeColor = Color.Firebrick;
        }
    }

    private void UpdateBufferUi()
    {
        if (IsDisposed) return;
        if (_getBufferStats is null)
        {
            _lblBatchStatus.Text = $"● Режим: {_cfg.RoomPeopleCount} чел. (ждёт {_cfg.DialogBatchMaxPhrases} фраз, затишье {_cfg.DialogSilenceTimeoutSec:F1} с)";
            return;
        }
        var (temp, max, total) = _getBufferStats();
        _lblBatchStatus.Text = $"● Временный буфер: {temp} / {max} реплик (затишье {_cfg.DialogSilenceTimeoutSec:F1} с)  |  Общий журнал: {total} записей";
        _lblBatchStatus.ForeColor = temp > 0 ? Color.DarkOrange : Color.ForestGreen;
    }

    private void LogAppend(string line)
    {
        if (_txtLog.IsDisposed) return;
        _txtLog.AppendText(line + Environment.NewLine);
        _txtLog.SelectionStart = _txtLog.TextLength;
        _txtLog.ScrollToCaret();
    }

    private sealed record DeviceItem(int Index, string Name)
    {
        public override string ToString() => Name;
    }
}
