namespace KlavaPult;

/// <summary>
/// Раскладка «как текст»: кнопки в строку с переносом, элементы с пометкой Full — во всю ширину (их высоту считает
/// GetPreferredSize от ширины). FlowLayoutPanel так не умеет: растягивать по ширине и честно мерить высоту.
/// </summary>
internal sealed class Flow : Panel
{
    public int Gap { get; set; } = 10;

    public Flow() => BackColor = Theme.Card;

    /// <summary>Во всю ширину строки.</summary>
    public static T Full<T>(T c) where T : Control { c.Tag = "full"; return c; }

    /// <summary>Перенос строки перед этим элементом.</summary>
    public static T Break<T>(T c) where T : Control { c.Tag = "break"; return c; }

    private int Arrange(int width, bool apply)
    {
        int x = 0, y = 0, rowH = 0;
        foreach (Control c in Controls)
        {
            if (!c.Visible) continue;
            var tag = c.Tag as string;
            if (tag == "full")
            {
                if (x > 0) { y += rowH + Gap; x = 0; rowH = 0; }
                var h = c.GetPreferredSize(new Size(width, 0)).Height;
                if (apply) c.SetBounds(0, y, width, h);
                y += h + Gap;
                continue;
            }
            if (tag == "break" && x > 0) { y += rowH + Gap; x = 0; rowH = 0; }
            var sz = c.Size;
            if (x > 0 && x + sz.Width > width) { y += rowH + Gap; x = 0; rowH = 0; }
            if (apply) c.Location = new Point(x, y);
            x += sz.Width + Gap;
            rowH = Math.Max(rowH, sz.Height);
        }
        return x > 0 ? y + rowH : Math.Max(0, y - Gap);
    }

    public override Size GetPreferredSize(Size proposed)
    {
        var w = proposed.Width > 0 ? proposed.Width : Width;
        return new Size(w, Arrange(w, false));
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Arrange(ClientSize.Width, true);
    }
}

/// <summary>Текст с переносом по словам; высота — по ширине.</summary>
internal sealed class TextBlock : Control
{
    public Color Color { get; set; } = Theme.Dim;

    public TextBlock(string text, Font? font = null, Color? color = null)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Text = text;
        Font = font ?? Theme.Body;
        if (color is { } c) Color = c;
    }

    private const TextFormatFlags Flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); Parent?.PerformLayout(); }

    public override Size GetPreferredSize(Size proposed)
    {
        var w = proposed.Width > 0 ? proposed.Width : Math.Max(100, Width);
        var sz = TextRenderer.MeasureText(Text.Length == 0 ? " " : Text, Font, new Size(w, int.MaxValue), Flags);
        return new Size(w, sz.Height + 2);
    }

    // свой BackColor: он наследуется от контейнера (Flow — цвет карточки), а у тела карточки его ставит Card.SetBody
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color, Flags);
    }
}

/// <summary>Страница пульта: заголовок, подзаголовок, карточки столбиком, своя прокрутка.</summary>
internal sealed class Page : Panel
{
    public string Title { get; }
    public string Sub { get; }
    public List<Card> Cards { get; } = [];

    public Page(string title, string sub)
    {
        Title = title;
        Sub = sub;
        AutoScroll = true;
        BackColor = Theme.Bg;
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    public Card Add(Card c)
    {
        Cards.Add(c);
        Controls.Add(c);
        return c;
    }

    public void Relayout()
    {
        if (!IsHandleCreated) return;
        SuspendLayout();
        // место под полосу прокрутки держим всегда: иначе её появление сужает окно, и вылезает горизонтальная
        var w = Math.Max(320, Width - 48 - SystemInformation.VerticalScrollBarWidth);
        var y = 96 + AutoScrollPosition.Y;
        foreach (var c in Cards.Where(c => c.Visible))
        {
            c.Fit(w);
            c.Location = new Point(24, y);
            y += c.Height + 16;
        }
        AutoScrollMinSize = new Size(0, y - AutoScrollPosition.Y + 8);
        ResumeLayout();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Relayout(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); Relayout(); }
    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var oy = AutoScrollPosition.Y;
        TextRenderer.DrawText(e.Graphics, Title, Theme.H1, new Point(24, 20 + oy), Theme.Text, TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, Sub, Theme.Body, new Point(27, 62 + oy), Theme.Dim, TextFormatFlags.NoPrefix);
    }
}

/// <summary>Строка мозга: имя, модель, запасной; справа кнопка «Сделать главным».</summary>
internal sealed class BrainRow : Control
{
    public BrainInfo Brain { get; }
    public bool Active { get; set; }
    public PillButton Pick { get; }

    public BrainRow(BrainInfo brain, bool active)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Brain = brain;
        Active = active;
        Pick = new PillButton(active ? "★ главный" : "Сделать главным", active ? Theme.Pink : Theme.Accent, primary: false) { Enabled = !active };
        // фон строки = фон кнопки (она закрашивает уголки цветом родителя)
        BackColor = active ? Theme.Mix(Theme.Card, Theme.Pink, 0.10) : Theme.Mix(Theme.Card, Theme.CardHi, 0.55);
        Controls.Add(Pick);
        Height = 58;
    }

    public override Size GetPreferredSize(Size proposed) => new(proposed.Width > 0 ? proposed.Width : Width, 58);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Pick.Location = new Point(Width - Pick.Width, (Height - Pick.Height) / 2);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Theme.Card);
        using (var path = Theme.Round(new Rectangle(0, 2, Width - 1, Height - 5), 12))
        using (var b = new SolidBrush(BackColor))
            g.FillPath(b, path);
        var kind = Brain.Kind switch { "codex" => "ChatGPT по подписке", "claude-code" => "Claude Code", "own" => "свой llama-server", _ => "API" };
        var model = Brain.Model.Length > 0 ? Brain.Model : "модель по умолчанию";
        if (!string.IsNullOrEmpty(Brain.ChatModel)) model += $"  · болтовня: {Brain.ChatModel}";
        var right = Width - Pick.Width - 24;
        TextRenderer.DrawText(g, Brain.Name, Theme.Bold, new Rectangle(14, 6, right, 24), Active ? Theme.Pink : Theme.Text,
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, $"{Brain.Id} · {kind} · {model}{(Brain.Fallback is { Length: > 0 } f ? $"  · запасной → {f}" : "")}", Theme.Small,
            new Rectangle(14, 28, right, 22), Theme.Dim, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
    }
}

/// <summary>Поле «сказать Клаве» и кнопка отправки в одну строку.</summary>
internal sealed class SayRow : Control
{
    public InputBox Input { get; } = new("Напиши Клаве — она услышит, как будто ты сказал вслух…");
    public PillButton Send { get; } = new("Сказать ➤", Theme.Pink, primary: true);

    public SayRow()
    {
        Controls.Add(Input);
        Controls.Add(Send);
        Height = 42;
    }

    public override Size GetPreferredSize(Size proposed) => new(proposed.Width > 0 ? proposed.Width : Width, 42);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Send.Location = new Point(Width - Send.Width, (Height - Send.Height) / 2);
        Input.SetBounds(0, 0, Math.Max(100, Width - Send.Width - 12), 42);
    }

    protected override void OnPaint(PaintEventArgs e) => e.Graphics.Clear(Parent?.BackColor ?? Theme.Card);
}
