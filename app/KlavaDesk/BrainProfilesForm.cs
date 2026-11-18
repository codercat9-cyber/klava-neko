using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>Пресет провайдера мозгов — все настройки уже забиты, остаётся только вставить ключ.</summary>
internal sealed record BrainPreset(
    string Title, string Emoji, string Kind, string Url, string DefaultModel,
    bool Vision, bool Tools, int MaxTokens, string KeyHint, string KeyPrefix, string DefaultId, string DefaultName);

/// <summary>
/// Окно «Мозги Клавы». Слева — список мозгов (★ — активный). Справа — всё про выбранный: адрес, ключ, модель
/// (список прямо с сервера), быстрая модель для болтовни, запасной мозг, как ходить в сеть (напрямую/прокси).
/// Сверху — «Автонастройка»: прокси, Antigravity Tools, ключи из окружения, локальные серверы — одной кнопкой.
/// Вставленный руками ключ не пишется в klava.json: он уходит в переменную окружения пользователя, в профиле — ссылка env:.
/// </summary>
internal sealed class BrainProfilesForm : Form
{
    private static readonly BrainPreset[] Presets =
    [
        new("AG Tools",     "🪐", "openai", "http://127.0.0.1:8045/v1",                               "gemini-4-argon",          true,  true, 4000, "(берётся сам)", "",        "antigravity", "Antigravity (4 акка через AG Tools)"),
        new("OpenAI",       "🟢", "openai", "https://api.openai.com/v1",                              "gpt-5-mini",              true,  true, 4000, "sk-…",          "sk-proj-", "openai",      "OpenAI (GPT)"),
        new("Anthropic",    "🟣", "openai", "https://api.anthropic.com/v1",                           "claude-sonnet-4-5",       true,  true, 4000, "sk-ant-…",      "sk-ant-",  "anthropic",   "Anthropic (Claude API)"),
        new("Gemini",       "🔵", "openai", "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-3.5-flash",       true,  true, 4000, "AIza…",         "AIza",     "gemini",      "Жминя (ключ API)"),
        new("OpenRouter",   "🌐", "openai", "https://openrouter.ai/api/v1",                           "openai/gpt-4o-mini",      true,  true, 4000, "sk-or-…",       "sk-or-",   "openrouter",  "OpenRouter"),
        new("Groq",         "⚡", "openai", "https://api.groq.com/openai/v1",                         "llama-3.3-70b-versatile", false, true, 4000, "gsk_…",         "gsk_",     "groq",        "Groq (быстрая Llama)"),
        new("xAI Grok",     "🅧", "openai", "https://api.x.ai/v1",                                    "grok-4",                  true,  true, 4000, "xai-…",         "xai-",     "xai",         "xAI Grok"),
        new("Ollama",       "🦙", "openai", "http://127.0.0.1:11434/v1",                              "",                        false, true, 800,  "(не нужен)",    "",         "ollama",      "Ollama (локально)"),
        new("Claude Code",  "📗", "claude-code", "",                                                  "sonnet",                  true,  false, 400, "(подписка)",    "",         "claude",      "Claude — Claude Code (подписка)"),
        new("ChatGPT (Codex)", "🧠", "codex",    "",                                                  "",                        true,  false, 800, "(подписка)",    "",         "codex",       "ChatGPT — Codex (подписка)"),
        new("DeepSeek",     "🐳", "openai", "https://api.deepseek.com",                               "deepseek-flash",          true,  true,  800, "sk-…",          "",         "deepseek",    "Дипсик — DeepSeek V4.1 Flash"),
    ];

    private readonly KlavaConfig _cfg;
    private readonly Action _onSaved;
    private readonly Func<BrainProfile, string?, Task>? _onActivate;

    private readonly ListBox _list = new() { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 10f), IntegralHeight = false };
    private readonly TextBox _name = new() { Dock = DockStyle.Fill };
    private readonly TextBox _id = new() { Dock = DockStyle.Fill };
    private readonly ComboBox _kind = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _url = new() { Dock = DockStyle.Fill };
    private readonly TextBox _key = new() { Dock = DockStyle.Fill, Font = new Font("Consolas", 10f) };
    private readonly ComboBox _model = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, MaxDropDownItems = 25 };
    private readonly ComboBox _chatModel = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, MaxDropDownItems = 25 };
    private readonly ComboBox _fallback = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly NumericUpDown _fallbackMs = new() { Minimum = 0, Maximum = 60000, Increment = 500, Width = 90 };
    private readonly ComboBox _proxy = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox _aliases = new() { Dock = DockStyle.Fill };
    private readonly CheckBox _vision = new() { Text = "видит картинки", AutoSize = true };
    private readonly CheckBox _tools = new() { Text = "инструменты", AutoSize = true };
    private readonly CheckBox _fromServer = new() { Text = "модели — с сервера", AutoSize = true };
    private readonly NumericUpDown _maxTokens = new() { Minimum = 64, Maximum = 32000, Increment = 100, Width = 90 };
    private readonly TextBox _netProxy = new() { Width = 230 };
    private readonly Label _netProxyInfo = new() { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(6, 7, 0, 0) };
    private readonly TextBox _log = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Font = new Font("Consolas", 9f), BackColor = Color.FromArgb(248, 249, 252) };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Bottom, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 6, 0) };

    private BrainProfile? _editing;
    private bool _loading;

    public BrainProfilesForm(KlavaConfig cfg, Action onSaved, Func<BrainProfile, string?, Task>? onActivate = null)
    {
        _cfg = cfg;
        _onSaved = onSaved;
        _onActivate = onActivate;
        Text = "Мозги Клавы";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(1000, 720);
        MinimumSize = new Size(860, 620);
        Font = new Font("Segoe UI", 9.75f);
        _kind.Items.AddRange(["openai", "claude-code", "codex", "own", "flashnext"]);
        _proxy.Items.AddRange(["auto", "direct", "proxy"]);

        // ===== слева: список =====
        var left = new Panel { Dock = DockStyle.Left, Width = 290, Padding = new Padding(6) };
        var leftBottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, Padding = new Padding(0, 4, 0, 0) };
        var newBtn = new Button { Text = "➕ Новый", AutoSize = true };
        newBtn.Click += (_, _) => { _list.ClearSelected(); LoadForEdit(null); };
        var delBtn = new Button { Text = "🗑 Удалить", AutoSize = true };
        delBtn.Click += (_, _) => DeleteSelected();
        leftBottom.Controls.Add(newBtn);
        leftBottom.Controls.Add(delBtn);
        left.Controls.Add(_list);
        left.Controls.Add(leftBottom);
        _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedIndex >= 0 && _list.SelectedIndex < _cfg.Brains.Count) LoadForEdit(_cfg.Brains[_list.SelectedIndex]); };
        _list.DoubleClick += (_, _) => _ = ActivateAsync();

        // ===== справа =====
        var right = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(8, 6, 8, 6) };
        right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));   // без неё колонка растёт под содержимое и правая часть не влезает
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // действия
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // пресеты
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // поля
        right.RowStyles.Add(new RowStyle(SizeType.AutoSize));      // прокси
        right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));  // журнал

        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        Button Btn(string text, Action a, bool bold = false)
        {
            var b = new Button { Text = text, AutoSize = true, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 0, 6, 6) };
            if (bold) b.Font = new Font(Font, FontStyle.Bold);
            b.Click += (_, _) => a();
            actions.Controls.Add(b);
            return b;
        }
        var autoBtn = Btn("⚡ Автонастройка всего", () => _ = AutoSetupAsync(), bold: true);
        Btn("💾 Сохранить", () => Save(), bold: true);
        Btn("✔ Сделать активным", () => _ = ActivateAsync());
        Btn("🔌 Тест ответа", () => _ = TestAsync());
        Btn("⟳ Модели с сервера", () => _ = LoadModelsAsync(true));
        right.Controls.Add(actions);

        // пресеты — сеткой 5 в ряд: FlowLayoutPanel в авторазмерной рамке не переносил строку, и половина кнопок пряталась
        var presetBox = new GroupBox { Text = " Новый мозг из пресета (заполнит всё — останется вставить ключ) ", Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        const int perRow = 5;
        var presetPanel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = perRow, Padding = new Padding(2) };
        for (var c = 0; c < perRow; c++) presetPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / perRow));
        for (var i = 0; i < Presets.Length; i++)
        {
            var p = Presets[i];
            var b = new Button { Text = $"{p.Emoji} {p.Title}", Dock = DockStyle.Fill, Height = 30, Margin = new Padding(0, 0, 6, 6), Tag = p };
            b.Click += (_, _) => ApplyPreset(p);
            presetPanel.Controls.Add(b, i % perRow, i / perRow);
        }
        presetBox.Controls.Add(presetPanel);
        right.Controls.Add(presetBox);

        // Dock.Top: при Fill лишняя высота уходила в последнюю строку и подпись «Позвать голосом» уезжала вниз
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 4, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 4, 0, 4) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        var row = 0;
        void Row(string l1, Control c1, string? l2 = null, Control? c2 = null)
        {
            grid.Controls.Add(new Label { Text = l1, AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(0, 7, 6, 0) }, 0, row);
            grid.Controls.Add(c1, 1, row);
            if (l2 is not null && c2 is not null)
            {
                grid.Controls.Add(new Label { Text = l2, AutoSize = true, Anchor = AnchorStyles.Right | AnchorStyles.Top, Margin = new Padding(10, 7, 6, 0) }, 2, row);
                grid.Controls.Add(c2, 3, row);
            }
            else grid.SetColumnSpan(c1, 3);
            row++;
        }
        Row("Имя в меню:", _name, "ID:", _id);
        Row("Тип:", _kind, "Сеть:", _proxy);
        Row("Адрес API:", _url);
        Row("🔑 Ключ:", _key);
        Row("🧠 Модель (задачи):", _model);
        Row("💬 Модель для болтовни:", _chatModel);
        var fbPanel = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        fbPanel.Controls.Add(_fallbackMs);
        fbPanel.Controls.Add(new Label { Text = "мс ждать", AutoSize = true, Margin = new Padding(4, 7, 0, 0) });
        Row("Запасной мозг:", _fallback, "Через:", fbPanel);
        var flags = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0) };
        flags.Controls.Add(_vision); flags.Controls.Add(_tools); flags.Controls.Add(_fromServer);
        flags.Controls.Add(new Label { Text = "  токенов:", AutoSize = true, Margin = new Padding(6, 7, 2, 0) });
        flags.Controls.Add(_maxTokens);
        Row("Флаги:", flags);
        Row("Позвать голосом:", _aliases);
        right.Controls.Add(grid);

        var proxyBox = new GroupBox { Text = " 🌐 Прокси для облака (для всех мозгов с «Сеть: auto/proxy») ", Dock = DockStyle.Fill, AutoSize = true };
        var proxyRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        proxyRow.Controls.Add(new Label { Text = "Адрес:", AutoSize = true, Margin = new Padding(0, 7, 4, 0) });
        proxyRow.Controls.Add(_netProxy);
        var findBtn = new Button { Text = "🔍 Найти рабочий", AutoSize = true };
        findBtn.Click += (_, _) => _ = FindProxyAsync();
        proxyRow.Controls.Add(findBtn);
        proxyRow.Controls.Add(_netProxyInfo);
        proxyBox.Controls.Add(proxyRow);
        right.Controls.Add(proxyBox);

        var logBox = new GroupBox { Text = " Журнал проверки ", Dock = DockStyle.Fill };
        logBox.Controls.Add(_log);
        right.Controls.Add(logBox);

        Controls.Add(right);
        Controls.Add(left);
        Controls.Add(_status);

        _kind.SelectedIndexChanged += (_, _) => UpdateKindUi();
        _key.TextChanged += (_, _) => { if (!_loading) AutoDetectByKey(); };
        _model.DropDown += (_, _) => _ = LoadModelsAsync(false);
        _chatModel.DropDown += (_, _) => _ = LoadModelsAsync(false);
        Shown += (_, _) =>
        {
            _netProxy.Text = _cfg.NetProxy;
            UpdateProxyInfo();
            Reload();
            var active = _cfg.Brains.FindIndex(b => b.Id == _cfg.ActiveBrain.Id);
            if (_cfg.Brains.Count > 0) _list.SelectedIndex = Math.Max(0, active); else LoadForEdit(null);
            Append("Совет: «⚡ Автонастройка всего» сама найдёт прокси, AG Tools и ключи и проверит, кто реально отвечает.");
        };
    }

    private void Append(string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => Append(line)); return; }
        _log.AppendText($"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
    }

    private void UpdateProxyInfo()
    {
        var cur = NetProxy.Current();
        _netProxyInfo.Text = cur is null ? "сейчас: напрямую" : $"сейчас: {cur}";
    }

    private void Reload()
    {
        var sel = _list.SelectedIndex;
        _list.Items.Clear();
        foreach (var b in _cfg.Brains)
            _list.Items.Add($"{(b.Id == _cfg.ActiveBrain.Id ? "★ " : "   ")}{b.Id}  ·  {b.Name}");
        if (sel >= 0 && sel < _list.Items.Count) { _loading = true; _list.SelectedIndex = sel; _loading = false; }
    }

    private void UpdateKindUi()
    {
        var kind = _kind.SelectedItem as string ?? "openai";
        var remote = kind == "openai";
        _url.Enabled = remote;
        _key.Enabled = remote;
        _proxy.Enabled = remote;
        _fromServer.Enabled = remote;
    }

    /// <summary>Заполнить все поля из пресета: новый мозг (или обновить с тем же ID).</summary>
    private void ApplyPreset(BrainPreset p)
    {
        _list.ClearSelected();
        var existing = _cfg.Brains.FirstOrDefault(b => b.Id == p.DefaultId);
        if (existing is not null) { _list.SelectedIndex = _cfg.Brains.IndexOf(existing); _status.Text = $"Такой мозг уже есть — «{existing.Id}», правь его."; return; }
        _editing = null;
        _loading = true;
        _id.Text = p.DefaultId;
        _name.Text = p.DefaultName;
        _kind.SelectedItem = p.Kind;
        _url.Text = p.Url;
        _model.Text = p.DefaultModel;
        _chatModel.Text = p.DefaultId == "antigravity" ? "gemini-4-argon-low" : "";
        _vision.Checked = p.Vision;
        _tools.Checked = p.Tools;
        _fromServer.Checked = p.DefaultId is "antigravity" or "gemini" or "openai" or "openrouter" or "ollama";
        _maxTokens.Value = Math.Clamp(p.MaxTokens, (int)_maxTokens.Minimum, (int)_maxTokens.Maximum);
        _proxy.SelectedItem = p.Url.StartsWith("http://127.", StringComparison.Ordinal) ? "direct" : "auto";
        _aliases.Text = string.Join(", ", DefaultAliases(p.DefaultId));
        _key.Text = p.DefaultId == "antigravity" ? "antigravity-tools" : "";
        _key.PlaceholderText = p.KeyHint;
        FillFallback(null, p.DefaultId);
        _fallbackMs.Value = 6000;
        _loading = false;
        UpdateKindUi();
        _key.Focus();
        _status.Text = p.Kind switch
        {
            "claude-code" => "Claude Code работает по подписке — жми «Сохранить».",
            "codex" => "ChatGPT через Codex: вход — «codex login» в терминале (аккаунт ChatGPT). Модель пустая — та, что Codex берёт сам. Жми «Сохранить».",
            _ => "Пресет применён. Вставь ключ и жми «Сохранить» (ключ уйдёт в переменную окружения).",
        };
    }

    private static string[] DefaultAliases(string id) => id switch
    {
        "openai" => ["гэпэтэ", "gpt", "опенай", "чатгпт"],
        "codex" => ["кодекс", "codex", "чатгпт", "чат гпт", "гпт", "джипити"],
        "anthropic" => ["антропик", "anthropic", "клод апи"],
        "claude" => ["клод", "claude", "клауд"],
        "gemini" => ["жмин", "гемин", "gemini", "гугл"],
        "deepseek" => ["дипсик", "deepseek"],
        "groq" => ["groq", "лама", "llama"],
        "openrouter" => ["роутер", "openrouter"],
        "xai" => ["грок", "grok", "маск"],
        "ollama" => ["оллама", "ollama"],
        "antigravity" => ["антигравит", "гравит", "antigravity", "аг тулз"],
        _ => [],
    };

    /// <summary>По префиксу ключа подсказать провайдера.</summary>
    private void AutoDetectByKey()
    {
        var k = _key.Text.Trim();
        if (k.Length < 8 || k.StartsWith("env:", StringComparison.Ordinal) || k == "antigravity-tools") return;
        var match = Presets.FirstOrDefault(p => p.KeyPrefix.Length > 0 && k.StartsWith(p.KeyPrefix, StringComparison.OrdinalIgnoreCase));
        if (match is not null && _editing is null && _url.Text.Length == 0) ApplyPresetFields(match);
        _status.Text = match is null ? "Ключ вставлен. Проверь адрес API и жми «Сохранить»." : $"Похоже на ключ {match.Emoji} {match.Title}.";
    }

    private void ApplyPresetFields(BrainPreset p)
    {
        _loading = true;
        if (_id.Text.Length == 0) _id.Text = p.DefaultId;
        if (_name.Text.Length == 0) _name.Text = p.DefaultName;
        _kind.SelectedItem = p.Kind;
        _url.Text = p.Url;
        if (_model.Text.Length == 0) _model.Text = p.DefaultModel;
        _loading = false;
    }

    private void FillFallback(string? current, string selfId)
    {
        _fallback.Items.Clear();
        _fallback.Items.Add("(нет)");
        foreach (var b in _cfg.Brains.Where(b => b.Id != selfId && !b.IsLocal)) _fallback.Items.Add(b.Id);
        _fallback.SelectedItem = current is not null && _fallback.Items.Contains(current) ? current : "(нет)";
    }

    private void LoadForEdit(BrainProfile? b)
    {
        _loading = true;
        _editing = b;
        _id.Text = b?.Id ?? "";
        _name.Text = b?.Name ?? "";
        _kind.SelectedItem = b?.Kind ?? "openai";
        _url.Text = b?.Url ?? "";
        _key.Text = b?.Key ?? "";
        _key.PlaceholderText = "ключ, env:ИМЯ_ПЕРЕМЕННОЙ или antigravity-tools";
        _model.Items.Clear();
        _chatModel.Items.Clear();
        _model.Text = b?.Model ?? "";
        _chatModel.Text = b?.ChatModel ?? "";
        _vision.Checked = b?.Vision ?? true;
        _tools.Checked = b?.Tools ?? true;
        _fromServer.Checked = b?.ModelsFromServer ?? false;
        _maxTokens.Value = Math.Clamp(b?.MaxTokens ?? 1500, (int)_maxTokens.Minimum, (int)_maxTokens.Maximum);
        _proxy.SelectedItem = b?.Proxy is "direct" or "proxy" ? b.Proxy : "auto";
        _aliases.Text = b is null ? "" : string.Join(", ", b.Aliases);
        FillFallback(b?.Fallback, b?.Id ?? "");
        _fallbackMs.Value = Math.Clamp(b?.FallbackAfterMs ?? _cfg.FallbackAfterMs, 0, 60000);
        _loading = false;
        UpdateKindUi();
        _status.Text = b is null ? "Новый мозг: выбери пресет сверху или заполни поля." :
            $"Редактируешь: {b.Id}{(b.Id == _cfg.ActiveBrain.Id ? " (активный)" : "")}. Двойной клик по списку — сделать активным.";
    }

    /// <summary>Сырой ключ → переменная окружения пользователя KLAVA_KEY_ID, в профиле — ссылка env:.</summary>
    private string? StoreKey(string id, string raw)
    {
        raw = raw.Trim();
        if (raw.Length == 0) return null;
        if (raw.StartsWith("env:", StringComparison.Ordinal) || raw == "antigravity-tools") return raw;
        var name = "KLAVA_KEY_" + Regex.Replace(id.ToUpperInvariant(), "[^A-Z0-9]", "_");
        Environment.SetEnvironmentVariable(name, raw, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(name, raw, EnvironmentVariableTarget.Process);
        Append($"ключ «{id}» сохранён в переменную окружения пользователя {name} (в klava.json — только ссылка)");
        return "env:" + name;
    }

    private BrainProfile? Save()
    {
        var id = _id.Text.Trim();
        if (id.Length == 0) { _status.Text = "Нужен ID (короткое латинское имя, например openai)."; _id.Focus(); return null; }
        var b = _editing ?? _cfg.Brains.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
        if (b is null) { b = new BrainProfile(); _cfg.Brains.Add(b); }
        b.Id = id;
        b.Name = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : id;
        b.Kind = _kind.SelectedItem as string ?? "openai";
        b.Url = _url.Text.Trim().TrimEnd('/');
        b.Model = _model.Text.Trim();
        b.ChatModel = _chatModel.Text.Trim().Length == 0 ? null : _chatModel.Text.Trim();
        b.Key = StoreKey(id, _key.Text);
        b.Vision = _vision.Checked;
        b.Tools = _tools.Checked;
        b.ModelsFromServer = _fromServer.Checked;
        b.MaxTokens = (int)_maxTokens.Value;
        b.Proxy = _proxy.SelectedItem as string is "direct" or "proxy" ? (string)_proxy.SelectedItem! : null;
        b.Aliases = _aliases.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        b.Fallback = _fallback.SelectedItem as string is { } f && f != "(нет)" ? f : null;
        b.FallbackAfterMs = (int)_fallbackMs.Value;
        _cfg.NetProxy = _netProxy.Text.Trim().Length == 0 ? "auto" : _netProxy.Text.Trim();
        NetProxy.Configured = _cfg.NetProxy;
        _cfg.Save();
        _onSaved();
        _editing = b;
        if (b.Key is not null) { _loading = true; _key.Text = b.Key; _loading = false; }
        Reload();
        _list.SelectedIndex = _cfg.Brains.IndexOf(b);
        _status.Text = $"✓ Сохранено: {b.Id}.";
        return b;
    }

    private void DeleteSelected()
    {
        if (_list.SelectedIndex < 0) return;
        var b = _cfg.Brains[_list.SelectedIndex];
        if (b.Id == _cfg.ActiveBrain.Id) { _status.Text = "Это активный мозг — сначала переключи Клаву на другой."; return; }
        if (MessageBox.Show(this, $"Удалить мозг «{b.Id}»?", "Мозги", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _cfg.Brains.Remove(b);
        _cfg.Save();
        _onSaved();
        Reload();
        _status.Text = $"Удалён: {b.Id}.";
    }

    private async Task ActivateAsync()
    {
        var b = Save();
        if (b is null || _onActivate is null) return;
        Append($"переключаю Клаву на «{b.DisplayName}»…");
        await _onActivate(b, null);
        Reload();
    }

    private string? CurrentKey() => ApiKeys.Resolve(_key.Text.Trim().Length == 0 ? null : _key.Text.Trim());

    private async Task LoadModelsAsync(bool verbose)
    {
        if ((_kind.SelectedItem as string) == "codex")
        {
            // ChatGPT через Codex: список у самого Codex; пустая модель — та, что Codex берёт по умолчанию
            var cx = CodexCli.Models();
            var keep = _model.Text;
            _model.Items.Clear(); _model.Items.Add(""); _model.Items.AddRange([.. cx]);
            _model.Text = keep;
            if (verbose) Append(cx.Count > 0 ? $"модели ChatGPT (Codex): {string.Join(", ", cx)}" : "Codex ещё не отдал список моделей — запусти его хоть раз");
            return;
        }
        if ((_kind.SelectedItem as string) != "openai" || _url.Text.Trim().Length == 0) return;
        if (_model.Items.Count > 0 && !verbose) return;
        var mode = _proxy.SelectedItem as string ?? "auto";
        var list = await AutoSetup.ModelsAsync(_url.Text.Trim(), CurrentKey(), mode, CancellationToken.None);
        if (IsDisposed) return;
        if (list.Count == 0) { if (verbose) Append($"{_url.Text}: список моделей не пришёл (ключ? прокси? сервер выключен?)"); return; }
        var b = _editing;
        var include = string.IsNullOrWhiteSpace(b?.ModelInclude) ? null : new Regex(b!.ModelInclude!, RegexOptions.IgnoreCase);
        var exclude = string.IsNullOrWhiteSpace(b?.ModelExclude) ? null : new Regex(b!.ModelExclude!, RegexOptions.IgnoreCase);
        var shown = list.Where(m => (include is null || include.IsMatch(m)) && (exclude is null || !exclude.IsMatch(m))).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var m1 = _model.Text; var m2 = _chatModel.Text;
        _model.Items.Clear(); _model.Items.AddRange(shown);
        _chatModel.Items.Clear(); _chatModel.Items.Add(""); _chatModel.Items.AddRange(shown);
        _model.Text = m1; _chatModel.Text = m2;
        if (verbose) Append($"{_url.Text}: моделей {list.Count}, в списке {shown.Length}");
    }

    private async Task TestAsync()
    {
        var kind = _kind.SelectedItem as string ?? "openai";
        if (kind == "claude-code")
        {
            var ok = await ClaudeCode.LoggedInAsync();
            Append(ok switch { true => "✓ Claude Code залогинен", false => "✗ Claude Code не залогинен — в терминале: claude auth login", _ => "? Claude Code не ответил" });
            return;
        }
        if (kind == "codex")
        {
            if (CodexCli.FindExe() is not { } cx) { Append("✗ Codex не найден — поставь Codex для Windows или «npm i -g @openai/codex»"); return; }
            if (!CodexCli.LoggedIn()) { Append("✗ Codex не залогинен — в терминале: codex login (аккаунт ChatGPT)"); return; }
            Append($"🔌 Codex ({cx})…");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var said = new System.Text.StringBuilder();
                await foreach (var t in CodexCli.AskAsync(_model.Text.Trim(), "low", "Ответь одним словом: ок", null, TimeSpan.FromSeconds(60), CancellationToken.None))
                    said.Append(t);
                Append($"   ✓ {sw.ElapsedMilliseconds} мс: «{said.ToString().Trim()}»");
            }
            catch (InvalidOperationException ex) { Append($"   ✗ {ex.Message}"); }
            var known = CodexCli.CachedModels();
            if (known.Count > 0) Append($"   модели подписки (кэш Codex): {string.Join(", ", known)}");
            return;
        }
        if (kind is "own" or "flashnext") { Append("Свои мозги поднимаются вместе с Клавой — проверь переключением."); return; }
        var url = _url.Text.Trim();
        if (url.Length == 0) { Append("Адрес API пуст."); return; }
        var mode = _proxy.SelectedItem as string ?? "auto";
        var key = CurrentKey();
        foreach (var model in new[] { _chatModel.Text.Trim(), _model.Text.Trim() }.Where(m => m.Length > 0).Distinct())
        {
            Append($"🔌 {model} ({(NetProxy.ProxyFor(url, mode) is { } px ? "через " + px : "напрямую")})…");
            var (said, ms, err) = await AutoSetup.PingChatAsync(url, key, model, mode, CancellationToken.None);
            Append(err is null ? $"   ✓ {ms} мс: «{said}»" : $"   ✗ {err}");
        }
    }

    private async Task FindProxyAsync()
    {
        _cfg.NetProxy = _netProxy.Text.Trim().Length == 0 ? "auto" : _netProxy.Text.Trim();
        NetProxy.Configured = _cfg.NetProxy;
        var p = await NetProxy.DetectAsync(new Progress<string>(Append), force: true);
        UpdateProxyInfo();
        Append(p is null ? "рабочего прокси нет — облако пойдёт напрямую" : $"рабочий прокси: {p}");
    }

    private async Task AutoSetupAsync()
    {
        Append("=== Автонастройка ===");
        _cfg.NetProxy = _netProxy.Text.Trim().Length == 0 ? "auto" : _netProxy.Text.Trim();
        NetProxy.Configured = _cfg.NetProxy;
        try { await AutoSetup.RunAsync(_cfg, new Progress<string>(Append)); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException) { Append($"✗ {ex.Message}"); }
        _onSaved();
        UpdateProxyInfo();
        Reload();
        if (_list.SelectedIndex >= 0) LoadForEdit(_cfg.Brains[_list.SelectedIndex]);
    }
}
