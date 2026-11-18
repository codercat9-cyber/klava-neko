using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Text.Json.Nodes;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KlavaDesk;

/// <summary>
/// Музыкальная студия Клавы — своё окно без браузера (друг 30.09: «без веба, своё, со своим интерфейсом»): что
/// услышал её слух, по дорожкам — части, волна, аккорды, текст, удары, ноты, инструменты; стемы можно глушить,
/// перевод — править с живым счётчиком слогов и отдавать ей петь.
/// </summary>
internal sealed class MusicStudioForm : Form
{
    internal sealed class Actions
    {
        public required Action<string> Analyze { get; init; }
        public required Action<string, bool, string?> Sing { get; init; }
        public required Action<JsonNode> PlayCover { get; init; }
        public required Action StopSong { get; init; }
        public required Action Cancel { get; init; }
        public required Action<string> AskOpinion { get; init; }
        public required Action<string> Similar { get; init; }
    }

    private static readonly Color Bg = Color.FromArgb(22, 20, 31), Panel2 = Color.FromArgb(32, 29, 44), Fg = Color.FromArgb(225, 225, 235),
        Dim = Color.FromArgb(130, 128, 150), Accent = Color.FromArgb(127, 243, 255), Pink = Color.FromArgb(255, 120, 190);

    private readonly string _root;
    private readonly Actions _act;
    private readonly Timeline _tl = new();
    private readonly Label _title = new() { AutoSize = true, ForeColor = Fg, Font = new Font("Segoe UI", 11f, FontStyle.Bold), Margin = new Padding(8, 8, 16, 0) };
    private readonly Label _status = new() { AutoSize = true, ForeColor = Dim, Margin = new Padding(8, 9, 8, 0) };
    private readonly ProgressBar _bar = new() { Width = 160, Height = 14, Margin = new Padding(4, 10, 4, 0), Visible = false };
    private readonly TextBox _passport = Box(true, wrap: true), _score = Box(true), _lyrics = Box(false);
    private readonly Label _syl = new() { Dock = DockStyle.Bottom, Height = 64, ForeColor = Dim, BackColor = Panel2, Padding = new Padding(6) };
    private readonly CheckBox[] _stemBoxes;
    private readonly CheckBox _coverBox = new() { Text = "её кавер", ForeColor = Pink, AutoSize = true, Margin = new Padding(12, 8, 4, 0) };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 40 };
    private string? _file;
    private string? _trackDir;
    private JsonNode? _an;
    private JsonNode? _plan;
    private JsonNode? _cover;
    private WaveOutEvent? _out;
    private readonly List<(string Key, WaveStream Reader, VolumeSampleProvider Vol)> _chans = [];
    private static readonly string[] StemKeys = ["vocals", "drums", "bass", "other"];
    private static readonly string[] StemRu = ["голос", "ударные", "бас", "остальное"];

    public MusicStudioForm(string musicRoot, Actions act)
    {
        _root = musicRoot;
        _act = act;
        Text = "Клава — музыкальная студия";
        BackColor = Bg;
        ForeColor = Fg;
        Size = new Size(1400, 860);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (ArgumentException) { }

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, BackColor = Panel2, WrapContents = false, Padding = new Padding(4, 4, 4, 0) };
        top.Controls.Add(Btn("Открыть…", (_, _) => { if (Pick() is { } f) LoadTrack(f); }));
        top.Controls.Add(Btn("Разобрать", (_, _) => { if (_file is not null) { _act.Analyze(_file); SetStatus(0.01, "разбор начался…"); } }));
        top.Controls.Add(Btn("▶ / ❚❚", (_, _) => TogglePlay()));
        top.Controls.Add(Btn("⏹", (_, _) => StopPlay(true)));
        top.Controls.Add(Btn("Кавер по-русски", (_, _) => { if (_file is not null) { _act.Sing(_file, false, LyricsIfEdited()); SetStatus(0.01, "кавер начался…"); } }));
        top.Controls.Add(Btn("Кавер в оригинале", (_, _) => { if (_file is not null) { _act.Sing(_file, true, null); SetStatus(0.01, "кавер начался…"); } }));
        top.Controls.Add(Btn("♪ Клава поёт", (_, _) => { if (_cover is not null) { StopPlay(false); _act.PlayCover(_cover); } }));
        top.Controls.Add(Btn("Похожая основа", (_, _) => { if (_file is not null) { StopPlay(false); _act.Similar(_file); SetStatus(0.01, "собираю похожую основу…"); } }));
        top.Controls.Add(Btn("Тихо", (_, _) => _act.StopSong()));
        top.Controls.Add(Btn("✖ Отмена задачи", (_, _) => _act.Cancel()));
        top.Controls.Add(_bar);
        top.Controls.Add(_status);

        var mixer = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, BackColor = Bg, WrapContents = false, Padding = new Padding(4, 0, 4, 0) };
        mixer.Controls.Add(_title);
        _stemBoxes = StemKeys.Select((k, i) => new CheckBox { Text = StemRu[i], Checked = true, ForeColor = Fg, AutoSize = true, Margin = new Padding(8, 8, 4, 0), Tag = k }).ToArray();
        foreach (var b in _stemBoxes)
        {
            b.CheckedChanged += (_, _) => ApplyMutes();
            mixer.Controls.Add(b);
        }
        _coverBox.CheckedChanged += (_, _) => { var pos = Position(); StopPlay(false); OpenAudio(); Seek(pos); };
        mixer.Controls.Add(_coverBox);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(Page("Паспорт", _passport));
        tabs.TabPages.Add(Page("Партитура", _score));
        var lp = Page("Перевод", _lyrics);
        lp.Controls.Add(_syl);
        tabs.TabPages.Add(lp);
        _lyrics.TextChanged += (_, _) => UpdateSyllables();

        var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Bg, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_tl);
        split.Panel2.Controls.Add(tabs);
        _tl.Dock = DockStyle.Fill;
        _tl.Seek += Seek;
        Controls.Add(split);
        Controls.Add(mixer);
        Controls.Add(top);
        Load += (_, _) => split.SplitterDistance = Math.Max(400, ClientSize.Width - 460);
        _tick.Tick += (_, _) => { if (_out is not null) _tl.Playhead = Position(); };
        _tick.Start();
        FormClosed += (_, _) => { _tick.Stop(); StopPlay(false); CloseAudio(); };
    }

    private static TextBox Box(bool ro, bool wrap = false) => new()
    {
        Multiline = true, ReadOnly = ro, ScrollBars = wrap ? ScrollBars.Vertical : ScrollBars.Both, WordWrap = wrap || !ro, Dock = DockStyle.Fill,
        BackColor = Color.FromArgb(18, 17, 26), ForeColor = Color.FromArgb(215, 215, 228), BorderStyle = BorderStyle.None,
        Font = new Font("Consolas", 9.5f),
    };

    private static TabPage Page(string title, Control c)
    {
        var p = new TabPage(title) { BackColor = Bg };
        p.Controls.Add(c);
        return p;
    }

    private static Button Btn(string text, EventHandler click)
    {
        var b = new Button
        {
            Text = text, AutoSize = true, FlatStyle = FlatStyle.Flat, ForeColor = Fg, BackColor = Color.FromArgb(48, 44, 66),
            Margin = new Padding(3, 4, 3, 0), Padding = new Padding(4, 0, 4, 0),
        };
        b.FlatAppearance.BorderColor = Color.FromArgb(80, 74, 110);
        b.Click += click;
        return b;
    }

    private static string? Pick()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Трек для студии",
            Filter = "Музыка|*.mp3;*.flac;*.ogg;*.wav;*.m4a;*.opus;*.aac;*.wma;*.webm;*.mp4|Все файлы|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
    }

    // ---------- данные ----------

    public void SetStatus(double p, string msg)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => SetStatus(p, msg)); return; }
        _status.Text = msg;
        _bar.Visible = msg.Length > 0 && p < 1;
        _bar.Value = (int)Math.Clamp(p * 100, 0, 100);
        // задача кончилась — свежие файлы разбора и кавера
        if (msg.Length == 0 && _file is not null) LoadTrack(_file);
    }

    public void LoadTrack(string file)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => LoadTrack(file)); return; }
        var same = string.Equals(_file, file, StringComparison.OrdinalIgnoreCase);
        var pos = same ? Position() : 0;
        StopPlay(false);
        CloseAudio();
        _file = file;
        _trackDir = FindTrackDir(file);
        _an = _trackDir is null ? null : ReadJson(Path.Combine(_trackDir, "analysis.json"));
        _plan = _trackDir is null ? null : ReadJson(Path.Combine(_trackDir, "plan.json"));
        _cover = FindCover();
        var meta = _an?["meta"];
        _title.Text = meta?["title"] is not null ? $"{meta["artist"]} — {meta["title"]}" : Path.GetFileNameWithoutExtension(file);
        _passport.Text = _trackDir is not null && File.Exists(Path.Combine(_trackDir, "passport.txt"))
            ? File.ReadAllText(Path.Combine(_trackDir, "passport.txt")).Replace("\n", "\r\n")
            : "Трек ещё не разобран — нажми «Разобрать» (1–3 минуты: дорожки, доли, ноты, аккорды, текст, инструменты).";
        _score.Text = _trackDir is not null && File.Exists(Path.Combine(_trackDir, "score.txt"))
            ? File.ReadAllText(Path.Combine(_trackDir, "score.txt")).Replace("\n", "\r\n") : "";
        var lyr = _trackDir is null ? null : Path.Combine(_trackDir, "lyrics_ru.txt");
        _lyrics.Text = lyr is not null && File.Exists(lyr) ? File.ReadAllText(lyr).Replace("\r", "").Replace("\n", "\r\n") : PlanTemplate();
        _lyricsLoaded = _lyrics.Text;
        _coverBox.Enabled = _cover is not null;
        if (_cover is null) _coverBox.Checked = false;
        _tl.SetData(_an, _plan, _cover);
        if (_trackDir is not null) _ = LoadPeaksAsync(Path.Combine(_trackDir, "mix.flac"));
        OpenAudio();
        if (same) Seek(pos);
        UpdateSyllables();
    }

    private string _lyricsLoaded = "";

    private string PlanTemplate()
    {
        if (_plan?["lines"] is not JsonArray lines) return "";
        return string.Join("\r\n", lines.Select(l => $"{l!["i"]!.GetValue<int>():00} | {(l["vocalise"]!.GetValue<bool>() ? "=" : "")}"));
    }

    /// <summary>Друг правил перевод — петь с его текстом; не трогал — пусть переводит она сама (или берёт сохранённый).</summary>
    private string? LyricsIfEdited()
    {
        var t = _lyrics.Text.Trim();
        return t.Length == 0 || t == _lyricsLoaded.Trim() && !File.Exists(Path.Combine(_trackDir ?? "", "lyrics_ru.txt")) ? null : t;
    }

    private void UpdateSyllables()
    {
        if (_plan?["lines"] is not JsonArray lines) { _syl.Text = "Слоги сверяются с нотами после разбора и плана кавера."; return; }
        var bad = new List<string>();
        foreach (var raw in _lyrics.Lines)
        {
            var m = System.Text.RegularExpressions.Regex.Match(raw, @"^\s*(\d{1,3})\s*\|\s*(.*)$");
            if (!m.Success) continue;
            var i = int.Parse(m.Groups[1].Value);
            var t = m.Groups[2].Value.Trim();
            if (i >= lines.Count || t is "" or "=") continue;
            var need = lines[i]!["k"]!.GetValue<int>();
            var got = MusicClient.Syllables(t);
            if (got != need) bad.Add($"{i:00}: {got}/{need}");
        }
        _syl.Text = bad.Count == 0 ? "Все строки в размер: слогов столько же, сколько нот. «=» — спеть оригинал её голосом, пусто — промолчать."
            : "Не в размер (есть/нужно): " + string.Join(", ", bad);
        _syl.ForeColor = bad.Count == 0 ? Accent : Pink;
    }

    private string? FindTrackDir(string file)
    {
        var tracks = Path.Combine(_root, "tracks");
        if (!Directory.Exists(tracks)) return null;
        var full = Path.GetFullPath(file);
        foreach (var aj in new DirectoryInfo(tracks).GetFiles("analysis.json", SearchOption.AllDirectories).OrderByDescending(x => x.LastWriteTimeUtc))
        {
            var src = ReadJson(aj.FullName)?["source"]?.GetValue<string>();
            if (string.Equals(src, full, StringComparison.OrdinalIgnoreCase)) return aj.DirectoryName;
        }
        return null;
    }

    private JsonNode? FindCover()
    {
        if (_trackDir is null) return null;
        var covers = Path.Combine(_root, "covers");
        if (!Directory.Exists(covers)) return null;
        var name = Path.GetFileName(_trackDir);
        var f = new DirectoryInfo(covers).GetDirectories(name + "-*").Select(d => new FileInfo(Path.Combine(d.FullName, "cover.json")))
            .Where(x => x.Exists).OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
        if (f is null) return null;
        var j = ReadJson(f.FullName);
        if (j is not null) j["dir"] = f.DirectoryName;
        return j;
    }

    private static JsonNode? ReadJson(string file)
    {
        try { return File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) : null; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; }
    }

    private async Task LoadPeaksAsync(string mix)
    {
        if (!File.Exists(mix)) return;
        var peaks = await Task.Run(() =>
        {
            try
            {
                using var r = new MediaFoundationReader(mix);
                var sp = r.ToSampleProvider();
                var per = sp.WaveFormat.SampleRate * sp.WaveFormat.Channels / 100;
                var buf = new float[per];
                var list = new List<float>();
                int n;
                while ((n = sp.Read(buf.AsSpan())) > 0)
                {
                    var m = 0f;
                    for (var i = 0; i < n; i++) m = Math.Max(m, Math.Abs(buf[i]));
                    list.Add(m);
                }
                return list.ToArray();
            }
            catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                Log.Write($"студия: волна не прочиталась — {ex.Message}");
                return [];
            }
        });
        if (!IsDisposed) BeginInvoke(() => _tl.SetPeaks(peaks));
    }

    // ---------- звук ----------

    private void OpenAudio()
    {
        CloseAudio();
        if (_trackDir is null) return;
        try
        {
            var files = _coverBox.Checked && _cover is not null
                ? [("cover", Path.Combine(_cover["dir"]!.GetValue<string>(), _cover["cover"]!.GetValue<string>()))]
                : StemKeys.Select(k => (k, Path.Combine(_trackDir, k + ".flac"))).Where(x => File.Exists(x.Item2)).ToArray();
            if (files.Length == 0) return;
            var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(44100, 2)) { ReadFully = false };
            foreach (var (k, f) in files)
            {
                WaveStream r = f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ? new WaveFileReader(f) : new MediaFoundationReader(f);
                ISampleProvider sp = r.ToSampleProvider();
                if (sp.WaveFormat.SampleRate != 44100) sp = new WdlResamplingSampleProvider(sp, 44100);
                if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
                var vol = new VolumeSampleProvider(sp);
                mixer.AddMixerInput(vol);
                _chans.Add((k, r, vol));
            }
            _out = new WaveOutEvent();
            _out.Init(mixer);
            ApplyMutes();
        }
        catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException or InvalidOperationException or NAudio.MmException)
        {
            Log.Write($"студия: звук не открылся — {ex.Message}");
            SetStatus(1, $"звук не открылся: {ex.Message}");
            CloseAudio();
        }
    }

    private void CloseAudio()
    {
        _out?.Dispose();
        _out = null;
        foreach (var c in _chans) c.Reader.Dispose();
        _chans.Clear();
    }

    private void ApplyMutes()
    {
        foreach (var (k, _, vol) in _chans)
        {
            var box = _stemBoxes.FirstOrDefault(b => (string)b.Tag! == k);
            vol.Volume = box is null || box.Checked ? 1f : 0f;
        }
    }

    private void TogglePlay()
    {
        if (_out is null) OpenAudio();
        if (_out is null) return;
        if (_out.PlaybackState == PlaybackState.Playing) _out.Pause();
        else
        {
            _act.StopSong();   // Клава не поёт поверх студии
            _out.Play();
        }
    }

    private void StopPlay(bool rewind)
    {
        _out?.Stop();
        if (rewind) Seek(0);
    }

    private double Position() => _chans.Count > 0 ? _chans[0].Reader.CurrentTime.TotalSeconds : 0;

    private void Seek(double sec)
    {
        foreach (var c in _chans)
        {
            var t = TimeSpan.FromSeconds(Math.Clamp(sec, 0, c.Reader.TotalTime.TotalSeconds));
            c.Reader.CurrentTime = t;
        }
        _tl.Playhead = sec;
    }

    // ---------- дорожки ----------

    private sealed class Timeline : Control
    {
        public event Action<double>? Seek;
        private JsonNode? _an, _plan, _cover;
        private float[] _peaks = [];
        private double _dur = 1, _view0, _viewLen = 60;
        private double _playhead;
        private readonly HScrollBar _scroll = new() { Dock = DockStyle.Bottom, Height = 16 };
        private static readonly Font Small = new("Segoe UI", 8f), Tiny = new("Segoe UI", 7f), Bold = new("Segoe UI", 8.5f, FontStyle.Bold);
        private static readonly Color[] SecColors =
        [
            Color.FromArgb(90, 70, 160), Color.FromArgb(40, 120, 150), Color.FromArgb(150, 70, 110), Color.FromArgb(60, 130, 80),
            Color.FromArgb(150, 110, 50), Color.FromArgb(100, 100, 130), Color.FromArgb(160, 60, 60), Color.FromArgb(60, 90, 150),
        ];

        public Timeline()
        {
            DoubleBuffered = true;
            ResizeRedraw = true;
            BackColor = Bg;
            Controls.Add(_scroll);
            _scroll.Scroll += (_, _) => { _view0 = _scroll.Value / 10.0; Invalidate(); };
        }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public double Playhead
        {
            get => _playhead;
            set
            {
                _playhead = value;
                // играет за правым краем — лента едет следом
                if (_playhead > _view0 + _viewLen * 0.92 || _playhead < _view0) { _view0 = Math.Max(0, _playhead - _viewLen * 0.1); SyncScroll(); }
                Invalidate();
            }
        }

        public void SetData(JsonNode? an, JsonNode? plan, JsonNode? cover)
        {
            _an = an;
            _plan = plan;
            _cover = cover;
            _dur = an?["duration"]?.GetValue<double>() ?? 1;
            _view0 = 0;
            _viewLen = Math.Min(60, _dur);
            _peaks = [];
            SyncScroll();
            Invalidate();
        }

        public void SetPeaks(float[] p)
        {
            _peaks = p;
            Invalidate();
        }

        private void SyncScroll()
        {
            _scroll.Minimum = 0;
            _scroll.Maximum = (int)(_dur * 10) + (int)(_viewLen * 10) - 1;
            _scroll.LargeChange = Math.Max(1, (int)(_viewLen * 10));
            _scroll.Value = Math.Clamp((int)(_view0 * 10), 0, Math.Max(0, _scroll.Maximum - _scroll.LargeChange + 1));
        }

        private float X(double t) => (float)((t - _view0) / _viewLen * Width);

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            var t = _view0 + e.X / (double)Math.Max(1, Width) * _viewLen;
            if (ModifierKeys.HasFlag(Keys.Control))
            {
                _viewLen = Math.Clamp(_viewLen * (e.Delta > 0 ? 0.8 : 1.25), 4, Math.Max(4, _dur));
                _view0 = Math.Clamp(t - e.X / (double)Math.Max(1, Width) * _viewLen, 0, Math.Max(0, _dur - _viewLen));
            }
            else _view0 = Math.Clamp(_view0 - Math.Sign(e.Delta) * _viewLen * 0.15, 0, Math.Max(0, _dur - _viewLen));
            SyncScroll();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            Seek?.Invoke(Math.Clamp(_view0 + e.X / (double)Math.Max(1, Width) * _viewLen, 0, _dur));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            var h = Height - _scroll.Height;
            if (_an is null)
            {
                TextRenderer.DrawText(g, "Открой трек и разбери его — здесь появятся части, аккорды, удары, ноты и текст.", Font, new Point(20, 20), Dim);
                return;
            }
            // полосы: линейка 22, части 26, волна 70, аккорды 22, текст 34, удары 36, ноты — остальное, инструменты 30
            var y = 0;
            Lane(g, ref y, 22, DrawRuler);
            Lane(g, ref y, 26, DrawSections);
            Lane(g, ref y, 70, DrawWave);
            Lane(g, ref y, 22, DrawChords);
            Lane(g, ref y, 34, DrawLyrics);
            Lane(g, ref y, 36, DrawDrums);
            var notesH = Math.Max(80, h - y - 30);
            Lane(g, ref y, notesH, DrawNotes);
            Lane(g, ref y, 30, DrawInstruments);
            var px = X(_playhead);
            using var ph = new Pen(Color.FromArgb(230, 255, 255, 255), 1.5f);
            g.DrawLine(ph, px, 0, px, h);
        }

        private void Lane(Graphics g, ref int y, int height, Action<Graphics, Rectangle> draw)
        {
            var r = new Rectangle(0, y, Width, height);
            g.SetClip(r);
            draw(g, r);
            g.ResetClip();
            using var sep = new Pen(Color.FromArgb(45, 42, 60));
            g.DrawLine(sep, 0, r.Bottom - 1, Width, r.Bottom - 1);
            y += height;
        }

        private IEnumerable<double> Arr(string key) => (_an?[key] as JsonArray ?? []).Select(x => x!.GetValue<double>());

        private void DrawRuler(Graphics g, Rectangle r)
        {
            using var bar = new Pen(Color.FromArgb(70, 66, 92));
            var bars = _an?["bars"] as JsonArray ?? [];
            var every = Math.Max(1, (int)(bars.Count * 1.4 / Math.Max(1, _dur) * _viewLen / 12));
            for (var i = 0; i < bars.Count; i++)
            {
                var t = bars[i]![0]!.GetValue<double>();
                if (t < _view0 - 1 || t > _view0 + _viewLen) continue;
                var x = X(t);
                g.DrawLine(bar, x, r.Top + 12, x, r.Bottom);
                if (i % every == 0) TextRenderer.DrawText(g, (i + 1).ToString(), Tiny, new Point((int)x + 2, r.Top), Dim);
            }
            var step = _viewLen > 120 ? 30 : _viewLen > 40 ? 10 : 5;
            for (var t = Math.Ceiling(_view0 / step) * step; t < _view0 + _viewLen; t += step)
                TextRenderer.DrawText(g, $"{(int)t / 60}:{(int)t % 60:00}", Tiny, new Point((int)X(t) + 2, r.Top + 10), Accent);
        }

        private void DrawSections(Graphics g, Rectangle r)
        {
            foreach (var s in _an?["sections"] as JsonArray ?? [])
            {
                var t0 = s!["t0"]!.GetValue<double>();
                var t1 = s["t1"]!.GetValue<double>();
                var lab = s["label"]!.GetValue<string>();
                var rc = RectangleF.FromLTRB(X(t0), r.Top + 3, X(t1), r.Bottom - 3);
                using var b = new SolidBrush(SecColors[(lab[0] - 'A') % SecColors.Length]);
                g.FillRectangle(b, rc);
                TextRenderer.DrawText(g, $"{s["name"]} [{lab}]", Bold, Rectangle.Round(rc), Fg, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
            }
        }

        private void DrawWave(Graphics g, Rectangle r)
        {
            var beats = Arr("beats").ToArray();
            using var bp = new Pen(Color.FromArgb(38, 36, 52));
            if (_viewLen < 40)
                foreach (var b in beats.Where(b => b >= _view0 && b <= _view0 + _viewLen)) g.DrawLine(bp, X(b), r.Top, X(b), r.Bottom);
            if (_peaks.Length == 0) { TextRenderer.DrawText(g, "волна загружается…", Small, new Point(8, r.Top + 4), Dim); return; }
            using var pen = new Pen(Color.FromArgb(170, 127, 243, 255));
            var mid = r.Top + r.Height / 2f;
            for (var x = 0; x < Width; x++)
            {
                var a = (int)((_view0 + x / (double)Width * _viewLen) * 100);
                var b = (int)((_view0 + (x + 1) / (double)Width * _viewLen) * 100);
                var m = 0f;
                for (var i = Math.Max(0, a); i < Math.Min(_peaks.Length, Math.Max(a + 1, b)); i++) m = Math.Max(m, _peaks[i]);
                var hh = Math.Min(1f, m) * (r.Height / 2f - 2);
                g.DrawLine(pen, x, mid - hh, x, mid + hh);
            }
        }

        private void DrawChords(Graphics g, Rectangle r)
        {
            foreach (var c in _an?["chords"] as JsonArray ?? [])
            {
                var t0 = c![0]!.GetValue<double>();
                var t1 = c[1]!.GetValue<double>();
                if (t1 < _view0 || t0 > _view0 + _viewLen) continue;
                var name = c[2]!.GetValue<string>();
                if (name == "N") continue;
                var rc = RectangleF.FromLTRB(X(t0), r.Top + 2, X(t1), r.Bottom - 2);
                using var pen = new Pen(Color.FromArgb(70, 66, 92));
                g.DrawRectangle(pen, rc.X, rc.Y, rc.Width, rc.Height);
                if (rc.Width > 14) TextRenderer.DrawText(g, name, Small, Rectangle.Round(rc), Color.FromArgb(255, 220, 140), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private void DrawLyrics(Graphics g, Rectangle r)
        {
            var right = int.MinValue;
            foreach (var w in _an?["lyrics"]?["words"] as JsonArray ?? [])
            {
                var t0 = w![0]!.GetValue<double>();
                if (t0 < _view0 - 2 || t0 > _view0 + _viewLen) continue;
                var x = (int)X(t0);
                if (x < right + 4) continue;   // мелкий масштаб — слова через одно, но не кашей
                var text = w[2]!.GetValue<string>();
                TextRenderer.DrawText(g, text, Small, new Point(x, r.Top + 1), Fg);
                right = x + TextRenderer.MeasureText(text, Small).Width;
            }
            var lines = (_cover?["lines"] as JsonArray ?? []).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                var t0 = lines[i]!["t0"]!.GetValue<double>();
                var t1 = lines[i]!["t1"]!.GetValue<double>();
                if (t1 < _view0 || t0 > _view0 + _viewLen) continue;
                var next = i + 1 < lines.Count ? X(lines[i + 1]!["t0"]!.GetValue<double>()) : Width;
                var rc = RectangleF.FromLTRB(X(t0), r.Top + 17, Math.Max(X(t1), next - 4), r.Bottom);
                TextRenderer.DrawText(g, lines[i]!["text"]!.GetValue<string>(), Small, Rectangle.Round(rc), Pink, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
        }

        private void DrawDrums(Graphics g, Rectangle r)
        {
            var rows = new Dictionary<string, (int Y, Color C)>
            {
                ["K"] = (r.Top + 28, Color.FromArgb(255, 140, 90)), ["S"] = (r.Top + 18, Color.FromArgb(240, 240, 120)), ["H"] = (r.Top + 8, Color.FromArgb(150, 200, 255)),
            };
            TextRenderer.DrawText(g, "K S H", Tiny, new Point(2, r.Top + 2), Dim);
            foreach (var hit in _an?["drums"]?["hits"] as JsonArray ?? [])
            {
                var t = hit![0]!.GetValue<double>();
                if (t < _view0 || t > _view0 + _viewLen) continue;
                var (yy, c) = rows[hit[1]!.GetValue<string>()];
                using var b = new SolidBrush(c);
                var rad = _viewLen < 30 ? 3f : 2f;
                g.FillEllipse(b, X(t) - rad, yy - rad, rad * 2, rad * 2);
            }
        }

        private void DrawNotes(Graphics g, Rectangle r)
        {
            var notes = _an?["notes"];
            var all = new List<(double T0, double T1, double P, Color C)>();
            void Add(string key, Color c)
            {
                foreach (var n in notes?[key] as JsonArray ?? [])
                    all.Add((n![0]!.GetValue<double>(), n[1]!.GetValue<double>(), n[2]!.GetValue<double>(), c));
            }
            Add("other", Color.FromArgb(120, 110, 220, 150));
            Add("bass", Color.FromArgb(200, 90, 150, 255));
            Add("vocals", Color.FromArgb(230, 255, 120, 190));
            if (all.Count == 0) { TextRenderer.DrawText(g, "нот нет (разбор «быстрый» или без мелодии)", Small, new Point(8, r.Top + 4), Dim); return; }
            var lo = Math.Floor(all.Min(n => n.P)) - 1;
            var hi = Math.Ceiling(all.Max(n => n.P)) + 1;
            var ph = r.Height / (hi - lo);
            using var oct = new Pen(Color.FromArgb(40, 38, 56));
            for (var p = Math.Ceiling(lo / 12) * 12; p < hi; p += 12)
            {
                var yy = (float)(r.Bottom - (p - lo) * ph);
                g.DrawLine(oct, 0, yy, Width, yy);
                TextRenderer.DrawText(g, $"C{(int)p / 12 - 1}", Tiny, new Point(2, (int)yy - 12), Dim);
            }
            foreach (var (t0, t1, p, c) in all)
            {
                if (t1 < _view0 || t0 > _view0 + _viewLen) continue;
                using var b = new SolidBrush(c);
                var yy = (float)(r.Bottom - (p - lo + 0.5) * ph);
                g.FillRectangle(b, X(t0), yy, Math.Max(1.5f, X(t1) - X(t0)), (float)Math.Max(2, ph * 0.9));
            }
            TextRenderer.DrawText(g, "● голос  ● бас  ● остальное", Tiny, new Point(Width - 170, r.Top + 2), Dim);
        }

        private void DrawInstruments(Graphics g, Rectangle r)
        {
            foreach (var w in _an?["labels"]?["timeline"] as JsonArray ?? [])
            {
                var t0 = w![0]!.GetValue<double>();
                var t1 = w[1]!.GetValue<double>();
                if (t1 < _view0 || t0 > _view0 + _viewLen) continue;
                var names = string.Join(", ", (w[2] as JsonArray ?? []).Take(3).Select(x => x![0]!.GetValue<string>()));
                var rc = RectangleF.FromLTRB(X(t0) + 2, r.Top + 2, X(t1) - 2, r.Bottom - 2);
                using var b = new SolidBrush(Color.FromArgb(36, 34, 50));
                g.FillRectangle(b, rc);
                TextRenderer.DrawText(g, names, Tiny, Rectangle.Round(rc), Color.FromArgb(200, 200, 220), TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
