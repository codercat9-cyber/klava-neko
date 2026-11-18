namespace KlavaDesk;

/// <summary>
/// Кибер-терминал Клавы — мультитерминал (друг 30.09: «нормальный исследовательский мультитерминал, и чтобы Клава
/// видела, что там субагенты делают»). Вкладки:
/// 🐾 Клава — её живой pwsh (тот же, что у инструмента shell): друг печатает внизу, Клава печатает лапками
///    (terminal_type); сюда же — команды прежнего самодельного агента;
/// ⌨ PS N — лишние живые pwsh для друга (➕ PS);
/// 🤖 #N — агенты Claude Code (AgentHub): шапка с состоянием, планом и субагентами, лента шагов, строка указания.
/// Окно при закрытии только прячется: оболочки, агенты и история остаются.
/// </summary>
internal sealed class TerminalForm : Form
{
    private static readonly Color Bg = Color.FromArgb(16, 18, 26);
    private static readonly Color Panel2 = Color.FromArgb(24, 27, 38);
    private static readonly Color Neon = Color.FromArgb(0, 255, 180);
    private static readonly Color Pink = Color.FromArgb(255, 170, 230);
    private static readonly Color Red = Color.FromArgb(255, 100, 120);

    private readonly ShellPage _klava;
    private readonly TabControl _tabs = new();
    private readonly Label _lblStatus = new();
    private readonly Label _lblCwd = new();
    private readonly Dictionary<int, (TabPage Tab, AgentPage Page)> _agents = [];
    private int _extraShells;

    /// <summary>Что ещё прервать по «Стоп» на вкладке Клавы: команду прежнего фонового агента (у него свой pwsh).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? OnStop { get; set; }

    /// <summary>Дать задачу новому агенту из терминала: (цель, папка) → что ответил AgentHub.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Func<string, string, Task<string>>? StartAgent { get; set; }

    public TerminalForm(Shell shell, string startDir)
    {
        Text = "🐱 Клава — Кибер Терминал";
        ClientSize = new Size(1000, 600);
        MinimumSize = new Size(620, 360);
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        BackColor = Bg;
        ForeColor = Color.FromArgb(220, 230, 245);
        Font = new Font("Segoe UI", 9.5f);
        ShowInTaskbar = true;
        KeyPreview = true;

        var top = new Panel { Dock = DockStyle.Top, Height = 44, BackColor = Panel2, Padding = new Padding(8, 6, 8, 6) };
        _lblStatus.Text = "● [ГОТОВА] Терминал ждёт команд";
        _lblStatus.ForeColor = Neon;
        _lblStatus.Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        _lblStatus.AutoSize = true;
        _lblStatus.Location = new Point(10, 12);
        _lblCwd.ForeColor = Color.FromArgb(160, 175, 200);
        _lblCwd.AutoSize = true;
        _lblCwd.Location = new Point(360, 12);
        // кнопки — потоком справа: якорь Right считался от ширины панели ДО растяжки (200 px), и кнопки уезжали за край
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, AutoSize = true, BackColor = Panel2 };
        Button Btn(string text, int w, Color fg, Color border, EventHandler click)
        {
            var b = new Button
            {
                Text = text, Size = new Size(w, 28), Margin = new Padding(3, 2, 3, 0), FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(28, 34, 48), ForeColor = fg,
            };
            b.FlatAppearance.BorderColor = border;
            b.Click += click;
            buttons.Controls.Add(b);
            return b;
        }
        var pin = new CheckBox { Text = "📌", Checked = true, Size = new Size(40, 28), Margin = new Padding(3, 4, 3, 0) };
        pin.CheckedChanged += (_, _) => TopMost = pin.Checked;
        buttons.Controls.Add(pin);
        Btn("🤖 ➕ Агент", 100, Pink, Color.FromArgb(160, 80, 140), (_, _) => _ = NewAgentAsync());
        Btn("⌨ ➕ PS", 92, Color.FromArgb(180, 210, 255), Color.FromArgb(60, 80, 120), (_, _) => AddShellTab());
        Btn("🧹", 36, Color.FromArgb(180, 210, 255), Color.FromArgb(60, 80, 120), (_, _) => Current()?.Clear());
        Btn("⏹️ Стоп", 75, Color.FromArgb(255, 120, 140), Color.FromArgb(255, 80, 100), (_, _) => StopCurrent());
        _lblCwd.AutoSize = false;
        _lblCwd.Dock = DockStyle.Fill;
        _lblCwd.TextAlign = ContentAlignment.MiddleLeft;
        _lblCwd.AutoEllipsis = true;
        _lblStatus.AutoSize = false;
        _lblStatus.Dock = DockStyle.Left;
        _lblStatus.Width = 340;
        _lblStatus.TextAlign = ContentAlignment.MiddleLeft;
        _lblStatus.AutoEllipsis = true;
        top.Controls.Add(_lblCwd);
        top.Controls.Add(buttons);
        top.Controls.Add(_lblStatus);

        _tabs.Dock = DockStyle.Fill;
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.SizeMode = TabSizeMode.Fixed;
        _tabs.ItemSize = new Size(170, 26);
        _tabs.DrawItem += DrawTab;
        _tabs.SelectedIndexChanged += (_, _) => { Current()?.FocusInput(); ShowPageStatus(); };

        _klava = new ShellPage(shell, startDir, owned: false, banner: true) { Dock = DockStyle.Fill };
        _klava.Status += SetStatusFrom;
        _klava.StopExtra = () => OnStop?.Invoke();
        AddTab("🐾 Клава", _klava);

        Controls.Add(_tabs);
        Controls.Add(top);
        UpdateCwd(startDir);

        // новая вкладка сама догоняет строки сессии; повторы отсекает номер строки
        AgentHub.Line += (s, l) => Post(() => AgentPageFor(s).Add(l));
        AgentHub.Changed += s => Post(() => OnAgentChanged(s));
        // хендл сразу: события оболочек и агентов приходят из фоновых потоков и зовут BeginInvoke, даже пока окно спрятано
        _ = Handle;
    }

    // ---------- вкладки ----------

    private TabPage AddTab(string title, Control page)
    {
        var tab = new TabPage(title) { BackColor = Bg, ForeColor = ForeColor };
        tab.Controls.Add(page);
        _tabs.TabPages.Add(tab);
        return tab;
    }

    private ITerminalPage? Current() => _tabs.SelectedTab?.Controls.OfType<ITerminalPage>().FirstOrDefault();

    private void DrawTab(object? sender, DrawItemEventArgs e)
    {
        var tab = _tabs.TabPages[e.Index];
        var selected = e.Index == _tabs.SelectedIndex;
        using var bg = new SolidBrush(selected ? Color.FromArgb(40, 46, 66) : Panel2);
        e.Graphics.FillRectangle(bg, e.Bounds);
        var page = tab.Controls.OfType<ITerminalPage>().FirstOrDefault();
        var color = page?.Mark switch { '●' => Color.FromArgb(0, 220, 255), '✓' => Neon, '✗' => Red, _ => Color.FromArgb(200, 210, 230) };
        TextRenderer.DrawText(e.Graphics, tab.Text, Font, e.Bounds, selected ? Color.White : color,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
        if (selected)
        {
            using var line = new Pen(color, 2);
            e.Graphics.DrawLine(line, e.Bounds.Left + 4, e.Bounds.Bottom - 2, e.Bounds.Right - 4, e.Bounds.Bottom - 2);
        }
    }

    private void AddShellTab()
    {
        _extraShells++;
        var dir = string.IsNullOrEmpty(_klava.Cwd) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : _klava.Cwd;
        var page = new ShellPage(new Shell(), dir, owned: true, banner: false) { Dock = DockStyle.Fill };
        page.Status += SetStatusFrom;
        var tab = AddTab($"⌨ PS {_extraShells}", page);
        page.CloseRequested += () => { _tabs.TabPages.Remove(tab); page.Dispose(); tab.Dispose(); };
        _tabs.SelectedTab = tab;
    }

    private AgentPage AgentPageFor(AgentSession s)
    {
        if (_agents.TryGetValue(s.Id, out var have)) return have.Page;
        var page = new AgentPage(s) { Dock = DockStyle.Fill };
        page.Status += SetStatusFrom;
        var tab = AddTab($"🤖 #{s.Id} {s.Title}", page);
        _agents[s.Id] = (tab, page);
        page.CloseRequested += () =>
        {
            AgentHub.Forget(s);
            _agents.Remove(s.Id);
            _tabs.TabPages.Remove(tab);
            page.Dispose();
            tab.Dispose();
        };
        foreach (var l in s.Lines) page.Add(l);   // вкладка открылась позже первых строк — догоняем
        return page;
    }

    private void OnAgentChanged(AgentSession s)
    {
        if (!AgentHub.Sessions.Contains(s))
        {
            if (_agents.Remove(s.Id, out var gone)) { _tabs.TabPages.Remove(gone.Tab); gone.Page.Dispose(); gone.Tab.Dispose(); }
            return;
        }
        var page = AgentPageFor(s);
        var fresh = page.Refresh();
        _tabs.Invalidate();
        if (fresh && _tabs.SelectedTab?.Controls.Contains(page) != true && s.Working && page.JustStarted)
            _tabs.SelectedTab = _agents[s.Id].Tab;   // новый агент — сразу на его вкладку
        if (_tabs.SelectedTab?.Controls.Contains(page) == true) ShowPageStatus();
    }

    private void StopCurrent()
    {
        switch (Current())
        {
            case ShellPage sp:
                sp.Cancel();
                break;
            case AgentPage ap:
                AgentHub.Stop(ap.Session.Id.ToString());
                break;
        }
    }

    private async Task NewAgentAsync()
    {
        if (StartAgent is null) { SetStatus("● агенты недоступны в этом режиме", Red); return; }
        using var dlg = new AgentTaskDialog(_klava.Cwd);
        if (dlg.ShowDialog(this) != DialogResult.OK || dlg.Goal.Trim().Length == 0) return;
        SetStatus("● [АГЕНТ] запускаю…", Pink);
        var answer = await StartAgent(dlg.Goal, dlg.Folder);
        SetStatus($"● {Truncate(answer.Replace('\n', ' '), 90)}", Pink);
    }

    private void ShowPageStatus()
    {
        if (Current() is AgentPage ap) { SetStatus($"● {ap.Session.State}", ap.Session.Working ? Color.FromArgb(0, 220, 255) : Neon); UpdateCwd(ap.Session.Cwd); }
        else if (Current() is ShellPage sp) UpdateCwd(sp.Cwd);
    }

    private void SetStatusFrom(ITerminalPage page, string text, Color color)
    {
        if (Current() == page) SetStatus(text, color);
        _tabs.Invalidate();
    }

    private void SetStatus(string text, Color color)
    {
        _lblStatus.Text = text;
        _lblStatus.ForeColor = color;
    }

    private void UpdateCwd(string dir) => _lblCwd.Text = "📁 " + dir;

    private void Post(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); }
        catch (InvalidOperationException) { }
    }

    // ---------- вкладка Клавы: API для OverlayForm и инструментов ----------

    public void SetStartDir(string dir) => _klava.SetStartDir(dir);

    public void AppendCommand(string command, string cwd) => Post(() => { _klava.AppendCommand(command, cwd); if (Current() == _klava) UpdateCwd(cwd); });

    /// <summary>Строка от прежнего фонового агента: что он думает и делает (розовым, отдельной строкой).</summary>
    public void AppendNote(string line)
    {
        if (!string.IsNullOrWhiteSpace(line)) Post(() => _klava.AppendNote(line));
    }

    public void AppendOutput(string text)
    {
        if (!string.IsNullOrEmpty(text)) Post(() => _klava.AppendOutput(text));
    }

    public void FinishCommand(int exitCode, long elapsedMs, string? newCwd = null) => Post(() => _klava.FinishCommand(exitCode, elapsedMs, newCwd));

    /// <summary>Клава печатает команду лапками прямо в строку своей вкладки и выполняет её в живом pwsh.</summary>
    public async Task<ShellResult> TypeAndRunAsync(string command, TimeSpan timeout, CancellationToken ct)
    {
        await UiAsync(() =>
        {
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            _tabs.SelectedIndex = 0;
            BringToFront();
        });
        return await _klava.TypeAndRunAsync(command, timeout, ct, UiAsync);
    }

    /// <summary>Выполнить на UI-потоке и дождаться.</summary>
    private Task UiAsync(Action a)
    {
        if (IsDisposed) return Task.CompletedTask;
        if (!InvokeRequired) { a(); return Task.CompletedTask; }
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(() =>
            {
                try { a(); tcs.TrySetResult(); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            });
        }
        catch (InvalidOperationException) { tcs.TrySetResult(); }
        return tcs.Task;
    }

    /// <summary>Для снимков: выбрать вкладку (−1 — последнюю).</summary>
    internal void SelectTabForTest(int index) => _tabs.SelectedIndex = index < 0 ? _tabs.TabCount - 1 : index;

    internal static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Current()?.FocusInput();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            base.OnFormClosing(e);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            foreach (var p in _tabs.TabPages.Cast<TabPage>().SelectMany(t => t.Controls.OfType<ShellPage>()).Where(p => p != _klava))
                p.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Вкладка терминала: что умеет каждая (очистить, фокус на ввод, метка состояния на ярлыке).</summary>
internal interface ITerminalPage
{
    char Mark { get; }
    void Clear();
    void FocusInput();
}

/// <summary>Общая консоль: цветной вывод с ограничением истории (RichTextBox на мегабайтах тормозит весь UI).</summary>
internal static class Console2
{
    public static RichTextBox Make() => new()
    {
        Dock = DockStyle.Fill, BackColor = Color.FromArgb(10, 12, 16), ForeColor = Color.FromArgb(210, 225, 240),
        Font = new Font("Consolas", 10f), ReadOnly = true, BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.Both,
        WordWrap = false, DetectUrls = false,
    };

    public static void Append(RichTextBox box, string text, Color color)
    {
        if (box.TextLength > 400_000)
        {
            box.Select(0, 150_000);
            box.ReadOnly = false;
            box.SelectedText = "";
            box.ReadOnly = true;
        }
        box.SelectionStart = box.TextLength;
        box.SelectionLength = 0;
        box.SelectionColor = color;
        box.AppendText(text);
        box.SelectionColor = box.ForeColor;
        box.ScrollToCaret();
    }

    public static (Panel Panel, Label Prompt, TextBox Input) InputLine(string prompt, string placeholder)
    {
        var panel = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Color.FromArgb(20, 24, 34), Padding = new Padding(6, 5, 6, 5) };
        var p = new Label
        {
            Text = prompt, Dock = DockStyle.Left, AutoSize = false, Width = 44, TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(0, 255, 180), Font = new Font("Consolas", 10.5f, FontStyle.Bold),
        };
        var input = new TextBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Color.FromArgb(20, 24, 34), ForeColor = Color.White,
            Font = new Font("Consolas", 11f), PlaceholderText = placeholder,
        };
        panel.Controls.Add(input);
        panel.Controls.Add(p);
        return (panel, p, input);
    }
}

/// <summary>Вкладка живого pwsh: вывод команд, строка ввода с историей, набор лапками Клавы.</summary>
internal sealed class ShellPage : Panel, ITerminalPage
{
    private readonly Shell _shell;
    private readonly bool _owned;
    private readonly RichTextBox _out = Console2.Make();
    private readonly Label _prompt;
    private readonly TextBox _input;
    private readonly List<string> _history = [];
    private int _historyAt = -1;
    private string _startDir;
    private bool _pawTyping;
    private char _mark = ' ';

    public event Action<ITerminalPage, string, Color>? Status;
    public event Action? CloseRequested;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? StopExtra { get; set; }
    public char Mark => _mark;
    public string Cwd => string.IsNullOrEmpty(_shell.Cwd) ? _startDir : _shell.Cwd;

    public ShellPage(Shell shell, string startDir, bool owned, bool banner)
    {
        _shell = shell;
        _owned = owned;
        _startDir = startDir;
        var (panel, prompt, input) = Console2.InputLine("PS›", "команда PowerShell… (Enter — выполнить, ↑/↓ — история, Ctrl+C — прервать)");
        _prompt = prompt;
        _input = input;
        _input.KeyDown += OnKey;
        Controls.Add(_out);
        Controls.Add(panel);
        if (owned)
        {
            // своя оболочка вкладки: её события — сюда (у вкладки Клавы события приходят от OverlayForm)
            _shell.CommandStarted += (cmd, cwd) => Post(() => AppendCommand(cmd, cwd));
            _shell.OutputChunk += text => Post(() => AppendOutput(text));
            _shell.CommandCompleted += (code, ms, cwd) => Post(() => FinishCommand(code, ms, cwd));
            var close = new Button
            {
                Text = "✖ закрыть вкладку", Dock = DockStyle.Right, Width = 130, FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(28, 34, 48), ForeColor = Color.FromArgb(180, 190, 210),
            };
            close.Click += (_, _) => CloseRequested?.Invoke();
            panel.Controls.Add(close);
        }
        if (banner)
        {
            Append("===================================================================\n", Color.FromArgb(90, 110, 150));
            Append("  🐱 КЛАВА — КИБЕР ТЕРМИНАЛ (живой PowerShell на весь разговор)\n", Color.FromArgb(0, 255, 200));
            Append("  Пиши команду внизу — или попроси Клаву, она наберёт сама.\n", Color.FromArgb(140, 160, 190));
            Append("  ⌨ ➕ PS — ещё терминал для тебя; 🤖 ➕ Агент — задача агенту (своя вкладка, субагенты, план).\n", Color.FromArgb(255, 170, 230));
            Append("  Автопилот режет только катастрофы; удаление — в Корзину.\n", Color.FromArgb(255, 180, 100));
            Append("===================================================================\n\n", Color.FromArgb(90, 110, 150));
        }
        else Append($"⌨ живой PowerShell (своя оболочка вкладки), папка {startDir}\n\n", Color.FromArgb(140, 160, 190));
    }

    public void SetStartDir(string dir) => _startDir = dir;
    public void Clear() => _out.Clear();
    public void FocusInput() => _input.Focus();

    public void Cancel()
    {
        _shell.CancelAsync();
        StopExtra?.Invoke();
        Append("\n[⏹️ Команда прервана]\n", Color.OrangeRed);
        Say("● [ПРЕРВАНО] Команда остановлена", Color.OrangeRed);
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            if (_pawTyping) return;
            var cmd = _input.Text.Trim();
            if (cmd.Length == 0) return;
            _input.Clear();
            Remember(cmd);
            _ = RunAsync(cmd);
        }
        else if (e.KeyCode == Keys.Up && _history.Count > 0)
        {
            e.SuppressKeyPress = true;
            _historyAt = _historyAt < 0 ? _history.Count - 1 : Math.Max(0, _historyAt - 1);
            _input.Text = _history[_historyAt];
            _input.SelectionStart = _input.TextLength;
        }
        else if (e.KeyCode == Keys.Down && _history.Count > 0)
        {
            e.SuppressKeyPress = true;
            if (_historyAt < 0) return;
            _historyAt++;
            if (_historyAt >= _history.Count) { _historyAt = -1; _input.Clear(); }
            else { _input.Text = _history[_historyAt]; _input.SelectionStart = _input.TextLength; }
        }
        else if (e.Control && e.KeyCode == Keys.C && _input.SelectionLength == 0)
        {
            e.SuppressKeyPress = true;
            Cancel();
        }
    }

    private void Remember(string cmd)
    {
        if (_history.Count == 0 || _history[^1] != cmd) _history.Add(cmd);
        if (_history.Count > 200) _history.RemoveAt(0);
        _historyAt = -1;
    }

    private async Task RunAsync(string cmd)
    {
        try
        {
            if (!_shell.IsOpen) await _shell.StartAsync(_startDir);
            await _shell.RunAsync(cmd, TimeSpan.FromMinutes(30));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            Append($"\n❌ {ex.Message}\n", Color.OrangeRed);
        }
    }

    /// <summary>
    /// Клава печатает команду лапками в строку ввода (видно каждую букву), жмёт Enter — и команда выполняется в живом
    /// pwsh. ui — выполнить на UI-потоке и дождаться (окно-владелец).
    /// </summary>
    public async Task<ShellResult> TypeAndRunAsync(string command, TimeSpan timeout, CancellationToken ct, Func<Action, Task> ui)
    {
        await ui(() =>
        {
            _pawTyping = true;
            _input.Clear();
            _prompt.Text = "🐾›";
            Say("● [ЛАПКИ] Клава печатает команду…", Color.FromArgb(255, 170, 230));
        });
        try
        {
            // одна строка в поле ввода — длинный скрипт показываем первой строкой с «…», выполняем целиком
            var visible = command.Replace("\r", "").Split('\n')[0];
            if (visible.Length < command.Trim().Length) visible += " …";
            var delay = Math.Clamp(1400 / Math.Max(1, visible.Length), 6, 35);
            foreach (var ch in visible)
            {
                ct.ThrowIfCancellationRequested();
                var c = ch;
                await ui(() => _input.AppendText(c.ToString()));
                await Task.Delay(delay, ct);
            }
            await Task.Delay(180, ct);
            await ui(() => { _input.Clear(); Remember(command); });
        }
        finally
        {
            await ui(() => { _pawTyping = false; _prompt.Text = "PS›"; });
        }
        if (!_shell.IsOpen) await _shell.StartAsync(_startDir);
        return await _shell.RunAsync(command, timeout, ct);
    }

    public void AppendCommand(string command, string cwd)
    {
        _mark = '●';
        Say($"● [ВЫПОЛНЯЮ] {TerminalForm.Truncate(command.Replace('\n', ' '), 40)}", Color.FromArgb(0, 220, 255));
        Append($"[{DateTime.Now:HH:mm:ss}] ", Color.FromArgb(100, 120, 150));
        Append($"PS {cwd}> ", Color.FromArgb(0, 255, 180));
        Append($"{command}\n", Color.White);
    }

    public void AppendNote(string line)
    {
        Say($"● [АГЕНТ] {TerminalForm.Truncate(line.Replace('\n', ' '), 50)}", Color.FromArgb(255, 170, 230));
        Append($"{line.TrimEnd()}\n", Color.FromArgb(255, 170, 230));
    }

    public void AppendOutput(string text) => Append(text, Color.FromArgb(200, 215, 230));

    public void FinishCommand(int exitCode, long elapsedMs, string? newCwd)
    {
        _mark = exitCode == 0 ? '✓' : '✗';
        if (exitCode == 0)
        {
            Say($"● [ГОТОВА] Успешно ({elapsedMs} мс)", Color.FromArgb(0, 255, 180));
            Append($"\n✔ [Exit Code: 0, {elapsedMs} мс]\n\n", Color.FromArgb(0, 255, 180));
        }
        else
        {
            Say($"● [КОД {exitCode}] ({elapsedMs} мс) — это не конец, смотри вывод", Color.FromArgb(255, 100, 120));
            Append($"\n❌ [Exit Code: {exitCode}, {elapsedMs} мс]\n\n", Color.FromArgb(255, 100, 120));
        }
    }

    private void Say(string text, Color color) => Status?.Invoke(this, text, color);

    private void Append(string text, Color color) => Console2.Append(_out, text, color);

    private void Post(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); }
        catch (InvalidOperationException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _owned) _shell.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Вкладка агента Claude Code: шапка (состояние, план, субагенты), лента шагов, указание по ходу.</summary>
internal sealed class AgentPage : Panel, ITerminalPage
{
    private readonly RichTextBox _out = Console2.Make();
    private readonly Label _head = new();
    private readonly TextBox _input;
    private readonly DateTime _created = DateTime.Now;
    private long _shown;   // номер последней показанной строки — вкладка, открытая позже, догоняет без повторов

    public AgentSession Session { get; }
    public event Action<ITerminalPage, string, Color>? Status;
    public event Action? CloseRequested;
    public char Mark => Session.Working ? '●' : Session.Alive && Session.State.StartsWith("готово", StringComparison.Ordinal) ? '✓' : Session.Alive ? ' ' : '✗';
    public bool JustStarted => DateTime.Now - _created < TimeSpan.FromSeconds(3);

    public AgentPage(AgentSession s)
    {
        Session = s;
        _head.Dock = DockStyle.Top;
        _head.Height = 64;
        _head.BackColor = Color.FromArgb(22, 20, 34);
        _head.ForeColor = Color.FromArgb(255, 200, 240);
        _head.Font = new Font("Segoe UI", 9f);
        _head.Padding = new Padding(8, 4, 8, 4);
        var (panel, _, input) = Console2.InputLine("🐱›", "указание агенту по ходу или новая задача ему (Enter — отправить)");
        _input = input;
        _input.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            var t = _input.Text.Trim();
            if (t.Length == 0) return;
            _input.Clear();
            Status?.Invoke(this, "● " + AgentHub.Tell(Session.Id.ToString(), t), Color.FromArgb(255, 170, 230));
        };
        var stop = new Button { Text = "⏹ стоп", Dock = DockStyle.Right, Width = 70, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(45, 25, 35), ForeColor = Color.FromArgb(255, 120, 140) };
        stop.Click += (_, _) => AgentHub.Stop(Session.Id.ToString());
        var close = new Button { Text = "✖ закрыть", Dock = DockStyle.Right, Width = 90, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(28, 34, 48), ForeColor = Color.FromArgb(180, 190, 210) };
        close.Click += (_, _) => CloseRequested?.Invoke();
        // модель агента — на ходу: список у шлюза, выбранная пересаживает агента (он продолжает с того же места)
        var model = new Button { Text = "🧠 модель", Dock = DockStyle.Right, Width = 92, FlatStyle = FlatStyle.Flat, BackColor = Color.FromArgb(30, 26, 48), ForeColor = Color.FromArgb(200, 180, 255) };
        model.Click += async (_, _) =>
        {
            var menu = new ContextMenuStrip();
            var list = await AgentHub.UiModelsAsync();
            if (IsDisposed) return;
            if (list.Count == 0) menu.Items.Add(new ToolStripMenuItem("шлюз моделей не ответил") { Enabled = false });
            foreach (var m in list)
            {
                var item = new ToolStripMenuItem(m) { Checked = m.Equals(Session.Model, StringComparison.OrdinalIgnoreCase) };
                item.Click += async (_, _) =>
                {
                    var res = await AgentHub.UiSwitchAsync(Session, m);
                    if (!IsDisposed) Status?.Invoke(this, "● " + res, Color.FromArgb(200, 180, 255));
                };
                menu.Items.Add(item);
            }
            menu.Show(model, new Point(0, -Math.Min(menu.PreferredSize.Height, 480)));
        };
        panel.Controls.Add(stop);
        panel.Controls.Add(model);
        panel.Controls.Add(close);
        Controls.Add(_out);
        Controls.Add(panel);
        Controls.Add(_head);
        Refresh();
    }

    public void Clear() => _out.Clear();
    public void FocusInput() => _input.Focus();

    /// <summary>Перерисовать шапку по сессии; true — первый раз (вкладка только появилась).</summary>
    public new bool Refresh()
    {
        var s = Session;
        var plan = s.Plan.Count == 0 ? "" :
            $"\n📋 план {s.Plan.Count(x => x.Status == "completed")}/{s.Plan.Count}: " +
            string.Join("  ", s.Plan.Select(x => $"{(x.Status == "completed" ? "✓" : x.Status == "in_progress" ? "▶" : "·")} {TerminalForm.Truncate(x.Text, 40)}"));
        var subs = s.Subs;
        var subText = subs.Count == 0 ? "" :
            "\n🧩 " + string.Join("   ", subs.TakeLast(5).Select(x => $"{x.Type}: {(x.State == "работает" ? $"▶ {TerminalForm.Truncate(x.Doing.Length > 0 ? x.Doing : x.Description, 50)}" : x.State)}"));
        var cost = s.Cost > 0 ? $" · ${s.Cost:F2}" : "";
        _head.Text = $"🤖 #{s.Id} «{s.Title}» — {s.State}{(s.Doing.Length > 0 ? $": {TerminalForm.Truncate(s.Doing, 90)}" : "")} · шагов {s.Steps}{cost} · {s.Route}, {s.Model}{plan}{subText}";
        _head.Height = 26 + 18 * ((plan.Length > 0 ? 1 : 0) + (subText.Length > 0 ? 1 : 0));
        return JustStarted;
    }

    public void Add(AgentLine l)
    {
        if (l.Seq <= _shown) return;
        _shown = l.Seq;
        var indent = l.Sub is { } sub ? $"   └ [{sub}] " : "";
        var (color, prefix) = l.Kind switch
        {
            't' => (Color.FromArgb(255, 200, 240), "🐱 "),
            'c' => (Color.FromArgb(0, 220, 255), "🔧 "),
            'o' => (Color.FromArgb(150, 200, 170), "   "),
            'e' => (Color.FromArgb(255, 110, 130), "   "),
            'a' => (Color.FromArgb(255, 190, 90), ""),
            'p' => (Color.FromArgb(180, 160, 255), ""),
            'r' => (Color.FromArgb(0, 255, 180), ""),
            'w' => (Color.FromArgb(255, 170, 60), ""),
            'y' => (Color.White, "👤 "),
            _ => (Color.FromArgb(120, 140, 170), ""),
        };
        Console2.Append(_out, $"[{l.At:HH:mm:ss}] ", Color.FromArgb(90, 105, 135));
        Console2.Append(_out, $"{indent}{prefix}{l.Text.TrimEnd()}\n", l.Sub is null ? color : ControlPaint.Dark(color, 0.1f));
        if (l.Kind == 'r') Console2.Append(_out, "\n", color);
        Status?.Invoke(this, $"● #{Session.Id}: {TerminalForm.Truncate(l.Text.Replace('\n', ' '), 70)}", color);
    }
}

/// <summary>Окно «задача агенту»: что сделать (многострочно) и в какой папке.</summary>
internal sealed class AgentTaskDialog : Form
{
    private readonly TextBox _goal = new();
    private readonly TextBox _folder = new();

    public string Goal => _goal.Text;
    public string Folder => _folder.Text;

    public AgentTaskDialog(string folder)
    {
        Text = "🤖 Задача агенту Клавы";
        ClientSize = new Size(620, 330);
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Color.FromArgb(24, 27, 38);
        ForeColor = Color.FromArgb(220, 230, 245);
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        var l1 = new Label { Text = "Что сделать (подробно: что, где, как проверить):", Location = new Point(12, 10), AutoSize = true };
        _goal.Multiline = true;
        _goal.ScrollBars = ScrollBars.Vertical;
        _goal.Location = new Point(12, 32);
        _goal.Size = new Size(596, 190);
        _goal.BackColor = Color.FromArgb(14, 16, 22);
        _goal.ForeColor = Color.White;
        var l2 = new Label { Text = "Папка:", Location = new Point(12, 234), AutoSize = true };
        _folder.Text = folder;
        _folder.Location = new Point(70, 231);
        _folder.Size = new Size(538, 24);
        _folder.BackColor = Color.FromArgb(14, 16, 22);
        _folder.ForeColor = Color.White;
        var ok = new Button { Text = "Запустить", DialogResult = DialogResult.OK, Location = new Point(412, 280), Size = new Size(96, 32), FlatStyle = FlatStyle.Flat, ForeColor = Color.FromArgb(0, 255, 180) };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Location = new Point(514, 280), Size = new Size(94, 32), FlatStyle = FlatStyle.Flat };
        AcceptButton = null;   // Enter — перенос строки в задаче
        CancelButton = cancel;
        Controls.AddRange([l1, _goal, l2, _folder, ok, cancel]);
    }
}
