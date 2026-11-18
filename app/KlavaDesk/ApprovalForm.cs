namespace KlavaDesk;

/// <summary>
/// Окно «Клава хочет…»: точное действие, подсветка опасного, «Запретить» по умолчанию.
/// Если хозяин не ответил за две минуты — считается отказом.
/// Чекбокс «Разрешать всегда» доступен только для НЕ-опасных действий и позволяет
/// запомнить разрешение на категорию на текущую сессию (без записи в конфиг).
/// </summary>
internal sealed class ApprovalForm : Form
{
    /// <summary>true — пользователь отметил «Разрешать всегда» и нажал «Разрешить».</summary>
    public bool AlwaysAllow { get; private set; }

    public ApprovalForm(ToolRequest req, string? klavaSays)
    {
        Text = "Клава просит разрешения";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(640, 460);
        Padding = new Padding(14);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // заголовок
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // что говорит
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // предупреждение
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // детали
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // чекбокс
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // кнопки

        layout.Controls.Add(new Label
        {
            Text = $"Клава хочет: {req.Title}",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 6),
        });
        layout.Controls.Add(new Label
        {
            Text = string.IsNullOrWhiteSpace(klavaSays) ? "" : $"Она говорит: «{klavaSays}»",
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Margin = new Padding(0, 0, 0, 6),
        });
        layout.Controls.Add(new Label
        {
            Text = req.Dangerous ? $"⚠ ВНИМАНИЕ: {req.DangerReason ?? "действие может навредить"}" : "",
            ForeColor = Color.Firebrick,
            Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
            AutoSize = true,
            MaximumSize = new Size(600, 0),
            Margin = new Padding(0, 0, 0, 6),
        });
        layout.Controls.Add(new TextBox
        {
            Text = req.Details,
            ReadOnly = true,
            Multiline = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Consolas", 10f),
            Dock = DockStyle.Fill,
            BackColor = req.Dangerous ? Color.MistyRose : SystemColors.Window,
        });

        // Чекбокс «Разрешать всегда» — только для безопасных действий
        var chkAlways = new CheckBox
        {
            Text = req.Dangerous
                ? "⚠ Запомнить нельзя — действие помечено как опасное"
                : $"Разрешать всегда (до перезапуска) — категория «{req.Category}»",
            AutoSize = true,
            Enabled = !req.Dangerous,
            ForeColor = req.Dangerous ? Color.Gray : SystemColors.ControlText,
            Margin = new Padding(0, 8, 0, 4),
        };
        layout.Controls.Add(chkAlways);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 4, 0, 0),
        };
        var deny  = new Button { Text = "Запретить", DialogResult = DialogResult.No,  AutoSize = true, Padding = new Padding(10, 4, 10, 4) };
        var allow = new Button { Text = "Разрешить", DialogResult = DialogResult.Yes, AutoSize = true, Padding = new Padding(10, 4, 10, 4) };

        // Разрешить — читаем чекбокс ДО закрытия формы
        allow.Click += (_, _) =>
        {
            AlwaysAllow = chkAlways.Checked && !req.Dangerous;
        };

        buttons.Controls.Add(deny);
        buttons.Controls.Add(allow);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = deny;   // Enter по привычке не должен разрешать
        CancelButton = deny;
        Shown += (_, _) => deny.Focus();

        var timeout = new System.Windows.Forms.Timer { Interval = 120_000 };
        timeout.Tick += (_, _) => { timeout.Stop(); DialogResult = DialogResult.No; };
        timeout.Start();
        FormClosed += (_, _) => timeout.Dispose();
    }
}
