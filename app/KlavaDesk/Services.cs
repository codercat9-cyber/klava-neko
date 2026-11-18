using System.ComponentModel;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Поднимает всё, что нужно Клаве: мозг (llama-server через START_FLASHNEXT.ps1), слух (whisper-server)
/// и голос (Python, Qwen3-TTS). Порядок строгий: сначала мозг, потом остальное — пока FlashNext грузится,
/// он забирает почти всю память, и соседи падали с «файл подкачки слишком мал».
/// Всё, что Клава запустила сама, живёт ровно столько же, сколько KlavaDesk (Job Object).
/// Уже запущенный кем-то мозг (например, для Studio) она только использует и не гасит.
/// </summary>
internal sealed class Services
{
    private const int Attempts = 3;

    private readonly KlavaConfig _cfg;
    // свои сервисы на 127.0.0.1 — строго напрямую: системный прокси (Karing) иначе заворачивал бы и их
    private readonly HttpClient _http = NetProxy.Client(null, TimeSpan.FromSeconds(3));
    // Всё, что запустила сама. Job Object ловит не всех: llama-server, запущенный из окна PowerShell,
    // в него не попадает и после выхода Клавы держал 42 ГБ ОЗУ — поэтому при выходе гасим явно, деревом.
    private readonly List<Process> _children = new();

    public Services(KlavaConfig cfg) => _cfg = cfg;

    /// <summary>Погасить всё, что Клава запустила сама (вместе с дочерними процессами). Чужое не трогаем.</summary>
    public void StopAll()
    {
        foreach (var p in _children)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception ex) { Log.Error($"не смогла погасить PID {p.Id}", ex); }
        }
        _children.Clear();
    }

    public async Task<bool> LlmAliveAsync() => await HttpOkAsync($"{_cfg.LlmUrl.TrimEnd('/')}/health");

    public async Task<bool> TtsAliveAsync() => _cfg.LiteSpeech ? Lite.Models(_cfg).CanSpeak : await HttpOkAsync($"{_cfg.TtsUrl.TrimEnd('/')}/health");

    /// <summary>
    /// Лёгкий комплект: сервисов нет, модели грузятся в процесс. Загрузить и прогнать разок вхолостую —
    /// первая настоящая фраза не должна ждать загрузки и прогрева ONNX Runtime.
    /// </summary>
    private static async Task<bool> LiteLoadAsync(string what, Action load, IProgress<string> log)
    {
        log.Report($"{what} (в процессе) загружается…");
        try
        {
            await Task.Run(load);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            Log.Error($"{what} (в процессе)", ex);
            log.Report($"{what} (в процессе) не загрузился: {ex.Message}");
            return false;
        }
    }

    /// <summary>Размер контекста работающего мозга (из /props); 0 — если не узнать.</summary>
    public async Task<int> LlmContextAsync()
    {
        try
        {
            var props = JsonNode.Parse(await _http.GetStringAsync($"{_cfg.LlmUrl.TrimEnd('/')}/props"));
            return props?["default_generation_settings"]?["n_ctx"]?.GetValue<int>() ?? 0;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return 0;
        }
    }

    /// <summary>Файл модели работающего llama-server (/props → model_path); null — не отвечает.</summary>
    public async Task<string?> LlmModelPathAsync()
    {
        try
        {
            var props = JsonNode.Parse(await _http.GetStringAsync($"{_cfg.LlmUrl.TrimEnd('/')}/props"));
            return props?["model_path"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Свой llama-server (own или окно FlashNext) подняла сама Клава и он ещё жив.</summary>
    public bool OwnsLlm => _brainProc is { HasExited: false };

    /// <summary>Мозг запущен с проектором картинок (/props → modalities.vision).</summary>
    private async Task<bool> LlmVisionAsync()
    {
        try
        {
            var props = JsonNode.Parse(await _http.GetStringAsync($"{_cfg.LlmUrl.TrimEnd('/')}/props"));
            return props?["modalities"]?["vision"]?.GetValue<bool>() ?? false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool LlmProcessRunning() => Process.GetProcessesByName("llama-server").Length > 0;

    /// <summary>На порту своего мозга кто-то слушает (свой из прошлого запуска или чужой).</summary>
    public bool LlmPortOpen() => PortOpen(_cfg.LlmUrl);

    /// <summary>Погасить чужой мозг (только с согласия друга — см. OverlayForm).</summary>
    public static async Task StopLlmAsync()
    {
        foreach (var p in Process.GetProcessesByName("llama-server"))
        {
            try { p.Kill(entireProcessTree: true); await p.WaitForExitAsync(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception ex) { Log.Error("не смогла погасить llama-server", ex); }
            finally { p.Dispose(); }
        }
        await Task.Delay(2000);   // expert_pin замечает, что сервера нет, и отпускает закреплённую память
    }

    /// <summary>Поднять или проверить мозг из профиля: свой llama-server, облако/прокси или Claude Code.</summary>
    public Task<bool> EnsureLlmAsync(BrainProfile p, TimeSpan wait, IProgress<string> log) => p.Kind switch
    {
        "own" => EnsureOwnBrainAsync(wait, log),
        "flashnext" => EnsureFlashNextAsync(wait, log),
        "claude-code" => EnsureClaudeCodeAsync(log),
        "codex" => Task.FromResult(EnsureCodex(log)),
        _ => EnsureRemoteAsync(p, log),
    };

    /// <summary>ChatGPT через Codex: CLI на месте и друг в него вошёл (проверка файлов, без сети — запрос стоит ~6 с).</summary>
    private static bool EnsureCodex(IProgress<string> log)
    {
        if (CodexCli.FindExe() is null)
        {
            log.Report("Codex не установлен: поставь Codex для Windows (Microsoft Store) или «npm i -g @openai/codex».");
            return false;
        }
        if (!CodexCli.LoggedIn())
        {
            log.Report("Codex не залогинен: открой терминал, набери «codex login» и войди аккаунтом ChatGPT — потом переключи меня снова.");
            return false;
        }
        return true;
    }

    /// <summary>Свой мозг (own или FlashNext), если его подняла сама Клава, — погасить: освобождает видеокарту.</summary>
    public void StopOwnLlm()
    {
        if (_brainProc is null) return;
        try { if (!_brainProc.HasExited) _brainProc.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception ex) { Log.Error("не смогла погасить свой мозг", ex); }
        _children.Remove(_brainProc);
        _brainProc = null;
        BrainSees = false;
        Log.Write("свой мозг погашен — видеокарта свободна");
    }

    /// <summary>Процесс своего мозга, запущенный Клавой (llama-server или окно START_FLASHNEXT).</summary>
    private Process? _brainProc;

    /// <summary>
    /// Облако или прокси с OpenAI-совместимым API: ключ на месте, адрес отвечает. Antigravity Tools, если не
    /// запущен, запускается (как обычная программа друга — с выходом Клавы он не гаснет).
    /// </summary>
    private async Task<bool> EnsureRemoteAsync(BrainProfile p, IProgress<string> log)
    {
        var key = ApiKeys.Resolve(p.Key);
        if (p.Key is not null && key is null) { log.Report($"Нет ключа для «{p.DisplayName}»: {ApiKeys.Hint(p.Key)}."); return false; }
        var url = _cfg.ApiUrl(p);
        if (!string.IsNullOrWhiteSpace(p.Launch) && !PortOpen(url))
        {
            var exe = Environment.ExpandEnvironmentVariables(p.Launch);
            if (!File.Exists(exe)) { log.Report($"«{p.DisplayName}» не отвечает, и нет {exe}"); return false; }
            log.Report($"запускаю {Path.GetFileNameWithoutExtension(exe)}…");
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose(); }
            catch (Win32Exception ex) { log.Report($"не запустился {exe}: {ex.Message}"); return false; }
            if (!await WaitAsync(() => Task.FromResult(PortOpen(url)), TimeSpan.FromSeconds(p.IsLocalServer ? 150 : 30), null))
            {
                log.Report($"«{p.DisplayName}»: {Path.GetFileNameWithoutExtension(exe)} не открыл порт за {(p.IsLocalServer ? 150 : 30)} с");
                return false;
            }
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{url}/models");
        if (key is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        try
        {
            var remote = NetProxy.ClientFor(url, p.Proxy, TimeSpan.FromSeconds(12));
            using var r = await remote.SendAsync(req);
            if (r.IsSuccessStatusCode) return true;
            var body = await r.Content.ReadAsStringAsync();
            log.Report((int)r.StatusCode switch
            {
                503 when body.Contains("disabled", StringComparison.OrdinalIgnoreCase)
                    => $"«{p.DisplayName}»: в Antigravity Tools выключен API-прокси — открой вкладку API Proxy и нажми запуск.",
                401 or 403 => $"«{p.DisplayName}»: ключ не подошёл ({(int)r.StatusCode}). Где он лежит: {ApiKeys.Hint(p.Key)}.",
                _ => $"«{p.DisplayName}»: {(int)r.StatusCode} {(body.Length > 160 ? body[..160] : body)}",
            });
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.Report($"«{p.DisplayName}» не отвечает: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Модели, которые отдаёт сервер мозга (/models), с фильтром из профиля. У AG Tools это всё, что есть на его
    /// аккаунтах; у Gemini по ключу — чатовые gemini-*. Пустой список — сервер не ответил (прокси выключен и т. п.).
    /// </summary>
    public async Task<List<string>> ListModelsAsync(BrainProfile p)
    {
        // ChatGPT через Codex: модели подписки знает сам Codex (его кэш и модель из config.toml), сети не нужно
        if (p.Kind == "codex") return CodexCli.Models();
        var key = ApiKeys.Resolve(p.Key);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{_cfg.ApiUrl(p)}/models");
        if (key is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        try
        {
            var remote = NetProxy.ClientFor(_cfg.ApiUrl(p), p.Proxy, TimeSpan.FromSeconds(12));
            using var r = await remote.SendAsync(req);
            if (!r.IsSuccessStatusCode) return [];
            var data = JsonNode.Parse(await r.Content.ReadAsStringAsync())?["data"] as JsonArray;
            var include = string.IsNullOrWhiteSpace(p.ModelInclude) ? null : new System.Text.RegularExpressions.Regex(p.ModelInclude, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var exclude = string.IsNullOrWhiteSpace(p.ModelExclude) ? null : new System.Text.RegularExpressions.Regex(p.ModelExclude, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return (data ?? [])
                .Select(m => m?["id"]?.GetValue<string>())
                .OfType<string>()
                .Select(id => id.StartsWith("models/", StringComparison.Ordinal) ? id[7..] : id)   // Gemini отдаёт «models/…»
                .Where(id => (include is null || include.IsMatch(id)) && (exclude is null || !exclude.IsMatch(id)))
                .Distinct()
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Claude Code: CLI на месте и залогинен (подписка друга).</summary>
    private static async Task<bool> EnsureClaudeCodeAsync(IProgress<string> log)
    {
        if (ClaudeCode.FindExe() is null) { log.Report("Claude Code не установлен (нет claude.exe)."); return false; }
        switch (await ClaudeCode.LoggedInAsync())
        {
            case true: return true;
            case false:
                log.Report("Claude Code не залогинен. Открой терминал, набери «claude auth login» и войди своим аккаунтом Claude — потом переключи меня снова.");
                return false;
            default:
                log.Report("Claude Code не ответил на проверку входа (claude auth status).");
                return false;
        }
    }

    /// <summary>
    /// Свой мозг: llama-server с Qwen3-VL-8B целиком на видеокарте, на своём порту (llmUrl).
    /// Зрение (mmproj) — в ОЗУ. Характер и история влезают в кэш слота, ответ начинается через секунду.
    /// </summary>
    private async Task<bool> EnsureOwnBrainAsync(TimeSpan wait, IProgress<string> log)
    {
        if (await LlmAliveAsync())
        {
            BrainSees = await LlmVisionAsync();   // уже запущен (прошлый запуск Клавы) — спросим, видит ли
            return true;
        }
        var model = _cfg.Abs(_cfg.BrainModel);
        var mmproj = _cfg.Abs(_cfg.BrainMmproj);
        if (!File.Exists(_cfg.Abs(_cfg.BrainExe)) || !File.Exists(model)) { log.Report($"нет {_cfg.Abs(_cfg.BrainExe)} или {model}"); return false; }
        var u = new Uri(_cfg.LlmUrl);
        // генерация — физические ядра минус два (система, окно Клавы, звук, whisper), промпт — все физические
        var cores = Math.Max(2, Environment.ProcessorCount / 2);
        var threads = _cfg.BrainThreads > 0 ? _cfg.BrainThreads : Math.Max(2, cores - 2);
        var args = new List<string>
        {
            "-m", model, "--alias", _cfg.LlmModel, "-c", _cfg.BrainCtx.ToString(),
            // два слота с общим пулом кэша: разговор (слот 0) и разовые вопросы (слот 1) не вытесняют друг друга
            "--parallel", "2", "--kv-unified", "-t", threads.ToString(), "-tb", Math.Max(threads, cores).ToString(), "--jinja", "--no-webui",
            "--host", u.Host, "--port", u.Port.ToString(),
        };
        // выгрузка на видеокарту и сэмплинг — из klava.json (brainArgs); клиент может перебить температурой из профиля
        args.AddRange(_cfg.BrainArgs);
        // --fit делит видеопамять по тому, что свободно сейчас. Голос и слух ещё не поднялись (мозг стартует первым) —
        // оставить место и им, иначе они потом не влезут; уже работают — хватит обычного запаса.
        if (!_cfg.BrainArgs.Contains("--fit-target"))
        {
            var voiceUp = _cfg.LiteSpeech || (await TtsAliveAsync() && PortOpen(_cfg.SttUrl));
            var target = _cfg.BrainFitTargetMiB + (voiceUp ? 0 : _cfg.VoiceVramMiB);
            args.AddRange(["--fit-target", target.ToString()]);
            Log.Write($"мозг: --fit оставит свободными {target} МБ видеопамяти{(voiceUp ? "" : " (голос и слух ещё впереди)")}");
        }
        if (File.Exists(mmproj))
        {
            args.AddRange(["--mmproj", mmproj, "--no-mmproj-offload", "--image-max-tokens", _cfg.BrainImageTokens.ToString()]);
            BrainSees = true;
        }
        log.Report("просыпаюсь…");
        var t0 = DateTime.UtcNow;
        // сигнал «видеопамять занята» — свежий на каждый запуск (первый ждёт уже запуск Клавы — его не подменять)
        if (BrainVramTaken.Task.IsCompleted) BrainVramTaken = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var env = new Dictionary<string, string>();
        if (_cfg.BrainOffloadMinBatch > 0) env["GGML_OP_OFFLOAD_MIN_BATCH"] = _cfg.BrainOffloadMinBatch.ToString();
        var vramBefore = GpuMemoryUsedMiB();
        var modelBytes = new FileInfo(model).Length;
        using var loading = new CancellationTokenSource();
        var ok = await StartWithRetryAsync("мозг", () =>
        {
            _brainProc = Start(_cfg.Abs(_cfg.BrainExe), Path.GetDirectoryName(_cfg.Abs(_cfg.BrainExe))!, env, [.. args]);
            // загрузка 28 ГБ в память и счёт промпта не должны отнимать процессор у окон и звука
            try { _brainProc.PriorityClass = ProcessPriorityClass.BelowNormal; }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            _ = WatchBrainLoadAsync(_brainProc, modelBytes, vramBefore, log, loading.Token);
            return _brainProc;
        }, LlmAliveAsync, wait, 1);
        // закрепить ~23 ГБ получается не всегда (ОЗУ занята играми, браузером): тогда модель — отображением файла,
        // промпт на видеокарте медленнее (~170 т/с против ~550), но мозг есть
        var pinIdx = args.IndexOf("--load-mode");
        if (!ok && pinIdx >= 0 && pinIdx + 1 < args.Count && args[pinIdx + 1] == "none" && _brainProc is { HasExited: true })
        {
            Log.Write("мозг: не закрепил модель в ОЗУ — запускаю с отображением файла");
            log.Report("памяти впритык — гружу мозг попроще…");
            args.RemoveRange(pinIdx, 2);
            ok = await StartWithRetryAsync("мозг", () =>
            {
                _brainProc = Start(_cfg.Abs(_cfg.BrainExe), Path.GetDirectoryName(_cfg.Abs(_cfg.BrainExe))!, env, [.. args]);
                try { _brainProc.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                return _brainProc;
            }, LlmAliveAsync, wait, 1);
        }
        loading.Cancel();
        BrainVramTaken.TrySetResult(ok);
        Log.Write(ok ? $"мозг готов за {(DateTime.UtcNow - t0).TotalSeconds:F0} с" : "мозг не поднялся — смотри data\\logs\\llama-server-.log");
        if (!ok) BrainSees = false;
        return ok;
    }

    /// <summary>
    /// Свой мозг уже забрал свою видеопамять (или не поднялся): голос и слух можно грузить параллельно с остатком
    /// модели, а не ждать минуту, пока 28 ГБ скопируются в ОЗУ. --fit-target уже оставил им место.
    /// </summary>
    public TaskCompletionSource<bool> BrainVramTaken { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Пока мозг грузится: раз в 10 с — сколько модели уже в памяти (пузырём: «загружаю мозг: 60 %»), и как только
    /// видеопамять выросла на гигабайт и больше и перестала расти — сигнал, что голосу можно стартовать.
    /// </summary>
    private async Task WatchBrainLoadAsync(Process p, long modelBytes, int vramBefore, IProgress<string> log, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        var lastReport = DateTime.UtcNow;
        int prevVram = -1, stable = 0;
        try
        {
            while (!ct.IsCancellationRequested && !p.HasExited)
            {
                await Task.Delay(1000, ct);
                if (!BrainVramTaken.Task.IsCompleted)
                {
                    var vram = GpuMemoryUsedMiB();
                    if (vram < 0 && DateTime.UtcNow - start > TimeSpan.FromSeconds(25)) BrainVramTaken.TrySetResult(true);   // без nvidia-smi — по времени
                    else if (vram >= 0 && vramBefore >= 0 && vram - vramBefore > 1024)
                    {
                        stable = Math.Abs(vram - prevVram) < 64 ? stable + 1 : 0;
                        if (stable >= 2)
                        {
                            Log.Write($"мозг: занял {vram - vramBefore} МБ видеопамяти — голос и слух грузятся параллельно");
                            BrainVramTaken.TrySetResult(true);
                        }
                    }
                    prevVram = vram;
                }
                if (DateTime.UtcNow - lastReport >= TimeSpan.FromSeconds(10))
                {
                    lastReport = DateTime.UtcNow;
                    // сколько файла модели уже прочитано (закреплённую память драйвер выделяет сразу — по ней не понять)
                    if (GetProcessIoCounters(p.Handle, out var io) && modelBytes > 0)
                        log.Report($"загружаю мозг в память: {Math.Clamp((long)io.ReadTransferCount * 100 / modelBytes, 1, 99)} %…");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }   // процесс уже закрыт
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

    /// <summary>Сколько видеопамяти занято сейчас (nvidia-smi), −1 — не узнать.</summary>
    private static int GpuMemoryUsedMiB()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p is null) return -1;
            var line = p.StandardOutput.ReadLine();
            if (!p.WaitForExit(3000)) { try { p.Kill(); } catch (InvalidOperationException) { } return -1; }
            return int.TryParse(line?.Trim(), out var v) ? v : -1;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException) { return -1; }
    }

    /// <summary>Мозг умеет смотреть на картинки (запущен с проектором зрения).</summary>
    public bool BrainSees { get; private set; }

    /// <summary>FlashNext: если не отвечает — ждать уже грузящийся или запустить START_FLASHNEXT.ps1 в режиме Клавы.</summary>
    private async Task<bool> EnsureFlashNextAsync(TimeSpan wait, IProgress<string> log)
    {
        if (await LlmAliveAsync()) return true;
        if (!LlmProcessRunning())
        {
            if (!_cfg.StartBrain) { log.Report("мозг не запущен (startBrain выключен в klava.json)"); return false; }
            if (!File.Exists(_cfg.Abs(_cfg.BrainScript))) { log.Report($"нет {_cfg.BrainScript}"); return false; }
            var p = StartPowerShell(_cfg.Abs(_cfg.BrainScript), "-Mode", _cfg.BrainMode);
            if (p is null) { log.Report("не нашла ни pwsh, ни powershell"); return false; }
            _children.Add(p);
            _brainProc = p;
            try { Native.TieToLifetime(p); }
            catch (InvalidOperationException ex) { Log.Error("мозг не привязан к жизни Клавы", ex); }
            Log.Write($"запущен мозг: {_cfg.BrainScript} -Mode {_cfg.BrainMode} (PID {p.Id})");
        }
        log.Report("просыпаюсь… мозг грузится, это минута-другая");
        var t0 = DateTime.UtcNow;
        var ok = await WaitAsync(LlmAliveAsync, wait, null);
        Log.Write(ok ? $"мозг готов за {(DateTime.UtcNow - t0).TotalSeconds:F0} с" : "мозг не поднялся");
        return ok;
    }

    /// <summary>Процесс голоса, поднятый Клавой (для RestartTtsAsync — прибить и запустить заново).</summary>
    private Process? _ttsProc;
    /// <summary>Процесс speaker_id.py, поднятый Клавой.</summary>
    private Process? _speakerProc;

    public async Task<bool> SpeakerAliveAsync() => Lite.InProcessSpeakers(_cfg) || await HttpOkAsync($"{_cfg.SpeakerIdUrl.TrimEnd('/')}/health");

    /// <summary>Запустить сервер голосовых профилей, если не запущен; ждать готовности.</summary>
    public async Task<bool> EnsureSpeakerAsync(TimeSpan wait, IProgress<string> log)
    {
        if (Lite.InProcessSpeakers(_cfg)) return await LiteLoadAsync("голосовые профили", () => Lite.Speakers(_cfg), log);
        if (await SpeakerAliveAsync()) return true;
        var py = _cfg.Abs(_cfg.TtsPython);   // тот же venv, что и у TTS — resemblyzer поставлен туда
        var script = _cfg.Abs(_cfg.SpeakerIdScript);
        if (!File.Exists(py) || !File.Exists(script)) { log.Report($"нет {py} или {script}"); return false; }
        var port = new Uri(_cfg.SpeakerIdUrl).Port;
        log.Report("голосовые профили загружаются…");
        return await StartWithRetryAsync("голосовые профили",
            () => _speakerProc = Start(py, Path.GetDirectoryName(script)!, script, "--port", port.ToString()),
            SpeakerAliveAsync, wait);
    }

    private readonly object _ttsStartGate = new();
    private Task<bool>? _ttsStarting;

    /// <summary>
    /// Запустить голос, если не запущен; ждать готовности до <paramref name="wait"/>. Один запуск за раз: 10.10 20:49
    /// сервер голоса упал при старте, и пока шёл повтор, сторож «голос завис» запустил свой — два сервера в одну
    /// миллисекунду, второй не открыл лог, ошибка оборвала запуск сервисов (порог голосовых профилей так и не
    /// подхватился — друзей перестало узнавать), а два голоса в видеокарте не влезли и синтез шёл в 3–7 раз медленнее.
    /// Теперь второй вызов ждёт тот же запуск.
    /// </summary>
    public Task<bool> EnsureTtsAsync(TimeSpan wait, IProgress<string> log)
    {
        lock (_ttsStartGate)
        {
            if (_ttsStarting is { IsCompleted: false } running)
            {
                Log.Write("голос: уже запускается — жду тот же запуск, второй не поднимаю");
                return running;
            }
            _ttsStarting = EnsureTtsCoreAsync(wait, log);
            return _ttsStarting;
        }
    }

    private async Task<bool> EnsureTtsCoreAsync(TimeSpan wait, IProgress<string> log)
    {
        if (_cfg.LiteSpeech)
            return await LiteLoadAsync("голос", () => Lite.Voice(_cfg).Synth("Мур.", _cfg.LiteVoiceId, _cfg.LiteVoiceSpeed, _cfg.LiteVoiceSteps, _cfg.LiteVoicePitch), log);
        if (await TtsAliveAsync()) return true;
        var py = _cfg.Abs(_cfg.TtsPython);
        var script = _cfg.Abs(_cfg.TtsScript);
        if (!File.Exists(py) || !File.Exists(script)) { log.Report($"нет {py} или {script}"); return false; }
        var port = new Uri(_cfg.TtsUrl).Port;

        // Если порт занят зависшим процессом, но /health не отвечает — освобождаем порт перед запуском
        KillProcessOnPort(port);

        log.Report("голос загружается…");
        return await StartWithRetryAsync("голос", () => _ttsProc = Start(py, Path.GetDirectoryName(script)!, script, "--port", port.ToString()),
            TtsAliveAsync, wait);
    }

    private Process? _musicProc;

    public async Task<bool> MusicAliveAsync() => await HttpOkAsync($"{_cfg.MusicUrl.TrimEnd('/')}/health");

    /// <summary>Музыкальный сервис (стемы, доли, ноты, аккорды, пение): поднять, если не поднят. Модели он грузит сам,
    /// когда пришла задача, так что старт — секунды.</summary>
    public async Task<bool> EnsureMusicAsync(IProgress<string> log)
    {
        if (await MusicAliveAsync()) return true;
        var py = _cfg.Abs(_cfg.TtsPython);
        var script = _cfg.Abs(_cfg.MusicScript);
        if (!File.Exists(py) || !File.Exists(script)) { log.Report($"нет {py} или {script} — музыкальный слух не установлен"); return false; }
        var port = new Uri(_cfg.MusicUrl).Port;
        if (_musicProc is { HasExited: false }) { try { _musicProc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        KillProcessOnPort(port);
        log.Report("музыкальный слух просыпается…");
        return await StartWithRetryAsync("музыка",
            () => _musicProc = Start(py, Path.GetDirectoryName(script)!, script, "--port", port.ToString(), "--stt", _cfg.SttUrl, "--tts", _cfg.TtsUrl),
            MusicAliveAsync, TimeSpan.FromSeconds(40), 2);
    }

    /// <summary>Принудительно завершить процессы, держащие порт (например зависший python на 8095).</summary>
    public static void KillProcessOnPort(int port)
    {
        try
        {
            using var ps = Process.Start(new ProcessStartInfo("powershell", $"-NoProfile -Command \"Get-NetTCPConnection -LocalPort {port} -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique | ForEach-Object {{ Stop-Process -Id $_ -Force -ErrorAction SilentlyContinue }}\"") { CreateNoWindow = true, UseShellExecute = false });
            ps?.WaitForExit(3000);
        }
        catch { }
    }

    /// <summary>
    /// Перезапустить голос: принудительно гасит зависший процесс TTS (включая внешние python на порту 8095)
    /// и поднимает сервер заново с прогревом CUDA-графов.
    /// </summary>
    public async Task<bool> RestartTtsAsync(IProgress<string> log)
    {
        if (_cfg.LiteSpeech) return await EnsureTtsAsync(TimeSpan.Zero, log);   // лёгкий голос в процессе — перезапускать нечего
        // голос и так сейчас поднимается — не убивать его посреди загрузки, а дождаться
        Task<bool>? running;
        lock (_ttsStartGate) running = _ttsStarting is { IsCompleted: false } t ? t : null;
        if (running is not null)
        {
            Log.Write("голос: перезапуск не нужен — он как раз поднимается, жду");
            return await running;
        }
        log.Report("голос перезапускается…");
        if (_ttsProc is { HasExited: false } p)
        {
            try { p.Kill(entireProcessTree: true); await p.WaitForExitAsync(); }
            catch (Exception) { }
        }
        if (_ttsProc is not null) _children.Remove(_ttsProc);
        _ttsProc = null;

        var u = new Uri(_cfg.TtsUrl);
        KillProcessOnPort(u.Port);

        await Task.Delay(1000);
        return await EnsureTtsAsync(TimeSpan.FromSeconds(120), log);
    }

    /// <summary>
    /// Слух: whisper large-v3-turbo на видеокарте (точный русский, фраза за доли секунды). Если он не поднялся —
    /// скорее всего, не хватило видеопамяти — запасной маленький whisper на процессоре.
    /// </summary>
    public async Task<bool> EnsureSttAsync(TimeSpan wait, IProgress<string> log)
    {
        if (_cfg.LiteSpeech)
            return await LiteLoadAsync("слух", () => Lite.Ears(_cfg).Transcribe(new float[16000 / 2]), log);
        if (PortOpen(_cfg.SttUrl)) return true;
        if (!File.Exists(_cfg.Abs(_cfg.SttExe))) { log.Report($"нет {_cfg.Abs(_cfg.SttExe)}"); return false; }
        log.Report("слух загружается…");
        var gpuModel = _cfg.Abs(_cfg.SttModel);
        if (_cfg.SttGpu && File.Exists(gpuModel) && await StartSttAsync(gpuModel, gpu: true, wait))
        {
            SttOnGpu = true;
            return true;
        }
        var cpuModel = _cfg.Abs(_cfg.SttFallbackModel);
        if (!File.Exists(cpuModel)) { log.Report($"нет {cpuModel}"); return false; }
        if (_cfg.SttGpu) Log.Write("слух: на видеокарте не поднялся — запасной маленький whisper на процессоре");
        return await StartSttAsync(cpuModel, gpu: false, wait);
    }

    /// <summary>Слух работает на видеокарте (иначе — запасной на процессоре).</summary>
    public bool SttOnGpu { get; private set; }

    /// <summary>«Пнуть Whisper» — принудительно погасить зависший процесс whisper-server и поднять службу заново.</summary>
    public async Task<bool> RestartSttAsync(IProgress<string> log)
    {
        if (_cfg.LiteSpeech) return await EnsureSttAsync(TimeSpan.Zero, log);
        log.Report("перезапускаю службу Whisper…");
        foreach (var p in Process.GetProcessesByName("whisper-server"))
        {
            try { p.Kill(entireProcessTree: true); await p.WaitForExitAsync(); }
            catch { }
            finally { p.Dispose(); }
        }
        await Task.Delay(1000);
        return await EnsureSttAsync(TimeSpan.FromSeconds(10), log);
    }

    /// <summary>Проверить состояние TTS-сервера (Qwen3-TTS): порт, http /health, пинг в мс.</summary>
    public async Task<(bool Alive, long PingMs, string Info)> PingTtsAsync()
    {
        if (_cfg.LiteSpeech)
            return Lite.Models(_cfg).CanSpeak ? (true, 0, "лёгкий: Supertonic-3 на процессоре") : (false, 0, "нет моделей models\\lite — запусти tools\\fetch_lite.ps1");
        var sw = Stopwatch.StartNew();
        try
        {
            var u = new Uri(_cfg.TtsUrl);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_cfg.TtsUrl.TrimEnd('/')}/health");
            using var resp = await _http.SendAsync(req);
            sw.Stop();
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync();
                return (true, sw.ElapsedMilliseconds, $"OK (Qwen3-TTS, {sw.ElapsedMilliseconds} мс)");
            }
            return (false, sw.ElapsedMilliseconds, $"HTTP {(int)resp.StatusCode} (загрузка)");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    /// <summary>Проверить состояние Whisper-сервера: порт, процесс, пинг в мс.</summary>
    public async Task<(bool Alive, long PingMs, string Info)> PingSttAsync()
    {
        if (_cfg.LiteSpeech)
            return Lite.Models(_cfg).CanHear ? (true, 0, "лёгкий: GigaAM v3 на процессоре") : (false, 0, "нет моделей models\\lite — запусти tools\\fetch_lite.ps1");
        var sw = Stopwatch.StartNew();
        try
        {
            var u = new Uri(_cfg.SttUrl);
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(1500);
            try { await client.ConnectAsync(u.Host, u.Port, cts.Token); }
            catch (OperationCanceledException) { return (false, sw.ElapsedMilliseconds, "Таймаут подключения (1.5 с)"); }
            catch (SocketException) { return (false, sw.ElapsedMilliseconds, "Порт закрыт"); }
            if (!client.Connected)
                return (false, sw.ElapsedMilliseconds, "Порт закрыт");
            sw.Stop();
            var running = Process.GetProcessesByName("whisper-server");
            var procInfo = running.Length > 0 ? $"PID {running[0].Id}" : "внешний процесс";
            var mode = SttOnGpu ? "GPU (large-v3-turbo)" : "CPU";
            return (true, sw.ElapsedMilliseconds, $"{procInfo}, {mode}");
        }
        catch (Exception ex)
        {
            return (false, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    private Task<bool> StartSttAsync(string model, bool gpu, TimeSpan wait)
    {
        var u = new Uri(_cfg.SttUrl);
        // Запуск whisper-server: чистый режим без урезания контекста (-ac 768) и без конфликтующего beam search (-bo 1 -bs 5),
        // которые вызывали деградацию декодера в "... ... ..." на коротких фразах.
        var args = new List<string> { "-m", model, "-l", "ru", "-t", _cfg.SttThreads.ToString(),
            "--host", u.Host, "--port", u.Port.ToString() };
        if (!gpu) args.Add("-ng");
        // на видеокарте — одна попытка: если не влезла, повтор ничего не даст, сразу к запасному
        return StartWithRetryAsync(gpu ? "слух (видеокарта)" : "слух", () => Start(_cfg.Abs(_cfg.SttExe), Path.GetDirectoryName(_cfg.Abs(_cfg.SttExe))!, [.. args]),
            () => Task.FromResult(PortOpen(_cfg.SttUrl)), wait, gpu ? 1 : Attempts);
    }

    /// <summary>Процесс, умерший при запуске (нехватка памяти в момент старта), перезапускается ещё пару раз.</summary>
    private static async Task<bool> StartWithRetryAsync(string what, Func<Process> start, Func<Task<bool>> alive, TimeSpan wait, int attempts = Attempts)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var p = start();
            if (await WaitAsync(alive, wait, p)) return true;
            if (!p.HasExited) { Log.Write($"{what}: не ответил за {wait.TotalSeconds:F0} с"); try { p.Kill(); } catch (InvalidOperationException) { } return false; }
            Log.Write($"{what}: процесс завершился при запуске (код {p.ExitCode}), попытка {attempt} из {attempts}");
            if (attempt < attempts) await Task.Delay(5000);
        }
        return false;
    }

    private static Process? StartPowerShell(string script, params string[] args)
    {
        foreach (var shell in new[] { "pwsh", "powershell" })
        {
            var psi = new ProcessStartInfo(shell) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Minimized };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(args)) psi.ArgumentList.Add(a);
            try { return Process.Start(psi); }
            catch (Win32Exception) { }   // этой оболочки нет — пробуем следующую
        }
        return null;
    }

    private Process Start(string exe, string workDir, params string[] args) => Start(exe, workDir, null, args);

    private Process Start(string exe, string workDir, IReadOnlyDictionary<string, string>? env, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Python пишет UTF-8 (PYTHONIOENCODING); без этого .NET читал вывод как OEM и в логе были кракозябры
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        if (env is not null) foreach (var (k, v) in env) psi.Environment[k] = v;
        var p = Process.Start(psi) ?? throw new InvalidOperationException($"не запустился {exe}");
        _children.Add(p);
        try { Native.TieToLifetime(p); }
        catch (InvalidOperationException ex) { Log.Error($"{Path.GetFileName(exe)} не привязан к жизни Клавы (переживёт её выход)", ex); }
        Log.Write($"запущен {Path.GetFileName(exe)} {string.Join(' ', args)} (PID {p.Id})");
        var logFile = _cfg.Abs($@"data\logs\{Path.GetFileNameWithoutExtension(exe)}-{Path.GetFileNameWithoutExtension(args.FirstOrDefault(a => a.EndsWith(".py")) ?? "")}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        var sink = OpenServiceLog(logFile, p.Id, args);
        var gate = new object();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (gate) sink.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (gate) sink.WriteLine(e.Data); };
        p.Exited += (_, _) => { lock (gate) sink.Dispose(); };
        p.EnableRaisingEvents = true;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    /// <summary>
    /// Лог сервиса — дописывается (с отметкой запуска), а не затирается: 10.10 первый запуск голоса упал, повтор
    /// перезаписал лог, и почему упал — уже не узнать. Общий доступ на запись: файл, который держит ещё живой процесс,
    /// больше не роняет запуск (IOException «used by another process»). Больше 5 МБ — в .prev.log. Совсем занят —
    /// отдельный файл с номером процесса.
    /// </summary>
    private static StreamWriter OpenServiceLog(string logFile, int pid, string[] args)
    {
        try
        {
            if (File.Exists(logFile) && new FileInfo(logFile).Length > 5 * 1024 * 1024)
                File.Move(logFile, Path.ChangeExtension(logFile, ".prev.log"), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        FileStream fs;
        try { fs = new FileStream(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            fs = new FileStream(Path.ChangeExtension(logFile, $".{pid}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        }
        var sink = new StreamWriter(fs, new System.Text.UTF8Encoding(false)) { AutoFlush = true };
        sink.WriteLine($"===== {DateTime.Now:dd.MM.yyyy HH:mm:ss} запуск PID {pid}: {string.Join(' ', args)}");
        return sink;
    }

    /// <summary>Ждать готовности; если процесс умер — сразу false, не дожидаясь таймаута.</summary>
    private static async Task<bool> WaitAsync(Func<Task<bool>> alive, TimeSpan wait, Process? proc)
    {
        var until = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < until)
        {
            if (await alive()) return true;
            if (proc is { HasExited: true }) return false;
            await Task.Delay(1000);
        }
        return false;
    }

    private async Task<bool> HttpOkAsync(string url)
    {
        try { using var r = await _http.GetAsync(url); return r.IsSuccessStatusCode; }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    private static bool PortOpen(string url)
    {
        var u = new Uri(url);
        return NetProxy.PortOpen(u.Host, u.Port, 300);
    }
}
