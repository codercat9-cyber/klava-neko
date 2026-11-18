using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace KlavaPult;

/// <summary>Тема пульта: ночь, синий кимоно, розовая сакура. Рисуем сами — стандартные кнопки WinForms на тёмном не смотрятся.</summary>
internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(13, 16, 28);
    public static readonly Color Side = Color.FromArgb(18, 22, 38);
    public static readonly Color Card = Color.FromArgb(24, 29, 50);
    public static readonly Color CardHi = Color.FromArgb(34, 41, 70);
    public static readonly Color Line = Color.FromArgb(46, 55, 92);
    public static readonly Color Text = Color.FromArgb(232, 236, 250);
    public static readonly Color Dim = Color.FromArgb(146, 156, 190);
    public static readonly Color Accent = Color.FromArgb(92, 140, 255);
    public static readonly Color Pink = Color.FromArgb(255, 122, 182);
    public static readonly Color Green = Color.FromArgb(61, 220, 151);
    public static readonly Color Amber = Color.FromArgb(255, 181, 71);
    public static readonly Color Red = Color.FromArgb(255, 92, 122);
    public static readonly Color Console = Color.FromArgb(9, 11, 20);

    public static readonly Font H1 = new("Segoe UI Semibold", 20f);
    public static readonly Font H2 = new("Segoe UI Semibold", 12.5f);
    public static readonly Font Body = new("Segoe UI", 10f);
    public static readonly Font Small = new("Segoe UI", 9f);
    public static readonly Font Bold = new("Segoe UI Semibold", 10f);
    public static readonly Font Mono = new(FontFamily.Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas", 9.5f);

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>Тёмная полоса заголовка окна (Windows 10 20H1+ / 11); на старых молча ничего.</summary>
    public static void DarkTitle(Form f)
    {
        var on = 1;
        try { _ = DwmSetWindowAttribute(f.Handle, 20, ref on, sizeof(int)); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }
}

/// <summary>Карточка с заголовком: скруглённая, с тонкой рамкой. Внутри одно тело (Body) — его высоту считает Fit.</summary>
internal sealed class Card : Panel
{
    private string _title = "";
    private string _note = "";
    public Control? Body { get; private set; }

    public Card(string title, string note = "")
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Bg;
        Title = title;
        Note = note;
    }

    public string Title { get => _title; set { _title = value; Invalidate(); } }
    public string Note { get => _note; set { _note = value; Invalidate(); } }

    private int Top0 => _title.Length == 0 ? 16 : _note.Length == 0 ? 50 : 70;

    public void SetBody(Control body)
    {
        if (Body is not null) Controls.Remove(Body);
        Body = body;
        body.BackColor = Theme.Card;
        Controls.Add(body);
    }

    /// <summary>Ширина задана — высота по содержимому (FlowLayoutPanel переносит строки по ширине).</summary>
    public void Fit(int width)
    {
        Width = width;
        if (Body is null) { Height = Top0 + 16; return; }
        var inner = Math.Max(100, width - 36);
        var h = Body.GetPreferredSize(new Size(inner, 0)).Height;
        Body.SetBounds(18, Top0, inner, h);
        Height = Top0 + h + 16;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 16);
        using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, path);
        using (var pen = new Pen(Theme.Line)) g.DrawPath(pen, path);
        if (_title.Length > 0) TextRenderer.DrawText(g, _title, Theme.H2, new Point(18, 14), Theme.Text, TextFormatFlags.NoPrefix);
        if (_note.Length > 0)
            TextRenderer.DrawText(g, _note, Theme.Small, new Rectangle(18, 40, Width - 36, 24), Theme.Dim,
                TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Кнопка-пилюля: обычная (контур) или главная (заливка цветом).</summary>
internal sealed class PillButton : Control
{
    private bool _hover, _down;
    public Color Accent { get; set; }
    public bool Primary { get; set; }

    public PillButton(string text, Color? accent = null, bool primary = false)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint
                 | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Text = text;
        Accent = accent ?? Theme.Accent;
        Primary = primary;
        Font = Theme.Bold;
        Cursor = Cursors.Hand;
        TabStop = true;
        Size = new Size(TextRenderer.MeasureText(text, Font).Width + 34, 38);
        Margin = new Padding(0, 0, 10, 10);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Space) OnClick(EventArgs.Empty);
        base.OnKeyUp(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        var r = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = Theme.Round(r, Height / 2);
        Color fill, border, text;
        if (!Enabled) { fill = Theme.Card; border = Theme.Line; text = Theme.Mix(Theme.Dim, Theme.Card, 0.45); }
        else if (Primary)
        {
            fill = _down ? Theme.Mix(Accent, Color.Black, 0.25) : _hover ? Theme.Mix(Accent, Color.White, 0.12) : Accent;
            border = fill;
            text = Color.White;
        }
        else
        {
            fill = _down ? Theme.Mix(Theme.CardHi, Accent, 0.30) : _hover ? Theme.Mix(Theme.CardHi, Accent, 0.16) : Theme.CardHi;
            border = _hover || Focused ? Accent : Theme.Line;
            text = Theme.Text;
        }
        using (var b = new SolidBrush(fill)) g.FillPath(b, path);
        using (var pen = new Pen(border, Focused ? 2f : 1f)) g.DrawPath(pen, path);
        TextRenderer.DrawText(g, Text, Font, r, text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Пункт бокового меню: значок, подпись, полоска выбранного.</summary>
internal sealed class NavButton : Control
{
    private bool _hover;
    private bool _selected;
    private static readonly Font EmojiFont = new("Segoe UI Emoji", 13f);
    public string Icon { get; }

    public NavButton(string icon, string text)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Icon = icon;
        Text = text;
        Height = 46;
        Cursor = Cursors.Hand;
        Font = Theme.Body;
    }

    public bool Selected { get => _selected; set { _selected = value; Invalidate(); } }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Side);
        var r = new Rectangle(10, 3, Width - 20, Height - 6);
        if (_selected || _hover)
        {
            using var path = Theme.Round(r, 12);
            using var b = new SolidBrush(_selected ? Theme.Mix(Theme.Side, Theme.Accent, 0.22) : Theme.Mix(Theme.Side, Theme.CardHi, 0.7));
            g.FillPath(b, path);
        }
        if (_selected)
        {
            using var bar = Theme.Round(new Rectangle(10, 12, 4, Height - 24), 2);
            using var bb = new SolidBrush(Theme.Pink);
            g.FillPath(bb, bar);
        }
        TextRenderer.DrawText(g, Icon, EmojiFont, new Rectangle(24, 0, 34, Height), _selected ? Theme.Pink : Theme.Dim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, Text, _selected ? Theme.Bold : Theme.Body, new Rectangle(62, 0, Width - 70, Height), _selected ? Theme.Text : Theme.Dim,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Состояние службы для строки статуса.</summary>
internal enum Health { Ok, Warn, Bad, Off }

/// <summary>Сетка строк «● имя — подробность» в две колонки, если широко.</summary>
internal sealed class StatusGrid : Control
{
    public sealed record Row(string Name, Health State, string Detail);
    private List<Row> _rows = [];

    public StatusGrid()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Theme.Body;
    }

    public void SetRows(List<Row> rows) { _rows = rows; Invalidate(); }

    private int Cols(int width) => width >= 720 ? 2 : 1;

    public override Size GetPreferredSize(Size proposed)
    {
        var w = proposed.Width > 0 ? proposed.Width : Width;
        var cols = Cols(w);
        return new Size(w, Math.Max(1, (_rows.Count + cols - 1) / cols) * 34);
    }

    public static Color ColorOf(Health h) => h switch { Health.Ok => Theme.Green, Health.Warn => Theme.Amber, Health.Bad => Theme.Red, _ => Theme.Dim };

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Card);
        var cols = Cols(Width);
        var colW = Width / cols;
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var x = i % cols * colW;
            var y = i / cols * 34;
            var c = ColorOf(row.State);
            using (var glow = new SolidBrush(Color.FromArgb(50, c))) g.FillEllipse(glow, x + 1, y + 8, 18, 18);
            using (var dot = new SolidBrush(c)) g.FillEllipse(dot, x + 5, y + 12, 10, 10);
            TextRenderer.DrawText(g, row.Name, Theme.Bold, new Rectangle(x + 26, y, 170, 34), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, row.Detail, Theme.Small, new Rectangle(x + 196, y, colW - 206, 34), Theme.Dim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }
    }
}

/// <summary>Квоты моделей AG Tools: имя, на скольких аккаунтах жива, полоска лучшего процента.</summary>
internal sealed class QuotaView : Control
{
    public sealed record Row(string Model, int Live, int Total, int Best);
    private List<Row> _rows = [];
    public string Empty { get; set; } = "квот нет";

    public QuotaView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    public void SetRows(List<Row> rows) { _rows = rows; Invalidate(); }

    public override Size GetPreferredSize(Size proposed) =>
        new(proposed.Width > 0 ? proposed.Width : Width, Math.Max(1, _rows.Count) * 28 + 4);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Card);
        if (_rows.Count == 0)
        {
            TextRenderer.DrawText(g, Empty, Theme.Body, new Point(0, 4), Theme.Dim, TextFormatFlags.NoPrefix);
            return;
        }
        var nameW = Math.Min(300, Width / 3);
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            var y = i * 28;
            var lonely = r.Total <= 1;
            TextRenderer.DrawText(g, r.Model, Theme.Body, new Rectangle(0, y, nameW, 28), r.Live == 0 ? Theme.Dim : Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            var acc = $"{r.Live}/{r.Total} акк{(lonely ? "  ⚠ одна" : "")}";
            TextRenderer.DrawText(g, acc, Theme.Small, new Rectangle(nameW + 8, y, 120, 28), lonely ? Theme.Amber : Theme.Dim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            var barX = nameW + 132;
            var barW = Math.Max(60, Width - barX - 56);
            using (var track = Theme.Round(new Rectangle(barX, y + 10, barW, 8), 4))
            using (var tb = new SolidBrush(Theme.CardHi)) g.FillPath(tb, track);
            if (r.Best > 0)
            {
                var c = r.Best >= 50 ? Theme.Green : r.Best >= 15 ? Theme.Amber : Theme.Red;
                using var fillPath = Theme.Round(new Rectangle(barX, y + 10, Math.Max(8, barW * r.Best / 100), 8), 4);
                using var fb = new SolidBrush(c);
                g.FillPath(fb, fillPath);
            }
            TextRenderer.DrawText(g, $"{r.Best}%", Theme.Small, new Rectangle(barX + barW + 6, y, 50, 28), Theme.Dim,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }
}

/// <summary>Поле ввода в скруглённой рамке (у TextBox своей рамки на тёмном нет).</summary>
internal sealed class InputBox : Panel
{
    public TextBox Box { get; } = new()
    {
        BorderStyle = BorderStyle.None, BackColor = Theme.CardHi, ForeColor = Theme.Text, Font = new Font("Segoe UI", 11f),
    };

    public InputBox(string placeholder)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Height = 42;
        Padding = new Padding(16, 11, 16, 8);
        Box.PlaceholderText = placeholder;
        Box.Dock = DockStyle.Fill;
        Controls.Add(Box);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Parent?.BackColor ?? Theme.Card);
        using var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 12);
        using (var b = new SolidBrush(Theme.CardHi)) g.FillPath(b, path);
        using (var pen = new Pen(Box.Focused ? Theme.Accent : Theme.Line, Box.Focused ? 2f : 1f)) g.DrawPath(pen, path);
    }
}
