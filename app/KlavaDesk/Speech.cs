using System.Buffers.Binary;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NAudio.Wave;

namespace KlavaDesk;

/// <summary>Кусок распознанной речи: где он в записи (секунды) и что сказано.</summary>
internal sealed record SttSegment(double Start, double End, string Text);

/// <summary>Распознанная фраза целиком и по кускам.</summary>
internal sealed record SttResult(string Text, List<SttSegment> Segments);

/// <summary>
/// Голос: текст → WAV 24 кГц. Большой комплект — сервер синтеза (tts/server.py, Qwen3-TTS), лёгкий — Supertonic в процессе.
/// Обрыв связи — та же фраза большому серверу ещё раз; лёгким голосом — только если сервер лежит и liteFallback включён
/// (у кого есть большой голос, тот обычно его выключает: два тембра вперемешку режут слух).
/// </summary>
internal sealed class Voice
{
    private readonly KlavaConfig _cfg;
    // свой сервер на 127.0.0.1 — строго напрямую, мимо системного прокси
    private readonly HttpClient _http = NetProxy.Client(null, TimeSpan.FromSeconds(120));

    public Voice(KlavaConfig cfg) => _cfg = cfg;

    /// <summary>Последняя фраза озвучена большим сервером (false — лёгким: так OverlayForm понимает, что сервер надо чинить).</summary>
    public bool LastFromServer { get; private set; }

    /// <summary>Короткие фразы («Хм...», «Ага.») — готовым звуком: живая пауза и междометия звучат сразу.</summary>
    private readonly Dictionary<(string, string, bool), byte[]> _short = [];

    /// <param name="emotion">Эмоция голоса (tts/emotions.json); лёгкий голос её не различает.</param>
    public async Task<byte[]> SynthAsync(string text, CancellationToken ct, string emotion = "calm")
    {
        var key = (text, emotion, _cfg.LiteSpeech);
        lock (_short)
            if (text.Length <= 24 && _short.TryGetValue(key, out var cached)) return cached;
        var wav = await SynthCoreAsync(text, ct, emotion);
        if (text.Length <= 24 && wav.Length > 0)
            lock (_short)
            {
                if (_short.Count >= 64) _short.Clear();
                _short[key] = wav;
            }
        return wav;
    }

    private async Task<byte[]> SynthCoreAsync(string text, CancellationToken ct, string emotion)
    {
        if (_cfg.LiteSpeech)
        {
            LastFromServer = false;
            return await LiteSynthAsync(text, ct);
        }
        // Питоновский сервер изредка рвёт свежее соединение, не ответив («response ended prematurely», ~1 из 40
        // запросов, воспроизведено стендом): это сбой доставки, а не падение сервера — та же фраза тем же голосом
        // уходит ещё раз. Лёгкий голос — только когда сервер правда лежит, и только если он включён в настройках.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var body = new JsonObject { ["text"] = text, ["emotion"] = emotion };
                using var resp = await _http.PostAsync($"{_cfg.TtsUrl.TrimEnd('/')}/tts",
                    new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
                resp.EnsureSuccessStatusCode();
                var wav = await resp.Content.ReadAsByteArrayAsync(ct);
                LastFromServer = true;
                return wav;
            }
            catch (HttpRequestException ex) when (attempt < 3 && !ct.IsCancellationRequested && ex.StatusCode is null)
            {
                Log.Write($"голос: обрыв связи с сервером ({Why(ex)}) — повторяю фразу, попытка {attempt + 1}");
                await Task.Delay(150 * attempt, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested
                                       && _cfg.LiteFallback && Lite.Models(_cfg).CanSpeak)
            {
                Log.Write($"голос: сервер не ответил ({Why(ex)}) — фраза лёгким голосом");
                LastFromServer = false;
                return await LiteSynthAsync(text, ct);
            }
        }
    }

    /// <summary>
    /// Фраза потоком: onChunk получает сырой PCM 16 бит моно 24 кГц кусками по мере синтеза (первый — через ~0,5 с).
    /// Сервер не ответил потоком или поток пустой — та же фраза целиком через /tts (или лёгким голосом) одним куском.
    /// Возвращает, сколько байт звука отдано.
    /// </summary>
    public async Task<int> SynthStreamAsync(string text, string emotion, Action<byte[]> onChunk, CancellationToken ct)
    {
        var total = 0;
        if (!_cfg.LiteSpeech)
        {
            try
            {
                var body = new JsonObject { ["text"] = text, ["emotion"] = emotion };
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{_cfg.TtsUrl.TrimEnd('/')}/tts_stream")
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                var buf = new byte[48000];               // до 1 с звука за чтение
                var carry = -1;                          // нечётный байт с прошлого чтения (отсчёт — 2 байта)
                int n;
                while ((n = await stream.ReadAsync(buf, ct)) > 0)
                {
                    var have = n + (carry >= 0 ? 1 : 0);
                    var piece = new byte[have & ~1];
                    if (carry >= 0) { piece[0] = (byte)carry; Buffer.BlockCopy(buf, 0, piece, 1, piece.Length - 1); }
                    else Buffer.BlockCopy(buf, 0, piece, 0, piece.Length);
                    carry = (have & 1) == 1 ? buf[n - 1] : -1;
                    if (piece.Length == 0) continue;
                    total += piece.Length;
                    onChunk(piece);
                }
                LastFromServer = true;
                if (total > 0) return total;
                Log.Write("голос: поток пустой — фраза целиком");
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException && !ct.IsCancellationRequested)
            {
                if (total > 0) { Log.Write($"голос: поток оборвался ({Why(ex)}) после {total / 48000.0:F1} с"); return total; }
                Log.Write($"голос: поток не пошёл ({Why(ex)}) — фраза целиком");
            }
        }
        var wav = await SynthAsync(text, ct, emotion);
        var pcm = PcmOf(wav);
        if (pcm.Length > 0) onChunk(pcm);
        return pcm.Length;
    }

    /// <summary>Сырой PCM из WAV (кусок data); не WAV — пусто.</summary>
    internal static byte[] PcmOf(byte[] wav)
    {
        if (wav.Length < 44 || wav[0] != 'R' || wav[1] != 'I' || wav[8] != 'W') return [];
        var pos = 12;
        while (pos + 8 <= wav.Length)
        {
            var id = Encoding.ASCII.GetString(wav, pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(pos + 4));
            if (id == "data")
            {
                var len = Math.Min(size, wav.Length - pos - 8) & ~1;
                return wav.AsSpan(pos + 8, len).ToArray();
            }
            pos += 8 + size + (size & 1);
        }
        return [];
    }

    /// <summary>Причина сбоя целиком: внешнее «An error occurred while sending the request» ничего не говорит.</summary>
    private static string Why(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null && parts.Count < 3; e = e.InnerException) parts.Add(e.Message);
        return string.Join(" ← ", parts);
    }

    private Task<byte[]> LiteSynthAsync(string text, CancellationToken ct) => Task.Run(() =>
        Lite.Voice(_cfg).Synth(text, _cfg.LiteVoiceId, _cfg.LiteVoiceSpeed, _cfg.LiteVoiceSteps, _cfg.LiteVoicePitch), ct);
}

/// <summary>
/// Слух. Микрофон открыт постоянно (16 кГц моно через системный преобразователь WAVE_MAPPER,
/// чтобы подошёл любой микрофон). Речь нарезается двумя способами:
///  - «рация»: пока зажата клавиша — запись, отпустил — фраза готова;
///  - «без кнопки»: детектор голоса по энергии с подстройкой под фоновый шум.
/// Пока Клава говорит сама, детектор глухой: иначе она услышит себя из колонок.
/// </summary>
internal sealed partial class Ears : IDisposable
{
    public const int Rate = 16000;
    private const int FrameMs = 30;
    private const int FrameBytes = Rate * 2 * FrameMs / 1000;

    private readonly KlavaConfig _cfg;
    private readonly HttpClient _http = NetProxy.Client(null, TimeSpan.FromSeconds(60));
    private readonly object _lock = new();
    private WaveIn? _in;
    // сторож: когда драйвер последний раз отдал данные и когда в них последний раз был живой звук (не мёртвый ноль)
    private DateTime _lastDataUtc = DateTime.UtcNow;
    private DateTime _lastSoundUtc = DateTime.UtcNow;

    // общий поток кадров
    private readonly MemoryStream _pending = new();
    private readonly Queue<byte[]> _preRoll = new();   // последние ~300 мс до начала речи
    // рация
    private bool _ptt;
    private MemoryStream? _pttBuf;
    // детектор голоса
    private bool _vadActive;
    private MemoryStream? _vadBuf;
    private int _voicedMs, _silenceMs, _utterMs;
    private double _noise = 0.004;
    /// <summary>Текущее усиление микрофона (≤ MicGain): громкая речь его снижает, чтобы не было клипа (см. OnData).</summary>
    private double _autoGain;
    // Костыль от «двоения»: после конца фразы 120 мс детектор глухой — эхо колонок затухает,
    // но начало следующего слова не проглатывается.
    private DateTime _lastUtterEnded = DateTime.MinValue;
    private const int PostUtterCoolMs = 120;

    /// <summary>Детектор голоса нашёл законченную фразу (WAV). Зовётся из потока записи.</summary>
    public event Action<byte[], double>? Utterance;
    /// <summary>Детектор услышал начало речи.</summary>
    public event Action? SpeechStarted;
    /// <summary>Событие уровня громкости для VU-метра: RMS (0..1), порог VAD (0..1), голос активен.</summary>
    public event Action<double, double, bool>? LevelTick;
    /// <summary>Событие распознавания фразы: текст, длительность в сек, ошибка (или null).</summary>
    public event Action<string, double, string?>? TranscribeResult;

    /// <summary>Слушать без кнопки (включается в меню).</summary>
    public bool HandsFree { get; set; }
    /// <summary>Клава сейчас говорит или думает — детектор глухой.</summary>
    public bool Deaf { get; set; }
    /// <summary>Клава сейчас говорит: порог перебивания выше, чтобы её же голос из колонок не активировал детектор.</summary>
    public bool SpeakingBack { get; set; }
    /// <summary>
    /// Она говорит при «не слышать себя из колонок»: фраза в разговор не идёт (это почти всегда её же эхо), а уходит
    /// только на проверку команды «кыс-кыс» (CommandUtterance). Раньше такой режим глушил микрофон целиком, и перебить
    /// её голосом было нельзя никак (друг 09.10: «пиздит без остановок и не перебить»).
    /// </summary>
    public bool CommandOnly { get; set; }
    /// <summary>Кусок речи, пойманный в режиме CommandOnly: только на проверку команды, не в разговор.</summary>
    public event Action<byte[], double>? CommandUtterance;
    private bool _vadCommandOnly;
    // её голос из колонок держит детектор открытым сколько угодно: каждую секунду на проверку уходят последние 2,5 с —
    // «кыс-кыс, Клава» (~1,5 с) целиком попадает хотя бы в одно окно. Было кусками по 5 с: 09.10 «кис-кис» лёг на стык
    // («Кошко и Клава»), а следующий кусок проверился, когда она уже договорила
    private const int CommandStepMs = 1000;
    private const int CommandWindowMs = 2500;
    private int _sinceCommandMs;
    public bool PttRecording => _ptt;
    private double _peak;
    public string? LastError { get; private set; }
    public bool IsOpen => _in is not null;
    /// <summary>Фоновая доучивалка: adaptive prompt + correction dictionary. Может быть null (тогда работаем как раньше).</summary>
    public WhisperLearn? Learn { get; set; }

    /// <summary>Пиковая громкость (RMS) с прошлого вызова — для лога: ноль значит, что звук не доходит.</summary>
    public double TakePeak()
    {
        lock (_lock) { var p = _peak; _peak = 0; return p; }
    }

    /// <summary>
    /// Сколько драйвер молчит совсем и сколько в данных не было живого звука (для сторожа микрофона). Переподключение
    /// «тишину» не сбрасывает: иначе сторож сам себе писал «снова слышу» и крутил первую попытку по кругу.
    /// </summary>
    public (TimeSpan NoData, TimeSpan Silent) Health()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            return (now - _lastDataUtc, now - _lastSoundUtc);
        }
    }

    // Что whisper выдумывает на тишине и шуме — такое не отправляем Клаве.
    // Плюс: одиночные фантомные выдохи («Спасибо.», «Пожалуйста.»), галлюцинации-повторы («улулулулул», «Я, я, я, я»).
    [GeneratedRegex(@"^\s*[\[\(].*[\]\)]\s*$|продолжение следует|субтитры|редактор субтитров|спасибо за просмотр|подпишитесь|^\s*(спасибо|спасибо большое|пожалуйста|благодарю|не за что)[\.\!\?]?\s*$|^[\s\.\,\-]+$", RegexOptions.IgnoreCase)]
    private static partial Regex JunkRx();

    /// <summary>
    /// Мусор whisper: шаблоны из JunkRx и подписи капсом. На музыке и звуках игры whisper пишет субтитры для глухих —
    /// «ТРЕВОЖНАЯ МУЗЫКА», «СМЕХ», «АПЛОДИСМЕНТЫ»: 02.10 в Бакшоте такая подпись ушла мозгу репликой отсутствующего
    /// Лиса, и Клава посреди катки заиграла Паганини. Живую речь whisper капсом не пишет: 5+ кириллических букв и ни
    /// одной строчной — подпись.
    /// </summary>
    internal static bool IsJunk(string text) => JunkRx().IsMatch(text) || IsCaptionCaps(text);

    internal static bool IsCaptionCaps(string text) =>
        text.Count(c => c is >= 'А' and <= 'Я' or 'Ё') >= 5 && !text.Any(char.IsLower);

    // Слог/фрагмент 2-6 букв повторён 3+ раза подряд («улулулул», «пух-пух-пух», «нанананана»).
    [GeneratedRegex(@"([а-яёa-z]{2,6})\1{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex SyllableRepeatRx();

    [GeneratedRegex(@"[^.!?…\n]+[.!?…]*")]
    private static partial Regex SentenceRx();

    /// <summary>
    /// Зацикливание whisper: «…и почему ты прерываешься. Спасибо. Спасибо. Спасибо.», «Ты это, кота сниз… ×16».
    /// Одинаковые предложения подряд — одно; предложение, повторённое 3+ раз по фразе, — только первое. Живая речь
    /// так не повторяется, а если повторилась — смысл от одного раза не теряется.
    /// </summary>
    internal static string CollapseRepeats(string text)
    {
        var sentences = SentenceRx().Matches(text).Select(m => m.Value.Trim()).Where(s => s.Length > 0).ToList();
        if (sentences.Count < 2) return text;
        static string Norm(string s) => new string(s.ToLowerInvariant().Replace('ё', 'е').Where(char.IsLetterOrDigit).ToArray());
        var counts = sentences.GroupBy(Norm).ToDictionary(g => g.Key, g => g.Count());
        var kept = new List<string>();
        var seen = new HashSet<string>();
        string? prev = null;
        foreach (var s in sentences)
        {
            var n = Norm(s);
            if (n.Length == 0 || n == prev || (counts[n] >= 3 && seen.Contains(n))) continue;
            kept.Add(s);
            seen.Add(n);
            prev = n;
        }
        return kept.Count == sentences.Count ? text : string.Join(" ", kept);
    }

    /// <summary>Whisper при тишине/шуме генерит повторы. Отсеиваем «Я, я, я, я», «Пишин Пишин Пишин», «улулулул».</summary>
    private static bool IsRepeatHallucination(string text)
    {
        // Слог повторяется подряд («улулул», «пупупупу»)
        if (SyllableRepeatRx().IsMatch(text)) return true;
        // Разбиваем на слова, смотрим уникальность
        var words = text.Split(new[] { ' ', ',', '.', '-', '!', '?', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3) return false;
        var unique = new HashSet<string>(words.Select(w => w.ToLowerInvariant()));
        // 3+ слов, но по факту 1 уникальное («я я я я»); 6+ слов и всего 2 уникальных («пишин пишин пишин пишин»)
        if (unique.Count == 1) return true;
        if (words.Length >= 6 && unique.Count <= 2) return true;
        return false;
    }

    public Ears(KlavaConfig cfg) => _cfg = cfg;

    public static List<(int DeviceIndex, string Name)> GetDevices()
    {
        var list = new List<(int, string)> { (-1, "По умолчанию в Windows") };
        for (var i = 0; i < WaveIn.DeviceCount; i++)
        {
            try
            {
                var caps = WaveIn.GetCapabilities(i);
                list.Add((i, caps.ProductName));
            }
            catch
            {
                list.Add((i, $"Аудиоустройство #{i}"));
            }
        }
        return list;
    }

    public void RestartMicrophone()
    {
        WaveIn? old;
        lock (_lock)
        {
            old = _in;
            _in = null;
            ResetVad();
            _noise = 0.004;
            _lastDataUtc = DateTime.UtcNow;
        }
        // StopRecording ждёт поток записи, а тот берёт _lock в OnData — останавливаем вне замка, иначе взаимная блокировка
        try { old?.StopRecording(); } catch (Exception ex) when (ex is InvalidOperationException or NAudio.MmException) { }
        old?.Dispose();
        lock (_lock) Open();
    }

    public void SwitchDevice(int deviceIndex)
    {
        _cfg.MicDevice = deviceIndex;
        _cfg.MicDeviceName = deviceIndex < 0 ? null : DeviceName(deviceIndex);
        _cfg.Save();
        RestartMicrophone();
    }

    private static string? DeviceName(int index)
    {
        try { return index >= 0 && index < WaveIn.DeviceCount ? WaveIn.GetCapabilities(index).ProductName : null; }
        catch (NAudio.MmException) { return null; }
    }

    /// <summary>
    /// Номер микрофона: по имени, если оно запомнено (номера WinMM сдвигаются, когда USB-устройство переподключили
    /// или воткнули гарнитуру), иначе номер из настроек; нет такого — системный по умолчанию.
    /// </summary>
    private int ResolveDevice() => ResolveDevice(_cfg);

    /// <summary>Тот же выбор микрофона для окон записи (профили, тренажёр чтения): пишут с того же, что слушает Клава.</summary>
    public static int ResolveDevice(KlavaConfig cfg)
    {
        if (!string.IsNullOrWhiteSpace(cfg.MicDeviceName))
        {
            for (var i = 0; i < WaveIn.DeviceCount; i++)
                if (string.Equals(DeviceName(i), cfg.MicDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    if (i != cfg.MicDevice) Log.Write($"микрофон «{cfg.MicDeviceName}» переехал: #{cfg.MicDevice} → #{i}");
                    return i;
                }
            for (var i = 0; i < WaveIn.DeviceCount; i++)
                if (DeviceName(i) is { } name && SameMic(name, cfg.MicDeviceName))
                {
                    Log.Write($"микрофон «{cfg.MicDeviceName}» теперь «{name}» (#{i}) — переткнули в другой порт, беру его");
                    return i;
                }
            Log.Write($"микрофона «{cfg.MicDeviceName}» нет — беру системный по умолчанию");
            return -1;
        }
        return cfg.MicDevice < WaveIn.DeviceCount ? cfg.MicDevice : -1;
    }

    // Тот же USB-микрофон в другом порту Windows называет с номером: «Микрофон (Usb Audio Device)» → «Микрофон
    // (4- Usb Audio Device)». 10.10: друг переткнул микрофон — Клава искала старое имя, взяла системный по умолчанию
    // (там тишина, пик 0,0000) и «одна в хате болтала»
    [System.Text.RegularExpressions.GeneratedRegex(@"\(\d+-\s*")]
    private static partial System.Text.RegularExpressions.Regex PortPrefixRx();

    /// <summary>Одно устройство с точностью до номера порта, который Windows дописывает в скобках («(3- …», «(4- …»).</summary>
    internal static bool SameMic(string a, string b) =>
        string.Equals(PortPrefixRx().Replace(a, "("), PortPrefixRx().Replace(b, "("), StringComparison.OrdinalIgnoreCase);

    public void Open()
    {
        if (_in is not null) return;
        try
        {
            var device = ResolveDevice();
            // 6 буферов по 60 мс (360 мс запаса): при 4×30 мс, когда видеокарту и процессор грузят whisper и голос,
            // WinMM не успевал отдавать буферы и терял куски речи. Кадры детектора всё равно по 30 мс (OnData режет).
            _in = new WaveIn
            {
                DeviceNumber = device,
                WaveFormat = new WaveFormat(Rate, 16, 1),
                BufferMilliseconds = FrameMs * 2,
                NumberOfBuffers = 6,
            };
            _in.DataAvailable += (_, e) => OnData(e.Buffer, e.BytesRecorded);
            _in.RecordingStopped += (_, e) =>
            {
                if (e.Exception is not null)
                {
                    LastError = e.Exception.Message;
                    Log.Error("микрофон остановился", e.Exception);
                }
            };
            _in.StartRecording();
            LastError = null;
            _lastDataUtc = DateTime.UtcNow;
            var devName = device < 0 ? "системное по умолчанию" : DeviceName(device) ?? "?";
            if (device >= 0 && string.IsNullOrWhiteSpace(_cfg.MicDeviceName)) { _cfg.MicDeviceName = devName; _cfg.Save(); }
            Log.Write($"микрофон открыт: устройство {device} ({devName}), 16 кГц моно");
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error("не удалось открыть микрофон", ex);
            throw;
        }
    }

    public void StartPtt()
    {
        lock (_lock) { _pttBuf = new MemoryStream(); _ptt = true; ResetVad(); }
    }

    /// <summary>Отпустили клавишу: WAV и длительность в секундах.</summary>
    public (byte[] Wav, double Seconds) StopPtt()
    {
        byte[] pcm;
        lock (_lock)
        {
            _ptt = false;
            pcm = _pttBuf?.ToArray() ?? [];
            _pttBuf = null;
        }
        return (ToWav(pcm), pcm.Length / 2.0 / Rate);
    }

    private void OnData(byte[] buffer, int count)
    {
        // Усиление тихого микрофона с автоподстройкой. MicGain — потолок. Раньше ×2,5 шло всегда и громкая речь упиралась
        // в край int16 (пики RMS 0,5 в журнале): хрип, whisper писал чушь, отпечаток голоса плыл. Теперь громкий кусок
        // сразу снижает усиление так, чтобы пик остался ниже 0,9, в тихом — усиление возвращается плавно (~×1,4 в секунду);
        // поверх — мягкий ограничитель вместо обрезки.
        var maxGain = _cfg.MicGain;
        if (maxGain > 1.0001 || maxGain < 0.9999)
        {
            var peak = 0;
            for (var i = 0; i + 1 < count; i += 2) peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i))));
            var peakF = peak / 32768.0;
            if (_autoGain <= 0 || _autoGain > maxGain) _autoGain = maxGain;
            if (maxGain > 1 && peakF * _autoGain > 0.9) _autoGain = Math.Max(1.0, 0.9 / Math.Max(peakF, 1e-6));
            else if (peakF * _autoGain < 0.45) _autoGain = Math.Min(maxGain, _autoGain * 1.02);
            var gain = _autoGain;
            for (var i = 0; i + 1 < count; i += 2)
            {
                var s = BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(i)) * gain / 32768.0;
                // мягкое колено: до 0,7 — как есть, выше — плавно к 1,0 (без ступеньки обрезки)
                var a = Math.Abs(s);
                if (a > 0.7) s = Math.Sign(s) * (0.7 + 0.3 * Math.Tanh((a - 0.7) / 0.3));
                BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i), (short)Math.Clamp(s * 32767.0, short.MinValue, short.MaxValue));
            }
        }
        lock (_lock)
        {
            _lastDataUtc = DateTime.UtcNow;
            _pending.Write(buffer, 0, count);
            var data = _pending.GetBuffer();
            var len = (int)_pending.Length;
            var off = 0;
            while (len - off >= FrameBytes)
            {
                var frame = data.AsSpan(off, FrameBytes).ToArray();
                off += FrameBytes;
                ProcessFrame(frame);
            }
            var rest = data.AsSpan(off, len - off).ToArray();
            _pending.SetLength(0);
            _pending.Write(rest);
        }
    }

    private void ProcessFrame(byte[] frame)
    {
        double sum = 0;
        for (var i = 0; i < frame.Length; i += 2)
        {
            var s = BinaryPrimitives.ReadInt16LittleEndian(frame.AsSpan(i)) / 32768.0;
            sum += s * s;
        }
        var rms = Math.Sqrt(sum / (frame.Length / 2));
        if (rms > _peak) _peak = rms;
        // живая комната никогда не даёт почти ноль (шум USB-микрофона ~0,001): тише трёх младших бит — мёртвый поток
        // или кнопка mute на самом микрофоне (так и было 25.09: ffmpeg с того же устройства тоже писал −91 дБ)
        if (rms >= 1e-4) _lastSoundUtc = DateTime.UtcNow;

        // рация: не длиннее 15 с — дальше whisper с аудиоконтекстом 768 всё равно не услышит
        if (_ptt) { if (_pttBuf!.Length < Rate * 2 * 15) _pttBuf.Write(frame); return; }
        if (!HandsFree || Deaf) { LevelTick?.Invoke(rms, _cfg.VadMinLevel, false); ResetVad(); return; }
        // замолчала посреди куска «только для команды» — он уходит на проверку, а речь друга после неё ловится
        // новой обычной фразой (как раньше, когда уши открывались после её речи)
        if (_vadActive && _vadCommandOnly && !CommandOnly) EndCommandUtterance();
        // Костыль от двоения: пока идёт 350-мс «отходняк» после только что законченной фразы —
        // просто копим фон, но не начинаем новую. Эхо своей же речи из колонок в этот зазор укладывается.
        if (_lastUtterEnded != DateTime.MinValue && (DateTime.UtcNow - _lastUtterEnded).TotalMilliseconds < PostUtterCoolMs)
        {
            _noise = _noise * 0.9 + rms * 0.1;
            LevelTick?.Invoke(rms, _cfg.VadMinLevel, false);
            return;
        }

        // Пока Клава говорит, микрофон слышит собственный голос из колонок. Чтобы её же «мур» не запустил
        // детектор, порог поднят: перебить может только реально громкая речь друга (обычно в 2–3 раза
        // выше собственного эха). Кто в наушниках — можно снять флаг Speaking совсем.
        var speaking = SpeakingBack;
        // Множитель шума понизили с 3.0 до 2.5 — тихая речь издалека теперь тоже вылезает над фоном.
        var base_ = Math.Max(_cfg.VadMinLevel, _noise * 2.5);
        // Пока Клава говорит, порог поднимаем в 2.5 раза от базы. Слишком низкий множитель — её эхо из
        // колонок само триггерит SpeechStarted и она себя прерывает («не договорила»). Слишком высокий —
        // друга не слышно. Компромисс 2.5x + VadInterruptLevel как абсолютный минимум.
        var threshold = speaking ? Math.Max(base_ * 2.5, _cfg.VadInterruptLevel) : base_;
        var voiced = rms > threshold;
        LevelTick?.Invoke(rms, threshold, _vadActive);
        var startFrames = Math.Max(1, (speaking ? _cfg.VadInterruptStartMs : _cfg.VadStartMs) / FrameMs);
        if (!_vadActive)
        {
            if (!voiced) _noise = _noise * 0.97 + rms * 0.03;     // фон подстраивается только в тишине
            _preRoll.Enqueue(frame);
            while (_preRoll.Count > 450 / FrameMs) _preRoll.Dequeue();
            _voicedMs = voiced ? _voicedMs + FrameMs : 0;
            if (_voicedMs >= startFrames * FrameMs)
            {
                _vadActive = true;
                _vadBuf = new MemoryStream();
                foreach (var f in _preRoll) _vadBuf.Write(f);
                _preRoll.Clear();
                _silenceMs = 0;
                _utterMs = _voicedMs;
                _vadCommandOnly = CommandOnly;
                // кусок «только для команды» — почти всегда её эхо: голову «слушаю» не наклоняет
                if (!_vadCommandOnly) SpeechStarted?.Invoke();
            }
            return;
        }

        _vadBuf!.Write(frame);
        _utterMs += FrameMs;
        _silenceMs = voiced ? 0 : _silenceMs + FrameMs;
        if (_vadCommandOnly)
        {
            if (_silenceMs >= _cfg.VadSilenceMs) { EndCommandUtterance(); return; }
            _sinceCommandMs += FrameMs;
            if (_sinceCommandMs < CommandStepMs) return;
            _sinceCommandMs = 0;
            var all = _vadBuf.ToArray();
            var keep = Math.Min(all.Length, Rate * 2 * CommandWindowMs / 1000);
            _vadBuf = new MemoryStream();
            _vadBuf.Write(all, all.Length - keep, keep);
            var window = _vadBuf.ToArray();
            CommandUtterance?.Invoke(ToWav(window), window.Length / 2.0 / Rate);
            return;
        }
        if (_silenceMs >= _cfg.VadSilenceMs || _utterMs >= 15000)
        {
            var pcm = _vadBuf.ToArray();
            var seconds = pcm.Length / 2.0 / Rate;
            ResetVad();
            // Выкидываем всё, что нападало в общий буфер за время обработки — иначе следующий кадр даст
            // «фантомный» кусок только что законченной фразы (это была одна из причин заикания).
            _pending.SetLength(0);
            _lastUtterEnded = DateTime.UtcNow;
            if (seconds >= 0.45) Utterance?.Invoke(ToWav(pcm), seconds);
        }
    }

    /// <summary>Кусок «только для команды» закончился (тишина или она замолчала): на проверку «кыс-кыс».</summary>
    private void EndCommandUtterance()
    {
        var pcm = _vadBuf?.ToArray() ?? [];
        var seconds = pcm.Length / 2.0 / Rate;
        ResetVad();
        _lastUtterEnded = DateTime.UtcNow;
        if (seconds >= 0.45) CommandUtterance?.Invoke(ToWav(pcm), seconds);
    }

    private void ResetVad()
    {
        _vadCommandOnly = false;
        _sinceCommandMs = 0;
        _vadActive = false;
        _vadBuf = null;
        _voicedMs = _silenceMs = _utterMs = 0;
        _preRoll.Clear();
    }

    private static byte[] ToWav(byte[] pcm)
    {
        var wav = new byte[44 + pcm.Length];
        var s = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + pcm.Length);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(s[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);          // размер fmt-чанка
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);           // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1);           // моно
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], Rate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], Rate * 2);    // байт в секунду
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 2);           // байт на отсчёт
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);          // бит на отсчёт
        Encoding.ASCII.GetBytes("data").CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], pcm.Length);
        pcm.CopyTo(s[44..]);
        return wav;
    }

    /// <summary>
    /// Распознать речь. Попытка 1: с подсказкой и сегментами (verbose_json) — время каждого куска нужно, чтобы
    /// вырезать её эхо из фразы, где друг заговорил сразу после неё. Сегменты, которые сам whisper считает тишиной
    /// (высокий no_speech_prob при низкой уверенности), выкидываются — это его выдумки на шуме и телевизоре.
    /// Попытка 2 (если пусто): без подсказки, температура 0,3.
    /// </summary>
    public async Task<SttResult> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        var sec = wav.Length / 2.0 / Rate;
        string clean = "";
        var segments = new List<SttSegment>();
        bool isGarbage = false;
        var lite = _cfg.LiteSpeech;
        // whisper-server не ответил вовсе (упал, порт закрыт) — повтор бессмыслен, фразу берёт лёгкий слух
        var serverDown = false;

        // Попытка 1: С адаптивным prompt и точным декодером
        if (!lite) try
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(wav);
            file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(file, "file", "speech.wav");
            form.Add(new StringContent("verbose_json"), "response_format");
            form.Add(new StringContent("0.0"), "temperature");
            form.Add(new StringContent("ru"), "language");
            // Prompt для whisper: базовая подсказка + последние фразы своих (доучивание в фоне).
            var prompt = _cfg.SttPrompt ?? "";
            if (Learn is { } l)
            {
                var suffix = l.PromptSuffix();
                if (suffix.Length > 0) prompt = string.IsNullOrWhiteSpace(prompt) ? suffix : prompt + " " + suffix;
            }
            if (prompt.Length > 400) prompt = prompt[^400..];
            if (!string.IsNullOrWhiteSpace(prompt)) form.Add(new StringContent(prompt), "prompt");

            using var resp = await _http.PostAsync($"{_cfg.SttUrl.TrimEnd('/')}/inference", form, ct);
            resp.EnsureSuccessStatusCode();
            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var dropped = 0;
            // куски whisper клеятся в одну строку: кусок начался с пробела — новое слово, без пробела — продолжение
            // слова. Раньше клеились через перевод строки, и в журнал шло «коф\nе пью», «хлещу\n.»
            var joined = new StringBuilder();
            if (root?["segments"] is JsonArray segs)
            {
                foreach (var s in segs)
                {
                    var raw = s?["text"]?.GetValue<string>() ?? "";
                    var t = raw.Trim();
                    if (t.Length == 0) continue;
                    var noSpeech = s?["no_speech_prob"]?.GetValue<double>() ?? 0;
                    var logprob = s?["avg_logprob"]?.GetValue<double>() ?? 0;
                    if (noSpeech > 0.6 && logprob < -0.8) { dropped++; continue; }
                    segments.Add(new SttSegment(s?["start"]?.GetValue<double>() ?? 0, s?["end"]?.GetValue<double>() ?? sec, t));
                    var newWord = raw.Length > 0 && char.IsWhiteSpace(raw[0]) || !char.IsLetterOrDigit(t[0]);
                    if (joined.Length > 0 && newWord && char.IsLetterOrDigit(t[0])) joined.Append(' ');
                    else if (joined.Length > 0 && newWord && t[0] is '«' or '(' or '—' or '-') joined.Append(' ');
                    joined.Append(t);
                }
            }
            var text = segments.Count > 0 || root?["segments"] is JsonArray
                ? joined.ToString()
                : root?["text"]?.GetValue<string>()?.Trim() ?? "";
            if (dropped > 0) Log.Write($"whisper: {dropped} сегм. похожи на тишину/выдумку — выкинула");
            if (segments.Count == 0 && text.Length > 0) segments.Add(new SttSegment(0, sec, text));
            var collapsed = CollapseRepeats(text);
            if (collapsed != text) { Log.Write($"whisper: зациклился — повторы убраны ({text.Length} → {collapsed.Length} символов)"); text = collapsed; }
            isGarbage = IsJunk(text) || (Learn?.IsBannedHallucination(text) == true);
            clean = text.Length < 2 || isGarbage || IsRepeatHallucination(text) ? "" : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            Log.Write($"whisper transcribe error (попытка 1): {ex.Message}");
            serverDown = ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);
        }

        // Попытка 2 (Fallback Retry): если фраза >= 0.6 с, а whisper выдал пустоту или галлюцинацию
        if (!lite && !serverDown && clean.Length == 0 && sec >= 0.6)
        {
            try
            {
                using var form2 = new MultipartFormDataContent();
                var file2 = new ByteArrayContent(wav);
                file2.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                form2.Add(file2, "file", "speech.wav");
                form2.Add(new StringContent("json"), "response_format");
                form2.Add(new StringContent("0.3"), "temperature"); // мягкая температура против зацикливания
                form2.Add(new StringContent("ru"), "language");
                // Без prompt и suffix — чистый прямой запуск модели
                using var resp2 = await _http.PostAsync($"{_cfg.SttUrl.TrimEnd('/')}/inference", form2, ct);
                if (resp2.IsSuccessStatusCode)
                {
                    var text2 = JsonNode.Parse(await resp2.Content.ReadAsStringAsync(ct))?["text"]?.GetValue<string>()?.Trim() ?? "";
                    var isGarbage2 = IsJunk(text2) || (Learn?.IsBannedHallucination(text2) == true);
                    // из шума повторный прогон любит «спасти» одиночное «а» / «м» — меньше двух букв это не фраза
                    if (text2.Count(char.IsLetter) >= 2 && !isGarbage2 && !IsRepeatHallucination(text2))
                    {
                        Log.Write($"whisper retry спас фразу ({sec:F1} с): «{text2}»");
                        clean = text2;
                        segments = [new SttSegment(0, sec, text2)];
                    }
                    else if (isGarbage2)
                    {
                        isGarbage = true;
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
            {
                Log.Write($"whisper fallback retry error: {ex.Message}");
            }
        }

        // Лёгкий слух: GigaAM на процессоре — основной в лёгком комплекте и подмена, когда whisper-server лежит
        if (lite || (serverDown && _cfg.LiteFallback && Lite.Models(_cfg).CanHear))
        {
            try
            {
                var segs = await Task.Run(() => Lite.Ears(_cfg).Transcribe(Lite.ReadWav16k(wav)), ct);
                var text = string.Join("\n", segs.Select(s => s.Text));
                if (!lite) Log.Write($"whisper не ответил — фраза распознана лёгким слухом: «{text}»");
                isGarbage = IsJunk(text) || (Learn?.IsBannedHallucination(text) == true);
                clean = text.Count(char.IsLetter) < 2 || isGarbage || IsRepeatHallucination(text) ? "" : text;
                segments = clean.Length > 0 ? segs : [];
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
            {
                Log.Error("лёгкий слух", ex);
            }
        }

        // Применить наученные исправления. В адаптивный контекст фразу кладёт OverlayForm — только речь своих:
        // раньше туда шёл и телевизор, и его каша подсказывала whisper'у новую кашу.
        if (Learn is not null && clean.Length > 0)
        {
            clean = Learn.Correct(clean);
            segments = segments.Select(s => s with { Text = Learn.Correct(s.Text) }).ToList();
        }
        if (clean.Length == 0) segments.Clear();

        // Если фраза длинная (>= 1.2 с), но распознать не удалось (и это НЕ забаненная галлюцинация / шум)
        if (clean.Length == 0 && sec >= 1.2 && !isGarbage)
        {
            clean = "[неразборчиво]";
        }

        TranscribeResult?.Invoke(clean, sec, null);
        return new SttResult(clean, segments);
    }

    /// <summary>Кусок WAV (16 кГц, 16 бит, моно) от from до to секунд.</summary>
    public static byte[] CutWav(byte[] wav, double from, double to)
    {
        var total = (wav.Length - 44) / 2;
        var a = Math.Clamp((int)(from * Rate), 0, total);
        var b = Math.Clamp((int)(to * Rate), a, total);
        var pcm = new byte[(b - a) * 2];
        Buffer.BlockCopy(wav, 44 + a * 2, pcm, 0, pcm.Length);
        return ToWav(pcm);
    }

    public void Dispose()
    {
        try { _in?.StopRecording(); } catch (InvalidOperationException) { }
        _in?.Dispose();
        _in = null;
    }
}
