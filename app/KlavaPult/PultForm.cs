using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace KlavaPult;

/// <summary>
/// Пульт Клавы. Слева разделы, справа карточки, снизу вывод команд. Живой Клавой рулит её же командами
/// (KlavaDesk.exe --say / --quit / --mute / --brains / --audio / --terminal), выключенной — через --set.
/// Скрипты (сборка, пакет) идут сюда же, в вывод; пуш по токену — в своём окне PowerShell (там спрашивается токен).
/// </summary>
internal sealed class PultForm : Form
{
    private KlavaHome? _home;
    private readonly Panel _side = new() { Dock = DockStyle.Left, Width = 236, BackColor = Theme.Side };
    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg };
    private readonly Panel _consoleHost = new() { Dock = DockStyle.Bottom, Height = 220, BackColor = Theme.Console, Padding = new Padding(14, 0, 14, 10) };
    private readonly RichTextBox _console = new()
    {
        Dock = DockStyle.Fill, BackColor = Theme.Console, ForeColor = Theme.Dim, BorderStyle = BorderStyle.None, ReadOnly = true,
        Font = Theme.Mono, DetectUrls = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical,
    };
    private readonly List<(NavButton Nav, Page Page)> _pages = [];
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 3000 };
    private readonly System.Windows.Forms.Timer _follow = new() { Interval = 1000 };
    private long _followPos = -1;
    private bool _statusBusy;
    private int _running;   // сколько команд сейчас идёт в выводе

    // главная
    private readonly TextBlock _heroState = new("…", new Font("Segoe UI Semibold", 16f), Theme.Text);
    private readonly TextBlock _heroInfo = new("");
    private readonly StatusGrid _services = new();
    private readonly TextBlock _brainNow = new("", Theme.Body, Theme.Text);
    private readonly TextBlock _chain = new("");
    private PillButton _start = null!, _restart = null!, _stop = null!, _muteOn = null!, _muteOff = null!, _brainsWin = null!, _terminal = null!;

    // мозги
    private readonly Flow _brainList = new();
    private readonly QuotaView _quota = new() { Empty = "Нет файлов аккаунтов Antigravity Tools (~/.antigravity_tools/accounts)." };
    private Card _quotaCard = null!;

    // команды
    private readonly SayRow _say = new();

    private PillButton _followBtn = null!;

    private readonly string? _shotDir;

    public PultForm(string? shotDir = null)
    {
        _shotDir = shotDir;
        Text = "Пульт Клавы";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1220, 860);
        MinimumSize = new Size(940, 640);
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        DoubleBuffered = true;

        _home = KlavaHome.Find();
        BuildConsole();
        BuildSide();
        Controls.Add(_content);
        Controls.Add(_consoleHost);
        Controls.Add(_side);

        AddPage("🏠", "Главная", BuildHome());
        AddPage("🧠", "Мозги", BuildBrains());
        AddPage("💬", "Команды", BuildCommands());
        AddPage("🛠", "Сервис", BuildService());
        AddPage("📦", "GitHub", BuildGitHub());
        Show(0);

        _tick.Tick += async (_, _) => await RefreshStatusAsync();
        _follow.Tick += (_, _) => FollowLog();
        Shown += async (_, _) =>
        {
            Theme.DarkTitle(this);
            if (_home is null) AskRoot();
            else Out($"Клава: {_home.Root}", Theme.Accent);
            await RefreshStatusAsync();
            RefreshBrains();
            if (_shotDir is not null) { await ShootAsync(_shotDir); return; }
            _tick.Start();
        };
        FormClosed += (_, _) => { _tick.Stop(); _follow.Stop(); };
    }

    // ================================================================ каркас

    private void BuildSide()
    {
        var logo = new TextBlock("🐾 Клава", new Font("Segoe UI Semibold", 18f), Theme.Text) { Location = new Point(22, 22), Size = new Size(200, 40) };
        var sub = new TextBlock("пульт управления", Theme.Small, Theme.Pink) { Location = new Point(25, 60), Size = new Size(200, 22) };
        _side.Controls.Add(logo);
        _side.Controls.Add(sub);
        var foot = new TextBlock("Мозг отвечает всегда:\nAG → соседняя модель AG →\nDeepSeek → Жминя → Codex →\nлокалка", Theme.Small)
        { Dock = DockStyle.Bottom, Height = 86, Padding = new Padding(22, 0, 8, 0) };
        _side.Controls.Add(foot);
        logo.BackColor = sub.BackColor = foot.BackColor = Theme.Side;
    }

    private void AddPage(string icon, string title, Page page)
    {
        var nav = new NavButton(icon, title) { Width = _side.Width, Location = new Point(0, 104 + _pages.Count * 50) };
        var index = _pages.Count;
        nav.Click += (_, _) => Show(index);
        _side.Controls.Add(nav);
        page.Visible = false;
        _content.Controls.Add(page);
        _pages.Add((nav, page));
    }

    private void Show(int index)
    {
        for (var i = 0; i < _pages.Count; i++)
        {
            _pages[i].Nav.Selected = i == index;
            _pages[i].Page.Visible = i == index;
        }
        _pages[index].Page.Relayout();
        if (_pages[index].Page.Title == "Мозги") { RefreshBrains(); _ = RefreshClaudeAsync(); }
    }

    private void BuildConsole()
    {
        var head = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Theme.Console };
        var title = new TextBlock("Вывод", Theme.Bold, Theme.Text) { Location = new Point(0, 12), Size = new Size(80, 22), BackColor = Theme.Console };
        var clear = new PillButton("Очистить") { Size = new Size(104, 32) };
        clear.Click += (_, _) => _console.Clear();
        var hide = new PillButton("▾ Свернуть") { Size = new Size(116, 32) };
        hide.Click += (_, _) =>
        {
            var collapsed = _consoleHost.Height < 100;
            _consoleHost.Height = collapsed ? 220 : 50;
            hide.Text = collapsed ? "▾ Свернуть" : "▴ Развернуть";
            hide.Invalidate();
        };
        head.Controls.Add(title);
        head.Controls.Add(clear);
        head.Controls.Add(hide);
        head.Resize += (_, _) =>
        {
            hide.Location = new Point(head.Width - hide.Width, 6);
            clear.Location = new Point(hide.Left - clear.Width - 8, 6);
        };
        clear.BackColor = hide.BackColor = Theme.Console;
        _consoleHost.Controls.Add(_console);
        _consoleHost.Controls.Add(head);
        var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Line };
        _consoleHost.Controls.Add(line);
    }

    /// <summary>Строка в вывод (с любого потока).</summary>
    private void Out(string text, Color? color = null)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Out(text, color)); return; }
        if (_console.TextLength > 400_000) { _console.Select(0, 100_000); _console.SelectedText = ""; }
        _console.SelectionStart = _console.TextLength;
        _console.SelectionLength = 0;
        _console.SelectionColor = color ?? Theme.Dim;
        _console.AppendText(text + "\n");
        _console.SelectionColor = _console.ForeColor;
        _console.ScrollToCaret();
    }

    private static Card CardWith(string title, string note, Control body)
    {
        var c = new Card(title, note);
        c.SetBody(body);
        return c;
    }

    private static PillButton Btn(string text, Action onClick, Color? accent = null, bool primary = false)
    {
        var b = new PillButton(text, accent, primary);
        b.Click += (_, _) => onClick();
        return b;
    }

    // ================================================================ страницы

    private Page BuildHome()
    {
        var page = new Page("Главная", "Клава, её службы и мозг — одним взглядом. Обновляется само каждые 3 секунды.");

        var hero = new Flow();
        hero.Controls.Add(Flow.Full(_heroState));
        hero.Controls.Add(Flow.Full(_heroInfo));
        _start = Btn("▶  Запустить", StartKlava, Theme.Green, primary: true);
        _restart = Btn("⟳  Перезапустить", () => _ = RestartKlavaAsync());
        _stop = Btn("⏻  Выключить", () => _ = StopKlavaAsync(), Theme.Red);
        _muteOn = Btn("🔇  Без звука", () => _ = DeskAsync("без звука", "--mute", "on"));
        _muteOff = Btn("🔊  Со звуком", () => _ = DeskAsync("со звуком", "--mute", "off"));
        foreach (var b in new[] { _start, _restart, _stop, _muteOn, _muteOff }) hero.Controls.Add(b);
        page.Add(CardWith("Клава", "", hero));

        var svc = new Flow();
        svc.Controls.Add(Flow.Full(_services));
        page.Add(CardWith("Службы", "зелёный — работает, жёлтый — стоит проверить, серый — выключено или не нужно сейчас", svc));

        var brain = new Flow();
        brain.Controls.Add(Flow.Full(_brainNow));
        brain.Controls.Add(Flow.Full(_chain));
        brain.Controls.Add(Btn("🧠  Мозги и квоты", () => Show(1)));
        brain.Controls.Add(Btn("🔌  Проверить связь", () => _ = CheckLinksAsync()));
        page.Add(CardWith("Мозг сейчас", "", brain));

        var wins = new Flow();
        _brainsWin = Btn("🧠  Окно мозгов", () => _ = DeskAsync("окно мозгов", "--brains"));
        _terminal = Btn("💻  Терминал Клавы", () => _ = DeskAsync("терминал", "--terminal"));
        wins.Controls.Add(_brainsWin);
        wins.Controls.Add(Btn("🎧  Микрофон и голос", OpenAudio));
        wins.Controls.Add(_terminal);
        wins.Controls.Add(Btn("📁  Папка Клавы", () => OpenFolder(_home?.Root)));
        page.Add(CardWith("Окна Клавы", "окно мозгов и терминал открываются у запущенной Клавы; микрофон — и у выключенной", wins));
        return page;
    }

    private Page BuildBrains()
    {
        var page = new Page("Мозги", "Кто думает за Клаву и кто подхватит, если он лёг. Квоты Antigravity Tools — по аккаунтам.");
        page.Add(CardWith("Мозги из klava.json",
            "у живой Клавы смена — фразой «переключись на …», у выключенной — правкой klava.json (KlavaDesk --set)", _brainList));

        var q = new Flow();
        q.Controls.Add(Flow.Full(_quota));
        q.Controls.Add(Btn("⟳  Обновить квоты", RefreshQuotas));
        q.Controls.Add(Btn("🔌  Проверить связь", () => _ = CheckLinksAsync()));
        q.Controls.Add(Btn("🪐  Запустить Antigravity Tools", StartAg));
        _quotaCard = CardWith("Квоты Antigravity Tools",
            "⚠ одна — модель есть только на одном аккаунте: её запирает circuit breaker вместе с аккаунтом, Клава тогда пересаживается сама", q);
        page.Add(_quotaCard);
        return page;
    }

    private Page BuildCommands()
    {
        var page = new Page("Команды", "Всё, что можно сказать голосом, — можно отсюда текстом. Клава слышит это как реплику друга.");

        var say = new Flow();
        say.Controls.Add(Flow.Full(_say));
        _say.Send.Click += (_, _) => SendSay();
        _say.Input.Box.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            SendSay();
        };
        page.Add(CardWith("Сказать Клаве", "Enter — отправить", say));

        var quick = new Flow();
        void Group(string caption, params (string Label, string Phrase)[] items)
        {
            quick.Controls.Add(Flow.Full(new TextBlock(caption, Theme.Bold, Theme.Pink)));
            foreach (var (label, phrase) in items)
            {
                var b = new PillButton(label) { Height = 34 };
                b.Click += (_, _) => _ = SayAsync(phrase);
                quick.Controls.Add(b);
            }
        }
        Group("Режим работы",
            ("🛠 Работа", "режим работы"), ("💬 Общение", "режим общения"), ("🤖 Авто", "авторежим"));
        Group("Характер",
            ("🔥 С матом", "давай режим с матом"), ("🎀 Без мата", "режим без мата"), ("🙂 Умеренный", "умеренный режим"),
            ("🧐 Рассудительный", "включи рассудительный режим"), ("↺ Обычный", "верни обычный режим"));
        Group("Мозг",
            ("🪐 Антигравити", "переключись на антигравити"), ("🐳 Дипсик", "переключись на дипсик"), ("🧠 ChatGPT (Codex)", "переключись на кодекс"),
            ("🔵 Жминя", "переключись на жминю"), ("🏠 Свой мозг", "переключись на свой мозг"));
        Group("Дела",
            ("👀 Глянь на экран", "глянь на экран"), ("📓 Наблюдай", "наблюдай за экраном"), ("🎵 Послушай, что играет", "послушай, что играет"),
            ("🎤 Спой", "спой что-нибудь"), ("🎨 Нарисуй картину", "нарисуй картину в пейнте"), ("🪞 Как ты выглядишь?", "как ты выглядишь?"),
            ("👘 Кимоно", "переоденься в кимоно"), ("🛏 Иди спать", "иди спать"), ("🐱 Кыс-кыс", "кыс-кыс"));
        page.Add(CardWith("Быстрые команды", "кнопка = фраза Клаве; работает, когда она запущена", quick));

        var cheat = new Flow();
        cheat.Controls.Add(Flow.Full(new TextBlock(
            "• «переключись на дипсик / антигравити / кодекс / жминю / свой мозг» — сменить мозг на ходу\n" +
            "• «режим работы / общения / авторежим» — с руками на компьютере, просто болтовня или само\n" +
            "• «режим без мата», «умеренный режим», «рассудительный режим», «режим с матом», «обычный режим»\n" +
            "• «глянь на экран», «наблюдай» и потом «что было, пока я спал» — отчёт по журналу экрана\n" +
            "• «послушай, что играет», «спой …», «нарисуй … в пейнте», «переоденься в …», «как ты выглядишь?»\n" +
            "• «переключи агента на джемини / опус» — модель агента; «стоп агент» — остановить агента\n" +
            "• «кыс-кыс» — замолчать посреди фразы; погладить — поводить курсором по голове", Theme.Body, Theme.Text)));
        page.Add(CardWith("Шпаргалка голосом", "из README — то, что она понимает словами", cheat));
        return page;
    }

    private Page BuildService()
    {
        var page = new Page("Сервис", "Сборка, проверки, журнал и папки. Вывод — внизу.");
        var build = new Flow();
        build.Controls.Add(Btn("🔨  Собрать Клаву (с проверками)", () => _ = RunScriptAsync("сборка", "build.ps1", "-Test"), Theme.Accent, primary: true));
        build.Controls.Add(Btn("✅  Самопроверка", () => _ = RunDeskOutAsync("самопроверка", "--selftest")));
        build.Controls.Add(Btn("⚡  Автонастройка мозгов", () => _ = RunDeskOutAsync("автонастройка", "--autosetup")));
        page.Add(CardWith("Сборка и проверки",
            "сборка закрывает запущенную Клаву по-хорошему (память пишется), собирает Klava.sln и запускает её снова", build));

        var log = new Flow();
        log.Controls.Add(Btn("📜  Журнал: последние строки", ShowLogTail));
        _followBtn = Btn("👁  Следить за журналом", ToggleFollow);
        log.Controls.Add(_followBtn);
        log.Controls.Add(Btn("📁  Папка журналов", () => OpenFolder(_home is null ? null : Path.GetDirectoryName(_home.LogFile))));
        page.Add(CardWith("Журнал Клавы", "data\\logs\\klava.log — что она слышит, кто ей отвечает, кто из мозгов отдыхает и почему", log));

        var folders = new Flow();
        var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var kitchen = Path.Combine(desk, "ИИ кошкодевочка");
        folders.Controls.Add(Btn("📁  Клава", () => OpenFolder(_home?.Root)));
        folders.Controls.Add(Btn("🖼  Картины Клавы", () => OpenFolder(Path.Combine(kitchen, "Картины Клавы"))));
        folders.Controls.Add(Btn("🎤  Каверы Клавы", () => OpenFolder(Path.Combine(kitchen, "Каверы Клавы"))));
        folders.Controls.Add(Btn("🐾  ИИ кошкодевочка", () => OpenFolder(kitchen)));
        folders.Controls.Add(Btn("⚙  klava.json", () => OpenFile(_home?.ConfigFile)));
        page.Add(CardWith("Папки", "", folders));
        return page;
    }

    private Page BuildGitHub()
    {
        var page = new Page("GitHub", "Чистая копия проекта без моделей и личного — для репозитория и друзей.");
        var dest = GitHubDest();
        var pack = new Flow();
        pack.Controls.Add(Flow.Full(new TextBlock($"Папка пакета: {dest}", Theme.Body, Theme.Text)));
        pack.Controls.Add(Btn("📦  Собрать пакет", () => _ = RunScriptAsync("пакет", "pack_github.ps1", "-Dest", dest), Theme.Accent, primary: true));
        pack.Controls.Add(Btn("🎁  Пакет + архив друзьям", () => _ = RunScriptAsync("пакет и архив", "pack_github.ps1", "-Dest", dest, "-Zip")));
        pack.Controls.Add(Btn("🚀  Запушить по токену", () => PushByToken(dest), Theme.Pink, primary: true));
        pack.Controls.Add(Btn("📁  Открыть пакет", () => OpenFolder(dest)));
        page.Add(CardWith("Пакет и пуш",
            "пуш спросит токен GitHub (скрытый ввод) и имя или ссылку репозитория; токен нигде не сохраняется", pack));
        var outside = new Flow();
        outside.Controls.Add(Flow.Full(new TextBlock(
            "модели и Python-окружения (их качает УСТАНОВИТЬ.bat), сборки (bin, editor_bin, pult_bin), личное: data, klava.json, " +
            "свой облик web\\config.json (вместо него config.default.json), ключи — их в файлах проекта нет вообще.", Theme.Body)));
        page.Add(CardWith("Что в пакет не попадает", "", outside));
        return page;
    }

    private static string GitHubDest()
    {
        var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var kitchen = Path.Combine(desk, "ИИ кошкодевочка");
        return Path.Combine(Directory.Exists(kitchen) ? kitchen : desk, "Klava-GitHub");
    }

    // ================================================================ статус

    private async Task RefreshStatusAsync()
    {
        if (_statusBusy || _home is null || IsDisposed) return;
        _statusBusy = true;
        try
        {
            var home = _home;
            var cfg = home.Config();
            var proc = home.Running();
            var running = proc is not null;
            string uptime = "";
            if (proc is not null)
            {
                try { var t = DateTime.Now - proc.StartTime; uptime = t.TotalHours >= 1 ? $"{(int)t.TotalHours} ч {t.Minutes} мин" : $"{t.Minutes} мин"; }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                uptime = $"PID {proc.Id}" + (uptime.Length > 0 ? $", работает {uptime}" : "");
                proc.Dispose();
            }

            int? P(string key) => KlavaHome.PortOf(KlavaHome.Str(cfg?[key]));
            int? embed = cfg?["embedPort"] is JsonValue ev && ev.TryGetValue<int>(out var ep) ? ep : null;
            var brainId = KlavaHome.Str(cfg?["brain"]) ?? "?";
            var speech = KlavaHome.Str(cfg?["speechBackend"]) ?? "auto";
            var agPort = KlavaHome.AgPort();
            var checks = new (string Name, int? Port)[]
            {
                ("Antigravity Tools", agPort), ("Свой мозг", P("llmUrl")), ("Голос", P("ttsUrl")), ("Слух", P("sttUrl")),
                ("Кто говорит", P("speakerIdUrl")), ("Память (векторы)", embed), ("Музыка", P("musicUrl")),
            };
            var open = await Task.WhenAll(checks.Select(c => c.Port is int p ? KlavaHome.PortOpenAsync(p) : Task.FromResult(false)));
            if (IsDisposed) return;

            var rows = new List<StatusGrid.Row>();
            for (var i = 0; i < checks.Length; i++)
            {
                var (name, port) = checks[i];
                var up = open[i];
                var where = port is int pp ? $":{pp}" : "";
                Health h;
                string detail;
                if (up) { h = Health.Ok; detail = $"слушает {where}"; }
                else if (name == "Antigravity Tools")
                {
                    h = brainId == "antigravity" ? Health.Bad : Health.Warn;
                    detail = KlavaHome.AgExe() is null ? "не установлен" : $"не запущен {where} — Клава поднимет сама";
                }
                else if (!running) { h = Health.Off; detail = "Клава выключена"; }
                else if (name == "Свой мозг") { h = brainId == "local" ? Health.Warn : Health.Off; detail = brainId == "local" ? "поднимается или упал" : "не нужен — мозг облачный"; }
                else if (name is "Голос" or "Слух" && speech != "lite") { h = Health.Warn; detail = $"не слушает {where} (или лёгкий комплект)"; }
                else { h = Health.Off; detail = name == "Музыка" ? "по требованию" : "не запущен"; }
                rows.Add(new StatusGrid.Row(name, h, detail));
            }
            rows.Add(new StatusGrid.Row("Ключ DeepSeek", KlavaHome.HasKey("DEEPSEEK_API_KEY") ? Health.Ok : Health.Off,
                KlavaHome.HasKey("DEEPSEEK_API_KEY") ? "есть (окружение или ~/.dsh/.env)" : "нет — запасной DeepSeek пропустится"));
            rows.Add(new StatusGrid.Row("Ключ Жмини", KlavaHome.HasKey("GEMINI_API_KEY") ? Health.Ok : Health.Off,
                KlavaHome.HasKey("GEMINI_API_KEY") ? "есть" : "нет"));
            var cx = KlavaHome.CodexExe();
            rows.Add(new StatusGrid.Row("ChatGPT (Codex)", cx is null ? Health.Off : KlavaHome.CodexLoggedIn() ? Health.Ok : Health.Warn,
                cx is null ? "не установлен" : KlavaHome.CodexLoggedIn() ? "вход ChatGPT есть" : "нужен codex login"));
            _services.SetRows(rows);

            _heroState.Text = running ? "● В сети" : "● Выключена";
            _heroState.Color = running ? Theme.Green : Theme.Dim;
            _heroInfo.Text = running
                ? $"{uptime}. Папка: {home.Root}"
                : File.Exists(home.Desk) ? $"Готова к запуску. Папка: {home.Root}" : $"Нет bin\\KlavaDesk.exe — собери на вкладке «Сервис». Папка: {home.Root}";
            _start.Enabled = !running && File.Exists(home.Desk);
            _restart.Enabled = _stop.Enabled = _muteOn.Enabled = _muteOff.Enabled = _brainsWin.Enabled = _terminal.Enabled = running;

            var brains = home.Brains(cfg);
            var active = brains.FirstOrDefault(b => b.Id == brainId) ?? brains.FirstOrDefault();
            if (active is not null)
            {
                var model = active.Model.Length > 0 ? active.Model : "по умолчанию";
                _brainNow.Text = $"{active.Name}\nмодель для задач: {model}" + (active.ChatModel is { Length: > 0 } cm ? $", для болтовни: {cm}" : "");
                _chain.Text = "Если не ответит: " + string.Join(" → ", Chain(active, brains, cfg, home).Select(b => b.Name.Split(" — ")[0]))
                              + (active.Kind == "openai" && active.Model.Length > 0 ? "  (у AG сначала — соседние модели того же сервера)" : "");
            }
            foreach (var (_, page) in _pages) if (page.Visible) page.Relayout();
        }
        finally { _statusBusy = false; }
    }

    /// <summary>Та же цепочка запасных, что строит сама Клава (Brain.FallbackChain): ссылки fallback, остальные API, Codex, свой мозг.</summary>
    private static List<BrainInfo> Chain(BrainInfo p, List<BrainInfo> all, JsonObject? cfg, KlavaHome home)
    {
        var chain = new List<BrainInfo>();
        bool KeyOk(BrainInfo b)
        {
            var key = cfg?["brains"] is JsonArray arr ? arr.OfType<JsonObject>().FirstOrDefault(o => KlavaHome.Str(o["id"]) == b.Id) is { } o
                ? KlavaHome.Str(o["key"]) : null : null;
            return key is null || key == "antigravity-tools" || !key.StartsWith("env:", StringComparison.Ordinal) || KlavaHome.HasKey(key[4..]);
        }
        bool LocalServer(BrainInfo b)
        {
            var o = cfg?["brains"] is JsonArray arr ? arr.OfType<JsonObject>().FirstOrDefault(x => KlavaHome.Str(x["id"]) == b.Id) : null;
            return KlavaHome.Str(o?["key"]) != "antigravity-tools" && Uri.TryCreate(KlavaHome.Str(o?["url"]) ?? "", UriKind.Absolute, out var u) && u.IsLoopback;
        }
        bool Usable(BrainInfo b) => b.Id != p.Id && !chain.Contains(b) && KeyOk(b) && b.Kind switch
        {
            "openai" => !LocalServer(b),   // STRATA и прочие свои серверы Клава сама в запасные не берёт
            "codex" => KlavaHome.CodexExe() is not null && KlavaHome.CodexLoggedIn(),
            _ => false,
        };
        var seen = new HashSet<string> { p.Id };
        for (var id = p.Fallback; !string.IsNullOrEmpty(id) && seen.Add(id);)
        {
            var b = all.FirstOrDefault(x => x.Id == id);
            if (b is null) break;
            if (Usable(b)) chain.Add(b);
            id = b.Fallback;
        }
        foreach (var b in all.Where(b => b.Kind == "openai").Concat(all.Where(b => b.Kind == "codex"))) if (Usable(b)) chain.Add(b);
        var modelPath = KlavaHome.Str(cfg?["brainModel"]);
        if (p.Kind != "own" && all.FirstOrDefault(b => b.Kind == "own") is { } own && modelPath is not null
            && File.Exists(Path.IsPathRooted(modelPath) ? modelPath : Path.Combine(home.Root, modelPath)))
            chain.Add(own);
        return chain;
    }

    private bool? _claudeOk;

    /// <summary>Спросить Claude Code, залогинен ли (процесс ~1–2 с) — и перерисовать список мозгов, если ответ сменился.</summary>
    private async Task RefreshClaudeAsync()
    {
        var ok = await KlavaHome.ClaudeLoggedInAsync();
        if (IsDisposed || ok == _claudeOk) return;
        _claudeOk = ok;
        RefreshBrains();
    }

    private void RefreshBrains()
    {
        if (_home is null) return;
        var cfg = _home.Config();
        var active = KlavaHome.Str(cfg?["brain"]);
        _brainList.SuspendLayout();
        foreach (Control c in _brainList.Controls.Cast<Control>().ToList()) { _brainList.Controls.Remove(c); c.Dispose(); }
        // Claude Code без входа переключиться не даст — строки нет, пока CLI не залогинен (claude auth login)
        foreach (var b in _home.Brains(cfg).Where(b => b.Kind != "claude-code" || _claudeOk == true || b.Id == active))
        {
            var row = Flow.Full(new BrainRow(b, b.Id == active));
            row.Pick.Click += (_, _) => _ = SwitchBrainAsync(b);
            _brainList.Controls.Add(row);
        }
        _brainList.ResumeLayout();
        RefreshQuotas();
    }

    private void RefreshQuotas()
    {
        var (rows, accounts) = KlavaHome.AgQuotas();
        _quota.SetRows(rows);
        _quotaCard.Title = accounts > 0 ? $"Квоты Antigravity Tools — аккаунтов: {accounts}" : "Квоты Antigravity Tools";
        foreach (var (_, page) in _pages) if (page.Visible) page.Relayout();
    }

    // ================================================================ действия

    private void AskRoot()
    {
        Out("Не нашла папку Клавы (рядом с пультом, KLAVA_HOME). Покажи её — ту, где START_KLAVA.ps1 и bin.", Theme.Amber);
        using var dlg = new FolderBrowserDialog { Description = "Папка Клавы (где START_KLAVA.ps1 и bin)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) == DialogResult.OK && KlavaHome.Use(dlg.SelectedPath) is { } home)
        {
            _home = home;
            Out($"Клава: {home.Root}", Theme.Accent);
            RefreshBrains();
        }
        else Out("Папка не выбрана — пульт без Клавы почти ничего не умеет.", Theme.Red);
    }

    private void StartKlava()
    {
        if (_home is null) { AskRoot(); return; }
        if (!File.Exists(_home.Desk)) { Out("Нет bin\\KlavaDesk.exe — сначала «Собрать Клаву» на вкладке «Сервис».", Theme.Red); return; }
        if (_home.Running() is { } p) { p.Dispose(); Out("Клава уже на рабочем столе.", Theme.Amber); return; }
        Process.Start(new ProcessStartInfo(_home.Desk) { WorkingDirectory = Path.GetDirectoryName(_home.Desk)!, UseShellExecute = true })?.Dispose();
        Out("▶ Клава запускается…", Theme.Green);
        _ = RefreshStatusAsync();
    }

    private async Task<bool> StopKlavaAsync()
    {
        if (_home is null) return false;
        if (_home.Running() is not { } p) { Out("Клава и так выключена.", Theme.Dim); return true; }
        using (p)
        {
            Out("⏻ Прошу Клаву выйти (память записывается)…", Theme.Amber);
            await DeskAsync("выход", "--quit");
            for (var i = 0; i < 60 && !p.HasExited; i++) await Task.Delay(500);
            if (!p.HasExited) { Out("Не вышла за 30 с — закрой из трея или жми «Выключить» ещё раз.", Theme.Red); return false; }
        }
        Out("Клава выключена.", Theme.Dim);
        await RefreshStatusAsync();
        return true;
    }

    private async Task RestartKlavaAsync()
    {
        if (await StopKlavaAsync()) { await Task.Delay(800); StartKlava(); }
    }

    /// <summary>Команда запущенной Клаве (KlavaDesk.exe --say/--quit/--mute/--brains/--terminal): выходит сразу.</summary>
    private async Task<int> DeskAsync(string what, params string[] args)
    {
        if (_home is null || !File.Exists(_home.Desk)) { Out("Нет bin\\KlavaDesk.exe.", Theme.Red); return -1; }
        var psi = new ProcessStartInfo(_home.Desk) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(_home.Desk)! };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return -1;
        await p.WaitForExitAsync();
        if (p.ExitCode == 1 && args[0] != "--set") Out($"«{what}»: Клава не запущена.", Theme.Amber);
        return p.ExitCode;
    }

    private void OpenAudio()
    {
        if (_home is null || !File.Exists(_home.Desk)) { Out("Нет bin\\KlavaDesk.exe.", Theme.Red); return; }
        // у выключенной Клавы окно звука поднимается само по себе и живёт, пока его не закроют — не ждём
        Process.Start(new ProcessStartInfo(_home.Desk, "--audio") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(_home.Desk)! })?.Dispose();
    }

    private async Task SayAsync(string phrase)
    {
        if (_home?.Running() is not { } p) { Out($"«{phrase}» — Клава не запущена, некому сказать.", Theme.Amber); return; }
        p.Dispose();
        Out($"💬 {phrase}", Theme.Pink);
        await DeskAsync(phrase, "--say", phrase);
    }

    private void SendSay()
    {
        var text = _say.Input.Box.Text.Trim();
        if (text.Length == 0) return;
        _say.Input.Box.Clear();
        _ = SayAsync(text);
    }

    private async Task SwitchBrainAsync(BrainInfo b)
    {
        if (_home is null) return;
        if (_home.Running() is { } p)
        {
            p.Dispose();
            var word = b.Id switch
            {
                "antigravity" => "антигравити", "deepseek" => "дипсик", "gemini" => "жминю", "codex" => "кодекс",
                "claude" => "клода", "local" => "свой мозг", _ => b.Aliases.FirstOrDefault() ?? b.Id,
            };
            await SayAsync($"переключись на {word}");
            await Task.Delay(1500);
        }
        else
        {
            var code = await DeskAsync("смена мозга", "--set", $"brain={b.Id}");
            Out(code == 0 ? $"Главный мозг: {b.Name} (запишется в klava.json, Клава выключена)" : $"--set не сработал (код {code})",
                code == 0 ? Theme.Green : Theme.Red);
        }
        RefreshBrains();
        await RefreshStatusAsync();
    }

    private async Task CheckLinksAsync()
    {
        Out("🔌 Проверяю связь…", Theme.Accent);
        foreach (var line in await KlavaHome.CheckLinksAsync())
            Out(line, line.StartsWith('✓') ? Theme.Green : line.StartsWith('✗') ? Theme.Red : Theme.Dim);
    }

    private void StartAg()
    {
        if (KlavaHome.AgExe() is not { } exe) { Out("Antigravity Tools не установлен.", Theme.Red); return; }
        if (Process.GetProcessesByName("antigravity-tools").Length > 0) { Out("Antigravity Tools уже запущен.", Theme.Dim); return; }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
        Out("🪐 Antigravity Tools запускается…", Theme.Accent);
    }

    // ---------------------------------------------------------------- вывод команд

    /// <summary>Запустить процесс с выводом в окно пульта.</summary>
    private async Task<int> RunAsync(string title, string exe, IEnumerable<string> args, string? workDir = null)
    {
        Out($"▶ {title}", Theme.Accent);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workDir ?? _home?.Root ?? Environment.CurrentDirectory,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = new Process { StartInfo = psi };
        p.OutputDataReceived += (_, e) => { if (e.Data is { } d) Out(d, d.Contains("[ПЛОХО]") || d.Contains("НЕ СОБРАЛОСЬ") ? Theme.Red : null); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 } d) Out(d, Theme.Red); };
        Interlocked.Increment(ref _running);
        try
        {
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) { Out($"не запустилось: {ex.Message}", Theme.Red); return -1; }
        finally { Interlocked.Decrement(ref _running); }
        Out($"■ {title}: {(p.ExitCode == 0 ? "готово" : $"код {p.ExitCode}")}", p.ExitCode == 0 ? Theme.Green : Theme.Red);
        return p.ExitCode;
    }

    /// <summary>Скрипт tools\*.ps1 с выводом сюда. Кириллица: вывод PowerShell переводим в UTF-8 до запуска скрипта.</summary>
    private async Task<int> RunScriptAsync(string title, string script, params string[] args)
    {
        if (_home is null) { AskRoot(); return -1; }
        var path = Path.Combine(_home.Root, "tools", script);
        if (!File.Exists(path)) { Out($"Нет {path}", Theme.Red); return -1; }
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var cmd = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $OutputEncoding=[Text.Encoding]::UTF8; $global:LASTEXITCODE=0; & "
                  + Q(path) + " " + string.Join(" ", args.Select(a => a.StartsWith('-') ? a : Q(a))) + "; exit $LASTEXITCODE";
        var code = await RunAsync(title, "powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", cmd]);
        await RefreshStatusAsync();
        return code;
    }

    private async Task RunDeskOutAsync(string title, string arg)
    {
        if (_home is null || !File.Exists(_home.Desk)) { Out("Нет bin\\KlavaDesk.exe.", Theme.Red); return; }
        await RunAsync(title, _home.Desk, [arg], Path.GetDirectoryName(_home.Desk));
    }

    /// <summary>Пуш по токену — в своём окне PowerShell: там скрытый ввод токена и вопрос про репозиторий.</summary>
    private void PushByToken(string dest)
    {
        if (_home is null) { AskRoot(); return; }
        var script = Path.Combine(_home.Root, "tools", "push_github.ps1");
        if (!File.Exists(script)) { Out($"Нет {script}", Theme.Red); return; }
        if (!Directory.Exists(dest)) { Out($"Пакета ещё нет ({dest}) — сначала «Собрать пакет».", Theme.Amber); return; }
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = true, WorkingDirectory = dest };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-NoExit", "-File", script, "-Dir", dest }) psi.ArgumentList.Add(a);
        Process.Start(psi)?.Dispose();
        Out("🚀 Пуш открыт в отдельном окне PowerShell — токен вводится там.", Theme.Pink);
    }

    // ---------------------------------------------------------------- журнал

    private void ShowLogTail()
    {
        if (_home is null) return;
        var (lines, pos) = KlavaHome.Tail(_home.LogFile, 120);
        Out($"── {_home.LogFile} (последние {lines.Count}) ──", Theme.Accent);
        foreach (var l in lines) Out(l, LineColor(l));
        _followPos = pos;
    }

    private void ToggleFollow()
    {
        if (_follow.Enabled)
        {
            _follow.Stop();
            _followBtn.Text = "👁  Следить за журналом";
            Out("── слежение за журналом выключено ──", Theme.Dim);
        }
        else
        {
            ShowLogTail();
            _follow.Start();
            _followBtn.Text = "⏸  Не следить";
            Out("── слежу за журналом ──", Theme.Accent);
        }
        _followBtn.Invalidate();
    }

    private void FollowLog()
    {
        if (_home is null) return;
        try
        {
            var len = File.Exists(_home.LogFile) ? new FileInfo(_home.LogFile).Length : 0;
            if (len < _followPos) _followPos = 0;   // Клава перезапустилась — журнал начат заново
            if (len == _followPos) return;
            var (lines, pos) = KlavaHome.Tail(_home.LogFile, 400, _followPos);
            foreach (var l in lines) Out(l, LineColor(l));
            _followPos = pos;
        }
        catch (IOException) { }
    }

    private static Color? LineColor(string l) =>
        l.Contains("ОШИБКА") || l.Contains("все мозги отказали") ? Theme.Red
        : l.Contains("сторож мозгов") || l.Contains("пересаживаюсь") || l.Contains("беру ") || l.Contains("берёт ") ? Theme.Amber
        : l.Contains("первая фраза") ? Theme.Green
        : null;

    /// <summary>--shot: каждая вкладка в PNG, потом выход.</summary>
    private async Task ShootAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        for (var i = 0; i < _pages.Count; i++)
        {
            Show(i);
            await Task.Delay(400);
            using var bmp = new Bitmap(Width, Height);
            DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
            var file = Path.Combine(dir, $"pult_{i + 1}.png");
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
        }
        Close();
    }

    // ---------------------------------------------------------------- папки

    private void OpenFolder(string? dir)
    {
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) { Out($"Папки нет: {dir}", Theme.Amber); return; }
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true })?.Dispose();
    }

    private void OpenFile(string? file)
    {
        if (string.IsNullOrEmpty(file) || !File.Exists(file)) { Out($"Файла нет: {file}", Theme.Amber); return; }
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true })?.Dispose();
    }
}
