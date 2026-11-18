using System.Buffers.Binary;
using System.Text;
using NAudio.Wave;

namespace KlavaDesk;

/// <summary>
/// Панель голосовых профилей: список записанных (Лис/Сова/Кот/…), запись профиля (короткая — 10 с, долгая — до 15 минут
/// разговора), доучивание, проверка «кто сейчас говорит», проверка профилей на чужие записи с карантином.
/// Пока здесь идёт запись, Клава не слушает (иначе она отвечала бы на то, что человек наговаривает в профиль).
/// </summary>
internal sealed class VoiceProfilesForm : Form
{
    private const int RecordSeconds = 10;
    private const int LongMaxMinutes = 15;
    private const int Rate = 16000;

    private readonly Speaker _speaker;
    private readonly KlavaConfig _cfg;
    private readonly ListBox _list = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10.5f), IntegralHeight = false };
    private readonly TextBox _nameBox = new() { Width = 220, Font = new Font("Segoe UI", 10.5f), Margin = new Padding(4, 5, 12, 4) };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 52, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0) };
    private readonly Label _ear = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 40, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 8, 0), ForeColor = Color.FromArgb(40, 90, 60) };
    private readonly Button _longBtn = new() { Text = $"🎙 Свободная речь (до {LongMaxMinutes} мин)", AutoSize = true, Padding = new Padding(8, 4, 8, 4), BackColor = Color.FromArgb(215, 240, 255) };
    private readonly ToolTip _tips = new() { AutoPopDelay = 15000 };
    private List<SpeakerProfile> _profiles = [];
    private WaveIn? _recorder;
    private MemoryStream? _recBuf;
    private System.Windows.Forms.Timer? _countdown;
    private TaskCompletionSource? _longStop;
    private ReadingTrainerForm? _reading;

    /// <summary>Не слушать микрофон Клавой, пока здесь идёт запись (ставит OverlayForm).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<bool>? PauseListening { get; set; }

    /// <summary>Профили или слух изменились — Клава берёт новые пороги «свой» без перезапуска (ставит OverlayForm).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action? ProfilesChanged { get; set; }

    private Button Btn(string text, string tip, Action click, Color? back = null)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(3) };
        if (back is { } c) b.BackColor = c;
        b.Click += (_, _) => click();
        _tips.SetToolTip(b, tip);
        return b;
    }

    public VoiceProfilesForm(KlavaConfig cfg, Speaker speaker)
    {
        _cfg = cfg;
        _speaker = speaker;
        Text = "Голосовые профили Клавы";
        Icon = null;
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(820, 600);
        MinimumSize = new Size(640, 480);
        Font = new Font("Segoe UI", 10f);

        // кнопки переносятся по строкам: при любой ширине окна ничего не уезжает за край
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(8, 8, 8, 4) };
        top.Controls.Add(new Label { Text = "Имя:", AutoSize = true, Margin = new Padding(4, 9, 2, 0) });
        top.Controls.Add(_nameBox);
        // друг 28.09: «кнопки добавления профиля нет — упущение»: раньше новичок заводился вписыванием имени в поле и записью,
        // а клик по профилю затирал поле его именем — догадаться было нельзя
        top.Controls.Add(Btn("➕ Новый человек", "Добавить человека: имя — и сразу записать его голос (чтение 5–10 мин или быстро 10 с).",
            () => _ = AddPersonAsync(), Color.FromArgb(220, 235, 255)));
        var reading = Btn("📖 Хардкорная тренировка — чтение 5–10 мин",
            "Самое точное: человек читает вслух отрывки с экрана 5–10 минут. В профиль уходят только куски его голоса, потом слух переобучается и сам проверяет, стало ли точнее.",
            OpenReading, Color.FromArgb(255, 226, 170));
        reading.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        top.Controls.Add(reading);
        top.SetFlowBreak(reading, true);
        top.Controls.Add(Btn($"🎙 Записать ({RecordSeconds} с)", "Быстрый профиль: 10 секунд речи.", () => _ = RecordAndEnrollAsync()));
        top.Controls.Add(Btn($"➕ Доучить выбранный ({RecordSeconds} с)", "Ещё одна запись в выбранный профиль (тихо, громко, издалека).", () => _ = RecordAndAppendAsync()));
        _longBtn.Click += (_, _) => { if (_longStop is not null) _longStop.TrySetResult(); else _ = LongRecordAsync(); };
        _tips.SetToolTip(_longBtn, "Говори что хочешь до 15 минут, второй раз нажми — хватит. Как чтение, только без текста.");
        top.Controls.Add(_longBtn);
        top.Controls.Add(Btn("🔥 До узнавания", "Короткие раунды по 4 с: не узнала — запись добавляется. До 3 узнаваний подряд.", () => _ = BatteryTrainAsync(), Color.FromArgb(255, 240, 210)));
        top.Controls.Add(Btn($"❓ Кто говорит ({RecordSeconds} с)", "Проверка: скажи что-нибудь — покажу, кого узнала и насколько уверенно.", () => _ = RecordAndIdentifyAsync()));
        top.Controls.Add(Btn("🧹 Проверить профили", "Найти в профилях чужие голоса, телевизор и эхо Клавы — и убрать в карантин.", () => _ = AuditAsync()));
        top.Controls.Add(Btn("🎓 Обучить слух", "Подстроить слух под эту комнату и этих людей по всем записям. Включится, только если стало точнее.", () => _ = TrainEarAsync(), Color.FromArgb(215, 245, 220)));

        var listPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(8, 4, 8, 4) };
        listPanel.Controls.Add(_list);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, WrapContents = true, Padding = new Padding(8, 2, 8, 2) };
        bottom.Controls.Add(Btn("🔄 Обновить", "Перечитать список.", () => _ = ReloadAsync()));
        bottom.Controls.Add(Btn("▶ Прослушать", "Послушать последнюю запись профиля.", () => _ = PlaySelectedAsync()));
        bottom.Controls.Add(Btn("✏ Переименовать", "Поменять имя, записи сохраняются.", () => _ = RenameSelectedAsync()));
        bottom.Controls.Add(Btn("🗑 Удалить", "Убрать профиль. Записи уедут в data\\speakers\\_карантин — их можно вернуть.", () => _ = DeleteSelectedAsync(), Color.FromArgb(255, 225, 225)));

        Controls.Add(listPanel);
        Controls.Add(top);
        Controls.Add(bottom);
        Controls.Add(_ear);
        Controls.Add(_status);
        _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _profiles.Count) _nameBox.Text = _profiles[_list.SelectedIndex].Name; };
        _list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Delete) _ = DeleteSelectedAsync(); };
        Shown += (_, _) => _ = ReloadAsync();
        FormClosed += (_, _) => { _longStop?.TrySetResult(); Cleanup(); PauseListening?.Invoke(false); _reading?.Close(); };
    }

    /// <summary>Имя выбранного в списке профиля (строки списка — в порядке _profiles).</summary>
    private string? SelectedName => _list.SelectedIndex >= 0 && _list.SelectedIndex < _profiles.Count ? _profiles[_list.SelectedIndex].Name : null;

    private void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(() => _status.Text = text); else _status.Text = text;
    }

    private async Task<List<SpeakerProfile>> ReloadAsync()
    {
        SetStatus("Загружаю список профилей…");
        if (!await _speaker.AliveAsync()) { SetStatus("Голосовые профили не отвечают (перезапусти Клаву)."); return []; }
        var profiles = (await _speaker.ListAsync()).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var info = await _speaker.InfoAsync();
        BeginInvoke(() =>
        {
            var keep = SelectedName;
            _profiles = profiles;
            _list.Items.Clear();
            foreach (var p in profiles)
            {
                var speech = p.Minutes > 0 ? $", речи {p.Minutes:F1} мин" : "";
                var hint = p.Name == "Клава" ? "  — её собственный голос (чтобы не слушать саму себя)"
                    : p.Minutes is > 0 and < 2 ? "  — мало, потренируй чтением" : "";
                _list.Items.Add($"{p.Name}   (записей {p.Samples}{speech}, обновлён {p.Updated:HH:mm dd.MM}){hint}");
            }
            if (keep is not null && profiles.FindIndex(p => p.Name == keep) is var i and >= 0) _list.SelectedIndex = i;
            _ear.Text = info is null ? ""
                : $"Слух: модель {info.Model}, порог «свой» {info.Threshold:F2}" +
                  (double.IsNaN(info.ShortThreshold) ? "" : $" (короткие фразы {info.ShortThreshold:F2})") +
                  (info.Ear.Length > 0 ? " · " + info.Ear.Split('\n').Skip(3).FirstOrDefault()?.Trim().TrimStart('→', ' ') : " · ещё не обучался — жми «Обучить слух» после тренировок");
        });
        SetStatus($"Профилей: {profiles.Count}. Точнее всего узнаёт после «Хардкорной тренировки»: 5–10 минут чтения на каждого, кто бывает в комнате.");
        return profiles;
    }

    /// <summary>
    /// Новый человек: имя → сразу запись голоса (хардкорное чтение или 10 с). Если в компании включён фильтр «слушать
    /// только этих», новичок добавляется в компанию (и число людей растёт) — иначе Клава его пропускала бы мимо ушей.
    /// </summary>
    private async Task AddPersonAsync()
    {
        var name = InputBox("Как зовут нового человека? Так Клава будет к нему обращаться (Лис, Сова, Чупачупс…):", "Новый человек", "");
        if (name is null) return;
        name = name.Trim();
        if (name.Length == 0) { SetStatus("Имя пустое — никого не добавила."); return; }
        if (name == "Клава") { SetStatus("Голос Клавы тренируется сам — из её синтеза. Впиши человека."); return; }
        var clean = LiteSpeakers.Sanitize(name);
        if ((await _speaker.ListAsync()).Any(p => p.Name.Equals(clean, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatus($"«{name}» уже есть — выбери его в списке и жми «Доучить» или «Хардкорная тренировка».");
            return;
        }
        _list.ClearSelected();
        _nameBox.Text = name;
        var joined = "";
        if (_cfg.RoomMembers.Count > 0 && !_cfg.RoomMembers.Contains(clean))
        {
            _cfg.RoomMembers.Add(clean);
            if (_cfg.RoomPeopleCount < _cfg.RoomMembers.Count)
            {
                _cfg.RoomPeopleCount = Math.Min(KlavaConfig.MaxRoomPeople, _cfg.RoomMembers.Count);
                _cfg.DialogBatchMaxPhrases = _cfg.RoomPeopleCount;
            }
            _cfg.Save();
            joined = $" «{name}» добавлен в компанию — теперь в ней {_cfg.RoomPeopleCount} чел.";
        }
        var r = MessageBox.Show(this,
            $"Записываем голос «{name}».\n\nДа — хардкорная тренировка: читает вслух с экрана 5–10 минут (узнаёт точнее всего).\n" +
            "Нет — быстро: 10 секунд речи (потом можно доучить).\nОтмена — запишу позже.",
            "Новый человек", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Yes) { SetStatus($"«{name}» читает вслух.{joined}"); OpenReading(); }
        else if (r == DialogResult.No) { await EnrollOnceAsync(name, append: false); SetStatus(_status.Text + joined); }
        else SetStatus($"Имя «{name}» вписано — запиши голос любой кнопкой, когда он будет у микрофона.{joined}");
    }

    private void OpenReading()
    {
        if (_reading is { IsDisposed: false }) { _reading.Activate(); return; }
        var name = _nameBox.Text.Trim();
        if (name.Length == 0) name = SelectedName ?? "";
        if (name.Length == 0) { SetStatus("Впиши имя того, кто будет читать (или выбери профиль в списке)."); return; }
        if (name == "Клава") { SetStatus("Голос Клавы тренируется сам — из её синтеза. Выбери человека."); return; }
        var exists = _profiles.Any(p => p.Name.Equals(LiteSpeakers.Sanitize(name), StringComparison.OrdinalIgnoreCase));
        _reading = new ReadingTrainerForm(_cfg, _speaker, name, exists)
        {
            PauseListening = PauseListening,
            Done = () => BeginInvoke(() => { _ = ReloadAsync(); ProfilesChanged?.Invoke(); }),
        };
        _reading.Show(this);
    }

    private async Task TrainEarAsync()
    {
        SetStatus("Обучаю слух на всех профилях…");
        var (ok, report) = await _speaker.TrainEarAsync(new Progress<string>(SetStatus));
        SetStatus(ok ? "✔ Слух обучен." : "✖ " + report);
        if (ok)
        {
            ProfilesChanged?.Invoke();
            await ReloadAsync();
            MessageBox.Show(this, report, "Обучение слуха", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedName is not { } name) { SetStatus("Ничего не выбрано."); return; }
        if (MessageBox.Show(this, $"Удалить профиль «{name}»?\n\nЗаписи не пропадут — уедут в data\\speakers\\_карантин, оттуда можно вернуть.",
                "Голосовые профили", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var ok = await _speaker.RemoveAsync(name);
        SetStatus(ok ? $"Профиль «{name}» убран (записи — в карантине)." : $"Не смог удалить «{name}».");
        if (ok) ProfilesChanged?.Invoke();
        await ReloadAsync();
    }

    private async Task PlaySelectedAsync()
    {
        if (SelectedName is not { } name) { SetStatus("Ничего не выбрано."); return; }
        SetStatus($"Загружаю запись «{name}»…");
        var wav = await _speaker.GetSampleAsync(name);
        if (wav is null) { SetStatus($"Запись «{name}» не найдена."); return; }
        try
        {
            using var ms = new MemoryStream(wav);
            using var reader = new WaveFileReader(ms);
            using var player = new WaveOut();
            player.Init(reader);
            player.Play();
            SetStatus($"▶ Играет «{name}» ({reader.TotalTime.TotalSeconds:F1} с).");
            while (player.PlaybackState == PlaybackState.Playing) await Task.Delay(120);
            SetStatus($"Прослушано «{name}».");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or NAudio.MmException)
        {
            SetStatus($"Не смог воспроизвести: {ex.Message}");
        }
    }

    private async Task RenameSelectedAsync()
    {
        if (SelectedName is not { } oldName) { SetStatus("Ничего не выбрано."); return; }
        var newName = InputBox($"Новое имя для «{oldName}»:", "Переименовать", oldName);
        if (newName is null || newName.Trim().Length == 0 || newName == oldName) return;
        var ok = await _speaker.RenameAsync(oldName, newName.Trim());
        SetStatus(ok ? $"«{oldName}» → «{newName.Trim()}»." : "Переименовать не смог.");
        if (ok) ProfilesChanged?.Invoke();
        await ReloadAsync();
    }

    /// <summary>Простой input-диалог на WinForms (стандартного нет). Возвращает null если Cancel.</summary>
    private static string? InputBox(string prompt, string title, string defaultText)
    {
        using var f = new Form
        {
            Text = title, FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(360, 130), MaximizeBox = false, MinimizeBox = false, Font = new Font("Segoe UI", 10f), Padding = new Padding(12),
        };
        var lbl = new Label { Text = prompt, Dock = DockStyle.Top, AutoSize = true };
        var tb = new TextBox { Text = defaultText, Dock = DockStyle.Top, Margin = new Padding(0, 8, 0, 0) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Right, Width = 80 };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, Dock = DockStyle.Right, Width = 80 };
        var bp = new Panel { Dock = DockStyle.Bottom, Height = 40 };
        bp.Controls.Add(cancel); bp.Controls.Add(ok);
        f.Controls.Add(bp); f.Controls.Add(tb); f.Controls.Add(lbl);
        f.AcceptButton = ok; f.CancelButton = cancel;
        return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
    }

    /// <summary>Есть ли уже такой профиль — тогда спросить: заменить целиком или добавить к нему. null — отмена.</summary>
    private async Task<bool?> AskAppendAsync(string name)
    {
        var exists = (await _speaker.ListAsync()).Any(p => p.Name.Equals(LiteSpeakers.Sanitize(name), StringComparison.OrdinalIgnoreCase));
        if (!exists) return false;
        var r = MessageBox.Show(this,
            $"Профиль «{name}» уже есть.\n\nДа — ДОБАВИТЬ новую запись к нему (обычно так и надо).\nНет — записать профиль заново (старые записи уедут в карантин).",
            "Голосовые профили", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        return r switch { DialogResult.Yes => true, DialogResult.No => false, _ => null };
    }

    /// <summary>Запись с микрофона: seconds — сколько, или до stop (долгая запись), не дольше maxSeconds.</summary>
    private async Task<byte[]?> RecordAsync(int seconds, Task? stop = null, int maxSeconds = 0, Action<TimeSpan>? tick = null)
    {
        if (_recorder is not null) { SetStatus("Уже пишу…"); return null; }
        PauseListening?.Invoke(true);
        try
        {
            var buf = new MemoryStream();
            _recBuf = buf;
            _recorder = new WaveIn { DeviceNumber = Ears.ResolveDevice(_cfg), WaveFormat = new WaveFormat(Rate, 16, 1), BufferMilliseconds = 50 };
            // запоздалый кусок от драйвера после остановки пишется в тот же поток, а не в null
            _recorder.DataAvailable += (_, e) => { lock (buf) buf.Write(e.Buffer, 0, e.BytesRecorded); };
            _recorder.StartRecording();
            var started = DateTime.UtcNow;
            var limit = TimeSpan.FromSeconds(stop is null ? seconds : maxSeconds);
            _countdown = new System.Windows.Forms.Timer { Interval = 1000 };
            _countdown.Tick += (_, _) => tick?.Invoke(DateTime.UtcNow - started);
            _countdown.Start();
            tick?.Invoke(TimeSpan.Zero);
            var timeout = Task.Delay(limit);
            await (stop is null ? timeout : Task.WhenAny(stop, timeout));
            _countdown.Stop(); _countdown.Dispose(); _countdown = null;
            _recorder.StopRecording();
            _recorder.Dispose();
            _recorder = null;
            byte[] pcm;
            lock (buf) pcm = buf.ToArray();
            _recBuf = null;
            return ToWav(pcm);
        }
        catch (Exception ex) when (ex is NAudio.MmException or InvalidOperationException or IOException)
        {
            SetStatus($"Ошибка записи: {ex.Message}");
            Cleanup();
            return null;
        }
        finally
        {
            PauseListening?.Invoke(false);
        }
    }

    private Task<byte[]?> RecordShortAsync(int seconds, string what) =>
        RecordAsync(seconds, tick: t => SetStatus(t.TotalSeconds < seconds ? $"🎙 {what}… {seconds - (int)t.TotalSeconds} сек" : "🎙 Обработка…"));

    private async Task RecordAndEnrollAsync()
    {
        var name = _nameBox.Text.Trim();
        if (name.Length == 0) { SetStatus("Впиши имя (Лис, Сова, Кот, Чупачупс…)."); return; }
        var append = await AskAppendAsync(name);
        if (append is null) return;
        await EnrollOnceAsync(name, append.Value);
    }

    private async Task EnrollOnceAsync(string name, bool append)
    {
        var wav = await RecordShortAsync(RecordSeconds, $"Говори, «{name}»");
        if (wav is null) return;
        SetStatus($"Проверяю запись «{name}»…");
        var ok = await _speaker.EnrollAsync(name, wav, append: append);
        SetStatus(ok ? $"✔ «{name}»: запись {(append ? "добавлена" : "стала новым профилем")}."
                     : $"✖ Не взяла запись в «{name}»: {(_speaker.LastReason.Length > 0 ? _speaker.LastReason : "глянь журнал")}.");
        if (ok) { ProfilesChanged?.Invoke(); await ReloadAsync(); }
    }

    /// <summary>Доучить выбранный профиль ещё одной записью (для устойчивости: тихо, громко, издалека).</summary>
    private async Task RecordAndAppendAsync()
    {
        if (SelectedName is not { } name) { SetStatus("Выбери профиль, который доучиваем."); return; }
        await EnrollOnceAsync(name, append: true);
    }

    /// <summary>
    /// Долгая запись: человек говорит сколько хочет (до 15 минут) — читает вслух, рассказывает, болтает; второй раз
    /// нажал кнопку — хватит. Дальше запись разбирается: куски его голоса — в профиль, чужие голоса, эхо Клавы и
    /// тишина — прочь. Чем дольше и разнообразнее (тихо, громко, смех, издалека), тем увереннее узнаёт.
    /// </summary>
    private async Task LongRecordAsync()
    {
        var name = _nameBox.Text.Trim();
        if (name.Length == 0) { SetStatus("Впиши имя, потом жми долгую запись."); return; }
        var append = await AskAppendAsync(name);
        if (append is null) return;
        if (MessageBox.Show(this,
                $"Долгая запись профиля «{name}».\n\n" +
                $"• Говори обычным голосом 3–{LongMaxMinutes} минут: читай вслух, рассказывай, смейся, говори тише и громче.\n" +
                "• Лучше, чтобы в это время говорил только ты: чужие голоса я выкину, но их лучше меньше.\n" +
                "• Клава в это время не слушает и не отвечает.\n" +
                "• Закончил — нажми кнопку ещё раз («⏹ Хватит»). Через 15 минут остановлюсь сама.",
                "Долгая запись", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        _longStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _longBtn.Text = "⏹ Хватит — обработать";
        _longBtn.BackColor = Color.FromArgb(255, 215, 215);
        byte[]? wav;
        try
        {
            wav = await RecordAsync(0, _longStop.Task, LongMaxMinutes * 60,
                t => SetStatus($"🎙 Пишу «{name}»: {(int)t.TotalMinutes}:{t.Seconds:00} из {LongMaxMinutes}:00 — говори, закончишь — жми «Хватит»."));
        }
        finally
        {
            _longStop = null;
            _longBtn.Text = $"🎙 Долгая запись (до {LongMaxMinutes} мин)";
            _longBtn.BackColor = Color.FromArgb(215, 240, 255);
        }
        if (wav is null) return;
        var minutes = (wav.Length - 44) / (double)(Rate * 2) / 60;
        if (minutes < 0.5) { SetStatus("Слишком коротко — говори хотя бы полминуты (лучше 3–5)."); return; }
        SetStatus($"Разбираю {minutes:F1} мин записи…");
        var progress = new Progress<string>(SetStatus);
        var r = await _speaker.LongEnrollAsync(name, wav, replace: !append.Value, progress);
        SetStatus((r.Ok ? "✔ " : "✖ ") + r.Report);
        MessageBox.Show(this, r.Report, r.Ok ? $"Профиль «{name}» готов" : "Не получилось", MessageBoxButtons.OK,
            r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        ProfilesChanged?.Invoke();
        await ReloadAsync();
    }

    /// <summary>
    /// Тренировка до узнавания: короткие записи, после каждой — «узнаю ли»; не узнала — запись (если это правда один
    /// голос и он не похож на другого человека) добавляется. Стоп — 3 раза подряд узнала или 30 раундов.
    /// </summary>
    private async Task BatteryTrainAsync()
    {
        string name;
        if (SelectedName is { } sel) name = sel;
        else if (_nameBox.Text.Trim().Length > 0) name = _nameBox.Text.Trim();
        else { SetStatus("Выбери профиль в списке ИЛИ впиши имя — потом жми 🔥."); return; }

        const int maxRounds = 30, roundSec = 4, pauseSec = 1, needStreak = 3;
        if (MessageBox.Show(this,
                $"Тренировка «{name}»: говоришь по {roundSec} сек, я проверяю, узнаю ли, и если нет — добавляю запись.\n" +
                $"До 3 узнаваний подряд или {maxRounds} раундов. Для большого запаса лучше «Долгая запись».",
                "Тренировка", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;

        int streak = 0, appended = 0, rejected = 0;
        for (var round = 1; round <= maxRounds && streak < needStreak; round++)
        {
            SetStatus($"🔥 Раунд {round}/{maxRounds} · подряд узнано: {streak}/{needStreak}…");
            await Task.Delay(pauseSec * 1000);
            var wav = await RecordShortAsync(roundSec, $"Раунд {round}: ГОВОРИ");
            if (wav is null) { SetStatus($"Раунд {round}: нет записи, стоп."); return; }
            var m = await _speaker.IdentifyAsync(wav);
            if (m?.Name is not null && string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                streak++;
                SetStatus($"✓ Раунд {round}: узнала «{name}» ({m.Similarity:F2}) · подряд {streak}/{needStreak}");
                continue;
            }
            streak = 0;
            if (await _speaker.EnrollAsync(name, wav, append: true)) { appended++; SetStatus($"➕ Раунд {round}: добавила запись в «{name}» (всего {appended})."); }
            else { rejected++; SetStatus($"✖ Раунд {round}: не взяла — {_speaker.LastReason}"); }
        }
        ProfilesChanged?.Invoke();
        await ReloadAsync();
        SetStatus(streak >= needStreak
            ? $"🎉 «{name}» узнаётся уверенно. Добавлено записей {appended}, отброшено {rejected}."
            : $"30 раундов: добавлено {appended}, отброшено {rejected}. Для запаса — «Долгая запись» на 5–10 минут.");
    }

    private async Task RecordAndIdentifyAsync()
    {
        var wav = await RecordShortAsync(RecordSeconds, "Говори — угадаю, кто");
        if (wav is null) return;
        SetStatus("Определяю, кто говорит…");
        var m = await _speaker.IdentifyAsync(wav);
        if (m is null) { SetStatus("Профили не ответили."); return; }
        var top = m.Scores is null ? "" : "  |  " + string.Join(", ", m.Scores.OrderByDescending(kv => kv.Value).Take(3).Select(kv => $"{kv.Key} {kv.Value:F2}"));
        SetStatus(m.Name is not null
            ? $"👤 Это: {m.Name} ({m.Similarity:F2}){top}"
            : $"❓ Никого уверенно не узнаю (ближе всех {m.Candidate ?? "нет"}, {m.Similarity:F2}){top}");
    }

    /// <summary>Проверка всех профилей: записи, где голос не свой (чужой, эхо Клавы, телевизор), — показать и убрать в карантин.</summary>
    private async Task AuditAsync()
    {
        SetStatus("Проверяю все записи профилей (каждую — против профиля без неё)…");
        var bad = await _speaker.AuditAsync();
        if (bad.Count == 0) { SetStatus("✔ Чужих записей в профилях не нашла."); return; }
        var text = string.Join("\n", bad.Select(b => $"• {b.Name}/{Path.GetFileName(b.File)}: на своего {b.Own:F2}, на «{b.OtherName}» {b.Other:F2}"));
        if (MessageBox.Show(this, $"Похоже, не свой голос ({bad.Count}):\n\n{text}\n\nУбрать их в карантин (data\\speakers\\_карантин — не удаляются)?",
                "Проверка профилей", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            SetStatus($"Нашла {bad.Count} сомнительных записей, оставила как есть.");
            return;
        }
        var n = await _speaker.QuarantineAsync(bad);
        ProfilesChanged?.Invoke();
        await ReloadAsync();
        SetStatus($"🧹 В карантин: {n}. Профили пересобраны.");
    }

    private void Cleanup()
    {
        try { _countdown?.Stop(); _countdown?.Dispose(); } catch (ObjectDisposedException) { }
        _countdown = null;
        try { _recorder?.StopRecording(); _recorder?.Dispose(); } catch (NAudio.MmException) { }
        _recorder = null;
        _recBuf = null;
    }

    /// <summary>PCM 16кГц моно → WAV с заголовком RIFF.</summary>
    private static byte[] ToWav(byte[] pcm)
    {
        var wav = new byte[44 + pcm.Length];
        var s = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + pcm.Length);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(s[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], Rate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], Rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        Encoding.ASCII.GetBytes("data").CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], pcm.Length);
        pcm.CopyTo(s[44..]);
        return wav;
    }
}
