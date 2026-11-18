using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Web.WebView2.Core;

namespace KlavaDesk;

/// <summary>
/// Прозрачное окно Клавы поверх всех окон. WebView2 рисует аватар через DirectComposition
/// (настоящая попиксельная прозрачность), окно обрезается по маске силуэта из страницы —
/// клики мимо Клавы уходят в окна под ней. Мышь: клик — погладить/ткнуть, перетаскивание — двигать,
/// колесо — зум, правая кнопка — меню. Зажатая клавиша (правый Ctrl) — говорить голосом.
/// </summary>
internal sealed partial class OverlayForm : Form
{
    // «Глянь на экран», «что у меня на экране», «видишь?» — тогда Клава читает текст с экрана.
    [System.Text.RegularExpressions.GeneratedRegex(@"(глян\w*|погляд\w*|посмотр\w*|смотри\s+(что|чё|сюда)|видишь|видно|что\s+у\s+меня|чё\s+у\s+меня|на\s+(мо[йём]\s+)?экран\w*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex LookIntentRx();

    // «Переключись на жминю», «подключайся к дипсику», «давай на клода» — сменить мозг. Срабатывает,
    // только если в хвосте нашлось имя мозга из aliases; иначе фраза идёт в разговор как обычно.
    [System.Text.RegularExpressions.GeneratedRegex(@"(переключ\w*|подключ\w*|перейд\w*|пересядь|смени\s+мозг\w*|давай)\s+(на|к|ко)\s+(?<who>.{2,40})", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SwitchIntentRx();

    private const int HotkeyText = 1;
    private const int HotkeyGameOverlay = 2;
    private const int HotkeyPanel = 3;
    private bool _isClickThroughActive;

    private readonly KlavaConfig _cfg;
    private readonly Brain _brain;
    private readonly Voice _voice;
    private readonly Ears _ears;
    private readonly Services _services;
    private readonly Hands _hands;
    private readonly Shell _shell;
    private readonly Speaker _speaker;
    private readonly WhisperLearn _whisperLearn;
    private readonly MemoryGraph _memoryGraph;
    private readonly FlightJournal _flightJournal;
    private readonly MemoryState _memoryState;
    /// <summary>Когда закончился прошлый ход разговора (для часов: «разговор молчал 2 ч») — при запуске из журнала.</summary>
    private DateTime? _prevTurnAt;
    private readonly LightMemory _lightMemory;
    private readonly FriendMemory _friendMemory;
    private readonly MemoryBank _memoryBank;
    private readonly System.Collections.Generic.List<(string Speaker, string Text, DateTime Time)> _tempBatch = new();
    private readonly object _batchGate = new();
    private readonly System.Windows.Forms.Timer _batchTimer = new() { Interval = 150 };
    private DateTime _lastBatchTime = DateTime.MinValue;
    private VoiceProfilesForm? _voicesForm;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 33 };
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _muteItem;
    private readonly ToolStripMenuItem _subtitlesItem;
    private readonly ToolStripMenuItem _handsFreeItem;
    private readonly ToolStripMenuItem _autopilotItem;
    private readonly ToolStripMenuItem _echoItem;
    private readonly ToolStripMenuItem _streamerItem;
    private readonly ToolStripMenuItem _clickThroughItem;
    private readonly ToolStripMenuItem _overFullscreenItem;
    private readonly ToolStripMenuItem _brainMenu = new("Мозг");
    private readonly ToolStripMenuItem _modeMenu = new("🎭 Режим общения");
    private TerminalForm? _terminalForm;
    private bool _switching;   // идёт смена мозга — второй смены не начинаем
    // старого мозга нет, пока грузится новый (свой на свой: один порт, одна видеокарта) — реплики ждут в буфере;
    // в остальных случаях старый мозг отвечает, пока новый поднимается
    private bool _switchBlocks;
    private string _switchTarget = "";
    private bool _speaking;   // страница ещё проигрывает её речь (до сообщения «idle»)
    private long _ticks;

    // Эхо: её голос из колонок микрофон слышит как чужую речь. Чтобы не принять себя за человека в комнате,
    // помним, что и когда она говорила, когда страница доиграет поставленные фразы, и ещё чуть держим микрофон
    // глухим после конца речи — хвост звука в колонках и отражение в комнате.
    private readonly List<(DateTime Start, DateTime End, string Text)> _played = new();
    private DateTime _playUntil = DateTime.MinValue;
    // сторож микрофона: цифровая тишина или нет данных — переподключаем с нарастающей паузой
    private DateTime _lastMicRestart = DateTime.MinValue;
    private int _micRestarts;
    private bool _micGated;        // микрофон с шумодавом: в тишине отдаёт ровный ноль — это норма
    private bool _micZeroRestart;  // последнее переподключение было из-за нуля, а не из-за пропавших данных
    // текущий ход: что он обрабатывает (бан фантома отменяет только свой ход) и из каких реплик собран (дозаказ)
    private string _turnInput = "";
    private bool _turnIsUser;
    private List<(string Speaker, string Text, DateTime Time)>? _turnBatch;
    private int _merges;
    // «поверх всех»: сторож смены активного окна (Win+D, полноэкранные окна)
    private IntPtr _winEventHook;
    private Native.WinEventProc? _winEventProc;
    // маска силуэта и «подвинься» для лапок
    private (int W, int H, int Cell, string Bits)? _lastMask;
    private byte[]? _maskBits;
    private int _maskW, _maskH, _maskCell;
    private bool _cursorOverFigure = true;
    private int _clickThrough;
    // голосовые профили: порог «свой» от модели сервера
    private double _speakerThreshold = 0.75;
    private double _speakerThresholdShort = 0.69;
    private string _speakerModel = "";
    private DateTime _warnedNoSpeaker = DateTime.MinValue;
    private DateTime _warnedProfile = DateTime.MinValue;
    private DateTime _echoTailUntil = DateTime.MinValue;
    private const int EchoTailMs = 450;
    /// <summary>Профиль голоса самой Клавы в speaker_id: всё, что опознано как он, — её эхо, не человек.</summary>
    private const string SelfVoiceName = "Клава";

    private DComp? _dcomp;
    private CoreWebView2CompositionController? _web;
    private bool _pageReady;
    private double _scale = 1;
    private Point _lastCss = new(-100000, -100000);
    private bool _pttDown;
    private CancellationTokenSource? _turn;
    private int _sayId;
    private bool _mouseDown, _dragging;
    private Point _downScreen, _downWindow;
    private DateTime _lastPoke = DateTime.MinValue;
    private bool _busy;
    private bool _ready;      // мозг, слух и голос подняты — до этого она не слушает и не отвечает
    private bool _voiceTurn;  // ответ уже зазвучал и ещё не закончен — между фразами колонки тоже «говорят»
    private volatile bool _pendingFlush; // full-duplex: во время речи Клавы набрался порог / обращение по имени — сбросить сразу после окончания хода
    private byte[]? _lastUtteranceWav;
    private string? _lastSpeakerName;
    private string _lastUtteranceText = "";
    private ViewForm? _viewForm;
    private AudioSettingsForm? _audioSettingsForm;
    private readonly System.Windows.Forms.Timer _saveLater = new() { Interval = 1500 };
    private string _lastSpoken = "";
    private InputForm? _input;
    // Голос иногда залипает: /health отвечает 200, а /tts виснет / рвёт соединение. Считаем провалы подряд;
    // 3 в ряд — прибиваем python-сервер и поднимаем заново. Не чаще, чем раз в минуту, чтобы не крутить рестарт по кругу.
    private int _ttsFails;
    private DateTime _lastTtsRestart = DateTime.MinValue;
    private volatile bool _ttsRestarting;

    // глаза: что на переднем плане и когда она последний раз сама об этом заговорила
    private Eyes? _eyes;
    /// <summary>«Наблюдай»: журнал экрана и отчёт.</summary>
    private readonly Watcher _watcher;
    /// <summary>Смотрит игру вместе с другом: счёт, события, коды (GameWatch).</summary>
    private readonly GameWatch _gameWatch;
    private readonly System.Windows.Forms.Timer _gameTimer = new() { Interval = 3000 };
    /// <summary>Кто ушёл из комнаты (по словам людей и её инструменту room) — их голоса не узнаются.</summary>
    private readonly Presence _presence;
    private readonly System.Windows.Forms.Timer _watch = new() { Interval = 15000 };
    /// <summary>Долгая задача и её «мотор»: пока не закрыта — Клава сама к ней возвращается (см. TaskTick).</summary>
    private readonly TaskBoard _tasks;
    private readonly System.Windows.Forms.Timer _taskTimer = new() { Interval = 1000 };
    private DateTime _lastTurnEnded = DateTime.MinValue;
    private readonly ToolStripMenuItem _watchItem;
    private string _glanceKey = "";
    private DateTime _glanceSince = DateTime.UtcNow;
    private bool _glanceCommented = true;
    private DateTime _lastTalk = DateTime.UtcNow;
    private DateTime _lastWatchComment = DateTime.UtcNow;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOREDIRECTIONBITMAP | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    public OverlayForm(KlavaConfig cfg)
    {
        _cfg = cfg;
        _brain = new Brain(cfg);
        _voice = new Voice(cfg);
        _ears = new Ears(cfg);
        _services = new Services(cfg);
        _hands = new Hands(this);
        _shell = new Shell();
        _terminalForm = new TerminalForm(_shell, cfg.Abs("workspace"));
        // подписка одна на всё время: события идут в то окно терминала, что живо сейчас
        _shell.CommandStarted += (cmd, cwd) => _terminalForm?.AppendCommand(cmd, cwd);
        _shell.OutputChunk += text => _terminalForm?.AppendOutput(text);
        _shell.CommandCompleted += (code, ms, cwd) => _terminalForm?.FinishCommand(code, ms, cwd);
        // фоновый агент: свой pwsh (долгая команда не держит общий), его команды и мысли — в тот же кибер-терминал
        AgentRunner.Shell.CommandStarted += (cmd, cwd) => _terminalForm?.AppendCommand("🐾 " + cmd, cwd);
        AgentRunner.Shell.OutputChunk += text => _terminalForm?.AppendOutput(text);
        AgentRunner.Shell.CommandCompleted += (code, ms, cwd) => _terminalForm?.FinishCommand(code, ms, cwd);
        AgentRunner.Note += line => _terminalForm?.AppendNote(line);
        WireTerminal(_terminalForm);
        // CS 1.6: плагин в игре шлёт события её бота — она комментирует (мост слушает всё время, игра подключается сама)
        CsBridge.Line += line => { if (IsHandleCreated) BeginInvoke(() => OnCsLine(line)); };
        CsBridge.Start(cfg);
        // агенты Claude Code (AgentHub): итог хода — ей самой, расскажет другу (ждёт в очереди, если она говорит или поёт)
        AgentHub.TurnDone += (s, result) => BeginInvoke(() =>
            QueueEvent($"(Твой агент #{s.Id} «{s.Title}» закончил ход. Итог: {(result.Length > 1500 ? result[..1500] + "…" : result)}\n" +
                       "Двумя-тремя живыми фразами расскажи другу, что получилось; не вышло — скажи честно и что предлагаешь. " +
                       "Нужно продолжить — дай агенту указание (agent tell), он помнит сделанное.)"));
        // итог прежнего агента — ей самой: расскажет другу, даже если в этот момент говорит (ждёт в очереди)
        AgentRunner.Finished += (goal, report) => BeginInvoke(() =>
            QueueEvent($"(Твой фоновый агент закончил задачу «{(goal.Length > 120 ? goal[..120] + "…" : goal)}». Итог: {report}\n" +
                       "Двумя-тремя живыми фразами расскажи другу, что получилось; не вышло — скажи честно и что предлагаешь.)"));
        _speaker = new Speaker(cfg);
        // память: имена людей в одном виде → журнал (старые журналы сами переезжают в data\journal) → часы и сеансы
        People.Load(cfg);
        _flightJournal = new FlightJournal(cfg.Abs(@"data"));
        _prevTurnAt = _flightJournal.LastTalk();
        _memoryState = new MemoryState(cfg.Abs(@"data\memory"));
        MemoryState.Current = _memoryState;
        _memoryState.Begin(_prevTurnAt);
        _flightJournal.LogMark("запуск");
        _lightMemory = new LightMemory(_flightJournal);
        // долгая память о людях: факты о каждом друге и итоги прошедших дней (data\memory)
        _friendMemory = new FriendMemory(cfg.Abs(@"data\memory"), _brain, _flightJournal);
        // банк памяти: журнал по полочкам (эпизоды, планы, заметки) и поиск по нему; раскладка прошлых дней — в фоне
        _memoryBank = new MemoryBank(cfg.Abs(@"data\memory"), _brain, _flightJournal, _friendMemory);
        _friendMemory.DayDigest = _memoryBank.DayDigest;   // итог дня — по эпизодам всего дня, а не по хвосту журнала
        WardrobeStore.Refresh(cfg);   // её собственные наряды и сменённые обувь/шляпа — для Look и выбора по словам
        MemoryBank.Current = _memoryBank;
        Log.Write($"банк памяти: {_memoryBank.Describe()}");
        // горячая ассоциативная память (граф фактов, векторы bge-m3 на процессоре): засев, векторы и «сон» — в фоне
        _memoryGraph = new MemoryGraph(cfg);
        MemoryGraph.Current = _memoryGraph;
        _ = Task.Delay(TimeSpan.FromSeconds(8)).ContinueWith(_ => _memoryGraph.StartAsync(_memoryBank, _friendMemory), TaskScheduler.Default).Unwrap();
        _ = Task.Delay(TimeSpan.FromSeconds(90)).ContinueWith(_ => _memoryBank.ConsolidateAsync(), TaskScheduler.Default);
        // Фоновая доучивалка whisper: adaptive prompt + correction learning. Живёт всё время работы Клавы.
        _whisperLearn = new WhisperLearn(cfg.Abs(@"data"));
        _ears.Learn = _whisperLearn;
        _whisperLearn.Start();
        _watcher = new Watcher(cfg, () => _eyes, _brain);
        _watcher.Noticed += e => BeginInvoke(() => OnWatchNoticed(e));
        _presence = new Presence(cfg.Abs(@"data\memory\presence.json"));
        SongLibrary = cfg.Abs(@"data\music\library");   // её фонотека (Карамельдансен) — для поиска песен по названию
        _gameWatch = new GameWatch(cfg, () => _eyes, _brain)
        {
            Say = SayFromGame,
            // речь из видео — тем же whisper, что слушает людей
            Transcribe = async (wav, ct) => (await _ears.TranscribeAsync(wav, ct).ConfigureAwait(false)).Text,
        };
        _brain.Hurry = () => _gameWatch.Active && _gameWatch.Hwnd != IntPtr.Zero;   // идёт игра — медленный мозг не ждём
        _gameTimer.Tick += (_, _) => GameTick();
        _gameTimer.Start();
        _tasks = new TaskBoard(cfg.Abs(@"data\task.json"));
        _tasks.Finished += (summary, cancelled) => BeginInvoke(() => Bubble(cancelled ? $"Задачу сняла: {summary}" : $"Готово: {summary}"));
        _taskTimer.Tick += (_, _) => TaskTick();
        _taskTimer.Start();
        // итог игры не теряется, даже если она в этот момент говорит: ждёт в очереди и звучит, как только освободится
        TetrisBot.Finished += result => BeginInvoke(() =>
            QueueEvent($"(Ты доиграла в тетрис: {result}. Одной-двумя живыми фразами скажи другу, как сыграла — похвастайся или посмейся над собой.)"));
        // играет — сидит в кресле за игровым столом, доиграла — встаёт
        GameMode.Changed += on => BeginInvoke(() => Post(on ? new { type = "gesture", name = "gaming" } : new { type = "gesture_stop", name = "gaming" }));
        ZumaBot.Finished += result => BeginInvoke(() =>
            QueueEvent($"(Ты доиграла в Зуму: {result}. Одной-двумя живыми фразами скажи другу, как сыграла — честно: проиграла — так и скажи, без «готово».)"));
        BotSchool.Finished += result => BeginInvoke(() =>
            QueueEvent($"(Ты смотрела, как играют боты, и училась у них: {result}. Одной-двумя живыми фразами скажи другу, что запомнила и что дальше — учёба идёт сама.)"));
        ArenaPlayer.Finished += result => BeginInvoke(() =>
            QueueEvent($"(Ты доиграла в шутер сама (Арена: глаза-нейронка, прицел и лапки твои): {result}. Одной-двумя живыми фразами скажи другу, как сыграла — честно, с фрагами и смертями.)"));
        GamePlayer.Finished += result => BeginInvoke(() =>
            QueueEvent($"(Ты доиграла по выученному (нейронка смотрела, ты жала лапками): {result}. Одной-двумя живыми фразами скажи другу, как вышло — честно.)"));
        // отложенные дела («когда лимит сбросится — нажми Enter»): живут в фоне, итог — репликой
        Later.Fired += result => BeginInvoke(() => QueueEvent(result.StartsWith('(') ? result : $"({result} Скажи другу коротко.)"));
        Later.Start(cfg.Abs(@"data\later.json"));
        _brain.Toolbox = new ToolBox
        {
            Hands = _hands,
            Shell = _shell,
            Autopilot = _cfg.Autopilot,
            Capture = () => (_eyes ?? throw new InvalidOperationException("глаза ещё не открылись")).CaptureAsync(1600, _cfg.ScreenTextMaxChars),
            ShowTerminal = () => BeginInvoke(() => ShowTerminalForm(activate: false)),
            TypeInTerminal = (cmd, timeout, ct) => Terminal().TypeAndRunAsync(cmd, timeout, ct),
            CaptureRegion = r => (_eyes ?? throw new InvalidOperationException("глаза ещё не открылись")).CaptureAsync(1600, _cfg.ScreenTextMaxChars, r),
            FindText = q => (_eyes ?? throw new InvalidOperationException("глаза ещё не открылись")).FindTextAsync(q),
            SaveScreenshot = (p, all) => (_eyes ?? throw new InvalidOperationException("глаза ещё не открылись")).SaveScreenshotAsync(p, all),
            WorkDir = cfg.Abs("workspace"),
            Watcher = _watcher,
            Tasks = _tasks,
            Music = (action, args) => MusicToolAsync(action, args),
            Wardrobe = (action, args) => WardrobeToolAsync(action, args),
            Craft = (action, args, allow) => CraftToolAsync(action, args, allow),
            Room = (action, args) => Task.FromResult(RoomTool(action, args)),
            Game = (action, args) => Task.FromResult(GameWatchTool(action, args)),
            Activity = kind => BeginInvoke(() => Post(new { type = "activity", kind })),
            HideSelfFromCapture = on =>
            {
                void Set() { if (!IsDisposed) Native.SetWindowDisplayAffinity(Handle, on ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE); }
                if (InvokeRequired) Invoke(Set); else Set();
            },
        };
        _brain.TakeInterjections = TakeInterjections;
        _brain.QuietAfterTools = () => SongPlaying || _pendingSong is not null;
        _hands.MakeWay = MakeWay;
        _hands.ActiveChanged += on => BeginInvoke(() => { Post(new { type = "paw", on }); if (on) Bubble("Взяла мышку, мур. Дёрни мышь или нажми Esc — сразу отпущу."); });
        // друг забрал мышь — руки прочь сразу, но фразу договаривает (раньше обрывалась на полуслове)
        _hands.Interrupted += why => BeginInvoke(() => { FinishCurrentPhrase(); Bubble(why == "Esc" ? "Поняла, отпустила." : "Ой, всё, руки убрала."); });

        Text = "Клава";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        var wa = Screen.PrimaryScreen!.WorkingArea;
        // в полный рост окно высокое: на экране с крупным масштабом не даём ему вылезти за панель задач
        Size = ScaledSize();
        Location = new Point(wa.Right - Width - 20, wa.Bottom - Height);

        _menu = new ContextMenuStrip();
        // открылось меню — сразу над Клавой (она тоже «поверх всех» и могла оказаться выше)
        _menu.Opened += (_, _) => Native.SetWindowPos(_menu.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _menu.Items.Add(new ToolStripMenuItem("⚙ Панель настроек Клавы…  (Ctrl+Alt+K)", null, (_, _) => ShowControlPanel())
            { Font = new Font(_menu.Font, FontStyle.Bold) });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add($"Написать Клаве…  (Ctrl+Alt+{cfg.TextHotkeyLetter})", null, (_, _) => ShowInput());
        foreach (var b in cfg.Brains)
        {
            var item = new ToolStripMenuItem(b.DisplayName) { Tag = b };
            if (b.ModelsFromServer)
            {
                // модели — подменю, список берётся с сервера при открытии
                item.DropDownItems.Add(new ToolStripMenuItem("(загружаю список моделей…)") { Enabled = false });
                item.DropDownOpening += (_, _) => _ = FillModelsAsync(item, b);
            }
            else item.Click += (_, _) => _ = SwitchBrainAsync(b);
            _brainMenu.DropDownItems.Add(item);
        }
        UpdateBrainMenu();
        _brainMenu.DropDownOpening += (_, _) => _ = RefreshClaudeLoginAsync();
        _ = RefreshClaudeLoginAsync();
        _menu.Items.Add(_brainMenu);
        _menu.Items.Add(BuildWorkModeMenu());
        _menu.Items.Add("🧠 Настройки мозгов (добавить/изменить)…", null, (_, _) => ShowBrainProfiles());
        _handsFreeItem = new ToolStripMenuItem("Слушать без кнопки", null, (_, _) => SetHandsFree(!_cfg.HandsFree)) { Checked = cfg.HandsFree };
        _menu.Items.Add(_handsFreeItem);
        _muteItem = new ToolStripMenuItem("Без звука", null, (_, _) => SetMuted(!_cfg.Muted)) { Checked = cfg.Muted };
        _menu.Items.Add(_muteItem);
        _subtitlesItem = new ToolStripMenuItem("💬 Суфлёр: текст её реплик", null, (_, _) => SetSubtitles(!_cfg.Subtitles)) { Checked = cfg.Subtitles };
        _menu.Items.Add(_subtitlesItem);
        // AEC MVP: жёстко отрубить мик пока говорит (для громких колонок без наушников).
        _echoItem = new ToolStripMenuItem("🔇 Не слышать себя из колонок", null, (_, _) => SetEchoBlock(!_cfg.EchoBlock)) { Checked = cfg.EchoBlock };
        _menu.Items.Add(_echoItem);
        _autopilotItem = new ToolStripMenuItem("🚀 Автопилот (без спроса на безопасное)", null, (_, _) => SetAutopilot(!_cfg.Autopilot)) { Checked = cfg.Autopilot };
        _menu.Items.Add(_autopilotItem);
        _menu.Items.Add("🖥️ Кибер-терминал Клавы…", null, (_, _) => ShowTerminalForm());
        var peopleMenu = new ToolStripMenuItem("👥 Компания в комнате");
        peopleMenu.DropDownItems.Add(new ToolStripMenuItem("(загружаю голоса…)") { Enabled = false });
        peopleMenu.DropDownOpening += (_, _) => _ = FillPeopleMenuAsync(peopleMenu);
        _menu.Items.Add(peopleMenu);
        // Режим общения (PersonaModes): обычная, с матом, без мата, умеренная, рассудительная. Голосом — «режим без мата» и т. п.
        var current = PersonaModes.Get(cfg.PersonaMode);
        foreach (var m in PersonaModes.All)
        {
            var mi = new ToolStripMenuItem(m.Title) { Tag = m.Id, Checked = m.Id == current.Id };
            var picked = m;
            mi.Click += (_, _) => SetPersonaMode(picked);
            _modeMenu.DropDownItems.Add(mi);
        }
        _menu.Items.Add(_modeMenu);
        // Игростример и оверлей поверх игр (CS2, CS:GO, Point Blank и др.)
        var streamerMenu = new ToolStripMenuItem("🎮 Игростример (поверх CS / игр)");
        _streamerItem = new ToolStripMenuItem("Включить режим стримера  (Ctrl+Alt+G)", null, (_, _) => SetStreamer(!_cfg.GameStreamerMode)) { Checked = cfg.GameStreamerMode };
        _clickThroughItem = new ToolStripMenuItem("Сквозной проклик мыши (не сбивать прицел)", null, (_, _) => SetClickThrough(!_cfg.GameClickThrough)) { Checked = cfg.GameClickThrough };
        _overFullscreenItem = new ToolStripMenuItem("Поверх любых полноэкранных окон", null, (_, _) => SetOverFullscreen(!_cfg.OverFullscreen)) { Checked = cfg.OverFullscreen };
        streamerMenu.DropDownItems.Add(_streamerItem);
        streamerMenu.DropDownItems.Add(_clickThroughItem);
        streamerMenu.DropDownItems.Add(_overFullscreenItem);
        _menu.Items.Add(streamerMenu);
        // учёба в играх: друг играет — Клава записывает кадры и его клаву с мышью, потом учится (gamer train)
        var miRecord = new ToolStripMenuItem("🎓 Записывать мою игру (учёба)") { CheckOnClick = true };
        miRecord.CheckedChanged += (_, _) =>
        {
            if (miRecord.Checked == GameRecorder.Recording) return;
            Bubble(miRecord.Checked ? GameRecorder.Start(_cfg.Abs(Path.Combine("gamer", "data")), "") : GameRecorder.Stop());
        };
        _menu.Opening += (_, _) => miRecord.Checked = GameRecorder.Recording;
        _menu.Items.Add(miRecord);
        // игру Клава берёт без спроса — зато отозвать её можно в любой момент (кроме Esc/Backspace и «стоп» голосом)
        var miTakeBack = new ToolStripMenuItem("⏹ Забрать игру у Клавы (Зума, тетрис, геймер)", null, (_, _) =>
        {
            var said = new List<string>();
            if (ZumaBot.Playing) said.Add(ZumaBot.Stop());
            if (TetrisBot.Playing) said.Add(TetrisBot.Stop());
            if (GamePlayer.Playing) said.Add(GamePlayer.Stop());
            if (ArenaPlayer.Playing) said.Add(ArenaPlayer.Stop());
            if (BotSchool.Running) said.Add(BotSchool.Stop());
            Log.Write("друг забрал игру у Клавы (пункт меню)");
            Bubble(said.Count > 0 ? "Всё-всё, отдаю! " + string.Join(" ", said) : "Да я и не играю сейчас, лапки свободны.");
        });
        _menu.Opening += (_, _) => miTakeBack.Enabled = ZumaBot.Playing || TetrisBot.Playing || GamePlayer.Playing || ArenaPlayer.Playing || BotSchool.Running;
        _menu.Items.Add(miTakeBack);
        _menu.Items.Add("Глянь на экран", null, (_, _) => _ = ConverseAsync("(Друг просит глянуть на его экран и сказать, что там.)", look: true));
        _menu.Items.Add("🍺 Выпить пивка / чокнуться", null, (_, _) =>
        {
            Post(new { type = "gesture", name = "cheers" });
            Bubble("Дзынь! Налито, за наше здоровье, мур!");
        });
        _menu.Items.Add("🚬 Затянуться сигареткой (напас)", null, (_, _) =>
        {
            Post(new { type = "gesture", name = "smoke" });
            MarkActivity("куришь сигарету — дым в комнате, затягиваешься", 60);
            Bubble("Фууух… смачный напас! Дымим по красоте, мур~ 🚬");
        });
        _menu.Items.Add("💻 Покодить лапками", null, (_, _) =>
        {
            Post(new { type = "gesture", name = "typingBrief" });
            Bubble("Ща закодим по красоте, лапки уже на клавиатуре!");
        });
        _menu.Items.Add("🐾 Ня / лапки-царапки", null, (_, _) =>
        {
            Post(new { type = "gesture", name = "nya" });
            Bubble("Ня! Мур-мур, лапки наготове!");
        });
        // гардероб (web/wardrobe.js): друг 29.09 — «чтобы она себе костюмчик подобрать могла»
        var dressMenu = new ToolStripMenuItem("👗 Гардероб");
        foreach (var (id, title, _) in Wardrobe.All)
            dressMenu.DropDownItems.Add(new ToolStripMenuItem(title, null, (_, _) => ApplyOutfit(id, announce: true)) { Tag = id });
        dressMenu.DropDownOpening += (_, _) =>
        {
            // её собственные наряды (сама собрала инструментом wardrobe) — в конец списка, заново при каждом открытии
            for (var i = dressMenu.DropDownItems.Count - 1; i >= Wardrobe.All.Length; i--) dressMenu.DropDownItems.RemoveAt(i);
            WardrobeStore.Refresh(_cfg);
            var own = WardrobeStore.Custom(_cfg);
            if (own.Count > 0) dressMenu.DropDownItems.Add(new ToolStripSeparator());
            foreach (var (name, _, title, _) in own)
                dressMenu.DropDownItems.Add(new ToolStripMenuItem($"✂ {title} (сама сшила)", null, (_, _) => ApplyOutfit(name, announce: true)) { Tag = name });
            foreach (var it in dressMenu.DropDownItems.OfType<ToolStripMenuItem>()) it.Checked = it.Tag as string == _cfg.Outfit;
        };
        _menu.Items.Add(dressMenu);
        // музыка (music\klava_music.py): друг 30.09 — «пусть слушает музыку как человек и поёт каверы по-русски»
        _menu.Items.Add(BuildMusicMenu());
        // обстановка и дела (web/props.js): её хотелки 30.09 — кресло, кровать с балдахином, ванна с пеной, коврик, танцы
        var placeMenu = new ToolStripMenuItem("🛋 Обстановка и дела");
        foreach (var (title, name, bubble) in new[]
        {
            ("🪑 Посидеть в дедовском кресле", "armchair", "О, дедовское кресло — с салфеточкой. Сижу, как бабуля на даче."),
            ("💆 Массажное кресло", "chair", "Мррр, массажик… вот это жизнь."),
            ("🛏 Прилечь на кровать", "bed", "Балдахин, подушки — царская кошка отдыхает."),
            ("😴 Уснуть", "sleep", "Сплю… не будить без рыбки."),
            ("🛁 Ванна с пеной", "bath", "Пенка! Только не брызгайся."),
            ("💃 Потанцевать", "danceBrief", "Музыку погромче — погнали!"),
            ("🍬 Карамельдансен", "caramell", "Ушки на макушке, бёдрами влево-вправо — у-у-уа-уа!"),
            ("🧍 Встать", "stand", "Всё, я на лапках."),
        })
            placeMenu.DropDownItems.Add(new ToolStripMenuItem(title, null, (_, _) => {
                if (name == "caramell") Caramell();
                else if (name == "sleep") GoToSleep("меню обстановки");
                else if (name == "stand") { WakeUp("меню обстановки"); Post(new { type = "gesture", name }); }
                else Post(new { type = "gesture", name });
                Bubble(bubble);
            }));
        var rugItem = new ToolStripMenuItem("🧶 Коврик под лапки") { Checked = _cfg.Rug, CheckOnClick = true };
        rugItem.Click += (_, _) => { _cfg.Rug = rugItem.Checked; _cfg.Save(); Post(new { type = "rug", on = _cfg.Rug }); };
        placeMenu.DropDownItems.Add(new ToolStripSeparator());
        placeMenu.DropDownItems.Add(rugItem);
        _menu.Items.Add(placeMenu);
        // еда и газы (web/snacks.js): друг 29.09 — «пуки и рыги как в Рике и Морти, чипсы, рыба, чеснок, зелёный пар»
        var snackMenu = new ToolStripMenuItem("🍟 Пожрать и побезобразничать");
        foreach (var (title, name, bubble) in new[]
        {
            ("🍟 Похрустеть чипсами", "chips", "Хрум-хрум, со сметанкой, мур!"),
            ("🐟 Схомячить рыбку", "fish", "Рыбка! Мой кошачий наркотик, ням."),
            ("🧄 Зажевать чеснок", "garlic", "Ну всё, теперь ко мне не целоваться, зелёный выхлоп гарантирован."),
            ("🦑 Кальмар в темпуре", "squid", "Хрусть! Кальмарчик в кляре — Кот, ты лучший."),
            ("💨 Рыгнуть", "burp", "Пардон… хотя нет, не пардон."),
            ("🍑 Пукнуть", "fart", "Это не я, это кимоно шуршит."),
        })
            snackMenu.DropDownItems.Add(new ToolStripMenuItem(title, null, (_, _) => { Post(new { type = "gesture", name }); Bubble(bubble); }));
        _menu.Items.Add(snackMenu);
        // Меню поз ушей: явное управление позой + твитч отдельно каждым.
        var earsMenu = new ToolStripMenuItem("👂 Ушки");
        void AddEarPose(string title, string pose, string? bubble = null)
        {
            earsMenu.DropDownItems.Add(new ToolStripMenuItem(title, null, (_, _) =>
            {
                Post(new { type = "ears", pose });
                if (bubble is not null) Bubble(bubble);
            }));
        }
        AddEarPose("Обычные (rest)", "rest");
        AddEarPose("Торчком вверх (насторожилась)", "perked", "Ушки топориком, я вся внимание, мур!");
        AddEarPose("Прижать назад (злость)", "flat", "Прижала ушки, ты меня бесишь.");
        AddEarPose("Свести вперёд (слушает)", "converge", "Ушки сведены, вся в уши!");
        AddEarPose("Развести в стороны (расслабон)", "spread", "Ушки в стороны, я на чилле, мур.");
        AddEarPose("Повесить (грустно)", "droop", "Ушки повисли…");
        AddEarPose("Alert (свести и вверх)", "alert", "Ушки топориком, слушаю всеми фибрами!");
        earsMenu.DropDownItems.Add(new ToolStripSeparator());
        earsMenu.DropDownItems.Add(new ToolStripMenuItem("Дёрнуть левым", null, (_, _) => Post(new { type = "ears", twitch = "L" })));
        earsMenu.DropDownItems.Add(new ToolStripMenuItem("Дёрнуть правым", null, (_, _) => Post(new { type = "ears", twitch = "R" })));
        earsMenu.DropDownItems.Add(new ToolStripMenuItem("Дёрнуть обоими", null, (_, _) => Post(new { type = "ears", twitch = "both" })));
        _menu.Items.Add(earsMenu);
        _menu.Items.Add("🤦 Рукалицо", null, (_, _) =>
        {
            Post(new { type = "gesture", name = "facepalm" });
            Bubble("Ой, бляяя… рукалицо просто!");
        });
        _watchItem = new ToolStripMenuItem("Подглядывать за экраном", null, (_, _) => SetWatchScreen(!_cfg.WatchScreen)) { Checked = cfg.WatchScreen };
        _menu.Items.Add(_watchItem);
        _menu.Items.Add("👁 Наблюдать и вести журнал экрана", null, (_, _) => ToggleWatcher());
        _menu.Items.Add("📝 Отчёт наблюдения (что было)", null, (_, _) => _ = OpenWatchReportAsync());
        _menu.Items.Add("Настройки вида…  (размер, приближение, поворот)", null, (_, _) => ShowViewForm());
        _menu.Items.Add("🎙️ Настройки микрофона и слуха…", null, (_, _) => ShowAudioSettingsForm());
        _menu.Items.Add("👤 Голосовые профили…", null, (_, _) => _ = ShowVoiceProfilesAsync());
        _menu.Items.Add("Забыть разговор", null, (_, _) => { _brain.Forget(); Bubble("(забыла всё, что было)"); });
        if (cfg.DevTools) _menu.Items.Add("Инструменты разработчика", null, (_, _) => _web?.CoreWebView2.OpenDevToolsWindow());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Выход", null, (_, _) => Close());

        _tray = new NotifyIcon { Text = "Клава — щелчок: панель настроек", Icon = MakeTrayIcon(), ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowControlPanel(); };
        _tray.DoubleClick += (_, _) => ShowOnDesktop();
        _menu.Opening += (_, _) => SyncMenuChecks();   // панель могла поменять настройки из своего потока

        _tick.Tick += OnTick;
        _watch.Tick += OnWatch;
        _mcPoll.Tick += OnMcPoll;
        _saveLater.Tick += (_, _) => { _saveLater.Stop(); _cfg.Save(); };
        _batchTimer.Tick += OnBatchTimerTick;
        _batchTimer.Start();
    }

    // ---------------- запуск ----------------

    public void ResetFullWindowRegion()
    {
        Native.SetWindowRgn(Handle, IntPtr.Zero, true);
    }

    public void ShowOnDesktop()
    {
        if (IsDisposed) return;
        Visible = true;
        ResetFullWindowRegion();
        Native.ShowWindow(Handle, Native.SW_SHOW);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, Left, Top, Width, Height, Native.SWP_SHOWWINDOW);
        Native.SetForegroundWindow(Handle);
        Bubble("Я тут, мур! Вылезла на рабочий стол!");
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Native.RegisterHotKey(Handle, HotkeyText, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, char.ToUpperInvariant(_cfg.TextHotkeyLetter));
        Native.RegisterHotKey(Handle, HotkeyGameOverlay, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 'G');
        Native.RegisterHotKey(Handle, HotkeyPanel, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, 'K');
        try
        {
            await InitWebAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось поднять окно Клавы:\n{ex.Message}", "Клава", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }
        Visible = true;
        ResetFullWindowRegion();
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, Left, Top, Width, Height, Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);
        _tick.Start();
        _eyes = new Eyes(Handle)
        {
            // на своих же снимках её не видно: под ней может быть та самая кнопка
            HideSelfFromCapture = on =>
            {
                void Set() { if (!IsDisposed) Native.SetWindowDisplayAffinity(Handle, on ? Native.WDA_EXCLUDEFROMCAPTURE : Native.WDA_NONE); }
                if (InvokeRequired) Invoke(Set); else Set();
            },
        };
        // смена активного окна (в том числе «Показать рабочий стол» Win+D) — вернуть Клаву поверх всех
        _winEventProc = (_, _, hwnd, _, _, _, _) => BeginInvoke(() => OnForegroundChanged(hwnd));
        _winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _winEventProc, 0, 0,
            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        _ = AutoSetup.QuickAsync(_cfg);
        _ears.HandsFree = _cfg.HandsFree;
        _ears.Deaf = true;   // до приветствия не слушаем
        _ears.SpeechStarted += () => BeginInvoke(() =>
        {
            // НЕ обрываем Клавину речь на самом факте детекции — сначала дождёмся распознанной фразы.
            // Если это её собственное эхо из колонок — распознавание вернёт мусор/пустоту, и Клава договорит.
            // Реальный обрыв делаем уже в HandleUtteranceAsync, когда есть текст.
            // Поёт — микрофон слышит песню и болтовню вокруг: не отвлекается, голову «слушаю» не наклоняет.
            if (!SongPlaying) Post(new { type = "state", value = "listening" });
        });
        _ears.Utterance += (wav, seconds) => BeginInvoke(() => _ = HandleUtteranceAsync(wav, seconds, "голос"));
        _ears.CommandUtterance += (wav, seconds) => BeginInvoke(() => _ = HandleCommandWhileSpeakingAsync(wav, seconds));
        try { _ears.Open(); }
        catch (Exception ex) { Log.Error("микрофон", ex); Bubble($"Микрофон не открылся: {ex.Message}"); }
        // слух на колонки: что микрофон слышит из колонок (видео, игра, её голос) — не люди в комнате
        _speakerTap = SpeakerTap.Start();
        _ = StartServicesGuardedAsync();
    }

    private async Task InitWebAsync()
    {
        _dcomp = new DComp(Handle);
        var options = new CoreWebView2EnvironmentOptions(additionalBrowserArguments:
            "--autoplay-policy=no-user-gesture-required --disable-background-timer-throttling " +
            "--disable-renderer-backgrounding --disable-backgrounding-occluded-windows --disable-http-cache");
        var env = await CoreWebView2Environment.CreateAsync(null, _cfg.Abs(@"data\webview"), options);
        _web = await env.CreateCoreWebView2CompositionControllerAsync(Handle);
        _web.DefaultBackgroundColor = Color.Transparent;
        _web.RootVisualTarget = _dcomp.RootVisual;
        _dcomp.Commit();
        _web.Bounds = new Rectangle(Point.Empty, ClientSize);
        _web.IsVisible = true;
        _scale = _web.RasterizationScale;
        _web.RasterizationScaleChanged += (_, _) =>
        {
            var s = _web.RasterizationScale;
            if (s > 0.1 && double.IsFinite(s)) _scale = s;   // нулевой масштаб в переходных состояниях не берём
        };

        var core = _web.CoreWebView2;
        var s = core.Settings;
        s.AreDevToolsEnabled = _cfg.DevTools;
        s.AreDefaultContextMenusEnabled = false;
        s.IsZoomControlEnabled = false;
        s.AreBrowserAcceleratorKeysEnabled = _cfg.DevTools;
        s.IsStatusBarEnabled = false;
        s.AreHostObjectsAllowed = false;
        core.SetVirtualHostNameToFolderMapping("klava.local", _cfg.Abs(_cfg.WebRoot), CoreWebView2HostResourceAccessKind.Allow);
        // каверы и разобранные треки (data\music) — отдельный хост: личные данные не лежат в web
        Directory.CreateDirectory(MusicRoot);
        core.SetVirtualHostNameToFolderMapping("klava-music.local", MusicRoot, CoreWebView2HostResourceAccessKind.Allow);
        core.NavigationStarting += (_, a) =>
        {
            if (!a.Uri.StartsWith("https://klava.local/", StringComparison.OrdinalIgnoreCase)) a.Cancel = true;
        };
        core.NewWindowRequested += (_, a) => a.Handled = true;
        core.WebMessageReceived += OnWebMessage;
        core.Navigate(PageUrl());
    }

    /// <summary>Запуск идёт в фоне: без этой обёртки любое исключение молча убивало его на полпути.</summary>
    private async Task StartServicesGuardedAsync()
    {
        try { await StartServicesAsync(); }
        catch (Exception ex)
        {
            Log.Error("запуск сервисов", ex);
            Bubble($"Что-то сломалось при запуске: {ex.Message}");
            _ready = true;   // что поднялось — тем и пользуемся, глухой навсегда не остаётся
            UpdateDeaf();
        }
    }

    private async Task StartServicesAsync()
    {
        var log = new Progress<string>(s => { Log.Write(s); Bubble(s); });

        // 1. мозг первым: свой llama-server забирает видеокарту, и голос со слухом должны встать уже рядом с ним.
        // Облачному мозгу видеокарта не нужна — ему только проверить ключ и адрес.
        var brain = _cfg.ActiveBrain;
        Log.Write($"мозг: {brain.DisplayName}");
        var brainTask = EnsureBrainAsync(brain, log);
        // свой мозг: как только он забрал видеопамять (--fit-target оставил место голосу и слуху), они грузятся
        // параллельно с остатком модели — раньше ждали, пока все 28 ГБ скопируются в ОЗУ
        if (brain.Kind == "own") await Task.WhenAny(brainTask, _services.BrainVramTaken.Task);
        var voiceTask = _services.EnsureTtsAsync(TimeSpan.FromSeconds(240), log);
        var llmOk = await brainTask;
        if (!llmOk && brain.IsLocal)
            Bubble(brain.Kind == "own" ? "Мозг не поднялся — глянь data\\logs\\llama-server-.log." : "Мозг не поднялся — глянь окно Qwen3.8 Flash-Next в панели задач.");
        if (!llmOk) Bubble("Выбери другой мозг: правый клик по мне → «Мозг».");

        // 2. голос, 3. слух — по очереди. Голос важнее, он первым берёт видеопамять;
        // слух, если ему её не хватит, сам уйдёт на процессор (запасная маленькая модель).
        // сбой одного сервиса не обрывает остальные: 10.10 ошибка запуска голоса оборвала цепочку раньше голосовых
        // профилей — порог «свой» не подхватился (остался 0,75 из кода), и друзей перестало узнавать
        bool ttsOk, sttOk;
        try { ttsOk = await voiceTask; }
        catch (Exception ex) { Log.Error("голос: запуск", ex); ttsOk = false; }
        try { sttOk = await _services.EnsureSttAsync(TimeSpan.FromSeconds(90), log); }
        catch (Exception ex) { Log.Error("слух: запуск", ex); sttOk = false; }
        Log.Write($"мозг: {(llmOk ? "готов" : "НЕТ")}, слух: {(sttOk ? (_cfg.LiteSpeech ? "готов (лёгкий, GigaAM)" : _services.SttOnGpu ? "готов (видеокарта)" : "готов (процессор)") : "НЕТ")}, " +
                  $"голос: {(ttsOk ? (_cfg.LiteSpeech ? "готов (лёгкий, Supertonic)" : "готов") : "НЕТ")}");
        if (!sttOk) Bubble("Слух не поднялся — пиши мне текстом (Ctrl+Alt+M).");
        if (!ttsOk) Bubble("Голос не поднялся — буду отвечать текстом.");
        if (llmOk)
        {
            // характер и история — в кэш мозга до первой реплики, иначе первый ответ считается долго
            var warm = Stopwatch.StartNew();
            try { await _brain.WarmUpAsync(CancellationToken.None); Log.Write($"мозг прогрет за {warm.ElapsedMilliseconds} мс"); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Log.Error("прогрев мозга", ex); }
        }
        // Голосовые профили — поднимаем в фоне, если включено; UI можно открыть до готовности.
        if (_cfg.SpeakerIdEnabled || _cfg.RoomMembers.Count > 0)
            _ = _services.EnsureSpeakerAsync(TimeSpan.FromSeconds(120), new Progress<string>(s => Log.Write($"speaker: {s}")))
                .ContinueWith(t => { if (t.Result) BeginInvoke(() => _ = OnSpeakerReadyAsync()); }, TaskContinuationOptions.OnlyOnRanToCompletion);
        _lastTalk = _lastWatchComment = DateTime.UtcNow;
        _sessionStart = DateTime.UtcNow;
        ScheduleChatter();
        _ready = true;
        _watch.Start();
        UiWatchdog.Start(this);
        _watcher.Resume();
        _mcPoll.Start();
        if (llmOk)
            await ConverseAsync(WakeHint());
        Bubble(_cfg.HandsFree
            ? "Просто говори — я слушаю. Зажми правый Ctrl, чтобы перебить. Написать: Ctrl+Alt+M."
            : "Зажми правый Ctrl и говори. Написать: Ctrl+Alt+M.");
        UpdateDeaf();
    }

    // ---------------- мозги: запуск и переключение ----------------

    /// <summary>
    /// Поднять мозг профиля. Свои мозги (own и FlashNext) живут на одном порту и вместе на видеокарту не влезут:
    /// чужой (запущенный не Клавой, например для Studio) гасим только с согласия друга, свой — молча.
    /// </summary>
    private async Task<bool> EnsureBrainAsync(BrainProfile p, IProgress<string> log)
    {
        // чужой llama-server на другом порту (Studio, свои опыты) нам не мешает — смотрим только свой порт
        if (p.IsLocal && (_services.LlmPortOpen() || _services.OwnsLlm))
        {
            var path = await _services.LlmModelPathAsync();   // null — ещё грузится или не отвечает
            var ownFile = Path.GetFileName(_cfg.BrainModel);
            var runningOwn = path?.EndsWith(ownFile, StringComparison.OrdinalIgnoreCase);
            // не ответил, что за модель: свой, который ещё грузится, не трогаем; чужой под own — не наш
            var wrong = runningOwn is null
                ? p.Kind == "own" && !_services.OwnsLlm
                : p.Kind == "own" ? !runningOwn.Value : runningOwn.Value;
            if (wrong)
            {
                if (_services.OwnsLlm) _services.StopOwnLlm();
                else
                {
                    var what = runningOwn == true ? "свой мозг Клавы (Qwen3.6)" : "другой llama-server";
                    var yes = AskTopMost($"Сейчас запущен {what} — он занимает видеокарту и порт, «{p.DisplayName}» рядом с ним не влезет.\n\n" +
                        "Погасить его? Studio, если сидит на нём, потеряет соединение, пока ты его не запустишь снова.");
                    if (!yes) { log.Report("Ладно, остаюсь на прежнем мозге."); return false; }
                    Bubble("гашу старый мозг…");
                    await Services.StopLlmAsync();
                }
            }
            else if (p.Kind == "flashnext" && await _services.LlmContextAsync() is > 20000 and var ctx)
            {
                var yes = AskTopMost($"Мозг (FlashNext) сейчас запущен в рабочем режиме — контекст {ctx / 1024}К. Рядом с ним голосу Клавы " +
                    "не хватит видеопамяти.\n\nПерезапустить мозг в режиме Клавы (16К)? Studio, если сидит на нём же, " +
                    "потеряет соединение на минуту-другую.");
                if (yes) { Bubble("перезапускаю мозг…"); await Services.StopLlmAsync(); }
            }
        }
        // 28 ГБ модели с диска: на SSD ~1 мин, на HDD — до пяти
        return await _services.EnsureLlmAsync(p, TimeSpan.FromMinutes(p.Kind == "flashnext" ? 7 : 6), log);
    }

    /// <summary>
    /// Вопрос поверх всех окон. Раньше владельцем было окно Клавы (прозрачное, «сквозное» для мыши) — окошко вопроса
    /// пряталось за ним или за игрой, Клава ждала ответа вечно и «висла» на старте или смене мозга.
    /// </summary>
    private static bool AskTopMost(string text)
    {
        using var owner = new Form
        {
            TopMost = true, ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.CenterScreen,
            Size = new Size(1, 1), Opacity = 0,
        };
        owner.Show();
        owner.Activate();
        return MessageBox.Show(owner, text, "Клава", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    private void UpdateBrainMenu()
    {
        var active = _cfg.ActiveBrain;
        foreach (var item in _brainMenu.DropDownItems.OfType<ToolStripMenuItem>())
        {
            if (item.Tag is not BrainProfile b) continue;
            item.Checked = b.Id == active.Id;
            item.Text = b.DisplayName;
            // Claude Code без входа переключиться не даст (друг 18.11: «либо чтобы включалось, либо убирай») — пункта нет,
            // пока CLI не залогинен; вошёл («claude auth login») — пункт появится при следующем открытии меню
            if (b.Kind == "claude-code") item.Available = b.Id == active.Id || _claudeLoggedIn == true;
            foreach (var mi in item.DropDownItems.OfType<ToolStripMenuItem>())
                mi.Checked = b.Id == active.Id && mi.Tag is string model && model == b.Model;
        }
        _brainMenu.Text = $"Мозг: {active.DisplayName}";
    }

    /// <summary>Залогинен ли Claude Code (null — ещё не проверяли). Проверка — процесс claude auth status, ~1–2 с, в фоне.</summary>
    private bool? _claudeLoggedIn;
    private bool _claudeChecking;

    private async Task RefreshClaudeLoginAsync()
    {
        if (_claudeChecking || _cfg.Brains.All(b => b.Kind != "claude-code")) return;
        _claudeChecking = true;
        try { _claudeLoggedIn = await ClaudeCode.LoggedInAsync(); }
        finally { _claudeChecking = false; }
        if (!IsDisposed) UpdateBrainMenu();
    }

    /// <summary>
    /// Подменю моделей мозга — список прямо с сервера при каждом открытии (у AG Tools он зависит от аккаунтов).
    /// Если сервер молчит (прокси AG Tools выключен) — подсказка и пункт «подключиться», который его запустит.
    /// </summary>
    private async Task FillModelsAsync(ToolStripMenuItem item, BrainProfile b)
    {
        if (!_modelsLoading.Add(b.Id)) return;
        try
        {
            var models = await _services.ListModelsAsync(b);
            item.DropDownItems.Clear();
            if (models.Count == 0)
            {
                item.DropDownItems.Add(new ToolStripMenuItem(b.Launch is not null
                    ? "список моделей не пришёл — в AG Tools включи API Proxy"
                    : "сервер не отдал список моделей") { Enabled = false });
                var retry = new ToolStripMenuItem($"Подключиться ({b.Model})");
                retry.Click += (_, _) => _ = SwitchBrainAsync(b, b.Model, force: true);
                item.DropDownItems.Add(retry);
                return;
            }
            _models[b.Id] = models;
            // выбранная модель могла прийти из подмены AG Tools (custom_mapping) и не значиться в списке — показать всё равно
            if (b.Model.Length > 0 && !models.Contains(b.Model, StringComparer.OrdinalIgnoreCase)) models.Insert(0, b.Model);
            foreach (var m in models)
            {
                var mi = new ToolStripMenuItem(m) { Tag = m, Checked = b.Id == _cfg.ActiveBrain.Id && m == b.Model };
                mi.Click += (_, _) => _ = SwitchBrainAsync(b, m);
                item.DropDownItems.Add(mi);
            }
        }
        finally { _modelsLoading.Remove(b.Id); }
    }

    private readonly HashSet<string> _modelsLoading = new();
    private readonly Dictionary<string, List<string>> _models = new();

    // «на аргон», «на опус», «на сонет», «на гпт» — модель у мозгов со списком моделей (AG Tools): слово → кусок id модели
    private static readonly (string Spoken, string InId)[] ModelWords =
        [("аргон", "argon"), ("argon", "argon"), ("опус", "opus"), ("opus", "opus"), ("сонет", "sonnet"), ("соннет", "sonnet"), ("sonnet", "sonnet"), ("гпт", "gpt"), ("gpt", "gpt")];

    private static bool WordAt(string who, string word)
    {
        var i = who.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        return i >= 0 && (i == 0 || !char.IsLetter(who[i - 1]));   // с начала слова: «клод», но не «заклодить»
    }

    /// <summary>
    /// Мозг (и модель) по фразе: «переключись на жминю» → gemini, «на опус» → Antigravity с моделью *opus*.
    /// null — имени мозга там нет, фраза идёт в разговор.
    /// </summary>
    private async Task<(BrainProfile Brain, string? Model)?> BrainByVoiceAsync(string text)
    {
        var m = SwitchIntentRx().Match(text);
        if (!m.Success) return null;
        // «переключи агента на опус» — это модель агента (инструмент agent model), а не её собственный мозг
        if (AgentWordRx().IsMatch(text)) return null;
        var who = m.Groups["who"].Value;
        foreach (var (spoken, inId) in ModelWords)
        {
            if (!WordAt(who, spoken)) continue;
            foreach (var b in _cfg.Brains.Where(x => x.ModelsFromServer))
            {
                if (!_models.TryGetValue(b.Id, out var list) || list.Count == 0) _models[b.Id] = list = await _services.ListModelsAsync(b);
                var model = list.FirstOrDefault(x => x.Contains(inId, StringComparison.OrdinalIgnoreCase));
                if (model is not null) return (b, model);
            }
        }
        foreach (var b in _cfg.Brains)
            if (b.Aliases.Any(a => WordAt(who, a))) return (b, null);
        return null;
    }

    /// <summary>
    /// Сменить мозг (и модель у мозгов со списком моделей). Разговор общий — новый мозг продолжает ту же историю.
    /// Если новый не поднялся, остаёмся на старом. Свой llama-server при уходе в облако гасится: видеокарта нужнее
    /// голосу и слуху.
    /// </summary>
    /// <param name="force">Проверить и подключить заново, даже если это тот же мозг и та же модель.</param>
    private async Task SwitchBrainAsync(BrainProfile p, string? model = null, bool force = false)
    {
        var prev = _cfg.ActiveBrain;
        model ??= p.Model;
        if (p.Id == prev.Id && model == p.Model && !force) { Bubble($"Я и так на «{p.DisplayName}», мур."); return; }
        if (_switching) { Bubble($"Погоди, ещё переключаюсь на «{_switchTarget}» — как поднимется, скажу."); return; }
        _switching = true;
        _switchTarget = p.DisplayName;
        _switchBlocks = prev.IsLocal && p.IsLocal;
        if (_switchBlocks) StopTurn();
        var oldModel = p.Model;
        var oldChat = p.ChatModel;
        p.Model = model;
        // выбрал модель руками или голосом — значит, хочет именно её и в болтовне (быстрая ChatModel — только по умолчанию)
        if (model != oldModel) p.ChatModel = null;
        Bubble($"Переключаюсь на «{p.DisplayName}»…");
        Log.Write($"мозг: {prev.Id}/{(prev == p ? oldModel : prev.Model)} → {p.Id}/{p.Model}");
        var log = new Progress<string>(s => { Log.Write(s); Bubble(s); });
        // свой мозг (~26 ГБ) и другой свой сервер моделей (STRATA ~62 ГБ) вместе в 96 ГБ не влезают: свой гасим ДО подъёма
        var stoppedOwn = false;
        if (prev.IsLocal && p.IsLocalServer && _services.OwnsLlm) { _services.StopOwnLlm(); stoppedOwn = true; }
        bool ok;
        try { ok = await EnsureBrainAsync(p, log); }
        catch (Exception ex)
        {
            Log.Error("смена мозга", ex);
            Bubble($"Не вышло: {ex.Message}");
            ok = false;
        }
        if (!ok)
        {
            p.Model = oldModel;
            p.ChatModel = oldChat;
            Log.Write($"мозг: остаюсь на {prev.Id}/{prev.Model}");
            Bubble($"«{p.DisplayName}» не поднялся — остаюсь на «{prev.DisplayName}».");
            if (stoppedOwn)
            {
                // свой мозг уже погасили ради нового — поднять обратно, иначе «остаюсь» на пустом месте
                try { await EnsureBrainAsync(prev, log); }
                catch (Exception ex) { Log.Error("возврат своего мозга", ex); }
            }
            _switching = _switchBlocks = false;
            if (!IsBusy) Post(new { type = "state", value = "idle" });
            if (_pendingFlush && _ready && !IsBusy) _ = FlushBatchAsync();
            return;
        }
        _cfg.Brain = p.Id;
        _cfg.Save();
        UpdateBrainMenu();
        _brain.OnBrainSwitched();
        if (prev.IsLocal && !p.IsLocal) _services.StopOwnLlm();
        if (p.IsLocal)
        {
            try { await _brain.WarmUpAsync(CancellationToken.None); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { Log.Error("прогрев мозга", ex); }
        }
        _switching = _switchBlocks = false;
        await ConverseAsync($"(Друг только что переключил тебя на другой мозг — теперь за тебя думает {p.DisplayName}. Скажи про это одной короткой фразой.)");
    }

    /// <summary>Пока Клава думает или говорит, детектор голоса глухой — иначе она слышит себя из колонок.</summary>
    // Слушает всегда, как только готова. Пока она говорит, детектор поднимает порог (SpeakingBack),
    // чтобы её же «мур» из колонок не активировал микрофон, — но настоящую громкую речь друга ловит
    // и мгновенно перебивает её. Совсем глохнет только до готовности мозга/слуха/голоса.
    // EchoBlock=true — жёсткая глухота во время её речи (для громких колонок без наушников): встрять можно только PTT.
    /// <summary>В окне голосовых профилей идёт запись — Клава не слушает.</summary>
    private bool _profilesRecording;

    private void UpdateDeaf()
    {
        // «говорит» — пока страница играет её речь, между фразами ответа и ещё EchoTailMs после конца речи
        var speaking = _speaking || _voiceTurn || DateTime.UtcNow < _echoTailUntil;
        _ears.Deaf = !_ready || _profilesRecording;
        // «не слышать себя из колонок»: пока говорит — не глухая, а слышит только «кыс-кыс» (друг 09.10: «не перебить никак»)
        _ears.CommandOnly = speaking && _cfg.EchoBlock;
        // песня из колонок: порог перебивания выше, но не глухо — «стоп» должен доходить (глухо 4 минуты — не остановить)
        _ears.SpeakingBack = speaking || SongPlaying;
    }

    // ---------------- связь со страницей ----------------

    private void Post(object msg)
    {
        if (_web is null || !_pageReady) return;
        _web.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(msg));
    }

    private void Bubble(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Bubble(text)); return; }
        // суфлёр спрятан — облачка нет; без звука оно единственный голос, тогда показывается всё равно
        if (!_cfg.Subtitles && !_cfg.Muted) return;
        Post(new { type = "bubble", text });
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonNode? m;
        try { m = JsonNode.Parse(e.WebMessageAsJson); } catch (JsonException) { return; }
        switch (m?["type"]?.GetValue<string>())
        {
            case "ready":
                _pageReady = true;
                // страница перечитала сменённый конфиг и перезагрузилась раньше, чем дошла команда переодеться, — досылаем
                if (m["outfit"]?.GetValue<string>() is { } pageOutfit && pageOutfit != _cfg.Outfit) Post(new { type = "outfit", name = _cfg.Outfit });
                _songUntil = DateTime.MinValue;   // страница перезагрузилась — песня (если шла) оборвалась
                Log.Write("страница аватара готова");
                PostView();
                if (GameMode.On) Post(new { type = "gesture", name = "gaming" });   // страница перезагрузилась посреди игры
                if (_cfg.Muted) Post(new { type = "bell", volume = 0 });   // без звука — и колокольчик на чокере молчит
                Post(new { type = "subtitles", on = _cfg.Subtitles || _cfg.Muted });
                Post(new { type = "rug", on = _cfg.Rug });
                BeginInvoke(() =>
                {
                    Visible = true;
                    ResetFullWindowRegion();
                    Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
                    Native.SetWindowPos(Handle, Native.HWND_TOPMOST, Left, Top, Width, Height, Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);
                });
                break;
            case "selfie":
                OnSelfie(m["data"]?.GetValue<string>());
                break;
            case "craft_reply":
                if (m is JsonObject craftReply) OnCraftReply(craftReply);
                break;
            case "craft_error":
                Log.Write($"мастерская: «{m["name"]}» — {m["message"]}");
                break;
            case "craft_loaded":
                Log.Write($"мастерская: модель «{m["name"]}» загружена");
                break;
            case "idle":
                // страница доиграла всё — но колонки и комната ещё звучат долю секунды: хвост держим глухим
                _speaking = false;
                // договорила — теперь можно переодеться (перезагрузка страницы оборвала бы фразу на полуслове);
                // сначала её новые вещи в config.json, потом сам наряд
                ApplyPendingLook();
                if (_pendingOutfit is { } dress) { _pendingOutfit = null; ApplyOutfit(dress); }
                // договорила перед песней — поёт
                else if (_pendingSong is { } song) { _pendingSong = null; PlaySong(song); }
                _echoTailUntil = DateTime.UtcNow.AddMilliseconds(EchoTailMs);
                UpdateDeaf();
                break;
            case "mask":
                ApplyMask(m["w"]!.GetValue<int>(), m["h"]!.GetValue<int>(), m["cell"]!.GetValue<int>(), m["bits"]!.GetValue<string>());
                break;
            case "walk":   // танцует шагами — окно едет по рабочему столу
                OnWalk(m["px"]?.GetValue<double>() ?? 0);
                break;
            case "poked":
                OnPoked(m["part"]?.GetValue<string>() ?? "body");
                break;
            case "song_start":
                Log.Write($"страница: запела «{m["title"]}»");
                break;
            case "song_end":
                OnSongEnd(m["natural"]?.GetValue<bool>() ?? false);
                break;
            case "played":   // страница сыграла действие (еда, рыг, пук, наряд) — в журнал, чтобы было видно, что сработало
                Log.Write($"страница: сыграла «{m["name"]}»");
                break;
            case "petted":   // водили курсором по голове дольше секунды
                OnPoked("pet");
                break;
            case "error":
                Log.Write($"страница: {m["message"]}");
                break;
        }
    }

    /// <summary>Маска силуэта Клавы (+пузырь): окно никогда не режется Win32-регионом (чтобы рендер оставался
    /// идеально гладким с субпиксельным альфа-сглаживанием), а мышь пропускается насквозь через WS_EX_TRANSPARENT,
    /// когда курсор не над силуэтом.</summary>
    private void ApplyMask(int w, int h, int cell, string b64)
    {
        _lastMask = (w, h, cell, b64);
        _maskW = w;
        _maskH = h;
        _maskCell = cell;
        try { _maskBits = Convert.FromBase64String(b64); } catch { _maskBits = null; }
        ResetFullWindowRegion();
    }

    /// <summary>Откуда она ушла в танец шагами: туда окно вернётся, когда шаги кончатся.</summary>
    private Point? _walkHome;

    /// <summary>
    /// Шаги в танце (Карамельдансен: приставные шаги, ход вбок): страница шлёт, на сколько CSS-пикселей ушла бы она сама,
    /// окно едет следом по рабочему столу (не дальше края экрана). 0 — встала на место: окно возвращается домой.
    /// </summary>
    private void OnWalk(double px)
    {
        if (_dragging || _mouseDown) { _walkHome = null; return; }
        if (Math.Abs(px) < 0.5)
        {
            if (_walkHome is { } home) { _walkHome = null; Location = home; }
            return;
        }
        _walkHome ??= Location;
        var wa = Screen.FromPoint(_walkHome.Value).WorkingArea;
        var x = _walkHome.Value.X + (int)Math.Round(px * _scale);
        x = Math.Clamp(x, wa.Left - Width / 3, wa.Right - Width * 2 / 3);
        if (x != Left) Location = new Point(x, _walkHome.Value.Y);
    }

    private bool IsPointOverFigure(int clientX, int clientY)
    {
        if (_maskBits == null || _maskW <= 0 || _maskH <= 0 || _maskCell <= 0 || _scale <= 0) return true;
        int mx = (int)(clientX / (_scale * _maskCell));
        int my = (int)(clientY / (_scale * _maskCell));
        if (mx < 0 || mx >= _maskW || my < 0 || my >= _maskH) return false;
        return Bit(_maskBits, my * _maskW + mx);
    }

    private static bool Bit(byte[] bits, int i) => (bits[i >> 3] & (1 << (i & 7))) != 0;

    // ---------------- таймер: взгляд за курсором и клавиша «говорить» ----------------

    private void OnTick(object? sender, EventArgs e)
    {
        // Масштаб WebView2 на миг бывает 0 (смена DPI, перетаскивание между мониторами): тогда деление
        // давало бесконечность, (int) превращал её в int.MinValue и Math.Abs падал — вместе с рацией ниже.
        if (_scale > 0.1 && double.IsFinite(_scale) && Native.GetCursorPos(out var p))
        {
            var c = PointToClient(new Point(p.X, p.Y));
            double x = c.X / _scale, y = c.Y / _scale;
            if (Math.Abs(x - _lastCss.X) + Math.Abs(y - _lastCss.Y) > 2)
            {
                _lastCss = new Point((int)Math.Clamp(x, -1e6, 1e6), (int)Math.Clamp(y, -1e6, 1e6));
                Post(new { type = "cursor", x = _lastCss.X, y = _lastCss.Y });
            }

            if (!_isClickThroughActive && !_dragging && !_mouseDown && _clickThrough == 0 && !_menu.Visible)
            {
                bool inBounds = c.X >= 0 && c.Y >= 0 && c.X < Width && c.Y < Height;
                bool overFigure = inBounds && IsPointOverFigure(c.X, c.Y);
                if (overFigure != _cursorOverFigure)
                {
                    _cursorOverFigure = overFigure;
                    SetWindowTransparent(!overFigure);
                }
            }
        }

        if (++_ticks % 300 == 0)   // раз в ~10 с: слышит ли микрофон хоть что-то
            Log.Write($"микрофон: пик громкости {_ears.TakePeak():F4}, глухая={_ears.Deaf}, без кнопки={_ears.HandsFree}");
        var inGame = _cfg.GameStreamerMode;
        if (!inGame)
        {
            var fg = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(fg, out var curPid);
            inGame = RealGame(curPid);
        }
        if ((inGame && _ticks % 8 == 0) || (_ticks % 60 == 0)) KeepOnTop(); // в игре каждые ~250 мс, на рабочем столе ~2 с

        // хвост эха кончился — открыть уши
        if (_echoTailUntil != DateTime.MinValue && DateTime.UtcNow >= _echoTailUntil)
        {
            _echoTailUntil = DateTime.MinValue;
            UpdateDeaf();
        }
        // страница так и не сказала «idle» (перезагрузилась, звук упал) — не оставаться глухой навечно
        if (_speaking && _playUntil != DateTime.MinValue && DateTime.UtcNow > _playUntil.AddSeconds(4))
        {
            Log.Write("страница не доложила, что доиграла речь, — снимаю глухоту по времени");
            _speaking = false;
            _playUntil = DateTime.MinValue;
            UpdateDeaf();
        }

        var down = (Native.GetAsyncKeyState(_cfg.PushToTalkVk) & 0x8000) != 0;
        if (down && !_pttDown) OnTalkStart();
        if (!down && _pttDown) _ = OnTalkEndAsync();
        _pttDown = down;
    }

    private void OnTalkStart()
    {
        if (_hands.Active) _hands.Interrupt("друг взялся за рацию");   // зажал говорить — сразу отпускает мышь
        StopTurn();          // рация перебивает Клаву на полуслове
        _speaking = false;
        _ears.StartPtt();
        Log.Write("рация: запись");
        Post(new { type = "state", value = "listening" });
    }

    private async Task OnTalkEndAsync()
    {
        if (!_ears.PttRecording) return;
        var (wav, seconds) = _ears.StopPtt();
        await HandleUtteranceAsync(wav, seconds, "рация");
    }

    /// <summary>Она сейчас занята ответом: думает, говорит или ещё играет последняя фраза.</summary>
    private bool IsBusy => _busy || _voiceTurn || _speaking || (_turn is not null && !_turn.IsCancellationRequested);

    /// <summary>Кто сейчас в компании: имена из настроек, не больше выбранного числа людей.</summary>
    private List<string> ActiveMembers()
    {
        var n = Math.Clamp(_cfg.RoomPeopleCount, 1, KlavaConfig.MaxRoomPeople);
        var all = _cfg.RoomMembers.Where(m => !string.IsNullOrWhiteSpace(m) && m != SelfVoiceName).Distinct().Take(n).ToList();
        // ушедшие (Лис на работе) — не в компании, пока не вернутся; ушли все — значит отметки устарели, слушаем всех своих
        var here = all.Where(m => !_presence.IsAway(m)).ToList();
        return here.Count > 0 ? here : all;
    }

    private static string HumanName(string profile) => profile.Replace('_', ' ').Trim();

    /// <summary>
    /// Фраза готова (рация или детектор): распознать, понять кто говорит, отсеять эхо и чужих, положить в буфер.
    /// Ничего из того, что сказал свой человек, не выбрасывается: занята — реплика ждёт в буфере и уходит
    /// сразу после её хода; думает над прошлой фразой — прошлый запрос отменяется и уходит вместе с новой.
    /// </summary>
    private async Task HandleUtteranceAsync(byte[] wav, double seconds, string source)
    {
        var endedAt = DateTime.UtcNow;
        var ptt = source == "рация";
        Log.Write($"{source}: фраза {seconds:F1} с");
        if (seconds < 0.4) { if (!IsBusy) Post(new { type = "state", value = "idle" }); return; }
        if (!_ready) { if (!IsBusy) Post(new { type = "state", value = "idle" }); Bubble("Погоди, я ещё просыпаюсь…"); return; }
        if (!IsBusy) Post(new { type = "state", value = "thinking" });

        string text;
        List<SttSegment> segments;
        var sw = Stopwatch.StartNew();
        try { (text, segments) = await _ears.TranscribeAsync(wav, CancellationToken.None); }
        catch (Exception ex)
        {
            Log.Error("распознавание", ex);
            Bubble($"Не расслышала: {ex.Message}");
            if (!IsBusy) Post(new { type = "state", value = "idle" });
            return;
        }
        Log.Write($"распознано за {sw.ElapsedMilliseconds} мс: «{text}»");
        var hasWords = text.Length > 0 && text != "[неразборчиво]";
        // «кыс-кыс» — заткнуть сразу: раньше эха, узнавания голоса и песни (в шуме её речи голос друга узнаётся плохо)
        if (hasWords && TryKys(text, endedAt, seconds, loose: IsBusy || SongPlaying)) return;
        // звук из колонок (видео, игра, её же голос) — не люди: целиком — мимо, кусками — вырезать, остальное дальше
        if (!ptt && hasWords && FromSpeakers(wav, seconds, endedAt, segments) is { } heardPc)
        {
            if (heardPc.Kept.Count == 0)
            {
                if (!IsBusy) Post(new { type = "state", value = "idle" });
                return;
            }
            text = string.Join(" ", heardPc.Kept.Select(s => s.Text)).Trim();
            var from = Math.Max(0, heardPc.Kept.Min(s => s.Start) - 0.15);
            var to = Math.Min(seconds, heardPc.Kept.Max(s => s.End) + 0.25);
            wav = Ears.CutWav(wav, from, to);
            seconds = to - from;
            segments = heardPc.Kept.Select(s => new SttSegment(s.Start - from, s.End - from, s.Text)).ToList();
        }
        // играет песня: микрофон слышит её кавер — это не реплика друга (проходят только «стоп» и обращение к ней,
        // и то — от своего голоса: решается после узнавания говорящего)
        var song = hasWords ? ClassifyHeardDuringSong(text, ptt) : SongHeard.None;
        if (song == SongHeard.Drop)
        {
            if (!IsBusy) Post(new { type = "state", value = "idle" });
            return;
        }

        if (!hasWords && seconds < 0.6)
        {
            if (!IsBusy) Post(new { type = "state", value = "idle" });
            if (ptt) Bubble("Мяу? Не расслышала…");
            return;
        }

        // 0. Её собственный голос из колонок: только если её речь звучала одновременно с этой фразой.
        //    Бывает, что друг заговорил сразу после неё и детектор склеил эхо и его ответ в одну фразу —
        //    тогда выкидываем только куски-эхо, а его слова (и их кусок звука для узнавания голоса) идут дальше.
        if (!ptt && hasWords && EchoPhrases(endedAt, seconds) is { Count: > 0 } said && EchoMatch(said, text))
        {
            var kept = segments.Where(s => !EchoMatch(said, s.Text) && ContentWords(s.Text).Count > 0).ToList();
            if (kept.Count == 0 || segments.Count <= 1)
            {
                Log.Write($"эхо: это я сама из колонок — «{text}» — пропускаю");
                if (!IsBusy) Post(new { type = "state", value = "idle" });
                return;
            }
            var from = Math.Max(0, kept.Min(s => s.Start) - 0.15);
            var to = Math.Min(seconds, kept.Max(s => s.End) + 0.25);
            var restText = string.Join(" ", kept.Select(s => s.Text));
            Log.Write($"эхо: вырезала свою речь, осталось от друга ({from:F1}–{to:F1} с): «{restText}»");
            text = restText;
            wav = Ears.CutWav(wav, from, to);
            seconds = to - from;
        }

        // 1. Кто говорит — по голосу (даже если слова не разобраны).
        var members = ActiveMembers();
        var speakerOn = (_cfg.SpeakerIdEnabled || members.Count > 0) && await _services.SpeakerAliveAsync();
        // порог «свой» у голосовых профилей ещё не спрошен (запуск споткнулся раньше OnSpeakerReady) — спросить сейчас:
        // 10.10 весь вечер мерили заводскими 0,75 при настоящих 0,50, и Кота (0,57–0,68) не узнавало
        if (speakerOn && _speakerModel.Length == 0) await RefreshSpeakerInfoAsync();
        SpeakerMatch? match = null;
        string? speakerName = null;
        double speakerSim = 0;
        // её собственный голос в микрофоне возможен только пока (или сразу после того, как) она говорит; в тишине её
        // профиль — не соперник людям: иначе живую фразу Аксюты «узнавало» как Клаву и выкидывало фоном
        var selfPossible = _speaking || _voiceTurn || DateTime.UtcNow < _echoTailUntil || endedAt < _playUntil.AddSeconds(1.5) || SongPlaying;
        if (speakerOn && seconds >= 0.5)
        {
            match = await _speaker.IdentifyAsync(wav);
            if (match is not null)
            {
                speakerSim = match.Similarity;
                speakerName = match.Name;
                Log.Write($"speaker: {match.Name ?? "не узнан"} ({match.Similarity:F2}{(match.Candidate is { } c && match.Name is null ? $", ближе всех {c}" : "")})");
                if (match.Name == SelfVoiceName)
                {
                    if (selfPossible)
                    {
                        Log.Write($"эхо: по голосу это я сама — «{text}» — пропускаю");
                        if (!IsBusy) Post(new { type = "state", value = "idle" });
                        return;
                    }
                    speakerName = null;   // она молчала — значит модель ошиблась, это человек
                }
                // Ушедшие (сказали «Лис ушёл на работу», «Паши сегодня нет») — как солдат (друг 10.10: «Паши в комнате
                // нет — значит Клава должна игнорить его голос, если ей не сказали, что Паша пришёл»). Голос ближе всего
                // к ушедшему — фраза игнорируется ЦЕЛИКОМ, на тех, кто здесь, она не переписывается: так Куплинов из
                // колонок, похожий на Лиса, становился Димочкой. Вернуть в комнату можно только словами «Паша пришёл» от
                // того, кто здесь (Presence.Hear) — по голосу никогда (раньше: «три уверенных узнавания за 10 минут»).
                // Кто-то из комнаты по голосу ближе — фраза его, ушедший в узнавании не участвует.
                if (match.Scores is { Count: > 0 } raw && _presence.AwayList() is { Count: > 0 } awayNow)
                {
                    var awaySet = awayNow.Select(a => a.Profile).ToHashSet(StringComparer.Ordinal);
                    var thr0 = seconds < 1.2 ? _speakerThresholdShort : _speakerThreshold;
                    var kept = raw.Where(kv => !awaySet.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                    var ghost = raw.Where(kv => awaySet.Contains(kv.Key)).DefaultIfEmpty().MaxBy(kv => kv.Value);
                    var keptBest = kept.Where(kv => kv.Key != SelfVoiceName || selfPossible).Select(kv => kv.Value).DefaultIfEmpty(-1).Max();
                    if (!ptt && ghost.Key is not null && ghost.Value >= keptBest + 0.03 && ghost.Value >= thr0 - 0.08)
                    {
                        Log.Write($"присутствие: голос ближе всего к {HumanName(ghost.Key)} ({ghost.Value:F2}; из тех, кто здесь, лучший {keptBest:F2}), " +
                                  $"а его нет в комнате — фразу игнорирую: «{text}»");
                        _flightJournal.LogNoise($"не из комнаты (голос как у {HumanName(ghost.Key)})", hasWords ? text : "(неразборчивая речь)");
                        if (!IsBusy) Post(new { type = "state", value = "idle" });
                        return;
                    }
                    if (ghost.Key is not null && ghost.Value >= keptBest)
                        Log.Write($"фантом: голос похож на {ghost.Key} ({ghost.Value:F2}), но его нет в комнате — не он");
                    var best = kept.Where(kv => kv.Key != SelfVoiceName || selfPossible).DefaultIfEmpty().MaxBy(kv => kv.Value);
                    var named = best.Key is not null && best.Value >= thr0 ? best.Key : null;
                    if (speakerName is not null && awaySet.Contains(speakerName)) speakerName = named;
                    match = new SpeakerMatch(speakerName, best.Key is not null ? best.Value : 0, best.Key, kept);
                    speakerSim = match.Similarity;
                }
                // Фантом: имя из базы, которого в комнате нет. Приписывалось отсутствующему, Клава
                // обращалась к нему. Теперь без компании имя даётся только если профиль сильно оторван
                // от второго кандидата и уверенность заметно выше порога — иначе аноним.
                if (speakerName is not null && members.Count == 0 && match.Scores is { Count: >= 2 } sc)
                {
                    var top = sc.Where(kv => kv.Key != SelfVoiceName || selfPossible).OrderByDescending(kv => kv.Value).Take(2).ToList();
                    if (top.Count == 2)
                    {
                        var margin = top[0].Value - top[1].Value;
                        var floor = (seconds < 1.2 ? _speakerThresholdShort : _speakerThreshold) + _cfg.SpeakerFreeExtra;
                        if (margin < _cfg.SpeakerFreeMargin || top[0].Value < floor)
                        {
                            Log.Write($"фантом: {speakerName} ({top[0].Value:F2}) близко к {top[1].Key} ({top[1].Value:F2}), " +
                                      $"отрыв {margin:F2} < {_cfg.SpeakerFreeMargin:F2} — не называю имя");
                            speakerName = null;
                        }
                    }
                }
            }
        }

        // 2. Компания: слушаем только выбранных людей. Рация — всегда свой (кнопку держит человек за компом).
        var accepted = true;
        var who = speakerName;
        if (members.Count > 0)
        {
            if (ptt)
            {
                who = members.Contains(speakerName ?? "") ? speakerName : members[0];
                // рацию держит свой человек — его чистая запись: в соло сразу доучиваем профиль
                if (members.Count == 1 && speakerOn && seconds >= 1.5 && hasWords)
                    _ = Task.Run(() => _speaker.EnrollAsync(members[0], wav, append: true));
            }
            else if (!speakerOn)
            {
                if (DateTime.UtcNow - _warnedNoSpeaker > TimeSpan.FromMinutes(5))
                {
                    _warnedNoSpeaker = DateTime.UtcNow;
                    Log.Write("компания: сервер голосовых профилей не отвечает — слушаю всех, фильтр выключен");
                }
                who = members.Count == 1 ? members[0] : speakerName;
            }
            else if (match?.Scores is not { Count: > 0 } scores)
            {
                // слишком коротко для голосового отпечатка — пропускаем только обращение к ней
                accepted = hasWords && KlavaNameRx().IsMatch(text);
                who = members.Count == 1 ? members[0] : null;
            }
            else
            {
                // у коротких фраз отпечаток шумнее — свой, мягче порог (но «не хуже чужих» остаётся)
                var thr = _cfg.SpeakerAcceptSim is { } fixedThr ? fixedThr - (seconds < 1.2 ? 0.06 : 0)
                    : seconds < 1.2 ? _speakerThresholdShort : _speakerThreshold;
                var bestMember = members.Select(m => (Name: m, Sim: scores.TryGetValue(m, out var s) ? s : -1)).MaxBy(x => x.Sim);
                (string Name, double Sim) bestOther = ("", -1);
                foreach (var (k, v) in scores)
                    if (!members.Contains(k) && (k != SelfVoiceName || selfPossible) && v > bestOther.Sim) bestOther = (k, v);
                accepted = bestMember.Sim >= thr && bestMember.Sim >= bestOther.Sim - 0.02;
                who = accepted ? bestMember.Name : speakerName ?? bestOther.Name;
                if (!accepted && hasWords && KlavaNameRx().IsMatch(text))
                {
                    // Аварийный выход: к ней обратились по имени — отвечаем, даже если голос не узнан как свой.
                    // Телевизор «Клава» не говорит, а свой человек с испорченным эталоном иначе остался бы без ответа.
                    // Имя — только похожему: свой почти дотянул до порога и не хуже чужих — свой; чужой уверенно выше
                    // порога — он; иначе никто («кто-то из вас»). Раньше имя бралось у лучшего ЧУЖОГО при любом балле:
                    // 02.10 «Паша (−0,08)» подписывал Настины и Котовы фразы, хотя свой Лис был выше (0,15).
                    accepted = true;
                    who = bestMember.Sim >= thr - 0.06 && bestMember.Sim >= bestOther.Sim - 0.02 ? bestMember.Name
                        : bestOther.Sim >= thr ? bestOther.Name : null;
                    Log.Write($"компания: обратились по имени — отвечаю; голос {(who is null ? "не узнан" : HumanName(who))} " +
                              $"(свой {bestMember.Name} {bestMember.Sim:F2}, чужой {bestOther.Name} {bestOther.Sim:F2}, порог {thr:F2})");
                    if (who is not null && !members.Contains(who) && DateTime.UtcNow - _warnedProfile > TimeSpan.FromMinutes(20))
                    {
                        _warnedProfile = DateTime.UtcNow;
                        Bubble($"Слышу, но голос похож на «{HumanName(who ?? "?")}», а не на «{string.Join(", ", members.Select(HumanName))}». " +
                               "Если это ты — перезапиши голос: меню → «Голосовые профили» → «Новый профиль».");
                    }
                }
                else if (!accepted)
                {
                    Log.Write($"компания: не свой голос ({bestMember.Name} {bestMember.Sim:F2} < {thr:F2} или ближе {bestOther.Name} {bestOther.Sim:F2}) — «{text}»");
                    if (hasWords) Post(new { type = "heard", text = $"🚫 {HumanName(who ?? "не свой")}: {text}" });
                }
            }
        }

        // поёт: «стоп» и «Клава» — только от уверенно узнанного своего из тех, кто здесь
        if (song is SongHeard.Stop or SongHeard.Called)
        {
            var sure = accepted && who is not null && who != SelfVoiceName && !_presence.IsAway(who) && (members.Count == 0 || members.Contains(who));
            if (SongInterrupt(song, text, sure, who))
            {
                if (!IsBusy) Post(new { type = "state", value = "idle" });
                return;
            }
        }

        _lastUtteranceWav = wav;
        _lastSpeakerName = speakerName;
        _lastUtteranceText = hasWords ? text : "";

        var logText = hasWords ? text : "⚠️ [речь не разобрана]";
        _audioSettingsForm?.AppendHeardLog(accepted ? who : $"фон: {who ?? "?"}", speakerSim, logText, seconds, hasWords && accepted);
        // Журнал — слова своих (с временем, как услышано); чужие голоса и неразборчивое — отдельно, в journal\noise
        if (accepted && hasWords)
        {
            if (_flightJournal.LogHeard(who ?? "Друг", text) is { } heardEntry) _lightMemory.IndexTurn(heardEntry);
            if (who is { Length: > 0 } && who != SelfVoiceName) _lastHeard[who] = DateTime.UtcNow;
            HearPresence(text, who);   // «Лис ушёл на работу», «Паши нет», «Лис вернулся», перекличка
        }
        else _flightJournal.LogNoise(accepted ? who ?? "Друг" : $"фон ({who ?? "кто-то"})", hasWords ? text : "(неразборчивая речь)");

        if (!accepted)
        {
            if (!IsBusy) Post(new { type = "state", value = "idle" });
            return;
        }
        if (hasWords) _whisperLearn.Feed(text);   // подсказка whisper'у — только речь своих

        // Уверенное совпадение — доучиваем профиль на лету (без блокировки).
        if (who is not null && who == speakerName && speakerSim >= _speakerThreshold + 0.05 && wav.Length > 16000 && hasWords)
            _ = Task.Run(() => _speaker.AdaptAsync(who, wav));

        // 3. Слова не разобрались, а человек реально говорил — переспросить, если она свободна; иначе молча.
        if (!hasWords)
        {
            if (!IsBusy && seconds >= 1.2 && (members.Count > 0 || ptt))
            {
                Log.Write($"речь не разобрана ({seconds:F1} с) от {who ?? "друга"} — переспрашиваю");
                _ = ConverseAsync($"(Собеседник {HumanName(who ?? "друг")} что-то сказал ({seconds:F1} с), но слова не разобрались. Переспроси его коротко, в своём стиле.)", look: false, speakerName: who);
            }
            else
            {
                Log.Write($"речь не разобрана ({seconds:F1} с) — пропускаю молча");
                if (!IsBusy) Post(new { type = "state", value = "idle" });
            }
            return;
        }
        Post(new { type = "heard", text });

        // 4. Стоп — только короткой командой («стоп», «тихо, Клава») или с обращением к ней.
        if (IsStopCommand(text))
        {
            Log.Write($"стоп-команда: «{text}»");
            lock (_batchGate) _tempBatch.Clear();
            if (_hands.Active) _hands.Interrupt("друг сказал стоп");
            StopTurn();
            // «стоп» снимает и долгую задачу — иначе через секунду она бы сама к ней вернулась
            var hadTask = _tasks.Active;
            if (hadTask) _tasks.Finish("друг сказал стоп", cancelled: true);
            // и игру-автопилот — только если «стоп» ей или про игру («Клава, стоп», «хватит играть», «отдай мышь»):
            // в живой компании короткие «хватит», «тихо», «стой» говорят друг другу, и от них Зума бросалась посреди
            // уровня (друг 30.09: «бот срывается в Зуме»). Речь её «стоп» гасит по-прежнему от любого
            var anyGame = ZumaBot.Playing || TetrisBot.Playing || GamePlayer.Playing || ArenaPlayer.Playing || BotSchool.Running;
            var hadGame = anyGame && IsGameStop(text);
            if (anyGame && !hadGame) Log.Write("стоп-команда: игру не трогаю — не ей и не про игру");
            if (hadGame && ZumaBot.Playing) ZumaBot.Stop();
            if (hadGame && TetrisBot.Playing) TetrisBot.Stop();
            if (hadGame && GamePlayer.Playing) GamePlayer.Stop();
            if (hadGame && ArenaPlayer.Playing) ArenaPlayer.Stop();
            if (hadGame && BotSchool.Running) BotSchool.Stop();
            // агентов — только словами про них («стоп агент», «останови агентов»): их работа долгая, а короткое «стоп»
            // в компании говорят друг другу
            var hadAgent = (AgentRunner.Busy || AgentHub.Busy) && AgentWordRx().IsMatch(text);
            if (hadAgent && AgentRunner.Busy) AgentRunner.Stop();
            if (hadAgent && AgentHub.Busy) AgentHub.Stop("all");
            Bubble(hadTask ? "Молчу, задачу бросила." : hadAgent ? "Всё, агента остановила." : hadGame ? "Всё, не играю." : "Молчу-молчу.");
            Post(new { type = "state", value = "idle" });
            return;
        }

        // 5. Голосовая коррекция профиля («я не Аксюта, я Кот»)
        var corr = SpeakerCorrectionRx().Match(text);
        if (corr.Success && await ProfileByWordAsync(corr.Groups["correct"].Value) is { } matchedProfile)
        {
            var (ok, _) = await CorrectLastSpeakerAsync(matchedProfile);
            Bubble(ok ? $"Поняла, исправила! Ты — {HumanName(matchedProfile)}!" : "Не смогла доучить голос.");
            if (!IsBusy) Post(new { type = "state", value = "idle" });
            return;
        }

        // 6. Смена мозга голосом
        if (await BrainByVoiceAsync(text) is { } target)
        {
            Log.Write($"друг: «{text}» — смена мозга");
            await SwitchBrainAsync(target.Brain, target.Model);
            return;
        }

        // 7. Во временный буфер. Дозаказ: она ещё думает над прошлым (не заговорила, инструменты не трогала) —
        //    отменяем тот запрос и отправляем всё вместе, чтобы не отвечать на половину мысли.
        var speaker = who ?? "Друг";
        var mergeable = _busy && !_voiceTurn && !_speaking && _turnIsUser && _turnBatch is { Count: > 0 } && !_brain.TurnUsedTools && _merges < 3;
        int batchCount;
        lock (_batchGate)
        {
            if (mergeable)
            {
                _tempBatch.InsertRange(0, _turnBatch!);
                _merges++;
            }
            _tempBatch.Add((speaker, text, DateTime.UtcNow));
            _lastBatchTime = DateTime.UtcNow;
            batchCount = _tempBatch.Count;
        }
        _batchWaitSec = BatchWait(speaker, text);
        if (mergeable)
        {
            Log.Write($"дозаказ: «{text}» пришло, пока думала — переспрашиваю мозг с полной репликой ({batchCount} фраз)");
            _turn?.Cancel();
            _turn = null;
            _busy = false;
            _turnBatch = null;
            _ = FlushBatchAsync();
            return;
        }

        var maxNeeded = Math.Max(1, members.Count > 0 ? members.Count : _cfg.DialogBatchMaxPhrases);
        var toKlava = KlavaNameRx().IsMatch(text);
        if (batchCount >= maxNeeded || toKlava || ptt)
        {
            if (IsBusy)
            {
                // Она сейчас говорит — не перебиваем: ответит сразу после своего хода.
                _pendingFlush = true;
                Log.Write($"очередь: «{text}» ждёт конца её реплики ({batchCount} в буфере)");
            }
            else
            {
                Log.Write(toKlava ? $"обратились по имени — отвечаю ({batchCount} в буфере)" : $"буфер {batchCount}/{maxNeeded} — отвечаю");
                _ = FlushBatchAsync();
            }
        }
        else
            Log.Write($"буфер [{speaker}]: «{text}» [{batchCount}/{maxNeeded}], жду затишья {_batchWaitSec:F1} с");
    }

    /// <summary>
    /// Одна фраза голосом потоком: say_start на первом куске (эмоция, пузырь, жест — вместе со звуком), say_chunk на
    /// каждый кусок, say_end в конце. Когда доиграет — считаем по кускам (24 кГц, 16 бит, моно — 48000 байт в секунду).
    /// Звука не вышло совсем — как раньше: пузырём и эмоцией, и счётчик сбоев голоса.
    /// </summary>
    /// <summary>Темп потокового синтеза (секунд работы на секунду звука, сглаженный) и секунд звука на знак текста.</summary>
    private double _ttsRate = 0.85, _ttsSecPerChar = 0.075;

    private async Task SpeakStreamAsync(Phrase p, bool isFirst, string userText, CancellationTokenSource cts)
    {
        var sid = Interlocked.Increment(ref _sayId);
        var begun = false;
        var playIdx = -1;
        var firstSoundAt = 0.0;
        var watch = Stopwatch.StartNew();
        string? fx = p.Fx;
        void Begin()
        {
            begun = true;
            // ход уже закончился (звук пришёл позже конца хода) — флаг «её ход» не ставить, иначе залипнет «занята»
            if (isFirst && _turn == cts) { Post(new { type = "state", value = "idle" }); _voiceTurn = true; }
            _lastSpoken = p.Text;
            if (fx is not null && fx.StartsWith("outfit:", StringComparison.Ordinal)) { _pendingOutfit = fx[7..]; fx = null; }
            _speaking = true;
            var now = DateTime.UtcNow;
            if (_playUntil < now) _playUntil = now;
            lock (_played) { _played.Add((_playUntil, _playUntil, p.Text)); playIdx = _played.Count - 1; }
            UpdateDeaf();
            // синтез идёт медленнее звука (мозг и голос делят видеокарту) — фраза стартует с запасом, чтобы не рвалась
            firstSoundAt = watch.Elapsed.TotalSeconds;
            var lead = _ttsRate > 0.9 ? Math.Clamp((_ttsRate - 0.88) * p.Text.Length * _ttsSecPerChar, 0.12, 3.0) : 0.12;
            Post(new { type = "say_start", id = sid, text = p.Text, emotion = p.Emotion, fx, fxAt = p.FxAfter ? "after" : "before", lead });
            // мозг сам поставил еду или газ — жесты по словам на эту реплику друга уже не нужны
            if (p.Fx is "chips" or "fish" or "garlic" or "squid") _eatFor = userText;
            if (p.Fx is "burp" or "fart") _gasFor = userText;
            if (p.Fx == "caramell") { _placeFor = userText; BeginInvoke(Caramell); }   // метка [карамельдансен] — с песней, если разобрана
            if (p.Fx is null) GestureFor(p.Text, userText);
            Log.Write($"говорит [{p.Emotion}/{p.Voice}] «{p.Text}»{(p.Fx is null ? "" : $" +{p.Fx}{(p.FxAfter ? " после" : "")}")} — первый звук через {watch.ElapsedMilliseconds} мс");
        }
        var bytes = 0;
        try
        {
            bytes = await _voice.SynthStreamAsync(p.Text, p.Voice, chunk => BeginInvoke(() =>
            {
                if (cts.IsCancellationRequested) return;
                if (!begun) Begin();
                Post(new { type = "say_chunk", id = sid, pcm = Convert.ToBase64String(chunk) });
                _playUntil = _playUntil.AddSeconds(chunk.Length / 48000.0);
                lock (_played)
                    if (playIdx >= 0 && playIdx < _played.Count) _played[playIdx] = (_played[playIdx].Start, _playUntil, p.Text);
            }), cts.Token);
            if (bytes > 0) _ttsFails = 0;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { Log.Error("голос (поток)", ex); }
        var total = bytes;
        BeginInvoke(() =>
        {
            if (cts.IsCancellationRequested) return;
            if (begun)
            {
                Post(new { type = "say_end", id = sid });
                // темп синтеза после первого звука: больше 1 — звук кончается раньше, чем досинтезируется хвост
                var seconds = total / 48000.0;
                if (seconds > 1.0)
                {
                    var rate = (watch.Elapsed.TotalSeconds - firstSoundAt) / seconds;
                    _ttsRate = _ttsRate * 0.6 + Math.Clamp(rate, 0.3, 3.0) * 0.4;
                    if (p.Text.Length > 8) _ttsSecPerChar = _ttsSecPerChar * 0.8 + seconds / p.Text.Length * 0.2;
                }
                Log.Write($"  фраза {seconds:F1} с, синтез потоком за {watch.ElapsedMilliseconds} мс (темп {_ttsRate:F2})");
                return;
            }
            // звука нет совсем — хотя бы пузырём, и голос, может, пора поднимать
            if (isFirst && _turn == cts) { Post(new { type = "state", value = "idle" }); _voiceTurn = true; UpdateDeaf(); }
            Log.Write($"говорит [{p.Emotion}/{p.Voice}] «{p.Text}» (без звука)");
            if (fx is not null && fx.StartsWith("outfit:", StringComparison.Ordinal)) { ApplyOutfit(fx[7..]); fx = null; }
            Post(new { type = "emotion", name = p.Emotion });
            Post(new { type = "bubble", text = p.Text });
            if (KlavaVR.IsVrcRunning()) KlavaVR.SendChatbox(p.Text);
            if (fx is not null) ApplyFx(fx);
            // без звука (режим «Без звука» или голос лёг) — жесты по словам те же: ест, садится, танцует по просьбе
            else GestureFor(p.Text, userText);
            if (Interlocked.Increment(ref _ttsFails) >= 2 && !_ttsRestarting && DateTime.UtcNow - _lastTtsRestart > TimeSpan.FromMinutes(1))
                _ = RestartTtsInBackgroundAsync();
        });
    }

    // «стоп», «замолчи», «молчи», «тихо», «хватит» — мгновенная остановка речи Клавы
    [System.Text.RegularExpressions.GeneratedRegex(@"\b(стоп|стой|хватит|хорош|молчи|замолчи|замолкни|тихо|заткнись|ша|тишина|помолчи|отставить)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex StopTalkRx();

    /// <summary>
    /// Стоп-команда, а не просто слово в разговоре: короткая фраза (до трёх слов: «стоп», «тихо, тихо») или
    /// обращение к ней («Клава, хватит»). «Хватит уже есть» между друзьями её не затыкает.
    /// </summary>
    /// <summary>Стоп, которым бросают игру-автопилот: с обращением к Клаве или со словами про игру.</summary>
    internal static bool IsGameStop(string text) => KlavaNameRx().IsMatch(text) || GameWordRx().IsMatch(text);

    [System.Text.RegularExpressions.GeneratedRegex(@"агент|субагент", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    internal static partial System.Text.RegularExpressions.Regex AgentWordRx();

    [System.Text.RegularExpressions.GeneratedRegex(@"(игр|игра|зум|мыш|мышк|лягуш|шарик|парти|автопилот|бот[аеуы]?\b|брось|отдай)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex GameWordRx();

    internal static bool IsStopCommand(string text)
    {
        if (!StopTalkRx().IsMatch(text)) return false;
        var words = NonWordRx().Split(text.Trim()).Count(w => w.Length > 0);
        return words <= 3 || KlavaNameRx().IsMatch(text);
    }

    /// <summary>
    /// Проверка фразы перед голосом: обращение к отмеченному ушедшим («Лис, привет!», «Привет, Паш!», «Ну что, Лис,
    /// как дела?») вырезается вместе с запятой — друг 10.10: «Паша и Лис на работе, а эта шиза хвостатая говорит „Лис,
    /// привет, как дела“». Мозгу об этом сказано и в RoomContext; это — страховка на случай, если он не послушал.
    /// null — от фразы ничего не осталось.
    /// </summary>
    internal Phrase? GuardAddress(Phrase p)
    {
        if (p.Text.Length == 0 || _presence.AwayList().Count == 0) return p;
        var text = p.Text;
        var known = KnownPeople();
        var cuts = new List<(int From, int To, string Who)>();
        foreach (System.Text.RegularExpressions.Match m in CapWordRx().Matches(text))
        {
            var before = text[..m.Index].TrimEnd();
            var after = text[(m.Index + m.Length)..].TrimStart();
            var opens = before.Length == 0 || before[^1] is ',' or '!' or '?' or '.' or '…' or '—' or '-';
            var closes = after.Length == 0 || after[0] is ',' or '!' or '?' or '…' or '.';
            if (!opens || !closes) continue;
            if (Presence.Resolve(m.Value, known) is not { } who || !_presence.IsAway(who)) continue;
            // «, Лис,» / «, Лис!» / «Лис, …» — вместе с одной запятой
            var from = m.Index;
            var to = m.Index + m.Length;
            if (before.EndsWith(',')) from = text.LastIndexOf(',', Math.Max(0, m.Index - 1));
            else if (after.StartsWith(',')) to = text.IndexOf(',', to) + 1;
            cuts.Add((from, to, who));
        }
        if (cuts.Count == 0) return p;
        var sb = new System.Text.StringBuilder(text);
        foreach (var c in cuts.OrderByDescending(c => c.From)) sb.Remove(c.From, c.To - c.From);
        var rest = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim(' ', ',', '—', '-');
        rest = System.Text.RegularExpressions.Regex.Replace(rest, @"\s+([,!?.…])", "$1");
        Log.Write($"проверка: обратилась к {string.Join(", ", cuts.Select(c => HumanName(c.Who)).Distinct())}, а их нет в комнате — " +
                  $"обращение убрала: «{text}» → «{rest}»");
        if (!System.Text.RegularExpressions.Regex.IsMatch(rest, @"[а-яёa-z]{2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return null;
        return p with { Text = char.ToUpper(rest[0]) + rest[1..] };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<![А-Яа-яЁё])[А-ЯЁ][а-яё]{1,15}(?![А-Яа-яЁё])")]
    private static partial System.Text.RegularExpressions.Regex CapWordRx();

    /// <summary>Слух на колонки (SpeakerTap): огибающая звука компьютера за 90 с — сверка с микрофоном.</summary>
    private SpeakerTap? _speakerTap;

    /// <summary>Корреляция огибающих «микрофон — колонки», с которой фраза считается звуком компьютера.</summary>
    /// (--tap-test: видео из колонок 0,87–0,93; живой голос 0,35; человек поверх видео 0,63 — его не выкидывать.)
    internal const double SpeakerEchoR = 0.75;

    /// <summary>
    /// Микрофон услышал компьютер (видео, игру, её саму), а не человека? Сверка огибающих (SpeakerTap): вся фраза
    /// повторяет колонки — null-кусков нет, Kept пуст; часть кусков whisper повторяет колонки — они вырезаны, Kept — слова
    /// людей. null — колонки молчали или сверка ничего не нашла: фраза идёт как есть. Друг 10.10: «Куплинов ко мне в
    /// гости придёт, только если рак на горе свиснет» — его голос из колонок шёл в журнал словами Димочки.
    /// </summary>
    private (List<SttSegment> Kept, double R)? FromSpeakers(byte[] wav, double seconds, DateTime endedAt, List<SttSegment> segments)
    {
        if (_speakerTap is not { Running: true } tap) return null;
        var start = endedAt - TimeSpan.FromSeconds(seconds);
        if (tap.LastSound < start - TimeSpan.FromSeconds(1)) return null;   // колонки молчали всю фразу
        const int lag = 60;   // ±0,6 с на задержки записи и воспроизведения
        var mic = SpeakerTap.Envelope(wav, Ears.Rate);
        var spk = tap.Slice(start - TimeSpan.FromMilliseconds(lag * SpeakerTap.FrameMs),
                            start + TimeSpan.FromMilliseconds((mic.Length + lag) * SpeakerTap.FrameMs));
        if (spk is null || spk.Length < mic.Length + 2 * lag) return null;
        var whole = SpeakerTap.Match(mic, 0, mic.Length, spk, lag);
        var shortText = SegmentsText(segments);
        if (whole.R >= SpeakerEchoR)
        {
            Log.Write($"колонки: фраза повторяет звук компьютера (r {whole.R:F2}, сдвиг {whole.Lag * SpeakerTap.FrameMs} мс) — " +
                      $"это видео/игра/я сама, не люди: «{shortText}» — пропускаю");
            return ([], whole.R);
        }
        if (segments.Count <= 1)
        {
            if (whole.R > 0.3) Log.Write($"колонки: сверка r {whole.R:F2} — живой голос «{shortText}»");
            return null;
        }
        var kept = new List<SttSegment>();
        var dropped = new List<string>();
        foreach (var s in segments)
        {
            var from = (int)(s.Start * 1000 / SpeakerTap.FrameMs);
            var len = Math.Min(mic.Length - from, (int)((s.End - s.Start) * 1000 / SpeakerTap.FrameMs));
            var r = len >= 15 && from >= 0 ? SpeakerTap.Match(mic, from, len, spk, lag).R : 0;
            if (r >= SpeakerEchoR) dropped.Add($"«{s.Text.Trim()}» (r {r:F2})");
            else kept.Add(s);
        }
        if (dropped.Count == 0)
        {
            if (whole.R > 0.3) Log.Write($"колонки: сверка r {whole.R:F2}, по кускам — живые голоса «{shortText}»");
            return null;
        }
        Log.Write($"колонки: вырезала звук компьютера — {string.Join(", ", dropped)}; " +
                  (kept.Count > 0 ? $"осталось от людей: «{string.Join(" ", kept.Select(k => k.Text.Trim()))}»" : "людей в фразе нет"));
        return (kept, whole.R);
    }

    /// <summary>
    /// Окно микрофона, пока она говорит, — только её эхо из колонок (SpeakerTap.OnlyEcho: всем окном и каждыми 0,8 с,
    /// чтобы «кыс-кыс» на секунду поверх её голоса не потерялся). Слуха на колонки нет или сомнение — false: whisper проверит.
    /// </summary>
    private bool OnlySpeakers(byte[] wav, double seconds, DateTime endedAt)
    {
        if (_speakerTap is not { Running: true } tap) return false;
        var start = endedAt - TimeSpan.FromSeconds(seconds);
        const int lag = 60;
        var mic = SpeakerTap.Envelope(wav, Ears.Rate);
        var spk = tap.Slice(start - TimeSpan.FromMilliseconds(lag * SpeakerTap.FrameMs),
                            start + TimeSpan.FromMilliseconds((mic.Length + lag) * SpeakerTap.FrameMs));
        if (spk is null || spk.Length < mic.Length + 2 * lag) return false;
        return SpeakerTap.OnlyEcho(mic, spk, lag, SpeakerEchoR);
    }

    private static string SegmentsText(List<SttSegment> segments)
    {
        var t = string.Join(" ", segments.Select(s => s.Text.Trim()));
        return t.Length > 90 ? t[..90] + "…" : t;
    }

    // «Кыс-кыс», «кис-кис», «кыс кыс, Клава», «кискис», «кыш-кыш»: два и больше «кыс» подряд отдельными словами
    // (whisper пишет то «кыс», то «кис», то слитно); «киска», «кискин» — не команда
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\p{L})(?:к[иы]с+|кыш|k[iy]s+)(?:[\s,.!?…\-–—]*(?:к[иы]с+|кыш|k[iy]s+))+(?!\p{L})", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex KysRx();

    internal static bool IsKysCommand(string text) => KysRx().IsMatch(text);

    // Пока она говорит, whisper слышит «кис-кис» сквозь её голос криво: 09.10 «кис-кис, Клава» → «Кошко и Клава».
    // Мягко: одно к-слово с шипящей («кис», «кыш», «кошко», «киса») прямо перед «Клава». «Как/кот/Ксюша, Клава» — нет
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!\p{L})к[иыоуеэ]?[сшщзц][ькоиыа]{0,3}[\s,.!?…\-–—]*(?:и\s+)?клав", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex KysLooseRx();

    /// <summary>«Кыс-кыс» или его кривое распознавание с именем — только пока она говорит (там её голос глушит слова).</summary>
    internal static bool IsKysLoose(string text) => KysRx().IsMatch(text) || KysLooseRx().IsMatch(text);

    /// <summary>После «кыс-кыс» сама не заговаривает (игра, события, задача, инициатива); отвечать людям — сразу.</summary>
    private static readonly TimeSpan KysHush = TimeSpan.FromSeconds(30);
    private DateTime _hushUntil = DateTime.MinValue;
    private bool Hushed => DateTime.UtcNow < _hushUntil;
    private bool _kysBusy;

    /// <summary>
    /// «Кыс-кыс, Клава» — заткнуть её сразу, на полуслове, вместе с очередью (друг 09.10: «бывает пиздит без остановок
    /// и не перебить никак»). От любого голоса и в любом месте фразы: в шуме её же речи узнавание голоса ненадёжно, а
    /// «кыс-кыс» ей никто, кроме своих, не скажет. Не срабатывает, если «кыс» был в её собственной звучавшей фразе (эхо).
    /// </summary>
    private bool TryKys(string text, DateTime endedAt, double seconds, bool loose = false)
    {
        if (!(loose ? IsKysLoose(text) : IsKysCommand(text))) return false;
        if (EchoPhrases(endedAt, seconds).Any(IsKysLoose))
        {
            Log.Write($"кыс-кыс: «{text}» — это я сама сказала, эхо");
            return false;
        }
        var wasTalking = IsBusy || SongPlaying;
        Log.Write($"кыс-кыс: «{text}» — замолкаю{(wasTalking ? "" : " (и так молчала)")}, сама не заговариваю {KysHush.TotalSeconds:F0} с");
        _hushUntil = DateTime.UtcNow + KysHush;
        lock (_batchGate) _tempBatch.Clear();
        if (SongPlaying) StopSong();
        StopTurn();
        Post(new { type = "heard", text });
        Bubble("Мур… молчу.");
        Post(new { type = "state", value = "idle" });
        return true;
    }

    /// <summary>
    /// Окно звука (последние 2,5 с), пойманное, пока она говорила при «не слышать себя из колонок»: в разговор не идёт,
    /// только на проверку «кыс-кыс». Whisper занят прошлым окном — свежее ждёт следующим (старые не копятся), чтобы
    /// последнее окно перед концом её речи не потерялось.
    /// </summary>
    private async Task HandleCommandWhileSpeakingAsync(byte[] wav, double seconds)
    {
        if (!_ready) return;
        if (_kysBusy) { _kysNext = (wav, seconds); return; }
        _kysBusy = true;
        try
        {
            while (true)
            {
                var endedAt = DateTime.UtcNow;
                // в окне только её эхо из колонок — whisper не гоняем: он на той же видеокарте, что и её голос, и раз в
                // секунду отнимал её у синтеза (10.10: первый звук 1,1 с, синтез медленнее реального времени)
                if (OnlySpeakers(wav, seconds, endedAt))
                {
                    if (_kysNext is not { } skipNext) return;
                    _kysNext = null;
                    (wav, seconds) = skipNext;
                    continue;
                }
                var (text, _) = await _ears.TranscribeAsync(wav, CancellationToken.None);
                if (text.Length > 0 && text != "[неразборчиво]")
                {
                    if (TryKys(text, endedAt, seconds, loose: true)) { _kysNext = null; return; }
                    Log.Write($"пока говорила ({seconds:F1} с): «{text}» — не кыс-кыс, мимо");
                }
                if (_kysNext is not { } next) return;
                _kysNext = null;
                (wav, seconds) = next;
            }
        }
        catch (Exception ex) { Log.Error("кыс-кыс: распознавание", ex); }
        finally { _kysBusy = false; }
    }

    private (byte[] Wav, double Seconds)? _kysNext;

    /// <summary>Профиль по слову из фразы «я не Аксюта, я Кот»: ищем среди записанных голосов по началу имени.</summary>
    private async Task<string?> ProfileByWordAsync(string word)
    {
        var w = word.Trim().ToLowerInvariant();
        if (w.Length < 3) return null;
        var profiles = await _speaker.ListAsync();
        var stem = w.Length > 4 ? w[..4] : w;
        return profiles.Select(p => p.Name).Where(n => n != SelfVoiceName)
            .FirstOrDefault(n => n.ToLowerInvariant().Replace('ё', 'е').Split('_', ' ').Any(part => part.StartsWith(stem, StringComparison.Ordinal)));
    }

    /// <summary>Сколько ждать тишины перед ответом на накопленное (см. BatchWait).</summary>
    private double _batchWaitSec = 1.0;
    private readonly Dictionary<string, DateTime> _heardAt = [];

    /// <summary>
    /// Пауза перед ответом (друг 29.09: «убрать задержки перед ответом»). Раньше всегда 2 с тишины сверх паузы детектора
    /// голоса — итого больше 3 с. Теперь: вопрос («?», «как…», «что…», «сколько…») — 0,35 с; говорит один человек
    /// (за последние полторы минуты других не было) — 0,6 с; живая компания — dialogSilenceTimeoutSec: там ждём, не
    /// скажет ли кто ещё. Если кто-то всё же заговорит, пока она думает, — «дозаказ» переспросит мозг с полной репликой.
    /// </summary>
    private double BatchWait(string speaker, string text)
    {
        var now = DateTime.UtcNow;
        _heardAt[speaker] = now;
        var others = _heardAt.Count(kv => kv.Key != speaker && now - kv.Value < TimeSpan.FromSeconds(90));
        var t = text.Trim();
        var question = t.EndsWith('?') || QuestionRx().IsMatch(t);
        var wait = _cfg.DialogSilenceTimeoutSec;
        if (others == 0) wait = Math.Min(wait, 0.6);
        if (question) wait = Math.Min(wait, 0.35);
        return wait;
    }

    // вопрос по первому слову: «как дела», «что делаешь», «сколько времени», «почему…», «ты…?»
    [System.Text.RegularExpressions.GeneratedRegex(@"^(как|что|чё|че|сколько|почему|зачем|где|когда|куда|кто|какой|какая|какие|каким|ну\s+и\s+как|а\s+ты|ты\s+(как|где|что|чё))\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex QuestionRx();

    private void OnBatchTimerTick(object? sender, EventArgs e)
    {
        lock (_batchGate)
        {
            if (_tempBatch.Count == 0) return;
        }
        // Занята речью/мозгом или меняет мозг — не сбрасываем по таймеру, она сама подхватит в конце хода
        if (IsBusy || _switchBlocks || !_ready) return;

        if ((DateTime.UtcNow - _lastBatchTime).TotalSeconds >= _batchWaitSec || _pendingFlush)
        {
            Log.Write($"затишье {_batchWaitSec:F1} с — отвечаю на накопленное");
            _pendingFlush = false;
            _ = FlushBatchAsync();
        }
    }

    /// <summary>
    /// Мозг между кругами инструментов забирает всё, что друзья сказали, пока она работала: поправка доходит сразу,
    /// а не после хода. Забранное из очереди уходит — отдельного ответа на него после хода не будет.
    /// </summary>
    private List<string> TakeInterjections()
    {
        List<(string Speaker, string Text, DateTime Time)> got;
        lock (_batchGate)
        {
            if (_tempBatch.Count == 0) return [];
            got = new List<(string Speaker, string Text, DateTime Time)>(_tempBatch);
            _tempBatch.Clear();
            _pendingFlush = false;
        }
        return got.Select(b => b.Speaker == "Друг" ? b.Text : $"{HumanName(b.Speaker)}: {b.Text}").ToList();
    }

    private async Task FlushBatchAsync()
    {
        List<(string Speaker, string Text, DateTime Time)> batch;
        lock (_batchGate)
        {
            if (_tempBatch.Count == 0) return;
            batch = new List<(string Speaker, string Text, DateTime Time)>(_tempBatch);
            _tempBatch.Clear();
        }

        string combinedText;
        string mainSpeaker;
        var voices = batch.Select(b => b.Speaker).Distinct().ToList();
        if (voices.Count == 1)
        {
            // один человек (может, в несколько приёмов) — одна реплика; узнанный по голосу — «Имя: слова»
            mainSpeaker = voices[0];
            var joined = string.Join(" ", batch.Select(b => b.Text));
            combinedText = mainSpeaker == "Друг" ? joined : $"{HumanName(mainSpeaker)}: {joined}";
        }
        else
        {
            mainSpeaker = string.Join(", ", voices);
            // слова людей — первыми, пояснение — в конце: реплика, начинавшаяся со скобки «(Говорят несколько человек…»,
            // везде считалась событием — ей не давали инструментов (01.10: вдвоём попросили запустить Зуму — «запускаю»,
            // и ничего), не подмешивали память и не писали в журнал
            var sb = new System.Text.StringBuilder();
            foreach (var (spk, txt, _) in batch) sb.AppendLine($"{HumanName(spk)}: «{txt}»");
            sb.Append($"(говорили {voices.Count} человека подряд — ответь всем разом, по именам)");
            combinedText = sb.ToString();
        }

        await ConverseAsync(combinedText, look: false, speakerName: mainSpeaker, batch: batch);
    }

    // ---------------- разговор ----------------

    private void StopTurn()
    {
        _turn?.Cancel();
        _turn = null;
        _voiceTurn = false;
        _speaking = false;
        _busy = false;
        _pendingFlush = false;
        UpdateDeaf();
        Post(new { type = "stop" });
    }

    /// <summary>
    /// Мягкая остановка: ход отменён, дальше не говорит, но текущую фразу ДОГОВАРИВАЕТ — не на полуслове. Для случаев,
    /// когда друг просто забрал мышь, а не просил замолчать. Уши остаются глухими до «idle» от страницы (фраза ещё звучит).
    /// </summary>
    private void FinishCurrentPhrase()
    {
        _turn?.Cancel();
        _turn = null;
        _voiceTurn = false;
        _busy = false;
        _pendingFlush = false;
        UpdateDeaf();
        Post(new { type = "trim" });
    }

    /// <summary>Что Клава видит на экране — приклеивается к реплике для мозга (и снимок, если мозг с глазами).</summary>
    /// <param name="look">Посмотреть на экран по-настоящему (по просьбе «глянь»), а не только на заголовок окна.</param>
    private async Task<(string? Context, byte[]? Image)> ScreenContextAsync(bool look)
    {
        if (_eyes is null) return (null, null);
        var g = _eyes.Foreground();
        if (!look)
            return (_cfg.WatchScreen && g is not null
                ? $"(Краем глаза: у друга на экране {g.Describe()}. Говори про это, только если к слову.)"
                : null, null);
        var sw = Stopwatch.StartNew();
        var p = _cfg.ActiveBrain;
        var sees = p.IsLocal ? _services.BrainSees : p.Vision;
        var wantImage = _cfg.SendScreenImage && sees;
        // Текст с экрана — всем: даже облачный мозг с глазами на ужатом снимке путает мелкие буквы, цифры и
        // названия («туго видит»), а распознанный с полного разрешения текст стоит 0,7 с и снимает это.
        var wantText = true;
        string text;
        byte[]? jpeg;
        try { (text, jpeg) = await _eyes.LookAsync(_cfg.ScreenTextMaxChars, p.IsLocal ? 1280 : 1920, wantImage, wantText); }
        catch (Exception ex) { Log.Error("глаза", ex); (text, jpeg) = ("", null); }
        Log.Write($"глаза: {(jpeg is null ? "без картинки" : $"снимок {jpeg.Length / 1024} КБ")}, текст {text.Length} символов, " +
                  $"{sw.ElapsedMilliseconds} мс; {g?.Describe() ?? "окно не определить"}");
        var what = g is not null ? $"Сейчас у него на экране {g.Describe()}." : "";
        var seen = jpeg is not null ? " Снимок экрана приложен — смотри на него." : "";
        var ocr = text.Length > 0
            ? $" Текст с экрана, распознанный автоматически (с ошибками и обрывками интерфейса):\n{text}\n"
            : wantText ? " Текста там почти не видно." : "";
        return ($"(Ты посмотрела на экран друга. {what}{seen}{ocr})\n" +
                "Скажи коротко и по-свойски, что там происходит, своими словами — не зачитывай текст подряд.", jpeg);
    }
    /// <summary>Кто в комнате — строка контекста для мозга (в историю не идёт): с кем говорить и к кому обращаться.</summary>
    /// <remarks>
    /// Раньше сюда шёл весь список компании из настроек — пятеро «в комнате», даже когда говорит один (01.10: «вспоминает
    /// людей, которых в комнате нет», «Лиса Лис, а чё ты молчишь?»). Теперь рядом — те, чей голос слышен за последние
    /// 25 минут (и кто говорит сейчас); остальные названы отсутствующими.
    /// </remarks>
    private string? RoomContext(IReadOnlyCollection<string> speaking)
    {
        var members = ActiveMembers();
        var gone = AwayText();
        var goneNote = gone is null ? "" :
            $" Ушли (тебе сказали): {gone} — их в комнате нет, не обращайся к ним. Если фразу подписало именем ушедшего — " +
            "это ошибка распознавалки: отвечай тем, кто здесь, и не спорь о ней. Скажут, что кто-то ушёл или вернулся, — отметь инструментом room.";
        if (members.Count == 0) return gone is null ? null : $"({goneNote.Trim()})";
        var now = DateTime.UtcNow;
        var here = members.Where(m => speaking.Contains(m) || _lastHeard.TryGetValue(m, out var t) && now - t < TimeSpan.FromMinutes(25)).ToList();
        var anon = speaking.Count == 0 || speaking.All(s => s is "Друг" or "");
        if (here.Count == 0)
            return "(Кто сейчас рядом — неизвестно: по голосу ты пока никого не узнала. Никого не называй по имени, пока не услышишь." + goneNote + MediaRule + ")";
        var away = members.Except(here).Where(m => !_presence.IsAway(m)).Select(HumanName).ToList();
        var names = string.Join(", ", here.Select(HumanName));
        return (anon ? $"(Сейчас говорил кто-то из вас — голос не разобрала, по имени его не называй; рядом, судя по голосам: {names}."
                     : here.Count == 1 ? $"(Сейчас с тобой говорит только {names}." : $"(Сейчас рядом, судя по голосам: {names}.") +
               (away.Count > 0 ? $" {string.Join(", ", away)} — {(away.Count == 1 ? "его (её)" : "их")} сейчас не слышно, значит в комнате нет: не обращайся к {(away.Count == 1 ? "нему" : "ним")} и не говори так, будто {(away.Count == 1 ? "он" : "они")} рядом." : "") +
               goneNote +
               MediaRule + (anon ? "" : " Отвечай тому, кто говорил, по имени.") + ")";
    }

    /// <summary>
    /// Граница «видео — люди» (друг 10.10: «Куплинов ко мне в гости придёт, только если рак на горе свиснет»): в комнате
    /// только люди из компании, всё из колонок — видео, стримы, игры — персонажи и блогеры, которых тут нет.
    /// </summary>
    private const string MediaRule =
        " В комнате — только люди из компании выше. Блогеры, стримеры и персонажи из видео и игр (например, Куплинов) — " +
        "это звук и картинка с экрана, их здесь нет и не будет: их можно обсуждать, но не говори с ними, не здоровайся " +
        "с ними и не приписывай их слова нам. Реплики людей тебе подписаны именами — только это и сказали люди.";

    /// <summary>Чей голос когда слышали в последний раз (свои, за этот запуск).</summary>
    private readonly Dictionary<string, DateTime> _lastHeard = new(StringComparer.Ordinal);

    /// <summary>
    /// Событие без голоса (погладили, друг вернулся к компьютеру, своя затея): кто рядом, она знает только по недавним
    /// голосам. 01.10 вернувшегося Кота она встретила «Аксюта, где пропадал?» — имя взяла из последней реплики истории.
    /// </summary>
    private string PresenceForEvent()
    {
        var now = DateTime.UtcNow;
        var recent = _lastHeard.Where(kv => now - kv.Value < TimeSpan.FromMinutes(25)).OrderByDescending(kv => kv.Value).ToList();
        if (recent.Count == 0)
            return "(Кто сейчас у компьютера — неизвестно: голосов давно не было. Никого не называй по имени — скажи без обращения или спроси, кто тут.)";
        var last = recent[0];
        var others = recent.Skip(1).Select(kv => HumanName(kv.Key)).ToList();
        var fresh = now - last.Value < TimeSpan.FromMinutes(3);
        return $"(Кто рядом: последним говорил {HumanName(last.Key)} — {Chronicle.Span(now - last.Value)} назад" +
               (others.Count > 0 ? $"; за последние полчаса ещё слышно: {string.Join(", ", others)}" : "") +
               ". Это событие без голоса: кто именно сейчас у компьютера, ты не видишь. " +
               (fresh ? $"Скорее всего это {HumanName(last.Key)} — можешь обратиться к нему." : "Обращайся без имени или спроси, кто тут.") + ")";
    }

    /// <summary>
    /// Мотор долгой задачи: раз в секунду — если задача открыта, Клава свободна, людям отвечать нечего, прошло 3 с после
    /// её хода и будильник task_wait прозвенел, — новый подход к задаче с планом и заметками в контексте. Предохранитель
    /// (40 подходов / 45 минут) снимает задачу и просит честно отчитаться.
    /// </summary>
    /// <summary>
    /// Событие, которое нельзя потерять (итог игры): сразу, если она свободна, иначе — в очередь; очередь разбирает
    /// TaskTick, как только она договорила и людям отвечать нечего. Обычные события (заметила окно) по-прежнему
    /// пропускаются, если занята, — они к моменту освобождения уже неактуальны.
    /// </summary>
    // ---------- CS 1.6: события её бота → короткие реплики ----------
    private DateTime _csLastSay = DateTime.MinValue;
    private DateTime _csStreakAt = DateTime.MinValue;
    private int _csStreak;
    private string _csTeam = "";
    private bool _csLowHpSaid;

    /// <summary>
    /// Строка от плагина CS. Комментирует дозированно: не чаще раза в 6 с, но смерть, конец раунда и серия фрагов —
    /// всегда (иначе очередь событий захлебнётся посреди перестрелки).
    /// </summary>
    private void OnCsLine(string line)
    {
        void Say(string note, bool always = false)
        {
            if (!always && DateTime.UtcNow - _csLastSay < TimeSpan.FromSeconds(6)) return;
            _csLastSay = DateTime.UtcNow;
            QueueEvent($"(CS 1.6: {note} Одна короткая живая реплика, как в голосовом чате, не больше 10 слов.)");
        }
        string Side(string t) => t == "T" ? "террористов" : t == "CT" ? "спецназ" : t;
        var sp = line.IndexOf(' ');
        var head = sp < 0 ? line : line[..sp];
        var rest = sp < 0 ? "" : line[(sp + 1)..];
        switch (head)
        {
            case "hello":
                Bubble($"CS на связи: {rest}");
                break;
            case "joined":
                _csTeam = rest;
                _csLowHpSaid = false;
                Say($"ты зашла в игру за {Side(rest)}.", true);
                break;
            case "left":
                _csTeam = "";
                Bubble("Вышла из CS.");
                break;
            case "disconnected":
                if (_csTeam.Length > 0) Bubble("CS закрылась.");
                _csTeam = "";
                break;
            case "kill":
            {
                // kill <роль> <hs> <оружие> <убийца-человек> <жертва-человек>|<убийца>|<жертва>
                var parts = rest.Split('|');
                var f = parts[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 5 || parts.Length < 3) break;
                var (role, hs, weapon, kh, vh, killer, victim) = (f[0], f[1] == "1", f[2], f[3] == "1", f[4] == "1", parts[1], parts[2]);
                var head2 = hs ? " в голову" : "";
                if (role == "killer")
                {
                    _csStreak = DateTime.UtcNow - _csStreakAt < TimeSpan.FromSeconds(8) ? _csStreak + 1 : 1;
                    _csStreakAt = DateTime.UtcNow;
                    Say(_csStreak >= 2 ? $"у тебя уже {_csStreak} фрага подряд, последний — {victim} из {weapon}{head2}."
                                       : $"ты убила {(vh ? "друга (!) " : "")}{victim} из {weapon}{head2}.", _csStreak >= 3 || vh);
                }
                else if (role == "victim")
                {
                    _csLowHpSaid = false;
                    Say($"тебя убил {(kh ? "друг (!) " : "")}{killer} из {weapon}{head2}.", true);
                }
                else if (kh && hs) Say($"друг снёс {victim} в голову из {weapon}.");
                break;
            }
            case "score":
            {
                var s = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (s.Length >= 2 && _csTeam.Length > 0) Say($"раунд закончился, счёт террористы {s[0]} : спецназ {s[1]}, ты за {Side(_csTeam)}.", true);
                break;
            }
            case "bomb":
                if (_csTeam.Length > 0) Say(rest switch { "planted" => "бомба заложена.", "defused" => "бомбу разминировали.", _ => "бомба взорвалась." });
                break;
            case "host_dead" when DateTime.UtcNow - Tools.LastPawsInCs < TimeSpan.FromMinutes(3):
            {
                // она играла лапками за игрока этого компа — его и убили: до нового раунда она мертва
                var p = rest.Split('|');
                QueueEvent($"(CS: тебя убил {(p.Length > 1 ? p[1] : "кто-то")} из {p[0]}. Ты мертва до начала следующего раунда — " +
                           "лапками не бегай и не стреляй, на экране камера смерти. Одной фразой отреагируй и жди нового раунда.)");
                break;
            }
            case "round_start" when DateTime.UtcNow - Tools.LastPawsInCs < TimeSpan.FromMinutes(3):
                QueueEvent("(CS: новый раунд — ты снова жива на респе. Сначала закуп лапками (B и цифры меню по снимку), потом в бой длинными цепочками.)");
                break;
            case "hurt":
                if (int.TryParse(rest, out var hp) && hp < 30 && !_csLowHpSaid)
                {
                    _csLowHpSaid = true;
                    Say($"тебя сильно ранили, осталось {hp} HP.");
                }
                break;
        }
    }

    /// <summary>
    /// Сменить режим общения: сохранить, отметить в меню и сказать ей самой, что это значит, — пусть подтвердит уже
    /// в новой манере. Раньше меню только показывало пузырь, мозг о смене не знал и переспрашивал «что за режим».
    /// </summary>
    private void SetPersonaMode(PersonaModes.Mode mode)
    {
        _cfg.PersonaMode = mode.Id;
        _cfg.Save();
        foreach (var it in _modeMenu.DropDownItems.OfType<ToolStripMenuItem>()) it.Checked = (string?)it.Tag == mode.Id;
        Log.Write($"режим общения: {mode.Id}");
        QueueEvent($"(Друг переключил тебе режим общения: «{mode.Title}». Это значит: {mode.Meaning}. " +
                   "С этой реплики говоришь только так, что бы ни было раньше в разговоре. Одной короткой фразой подтверди — уже в новой манере.)");
    }

    private void QueueEvent(string text)
    {
        if (_ready && !_switchBlocks && !IsBusy && !SongPlaying && !Hushed) { _ = ConverseAsync(text); return; }
        lock (_pendingEvents) _pendingEvents.Enqueue(text);
        Log.Write($"событие ждёт очереди (занята): {text}");
    }

    private readonly Queue<string> _pendingEvents = new();

    private void TaskTick()
    {
        AgentHub.Tick(_cfg);
        if (!_ready || _switchBlocks || IsBusy || _hands.Active || _profilesRecording || SongPlaying || Hushed) return;
        if (DateTime.UtcNow - _lastTurnEnded >= TimeSpan.FromSeconds(1.5))
        {
            string? ev = null;
            lock (_batchGate) if (_tempBatch.Count > 0 || _pendingFlush) return;
            lock (_pendingEvents) if (_pendingEvents.Count > 0) ev = _pendingEvents.Dequeue();
            if (ev is not null) { _ = ConverseAsync(ev); return; }
        }
        if (!_tasks.Active) return;
        if (DateTime.UtcNow - _lastTurnEnded < TimeSpan.FromSeconds(3)) return;
        lock (_batchGate) if (_tempBatch.Count > 0 || _pendingFlush) return;   // сначала ответить людям
        if (!_tasks.Due(DateTime.Now)) return;
        if (_tasks.Exhausted() is { } why)
        {
            var goal = _tasks.Goal;
            var plan = _tasks.Describe();
            _tasks.Finish($"остановлена предохранителем ({why})", cancelled: true);
            _ = ConverseAsync($"(Твоя долгая задача «{goal}» остановлена: {why}. Вот где ты остановилась:\n{plan}\n" +
                              "Одной-двумя фразами честно скажи другу, что успела, что нет и как можно продолжить.)");
            return;
        }
        _tasks.MarkContinued();
        Log.Write($"задача: новый подход — «{_tasks.Goal}»");
        _ = ConverseAsync($"{Brain.TaskPrefix} продолжаю задачу «{_tasks.Goal}»", extraContext:
            "(Система: у тебя незаконченная долгая задача — друг ждёт результат, не бросай её.\n" + _tasks.Describe() + "\n" +
            "Сделай СЛЕДУЮЩИЙ шаг инструментами (shell, look_screen, send_chat, read_file…), затем отметь его task_step с заметкой, " +
            "что узнала. Ошибка — прочитай её и попробуй иначе, не сдавайся после первой. Нужно подождать (ответ нейронки, сборка) — " +
            "task_wait. Цель достигнута и ПРОВЕРЕНА — task_done с итогом. Сделать нельзя — task_cancel с причиной. " +
            "Вслух — одна короткая фраза, что сейчас делаешь.)");
    }

    /// <param name="batch">Реплики, из которых собран этот ход, — чтобы при дозаказе вернуть их в буфер.</param>
    /// <param name="extraContext">Добавка к контексту хода (в историю не идёт): план долгой задачи.</param>
    private async Task ConverseAsync(string userText, bool look = false, string? speakerName = null,
        List<(string Speaker, string Text, DateTime Time)>? batch = null, string? extraContext = null)
    {
        // общая реплика нескольких человек начинается со скобки «(Говорят несколько человек…)», но это люди, а не
        // событие: раньше такие ходы шли без памяти и в журнал не попадали вовсе
        var isUser = batch is not null || !userText.StartsWith('(');
        // после «кыс-кыс» сама не заговаривает (события, инициатива); людям и «глянь на экран» из меню отвечает как всегда
        if (!isUser && !look && Hushed)
        {
            Log.Write($"кыс-кыс: молчу, своё не начинаю — {(userText.Length > 90 ? userText[..90] + "…" : userText)}");
            return;
        }
        // пока Клава водит мышью, «стоп/хватит» — это команда отобрать лапки, а не новая реплика
        if (_hands.Active && isUser && StopHandsRx().IsMatch(userText) && userText.Length < 40) { _hands.Interrupt("друг сказал стоп"); return; }
        // «режим без мата», «включи рассудительный режим» — переключение режима общения, голосом или текстом
        if (isUser && userText.Length < 80 && PersonaModes.FromSpeech(userText) is { } mode) { SetPersonaMode(mode); return; }
        // «игровой режим», «режим стримера», «поверх игры», «проклик» — переключение режима игрового оверлея
        if (isUser && userText.Length < 60 && GameOverlayRx().IsMatch(userText)) { ToggleGameOverlayMode(); return; }
        if (isUser && userText.Length < 60 && WorkModeRx().Match(userText) is { Success: true } wm)
        {
            SetWorkMode(wm.Groups["work"].Success ? "work" : wm.Groups["chat"].Success ? "chat" : "auto");
            return;
        }
        if (isUser && batch is null && await BrainByVoiceAsync(userText) is { } target)
        {
            Log.Write($"друг: «{userText}» — смена мозга");
            await SwitchBrainAsync(target.Brain, target.Model);
            return;
        }
        if (_switchBlocks)
        {
            // мозги меняются — реплика не пропадает, а ждёт в буфере и уйдёт новому мозгу
            if (isUser)
            {
                lock (_batchGate)
                {
                    if (batch is not null) _tempBatch.InsertRange(0, batch);
                    else
                    {
                        _tempBatch.Add((speakerName ?? "Друг", userText, DateTime.UtcNow));
                        _flightJournal.LogHeard(speakerName ?? "Друг", userText);   // всё, что в буфере, в журнале уже лежит
                    }
                    _lastBatchTime = DateTime.UtcNow;
                }
                _pendingFlush = true;
                Bubble("Секунду, мозги меняю — отвечу сразу после.");
            }
            return;
        }
        // события (погладили, заметила окно) никогда не перебивают живой разговор и не будят Клаву фоном
        if (!isUser && (IsBusy || IsSleeping)) { Log.Write($"событие пропущено ({(IsSleeping ? "спит" : "занята")}): {userText}"); return; }
        if (isUser && IsSleeping)
        {
            if (!IsWakeUpCall(userText))
            {
                Log.Write($"сон: разговор в комнате не будит спящую Клаву (нет обращения): «{userText}»");
                return;
            }
            var span = DateTime.UtcNow - _sleptAt;
            WakeUp("обращение по имени");
            var mins = Math.Max(1, (int)span.TotalMinutes);
            var sleepNote = $"(Ты только что проснулась: ты крепко и сладко спала {mins} мин, пока тебя не разбудил друг. " +
                            "Ты сладко зеваешь, потягиваешься лапками, трёшь сонные глазки. Ты чётко помнишь, что спала и только что проснулась. " +
                            "Отвечай сонно, уютно и тепло, с лёгкой полусонной хрипотцой, помня, что только продрала глаза.)";
            extraContext = string.IsNullOrEmpty(extraContext) ? sleepNote : $"{extraContext}\n{sleepNote}";
        }
        StopTurn();
        var cts = new CancellationTokenSource();
        _turn = cts;
        _busy = true;
        _turnInput = userText;
        _turnIsUser = isUser;
        _turnBatch = batch;
        UpdateDeaf();
        Log.Write($"друг: «{userText}»");
        if (isUser) _lastTalk = DateTime.UtcNow;
        var turnWatch = Stopwatch.StartNew();
        Post(new { type = "state", value = "thinking" });
        var selfAsk = isUser && SelfLookRx().IsMatch(userText);
        var (context, image) = await ScreenContextAsync(!selfAsk && (look || (isUser && LookIntentRx().IsMatch(userText))));
        if (cts.IsCancellationRequested) return;
        // спрашивают про неё саму — зеркало: снимок её аватара и словами, что на ней надето
        if (selfAsk)
        {
            var (selfNote, selfImage) = await SelfViewAsync();
            if (selfImage is not null) image = selfImage;
            context = string.IsNullOrEmpty(context) ? selfNote : $"{selfNote}\n{context}";
        }
        var screenNote = context;   // что было на экране — единственное из контекста, что идёт в журнал
        var speaking = batch is { Count: > 0 } ? batch.Select(b => b.Speaker).Distinct().ToList() : speakerName is { Length: > 0 } ? [speakerName] : new List<string>();
        var presence = isUser ? RoomContext(speaking) : PresenceForEvent();
        if (presence is not null) context = string.IsNullOrEmpty(context) ? presence : $"{presence}\n{context}";
        if (_presence.TakeNote() is { } back) context = string.IsNullOrEmpty(context) ? back : $"{back}\n{context}";
        // игра, которую она смотрит вместе с другом: счёт патронов, события, коды — её собственные глаза, а не догадки;
        // слова друга («шмальнул в Дилера холостым», «5 боевых 3 холостых») сначала вписываются в счёт
        if (isUser) _gameWatch.HearFriend(userText);
        if (_gameWatch.ContextNote(isUser ? userText : "") is { } game) context = string.IsNullOrEmpty(context) ? game : $"{context}\n{game}";
        // Что она сейчас делает (курит, пьёт, кодит) — иначе мозг отвечает по персоне и говорит «не курю»,
        // пока сигарета зажата в клыках. Напоминание о собственном теле.
        if (ActivityNote() is { } act) context = string.IsNullOrEmpty(context) ? act : $"{act}\n{context}";
        if (!string.IsNullOrEmpty(extraContext)) context = string.IsNullOrEmpty(context) ? extraContext : $"{context}\n{extraContext}";
        // живая просьба посреди открытой задачи: пусть знает о ней (может, это поправка к ней, а может — отдельное дело)
        else if (isUser && _tasks.Active && !Brain.IsTaskContinuation(userText))
            context = (string.IsNullOrEmpty(context) ? "" : context + "\n") +
                      $"(Справка: у тебя открыта долгая задача, ты к ней вернёшься сама после ответа.\n{_tasks.Describe()})";
        // что делают её агенты прямо сейчас (шаг, план, субагенты) — чтобы отвечала «как там агент» по делу, а не выдумывала
        if (AgentHub.Overview() is { Length: > 0 } agents)
            context = (string.IsNullOrEmpty(context) ? "" : context + "\n") + $"(Справка — твои агенты сейчас:\n{agents})";

        // Память — одним блоком и по времени (Chronicle, друг 01.10: «по датам и по часам, как часовой»): часы; на вопрос
        // про прошлое — хроника нужного отрезка из банка; к слову — факты графа, эпизод или заметка банка, сегодняшние
        // реплики и итог прошлого дня одним списком, каждая запись с датой; знание о говорящем; созревший план.
        var clockNow = DateTime.Now;
        var memory = new List<string> { _memoryState.ClockNote(clockNow, _prevTurnAt) };
        if (isUser)
        {
            // говорили несколько — память по тому, кто сказал последним
            var memSpeaker = batch is { Count: > 0 } ? batch[^1].Speaker : speakerName;
            var bank = _memoryBank.ContextFor(memSpeaker, userText);
            var recalls = new List<Recall>(bank.Items);
            var (friendFacts, lastDay) = _friendMemory.NoteFor(memSpeaker, userText);
            if (lastDay is not null) recalls.Add(lastDay);
            recalls.AddRange(await _memoryGraph.ContextForAsync(memSpeaker, userText));
            if (bank.Timeline is null && LightMemory.HasMemoryIntent(userText)) recalls.AddRange(_lightMemory.Search(userText));
            if (bank.Timeline is not null) memory.Add(bank.Timeline);
            if (Chronicle.Compose(recalls, clockNow) is { } remembered) memory.Add(remembered);
            if (friendFacts is not null) memory.Add(friendFacts);
            if (bank.Plan is not null) memory.Add(bank.Plan);
        }
        context = string.IsNullOrEmpty(context) ? string.Join("\n", memory) : $"{string.Join("\n", memory)}\n{context}";

        var phrases = Channel.CreateUnbounded<Phrase>();
        // Озвучка идёт параллельно генерации: пока играет фраза N, синтезируется N+1.
        var speakerTask = Task.Run(async () =>
        {
            var first = true;
            await foreach (var p in phrases.Reader.ReadAllAsync(cts.Token))
            {
                // голос потоком: куски звука уходят странице по мере синтеза — первый через ~0,5 с, а не после всей фразы
                if (_cfg.TtsStream && !_cfg.Muted && !_cfg.LiteSpeech && p.Text.Length > 0)
                {
                    var isFirstS = first;
                    first = false;
                    await SpeakStreamAsync(p, isFirstS, userText, cts);
                    continue;
                }
                byte[] wav = [];
                if (!_cfg.Muted && p.Text.Length > 0)
                {
                    // 1 попытка + 1 ретрай при пустом/битом ответе — чтобы одиночное залипание TTS не съело фразу.
                    for (var attempt = 0; attempt < 2; attempt++)
                    {
                        try
                        {
                            wav = await _voice.SynthAsync(p.Text, cts.Token, p.Voice);
                            // фразу спас лёгкий голос — она звучит, но большой сервер всё равно лежит и его надо поднять
                            if (wav.Length > 0 && !_cfg.LiteSpeech && !_voice.LastFromServer)
                            {
                                if (Interlocked.Increment(ref _ttsFails) >= 2 && !_ttsRestarting && DateTime.UtcNow - _lastTtsRestart > TimeSpan.FromMinutes(1))
                                    _ = RestartTtsInBackgroundAsync();
                                break;
                            }
                            if (wav.Length > 0) { _ttsFails = 0; break; }
                            Log.Write($"голос: пустой ответ (попытка {attempt + 1})");
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Log.Error($"голос (попытка {attempt + 1})", ex);
                        }
                        await Task.Delay(200, cts.Token);
                    }
                    if (wav.Length == 0)
                    {
                        var fails = Interlocked.Increment(ref _ttsFails);
                        if (fails >= 2 && !_ttsRestarting && DateTime.UtcNow - _lastTtsRestart > TimeSpan.FromMinutes(1))
                            _ = RestartTtsInBackgroundAsync();
                    }
                }
                var isFirst = first;
                first = false;
                BeginInvoke(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    // фраза без звука ставится в очередь окна уже после конца хода — тогда флаг «её ход» залипал навсегда
                    if (isFirst && _turn == cts) { Post(new { type = "state", value = "idle" }); _voiceTurn = true; UpdateDeaf(); }
                    _lastSpoken = p.Text;
                    Log.Write($"говорит [{p.Emotion}/{p.Voice}] «{p.Text}» ({(wav.Length == 0 ? "без звука" : $"{wav.Length / 48000.0:F1} с")})");
                    // [наряд …] — не жест страницы, а переодевание: после того как договорит
                    var fx = p.Fx;
                    if (fx is not null && fx.StartsWith("outfit:", StringComparison.Ordinal))
                    {
                        _pendingOutfit = fx[7..];
                        fx = null;
                        if (wav.Length == 0) { ApplyOutfit(_pendingOutfit); _pendingOutfit = null; }
                    }
                    if (wav.Length == 0)
                    {
                        if (p.Text.Length > 0)
                        {
                            Post(new { type = "emotion", name = p.Emotion });
                            Post(new { type = "bubble", text = p.Text });
                            if (KlavaVR.IsVrcRunning()) KlavaVR.SendChatbox(p.Text);
                        }
                        ApplyFx(fx);
                    }
                    else
                    {
                        _speaking = true;
                        // когда страница доиграет очередь (24 кГц, 16 бит, моно — 48000 байт в секунду)
                        var now = DateTime.UtcNow;
                        var start = _playUntil > now ? _playUntil : now;
                        _playUntil = start.AddSeconds(wav.Length / 48000.0);
                        lock (_played) _played.Add((start, _playUntil, p.Text));
                        UpdateDeaf();
                        Post(new { type = "say", id = Interlocked.Increment(ref _sayId), text = p.Text, emotion = p.Emotion, wav = Convert.ToBase64String(wav),
                                   fx, fxAt = p.FxAfter ? "after" : "before" });
                    }
                    if (p.Fx is null) GestureFor(p.Text, userText);
                });
            }
        }, cts.Token);

        var fullReplySb = new System.Text.StringBuilder();
        var failed = false;
        try
        {
            var firstSeen = false;
            // «Хм…», «Так-так…» в паузе убраны (друг 29.09: «вот от этого и надо избавиться»): вместо заглушки — быстрый
            // первый звук (первая фраза режется по запятой, голос идёт кусками по мере синтеза)
            await foreach (var raw in _brain.AskAsync(userText, context, image, ApproveAsync, cts.Token))
            {
                if (!firstSeen) { firstSeen = true; Log.Write($"мозг {_brain.LastBrainId}: первая фраза через {turnWatch.ElapsedMilliseconds} мс"); }
                // последний рубеж: обращение к тому, кого нет в комнате, — вырезать до голоса
                if (GuardAddress(raw) is not { } p) continue;
                fullReplySb.Append(p.Text).Append(' ');
                await phrases.Writer.WriteAsync(p, cts.Token);
            }
        }
        catch (OperationCanceledException) { Log.Write("ответ прерван"); }
        catch (Exception ex)
        {
            failed = true;
            Log.Error("мозг", ex);
            Bubble($"Мозг не отвечает: {ex.Message}");
            // вслух — коротко и честно, ПОЧЕМУ (друг 18.11: «почему точно мозг не ответил — непонятно»)
            if (isUser && fullReplySb.Length == 0)
                phrases.Writer.TryWrite(new Phrase(ex is BrainsDownException down ? BrainGuard.SpokenWhy(down) : "Бля, мозг не ответил. Скажи ещё раз?", "sad"));
        }
        finally
        {
            // промолчала, потому что запела (music play) — не переспрашивать поверх песни
            if (!failed && !cts.IsCancellationRequested && isUser && fullReplySb.Length == 0 && !(_brain.TurnUsedTools && (SongPlaying || _pendingSong is not null)))
            {
                Log.Write("мозг промолчал — переспрашиваю вслух");
                phrases.Writer.TryWrite(new Phrase("Чё-то я зависла. Повтори, а?", "surprised"));
            }
            phrases.Writer.TryComplete();
        }
        try { await speakerTask; } catch (OperationCanceledException) { }
        Log.Write($"ответ готов за {turnWatch.ElapsedMilliseconds} мс");
        // друг попросил переодеться (или она сама сказала «переоделась»), а мозг метку [наряд …] не поставил —
        // переодеваемся по его словам, а нет их — по её
        if (isUser && _pendingOutfit is null && OutfitAfterReply(userText, fullReplySb.ToString(), _cfg.Outfit) is { } dressTo)
        {
            Log.Write($"гардероб: метки не было — по словам надеваю {dressTo}");
            _pendingOutfit = dressTo;
        }
        if (!_speaking) ApplyPendingLook();
        if (_pendingOutfit is { } afterTurn && !_speaking) { _pendingOutfit = null; ApplyOutfit(afterTurn); }
        _hands.End();   // мышь — только на время задачи; договорила — отпустила
        Post(new { type = "activity", kind = "none" });   // ноутбук убрать

        // Журнал: её ответ (слова людей из услышанной пачки там уже лежат) или то, что она сказала сама
        var fullReply = fullReplySb.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(fullReply))
        {
            if (isUser)
            {
                var speakers = batch is { Count: > 0 } ? batch.Select(b => b.Speaker).Distinct().ToList() : [speakerName ?? "Друг"];
                var entry = _flightJournal.LogTurn(speakers.Count == 1 ? speakers[0] : string.Join(", ", speakers.Select(HumanName)),
                    userText, fullReply, screenNote, heard: batch is not null, brain: SpokenBy());
                if (entry is not null) _lightMemory.IndexTurn(entry);
                foreach (var sp in speakers) _friendMemory.OnTurn(sp);
                _memoryBank.OnTurn();
            }
            else if (_flightJournal.LogSelf(userText, fullReply, SpokenBy()) is { } own) _lightMemory.IndexTurn(own);
            _prevTurnAt = DateTime.Now;
        }

        if (_turn == cts)
        {
            // Ход закончен: _turn обнуляем — иначе таймер затишья вечно считал её «занятой». _speaking НЕ трогаем:
            // последняя фраза ещё играет, «idle» от страницы снимет его сам.
            _turn = null;
            _lastTurnEnded = DateTime.UtcNow;
            _busy = false; _voiceTurn = false; UpdateDeaf();
            _turnBatch = null;
            _turnInput = "";
            _merges = 0;
            Post(new { type = "state", value = "idle" });

            // Пока она говорила, друг мог сказать ещё — его реплики ждут в буфере: отвечаем сразу.
            var pending = _pendingFlush;
            _pendingFlush = false;
            var members = ActiveMembers();
            var needed = Math.Max(1, members.Count > 0 ? members.Count : _cfg.DialogBatchMaxPhrases);
            bool shouldFlush;
            lock (_batchGate)
                shouldFlush = _tempBatch.Count > 0 && (pending || _tempBatch.Count >= needed ||
                              (DateTime.UtcNow - _lastBatchTime).TotalSeconds >= _batchWaitSec);
            if (shouldFlush)
            {
                Log.Write("ход закончен — отвечаю на то, что сказали, пока я говорила");
                _ = FlushBatchAsync();
            }
        }
    }

    /// <summary>Жест по смыслу фразы (или реплики друга): печатает, фейспалм, ня, чокается, пьёт, курит.</summary>
    private void GestureFor(string said, string userText)
    {
        bool Any(System.Text.RegularExpressions.Regex rx) => rx.IsMatch(said) || (!string.IsNullOrEmpty(userText) && !userText.StartsWith('(') && rx.IsMatch(userText));
        // друг зовёт в кресло, на кровать, в ванну, танцевать или встать, а мозг метку не поставил — один раз за его реплику
        if (!string.IsNullOrEmpty(userText) && !userText.StartsWith('(') && _placeFor != userText && PlaceOf(userText) is { } place)
        {
            _placeFor = userText;
            if (place == "caramell") Caramell();   // с песней из фонотеки, если она разобрана
            else if (place == "sleep") GoToSleep("слова друга");
            else if (place == "stand") { WakeUp("слова друга"); Post(new { type = "gesture", name = place }); }
            else Post(new { type = "gesture", name = place });
            Log.Write($"жест по словам друга: {place}");
            return;
        }
        if (Any(CodingRx())) { Post(new { type = "gesture", name = "typingBrief" }); MarkActivity("кодишь — пальчики на клавиатуре", 20); }
        else if (Any(FacepalmRx())) Post(new { type = "gesture", name = "facepalm" });
        else if (Any(NyaRx())) Post(new { type = "gesture", name = "nya" });
        else if (Any(BeerCheersRx())) { Post(new { type = "gesture", name = "cheers" }); MarkActivity("пьёшь киберпиво — кружка в лапе", 25); }
        else if (Any(BeerDrinkRx())) { Post(new { type = "gesture", name = "beer" }); MarkActivity("пьёшь пиво — кружка в лапе", 30); }
        else if (Any(SmokeRx())) { Post(new { type = "gesture", name = "smoke" }); MarkActivity("куришь сигарету — дым в комнате, затягиваешься", 60); }
        else if (_eatFor != userText && (EatRx().IsMatch(said) || (!string.IsNullOrEmpty(userText) && !userText.StartsWith('(') && EatRx().IsMatch(userText))))
        {
            // один раз на реплику друга (иначе на каждой её фразе еда начиналась заново) и не поверх метки мозга
            _eatFor = userText;
            // «съешь чипсы», «хрумкаю рыбку», «зажую чеснок» — ест (web/snacks.js); без глагола еды не трогаем
            var both = said + " " + userText;
            var food = ChipsRx().IsMatch(both) ? "chips" : SquidRx().IsMatch(both) ? "squid" : FishRx().IsMatch(both) ? "fish"
                : GarlicRx().IsMatch(both) ? "garlic" : null;
            if (food is not null)
            {
                Post(new { type = "gesture", name = food });
                Log.Write($"жест по словам: ест ({food})");
            }
        }
        // друг просит рыгнуть/пукнуть, а мозг метку не поставил — один раз за его реплику
        else if (!string.IsNullOrEmpty(userText) && !userText.StartsWith('(') && _gasFor != userText && GasAskRx().Match(userText) is { Success: true } gas)
        {
            _gasFor = userText;
            Post(new { type = "gesture", name = gas.Groups["burp"].Success ? "burp" : "fart" });
        }
    }

    private string? _gasFor;
    private string? _placeFor;
    /// <summary>Что она сейчас делает (курит/пьёт/кодит/ест) и до какого момента — подмешивается в контекст реплик.</summary>
    private readonly Dictionary<string, DateTime> _activityUntil = new();

    /// <summary>Отметить, что она начала курить/пить/кодить. Активность живёт <paramref name="seconds"/> и попадает в контекст.</summary>
    internal void MarkActivity(string label, double seconds)
    {
        _activityUntil[label] = DateTime.UtcNow.AddSeconds(seconds);
    }

    /// <summary>Строка «что я сейчас делаю» для контекста мозга; пустая, если ничего активного.</summary>
    private string? ActivityNote()
    {
        var now = DateTime.UtcNow;
        var list = _activityUntil.Where(kv => kv.Value > now).Select(kv => kv.Key).ToList();
        if (IsSleeping) list.Add("спишь в кровати, сладко посапываешь");
        else if (_justWokeUp && now - _wokeUpAt < TimeSpan.FromMinutes(2)) list.Add("только что проснулась после сладкого сна, потягиваешься и зеваешь");
        foreach (var k in _activityUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList()) _activityUntil.Remove(k);
        return list.Count == 0 ? null : $"(Прямо сейчас ты: {string.Join(", ", list)}. Это правда, отвечай по этому состоянию, не отрицай.)";
    }

    /// <summary>Просьба друга про обстановку → жест страницы (web/props.js) или null.</summary>
    internal static string? PlaceOf(string userText)
    {
        // «станцуй Карамельдансен», «давай карамельку», «врубай карамельдансен» — мем-танец с ушками
        if (CaramellAskRx().IsMatch(userText)) return "caramell";
        var m = PlaceAskRx().Match(userText);
        if (!m.Success) return null;
        return m.Groups["stand"].Success ? "stand" : m.Groups["sleep"].Success ? "sleep" : m.Groups["bed"].Success ? "bed"
            : m.Groups["bath"].Success ? "bath"
            : m.Groups["chair"].Success ? (userText.Contains("массаж", StringComparison.OrdinalIgnoreCase) ? "chair" : "armchair") : "danceBrief";
    }
    // глагол-просьба + место: «сядь в кресло», «ложись на кровать», «иди спать», «залезай в ванну», «потанцуй», «встань»
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<stand>\b(?:встань|вставай|поднимайся|подъём|подъем|вылезай|слезай|хватит\s+(?:валяться|лежать|сидеть|спать|танцевать|плескаться))\b)|(?<sleep>\b(?:иди|пора|ложись|давай|можешь)\s+(?:\w+\s+)?(?:спать|баиньки|дрыхнуть|поспать|вздремнуть)\b)|(?<bath>\b(?:залезай|полезай|прими|иди|давай|понежься|полежи|окунись|плюхнись)\w*\b.{0,25}(?:ванн|пен[уеой]\b|купел)|\b(?:искупайся|купнись)\b)|(?<bed>\b(?:ляг|ложись|приляг|полежи|завались|плюхнись)\w*\b.{0,25}(?:кроват|постел|балдахин)|\b(?:ляг|приляг)\b)|(?<chair>\b(?:сядь|садись|присядь|посиди|усаживайся|развались|плюхнись)\w*\b.{0,25}(?:кресл|массаж)|\b(?:сядь|присядь)\b(?!\s+за\b))|(?<dance>(?<!не\s)\b(?:потанцуй|станцуй|танцуй|потанцуем|спляши|попляши)\w*\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex PlaceAskRx();
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<!не\s)(?:станцуй|танцуй|потанцуй|потанцуем|спляши|попляши|давай|врубай|включай|покажи|зажги|го\b|погнали)\w*\W+(?:\w+\W+){0,3}(?:карамел|caramel)|(?:карамел|caramel)\w*\W+(?:\w+\W+){0,2}(?:станцуй|танцуй|давай|покажи|го\b|погнали)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CaramellAskRx();
    private string? _eatFor;

    // глагол еды, в том числе с приставкой: «похрусти», «схрумкай», «зажуй», «покушай», «похавай»
    [System.Text.RegularExpressions.GeneratedRegex(@"\b(?:по|на|за|под|с|у)?(ем|ешь|ест|съе\w*|съем|куша\w*|жр\w*|хрум\w*|хрус\w*|трескаю|точу|грыз\w*|перекус\w*|угоща\w*|угощу|жу[её]\w*|жую|жуй\w*|закус\w*|хомяч\w*|слопа\w*|лопай|хава\w*|наверн\w*|попробу\w*|вкусн\w*|ням\w*|держи\s+(чипс|рыб|чеснок))", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EatRx();
    [System.Text.RegularExpressions.GeneratedRegex(@"чипс", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ChipsRx();
    [System.Text.RegularExpressions.GeneratedRegex(@"(рыб[кауоыэ]|рыбой|вобл|тарань|сёмг|семг|селёд|селед|шпрот|скумбри|кильк)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FishRx();
    [System.Text.RegularExpressions.GeneratedRegex(@"(чеснок|чесноч)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex GarlicRx();
    [System.Text.RegularExpressions.GeneratedRegex(@"(кальмар|темпур)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SquidRx();
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<burp>рыгн\w*|рыгай|рыгани|отрыгн\w*)|(?<fart>пукн\w*|пукай|перни\w*|пёрни\w*|пердан\w*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex GasAskRx();

    // «стоп», «хватит», «стой», «отпусти», «убери руки» — во время работы лапками мгновенно их отобрать.
    [System.Text.RegularExpressions.GeneratedRegex(@"\b(стоп|стой|хватит|хорош|отпусти|убери\s+руки|прекрати|отставить)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex StopHandsRx();

    [System.Text.RegularExpressions.GeneratedRegex(@"(игров(ой|ым)\s+режим|режим\s+стример|поверх\s+игры|проклик)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex GameOverlayRx();

    // «режим работы», «рабочий режим», «режим общения», «режим болтовни», «авторежим»
    [System.Text.RegularExpressions.GeneratedRegex(@"(?<work>режим\s+работ|рабоч\w+\s+режим)|(?<chat>режим\s+(общени|болтовн)|режим\s+поболтать)|(?<auto>авто\s*режим|режим\s+авто|автоматическ\w+\s+режим)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex WorkModeRx();

    // «Клава», «Клавуся», «Клавочка», «Клаву» — обращаются к ней: отвечает сразу, без ожидания затишья
    [System.Text.RegularExpressions.GeneratedRegex(@"\bклав", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex KlavaNameRx();

    [System.Text.RegularExpressions.GeneratedRegex(@"\b(просн(ись|итесь)|встав(ай|айте)|подъем|подъём|хватит спать|пора вставать|доброе утро|просыпай(ся|тесь))\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex WakeUpWordsRx();

    private static bool IsWakeUpCall(string text) =>
        !string.IsNullOrWhiteSpace(text) && (KlavaNameRx().IsMatch(text) || WakeUpWordsRx().IsMatch(text));

    [System.Text.RegularExpressions.GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial System.Text.RegularExpressions.Regex NonWordRx();

    // служебные слова и её «мур» есть почти в любой фразе — по ним эхо не узнать, только ложные срабатывания
    private static readonly HashSet<string> EchoStopWords = new(StringComparer.Ordinal)
    {
        "ну", "да", "нет", "не", "и", "а", "но", "я", "ты", "мы", "он", "она", "же", "ж", "чё", "че", "что", "это", "вот", "так",
        "как", "там", "тут", "в", "на", "с", "у", "к", "о", "по", "за", "мур", "ня", "ага", "угу", "ой", "ну-ка", "бля", "блин",
    };

    private static List<string> ContentWords(string s) =>
        NonWordRx().Split(s.ToLowerInvariant().Replace('ё', 'е'))
            .Where(w => w.Length >= 2 && !EchoStopWords.Contains(w)).ToList();

    /// <summary>
    /// Эхо: услышанное — её же фраза из колонок. Эхо звучит одновременно с ней, поэтому сравниваем только с теми
    /// её фразами, что играли в момент этой реплики (±1,5 с на хвост в комнате). Раньше брались все фразы за 40 с —
    /// и друг, повторивший пару её слов через 5 секунд («Не доебывайся до Аксюты»), выкидывался как эхо.
    /// </summary>
    private List<string> EchoPhrases(DateTime endedAt, double seconds)
    {
        var start = endedAt - TimeSpan.FromSeconds(seconds + _cfg.VadSilenceMs / 1000.0);
        lock (_played)
        {
            _played.RemoveAll(p => endedAt - p.End > TimeSpan.FromMinutes(2));
            return _played.Where(p => p.Start - TimeSpan.FromMilliseconds(300) <= endedAt && p.End + TimeSpan.FromSeconds(1.5) >= start)
                          .Select(p => p.Text).ToList();
        }
    }

    /// <summary>
    /// Совпадает ли услышанное с её фразами настолько, что это эхо: значимые слова (длинные — по началу, whisper
    /// путает окончания: «киберпивка/киберпивко»). 1–2 слова — все должны совпасть; 3 и больше — не меньше трёх
    /// и не меньше 60 % услышанного, иначе короткое «давай погнали, накатим» друга посчиталось бы эхом.
    /// </summary>
    internal static bool EchoMatch(IEnumerable<string> saidTexts, string heard)
    {
        var said = saidTexts.SelectMany(ContentWords).ToList();
        var got = ContentWords(heard);
        if (got.Count == 0) return false;
        var stems = said.Where(w => w.Length >= 5).Select(w => w[..4]).ToHashSet();
        var saidSet = said.ToHashSet();
        var hit = got.Count(w => saidSet.Contains(w) || (w.Length >= 5 && stems.Contains(w[..4])));
        if (got.Count <= 2) return hit == got.Count;
        return hit >= 3 && hit >= got.Count * 0.6;
    }

    /// <summary>
    /// Профиль голоса самой Клавы для speaker_id — из её эталона (tts/voice.json → ref). Без него её же голос из
    /// колонок опознавался как похожий женский голос друзей (эхо шло как «кАксюта» с совпадением 0,82).
    /// </summary>
    /// <summary>Модель голосов и пороги «свой» (после тренировки слуха в окне профилей — подобранные заново).</summary>
    private async Task RefreshSpeakerInfoAsync()
    {
        if (await _speaker.InfoAsync() is not { } info) return;
        _speakerThreshold = info.Threshold;
        _speakerThresholdShort = double.IsNaN(info.ShortThreshold) ? info.Threshold - 0.06 : info.ShortThreshold;
        _speakerModel = info.Model;
        Log.Write($"speaker: модель {info.Model}, порог «свой» {info.Threshold:F2} (короткие фразы {_speakerThresholdShort:F2}), профилей {info.Profiles}");
    }

    /// <summary>Сервер голосов поднялся: узнать его модель и порог «свой», завести профиль её голоса.</summary>
    private async Task OnSpeakerReadyAsync()
    {
        await RefreshSpeakerInfoAsync();
        await EnsureSelfVoiceAsync();
        var members = ActiveMembers();
        if (members.Count > 0)
        {
            var names = (await _speaker.ListAsync()).Select(p => p.Name).ToHashSet();
            var missing = members.Where(m => !names.Contains(m)).ToList();
            if (missing.Count > 0) Bubble($"Нет записи голоса: {string.Join(", ", missing.Select(HumanName))} — запиши в «Голосовые профили».");
        }
    }

    private async Task EnsureSelfVoiceAsync()
    {
        try
        {
            if (!await _speaker.AliveAsync()) return;
            if (_cfg.LiteSpeech)
            {
                // в лёгком комплекте она говорит голосом Supertonic — эхо узнаётся по нему; метка — какой голос уже записан
                var mark = _cfg.Abs($@"data\speakers\{SelfVoiceName}.lite");
                var voiceTag = $"{_cfg.LiteVoiceId} {_cfg.LiteVoicePitch.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                if (File.Exists(mark) && (await File.ReadAllTextAsync(mark)).Trim() == voiceTag) return;
                var sample = await _voice.SynthAsync("Приветик! Я Клава, твоя кошечка. Мур-мур! Давай сегодня сделаем что-нибудь классное, а?", CancellationToken.None);
                var had = (await _speaker.ListAsync()).Any(p => p.Name == SelfVoiceName);
                var okLite = sample.Length > 0 && await _speaker.EnrollAsync(SelfVoiceName, sample, append: had);
                if (okLite) await File.WriteAllTextAsync(mark, voiceTag);
                Log.Write(okLite ? $"speaker: записала свой лёгкий голос в профиль «{SelfVoiceName}» — своё эхо буду пропускать"
                                 : "speaker: не смогла записать свой лёгкий голос");
                return;
            }
            // Большой голос: профиль — из того, что реально звучит из колонок (фразы текущей модели голоса), а не из
            // эталона. Эталон записан другой моделью; после смены голоса эхо Клавы переставало узнаваться как её
            // и уходило в ближайший женский профиль — «Аксюта говорит, когда она молчала».
            var voiceModel = Directory.Exists(_cfg.Abs(@"models\Qwen3-TTS-12Hz-1.7B-Base-ru-stress")) ? "qwen3-tts-1.7b-ru-stress" : "qwen3-tts-0.6b";
            var voiceJson = _cfg.Abs(@"tts\voice.json");
            var refRel = File.Exists(voiceJson) ? JsonNode.Parse(await File.ReadAllTextAsync(voiceJson))?["ref"]?.GetValue<string>() : null;
            var tag = $"{voiceModel}|{refRel}";
            var markFull = _cfg.Abs($@"data\speakers\{SelfVoiceName}.voice");
            if (File.Exists(markFull) && (await File.ReadAllTextAsync(markFull)).Trim() == tag
                && (await _speaker.ListAsync()).Any(p => p.Name == SelfVoiceName)) return;
            string[] lines =
            [
                "Приветик! Я Клава, твоя кошечка. Мур-мур! Давай сегодня сделаем что-нибудь классное, а?",
                "Слушай, ну ты даёшь, конечно. Я тут подумала и решила, что сегодня мы точно всё успеем.",
                "Ой, блин, опять эти провода! Ладно, щас разберусь, не переживай, мур.",
            ];
            var added = 0;
            foreach (var line in lines)
            {
                byte[] wav;
                try { wav = await _voice.SynthAsync(line, CancellationToken.None); }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { continue; }
                if (wav.Length == 0 || !_voice.LastFromServer) continue;
                if (await _speaker.EnrollAsync(SelfVoiceName, wav, append: added > 0)) added++;
            }
            if (added > 0) await File.WriteAllTextAsync(markFull, tag);
            Log.Write(added > 0 ? $"speaker: профиль своего голоса «{SelfVoiceName}» — {added} фразы текущим голосом ({voiceModel}); своё эхо буду пропускать"
                                : "speaker: не смогла записать свой голос в профиль (голос не ответил)");
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Log.Error("speaker: свой профиль", ex);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:я не|это не)\s+(?<wrong>\w+).*?(?:я|это|был|была)\s+(?<correct>\w+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SpeakerCorrectionRx();

    [System.Text.RegularExpressions.GeneratedRegex(@"(чока|чокне|дзынь|тост|банку|бокал|за\s+нас|за\s+здоровье)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex BeerCheersRx();

    [System.Text.RegularExpressions.GeneratedRegex(@"(пив[оауеы]|пивас|пивк|киберпив|бухн|накат|налив|вскры|глот|хлебн|дерн)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex BeerDrinkRx();

    [System.Text.RegularExpressions.GeneratedRegex(@"(сиг[ауеы]|покур|закур|напас|тяг[ау]|затян|дыми|папирос|косяк|смок|табач)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SmokeRx();

    [System.Text.RegularExpressions.GeneratedRegex(@"(код|закод|программ|скрипт|компил|билд|собер|пофикс|разработ|печата)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CodingRx();

    [System.Text.RegularExpressions.GeneratedRegex(@"(рукалицо|фейспалм|facepalm|рука\s+лицо|позор|ппц|пипец|пиздец)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex FacepalmRx();

    [System.Text.RegularExpressions.GeneratedRegex(@"(ня[а-я]*|мило|лапки|царапк|помурчи)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex NyaRx();

    /// <summary>
    /// Прибить залипший python-сервер голоса и поднять его заново. Уведомляем друга пузырём, синтезом не занимаем
    /// поток текущего ответа — эта фраза уйдёт «без звука», зато следующие снова заговорят.
    /// </summary>
    private async Task RestartTtsInBackgroundAsync()
    {
        _ttsRestarting = true;
        _lastTtsRestart = DateTime.UtcNow;
        try
        {
            Bubble("Голос завис, перезапускаю…");
            var ok = await _services.RestartTtsAsync(new Progress<string>(s => Log.Write($"голос: {s}")));
            Bubble(ok ? "Голос ожил, продолжаем." : "Голос не поднялся — буду отвечать текстом.");
            _ttsFails = 0;
        }
        catch (Exception ex) { Log.Error("перезапуск голоса", ex); }
        finally { _ttsRestarting = false; }
    }

    /// <summary>Спросить друга. Модальное окно на UI-потоке; без «Разрешить» — отказ.</summary>
    private Task<bool> ApproveAsync(ToolRequest req)
    {
        var tcs = new TaskCompletionSource<bool>();
        BeginInvoke(() =>
        {
            Post(new { type = "state", value = "thinking" });
            using var f = new ApprovalForm(req, _lastSpoken);
            var ok = f.ShowDialog() == DialogResult.Yes;
            // Запомнить «Разрешать всегда» для этой категории в сессии (только не Dangerous)
            if (ok && f.AlwaysAllow && _brain.Toolbox is { } tb && !req.Dangerous)
            {
                tb.AlwaysAllowed.Add(req.Category);
                Log.Write($"«Разрешать всегда» добавлена категория [{req.Category}] (сессия)");
            }
            Log.Write($"инструмент «{req.Title}»: {(ok ? "РАЗРЕШЕНО" : "запрещено")}{(ok && f.AlwaysAllow ? " [всегда]" : "")} — {req.Details.Split('\n')[0]}");
            tcs.SetResult(ok);
        });
        return tcs.Task;
    }

    private void OnPoked(string part)
    {
        if (!_ready || _busy || _speaking || _ears.PttRecording || SongPlaying || DateTime.UtcNow - _lastPoke < TimeSpan.FromSeconds(8)) return;
        _lastPoke = DateTime.UtcNow;
        _ = ConverseAsync(part switch
        {
            "pet" => "(Друг гладит тебя по голове — тебе очень приятно, ты жмуришься и мурчишь)",
            "head" => "(Друг погладил тебя по голове)",
            _ => "(Друг легонько ткнул тебя пальцем)",
        });
    }

    /// <summary>
    /// Раз в 15 с: что у друга на переднем плане. Если он минимум 20 с сидит в новом окне, давно молчит
    /// и Клава давно ничего не говорила сама — она коротко прокомментирует. Не чаще WatchCommentMinutes.
    /// </summary>
    private void OnWatch(object? sender, EventArgs e)
    {
        LifeTick();
        if (!_cfg.WatchScreen || _eyes is null) return;
        var g = _eyes.Foreground();
        if (g is null) return;
        var key = g.App + "|" + g.Title;
        var now = DateTime.UtcNow;
        if (key != _glanceKey)
        {
            _glanceKey = key;
            _glanceSince = now;
            _glanceCommented = false;
            return;
        }
        if (_glanceCommented || now - _glanceSince < TimeSpan.FromSeconds(20)) return;
        if (_busy || _speaking || _ears.PttRecording || SongPlaying) return;
        if (now - _lastTalk < TimeSpan.FromSeconds(90)) return;                       // вы и так болтаете
        if (now - _lastWatchComment < TimeSpan.FromMinutes(Math.Max(1, _cfg.WatchCommentMinutes))) return;
        _glanceCommented = true;
        _lastWatchComment = now;
        Log.Write($"глаза: заметила {g.Describe()}");
        _ = ConverseAsync($"(Ты краем глаза заметила, что друг сейчас сидит в: {g.Describe()}. " +
                          "Скажи про это что-нибудь одной короткой дружеской фразой — пошути или спроси.)");
    }

    /// <summary>
    /// Наблюдение заметило новое. Друг рядом (трогал мышь/клавиатуру за последние 2 минуты) и ей можно влезть —
    /// одна живая реплика про увиденное, не чаще WatchCommentMinutes. Друг спит или отошёл — только журнал, молча.
    /// </summary>
    private void OnWatchNoticed(WatchEntry e)
    {
        var now = DateTime.UtcNow;
        if (Native.UserIdle() > TimeSpan.FromMinutes(2) || !CanChime(now)) return;
        if (now - _lastWatchComment < TimeSpan.FromMinutes(Math.Max(1, _cfg.WatchCommentMinutes))) return;
        _lastWatchComment = now;
        Log.Write($"глаза: наблюдая, заметила — {e.What}");
        _ = ConverseAsync($"(Ты поглядываешь за экраном и видишь: {e.What} Скажи про это одной короткой живой фразой — " +
                          "похвали, пошути или спроси по делу. Не пересказывай увиденное целиком.)");
    }

    // ---------------- Minecraft: чат игры → Клава ----------------

    private readonly System.Windows.Forms.Timer _mcPoll = new() { Interval = 3000 };
    private int _mcEventId = -1;
    private bool _mcPolling;
    private static readonly HttpClient McEventsHttp = NetProxy.Client(null, TimeSpan.FromSeconds(3));

    /// <summary>
    /// Раз в 3 с, пока мост в Minecraft запущен: новые события игры. Игрок пишет Клаве (шёпотом, по имени или когда
    /// в игре с ней он один) — это реплика ей: она отвечает в чат игры и вслух, просьбу в игре выполняет.
    /// </summary>
    private async void OnMcPoll(object? sender, EventArgs e)
    {
        if (_mcPolling || !_ready) return;
        _mcPolling = true;
        try
        {
            string raw;
            try { raw = await McEventsHttp.GetStringAsync($"{Tools.McBridgeUrl}/events?since={Math.Max(0, _mcEventId)}"); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return; }   // мост не запущен
            var n = JsonNode.Parse(raw);
            var last = n?["last"]?.GetValue<int>() ?? 0;
            if (_mcEventId < 0) { _mcEventId = last; return; }   // старое, что было до нас, не пересказываем
            _mcEventId = last;
            if (n?["events"] is not JsonArray evs) return;
            foreach (var ev in evs)
            {
                var kind = ev?["kind"]?.GetValue<string>() ?? "";
                var from = ev?["from"]?.GetValue<string>() ?? "";
                var text = ev?["text"]?.GetValue<string>() ?? "";
                switch (kind)
                {
                    case "chat" or "whisper":
                    {
                        var toHer = kind == "whisper" || KlavaNameRx().IsMatch(text) || text.Contains("klava", StringComparison.OrdinalIgnoreCase);
                        Log.Write($"minecraft: <{from}> {text}{(toHer ? " (ей)" : "")}");
                        if (!toHer) continue;
                        var mcPrompt = $"(В Minecraft игрок {from} пишет {(kind == "whisper" ? "тебе лично" : "в чат")}: «{text}». " +
                                       "Ответь ему коротко в чат игры (mc_chat), можно и вслух. Просит что-то сделать в игре — сделай (mc_action).)";
                        if (SongPlaying) { QueueEvent(mcPrompt); continue; }   // допоёт — ответит
                        await ConverseAsync(mcPrompt);
                        break;
                    }
                    case "death":
                        Bubble("Меня в майне убили, мяу…");
                        Log.Write("minecraft: Клаву убили");
                        break;
                    case "kicked" or "end":
                        Log.Write($"minecraft: {text}");
                        break;
                }
            }
        }
        catch (JsonException) { }
        finally { _mcPolling = false; }
    }

    // ---------------- живость: она сама по себе ----------------

    private readonly Random _rng = new();
    private DateTime _nextChatter = DateTime.MaxValue;
    private DateTime _lastChime = DateTime.MinValue;
    private int _ignoredChimes;
    private bool _away;
    private DateTime _awaySince;
    private DateTime _sessionStart = DateTime.UtcNow;
    private DateTime _lastNightNag = DateTime.MinValue;
    private DateTime _lastBreakNag = DateTime.MinValue;

    /// <summary>С чего она может начать разговор сама — мозг превращает затравку в живую фразу своим характером.</summary>
    private static readonly string[] ChatterSeeds =
    [
        "Расскажи ему короткую смешную историю из своей кошачьей жизни на его рабочем столе.",
        "Спроси с интересом, как у него день и чем он сейчас занят.",
        "Поделись одним неожиданным интересным фактом — про космос, животных, железо или игры.",
        "Предложи мини-игру голосом — города, загадку или «правда или ложь» — и сразу начни.",
        "Шутливо пожалуйся, что тебе скучно, и предложи, чем заняться вместе.",
        "Сделай ему искренний, но пацанский комплимент.",
        "Загадай ему короткую загадку и жди ответа.",
        "Вспомни что-нибудь из ваших прошлых разговоров и спроси, как с этим дела.",
        "Промурлычи что-нибудь и признайся, о чём ты сейчас думала.",
        "Предложи ему что-нибудь сделать на компе вместе: нарисовать в Paint, найти музыку, глянуть новости или покодить.",
    ];

    /// <summary>Клава сейчас спит: посапывает, никого не зовёт, спонтанные оклики и болтовня наглухо выключены.</summary>
    public bool IsSleeping { get; private set; }
    private DateTime _sleptAt = DateTime.MinValue;
    private DateTime _wokeUpAt = DateTime.MinValue;
    private bool _justWokeUp;

    /// <summary>Уложить Клаву спать: отключает инициативу и оклики, включает жест sleep.</summary>
    public void GoToSleep(string reason)
    {
        if (IsSleeping) return;
        IsSleeping = true;
        _sleptAt = DateTime.UtcNow;
        _nightSleep = reason.StartsWith("ночь");
        Post(new { type = "gesture", name = "sleep" });
        Log.Write($"сон: уснула ({reason})");
    }

    /// <summary>Разбудить Клаву: потягивается, зевает, выходит из сна и помнит, что только что проснулась.</summary>
    public void WakeUp(string reason)
    {
        if (!IsSleeping) return;
        var sleptSpan = DateTime.UtcNow - _sleptAt;
        IsSleeping = false;
        _nightSleep = false;
        _wokeUpAt = DateTime.UtcNow;
        _justWokeUp = true;
        Post(new { type = "gesture", name = "stretch" });
        Log.Write($"сон: проснулась после {(int)sleptSpan.TotalMinutes} мин ({reason})");
    }

    /// <summary>Применить жест fx с учётом сна и пробуждения.</summary>
    private void ApplyFx(string? fx)
    {
        if (fx is null) return;
        if (fx == "sleep") GoToSleep("метка мозга");
        else if (fx == "stand") { WakeUp("метка мозга"); Post(new { type = "gesture", name = fx }); }
        else Post(new { type = "gesture", name = fx });
    }

    private void ScheduleChatter() =>
        _nextChatter = DateTime.UtcNow + TimeSpan.FromMinutes(Math.Max(1, _cfg.ChatterMinutes) * (0.6 + _rng.NextDouble() * 0.8));

    /// <summary>Можно ли ей сейчас влезть самой: никто не говорит, она не занята, не спит, и сама не трещала только что.</summary>
    private bool CanChime(DateTime now) =>
        _ready && !_busy && !_speaking && !_switchBlocks && !_ears.PttRecording && !IsBusy && !SongPlaying && !IsSleeping &&
        now - _lastTalk > TimeSpan.FromSeconds(90) && now - _lastChime > TimeSpan.FromMinutes(3) &&
        now - _lastWatchComment > TimeSpan.FromMinutes(2);

    private void Chime(string why, string prompt)
    {
        var now = DateTime.UtcNow;
        // друг молчит с прошлой её реплики — это «проигнорил»; две подряд — больше не навязывается
        _ignoredChimes = _lastTalk < _lastChime ? _ignoredChimes + 1 : 0;
        _lastChime = now;
        _lastWatchComment = now;
        Log.Write($"жизнь: {why}");
        _ = ConverseAsync(prompt);
    }

    /// <summary>
    /// Раз в 15 с (вместе с глазами): отошёл/вернулся, ночь, давно без перерыва, давно тихо. Каждое событие — затравка
    /// мозгу в скобках: он отвечает одной-двумя фразами в своём характере, без инструментов.
    /// </summary>
    private void LifeTick()
    {
        if (!_ready || !_cfg.LifeEvents) return;
        var now = DateTime.UtcNow;
        var idle = Native.UserIdle();
        // ночь: друг ушёл — спит в кровати и пишет дневник; вернулся — встаёт (OverlayDiary)
        if (NightTick(now, idle)) return;
        if (IsSleeping) return;   // дрыхнет — сопит и никого не зовёт, спонтанные оклики выключены
        if (idle > TimeSpan.FromMinutes(10))
        {
            // 10 минут без мыши и клавиатуры и без разговора — отошёл
            if (!_away && now - _lastTalk > TimeSpan.FromMinutes(10)) { _away = true; _awaySince = now - idle; Log.Write("жизнь: друг отошёл"); }
            return;
        }
        if (_away && idle < TimeSpan.FromSeconds(30))
        {
            _away = false;
            _sessionStart = now;
            var gone = now - _awaySince;
            _ignoredChimes = 0;
            ScheduleChatter();
            if (_lastTalk < _awaySince && CanChime(now))
            {
                var mins = (int)gone.TotalMinutes;
                var span = mins >= 120 ? $"{mins / 60} ч" : $"{mins} мин";
                // ночью записала сон в дневник — утром первым делом рассказывает его
                if (TakeUntoldDream() is { } dream)
                    Chime("утро: рассказывает сон",
                        $"(Друг вернулся к компьютеру, его не было {span}; ты спала и только проснулась. Ночью ты записала в дневник сон: «{dream}» " +
                        "Поздоровайся сонно и радостно и расскажи ему этот сон коротко, 2–3 живые фразы своими словами, не зачитывай; спроси, что снилось ему.)");
                else
                    Chime($"друг вернулся через {span}",
                        $"(Друг вернулся к компьютеру, его не было {span}. Встреть его одной живой фразой — обрадуйся, подколи или спроси, где пропадал.)");
                return;
            }
        }
        if (!CanChime(now)) return;
        var local = DateTime.Now;
        if (local.Hour is >= 1 and < 5 && now - _lastNightNag > TimeSpan.FromMinutes(50) && now - _lastTalk > TimeSpan.FromMinutes(3))
        {
            _lastNightNag = now;
            Chime("ночь",
                $"(Сейчас {local:HH:mm} ночи, а друг всё ещё за компом. Одной фразой по-дружески подколи, что пора бы спать, — заботливо, без нотаций.)");
            return;
        }
        if (now - _sessionStart > TimeSpan.FromHours(2) && now - _lastBreakNag > TimeSpan.FromHours(2))
        {
            _lastBreakNag = now;
            Chime("два часа без перерыва",
                "(Друг сидит за компом больше двух часов без перерыва. Одной фразой позови его размяться, попить воды или глянуть в окно — весело, по-свойски.)");
            return;
        }
        // пауза в разговоре, люди рядом — сама заводит разговор по имени, с поводом из памяти (OverlayInitiative)
        if (_cfg.ChatterMinutes > 0 && InitiativeTick(now)) return;
        if (_cfg.ChatterMinutes > 0 && _ignoredChimes < 2 && now >= _nextChatter &&
            now - _lastTalk > TimeSpan.FromMinutes(_cfg.ChatterMinutes * 0.6))
        {
            ScheduleChatter();
            var seed = ChatterSeeds[_rng.Next(ChatterSeeds.Length)];
            var day = local.ToString("dddd", new System.Globalization.CultureInfo("ru-RU"));
            Chime("давно тихо",
                $"(Давно тихо, друг за компом. Сейчас {local:HH:mm}, {day}. {seed} Одна-две короткие фразы, живо и по-свойски, не повторяй то, что уже говорила.)");
        }
    }

    /// <summary>
    /// Реплика текстом снаружи (KlavaDesk.exe --say "…"): как будто друг написал. Занята — ждёт в очереди и уходит
    /// сразу после её хода, ничего не обрывая.
    /// </summary>
    public void SayText(string text)
    {
        Log.Write($"--say: «{text}»");
        _flightJournal.LogHeard("Друг", text);   // всё, что в буфере, в журнале уже лежит
        lock (_batchGate)
        {
            _tempBatch.Add(("Друг", text, DateTime.UtcNow));
            _lastBatchTime = DateTime.UtcNow;
        }
        if (_ready && !IsBusy && !_switchBlocks) _ = FlushBatchAsync();
        else _pendingFlush = true;
    }

    private void ShowInput()
    {
        if (_input is { IsDisposed: false }) { Reveal(_input); return; }
        _input = new InputForm(text =>
        {
            if (IsKysCommand(text)) TryKys(text, DateTime.UtcNow, 0);   // «кыс-кыс» и текстом затыкает
            else if (_ready) _ = ConverseAsync(text);
            else Bubble("Погоди, я ещё просыпаюсь…");
        });
        var k = DeviceDpi / 96.0;
        _input.Location = new Point(Left + (Width - _input.Width) / 2, Math.Max(0, Top - _input.Height - (int)(8 * k)));
        _input.Show(this);   // окно Клавы — владелец: Windows держит своё окно над владельцем, под неё не провалится
        _input.Activate();
    }

    // ---------------- мышь ----------------

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = Native.MA_NOACTIVATE; return; }
        if (m.Msg == Native.WM_HOTKEY && m.WParam == HotkeyText) { ShowInput(); return; }
        if (m.Msg == Native.WM_HOTKEY && m.WParam == HotkeyGameOverlay) { ToggleGameOverlayMode(); return; }
        if (m.Msg == Native.WM_HOTKEY && m.WParam == HotkeyPanel) { ShowControlPanel(); return; }
        base.WndProc(ref m);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _mouseDown = true;
        _dragging = false;
        _downScreen = Cursor.Position;
        _downWindow = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_mouseDown) return;
        var d = new Point(Cursor.Position.X - _downScreen.X, Cursor.Position.Y - _downScreen.Y);
        if (!_dragging && Math.Abs(d.X) + Math.Abs(d.Y) > 6) _dragging = true;
        if (_dragging) Location = new Point(_downWindow.X + d.X, _downWindow.Y + d.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right) { _menu.Show(Cursor.Position); return; }
        if (e.Button != MouseButtons.Left) return;
        if (_mouseDown && !_dragging) Post(new { type = "poke", x = e.X / _scale, y = e.Y / _scale });
        var dragged = _dragging;
        _mouseDown = false;
        _dragging = false;
        Capture = false;
        if (dragged)
        {
            ClampToScreen();   // утащили за край или под панель задач — вернуть на экран
            if (_lastMask is { } m) ApplyMask(m.W, m.H, m.Cell, m.Bits);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        var step = Math.Sign(e.Delta);
        if ((ModifierKeys & Keys.Control) != 0) _cfg.WindowScale = Math.Clamp(Math.Round(_cfg.WindowScale + step * 0.05, 2), 0.4, 1.6);
        else if ((ModifierKeys & Keys.Shift) != 0) _cfg.ModelYaw = Math.Clamp(_cfg.ModelYaw + step * 15, -180, 180);
        else _cfg.CameraZoom = Math.Clamp(Math.Round(_cfg.CameraZoom + step * 0.06, 2), 0, 1);
        ApplyView();
        _viewForm?.LoadValues();
        _saveLater.Stop();
        _saveLater.Start();   // пишем конфиг, когда колесо успокоится
    }

    // ---------------- вид: размер, приближение, поворот ----------------

    /// <summary>Размер окна по конфигу и масштабу экрана, не больше рабочей области.</summary>
    private Size ScaledSize()
    {
        var k = DeviceDpi / 96.0 * Math.Clamp(_cfg.WindowScale, 0.4, 1.6);
        var wa = Screen.FromControl(this).WorkingArea;
        return new Size(Math.Min((int)(_cfg.WindowWidth * k), wa.Width), Math.Min((int)(_cfg.WindowHeight * k), wa.Height));
    }

    /// <summary>Применить вид: окно меняет размер, стоя «ногами» на том же месте, камера и поворот — на странице.</summary>
    private void ApplyView()
    {
        var size = ScaledSize();
        if (size != Size)
        {
            var bottom = Bottom;
            var center = Left + Width / 2;
            Size = size;
            var wa = Screen.FromControl(this).WorkingArea;
            Location = new Point(Math.Clamp(center - Width / 2, wa.Left, wa.Right - Width), Math.Clamp(bottom - Height, wa.Top, wa.Bottom - Height));
        }
        PostView();
    }

    private void PostView() => Post(new { type = "view", zoom = _cfg.CameraZoom, yaw = _cfg.ModelYaw });

    private void ShowViewForm()
    {
        if (_viewForm is { IsDisposed: false }) { Reveal(_viewForm); return; }
        _viewForm = new ViewForm(_cfg, ApplyView);
        var wa = Screen.FromControl(this).WorkingArea;
        _viewForm.Location = new Point(Math.Clamp(Left - _viewForm.Width - 10, wa.Left, wa.Right - _viewForm.Width), Math.Clamp(Top + Height / 3, wa.Top, wa.Bottom - _viewForm.Height));
        _viewForm.Show(this);
        _viewForm.Activate();
    }

    public void ShowAudioSettingsForm()
    {
        if (_audioSettingsForm is { IsDisposed: false }) { Reveal(_audioSettingsForm); return; }
        _audioSettingsForm = new AudioSettingsForm(_cfg, _ears, _services, ClearTempBatch, GetBufferStats, OpenTodayJournal, FileMemoryNowAsync, CorrectLastSpeakerAsync, RewardLastSpeakerAsync, BanLastHallucination, _speaker);
        _audioSettingsForm.Show(this);
        _audioSettingsForm.Activate();
    }

    /// <summary>Штраф за фантомные слова: добавление фразы в чёрный список галлюцинаций whisper, отмена хода и очистка буфера.</summary>
    public void BanLastHallucination(string? phrase = null)
    {
        var target = phrase?.Trim() ?? _lastUtteranceText?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(target) || target == "[неразборчиво]") return;

        if (target.StartsWith('⚠') || target.StartsWith('(')) return;   // служебная пометка лога — не слова whisper
        _ears.Learn?.BanHallucination(target);

        // Отменяем ответ, только если он — на эту самую фантомную фразу; чужой разговор не трогаем
        var ownTurn = _turnInput.Length > 0 && _turnInput.Contains(target, StringComparison.OrdinalIgnoreCase);
        if (ownTurn) StopTurn();

        // Очищаем временный буфер от этой фразы
        lock (_batchGate)
        {
            _tempBatch.RemoveAll(x => x.Text.Contains(target, StringComparison.OrdinalIgnoreCase));
        }

        Log.Write($"whisper: забанена галлюцинация «{target}»{(ownTurn ? ", ответ на неё отменён" : "")}");
        Bubble($"🚫 Фраза «{target}» забанена! Больше модель не будет слышать эти ложные слова.");
        if (!IsBusy) Post(new { type = "state", value = "idle" });
    }

    /// <summary>Штраф модели за ошибку: последняя реплика была сказана correctSpeaker, а не _lastSpeakerName.</summary>
    public async Task<(bool Ok, string Message)> CorrectLastSpeakerAsync(string correctSpeaker)
    {
        if (_lastUtteranceWav is null) return (false, "Нет недавней фразы для коррекции.");
        var wrong = _lastSpeakerName ?? "";
        // «Штраф» с тем же именем, что уже узнано, — это не ошибка модели, а подтверждение: просто доучиваем
        if (string.Equals(wrong, correctSpeaker, StringComparison.Ordinal)) return await RewardLastSpeakerAsync(correctSpeaker);
        Log.Write($"speaker: коррекция ошибки! Было: «{wrong}», правильный: «{correctSpeaker}»");
        var ok = await _speaker.PenalizeAndCorrectAsync(wrong, correctSpeaker, _lastUtteranceWav);
        if (ok)
        {
            var msg = string.IsNullOrWhiteSpace(wrong)
                ? $"Профиль «{correctSpeaker}» доучен последней фразой!"
                : $"Ошибка исправлена! Оштрафован «{wrong}», доучен «{correctSpeaker}»!";
            Bubble(msg);
            Log.Write($"speaker: {msg}");
            return (true, msg);
        }
        return (false, "Не удалось связаться с сервером speaker_id.");
    }

    /// <summary>Награда модели за правильное распознавание: закрепление эталонного сэмпла в целевой профиль.</summary>
    public async Task<(bool Ok, string Message)> RewardLastSpeakerAsync(string targetSpeaker)
    {
        if (_lastUtteranceWav is null) return (false, "Нет недавней фразы для награды.");
        Log.Write($"speaker: награда модели! Закрепляем голос: «{targetSpeaker}»");
        var ok = await _speaker.EnrollAsync(targetSpeaker, _lastUtteranceWav, append: true);
        if (ok)
        {
            var msg = $"🌟 Награда засчитана! Голос «{targetSpeaker}» закреплён как эталон!";
            Bubble(msg);
            Log.Write($"speaker: {msg}");
            return (true, msg);
        }
        return (false, "Не удалось сохранить награду на сервере speaker_id.");
    }

    public void ClearTempBatch()
    {
        lock (_batchGate) _tempBatch.Clear();
        Bubble("Временный буфер очищен, мур.");
        Post(new { type = "state", value = "idle" });
    }

    /// <summary>
    /// Повод поздороваться при запуске: она проснулась, а не «стояла всю ночь» и не «только что появилась» (друг 01.10:
    /// «вчера спать уложил — встанет и будет ворчать»). Сколько спала, как её выключили и что было прямо перед сном.
    /// </summary>
    private string WakeHint()
    {
        var now = DateTime.Now;
        // выключали на минуты — это перезапуск, а не сон
        if (_memoryState.Previous is { } blink && now - blink.End < TimeSpan.FromMinutes(10))
            return $"(Тебя перезапустили: ты была выключена {Chronicle.Span(now - blink.End)} и снова на связи — ничего не забыла. " +
                   "Скажи об этом одной короткой фразой, по-свойски, без приветствий заново.)";
        var sb = new System.Text.StringBuilder("(Ты проснулась: тебя только что включили на рабочем столе друга.");
        if (_memoryState.Previous is { } prev)
        {
            sb.Append($" Спала ты с {Chronicle.Stamp(prev.End, now)} — {Chronicle.Span(now - prev.End)}");
            sb.Append(prev.Clean ? "." : " (в тот раз тебя выключили внезапно, без прощания).");
            var last = _flightJournal.Transcript(prev.End.Date).Where(u => !u.Hint && u.Time <= prev.End.AddMinutes(1)).TakeLast(4).ToList();
            if (last.Count > 0)
                sb.Append(" Последнее перед сном: " + string.Join(" / ", last.Select(u =>
                    $"{u.Time:HH:mm} {(u.Klava ? "ты" : u.Who)}: «{(u.Text.Length > 110 ? u.Text[..110] + "…" : u.Text)}»")) + ".");
        }
        return sb.Append(" Поздоровайся коротко, по-дружески и по времени суток. Ты спала, а не «стояла всю ночь» и не «только что " +
                         "появилась» — можешь сказать, как спалось, или подхватить то, на чём вчера остановились.)").ToString();
    }

    /// <summary>Каким мозгом сказана последняя реплика: «antigravity/gemini-3.8-flash-high», «local».</summary>
    private string SpokenBy()
    {
        var id = _brain.LastBrainId;
        var p = _cfg.Brains.FirstOrDefault(b => b.Id == id);
        return p is null || p.IsLocal || string.IsNullOrWhiteSpace(p.Model) ? id : $"{id}/{p.Model}";
    }

    /// <summary>Открыть журнал сегодняшнего дня (по часам) в привычной программе.</summary>
    public void OpenTodayJournal()
    {
        var md = _flightJournal.MdPath(DateTime.Now);
        var target = File.Exists(md) ? md : _flightJournal.Dir;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { Bubble($"Журнал не открылся: {ex.Message}"); }
    }

    /// <summary>Разложить журнал по полочкам сейчас (обычно это идёт само, в фоне). Журнал при этом не меняется.</summary>
    public async Task<string?> FileMemoryNowAsync()
    {
        await _memoryBank.ConsolidateAsync();
        var text = $"банк: {_memoryBank.Describe()}; граф: {_memoryGraph.Describe()}";
        Log.Write($"память разложена по кнопке — {text}");
        return text;
    }

    public (int TempCount, int MaxCount, int TotalJournalCount) GetBufferStats()
    {
        lock (_batchGate)
        {
            return (_tempBatch.Count, _cfg.DialogBatchMaxPhrases, JournalCount());
        }
    }

    /// <summary>Окно терминала (создаётся на UI-потоке; если его закрыли насовсем — новое).</summary>
    private TerminalForm Terminal()
    {
        if (InvokeRequired) return (TerminalForm)Invoke(Terminal);
        if (_terminalForm is null || _terminalForm.IsDisposed)
        {
            _terminalForm = new TerminalForm(_shell, _cfg.Abs("workspace"));
            WireTerminal(_terminalForm);
        }
        return _terminalForm;
    }

    /// <summary>Кнопки терминала: «Стоп» на её вкладке гасит и прежнего агента, «➕ Агент» — задача агенту Claude Code.</summary>
    private void WireTerminal(TerminalForm t)
    {
        t.OnStop = () => AgentRunner.Shell.CancelAsync();
        t.StartAgent = async (goal, path) =>
            _brain.Toolbox is { } box && AgentHub.Available(_cfg)
                ? await AgentHub.StartAsync(goal, "", path, _cfg, box, ApproveAsync)
                : "Агенты на Claude Code недоступны (нет claude.exe или выбран прежний агент agentEngine=own).";
    }

    public void ShowTerminalForm() => ShowTerminalForm(activate: true);

    /// <param name="activate">false — показать, не отбирая фокус у друга (когда Клава сама выполняет команду).</param>
    public void ShowTerminalForm(bool activate)
    {
        var t = Terminal();
        if (activate)
        {
            if (!t.Visible) t.Show();
            if (t.WindowState == FormWindowState.Minimized) t.WindowState = FormWindowState.Normal;
            t.BringToFront();
            t.Activate();
        }
        else if (!t.Visible)
        {
            Native.ShowWindow(t.Handle, Native.SW_SHOWNOACTIVATE);
            t.Visible = true;
        }
    }

    private BrainProfilesForm? _brainForm;

    /// <summary>Пересобрать меню «Мозг» под текущий список мозгов.</summary>
    private void RebuildBrainMenu()
    {
        _brainMenu.DropDownItems.Clear();
        foreach (var b in _cfg.Brains)
        {
            var item = new ToolStripMenuItem(b.DisplayName) { Tag = b };
            if (b.ModelsFromServer)
            {
                item.DropDownItems.Add(new ToolStripMenuItem("(загружаю список моделей…)") { Enabled = false });
                item.DropDownOpening += (_, _) => _ = FillModelsAsync(item, b);
            }
            else item.Click += (_, _) => _ = SwitchBrainAsync(b);
            _brainMenu.DropDownItems.Add(item);
        }
        UpdateBrainMenu();
    }

    /// <summary>Открыть редактор мозгов. После сохранения — пересобрать меню «Мозг»; «сделать активным» — переключить.</summary>
    public void ShowBrainProfiles()
    {
        if (_brainForm is { IsDisposed: false }) { Reveal(_brainForm); return; }
        _brainForm = new BrainProfilesForm(_cfg, RebuildBrainMenu, (b, model) => SwitchBrainAsync(b, model, force: true));
        _brainForm.Show(this);
        _brainForm.Activate();
    }

    /// <summary>Меню «Компания в комнате»: сколько человек и кто именно (голосовые профили).</summary>
    private async Task FillPeopleMenuAsync(ToolStripMenuItem menu)
    {
        List<string> profiles = [];
        try { if (await _services.SpeakerAliveAsync()) profiles = (await _speaker.ListAsync()).Select(p => p.Name).Where(n => n != SelfVoiceName).Order().ToList(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        menu.DropDownItems.Clear();
        var count = Math.Clamp(_cfg.RoomPeopleCount, 1, KlavaConfig.MaxRoomPeople);
        string[] titles = ["Соло — только один человек", "Двое", "Трое", "Четверо", "Пятеро", "Шестеро", "Семеро", "Восьмеро"];
        foreach (var (title, n) in titles.Take(KlavaConfig.MaxRoomPeople).Select((t, i) => (t, i + 1)))
        {
            var mi = new ToolStripMenuItem(title) { Checked = count == n };
            mi.Click += (_, _) => SetRoomCount(n);
            menu.DropDownItems.Add(mi);
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        if (profiles.Count == 0)
            menu.DropDownItems.Add(new ToolStripMenuItem("(нет записанных голосов — «Голосовые профили…»)") { Enabled = false });
        else
        {
            menu.DropDownItems.Add(new ToolStripMenuItem($"Кто в компании (выбери {count}):") { Enabled = false });
            foreach (var name in profiles)
            {
                var mi = new ToolStripMenuItem(HumanName(name)) { Checked = _cfg.RoomMembers.Contains(name) };
                mi.Click += (_, _) => ToggleMember(name);
                menu.DropDownItems.Add(mi);
            }
            var all = new ToolStripMenuItem("Слушать всех (без фильтра по голосу)") { Checked = _cfg.RoomMembers.Count == 0 };
            all.Click += (_, _) => { _cfg.RoomMembers.Clear(); _cfg.Save(); Bubble("Слушаю всех подряд, без фильтра по голосу."); };
            menu.DropDownItems.Add(all);
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("🧹 Сбросить временный буфер", null, (_, _) => ClearTempBatch()));
    }

    /// <summary>Сколько человек в компании; лишние выбранные отпадают (остаются последние выбранные).</summary>
    public void SetRoomCount(int n)
    {
        n = Math.Clamp(n, 1, KlavaConfig.MaxRoomPeople);
        _cfg.RoomPeopleCount = n;
        _cfg.DialogBatchMaxPhrases = n;
        while (_cfg.RoomMembers.Count > n) _cfg.RoomMembers.RemoveAt(0);
        _cfg.Save();
        Bubble(n == 1
            ? (_cfg.RoomMembers.Count == 1 ? $"Соло: слушаю только {HumanName(_cfg.RoomMembers[0])}." : "Соло: выбери, кого слушать (Компания → имя).")
            : $"В компании {n}: {(_cfg.RoomMembers.Count > 0 ? string.Join(", ", _cfg.RoomMembers.Select(HumanName)) : "выбери, кого слушать")}.");
    }

    /// <summary>Добавить/убрать человека из компании; в соло — выбор одного (как радиокнопка).</summary>
    public void ToggleMember(string name)
    {
        if (_cfg.RoomMembers.Remove(name)) { _cfg.Save(); Bubble($"{HumanName(name)} больше не в компании."); return; }
        _cfg.RoomMembers.Add(name);
        while (_cfg.RoomMembers.Count > Math.Max(1, _cfg.RoomPeopleCount)) _cfg.RoomMembers.RemoveAt(0);
        _cfg.SpeakerIdEnabled = true;
        _cfg.Save();
        Bubble(_cfg.RoomMembers.Count == 1 ? $"Слушаю только {HumanName(name)}, мур." : $"В компании: {string.Join(", ", _cfg.RoomMembers.Select(HumanName))}.");
        _ = EnsureSpeakerRunningAsync();
    }

    /// <summary>Фильтр компании без сервера голосов не работает — поднять его, если лежит.</summary>
    private async Task EnsureSpeakerRunningAsync()
    {
        if (await _services.SpeakerAliveAsync()) return;
        var ok = await _services.EnsureSpeakerAsync(TimeSpan.FromSeconds(120), new Progress<string>(s => Log.Write($"speaker: {s}")));
        if (ok) await OnSpeakerReadyAsync();
        else Bubble("Сервер голосовых профилей не поднялся — пока слушаю всех.");
    }

    // ---------------- сторожа: микрофон и «поверх всех» ----------------

    /// <summary>
    /// Микрофон замолчал: нет данных от драйвера или ровно ноль (цифровая тишина — USB-микрофон отвалился, сменилось
    /// устройство по умолчанию). Переподключаем: 3 раза через 30 с, дальше раз в 5 минут. Живую тишину комнаты
    /// (шум ~0,001) это не трогает — ноль бывает только у мёртвого потока или выключенного микрофона.
    /// </summary>
    private void CheckMicrophone()
    {
        if (!_ready || _ears.PttRecording) return;
        var (noData, zero) = _ears.Health();
        // У микрофона с шумодавом тишина в комнате — это ровный ноль. Такой ноль не поломка: переподключать нечего.
        var dead = !_ears.IsOpen || noData > TimeSpan.FromSeconds(5) || (!_micGated && zero > TimeSpan.FromSeconds(20));
        if (!dead)
        {
            if (_micRestarts > 0 && zero < TimeSpan.FromSeconds(5))
            {
                // звук вернулся сам, а не сразу после переподключения — значит, это была просто тишина за шумодавом
                if (_micZeroRestart && DateTime.UtcNow - _lastMicRestart > TimeSpan.FromSeconds(10))
                {
                    _micGated = true;
                    Log.Write("микрофон: у него шумодав — ровный ноль в тишине для него норма, больше не переподключаю из-за тишины");
                }
                else Log.Write("микрофон: снова слышу звук");
                _micRestarts = 0;
            }
            return;
        }
        var pause = _micRestarts < 3 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(5);
        if (DateTime.UtcNow - _lastMicRestart < pause) return;
        _lastMicRestart = DateTime.UtcNow;
        _micRestarts++;
        var why = !_ears.IsOpen ? "закрыт" : noData > TimeSpan.FromSeconds(5) ? $"нет данных {noData.TotalSeconds:F0} с" : $"ровный ноль {zero.TotalSeconds:F0} с";
        _micZeroRestart = _ears.IsOpen && noData <= TimeSpan.FromSeconds(5);
        Log.Write($"микрофон: {why} — переподключаю (попытка {_micRestarts})");
        try { _ears.RestartMicrophone(); }
        catch (Exception ex)
        {
            Log.Error("микрофон: переподключение", ex);
            if (_cfg.MicDevice != -1)
            {
                Log.Write("микрофон: пробую системный по умолчанию");
                try { _ears.SwitchDevice(-1); } catch (Exception ex2) { Log.Error("микрофон по умолчанию", ex2); }
            }
        }
        if (_micRestarts == 1) Bubble("Микрофон затих — переподключила.");
        if (_micRestarts == 2)
            Bubble("С микрофона идёт полная тишина прямо с устройства — глянь кнопку mute на самом микрофоне или провод. Пока могу по рации (правый Ctrl) или текстом (Ctrl+Alt+M).");
    }

    /// <summary>
    /// Известные игры: шутеры, мультиплеер и лаунчеры (CS2, CS:GO, CS 1.6, Point Blank, Dota 2 и др.).
    /// </summary>
    private static readonly HashSet<string> KnownGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "cs2", "csgo", "hl", "cstrike", "PointBlank", "dota2", "javaw", "minecraft",
        "hl2", "left4dead2", "portal2", "rust", "pubg", "tslgame", "overwatch",
        "r5apex", "valorant-win64-shipping", "gta5", "eurotrucks2", "steam", "obs64"
    };

    private static bool IsGameProcess(uint pid)
    {
        if (pid == 0) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return KnownGames.Contains(p.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return false; }
    }

    /// <summary>
    /// Игра, где Клава участвует или держится как игростример-оверлей (CS2, CS 1.6, Point Blank и др.).
    /// </summary>
    private bool CompanionGame(uint pid)
    {
        if (_cfg.OverFullscreen || _cfg.GameStreamerMode || CsBridge.Connected || ZumaBot.Playing || TetrisBot.Playing || GamePlayer.Playing) return true;
        return RealGame(pid);
    }

    private uint _fgPid;
    private bool _fgPidIsGame;

    /// <summary>
    /// Впереди настоящая игра (hl, cs2, javaw…). Для сквозного проклика «поверх всех» (CompanionGame) не годится: с ним
    /// игрой считались и браузер, и проводник — кошка становилась сквозной над всем, ни тронуть, ни меню открыть.
    /// Ответ кэшируется по pid: таймер спрашивает это на каждом кадре, а GetProcessById перебирает все процессы.
    /// </summary>
    private bool RealGame(uint pid)
    {
        if (pid == 0 || pid == Environment.ProcessId) return false;
        if (pid != _fgPid)
        {
            _fgPid = pid;
            _fgPidIsGame = IsGameProcess(pid);
        }
        return _fgPidIsGame;
    }

    /// <summary>Сквозной проклик — только пока впереди игра; на столе, в браузере и в своём меню кошку снова можно трогать.</summary>
    private void SyncClickThrough(IntPtr fg)
    {
        Native.GetWindowThreadProcessId(fg, out var pid);
        ApplyGameOverlayStyle(_cfg.GameClickThrough && RealGame(pid));
    }

    /// <summary>Динамическое переключение прозрачности окна для мыши на основе маски силуэта.</summary>
    private void SetWindowTransparent(bool transparent)
    {
        if (IsDisposed) return;
        try
        {
            var ex = (int)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
            var desired = transparent ? (ex | Native.WS_EX_TRANSPARENT) : (ex & ~Native.WS_EX_TRANSPARENT);
            if (desired != ex)
            {
                Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, (IntPtr)desired);
            }
        }
        catch (Exception ex) { Log.Error("SetWindowTransparent", ex); }
    }

    /// <summary>Сквозной проклик мыши в игре: клики летят прямо в игру, прицел и стрельба не сбиваются.</summary>
    private void ApplyGameOverlayStyle(bool transparent)
    {
        if (_isClickThroughActive == transparent || IsDisposed) return;
        _isClickThroughActive = transparent;
        try
        {
            var ex = (int)Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
            if (transparent) ex |= Native.WS_EX_TRANSPARENT;
            else ex &= ~Native.WS_EX_TRANSPARENT;
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, (IntPtr)ex);
            Log.Write(transparent ? "окно: включен сквозной проклик мыши в игру (WS_EX_TRANSPARENT)" : "окно: сквозной проклик выключен");
        }
        catch (Exception ex) { Log.Error("ApplyGameOverlayStyle", ex); }
    }

    public void ToggleGameOverlayMode()
    {
        _cfg.GameStreamerMode = !_cfg.GameStreamerMode;
        _cfg.Save();
        SyncClickThrough(Native.GetForegroundWindow());
        BringToTopImmediate();
        Bubble(_cfg.GameStreamerMode
            ? "🎮 Режим игростримера ВКЛ! Держусь поверх CS2/игр, мышь летит сквозь меня прямо в катку!"
            : "Режим игростримера выключен — обычный режим на рабочем столе.");
    }

    private void BringToTopImmediate()
    {
        if (IsDisposed || _menu.Visible) return;
        Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        ControlPanelHost.KeepAbove();
    }

    /// <summary>Окно на весь монитор (игра, видео на весь экран) — тогда не лезем поверх.</summary>
    private static bool IsFullscreen(IntPtr h)
    {
        if (h == IntPtr.Zero || !Native.GetWindowRect(h, out var r)) return false;
        var cls = new System.Text.StringBuilder(64);
        Native.GetClassNameW(h, cls, cls.Capacity);
        if (cls.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd") return false;
        var scr = Screen.FromHandle(h).Bounds;
        return r.Left <= scr.Left && r.Top <= scr.Top && r.Right >= scr.Right && r.Bottom >= scr.Bottom;
    }

    /// <summary>
    /// «Поверх всех» держится само: после Win+D, свёрнутых окон, чужих topmost-окон и смены разрешения Клава
    /// проваливалась под окна или уезжала за край.
    /// </summary>
    private void KeepOnTop()
    {
        // меню открыто — не лезть над ним: и меню, и Клава «поверх всех», и кто последний поднялся, тот сверху
        if (IsDisposed || !_pageReady || _dragging || _mouseDown || _menu.Visible) return;
        if (!Native.IsWindowVisible(Handle) || Native.IsIconic(Handle))
        {
            Log.Write("окно: пропала со стола — возвращаю");
            Native.ShowWindow(Handle, Native.SW_SHOWNOACTIVATE);
            Visible = true;
        }
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out var pid);
        SyncClickThrough(fg);

        // Если не игра и не оверлей, но чужое полноэкранное окно (напр. видео) — не лезем поверх
        if (pid != Environment.ProcessId && IsFullscreen(fg) && !CompanionGame(pid)) return;

        // Обязательно SWP_SHOWWINDOW, чтобы DWM не схлопывал DirectComposition слой под игру
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        ControlPanelHost.KeepAbove();   // открытая панель настроек — над Клавой, иначе силуэт закрывает кнопки
        ClampToScreen();
    }

    /// <summary>Сменилось активное окно: если это рабочий стол (Win+D), панель задач или игра — сразу вернуть Клаву наверх.</summary>
    private void OnForegroundChanged(IntPtr hwnd)
    {
        // клик по значку в трее делает активной панель задач — поднимать Клаву тут нельзя, иначе она накрывает меню
        if (IsDisposed || _menu.Visible) return;
        var cls = new System.Text.StringBuilder(64);
        Native.GetClassNameW(hwnd, cls, cls.Capacity);
        Native.GetWindowThreadProcessId(hwnd, out var fgPid);

        var isDesktop = cls.ToString() is "WorkerW" or "Progman" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
        var isGame = fgPid != Environment.ProcessId && CompanionGame(fgPid);
        SyncClickThrough(hwnd);

        if (isDesktop || isGame)
        {
            BringToTopImmediate();
            if (isGame)
            {
                // Игра при активации окна (через 60-200 мс) перехватывает Z-order при рендере кадра.
                // Поднимаем кошку повторно после инициализации SwapChain игры:
                Task.Delay(80).ContinueWith(_ => { if (!IsDisposed) BeginInvoke(BringToTopImmediate); });
                Task.Delay(250).ContinueWith(_ => { if (!IsDisposed) BeginInvoke(BringToTopImmediate); });
            }
        }
    }

    /// <summary>Не дать окну уехать за край экрана или под панель задач (ноги — на рабочей области).</summary>
    private void ClampToScreen()
    {
        var wa = Screen.FromPoint(new Point(Left + Width / 2, Top + Height / 2)).WorkingArea;
        var x = Math.Clamp(Left, wa.Left - Width / 3, wa.Right - Width * 2 / 3);
        var y = Math.Clamp(Top, wa.Top - Height / 3, wa.Bottom - Height);
        if (x != Left || y != Top)
        {
            Log.Write($"окно: было за краем ({Left},{Top}) — вернула на экран ({x},{y})");
            Location = new Point(x, y);
        }
    }

    /// <summary>
    /// «Подвинься»: лапкам надо кликнуть туда, где стоит сама Клава, — на время клика окно становится пустым
    /// (без региона, мышь проходит насквозь), потом силуэт возвращается.
    /// </summary>
    private IDisposable? MakeWay(Rectangle target)
    {
        if (IsDisposed) return null;
        var mine = RectangleToScreen(ClientRectangle);
        if (!mine.IntersectsWith(target)) return null;
        void Hide()
        {
            if (_clickThrough++ == 0) SetWindowTransparent(true);
        }
        if (InvokeRequired) Invoke(Hide); else Hide();
        return new Restorer(this);
    }

    private sealed class Restorer(OverlayForm f) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1 || f.IsDisposed) return;
            void Back()
            {
                if (--f._clickThrough > 0) return;
                f.SetWindowTransparent(!f._cursorOverFigure);
            }
            try { if (f.InvokeRequired) f.BeginInvoke(Back); else Back(); }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>Открыть панель голосовых профилей — если сервер speaker_id ещё не запущен, поднять его сначала.</summary>
    private async Task ShowVoiceProfilesAsync()
    {
        if (_voicesForm is { IsDisposed: false }) { Reveal(_voicesForm); return; }
        // Поднять speaker_id.py если не запущен — заодно включает флаг в конфиге, чтобы стартовал автоматом дальше.
        if (!await _services.SpeakerAliveAsync())
        {
            Bubble("Поднимаю сервер голосовых профилей…");
            var ok = await _services.EnsureSpeakerAsync(TimeSpan.FromSeconds(60), new Progress<string>(s => Log.Write($"speaker: {s}")));
            if (!ok) { Bubble("Не смог поднять сервер профилей — глянь python-server-speaker_id.log."); return; }
            _cfg.SpeakerIdEnabled = true; _cfg.Save();
            _ = EnsureSelfVoiceAsync();
        }
        _voicesForm = new VoiceProfilesForm(_cfg, _speaker)
        {
            // пока в окне профилей идёт запись, Клава не слушает: иначе отвечала бы на то, что наговаривают в профиль
            PauseListening = on => BeginInvoke(() => { _profilesRecording = on; UpdateDeaf(); }),
            // слух обучен / профили переписаны — пороги «свой» берём новые, без перезапуска
            ProfilesChanged = () => BeginInvoke(() => _ = RefreshSpeakerInfoAsync()),
        };
        _voicesForm.Show(this);
        _voicesForm.Activate();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_web is not null) _web.Bounds = new Rectangle(Point.Empty, ClientSize);
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        _web?.NotifyParentWindowPositionChanged();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // память — первой: дальше идёт долгая уборка (серверы, агенты, окна), и если процесс снимут посреди неё,
        // журнал, сеанс и граф уже на диске (30.09 граф писался последним и остался обрезанным)
        _flightJournal.LogMark("выход");
        _memoryState.End();
        _memoryGraph.SaveNow();
        _turn?.Cancel();
        _tick.Stop();
        _watch.Stop();
        _batchTimer.Stop();
        _batchTimer.Dispose();
        if (_winEventHook != IntPtr.Zero) { Native.UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
        Native.UnregisterHotKey(Handle, HotkeyText);
        Native.UnregisterHotKey(Handle, HotkeyGameOverlay);
        Native.UnregisterHotKey(Handle, HotkeyPanel);
        ControlPanelHost.Close();
        _tray.Visible = false;
        _tray.Dispose();
        _ears.Dispose();
        _speakerTap?.Dispose();
        _whisperLearn.Dispose();   // остановить фоновый learn-loop, сохранить corrections
        Later.Stop();              // отложенные дела лежат в data\later.json — продолжатся после запуска
        _hands.Dispose();      // снять хуки, спрятать лапку
        _shell.Dispose();      // закрыть живой pwsh, если был открыт в этом разговоре
        CsBridge.Stop();
        GameRecorder.Stop();
        GamePlayer.Stop();
        AgentHub.StopAll();    // агенты Claude Code — вместе с ней
        AgentRunner.Stop();    // фоновый агент и его pwsh, фоновые задания (серверы, обучение) — вместе с ней
        AgentRunner.Shell.Dispose();
        Tools.StopAllJobs();
        _terminalForm?.Dispose(); // закрыть окно кибер-терминала
        _brain.Dispose();      // процесс Claude Code, если был
        _memoryGraph.Stop();   // векторизатор памяти и последняя запись графа
        _services.StopAll();   // мозг, слух и голос, которые она подняла сама
        _web?.Close();
        _dcomp?.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>Иконка трея рисуется кодом: голубая кошачья мордочка, никаких внешних файлов.</summary>
    private static Icon MakeTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var fill = new SolidBrush(Color.FromArgb(120, 170, 255));
            g.FillPolygon(fill, new[] { new Point(4, 14), new Point(7, 2), new Point(13, 9) });
            g.FillPolygon(fill, new[] { new Point(28, 14), new Point(25, 2), new Point(19, 9) });
            g.FillEllipse(fill, 3, 7, 26, 23);
            using var eye = new SolidBrush(Color.FromArgb(40, 30, 70));
            g.FillEllipse(eye, 10, 15, 4, 5);
            g.FillEllipse(eye, 18, 15, 4, 5);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
