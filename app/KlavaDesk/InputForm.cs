namespace KlavaDesk;

/// <summary>Маленькое поле «Написать Клаве»: Enter — отправить, Esc — закрыть. Русский ввод и IME работают как в любом TextBox.</summary>
internal sealed class InputForm : Form
{
    public InputForm(Action<string> send)
    {
        Text = "Клаве";
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 11f);
        ClientSize = new Size(380, 40);

        var box = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "напиши Клаве и нажми Enter…" };
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
            else if (e.KeyCode == Keys.Enter && box.Text.Trim().Length > 0)
            {
                e.SuppressKeyPress = true;
                send(box.Text.Trim());
                box.Clear();
                Close();
            }
        };
        Controls.Add(box);
        Padding = new Padding(6);
        Shown += (_, _) => box.Focus();
        Deactivate += (_, _) => { if (box.Text.Length == 0) Close(); };
    }
}
