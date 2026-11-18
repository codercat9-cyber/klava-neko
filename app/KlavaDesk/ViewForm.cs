namespace KlavaDesk;

/// <summary>
/// «Настройки вида»: размер Клавы, приближение и поворот — ползунками, с живым предпросмотром.
/// Значения пишутся прямо в конфиг; окно Клавы применяет их через <c>apply</c>.
/// </summary>
internal sealed class ViewForm : Form
{
    private readonly KlavaConfig _cfg;
    private readonly Action _apply;
    private readonly TrackBar _size, _zoom, _yaw;
    private readonly Label _sizeVal, _zoomVal, _yawVal;
    private bool _loading;

    public ViewForm(KlavaConfig cfg, Action apply)
    {
        _cfg = cfg;
        _apply = apply;
        Text = "Клава — вид";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(380, 250);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4, Padding = new Padding(10) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        for (var i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        (_size, _sizeVal) = AddRow(grid, 0, "Размер", 40, 160, 10);
        (_zoom, _zoomVal) = AddRow(grid, 1, "Приближение", 0, 100, 10);
        (_yaw, _yawVal) = AddRow(grid, 2, "Поворот", -180, 180, 30);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var close = new Button { Text = "Готово", AutoSize = true };
        close.Click += (_, _) => Close();
        var reset = new Button { Text = "Сбросить", AutoSize = true };
        reset.Click += (_, _) => { _cfg.WindowScale = 1; _cfg.CameraZoom = 0; _cfg.ModelYaw = 0; LoadValues(); _apply(); };
        var hint = new Label
        {
            Text = "Мышью: колесо — приближение, Ctrl+колесо — размер, Shift+колесо — поворот.",
            AutoSize = true, MaximumSize = new Size(360, 0), ForeColor = SystemColors.GrayText,
        };
        buttons.Controls.AddRange([close, reset]);
        var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2 };
        bottom.Controls.Add(hint, 0, 0);
        bottom.Controls.Add(buttons, 0, 1);
        grid.Controls.Add(bottom, 0, 3);
        grid.SetColumnSpan(bottom, 3);
        Controls.Add(grid);

        LoadValues();
        _size.ValueChanged += (_, _) => { if (_loading) return; _cfg.WindowScale = _size.Value / 100.0; ShowValues(); _apply(); };
        _zoom.ValueChanged += (_, _) => { if (_loading) return; _cfg.CameraZoom = _zoom.Value / 100.0; ShowValues(); _apply(); };
        _yaw.ValueChanged += (_, _) => { if (_loading) return; _cfg.ModelYaw = _yaw.Value; ShowValues(); _apply(); };
        FormClosed += (_, _) => _cfg.Save();
    }

    /// <summary>Ползунки подтянуть к конфигу (после колеса мыши или «Сбросить»).</summary>
    public void LoadValues()
    {
        _loading = true;
        _size.Value = Math.Clamp((int)Math.Round(_cfg.WindowScale * 100), _size.Minimum, _size.Maximum);
        _zoom.Value = Math.Clamp((int)Math.Round(_cfg.CameraZoom * 100), _zoom.Minimum, _zoom.Maximum);
        _yaw.Value = Math.Clamp((int)Math.Round(_cfg.ModelYaw), _yaw.Minimum, _yaw.Maximum);
        _loading = false;
        ShowValues();
    }

    /// <summary>
    /// Подписи у ползунков. Раньше метод звался Show() и сам показывал окно — ещё из конструктора, без владельца;
    /// потом OverlayForm звал Show(this) у уже видимой формы, и WinForms падал «ОШИБКА UI … нельзя отобразить».
    /// </summary>
    private void ShowValues()
    {
        _sizeVal.Text = $"{_size.Value} %";
        _zoomVal.Text = $"{_zoom.Value} %";
        _yawVal.Text = $"{_yaw.Value}°";
    }

    private static (TrackBar, Label) AddRow(TableLayoutPanel grid, int row, string name, int min, int max, int tick)
    {
        var label = new Label { Text = name, Anchor = AnchorStyles.Left, AutoSize = true };
        var bar = new TrackBar { Minimum = min, Maximum = max, TickFrequency = tick, SmallChange = 1, LargeChange = tick, Dock = DockStyle.Fill };
        var val = new Label { Anchor = AnchorStyles.Left, AutoSize = true };
        grid.Controls.Add(label, 0, row);
        grid.Controls.Add(bar, 1, row);
        grid.Controls.Add(val, 2, row);
        return (bar, val);
    }
}
