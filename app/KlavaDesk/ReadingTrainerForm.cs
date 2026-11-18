using System.Text;
using NAudio.Wave;

namespace KlavaDesk;

/// <summary>
/// Хардкорная тренировка голоса: человек 5–15 минут читает вслух страницы (встроенные отрывки или своя книга — txt,
/// fb2, epub, docx…, она сама режется на ~20 страниц с ударениями нейросети RUAccent, стрелками интонации и пометками
/// «громче/тише»), запись идёт непрерывно. «Хватит — сохранить и обучить» можно нажать в любой момент: запись
/// сохраняется на диск, режется на куски, в профиль уходят только куски ЕГО голоса (чужие голоса, эхо Клавы и тишина
/// выкидываются), потом слух переобучается на всех профилях и сам проверяет, стало ли точнее.
/// Пока идёт запись, Клава не слушает (иначе отвечала бы на читаемый текст).
/// </summary>
internal sealed class ReadingTrainerForm : Form
{
    private const int Rate = 16000;
    private const int MaxMinutes = 15;
    /// <summary>Меньше — обучать нечем: только сохранить запись.</summary>
    private const double MinTrainSpeech = 20;
    /// <summary>Меньше — «начать заново» опасно (старый профиль лучше новой минуты): куски добавляются к старым.</summary>
    private const double MinReplaceSpeech = 150;

    private readonly KlavaConfig _cfg;
    private readonly Speaker _speaker;
    private readonly string _name;
    private readonly RichTextBox _text = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 19f),
        BackColor = Color.FromArgb(252, 250, 244), ForeColor = Color.FromArgb(30, 30, 36), ScrollBars = RichTextBoxScrollBars.Vertical,
        DetectUrls = false,
    };
    private readonly Label _pageLabel = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(4, 10, 12, 0) };
    private readonly ComboBox _goal = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly CheckBox _replace = new() { AutoSize = true, Margin = new Padding(12, 8, 4, 0) };
    private readonly ComboBox _books = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330, Margin = new Padding(4, 5, 4, 0) };
    private readonly Button _book = new() { Text = "📂 Загрузить книгу…", AutoSize = true, Padding = new Padding(8, 3, 8, 3) };
    private readonly CheckBox _stress = new() { Text = "Ударения", AutoSize = true, Checked = true, Margin = new Padding(12, 8, 4, 0) };
    private readonly Button _export = new() { Text = "📄 Текстом", AutoSize = true, Padding = new Padding(6, 3, 6, 3) };
    private readonly Label _bookStatus = new() { AutoSize = true, ForeColor = Color.FromArgb(90, 90, 100), Margin = new Padding(8, 9, 4, 0) };
    private readonly Button _start = new() { Text = "▶ Начать чтение", AutoSize = true, Padding = new Padding(10, 5, 10, 5), BackColor = Color.FromArgb(210, 245, 215) };
    private readonly Button _prev = new() { Text = "◀ Назад", AutoSize = true, Padding = new Padding(8, 5, 8, 5) };
    private readonly Button _next = new() { Text = "Следующая страница ▶", AutoSize = true, Padding = new Padding(8, 5, 8, 5) };
    private readonly Button _finish = new() { Text = "🏁 Хватит — сохранить и обучить", AutoSize = true, Padding = new Padding(10, 5, 10, 5), Enabled = false, BackColor = Color.FromArgb(255, 235, 200) };
    private readonly LevelBar _level = new() { Height = 14, Dock = DockStyle.Top };
    private readonly ProgressBar _progress = new() { Height = 12, Dock = DockStyle.Top, Maximum = 1000 };
    private readonly Label _stats = new() { Dock = DockStyle.Top, Height = 26, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) };
    private readonly System.Windows.Forms.Timer _ui = new() { Interval = 250 };
    private readonly ToolTip _tips = new();
    private readonly CancellationTokenSource _life = new();

    private ReadingBook _current = ReadingBooks.BuiltinPlain();
    private int _index;
    private bool _bookBusy;
    private bool _fillingBooks;
    private WaveIn? _in;
    private readonly MemoryStream _pcm = new();
    private bool _paused = true;
    private bool _busy;
    private double _recordedSec, _speechSec, _noise = 0.004, _lastRms;

    /// <summary>Не слушать микрофон Клавой, пока идёт чтение (ставит окно профилей → OverlayForm).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<bool>? PauseListening { get; set; }

    /// <summary>Профиль записан и слух переобучен — окну профилей и Клаве обновиться.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? Done { get; set; }

    public ReadingTrainerForm(KlavaConfig cfg, Speaker speaker, string name, bool profileExists)
    {
        _cfg = cfg;
        _speaker = speaker;
        _name = name;
        Text = $"Хардкорная тренировка голоса — {name}";
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10f);
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        ClientSize = new Size(Math.Min(1040, wa.Width - 40), Math.Min(720, wa.Height - 60));
        MinimumSize = new Size(Math.Min(760, wa.Width), Math.Min(540, wa.Height));
        KeyPreview = true;

        _goal.Items.AddRange(["5 минут", "10 минут", "15 минут"]);
        _goal.SelectedIndex = 1;
        _replace.Text = profileExists ? "Начать профиль заново (старые записи — в карантин, их можно вернуть)" : "Новый профиль";
        _replace.Checked = true;
        _replace.Enabled = profileExists;

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8, 6, 8, 0) };
        top.Controls.Add(new Label { Text = $"Читает: {name}", AutoSize = true, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(4, 8, 16, 0) });
        top.Controls.Add(new Label { Text = "Цель:", AutoSize = true, Margin = new Padding(4, 9, 4, 0) });
        top.Controls.Add(_goal);
        top.Controls.Add(_replace);

        var bookRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8, 0, 8, 2) };
        bookRow.Controls.Add(new Label { Text = "Текст:", AutoSize = true, Margin = new Padding(4, 9, 0, 0) });
        bookRow.Controls.Add(_books);
        bookRow.Controls.Add(_book);
        bookRow.Controls.Add(_stress);
        bookRow.Controls.Add(_export);
        bookRow.Controls.Add(_bookStatus);

        var hint = new Label
        {
            Dock = DockStyle.Top, Height = 46, Padding = new Padding(10, 2, 10, 2), ForeColor = Color.FromArgb(70, 70, 80),
            Text = "Читай вслух, как разговариваешь. Красная гласная — ударение (ё всегда ударная); ↗ — голос вверх на этом слове (вопрос, " +
                   "перечисление); ↘ — нажим и вниз («кто? где?», восклицание). (Оранжевым) — как читать. Сбился — читай дальше. → / ← — листать.",
        };

        var textPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(18, 10, 18, 10), BackColor = _text.BackColor };
        textPanel.Controls.Add(_text);

        var meters = new Panel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(10, 4, 10, 4) };
        meters.Controls.Add(_stats);
        meters.Controls.Add(_progress);
        meters.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 4 });
        meters.Controls.Add(_level);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(8, 4, 8, 8) };
        bottom.Controls.Add(_start);
        bottom.Controls.Add(_prev);
        bottom.Controls.Add(_next);
        bottom.Controls.Add(_pageLabel);
        bottom.Controls.Add(_finish);

        Controls.Add(textPanel);
        Controls.Add(hint);
        Controls.Add(bookRow);
        Controls.Add(top);
        Controls.Add(meters);
        Controls.Add(bottom);

        _tips.SetToolTip(_book, "Книга в txt, fb2, fb2.zip, epub, docx, odt, rtf или html. Она сама разберётся на ~20 страниц:\n" +
                                "лучшие куски по всей книге, ударения нейросетью, интонация и «громче/тише». Второй раз — мгновенно.");
        _tips.SetToolTip(_finish, "Устал — жми в любой момент: запись сохранится на диск, и профиль обучится на том, что уже прочитано.");
        _tips.SetToolTip(_export, "Страницы с разметкой — текстовым файлом (распечатать или читать с телефона).");

        _start.Click += (_, _) => TogglePause();
        _next.Click += (_, _) => ShowPage(_index + 1);
        _prev.Click += (_, _) => ShowPage(_index - 1);
        _finish.Click += (_, _) => _ = FinishAsync();
        _book.Click += (_, _) => _ = PickBookAsync();
        _stress.CheckedChanged += (_, _) => ShowPage(_index);
        _export.Click += (_, _) => ExportCurrent();
        _books.SelectedIndexChanged += (_, _) => { if (!_fillingBooks) _ = SelectBookAsync(); };
        _ui.Tick += (_, _) => UpdateStats();
        KeyDown += (_, e) =>
        {
            if (_busy || _books.Focused || _goal.Focused) return;
            if (e.KeyCode is Keys.Right or Keys.PageDown) { ShowPage(_index + 1); e.Handled = true; }
            else if (e.KeyCode is Keys.Left or Keys.PageUp) { ShowPage(_index - 1); e.Handled = true; }
        };
        FormClosing += OnClosing;
        FillBooks(ReadingBooks.LastChoice(_cfg));
        ShowPage(0);
        UpdateStats();
        Shown += (_, _) => _ = SelectBookAsync();
    }

    private double GoalSec => (_goal.SelectedIndex switch { 0 => 5, 2 => 15, _ => 10 }) * 60;

    // ---------------- книги ----------------

    private sealed record BookItem(string Id, string Title, ReadingBook? Book)
    {
        public override string ToString() => Title;
    }

    private void FillBooks(string? select)
    {
        _fillingBooks = true;
        _books.Items.Clear();
        _books.Items.Add(new BookItem(ReadingBooks.BuiltinId, $"Встроенные отрывки ({ReadingTexts.Builtin.Length})", null));
        foreach (var b in ReadingBooks.Library(_cfg)) _books.Items.Add(new BookItem(b.Id, $"{b.Title} — {b.Pages.Count} стр.", b));
        var idx = 0;
        for (var i = 0; i < _books.Items.Count; i++) if (((BookItem)_books.Items[i]!).Id == select) idx = i;
        _books.SelectedIndex = idx;
        _fillingBooks = false;
    }

    private async Task SelectBookAsync()
    {
        if (_books.SelectedItem is not BookItem item || _bookBusy) return;
        ReadingBooks.SaveLastChoice(_cfg, item.Id);
        if (item.Book is { } b) { SetBook(b); return; }
        // встроенные: сразу как есть, ударения досчитываются в фоне
        if (_current.Id != ReadingBooks.BuiltinId) SetBook(ReadingBooks.BuiltinPlain());
        if (!string.IsNullOrEmpty(_current.AccentBy)) return;
        _bookBusy = true;
        try
        {
            var built = await ReadingBooks.BuiltinAsync(_cfg, new Progress<string>(s => { if (!IsDisposed && _current.Id == ReadingBooks.BuiltinId) _bookStatus.Text = s; }), _life.Token);
            if (!IsDisposed && _current.Id == ReadingBooks.BuiltinId) SetBook(built, keepPage: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException or HttpRequestException)
        {
            if (!IsDisposed) _bookStatus.Text = $"ударения не расставились: {ex.Message}";
        }
        finally { _bookBusy = false; }
    }

    private void SetBook(ReadingBook b, bool keepPage = false)
    {
        _current = b;
        ShowPage(keepPage ? _index : 0);
        _bookStatus.Text = string.IsNullOrEmpty(b.AccentBy) ? "" : $"ударения: {b.AccentBy}";
        _export.Enabled = b.Id != ReadingBooks.BuiltinId || !string.IsNullOrEmpty(b.AccentBy);
    }

    private async Task PickBookAsync()
    {
        if (_bookBusy) return;
        using var dlg = new OpenFileDialog { Filter = BookFormats.DialogFilter, Title = "Книга для чтения вслух" };
        var books = ReadingBooks.BooksDir(_cfg);
        if (Directory.Exists(books)) dlg.InitialDirectory = books;
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await LoadBookAsync(dlg.FileName);
    }

    /// <summary>Разобрать книгу в фоне: окно живое, читать встроенные можно и пока она разбирается.</summary>
    internal async Task<bool> LoadBookAsync(string path)
    {
        _bookBusy = true;
        _book.Enabled = _books.Enabled = false;
        var progress = new Progress<string>(s => { if (!IsDisposed) _bookStatus.Text = s; });
        try
        {
            var b = await ReadingBooks.BuildAsync(_cfg, path, ReadingBooks.DefaultPages, force: false, progress, _life.Token);
            if (IsDisposed) return true;
            ReadingBooks.SaveLastChoice(_cfg, b.Id);
            _bookBusy = false;
            FillBooks(b.Id);
            SetBook(b);
            _bookStatus.Text = $"{b.Pages.Count} стр. из {b.Candidates} кусков книги · ударения: {b.AccentBy}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException
                                       or System.Text.Json.JsonException or ArgumentException or OperationCanceledException)
        {
            if (IsDisposed) return false;
            _bookStatus.Text = "";
            MessageBox.Show(this, $"Книга не разобралась: {ex.Message}", "Своя книга", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        finally
        {
            _bookBusy = false;
            if (!IsDisposed) _book.Enabled = _books.Enabled = true;
        }
    }

    private void ExportCurrent()
    {
        try
        {
            var file = ReadingBooks.Export(_cfg, _current);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"Не сохранилось: {ex.Message}", "Страницы текстом");
        }
    }

    /// <summary>Показать страницу: крупно, с ударениями, стрелками и пометками цветом.</summary>
    private void ShowPage(int i)
    {
        var pages = _current.Pages;
        if (pages.Count == 0) return;
        _index = (i % pages.Count + pages.Count) % pages.Count;
        _text.Rtf = ReadingRtf.Build(pages[_index].Marked, _stress.Checked, pages[_index].Verse);
        _text.Select(0, 0);
        _pageLabel.Text = $"Страница {_index + 1} из {pages.Count}";
    }

    // ---------------- запись ----------------

    private void TogglePause()
    {
        if (_busy) return;
        if (_in is null && !StartMic()) return;
        _paused = !_paused;
        _start.Text = _paused ? "▶ Продолжить" : "⏸ Пауза";
        _start.BackColor = _paused ? Color.FromArgb(210, 245, 215) : Color.FromArgb(255, 225, 225);
        _goal.Enabled = false;
        _replace.Enabled = false;
        _finish.Enabled = true;
        _text.Focus();
    }

    private bool StartMic()
    {
        try
        {
            PauseListening?.Invoke(true);
            _in = new WaveIn { DeviceNumber = Ears.ResolveDevice(_cfg), WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 100, NumberOfBuffers = 4 };
            _in.DataAvailable += OnData;
            _in.StartRecording();
            _ui.Start();
            return true;
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException)
        {
            PauseListening?.Invoke(false);
            _in?.Dispose();
            _in = null;
            MessageBox.Show(this, $"Микрофон не открылся: {ex.Message}", "Тренировка");
            return false;
        }
    }

    /// <summary>Поток записи: копим звук (не на паузе), считаем громкость и «сколько было речи» по порогу над фоном.</summary>
    private void OnData(object? sender, WaveInEventArgs e)
    {
        double sum = 0;
        var n = e.BytesRecorded / 2;
        for (var i = 0; i + 1 < e.BytesRecorded; i += 2)
        {
            var s = BitConverter.ToInt16(e.Buffer, i) / 32768.0;
            sum += s * s;
        }
        // уровень — с тем же усилением, что у слуха Клавы (MicGain): тихий микрофон иначе «не говорил бы» вовсе;
        // пишется сырой звук — отпечаток голоса громкость всё равно выравнивает
        var rms = n > 0 ? Math.Sqrt(sum / n) * Math.Max(1, _cfg.MicGain) : 0;
        var sec = n / (double)Rate;
        lock (_pcm)
        {
            _lastRms = rms;
            if (_paused) return;
            _pcm.Write(e.Buffer, 0, e.BytesRecorded);
            _recordedSec += sec;
            var voiced = rms > Math.Max(0.008, _noise * 3);
            if (voiced) _speechSec += sec;
            else _noise = _noise * 0.95 + rms * 0.05;
        }
    }

    private void UpdateStats()
    {
        double rec, speech, rms;
        lock (_pcm) { rec = _recordedSec; speech = _speechSec; rms = _lastRms; }
        _level.Value = Math.Min(1, rms * 6);
        _progress.Value = (int)Math.Clamp(speech / GoalSec * 1000, 0, 1000);
        static string T(double s) => $"{(int)s / 60}:{(int)s % 60:00}";
        if (_busy) return;
        _stats.Text = _in is null
            ? $"Цель — {T(GoalSec)} живой речи. Жми «Начать чтение» и читай с экрана. Устанешь раньше — «Хватит», обучу на том, что есть."
            : $"{(_paused ? "⏸ пауза" : "🎙 пишу")} · запись {T(rec)} · речи {T(speech)} из {T(GoalSec)}" +
              (speech >= GoalSec ? " · ✔ цель есть — дочитай страницу и жми «Хватит»" : speech >= MinTrainSpeech ? " · устал — жми «Хватит»" : "") +
              (rms < 0.004 && !_paused ? " · 🔇 тишина — микрофон слышит?" : "");
        if (rec >= MaxMinutes * 60 && !_paused) { TogglePause(); _ = FinishAsync(); }
    }

    private void StopMic()
    {
        _ui.Stop();
        try { _in?.StopRecording(); } catch (NAudio.MmException) { }
        _in?.Dispose();
        _in = null;
        PauseListening?.Invoke(false);
    }

    /// <summary>Запись чтения — на диск (data\speakers\_записи_чтения): обучение можно повторить, запись не пропадёт.</summary>
    private string? SaveRecording(byte[] wav)
    {
        try
        {
            var dir = _cfg.Abs(@"data\speakers\_записи_чтения");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"{LiteSpeakers.Sanitize(_name)}_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
            File.WriteAllBytes(file, wav);
            Log.Write($"чтение: запись {_name} сохранена — {file}");
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error("чтение: сохранить запись", ex);
            return null;
        }
    }

    /// <summary>«Хватит»: в любой момент — сохранить запись и обучить на том, что прочитано.</summary>
    private async Task FinishAsync()
    {
        if (_busy) return;
        double speech;
        lock (_pcm) speech = _speechSec;
        if (speech < MinTrainSpeech)
        {
            var r0 = MessageBox.Show(this, $"Речи всего {(int)speech} с — обучать на этом нечем (нужно хотя бы {MinTrainSpeech:F0} с, лучше 3–5 минут).\n\n" +
                                         "Да — продолжить чтение.\nНет — сохранить запись на диск и закончить без обучения.",
                "Тренировка", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r0 == DialogResult.Yes)
            {
                if (_paused && _in is not null) TogglePause();
                return;
            }
        }
        _busy = true;
        _paused = true;
        StopMic();
        foreach (var c in new Control[] { _start, _next, _prev, _finish, _book, _books }) c.Enabled = false;
        byte[] wav;
        lock (_pcm) wav = Lite.ToWav(ToFloat(_pcm.ToArray()), Rate);
        var saved = SaveRecording(wav);
        var savedNote = saved is null ? "Запись на диск не сохранилась (см. журнал)." : $"Запись сохранена: {saved}";
        if (speech < MinTrainSpeech)
        {
            ShowReport($"Обучение не запускалось: речи {(int)speech} с.\n\n{savedNote}");
            _stats.Text = "Готово. Окно можно закрыть.";
            _busy = false;
            return;
        }
        var replace = _replace.Checked && speech >= MinReplaceSpeech;
        var note = _replace.Checked && !replace
            ? $"Речи {(int)speech} с — меньше {MinReplaceSpeech / 60:F1} мин, поэтому старый профиль НЕ заменён: новые куски добавлены к нему.\n"
            : "";
        var progress = new Progress<string>(s => _stats.Text = s);
        _stats.Text = "Разбираю запись: режу на куски, отбираю твой голос…";
        var r = await _speaker.LongEnrollAsync(_name, wav, replace: replace, progress);
        var report = (r.Ok ? "✔ " : "✖ ") + note + r.Report;
        if (r.Ok)
        {
            _stats.Text = "Переобучаю слух на всех профилях и проверяю, стало ли точнее…";
            var (ok, earReport) = await _speaker.TrainEarAsync(progress);
            report += "\n\n" + (ok ? earReport : "Слух не переобучен: " + earReport);
            Done?.Invoke();
        }
        ShowReport(report + "\n\n" + savedNote);
        _stats.Text = r.Ok ? "Готово. Окно можно закрыть." : "Не получилось — прочитай ещё раз, ближе к микрофону и без посторонних голосов.";
        _busy = false;
    }

    private void ShowReport(string text)
    {
        _text.Clear();
        _text.Font = new Font("Segoe UI", 12f);
        _text.Text = text;
    }

    // ---- для проверки без человека у микрофона (KlavaDesk.exe --ui-shot reading-rec | reading-full | reading-book) ----

    /// <summary>Подложить готовую запись вместо микрофона: дальше «Хватит» разбирает её как настоящее чтение.</summary>
    internal void InjectForTest(float[] audio)
    {
        var pcm = new byte[audio.Length * 2];
        for (var i = 0; i < audio.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)Math.Clamp(audio[i] * 32767f, short.MinValue, short.MaxValue));
        lock (_pcm)
        {
            _pcm.SetLength(0);
            _pcm.Write(pcm);
            _recordedSec = _speechSec = audio.Length / (double)Rate;
        }
        _finish.Enabled = true;
    }

    internal Task FinishForTestAsync() => FinishAsync();
    internal void StartForTest() => _start.PerformClick();
    internal (double Recorded, double Speech, double Rms) StatsForTest() { lock (_pcm) return (_recordedSec, _speechSec, _lastRms); }
    internal string ReportForTest => _text.Text;
    internal void ShowPageForTest(int i) => ShowPage(i);
    internal ReadingBook BookForTest => _current;

    private static float[] ToFloat(byte[] pcm)
    {
        var a = new float[pcm.Length / 2];
        for (var i = 0; i < a.Length; i++) a[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        return a;
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_busy) { e.Cancel = true; return; }
        double rec;
        lock (_pcm) rec = _recordedSec;
        if (_in is not null && rec > 10)
        {
            var r = MessageBox.Show(this, "Запись не обработана. Сохранить и обучить на ней перед закрытием?\n\nДа — «Хватит»: сохранить и обучить.\nНет — закрыть, запись пропадёт.",
                "Тренировка", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Cancel) { e.Cancel = true; return; }
            if (r == DialogResult.Yes) { e.Cancel = true; _ = FinishAsync(); return; }
        }
        _life.Cancel();
        StopMic();
        _ui.Dispose();
    }

    /// <summary>Полоска громкости: зелёная — хорошо, жёлтая — громко, красная — перегруз.</summary>
    private sealed class LevelBar : Control
    {
        private double _value;
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double Value { get => _value; set { _value = value; Invalidate(); } }

        public LevelBar()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.FromArgb(230, 230, 235));
            var w = (int)(Width * Math.Clamp(_value, 0, 1));
            var c = _value > 0.9 ? Color.IndianRed : _value > 0.7 ? Color.Goldenrod : Color.MediumSeaGreen;
            using var b = new SolidBrush(c);
            e.Graphics.FillRectangle(b, 0, 0, w, Height);
        }
    }
}

/// <summary>
/// Разметка страницы → RTF для RichTextBox одним присваиванием (раньше — сотни Select/SelectionColor, медленно):
/// ударная гласная — красная жирная (сам знак ́ RichEdit рисует со сдвигом на соседнюю букву, поэтому на экране его
/// нет; в «Текстом» он есть), слово после ↗ — синее, после ↘ — фиолетовое, [как читать] — оранжевым курсивом.
/// </summary>
internal static class ReadingRtf
{
    /// <param name="verse">Стихи: перенос — новая строка того же абзаца (без отступа между строками).</param>
    public static string Build(string marked, bool stress, bool verse = false)
    {
        var sb = new StringBuilder(marked.Length * 3 + 400);
        sb.Append(@"{\rtf1\ansi\ansicpg1251\deff0\uc1{\fonttbl{\f0\fswiss Segoe UI;}}");
        sb.Append(@"{\colortbl ;\red30\green30\blue36;\red196\green96\blue24;\red205\green30\blue50;\red25\green95\blue215;\red135\green40\blue175;}");
        sb.Append(@"\pard\sa200\sl330\slmult1\f0\fs38\cf1 ");
        var i = 0;
        var inWord = false;   // внутри слова после стрелки — его группа ещё открыта
        while (i < marked.Length)
        {
            var c = marked[i];
            if (inWord && !ReadingProsody.IsCyr(c) && c != ReadingProsody.Acute && c != '-')
            {
                sb.Append('}');
                inWord = false;
            }
            if (c == '[')
            {
                var close = marked.IndexOf(']', i);
                var nl = marked.IndexOf('\n', i);
                if (close > i && (nl < 0 || close < nl))
                {
                    var inner = marked[(i + 1)..close];
                    if (inner.StartsWith("читать:", StringComparison.Ordinal))
                        sb.Append(@"{\cf2\i\fs28 ").Append(Esc("▸ " + inner)).Append('}');
                    else
                        sb.Append(@"{\cf2\i\fs30 ").Append(Esc("(" + inner + ")")).Append('}');
                    i = close + 1;
                    continue;
                }
            }
            if (c == '\n')
            {
                // у стихов первая строка — «читать: …», дальше строки стиха через \line
                sb.Append(verse && i > 0 && marked.LastIndexOf('\n', i - 1) >= 0 ? "\\line\n" : "\\par\n");
                i++;
                continue;
            }
            if (c is ReadingProsody.Rise or ReadingProsody.Fall)
            {
                sb.Append(c == ReadingProsody.Rise ? @"{\cf4\b " : @"{\cf5\b ").Append(Esc(c.ToString()));
                inWord = true;
                i++;
                continue;
            }
            var stressed = i + 1 < marked.Length && marked[i + 1] == ReadingProsody.Acute;
            if (stress && (stressed || c is 'ё' or 'Ё'))
            {
                sb.Append(@"{\cf3\b ").Append(Esc(c.ToString())).Append('}');
                i += stressed ? 2 : 1;
                continue;
            }
            if (c == ReadingProsody.Acute) { i++; continue; }   // ударения выключены
            sb.Append(Esc(c.ToString()));
            i++;
        }
        if (inWord) sb.Append('}');
        sb.Append('}');
        return sb.ToString();
    }

    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length * 2);
        foreach (var c in s)
        {
            if (c is '\\' or '{' or '}') sb.Append('\\').Append(c);
            else if (c < 128) sb.Append(c);
            else sb.Append("\\u").Append((short)c).Append('?');
        }
        return sb.ToString();
    }
}
