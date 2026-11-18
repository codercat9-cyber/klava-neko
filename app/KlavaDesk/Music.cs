using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KlavaDesk;

/// <summary>
/// Музыкальный слух и пение Клавы — со стороны хозяина. Друг 30.09: «нейросети не слышат музыку — ни ритма, ни
/// инструментов; пусть она заценит Моцарта и Чайковского и научится петь каверы по-русски». Тяжёлое (стемы, доли,
/// ноты, аккорды, Seed-VC) делает music\klava_music.py на видеокарте; здесь — задачи ему с прогрессом, запись звука
/// компьютера (без её собственного голоса), «что играет» из плеера Windows и эквиритмический перевод её мозгом.
/// </summary>
internal sealed partial class MusicClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly KlavaConfig _cfg;
    private readonly Services _services;

    public MusicClient(KlavaConfig cfg, Services services)
    {
        _cfg = cfg;
        _services = services;
    }

    /// <summary>Шаг задачи: доля 0..1 и что сейчас делается.</summary>
    public readonly record struct Step(double Progress, string Message);

    /// <summary>Задача сервису (analyze | plan | check | sing) с ожиданием итога; отмена — отменяет и там.</summary>
    public async Task<JsonNode?> RunAsync(string kind, JsonObject args, IProgress<Step>? progress, CancellationToken ct)
    {
        if (!await _services.EnsureMusicAsync(new Progress<string>(s => progress?.Report(new Step(0, s)))))
            throw new InvalidOperationException("музыкальный слух не поднялся (music\\klava_music.py) — лог в data\\logs\\python-klava_music.log");
        args["kind"] = kind;
        var url = _cfg.MusicUrl.TrimEnd('/');
        using var resp = await Http.PostAsync($"{url}/job", new StringContent(args.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        var id = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["id"]?.GetValue<string>()
                 ?? throw new InvalidOperationException("музыкальный сервис не принял задачу");
        try
        {
            var last = "";
            while (true)
            {
                await Task.Delay(700, ct);
                JsonNode? j;
                try { j = JsonNode.Parse(await Http.GetStringAsync($"{url}/job/{id}", ct)); }
                catch (HttpRequestException)
                {
                    if (!await _services.MusicAliveAsync())
                        throw new InvalidOperationException("музыкальный сервис упал посреди задачи — лог в data\\logs\\python-klava_music.log");
                    continue;   // разовый сбой соединения — спросим ещё раз
                }
                var state = j?["state"]?.GetValue<string>() ?? "";
                var msg = j?["message"]?.GetValue<string>() ?? "";
                var p = j?["progress"]?.GetValue<double>() ?? 0;
                if (msg != last) { progress?.Report(new Step(p, msg)); last = msg; }
                switch (state)
                {
                    case "done": return j?["result"]?.DeepClone();
                    case "error": throw new InvalidOperationException(msg);
                    case "cancelled": throw new OperationCanceledException(msg);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            try { await Http.PostAsync($"{url}/cancel/{id}", new StringContent("{}"), CancellationToken.None); }
            catch (HttpRequestException) { }
            throw;
        }
    }

    // ---------- звук компьютера ----------

    /// <summary>
    /// Записать то, что играет на компьютере, в WAV. Своё дерево процессов (её голос в WebView2) исключается —
    /// Windows 10 2004+ умеет захват «всё, кроме этого процесса»; не вышло — обычный захват выхода (тогда пока она
    /// говорит, запись ждёт тишины от неё — это делает вызывающий).
    /// </summary>
    public static async Task<(string Path, double Seconds, double Peak)> RecordAsync(string file, int seconds, CancellationToken ct,
        Action<float[], int>? tap = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var fmt = new WaveFormat(44100, 16, 2);
        WaveFileWriter? writer = null;
        var peak = 0.0;
        long bytes = 0;
        var need = (long)fmt.AverageBytesPerSecond * seconds;
        try
        {
            await using var rec = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree)
                .WithFormat(fmt)
                .BuildAsync();
            writer = new WaveFileWriter(file, rec.WaveFormat);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // в полной тишине пакетов может не быть вовсе — не ждать вечно
            stop.CancelAfter(TimeSpan.FromSeconds(seconds + 5));
            try
            {
                await foreach (var buf in rec.CaptureAsync(stop.Token))
                {
                    var data = buf.Data.ToArray();
                    writer.Write(data, 0, data.Length);
                    peak = Math.Max(peak, Peak16(data));
                    bytes += data.Length;
                    if (tap is not null)
                    {
                        var mono = new float[data.Length / 4];
                        for (var i = 0; i < mono.Length; i++)
                            mono[i] = (BitConverter.ToInt16(data, i * 4) + BitConverter.ToInt16(data, i * 4 + 2)) / 65536f;
                        tap(mono, fmt.SampleRate);
                    }
                    if (bytes >= need) break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        {
            Log.Write($"музыка: захват без себя не вышел ({ex.Message}) — пишу весь выход звука");
            writer?.Dispose();
            writer = null;
            (bytes, peak) = await RecordLoopbackAsync(file, seconds, ct, tap);
            return (file, bytes / (double)fmt.AverageBytesPerSecond, peak);
        }
        finally { writer?.Dispose(); }
        return (file, bytes / (double)fmt.AverageBytesPerSecond, peak);
    }

    private static async Task<(long Bytes, double Peak)> RecordLoopbackAsync(string file, int seconds, CancellationToken ct, Action<float[], int>? tap)
    {
        using var cap = new WasapiLoopbackCapture();
        using var writer = new WaveFileWriter(file, cap.WaveFormat);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long bytes = 0;
        var peak = 0.0;
        var need = (long)cap.WaveFormat.AverageBytesPerSecond * seconds;
        var isFloat = cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat || cap.WaveFormat.BitsPerSample == 32;
        cap.DataAvailable += (_, e) =>
        {
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            bytes += e.BytesRecorded;
            if (isFloat)
            {
                for (var i = 0; i + 3 < e.BytesRecorded; i += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(e.Buffer, i)));
                if (tap is not null)
                {
                    var ch = cap.WaveFormat.Channels;
                    var mono = new float[e.BytesRecorded / 4 / ch];
                    for (var i = 0; i < mono.Length; i++)
                    {
                        var sum = 0f;
                        for (var c = 0; c < ch; c++) sum += BitConverter.ToSingle(e.Buffer, (i * ch + c) * 4);
                        mono[i] = sum / ch;
                    }
                    tap(mono, cap.WaveFormat.SampleRate);
                }
            }
            if (bytes >= need) cap.StopRecording();
        };
        cap.RecordingStopped += (_, _) => done.TrySetResult();
        cap.StartRecording();
        await using (ct.Register(() => cap.StopRecording()))
        {
            // тишина на выходе — WASAPI не шлёт пакеты: не ждём вечно
            await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(seconds + 5), CancellationToken.None));
        }
        if (!done.Task.IsCompleted) cap.StopRecording();
        return (bytes, peak);
    }

    private static double Peak16(byte[] data)
    {
        var p = 0;
        for (var i = 0; i + 1 < data.Length; i += 2) p = Math.Max(p, Math.Abs((int)BitConverter.ToInt16(data, i)));
        return p / 32768.0;
    }

    /// <summary>Что играет по словам плеера Windows (браузер, Spotify, медиаплеер): «Исполнитель — Название» или null.</summary>
    public static async Task<string?> NowPlayingAsync()
    {
        try
        {
            var mgr = await Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var s = mgr.GetCurrentSession();
            if (s is null) return null;
            var props = await s.TryGetMediaPropertiesAsync();
            var info = s.GetPlaybackInfo();
            var playing = info?.PlaybackStatus == Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var who = string.Join(" — ", new[] { props?.Artist, props?.Title }.Where(x => !string.IsNullOrWhiteSpace(x)));
            if (who.Length == 0) return null;
            return $"{who} (плеер: {s.SourceAppUserModelId}{(playing ? "" : ", на паузе")})";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------- живой бит (кивать, пока слушает) ----------

    /// <summary>
    /// Бит того, что играет, на лету — чтобы она кивала в такт, пока слушает (друг: «пусть почувствует ритм»).
    /// Огибающая атак по 10 мс (прирост громкости всей полосы и низа — бочка), темп — автокорреляция последних 6 с
    /// (60–180 ударов в минуту, мягкое предпочтение ~110), фаза — где гребёнка с этим шагом собирает больше атак.
    /// </summary>
    internal sealed class LiveBeat
    {
        private const int Fps = 100, Keep = 600;
        private readonly float[] _env = new float[Keep];
        private int _count;
        private double _acc, _accLow, _low, _prevE, _prevL;
        private int _accN, _hop = 441, _sinceEst;
        /// <summary>(период доли, с; через сколько секунд следующая доля).</summary>
        public event Action<double, double>? Beat;

        public void Feed(float[] mono, int sr)
        {
            _hop = sr / Fps;
            var a = Math.Exp(-2 * Math.PI * 150 / sr);
            foreach (var x in mono)
            {
                _low = a * _low + (1 - a) * x;
                _acc += x * x;
                _accLow += _low * _low;
                if (++_accN < _hop) continue;
                var e = Math.Log10(_acc / _accN + 1e-9);
                var l = Math.Log10(_accLow / _accN + 1e-9);
                var on = Math.Max(0, e - _prevE) + Math.Max(0, l - _prevL);
                _prevE = e;
                _prevL = l;
                _acc = _accLow = 0;
                _accN = 0;
                Array.Copy(_env, 1, _env, 0, Keep - 1);
                _env[Keep - 1] = (float)on;
                _count++;
                if (++_sinceEst >= 50 && _count >= 300) { _sinceEst = 0; Estimate(); }
            }
        }

        private void Estimate()
        {
            var n = Math.Min(_count, Keep);
            var start = Keep - n;
            var mean = 0.0;
            for (var i = start; i < Keep; i++) mean += _env[i];
            mean /= n;
            double Ac(int lag)
            {
                var s = 0.0;
                for (var i = start + lag; i < Keep; i++) s += (_env[i] - mean) * (_env[i - lag] - mean);
                return s / (n - lag);
            }
            var zero = Ac(0);
            if (zero <= 1e-9) return;
            int best = 0;
            var bestV = 0.0;
            for (var lag = 33; lag <= 100; lag++)
            {
                var bpm = 6000.0 / lag;
                var prior = Math.Exp(-0.5 * Math.Pow(Math.Log2(bpm / 110) / 0.9, 2));
                var v = Ac(lag) * prior;
                if (v > bestV) { bestV = v; best = lag; }
            }
            // слабая периодичность (речь, игра, тишина) — не кивать
            if (best == 0 || Ac(best) / zero < 0.12) return;
            var bestPh = 0;
            var bestSum = double.MinValue;
            for (var ph = 0; ph < best; ph++)
            {
                var s = 0.0;
                for (var i = Keep - 1 - ph; i >= start; i -= best) s += _env[i];
                if (s > bestSum) { bestSum = s; bestPh = ph; }
            }
            // последняя доля была bestPh кадров назад — следующая через best − bestPh
            Beat?.Invoke(best / (double)Fps, (best - bestPh) / (double)Fps);
        }
    }

    // ---------- эквиритмический перевод ----------

    private const string Vowels = "аеёиоуыэюяАЕЁИОУЫЭЮЯ";

    public static int Syllables(string s) => s.Count(c => Vowels.Contains(c));

    private const string TranslatorSystem =
        "Ты — Клава, поэтесса-песенница. Делаешь русский текст песни для своего кавера: каждая строка поётся на ТЕ ЖЕ НОТЫ, " +
        "что в оригинале, поэтому главное правило жёсткое — в русской строке РОВНО столько слогов (гласных букв а е ё и о у ы э ю я), " +
        "сколько указано в листе. Ударения русских слов старайся ставить на ТА (сильные, долгие ноты), долгие ноты «—» — на открытые " +
        "гласные. Это не подстрочник: сохрани смысл, образы и настроение, пиши живым поэтичным языком, грамотно, рифмуй там, где " +
        "рифмуется оригинал (перекрёстно или парно), без канцелярита и без англицизмов. Одинаковые строки оригинала (припев) переводи " +
        "одинаково. Строку «(распев без слов)» оставь знаком «=» (её споют как в оригинале). Слоги считай честно: после строки через " +
        "вторую черту распиши её по слогам через дефис — по одной гласной на слог (\"Нын-че я как будь-то не сво-я\" = 9); в самом " +
        "тексте строки дефисов-разбивки нет, и слово НЕ переносится на следующую строку — каждая строка из целых слов. " +
        "Ответ — только строки «NN | текст | сло-ги», по одной на номер, все номера по порядку, без пояснений.";

    /// <summary>
    /// Стиль песни для ACE-Step одной строкой по-английски (жанр, настроение, темп, инструменты — без вокала): мозг знает
    /// многие песни по названию, а паспорт слуха даёт факты. Замер 30.09 на «Lately»: описание LM самого ACE («raw, driving
    /// indie rock») увело кавер мимо мелодии (27 % кадров в полутоне), «melancholic slow indie folk ballad…» — 54 %.
    /// Пусто — мозг не ответил, ACE опишет сам.
    /// </summary>
    public static async Task<string> StyleAsync(Brain brain, JsonNode plan, CancellationToken ct)
    {
        var dir = plan["dir"]?.GetValue<string>() ?? "";
        var passport = File.Exists(Path.Combine(dir, "passport.txt"))
            ? string.Join("\n", File.ReadLines(Path.Combine(dir, "passport.txt")).Take(9)) : "";
        var system = "You describe songs for a music generator. Answer with ONE line in English, 12-30 words, comma-separated: " +
                     "genre, mood, tempo word (slow / mid-tempo / fast), main instruments, production feel. Never mention vocals, " +
                     "singers, voice or lyrics. No quotes, no explanations.";
        var user = $"Song: {plan["artist"]?.GetValue<string>()} - {plan["title"]?.GetValue<string>()}.\n" +
                   (passport.Length > 0 ? $"Measured by an audio analyzer (Russian):\n{passport}\n" : "") +
                   "If you know this song, describe how it really sounds; the analyzer's tempo may be a subdivision of the real one.";
        try
        {
            var r = await brain.OneShotAsync(system, user, null, 120, ct) ?? "";
            var line = r.Replace("\r", "").Split('\n').Select(x => x.Trim().Trim('"', '«', '»', '`')).FirstOrDefault(x => x.Length > 10) ?? "";
            line = VocalWordRx().Replace(line, "").Replace(" ,", ",").Replace(",,", ",").Trim(' ', ',', '.');
            return line.Length is > 10 and < 400 ? line : "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Write($"музыка: стиль для ACE — мозг не ответил ({ex.Message})");
            return "";
        }
    }

    [GeneratedRegex(@"[^,]*\b(vocal\w*|voice\w*|sing\w*|singer\w*|lyric\w*|male|female)\b[^,]*,?", RegexOptions.IgnoreCase)]
    private static partial Regex VocalWordRx();

    /// <summary>
    /// Перевод листа (translate.txt) мозгом Клавы с проверкой слогов (модели плохо считают слоги — поэтому строка
    /// расписывается по слогам, а считает всё равно код): строки не в размер уходят на переделку по три варианта
    /// (до трёх кругов), что так и не влезло — ближайший вариант (сервис подгонит ноты под слоги).
    /// </summary>
    public static async Task<List<string?>> TranslateAsync(Brain brain, JsonNode plan, IProgress<Step>? progress, CancellationToken ct)
    {
        var lines = plan["lines"]!.AsArray();
        var sheet = plan["sheet"]!.GetValue<string>();
        var need = lines.Select(l => l!["k"]!.GetValue<int>()).ToArray();
        var vocalise = lines.Select(l => l!["vocalise"]?.GetValue<bool>() ?? false).ToArray();
        var result = new string?[need.Length];
        progress?.Report(new Step(0.1, "перевожу песню слог в слог…"));
        var answer = await brain.OneShotAsync(TranslatorSystem,
            $"Песня: {plan["artist"]} — {plan["title"]}.\nЛист (номер | время | слогов | ритм | оригинал):\n{sheet}", null, 4000, ct)
            ?? throw new InvalidOperationException("мозг не ответил на перевод");
        Take(answer, result, need, all: true);
        for (var round = 1; round <= 3; round++)
        {
            var bad = Enumerable.Range(0, need.Length)
                .Where(i => !vocalise[i] && (result[i] is null || (result[i] != "=" && (Syllables(result[i]!) != need[i] || BrokenLine(result[i]!))))).ToList();
            if (bad.Count == 0) break;
            progress?.Report(new Step(0.1 + 0.25 * round, $"перевод: {bad.Count} строк не в размер — переделываю ({round}/3)"));
            var sb = new StringBuilder("Эти строки не влезают в ноты. Для КАЖДОЙ дай ТРИ разных варианта ровно с нужным числом слогов " +
                                       "(короче — выкинь слово или возьми короче синоним, длиннее — добавь «и», «же», «вновь», " +
                                       "прилагательное). Смысл и рифму с соседними строками держи. Весь текст — ниже для контекста.\n");
            foreach (var i in bad)
                sb.AppendLine($"{i:00}: нужно {need[i]} слогов, сейчас {(result[i] is null ? "пусто" : $"{Syllables(result[i]!)} — «{result[i]}»")}; " +
                              (result[i] is { } bl && BrokenLine(bl) ? "; слово порвано между строками — нужна строка из целых слов; " : "") +
                              $"оригинал: {lines[i]!["en"]}; ритм: {lines[i]!["rhythm"]}");
            sb.AppendLine("\nТекущий текст:");
            for (var i = 0; i < need.Length; i++) sb.AppendLine($"{i:00} | {result[i]}");
            sb.AppendLine("\nОтвет — только варианты, по строке на вариант: «NN | текст | сло-ги».");
            var fix = await brain.OneShotAsync(TranslatorSystem, sb.ToString(), null, 2000, ct);
            if (fix is null) break;
            Take(fix, result, need, all: false, only: bad);
        }
        for (var i = 0; i < need.Length; i++)
            if (vocalise[i] || result[i] is null) result[i] ??= "=";
        progress?.Report(new Step(1, "перевод готов"));
        return [.. result];
    }

    [GeneratedRegex(@"^\s*(\d{1,3})\s*[|:.)]\s*(.*?)\s*$")]
    private static partial Regex LineRx();

    [GeneratedRegex(@"(?<=[а-яёА-ЯЁ])-(?=[а-яёА-ЯЁ])")]
    private static partial Regex InnerHyphenRx();

    /// <summary>Модель иногда пишет строку уже разбитой по слогам («стек-ло гля-дит») — голос прочёл бы дефисы паузами.
    /// Замер 30.09 («Lately» из трея): разбивка бывает и в половине слов, так что счёт дефисов не годится — снимается
    /// любой дефис внутри слова, кроме настоящих: «кто-то», «где-нибудь», «кое-как», «по-русски», «из-за», «ну-ка».</summary>
    internal static string Unsyllabify(string t) => HyphenWordRx().Replace(t, m => RealHyphenRx().IsMatch(m.Value) ? m.Value : m.Value.Replace("-", ""));

    [GeneratedRegex(@"[а-яёА-ЯЁ]+(?:-[а-яёА-ЯЁ]+)+")]
    private static partial Regex HyphenWordRx();

    // настоящие дефисы: кто-то/где-нибудь (после вопросительного слова), ну-ка/дай-ка, всё-таки, кое-как, по-русски/по-моему,
    // во-первых, из-за/из-под, повторы (еле-еле, чуть-чуть), давным-давно, точь-в-точь, крест-накрест…
    [GeneratedRegex(@"^(?:(?:кто|кого|кому|кем|ком|что|чего|чему|чем|чём|как|какой|какая|какое|какие|каких|каким|какую|где|куда|откуда|когда|чей|чья|чьё|чьи|сколько|почему|зачем|отчего)-(?:то|либо|нибудь)|(?:ну|дай|давай|давайте|смотри|гляди|глянь|скажи|погоди|подожди|послушай|иди|пойди|возьми|поди|на)-ка|[а-яё]+-таки|кое-[а-яё]+|по-[а-яё]+(?:ски|цки|ому|ему|ьи)|(?:во|в)-[а-яё]+(?:ых|их)|из-(?:за|под)|давным-давно|точь-в-точь|бок-о-бок|крест-накрест|шиворот-навыворот|волей-неволей|мало-мальски|(?<w>[а-яё]+)-\k<w>)$", RegexOptions.IgnoreCase)]
    private static partial Regex RealHyphenRx();

    /// <summary>Строка рвёт слово на границе («…в другой го-» / «род — чужаки…»): голос прочёл бы «го» и «род» отдельно.</summary>
    internal static bool BrokenLine(string t) => t.TrimEnd().EndsWith('-') || Regex.IsMatch(t.TrimStart(), @"^-?[а-яё]{1,3}\s*[—–-]");

    /// <summary>Строки ответа «NN | текст | сло-ги»: на каждый номер — вариант ровно в размер, иначе ближайший к нужному.
    /// all — ответ на весь лист (пустое место заполняется любым вариантом), иначе меняются только строки из only.</summary>
    private static void Take(string answer, string?[] into, int[] need, bool all, List<int>? only = null)
    {
        var cand = new Dictionary<int, List<string>>();
        foreach (var raw in answer.Split('\n'))
        {
            var m = LineRx().Match(raw.Replace("**", ""));
            if (!m.Success) continue;
            var i = int.Parse(m.Groups[1].Value);
            var t = Unsyllabify(m.Groups[2].Value.Split('|')[0].Trim().Trim('«', '»', '"'));
            if (i < 0 || i >= into.Length || t.Length == 0) continue;
            (cand.TryGetValue(i, out var l) ? l : cand[i] = []).Add(t);
        }
        foreach (var (i, list) in cand)
        {
            if (!all && only is not null && !only.Contains(i)) continue;
            var best = list.FirstOrDefault(t => t == "=" || (Syllables(t) == need[i] && !BrokenLine(t)))
                       ?? list.OrderBy(t => Math.Abs(Syllables(t) - need[i])).First();
            if (into[i] is null || best == "=" || Math.Abs(Syllables(best) - need[i]) <= Math.Abs(Syllables(into[i]!) - need[i]))
                into[i] = best;
        }
    }

    /// <summary>
    /// Проверка без живой Клавы: KlavaDesk.exe --music-test listen [сек] | analyze &lt;файл&gt; | translate &lt;файл&gt; | style &lt;файл&gt; |
    /// sing &lt;файл&gt; [orig]. Поднимает музыкальный сервис (и выключает вместе с собой), мозг — из klava.json.
    /// </summary>
    public static int Test(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\music_test.log"));
        var services = new Services(cfg);
        var music = new MusicClient(cfg, services);
        var progress = new Progress<Step>(s => Console.WriteLine($"  {(int)(s.Progress * 100),3}% {s.Message}"));
        var what = args.FirstOrDefault() ?? "";
        try
        {
            switch (what)
            {
                case "listen":
                {
                    var sec = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 15;
                    Console.WriteLine($"играет: {NowPlayingAsync().GetAwaiter().GetResult() ?? "(плеер Windows молчит)"}");
                    var file = cfg.Abs($@"data\music\listen\test_{DateTime.Now:HHmmss}.wav");
                    var (path, got, peak) = RecordAsync(file, sec, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine($"записала {got:F1} с, пик {peak:F3}: {path}");
                    if (peak < 0.004) { Console.WriteLine("тишина — разбирать нечего"); return 2; }
                    var res = music.RunAsync("analyze", new JsonObject { ["path"] = path, ["quick"] = true }, progress, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine(res?["passport"]);
                    return 0;
                }
                case "analyze":
                {
                    var res = music.RunAsync("analyze", new JsonObject { ["path"] = args[1] }, progress, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine(res?["passport"]);
                    return 0;
                }
                case "style":
                {
                    var plan = music.RunAsync("plan", new JsonObject { ["path"] = args[1] }, progress, CancellationToken.None).GetAwaiter().GetResult()!;
                    using var brain = new Brain(cfg, saveHistory: false);
                    var style = StyleAsync(brain, plan, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine($"стиль для ACE: {(style.Length > 0 ? style : "(мозг не ответил — опишет LM ACE)")}");
                    return style.Length > 0 ? 0 : 1;
                }
                case "translate" or "sing":
                {
                    var plan = music.RunAsync("plan", new JsonObject { ["path"] = args[1] }, progress, CancellationToken.None).GetAwaiter().GetResult()!;
                    var orig = args.Length > 2 && args[2] == "orig";
                    List<string?>? lyrics = null;
                    if (!orig)
                    {
                        using var brain = new Brain(cfg, saveHistory: false);
                        var sw = Stopwatch.StartNew();
                        lyrics = TranslateAsync(brain, plan, progress, CancellationToken.None).GetAwaiter().GetResult();
                        Console.WriteLine($"перевод за {sw.Elapsed.TotalSeconds:F0} с:");
                        var lines = plan["lines"]!.AsArray();
                        var ok = 0;
                        for (var i = 0; i < lyrics.Count; i++)
                        {
                            var need = lines[i]!["k"]!.GetValue<int>();
                            var got = lyrics[i] is { } t && t != "=" ? Syllables(t) : need;
                            ok += got == need ? 1 : 0;
                            Console.WriteLine($"{i:00} [{got}/{need}{(got == need ? "" : " ✗")}] {lyrics[i]}   ← {lines[i]!["en"]}");
                        }
                        Console.WriteLine($"в размер: {ok}/{lyrics.Count}");
                    }
                    if (what == "translate") return 0;
                    var a = new JsonObject { ["path"] = args[1], ["orig"] = orig, ["engine"] = cfg.MusicCoverEngine };
                    if (lyrics is not null) a["lyrics"] = new JsonArray([.. lyrics.Select(x => (JsonNode?)JsonValue.Create(x))]);
                    if (!orig && cfg.MusicCoverEngine == "ace")
                    {
                        using var sb = new Brain(cfg, saveHistory: false);
                        if (StyleAsync(sb, plan, CancellationToken.None).GetAwaiter().GetResult() is { Length: > 0 } style) { a["style"] = style; Console.WriteLine($"стиль: {style}"); }
                    }
                    var cover = music.RunAsync("sing", a, progress, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine($"кавер: {cover?["dir"]}\\{cover?["mp3"]} за {cover?["seconds"]} с");
                    return 0;
                }
                case "beat":
                {
                    // живой бит на файле, как если бы он играл из колонок: темп по оценкам против разбора beat_this
                    using var r = new MediaFoundationReader(args[1]);
                    var sp = r.ToSampleProvider().ToMono();
                    var lb = new LiveBeat();
                    var t = 0.0;
                    var got = new List<double>();
                    lb.Beat += (period, next) => { got.Add(60 / period); if (got.Count % 6 == 1) Console.WriteLine($"  {t,5:F1} с: {60 / period:F0} BPM, следующая доля через {next:F2} с"); };
                    var buf = new float[sp.WaveFormat.SampleRate / 10];
                    int n;
                    while ((n = sp.Read(buf.AsSpan())) > 0 && t < 60)
                    {
                        lb.Feed(buf[..n], sp.WaveFormat.SampleRate);
                        t += n / (double)sp.WaveFormat.SampleRate;
                    }
                    Console.WriteLine(got.Count == 0 ? "бита не нашла" : $"медиана {got.Order().ElementAt(got.Count / 2):F0} BPM по {got.Count} оценкам");
                    return 0;
                }
                case "studio":
                {
                    // окно студии за краем экрана: грузит трек, рисует себя в картинку, закрывается (экран друга не трогает)
                    var shot = cfg.Abs(@"data\logs\studio_test.png");
                    Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                    using var form = new MusicStudioForm(cfg.Abs(@"data\music"), new MusicStudioForm.Actions
                    {
                        Analyze = _ => { }, Sing = (_, _, _) => { }, PlayCover = _ => { }, StopSong = () => { }, Cancel = () => { },
                        AskOpinion = _ => { }, Similar = _ => { },
                    })
                    { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
                    var timer = new System.Windows.Forms.Timer { Interval = 4000 };
                    timer.Tick += (_, _) =>
                    {
                        timer.Stop();
                        using var bmp = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                        bmp.Save(shot, System.Drawing.Imaging.ImageFormat.Png);
                        form.Close();
                    };
                    form.Shown += (_, _) => { form.LoadTrack(args[1]); timer.Start(); };
                    Application.Run(form);
                    Console.WriteLine($"снимок студии: {shot}");
                    return 0;
                }
                case "similar":
                {
                    var res = music.RunAsync("analyze", new JsonObject { ["path"] = args[1] }, progress, CancellationToken.None).GetAwaiter().GetResult()!;
                    var dir = res["dir"]!.GetValue<string>();
                    var seed = args.Length > 2 && int.TryParse(args[2], out var sd) ? sd : 7;
                    var sw = Stopwatch.StartNew();
                    var r = MusicCompose.Compose(dir, cfg.Abs($@"data\music\similar\{Path.GetFileName(dir)}-{seed}"), cfg.Abs(cfg.MusicSoundFont), seed);
                    Console.WriteLine($"за {sw.Elapsed.TotalSeconds:F1} с: {r.Wav}\n{r.Description}");
                    return 0;
                }
                default:
                    Console.WriteLine("--music-test listen [сек] | analyze <файл> | translate <файл> | sing <файл> [orig] | similar <файл> [seed]");
                    return 1;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or COMException)
        {
            Console.WriteLine($"ошибка: {ex.Message}");
            return 3;
        }
    }
}
