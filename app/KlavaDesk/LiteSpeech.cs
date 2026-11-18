using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SherpaOnnx;

namespace KlavaDesk;

/// <summary>
/// Лёгкий комплект слуха и голоса — без Python и без видеокарты, для любого ноутбука: sherpa-onnx (ONNX Runtime на процессоре).
///  - слух: GigaAM v3 (русский CTC с пунктуацией), Silero VAD режет запись на куски речи (время кусков нужно для вырезания эха);
///  - голос: Supertonic-3 (русский, 10 готовых голосов, в 3–4 раза быстрее реального времени на 4 ядрах), запасной — Piper «Ирина»;
///  - кто говорит: CAM++ (WeSpeaker), профили в той же папке data\speakers, что и у speaker_id.py.
/// Модели лежат в models\lite (их качает tools\fetch_lite.ps1). Всё грузится лениво, при первом обращении, и живёт до выхода.
/// </summary>
internal static class Lite
{
    private static readonly object Gate = new();
    private static LiteModels? _models;
    private static LiteEars? _ears;
    private static LiteVoice? _voice;
    private static LiteSpeakers? _speakers;

    public static LiteModels Models(KlavaConfig cfg)
    {
        lock (Gate) return _models ??= LiteModels.Find(cfg.Abs(cfg.LiteDir));
    }

    /// <summary>Сколько потоков процессора отдать каждой модели: половина ядер, но не больше 4 (дальше прироста нет).</summary>
    public static int Threads(KlavaConfig cfg) => cfg.LiteThreads > 0 ? cfg.LiteThreads : Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    public static LiteEars Ears(KlavaConfig cfg)
    {
        lock (Gate)
        {
            if (_ears is not null) return _ears;
            var m = Models(cfg);
            if (!m.CanHear) throw new FileNotFoundException($"нет модели слуха GigaAM в {m.Dir} (запусти tools\\fetch_lite.ps1)");
            var t = DateTime.UtcNow;
            _ears = new LiteEars(m, Threads(cfg));
            Log.Write($"лёгкий слух: GigaAM v3{(m.Vad is null ? "" : " + Silero VAD")} загружен за {(DateTime.UtcNow - t).TotalMilliseconds:F0} мс");
            return _ears;
        }
    }

    public static LiteVoice Voice(KlavaConfig cfg)
    {
        lock (Gate)
        {
            if (_voice is not null) return _voice;
            var m = Models(cfg);
            if (!m.CanSpeak) throw new FileNotFoundException($"нет модели голоса Supertonic/Piper в {m.Dir} (запусти tools\\fetch_lite.ps1)");
            var t = DateTime.UtcNow;
            _voice = new LiteVoice(m, Threads(cfg));
            Log.Write($"лёгкий голос: {_voice.Name} загружен за {(DateTime.UtcNow - t).TotalMilliseconds:F0} мс");
            return _voice;
        }
    }

    /// <summary>Модель отпечатков голоса: своя из настроек (speakerModel, по умолчанию TitaNet-large), иначе из лёгкого комплекта.</summary>
    public static string? SpeakerModelPath(KlavaConfig cfg)
    {
        var own = string.IsNullOrWhiteSpace(cfg.SpeakerModel) ? null : cfg.Abs(cfg.SpeakerModel);
        return own is not null && File.Exists(own) ? own : Models(cfg).SpeakerModel;
    }

    /// <summary>Профили считаются в процессе (sherpa-onnx), а не питоновским speaker_id.py с WavLM.</summary>
    public static bool InProcessSpeakers(KlavaConfig cfg) =>
        cfg.LiteSpeech || !cfg.SpeakerBackend.Equals("wavlm", StringComparison.OrdinalIgnoreCase) && SpeakerModelPath(cfg) is not null;

    public static LiteSpeakers Speakers(KlavaConfig cfg)
    {
        lock (Gate)
        {
            if (_speakers is not null) return _speakers;
            var model = SpeakerModelPath(cfg)
                        ?? throw new FileNotFoundException($"нет модели голосовых профилей ({cfg.SpeakerModel} или в {cfg.Abs(cfg.LiteDir)}; запусти tools\\fetch_lite.ps1)");
            var tag = LiteSpeakers.ModelTag(model);
            // порог: свой из настроек, иначе замеренный для этой модели (у ERes2Net лёгкого комплекта — liteSpeakerThreshold)
            var thr = cfg.SpeakerThreshold > 0 ? cfg.SpeakerThreshold : tag == "eres2net" ? cfg.LiteSpeakerThreshold : LiteSpeakers.DefaultThreshold(tag);
            _speakers = new LiteSpeakers(model, cfg.Abs(@"data\speakers"), thr, calibrated: cfg.SpeakerThreshold <= 0);
            Log.Write($"голосовые профили: {_speakers.Model} в процессе, порог «свой» {_speakers.DecisionThreshold:F2} (короткие {_speakers.ShortThreshold:F2})");
            return _speakers;
        }
    }

    // ---------------- звук ----------------

    /// <summary>WAV любого обычного формата (PCM 8/16/24/32, float32, любые каналы) → моно float, частота в sr.</summary>
    public static float[] ReadWav(byte[] b, out int sr)
    {
        sr = 16000;
        if (b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE")
            throw new InvalidDataException("это не WAV");
        int fmt = 1, ch = 1, bits = 16, pos = 12, dataPos = -1, dataLen = 0;
        while (pos + 8 <= b.Length)
        {
            var id = Encoding.ASCII.GetString(b, pos, 4);
            var len = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pos + 4));
            if (id == "fmt ")
            {
                fmt = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(pos + 8));
                ch = Math.Max((int)BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(pos + 10)), 1);
                sr = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(pos + 12));
                bits = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(pos + 22));
                // WAVE_FORMAT_EXTENSIBLE: настоящий формат — в подформате
                if (fmt == 0xFFFE && len >= 26) fmt = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(pos + 32));
            }
            else if (id == "data")
            {
                dataPos = pos + 8;
                dataLen = len < 0 || dataPos + len > b.Length ? b.Length - dataPos : len;
                break;
            }
            pos += 8 + len + (len & 1);
        }
        if (dataPos < 0) throw new InvalidDataException("в WAV нет данных");
        var bytesPer = bits / 8;
        if (bytesPer is < 1 or > 4) throw new InvalidDataException($"неподдерживаемая разрядность {bits}");
        var n = dataLen / (bytesPer * ch);
        var outp = new float[n];
        for (int i = 0; i < n; i++)
        {
            double sum = 0;
            for (int c = 0; c < ch; c++)
            {
                var o = dataPos + (i * ch + c) * bytesPer;
                sum += (fmt, bytesPer) switch
                {
                    (3, 4) => BitConverter.ToSingle(b, o),
                    (_, 1) => (b[o] - 128) / 128.0,
                    (_, 2) => BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(o)) / 32768.0,
                    (_, 3) => ((b[o] << 8) | (b[o + 1] << 16) | (b[o + 2] << 24)) / 2147483648.0,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(o)) / 2147483648.0,
                };
            }
            outp[i] = (float)(sum / ch);
        }
        return outp;
    }

    /// <summary>WAV 16 бит моно в звук 16 кГц (что пишет слух, то и нужно моделям).</summary>
    public static float[] ReadWav16k(byte[] wav)
    {
        var a = ReadWav(wav, out var sr);
        return sr == 16000 ? a : Resample(a, sr, 16000);
    }

    /// <summary>
    /// Передискретизация с фильтром (окно Блэкмана, 32 отвода): при понижении частоты срезает всё выше новой Найквиста,
    /// иначе 44,1 → 24 кГц давал бы металлический звон.
    /// </summary>
    public static float[] Resample(float[] x, double fromRate, int toRate)
    {
        if (x.Length == 0 || Math.Abs(fromRate - toRate) < 0.5) return x;
        var ratio = toRate / fromRate;
        var n = (int)(x.Length * ratio);
        var y = new float[n];
        var cutoff = Math.Min(1.0, ratio) * 0.95;
        const int half = 16;
        for (int i = 0; i < n; i++)
        {
            var t = i / ratio;
            var c = (int)Math.Floor(t);
            double acc = 0, wsum = 0;
            for (int k = c - half + 1; k <= c + half; k++)
            {
                if (k < 0 || k >= x.Length) continue;
                var d = t - k;
                var sinc = Math.Abs(d) < 1e-9 ? 1.0 : Math.Sin(Math.PI * cutoff * d) / (Math.PI * cutoff * d);
                var wpos = (d + half) / (2.0 * half);
                if (wpos is < 0 or > 1) continue;
                var win = 0.42 - 0.5 * Math.Cos(2 * Math.PI * wpos) + 0.08 * Math.Cos(4 * Math.PI * wpos);
                var w = sinc * win;
                acc += x[k] * w;
                wsum += w;
            }
            y[i] = (float)(wsum > 1e-9 ? acc / wsum : 0);
        }
        return y;
    }

    /// <summary>float [-1..1] → WAV 16 бит моно.</summary>
    public static byte[] ToWav(float[] a, int rate)
    {
        var wav = new byte[44 + a.Length * 2];
        var s = wav.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + a.Length * 2);
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(s[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        Encoding.ASCII.GetBytes("data").CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], a.Length * 2);
        for (int i = 0; i < a.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(s[(44 + i * 2)..], (short)Math.Clamp(Math.Round(a[i] * 32767.0), -32768, 32767));
        return wav;
    }
}

/// <summary>Где что лежит в models\lite. Папки ищутся по шаблону — новые версии архивов подхватятся без правки кода.</summary>
internal sealed class LiteModels
{
    public required string Dir { get; init; }
    public string? AsrModel { get; init; }
    public string? AsrTokens { get; init; }
    public string? Vad { get; init; }
    public string? SpeakerModel { get; init; }
    public string? SupertonicDir { get; init; }
    public string? PiperModel { get; init; }
    public string? PiperTokens { get; init; }
    public string? PiperData { get; init; }

    public bool CanHear => AsrModel is not null && AsrTokens is not null;
    public bool CanSpeak => SupertonicDir is not null || PiperModel is not null;
    public bool CanTellSpeakers => SpeakerModel is not null;

    /// <summary>Модели отпечатков голоса по предпочтению (замерено на живых записях друзей и голосах Supertonic).</summary>
    public static readonly string[] SpeakerModels =
    [
        "3dspeaker_speech_eres2net_sv_en_voxceleb_16k.onnx",
        "3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx",
        "3dspeaker_speech_eres2netv2_sv_zh-cn_16k-common.onnx",
        "nemo_en_titanet_large.onnx",
    ];

    public static readonly string[] SupertonicFiles =
        ["duration_predictor.int8.onnx", "text_encoder.int8.onnx", "vector_estimator.int8.onnx", "vocoder.int8.onnx", "tts.json", "unicode_indexer.bin", "voice.bin"];

    public static LiteModels Find(string dir)
    {
        string? Sub(string pattern) => Directory.Exists(dir)
            ? Directory.GetDirectories(dir, pattern).OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
            : null;
        string? F(string? d, string name) => d is not null && File.Exists(Path.Combine(d, name)) ? Path.Combine(d, name) : null;

        var asr = Sub("*giga-am*");
        var asrModel = F(asr, "model.int8.onnx") ?? F(asr, "model.onnx");
        var st = Sub("*supertonic*");
        if (st is not null && !SupertonicFiles.All(f => File.Exists(Path.Combine(st, f)))) st = null;
        var piper = Sub("vits-piper-ru_RU-*");
        var piperModel = piper is null ? null : Directory.GetFiles(piper, "*.onnx").FirstOrDefault();
        // отпечатки голоса: модели 3D-Speaker. На записях друзей ERes2Net (VoxCeleb) не спутал ни одной пары,
        // CAM++ zh_en — 0,6 %; wespeaker (CAM++, ResNet34) в sherpa ошибались в 20–40 % пар — их не берём
        var spk = Directory.Exists(dir)
            ? SpeakerModels.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists)
              ?? Directory.GetFiles(dir, "3dspeaker_*.onnx").Concat(Directory.GetFiles(dir, "nemo_*titanet*.onnx")).FirstOrDefault()
            : null;
        return new LiteModels
        {
            Dir = dir,
            AsrModel = asrModel,
            AsrTokens = F(asr, "tokens.txt"),
            Vad = F(dir, "silero_vad.onnx"),
            SpeakerModel = spk,
            SupertonicDir = st,
            PiperModel = piperModel,
            PiperTokens = F(piper, "tokens.txt"),
            PiperData = piper is not null && Directory.Exists(Path.Combine(piper, "espeak-ng-data")) ? Path.Combine(piper, "espeak-ng-data") : null,
        };
    }

    public override string ToString() =>
        $"слух {(CanHear ? "GigaAM" : "нет")}, VAD {(Vad is null ? "нет" : "Silero")}, голос {(SupertonicDir is not null ? "Supertonic" : PiperModel is not null ? "Piper" : "нет")}, " +
        $"профили {(SpeakerModel is null ? "нет" : LiteSpeakers.ModelTag(SpeakerModel))}";
}

/// <summary>Слух: Silero VAD находит куски речи, GigaAM распознаёт их пачкой.</summary>
internal sealed class LiteEars : IDisposable
{
    private const int Rate = 16000;
    private readonly OfflineRecognizer _rec;
    private readonly VadModelConfig? _vad;
    private readonly object _gate = new();

    public LiteEars(LiteModels m, int threads)
    {
        var rc = new OfflineRecognizerConfig();
        rc.ModelConfig.NeMoCtc.Model = m.AsrModel!;
        rc.ModelConfig.Tokens = m.AsrTokens!;
        rc.ModelConfig.NumThreads = threads;
        rc.ModelConfig.Provider = "cpu";
        rc.ModelConfig.Debug = 0;
        _rec = new OfflineRecognizer(rc);
        if (m.Vad is null) return;
        var vc = new VadModelConfig();
        vc.SileroVad.Model = m.Vad;
        vc.SileroVad.Threshold = 0.45f;
        vc.SileroVad.MinSilenceDuration = 0.45f;
        vc.SileroVad.MinSpeechDuration = 0.2f;
        vc.SileroVad.MaxSpeechDuration = 20f;
        vc.SileroVad.WindowSize = 512;
        vc.SampleRate = Rate;
        vc.NumThreads = 1;
        vc.Provider = "cpu";
        vc.Debug = 0;
        _vad = vc;
    }

    /// <summary>Куски речи с временем (секунды от начала записи) и текстом. Пусто — речи нет.</summary>
    public List<SttSegment> Transcribe(float[] a)
    {
        var spans = new List<(int From, int To)>();
        if (_vad is { } vc)
        {
            using var vad = new VoiceActivityDetector(vc, Math.Max(30f, a.Length / (float)Rate + 5f));
            var w = vc.SileroVad.WindowSize;
            for (int i = 0; i + w <= a.Length; i += w) vad.AcceptWaveform(a[i..(i + w)]);
            vad.Flush();
            while (!vad.IsEmpty())
            {
                var s = vad.Front();
                // VAD режет впритык — 0,2 с запаса с каждой стороны, иначе первый и последний звук слова теряются
                spans.Add((Math.Max(0, s.Start - Rate / 5), Math.Min(a.Length, s.Start + s.Samples.Length + Rate / 5)));
                vad.Pop();
            }
        }
        // VAD ничего не нашёл (шёпот, тихий микрофон) — распознаём целиком: детектор громкости в Ears уже решил, что это речь
        if (spans.Count == 0) spans.Add((0, a.Length));

        var outp = new List<SttSegment>();
        lock (_gate)
        {
            var streams = new List<OfflineStream>();
            try
            {
                foreach (var (from, to) in spans)
                {
                    var st = _rec.CreateStream();
                    st.AcceptWaveform(Rate, a[from..to]);
                    streams.Add(st);
                }
                _rec.Decode(streams);
                for (int i = 0; i < streams.Count; i++)
                {
                    var t = streams[i].Result.Text.Trim();
                    if (t.Length > 0) outp.Add(new SttSegment(spans[i].From / (double)Rate, spans[i].To / (double)Rate, t));
                }
            }
            finally
            {
                foreach (var st in streams) st.Dispose();
            }
        }
        return outp;
    }

    public void Dispose() => _rec.Dispose();
}

/// <summary>
/// Голос: Supertonic-3 (или Piper, если Supertonic не скачан). Выход — WAV 24 кГц 16 бит моно, как у большого голоса:
/// страница и расчёт «когда она договорит» рассчитаны на этот формат.
/// </summary>
internal sealed class LiteVoice : IDisposable
{
    public const int OutRate = 24000;
    private readonly OfflineTts _tts;
    private readonly bool _supertonic;
    private readonly object _gate = new();

    public string Name { get; }
    public int Voices => _tts.NumSpeakers;

    public LiteVoice(LiteModels m, int threads)
    {
        LiteStress.Init(KlavaConfig.Load());
        var tc = new OfflineTtsConfig();
        if (m.SupertonicDir is { } d)
        {
            tc.Model.Supertonic.DurationPredictor = Path.Combine(d, "duration_predictor.int8.onnx");
            tc.Model.Supertonic.TextEncoder = Path.Combine(d, "text_encoder.int8.onnx");
            tc.Model.Supertonic.VectorEstimator = Path.Combine(d, "vector_estimator.int8.onnx");
            tc.Model.Supertonic.Vocoder = Path.Combine(d, "vocoder.int8.onnx");
            tc.Model.Supertonic.TtsJson = Path.Combine(d, "tts.json");
            tc.Model.Supertonic.UnicodeIndexer = Path.Combine(d, "unicode_indexer.bin");
            tc.Model.Supertonic.VoiceStyle = Path.Combine(d, "voice.bin");
            _supertonic = true;
            Name = "Supertonic-3";
        }
        else
        {
            tc.Model.Vits.Model = m.PiperModel!;
            tc.Model.Vits.Tokens = m.PiperTokens ?? "";
            tc.Model.Vits.DataDir = m.PiperData ?? "";
            Name = "Piper " + Path.GetFileNameWithoutExtension(m.PiperModel);
        }
        tc.Model.NumThreads = threads;
        tc.Model.Provider = "cpu";
        tc.Model.Debug = 0;
        tc.MaxNumSentences = 1;
        _tts = new OfflineTts(tc);
    }

    /// <summary>
    /// Синтез фразы. pitch &gt; 1 — голос выше при том же темпе: модель говорит медленнее в pitch раз, а запись
    /// проигрывается быстрее во столько же (тембр становится моложе, «кошачьее»).
    /// </summary>
    public byte[] Synth(string text, int sid, double speed, int steps, double pitch) =>
        SynthPrepared(LiteText.Normalize(text), sid, speed, steps, pitch);

    /// <summary>Синтез уже подготовленного текста (без нормализации) — для проверок произношения.</summary>
    public byte[] SynthPrepared(string t, int sid, double speed, int steps, double pitch)
    {
        if (t.Length == 0) return [];
        pitch = Math.Clamp(pitch, 0.8, 1.3);
        var sp = (float)Math.Clamp(speed / pitch, 0.5, 2.0);
        float[] samples;
        int rate;
        lock (_gate)
        {
            OfflineTtsGeneratedAudio au;
            if (_supertonic)
            {
                var g = new OfflineTtsGenerationConfig { Sid = Math.Clamp(sid, 0, Math.Max(0, _tts.NumSpeakers - 1)), NumSteps = Math.Clamp(steps, 2, 32), Speed = sp };
                g.Extra["lang"] = "ru";
                au = _tts.GenerateWithConfig(t, g, (_, _, _, _) => 1);
            }
            else au = _tts.Generate(t, sp, 0);
            // у OfflineTtsGeneratedAudio есть Dispose, но нет IDisposable — освобождаем руками
            try
            {
                samples = au.Samples;
                rate = au.SampleRate;
            }
            finally
            {
                au.Dispose();
            }
        }
        if (samples.Length == 0) return [];
        var y = Lite.Resample(samples, rate * pitch, OutRate);
        // ровная громкость: пик 0,9 — как у большого голоса, чтобы при подмене одного другим не прыгала громкость
        var peak = 0f;
        foreach (var v in y) peak = Math.Max(peak, Math.Abs(v));
        if (peak > 1e-4f) { var k = 0.9f / peak; for (int i = 0; i < y.Length; i++) y[i] *= k; }
        return Lite.ToWav(y, OutRate);
    }

    public void Dispose() => _tts.Dispose();
}

/// <summary>Запись профиля, которая похоже не его голос (проверка «без неё»): чей голос на ней на самом деле.</summary>
internal sealed record SuspectSample(string Name, string File, double Own, string OtherName, double Other);

/// <summary>Итог долгой записи профиля.</summary>
internal sealed record LongEnrollResult(bool Ok, int Kept, int Chunks, double SpeechSec, string Report);

/// <summary>
/// Голосовые профили на модели отпечатков голоса sherpa-onnx (TitaNet-large или 3D-Speaker), прямо в процессе.
///
/// Каждый человек — папка data\speakers\&lt;имя&gt;\ с записями; каждая запись режется на окна по 1,5 с, окна кэшируются
/// рядом (&lt;запись&gt;.&lt;модель&gt;.bin). Записи двух сортов: опорные (записал сам друг в окне профилей, до MaxAnchors)
/// и автодоучка по ходу разговора (auto_*.wav, до MaxAuto — вытесняются первыми). Всё, что попадает в профиль, проходит
/// проверку: хватает речи, в записи один голос, и он не ближе к другому человеку (иначе в профиль Аксюты утекали
/// Настя и эхо самой Клавы — и дальше она «узнавала» Аксюту, когда та молчала). Сомнительное не удаляется, а уезжает
/// в data\speakers\_карантин\ — вернуть можно руками.
/// </summary>
internal sealed class LiteSpeakers : IDisposable
{
    /// <summary>Короткое имя модели: им помечены кэши окон, чтобы при смене модели не смешать чужие отпечатки.</summary>
    public string Model { get; }

    public static string ModelTag(string file)
    {
        var n = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
        return n.Contains("eres2netv2") ? "eres2netv2" : n.Contains("eres2net") ? "eres2net" : n.Contains("campplus") ? "campplus"
            : n.Contains("titanet") ? "titanet" : new string(n.Where(char.IsLetterOrDigit).Take(24).ToArray());
    }

    /// <summary>
    /// Порог «тот же человек» по умолчанию для модели — до первой тренировки слуха (она подбирает свой по записям).
    /// Замер --speakers-bench 26.09 на записях друзей, куски по 2 с против профиля без их записи (равные ошибки):
    /// CAM++ 9,9 % на 0,42 (1,2 с — 13,2 % на 0,39), ERes2NetV2 9,9 % на 0,51, TitaNet 12,3 % на 0,34.
    /// </summary>
    public static double DefaultThreshold(string tag) => tag switch
    {
        "titanet" => 0.36,
        "campplus" => 0.42,
        "eres2netv2" => 0.51,
        _ => 0.45,
    };

    private const int MaxAnchors = 200, MaxAuto = 12, MaxLongTake = 150;
    private const double Win = 1.5, Hop = 0.75;
    private const int Rate = 16000;
    public const string AutoPrefix = "auto_";
    public const string QuarantineDir = "_карантин";

    private readonly SpeakerEmbeddingExtractor _ex;
    private readonly string _store;
    private readonly object _gate = new();
    private readonly Dictionary<string, float[][]> _cache = new();
    // «кто говорит» решается в пространстве обученного слуха (если самопроверка показала, что с ним точнее),
    // центры профилей в этом пространстве — свой кэш
    private TrainedEar? _ear;
    private readonly Dictionary<string, float[]> _decisionMeans = new();

    /// <summary>Порог сырых отпечатков: проверка записей перед профилем, отбор кусков долгой записи, доучивание.</summary>
    public double Threshold { get; }
    /// <summary>Порог «тот же человек» для опознания фраз длиннее 1,2 с (после тренировки — подобран по записям).</summary>
    public double DecisionThreshold { get; private set; }
    /// <summary>То же для коротких фраз (до 1,2 с): отпечаток шумнее, порог ниже.</summary>
    public double ShortThreshold { get; private set; }
    /// <summary>Итог последнего обучения слуха (для окна профилей), пусто — не обучался.</summary>
    public string EarReport { get; private set; } = "";
    private string EarFile => Path.Combine(_store, $"_слух.{Model}.bin");
    private string CalibFile => Path.Combine(_store, $"_слух.{Model}.json");
    /// <summary>Почему последняя запись в профиль не принята (для окна профилей).</summary>
    public string LastReason { get; private set; } = "";
    /// <summary>Запись «того же голоса», что остальные записи профиля (близость к медоиду записей).</summary>
    private double MajoritySim => Threshold - 0.1;
    /// <summary>Окна по 1,5 с одного голоса похожи слабее целых записей — для проверки «в записи один голос».</summary>
    private double WindowSim => Threshold - 0.08;

    /// <param name="calibrated">Брать пороги и обученный слух из прошлой тренировки (false — порог задан в настройках руками).</param>
    public LiteSpeakers(string model, string store, double threshold, bool calibrated = true)
    {
        _ex = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = model, NumThreads = 2, Provider = "cpu", Debug = 0 });
        Model = ModelTag(model);
        _store = store;
        Directory.CreateDirectory(store);
        Threshold = threshold > 0 ? threshold : DefaultThreshold(Model);
        DecisionThreshold = Threshold;
        ShortThreshold = Threshold - 0.04;
        if (calibrated) LoadCalibration();
    }

    /// <summary>Пороги и слух из прошлой тренировки (_слух.&lt;модель&gt;.json/.bin); битое или чужое — остаются по умолчанию.</summary>
    private void LoadCalibration()
    {
        try
        {
            if (!File.Exists(CalibFile)) return;
            var j = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(CalibFile));
            if (j?["decision"]?.GetValue<double>() is not { } dec || j["short"]?.GetValue<double>() is not { } sh) return;
            var useEar = j["useEar"]?.GetValue<bool>() ?? false;
            var ear = useEar ? TrainedEar.Load(EarFile) : null;
            if (useEar && ear is null) { Log.Write("профили: файл обученного слуха не читается — опознаю по сырым отпечаткам"); return; }
            _ear = ear;
            DecisionThreshold = dec;
            ShortThreshold = sh;
            EarReport = j["report"]?.GetValue<string>() ?? "";
            Log.Write($"профили: слух {(ear is null ? "без обучения" : "обученный")}, порог {dec:F2} (короткие {sh:F2})");
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException or FormatException)
        {
            Log.Error("профили: калибровка слуха", ex);
        }
    }

    public static string Sanitize(string name)
    {
        var s = new string(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        if (s.Length > 40) s = s[..40];
        return s.Length == 0 ? "unnamed" : s;
    }

    private string ProfileDir(string name) => Path.Combine(_store, Sanitize(name));

    public List<string> Names() => Directory.Exists(_store)
        ? Directory.GetDirectories(_store)
            .Where(d => !Path.GetFileName(d).StartsWith('_') && Directory.GetFiles(d, "*.wav").Length > 0)
            .Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList()
        : [];

    private List<string> Samples(string name)
    {
        var d = ProfileDir(name);
        return Directory.Exists(d) ? Directory.GetFiles(d, "*.wav").OrderBy(File.GetLastWriteTimeUtc).ToList() : [];
    }

    private static bool IsAuto(string f) => Path.GetFileName(f).StartsWith(AutoPrefix, StringComparison.Ordinal);

    // ---------------- отпечатки ----------------

    /// <summary>Срезать тишину по краям: у фраз от детектора голоса секунда тишины в хвосте.</summary>
    private static float[] TrimSilence(float[] a)
    {
        const int frame = 480;
        if (a.Length < frame * 4) return a;
        var n = a.Length / frame;
        var rms = FrameRms(a, frame);
        var thr = Math.Max(rms.Max() * 0.08, 0.004);
        int first = -1, last = -1;
        for (int i = 0; i < n; i++) if (rms[i] > thr) { if (first < 0) first = i; last = i; }
        if (first < 0) return a;
        var st = Math.Max(0, first - 3) * frame;
        var en = Math.Min(n, last + 4) * frame;
        return a[st..en];
    }

    private static double[] FrameRms(float[] a, int frame)
    {
        var n = a.Length / frame;
        var rms = new double[n];
        for (int i = 0; i < n; i++)
        {
            double s = 0;
            for (int j = 0; j < frame; j++) { var v = a[i * frame + j]; s += v * v; }
            rms[i] = Math.Sqrt(s / frame + 1e-12);
        }
        return rms;
    }

    private float[] EmbedAudio(float[] audio)
    {
        audio = TrimSilence(audio);
        if (audio.Length < Rate / 2) throw new InvalidDataException("слишком коротко для отпечатка голоса (нужно ≥0,5 с речи)");
        var peak = 0f;
        foreach (var v in audio) peak = Math.Max(peak, Math.Abs(v));
        if (peak > 1e-4f)
        {
            // громкость не должна влиять на отпечаток
            var k = 0.9f / peak;
            audio = audio.Select(v => v * k).ToArray();
        }
        float[] e;
        lock (_gate)
        {
            using var s = _ex.CreateStream();
            s.AcceptWaveform(Rate, audio);
            s.InputFinished();
            e = _ex.Compute(s);
        }
        var norm = MathF.Sqrt(e.Sum(x => x * x));
        if (norm > 1e-9f) for (int i = 0; i < e.Length; i++) e[i] /= norm;
        return e;
    }

    private float[] Embed(byte[] wav) => EmbedAudio(Lite.ReadWav16k(wav));

    private static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>Запись режется на окна по 1,5 с: в одной записи бывает и друг, и кто-то ещё.</summary>
    private List<float[]> WindowEmbeddings(float[] audio)
    {
        audio = TrimSilence(audio);
        var total = audio.Length / (double)Rate;
        if (total < Win + 0.3) return [EmbedAudio(audio)];
        var outp = new List<float[]>();
        for (var st = 0.0; st + Win <= total + 1e-6; st += Hop)
        {
            var from = (int)(st * Rate);
            var to = Math.Min(audio.Length, (int)((st + Win) * Rate));
            var seg = audio[from..to];
            double e = 0;
            foreach (var v in seg) e += v * v;
            if (Math.Sqrt(e / Math.Max(1, seg.Length)) <= 0.003) continue;   // тишина между словами — не отпечаток
            try { outp.Add(EmbedAudio(seg)); }
            catch (InvalidDataException) { }
        }
        return outp.Count > 0 ? outp : [EmbedAudio(audio)];
    }

    /// <summary>Окна основного голоса профиля: близкие к медоиду.</summary>
    private float[][] Majority(float[][] v)
    {
        if (v.Length < 4) return v;
        var med = Medoid(v);
        var keep = v.Where(x => Dot(v[med], x) >= MajoritySim).ToArray();
        return keep.Length >= 2 ? keep : v;
    }

    private static int Medoid(float[][] v)
    {
        var mean = new double[v.Length];
        for (int i = 0; i < v.Length; i++)
            for (int j = 0; j < v.Length; j++) mean[i] += Dot(v[i], v[j]);
        return Array.IndexOf(mean, mean.Max());
    }

    private string EmbFile(string wav) => Path.ChangeExtension(wav, $".{Model}.emb");

    /// <summary>Отпечаток записи целиком (кэш рядом с ней): по замеру точнее, чем окна по 1,5 с.</summary>
    private float[]? SampleEmbedding(string f)
    {
        var cf = EmbFile(f);
        try
        {
            if (File.Exists(cf) && File.GetLastWriteTimeUtc(cf) >= File.GetLastWriteTimeUtc(f))
            {
                var b = File.ReadAllBytes(cf);
                if (b.Length >= 4 && b.Length % 4 == 0)
                {
                    var v = new float[b.Length / 4];
                    Buffer.BlockCopy(b, 0, v, 0, b.Length);
                    return v;
                }
            }
        }
        catch (IOException) { }
        try
        {
            var v = EmbedAudio(Lite.ReadWav16k(File.ReadAllBytes(f)));
            var buf = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, buf, 0, buf.Length);
            File.WriteAllBytes(cf, buf);
            return v;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            Log.Write($"профили: запись {Path.GetFileName(Path.GetDirectoryName(f))}/{Path.GetFileName(f)} пропущена ({ex.Message})");
            return null;
        }
    }

    /// <summary>Отпечатки записей профиля (по одному на запись), без тех, что далеки от основного голоса.</summary>
    private float[][]? ProfileMatrix(string name)
    {
        lock (_cache) if (_cache.TryGetValue(name, out var c)) return c;
        var all = Samples(name).Select(SampleEmbedding).OfType<float[]>().ToArray();
        if (all.Length == 0) return null;
        // у самой Клавы законно несколько голосов (большой и лёгкий) — «основной голос» у неё не выбираем, иначе эхо
        // одного из них перестаёт узнаваться
        var m = name == "Клава" ? all : Majority(all);
        if (m.Length < all.Length) Log.Write($"профили: {name} — из {all.Length} записей в расчёт взято {m.Length}, остальные далеки от основного голоса");
        lock (_cache) _cache[name] = m;
        return m;
    }

    private void Invalidate(string name)
    {
        lock (_cache) _cache.Remove(name);
        lock (_decisionMeans) _decisionMeans.Remove(Sanitize(name));
    }

    /// <summary>Отпечаток → пространство, в котором решается «кто говорит» (обученный слух или как есть).</summary>
    private float[] Space(float[] e) => _ear?.Apply(e) ?? e;

    /// <summary>Центр профиля в пространстве решения (по отпечаткам его записей).</summary>
    private float[]? DecisionMean(string name)
    {
        var key = Sanitize(name);
        lock (_decisionMeans) if (_decisionMeans.TryGetValue(key, out var c)) return c;
        if (ProfileMatrix(name) is not { Length: > 0 } m) return null;
        var mean = Mean(m.Select(Space).ToArray());
        lock (_decisionMeans) _decisionMeans[key] = mean;
        return mean;
    }

    /// <summary>Близость к человеку: к центру отпечатков его записей (по замеру ошибок меньше, чем «лучшие окна»).</summary>
    private static double Score(float[] e, float[][] m) => Dot(e, Mean(m));

    /// <summary>Лучший чужой профиль для отпечатка (кроме name): имя и близость.</summary>
    private (string Name, double Score) BestOther(float[] e, string? except)
    {
        var best = ("", -1.0);
        foreach (var n in Names())
        {
            if (n == except || ProfileMatrix(n) is not { } m) continue;
            var s = Score(e, m);
            if (s > best.Item2) best = (n, s);
        }
        return best;
    }

    // ---------------- проверка записи перед профилем ----------------

    /// <summary>
    /// Годится ли запись в профиль name: речи не меньше minSpeech, голос в ней один (окна похожи друг на друга),
    /// и если профиль уже есть — запись ближе к нему, чем к любому другому человеку. null — годится, иначе причина.
    /// </summary>
    private string? Check(float[] audio, string name, double minSpeech, bool compareWithOthers)
    {
        var trimmed = TrimSilence(audio);
        var speech = trimmed.Length / (double)Rate;
        if (speech < minSpeech) return $"мало речи ({speech:F1} с, нужно от {minSpeech:F1} с)";
        var wins = WindowEmbeddings(trimmed);
        if (wins.Count >= 3)
        {
            var arr = wins.ToArray();
            var med = Medoid(arr);
            var same = arr.Count(x => Dot(arr[med], x) >= WindowSim);
            if (same < arr.Length * 0.7) return "в записи несколько голосов или шум — говори один, без музыки и телевизора";
        }
        if (!compareWithOthers || name == "Клава") return null;
        var e = EmbedAudio(trimmed);
        var own = ProfileMatrix(name) is { } m && Samples(name).Count >= 2 ? Score(e, m) : double.NaN;
        var (other, os) = BestOther(e, name);
        if (other.Length > 0 && os >= Threshold && (double.IsNaN(own) || os > own + 0.03))
            return $"голос похож на «{other}» ({os:F2}{(double.IsNaN(own) ? "" : $", на «{name}» — {own:F2}")}) — не добавляю, чтобы не спутать";
        return null;
    }

    private int AddSample(string name, byte[] wav, bool reset, bool auto = false)
    {
        var d = ProfileDir(name);
        if (reset && Directory.Exists(d))
        {
            // «записать заново» — старые записи не пропадают, а уезжают в карантин
            foreach (var f in Directory.GetFiles(d, "*.wav")) MoveToQuarantine(name, f);
        }
        Directory.CreateDirectory(d);
        File.WriteAllBytes(Path.Combine(d, $"{(auto ? AutoPrefix : "")}{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}.wav"), wav);
        var files = Samples(name);
        var autos = files.Where(IsAuto).ToList();
        foreach (var old in autos.Take(Math.Max(0, autos.Count - MaxAuto))) MoveToQuarantine(name, old);
        var anchors = files.Where(f => !IsAuto(f)).ToList();
        foreach (var old in anchors.Take(Math.Max(0, anchors.Count - MaxAnchors))) MoveToQuarantine(name, old);
        File.WriteAllBytes(Path.Combine(_store, $"{Sanitize(name)}.wav"), wav);
        Invalidate(name);
        return Samples(name).Count;
    }

    /// <summary>Запись профиля — в карантин (вместе с кэшами окон): не удаляется, вернуть можно руками.</summary>
    private void MoveToQuarantine(string name, string wav)
    {
        var q = Path.Combine(_store, QuarantineDir, Sanitize(name));
        Directory.CreateDirectory(q);
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(wav)!, Path.GetFileNameWithoutExtension(wav) + ".*"))
        {
            var dst = Path.Combine(q, Path.GetFileName(f));
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(f, dst);
        }
        Invalidate(name);
    }

    // ---------------- операции ----------------

    public List<SpeakerProfile> List() => Names().Select(n =>
    {
        var files = Samples(n);
        // записи — WAV 16 кГц 16 бит моно: длительность по размеру, без чтения
        var minutes = files.Sum(f => Math.Max(0, new FileInfo(f).Length - 44) / (double)(Rate * 2)) / 60;
        return new SpeakerProfile(n, files.Count > 0 ? files.Max(File.GetLastWriteTime) : DateTime.MinValue, files.Count, minutes);
    }).ToList();

    /// <summary>Записать в профиль: append=false — начать профиль заново (старые записи — в карантин).</summary>
    public bool Enroll(string name, byte[] wav, bool append)
    {
        var audio = Lite.ReadWav16k(wav);
        if (Check(audio, name, 1.2, compareWithOthers: append) is { } why)
        {
            LastReason = why;
            Log.Write($"профили: запись в «{name}» не принята — {why}");
            return false;
        }
        var n = AddSample(name, wav, reset: !append);
        ProfileMatrix(name);
        LastReason = "";
        Log.Write($"профили: enroll{(append ? "+" : "")} {name} (записей {n})");
        return true;
    }

    /// <summary>
    /// Кто говорит: близость фразы к центру каждого профиля в пространстве решения (после тренировки — обученный слух)
    /// и порог по длине речи (короткие фразы — свой, мягче).
    /// </summary>
    public SpeakerMatch Identify(byte[] wav)
    {
        var audio = Lite.ReadWav16k(wav);
        var speech = TrimSilence(audio).Length / (double)Rate;
        var e = Space(EmbedAudio(audio));
        var scores = new Dictionary<string, double>();
        foreach (var n in Names())
            if (DecisionMean(n) is { } m) scores[n] = Math.Round(Dot(e, m), 4);
        if (scores.Count == 0) return new SpeakerMatch(null, 0, null, scores);
        var best = scores.MaxBy(kv => kv.Value);
        var thr = speech < 1.2 ? ShortThreshold : DecisionThreshold;
        return new SpeakerMatch(best.Value >= thr ? best.Key : null, best.Value, best.Key, scores);
    }

    /// <summary>Ошибка опознания: у wrong ближайшая к фразе запись уходит в карантин, фраза — к correct.</summary>
    public bool PenalizeAndCorrect(string wrong, string correct, byte[] wav)
    {
        var audio = Lite.ReadWav16k(wav);
        var e = EmbedAudio(audio);
        if (wrong.Length > 0 && wrong != correct)
        {
            var files = Samples(wrong);
            if (files.Count > 1)
            {
                var best = files.Select(f => (f, s: SampleEmbedding(f) is { } v ? Dot(v, e) : -1)).MaxBy(x => x.s);
                if (best.s > -1)
                {
                    MoveToQuarantine(wrong, best.f);
                    Log.Write($"профили: штраф — у {wrong} запись {Path.GetFileName(best.f)} в карантин (близость {best.s:F2})");
                }
            }
        }
        // друг сам сказал, чей это голос, — сравнение с другими не нужно, но голос в записи должен быть один
        if (Check(audio, correct, 1.0, compareWithOthers: false) is { } why)
        {
            LastReason = why;
            Log.Write($"профили: фраза в {correct} не добавлена — {why}");
            return true;
        }
        var n = AddSample(correct, wav, reset: false);
        Log.Write($"профили: фраза ушла в {correct} (записей {n})");
        return true;
    }

    /// <summary>Доучивание на лету: только уверенно его, заметно дальше от всех остальных, один голос и ≥2 с речи.</summary>
    public bool Adapt(string name, byte[] wav)
    {
        if (name.Length == 0 || name == "Клава") return false;
        if (ProfileMatrix(name) is not { } m) return false;
        var audio = Lite.ReadWav16k(wav);
        if (TrimSilence(audio).Length < Rate * 2) return false;
        var e = EmbedAudio(audio);
        var own = Score(e, m);
        var (otherName, other) = BestOther(e, name);
        if (own < Threshold + 0.1 || own - other < 0.15) return false;
        if (Check(audio, name, 2.0, compareWithOthers: false) is not null) return false;
        var cnt = AddSample(name, wav, reset: false, auto: true);
        Log.Write($"профили: {name} доучен (свой {own:F2}, ближайший чужой {otherName} {other:F2}, записей {cnt})");
        return true;
    }

    /// <summary>Удалить профиль: его записи уезжают в карантин (вернуть можно руками).</summary>
    public bool Remove(string name)
    {
        var d = ProfileDir(name);
        var removed = false;
        if (Directory.Exists(d))
        {
            foreach (var f in Directory.GetFiles(d, "*.wav")) MoveToQuarantine(name, f);
            foreach (var f in Directory.GetFiles(d)) File.Delete(f);   // остались только кэши окон
            Directory.Delete(d);
            removed = true;
        }
        foreach (var p in Directory.GetFiles(_store, Sanitize(name) + ".*")) { File.Delete(p); removed = true; }
        Invalidate(name);
        return removed;
    }

    public bool Rename(string from, string to)
    {
        var o = Sanitize(from);
        var n = Sanitize(to);
        if (o == n) return false;
        if (Directory.Exists(Path.Combine(_store, o)))
        {
            if (Directory.Exists(Path.Combine(_store, n)))
            {
                // слить с существующим: записи переезжают к новому имени
                foreach (var f in Directory.GetFiles(Path.Combine(_store, o))) File.Move(f, Path.Combine(_store, n, Path.GetFileName(f)), overwrite: true);
                Directory.Delete(Path.Combine(_store, o));
            }
            else Directory.Move(Path.Combine(_store, o), Path.Combine(_store, n));
        }
        foreach (var p in Directory.GetFiles(_store, o + ".*"))
        {
            var dst = Path.Combine(_store, n + Path.GetFileName(p)[o.Length..]);
            File.Move(p, dst, overwrite: true);
        }
        Invalidate(from);
        Invalidate(to);
        return true;
    }

    public byte[]? Sample(string name)
    {
        var p = Path.Combine(_store, $"{Sanitize(name)}.wav");
        if (File.Exists(p)) return File.ReadAllBytes(p);
        var files = Samples(name);
        return files.Count > 0 ? File.ReadAllBytes(files[^1]) : null;
    }

    /// <summary>Близость двух записей одним отпечатком (для подбора голоса, ближайшего к эталону).</summary>
    public double Similarity(byte[] wavA, byte[] wavB) => Dot(Embed(wavA), Embed(wavB));

    /// <summary>
    /// Проверка профилей: каждая запись против своего профиля без неё и против остальных. Сомнительная — если она
    /// ближе к другому человеку или слишком далека от своих (чужой голос, эхо Клавы, телевизор).
    /// </summary>
    public List<SuspectSample> Audit()
    {
        var res = new List<SuspectSample>();
        var names = Names();
        foreach (var n in names.Where(x => x != "Клава"))
        {
            var files = Samples(n);
            if (files.Count < 2) continue;
            foreach (var f in files)
            {
                float[] e;
                try { e = Embed(File.ReadAllBytes(f)); }
                catch (Exception ex) when (ex is InvalidDataException or IOException) { continue; }
                var rest = files.Where(x => x != f).Select(SampleEmbedding).OfType<float[]>().ToArray();
                if (rest.Length == 0) continue;
                var own = Score(e, Majority(rest));
                var (other, os) = BestOther(e, n);
                if (os > own + 0.02 || own < Threshold - 0.15)
                    res.Add(new SuspectSample(n, f, Math.Round(own, 2), other, Math.Round(os, 2)));
            }
        }
        return res;
    }

    public void Quarantine(SuspectSample s)
    {
        if (File.Exists(s.File)) MoveToQuarantine(s.Name, s.File);
        Log.Write($"профили: {s.Name}/{Path.GetFileName(s.File)} в карантин (своим {s.Own:F2}, на «{s.OtherName}» {s.Other:F2})");
    }

    /// <summary>
    /// Долгая запись профиля (до 15 минут разговора): речь режется на куски по паузам (длинные — по 4 с), у каждого
    /// куска — отпечаток. Голос профиля — самый «густой» (у кого больше всего похожих кусков) или, если профиль уже есть
    /// и не пишется заново, — сам профиль. Оставляются куски этого голоса, не похожие на других людей и на эхо Клавы;
    /// из них в профиль уходят до 150 штук (10 минут чтения — это 150 кусков по 4 с), равномерно по времени (разные интонации и громкость).
    /// </summary>
    public LongEnrollResult LongEnroll(string name, float[] audio, bool replace, IProgress<string>? progress)
    {
        var chunks = SplitSpeech(audio);
        var speech = chunks.Sum(c => c.Length) / (double)Rate;
        if (chunks.Count < 3) return new LongEnrollResult(false, 0, chunks.Count, speech, $"речи почти нет ({speech:F0} с) — говори ближе к микрофону");
        var emb = new List<float[]>();
        for (var i = 0; i < chunks.Count; i++)
        {
            if (i % 10 == 0) progress?.Report($"Разбираю речь: кусок {i + 1} из {chunks.Count}…");
            try { emb.Add(EmbedAudio(chunks[i])); }
            catch (InvalidDataException) { emb.Add([]); }
        }
        var valid = Enumerable.Range(0, chunks.Count).Where(i => emb[i].Length > 0).ToList();
        // опора: существующий профиль (если добавляем) или «густой» центр записи
        float[] center;
        if (!replace && ProfileMatrix(name) is { Length: > 0 } pm)
            center = Mean(pm);
        else
        {
            var dens = valid.Select(i => (i, n: valid.Count(j => Dot(emb[i], emb[j]) >= Threshold))).MaxBy(x => x.n);
            center = Mean(valid.Where(j => Dot(emb[dens.i], emb[j]) >= Threshold).Select(j => emb[j]).ToArray());
        }
        // уточнить центр по своим кускам и собрать своих
        for (var it = 0; it < 2; it++)
        {
            var near = valid.Where(i => Dot(emb[i], center) >= Threshold).Select(i => emb[i]).ToArray();
            if (near.Length > 0) center = Mean(near);
        }
        int foreign = 0, lookalike = 0;
        var keep = new List<int>();
        foreach (var i in valid)
        {
            var own = Dot(emb[i], center);
            if (own < Threshold + 0.05) { foreign++; continue; }
            // сравнение с ДРУГИМИ людьми: свой старый профиль (даже если пишется заново) — не «другой»
            var (other, os) = BestOther(emb[i], name);
            if (other.Length > 0 && os > own - 0.05) { lookalike++; continue; }
            keep.Add(i);
        }
        if (keep.Count < 3)
            return new LongEnrollResult(false, keep.Count, chunks.Count, speech,
                $"своего голоса набралось мало ({keep.Count} кусков из {chunks.Count}): чужие голоса {foreign}, похожие на других {lookalike}. Говори один, без музыки.");
        // до MaxLongTake кусков, равномерно по времени
        var take = keep.Count <= MaxLongTake ? keep : Enumerable.Range(0, MaxLongTake).Select(k => keep[k * keep.Count / MaxLongTake]).ToList();
        progress?.Report($"Записываю {take.Count} кусков в профиль «{name}»…");
        var d = ProfileDir(name);
        if (replace && Directory.Exists(d)) foreach (var f in Directory.GetFiles(d, "*.wav")) MoveToQuarantine(name, f);
        Directory.CreateDirectory(d);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var k = 0; k < take.Count; k++)
            File.WriteAllBytes(Path.Combine(d, $"{stamp + k}.wav"), Lite.ToWav(chunks[take[k]], Rate));
        var anchors = Samples(name).Where(f => !IsAuto(f)).ToList();
        foreach (var old in anchors.Take(Math.Max(0, anchors.Count - MaxAnchors))) MoveToQuarantine(name, old);
        File.WriteAllBytes(Path.Combine(_store, $"{Sanitize(name)}.wav"), Lite.ToWav(chunks[take[take.Count / 2]], Rate));
        Invalidate(name);
        ProfileMatrix(name);
        var report = $"Речи {speech / 60:F1} мин, кусков {chunks.Count}: взяла {take.Count}" +
                     $"{(keep.Count > take.Count ? $" (из {keep.Count} годных)" : "")}, выбросила чужих голосов {foreign}, похожих на других {lookalike}. " +
                     $"В профиле «{name}» теперь записей {Samples(name).Count}.";
        Log.Write($"профили: долгая запись «{name}» — {report}");
        return new LongEnrollResult(true, take.Count, chunks.Count, speech, report);
    }

    // ---------------- обучение слуха ----------------

    /// <summary>Окна по 1,5 с одной записи (для обучения): считаются один раз и лежат рядом (&lt;запись&gt;.&lt;модель&gt;.win).</summary>
    private float[][]? SampleWindows(string f)
    {
        var cf = Path.ChangeExtension(f, $".{Model}.win");
        try
        {
            if (File.Exists(cf) && File.GetLastWriteTimeUtc(cf) >= File.GetLastWriteTimeUtc(f))
            {
                var b = File.ReadAllBytes(cf);
                if (b.Length >= 8)
                {
                    var dim = BinaryPrimitives.ReadInt32LittleEndian(b);
                    var count = (b.Length - 4) / 4 / Math.Max(1, dim);
                    if (dim > 0 && 4 + count * dim * 4 == b.Length)
                    {
                        var res = new float[count][];
                        for (var i = 0; i < count; i++)
                        {
                            res[i] = new float[dim];
                            Buffer.BlockCopy(b, 4 + i * dim * 4, res[i], 0, dim * 4);
                        }
                        return res;
                    }
                }
            }
        }
        catch (IOException) { }
        try
        {
            var w = WindowEmbeddings(Lite.ReadWav16k(File.ReadAllBytes(f))).ToArray();
            if (w.Length == 0) return null;
            var dim = w[0].Length;
            var buf = new byte[4 + w.Length * dim * 4];
            BinaryPrimitives.WriteInt32LittleEndian(buf, dim);
            for (var i = 0; i < w.Length; i++) Buffer.BlockCopy(w[i], 0, buf, 4 + i * dim * 4, dim * 4);
            File.WriteAllBytes(cf, buf);
            return w;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private sealed record EarEval(double EerShort, double ThrShort, double EerLong, double ThrLong, double Top1, Dictionary<int, (int Ok, int All)> PerPerson);

    /// <summary>
    /// Обучить слух на записях всех профилей и честно проверить: записи делятся на 5 частей, каждая часть проверяется
    /// слухом и профилями, собранными БЕЗ неё. Меряются ошибки на коротких кусках (окна 1,5 с) и на целых записях,
    /// и точность «кто из комнаты». Обученный слух включается, только если с ним ошибок заметно меньше, чем без него;
    /// пороги берутся из той же проверки (равные ошибки). Итог — в _слух.&lt;модель&gt;.json (+ .bin, если слух включён).
    /// </summary>
    public (bool Ok, string Report) TrainEar(IProgress<string>? progress)
    {
        var names = Names();
        var files = new List<(int Person, string Path)>();
        for (var p = 0; p < names.Count; p++)
            foreach (var f in Samples(names[p])) files.Add((p, f));
        var withTwo = names.Where((_, p) => files.Count(x => x.Person == p) >= 2).Count();
        if (withTwo < 2) return (false, "Мало записей: нужно хотя бы два человека, у каждого минимум две записи. Сначала потренируй голоса чтением.");

        var fileEmb = new float[files.Count][];
        var winEmb = new float[files.Count][][];
        for (var i = 0; i < files.Count; i++)
        {
            if (i % 20 == 0) progress?.Report($"Слушаю записи профилей: {i + 1} из {files.Count}…");
            fileEmb[i] = SampleEmbedding(files[i].Path) ?? [];
            winEmb[i] = SampleWindows(files[i].Path) ?? [];
        }
        var ok = Enumerable.Range(0, files.Count).Where(i => fileEmb[i].Length > 0 && winEmb[i].Length > 0).ToList();
        progress?.Report("Проверяю, как слух различает людей без обучения…");
        var raw = Evaluate(files, fileEmb, winEmb, ok, names.Count, useEar: false);
        progress?.Report("Обучаю слух и проверяю его на отложенных записях…");
        var trained = Evaluate(files, fileEmb, winEmb, ok, names.Count, useEar: true);
        if (raw is null) return (false, "Не получилось проверить профили — мало годных записей.");

        var rawAvg = (raw.EerShort + raw.EerLong) / 2;
        var trainedAvg = trained is null ? 1 : (trained.EerShort + trained.EerLong) / 2;
        var useEar = trained is not null && trainedAvg < rawAvg - 0.005 && trained.Top1 >= raw.Top1 - 0.005;
        var chosen = useEar ? trained! : raw;
        TrainedEar? ear = null;
        if (useEar)
        {
            var x = ok.SelectMany(i => winEmb[i]).ToList();
            var y = ok.SelectMany(i => winEmb[i].Select(_ => files[i].Person)).ToList();
            ear = TrainedEar.Train(x, y);
            if (ear is null) { useEar = false; chosen = raw; }
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Слух обучен {DateTime.Now:dd.MM HH:mm} на {ok.Count} записях ({ok.Sum(i => winEmb[i].Length)} кусков по 1,5 с), людей: {names.Count}.");
        sb.AppendLine($"Ошибки без обучения: короткие фразы {raw.EerShort * 100:F1} %, длинные {raw.EerLong * 100:F1} %, «кто из комнаты» верно {raw.Top1 * 100:F0} %.");
        if (trained is not null)
            sb.AppendLine($"С обученным слухом: короткие {trained.EerShort * 100:F1} %, длинные {trained.EerLong * 100:F1} %, «кто из комнаты» верно {trained.Top1 * 100:F0} %.");
        sb.AppendLine(useEar ? "→ Включила обученный слух: с ним ошибок меньше." : "→ Обученный слух не лучше — опознаю по отпечаткам как есть, но с новыми порогами.");
        sb.AppendLine($"Порог «свой»: {chosen.ThrLong:F2}, для коротких фраз {chosen.ThrShort:F2}.");
        foreach (var (p, (right, all)) in chosen.PerPerson.OrderBy(kv => names[kv.Key], StringComparer.CurrentCulture))
            sb.AppendLine($"  {names[p]}: узнаю {right} из {all} кусков ({(all == 0 ? 0 : right * 100 / all)} %)");
        var report = sb.ToString().TrimEnd();

        _ear = useEar ? ear : null;
        DecisionThreshold = chosen.ThrLong;
        ShortThreshold = Math.Min(chosen.ThrShort, chosen.ThrLong);
        EarReport = report;
        lock (_decisionMeans) _decisionMeans.Clear();
        if (_ear is not null) _ear.Save(EarFile);
        else if (File.Exists(EarFile)) File.Delete(EarFile);
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["decision"] = Math.Round(DecisionThreshold, 4), ["short"] = Math.Round(ShortThreshold, 4), ["useEar"] = _ear is not null,
            ["eerRaw"] = Math.Round(rawAvg, 4), ["eerTrained"] = Math.Round(trainedAvg, 4), ["trainedAt"] = DateTime.Now.ToString("s"),
            ["files"] = ok.Count, ["people"] = names.Count, ["report"] = report,
        };
        File.WriteAllText(CalibFile, json.ToJsonString(new System.Text.Json.JsonSerializerOptions
            { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        Log.Write($"профили: {report.Replace(Environment.NewLine, " | ")}");
        return (true, report);
    }

    /// <summary>Проверка в 5 частей (см. <see cref="TrainEar"/>): null — не на чем проверять.</summary>
    private static EarEval? Evaluate(List<(int Person, string Path)> files, float[][] fileEmb, float[][][] winEmb, List<int> ok, int people, bool useEar)
    {
        const int folds = 5;
        var ownS = new List<double>(); var impS = new List<double>();
        var ownL = new List<double>(); var impL = new List<double>();
        int top = 0, topAll = 0;
        var per = new Dictionary<int, (int Ok, int All)>();
        for (var k = 0; k < folds; k++)
        {
            var test = ok.Where((_, idx) => idx % folds == k).ToList();
            var train = ok.Where((_, idx) => idx % folds != k).ToList();
            TrainedEar? ear = null;
            if (useEar)
            {
                ear = TrainedEar.Train(train.SelectMany(i => winEmb[i]).ToList(), train.SelectMany(i => winEmb[i].Select(_ => files[i].Person)).ToList());
                if (ear is null) return null;
            }
            float[] S(float[] e) => ear?.Apply(e) ?? e;
            var centers = new Dictionary<int, float[]>();
            for (var p = 0; p < people; p++)
            {
                var mine = train.Where(i => files[i].Person == p).Select(i => S(fileEmb[i])).ToArray();
                if (mine.Length > 0) centers[p] = Mean(mine);
            }
            foreach (var t in test)
            {
                var me = files[t].Person;
                if (!centers.ContainsKey(me)) continue;   // у человека все записи в этой части — сравнить не с чем
                void Pair(float[] e, List<double> own, List<double> imp, bool countTop)
                {
                    var z = S(e);
                    var o = Dot(z, centers[me]);
                    own.Add(o);
                    var bestP = me; var bestS = o;
                    foreach (var (p, c) in centers)
                    {
                        if (p == me) continue;
                        var s = Dot(z, c);
                        imp.Add(s);
                        if (s > bestS) { bestS = s; bestP = p; }
                    }
                    if (!countTop) return;
                    topAll++;
                    var right = bestP == me;
                    if (right) top++;
                    var cur = per.GetValueOrDefault(me);
                    per[me] = (cur.Ok + (right ? 1 : 0), cur.All + 1);
                }
                foreach (var w in winEmb[t]) Pair(w, ownS, impS, countTop: true);
                Pair(fileEmb[t], ownL, impL, countTop: false);
            }
        }
        if (ownS.Count < 5 || impS.Count < 5) return null;
        var (eS, tS) = TrainedEar.Eer(ownS, impS);
        var (eL, tL) = ownL.Count >= 3 && impL.Count >= 3 ? TrainedEar.Eer(ownL, impL) : (eS, tS);
        return new EarEval(eS, tS, eL, tL, topAll == 0 ? 0 : top / (double)topAll, per);
    }

    private static float[] Mean(IReadOnlyList<float[]> v)
    {
        var m = new float[v[0].Length];
        foreach (var x in v) for (int i = 0; i < m.Length; i++) m[i] += x[i];
        var norm = MathF.Sqrt(m.Sum(x => x * x));
        if (norm > 1e-9f) for (int i = 0; i < m.Length; i++) m[i] /= norm;
        return m;
    }

    /// <summary>Речь по паузам: кадры 30 мс громче фона; паузы короче 0,3 с не рвут; куски 1,5–4 с.</summary>
    private static List<float[]> SplitSpeech(float[] a)
    {
        const int frame = 480;
        var rms = FrameRms(a, frame);
        if (rms.Length == 0) return [];
        var sorted = rms.Order().ToArray();
        var floor = sorted[(int)(sorted.Length * 0.2)];
        var thr = Math.Max(floor * 3, 0.006);
        var res = new List<float[]>();
        int start = -1, lastLoud = -1;
        void Flush(int end)
        {
            if (start < 0) return;
            var from = Math.Max(0, start - 5) * frame;
            var to = Math.Min(rms.Length, end + 5) * frame;
            for (var s = from; s < to; s += 4 * Rate)
            {
                var len = Math.Min(4 * Rate, to - s);
                if (len >= (int)(1.5 * Rate)) res.Add(a[s..(s + len)]);
            }
            start = -1;
        }
        for (var i = 0; i < rms.Length; i++)
        {
            if (rms[i] > thr) { if (start < 0) start = i; lastLoud = i; }
            else if (start >= 0 && i - lastLoud > 10) Flush(lastLoud);
        }
        Flush(lastLoud);
        return res;
    }

    /// <summary>Проверка порога на своих же записях: каждая запись против профиля без неё и против чужих профилей.</summary>
    public IEnumerable<string> Calibrate()
    {
        var names = Names();
        var others = new List<double>();
        var owns = new List<double>();
        foreach (var n in names)
        {
            var files = Samples(n);
            foreach (var f in files)
            {
                float[] e;
                try { e = Embed(File.ReadAllBytes(f)); }
                catch (Exception ex) when (ex is InvalidDataException or IOException) { continue; }
                var rest = files.Where(x => x != f).Select(SampleEmbedding).OfType<float[]>().ToArray();
                if (rest.Length > 0) owns.Add(Score(e, Majority(rest)));
                foreach (var o in names.Where(x => x != n))
                    if (ProfileMatrix(o) is { } m) others.Add(Score(e, m));
            }
        }
        yield return $"профилей {names.Count}; свой-свой (без этой записи): {Stat(owns)}";
        yield return $"свой-чужой: {Stat(others)}";
        yield return $"порог {Threshold:F2}: своих ниже порога {owns.Count(x => x < Threshold)}/{owns.Count}, чужих выше {others.Count(x => x >= Threshold)}/{others.Count}";
    }

    private static string Stat(List<double> v) => v.Count == 0 ? "нет данных"
        : $"мин {v.Min():F2}, медиана {v.Order().ElementAt(v.Count / 2):F2}, макс {v.Max():F2} (n={v.Count})";

    public void Dispose() => _ex.Dispose();
}

/// <summary>
/// Текст для лёгкого голоса: числа — словами, латиница — кириллицей, мусор (смайлики, разметка) — прочь.
/// Большой голос делает это в Python (stress.py), здесь — своё, без зависимостей.
/// </summary>
internal static partial class LiteText
{
    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github"] = "гитхаб", ["git"] = "гит", ["minecraft"] = "майнкрафт", ["windows"] = "виндовс", ["python"] = "питон",
        ["paint"] = "пэйнт", ["claude"] = "клод", ["google"] = "гугл", ["youtube"] = "ютуб", ["steam"] = "стим",
        ["discord"] = "дискорд", ["telegram"] = "телеграм", ["ok"] = "окей", ["okay"] = "окей", ["deepseek"] = "дипсик",
        ["gemini"] = "джемини", ["qwen"] = "квен", ["chrome"] = "хром", ["edge"] = "эдж", ["word"] = "ворд", ["excel"] = "эксель",
        ["notepad"] = "блокнот", ["explorer"] = "эксплорер", ["powershell"] = "пауэршелл", ["terminal"] = "терминал",
        ["vscode"] = "вэ эс код", ["code"] = "код", ["file"] = "файл", ["the"] = "зе", ["hello"] = "хэллоу", ["wow"] = "вау",
        ["lol"] = "лол", ["kek"] = "кек", ["cringe"] = "кринж", ["vibe"] = "вайб", ["antigravity"] = "антигравити",
        ["whisper"] = "виспер", ["llama"] = "лама", ["ai"] = "эй ай", ["api"] = "апи", ["gpu"] = "джи пи ю", ["cpu"] = "си пи ю",
        ["ram"] = "рам", ["usb"] = "ю эс би", ["pc"] = "пи си", ["it"] = "ай ти", ["dotnet"] = "дотнет", ["net"] = "нет",
        ["html"] = "эйч ти эм эл", ["json"] = "джейсон", ["exe"] = "экзешник", ["bat"] = "батник", ["nvidia"] = "энвидиа",
        ["amd"] = "эй эм ди", ["intel"] = "интел", ["linux"] = "линукс", ["mac"] = "мак", ["iphone"] = "айфон", ["android"] = "андроид",
    };

    private static readonly Dictionary<char, string> Letters = new()
    {
        ['a'] = "эй", ['b'] = "би", ['c'] = "си", ['d'] = "ди", ['e'] = "и", ['f'] = "эф", ['g'] = "джи", ['h'] = "эйч", ['i'] = "ай",
        ['j'] = "джей", ['k'] = "кей", ['l'] = "эл", ['m'] = "эм", ['n'] = "эн", ['o'] = "оу", ['p'] = "пи", ['q'] = "кью", ['r'] = "ар",
        ['s'] = "эс", ['t'] = "ти", ['u'] = "ю", ['v'] = "ви", ['w'] = "дабл ю", ['x'] = "экс", ['y'] = "уай", ['z'] = "зед",
    };

    [GeneratedRegex(@"\[[^\]]*\]|\*+|_{2,}|`+|#+|<[^>]*>")] private static partial Regex MarkupRx();
    [GeneratedRegex(@"https?://\S+")] private static partial Regex UrlRx();
    [GeneratedRegex(@"\b(\d{1,4})(?:-?[мй])?\s+(году|год)\b")] private static partial Regex YearRx();
    [GeneratedRegex(@"(\d+):(\d{2})")] private static partial Regex TimeRx();
    [GeneratedRegex(@"(\d+)\s*%")] private static partial Regex PercentRx();
    [GeneratedRegex(@"\d+(?:[.,]\d+)?")] private static partial Regex NumRx();
    [GeneratedRegex(@"[A-Za-z]+")] private static partial Regex LatinRx();
    [GeneratedRegex(@"[^\p{L}\p{M}\p{N}+\s.,!?;:\-—–()«»""'…]")] private static partial Regex JunkRx();
    [GeneratedRegex(@"\s{2,}")] private static partial Regex SpacesRx();

    public static string Normalize(string text)
    {
        var t = MarkupRx().Replace(text, " ");
        t = UrlRx().Replace(t, " ссылка ");
        t = YearRx().Replace(t, m => $"{Ordinal(int.Parse(m.Groups[1].Value), m.Groups[2].Value == "году")} {m.Groups[2].Value}");
        t = TimeRx().Replace(t, m => $"{Num(long.Parse(m.Groups[1].Value))} {(m.Groups[2].Value == "00" ? "ноль ноль" : Num(long.Parse(m.Groups[2].Value)))}");
        t = PercentRx().Replace(t, m => $"{m.Groups[1].Value} {Plural(long.Parse(m.Groups[1].Value), "процент", "процента", "процентов")}");
        t = NumRx().Replace(t, m => Number(m.Value));
        t = LatinRx().Replace(t, m => Latin(m.Value));
        t = t.Replace('—', ',').Replace('–', ',');
        t = LiteStress.StressSentence(t);
        t = LiteStress.FixYo(t);
        t = JunkRx().Replace(t, " ");
        return SpacesRx().Replace(t, " ").Trim();
    }

    private static string Number(string s)
    {
        var sep = s.IndexOfAny(['.', ',']);
        if (sep < 0) return s.Length > 15 ? string.Join(" ", s.Select(c => Num(c - '0'))) : Num(long.Parse(s, CultureInfo.InvariantCulture));
        var a = s[..sep];
        var b = s[(sep + 1)..];
        var whole = a.Length > 15 ? string.Join(" ", a.Select(c => Num(c - '0'))) : Num(long.Parse(a, CultureInfo.InvariantCulture));
        // «3.6» в речи о версиях — «три точка шесть», «2,5» — «два запятая пять»; ведущие нули читаются
        var frac = b.StartsWith('0') || b.Length > 15 ? string.Join(" ", b.Select(c => Num(c - '0'))) : Num(long.Parse(b, CultureInfo.InvariantCulture));
        return $"{whole} {(s[sep] == '.' ? "точка" : "запятая")} {frac}";
    }

    private static string Latin(string w)
    {
        if (Words.TryGetValue(w, out var known)) return known;
        // аббревиатуры читаем по буквам
        if (w.Length <= 4 && w.All(char.IsUpper)) return string.Join(" ", w.ToLowerInvariant().Select(c => Letters[c]));
        return Translit(w.ToLowerInvariant());
    }

    /// <summary>Грубая транслитерация по звучанию — лучше, чем молча проглотить слово.</summary>
    private static string Translit(string w)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < w.Length; i++)
        {
            var c = w[i];
            var next = i + 1 < w.Length ? w[i + 1] : '\0';
            string? two = next == '\0' ? null : (c.ToString() + next) switch
            {
                "sh" => "ш", "ch" => "ч", "th" => "т", "ph" => "ф", "ck" => "к", "ee" => "и", "oo" => "у", "ou" => "ау",
                "ai" => "эй", "ay" => "эй", "ea" => "и", "kh" => "х", "zh" => "ж", "ts" => "ц", "ya" => "я", "yu" => "ю", "yo" => "ё",
                "qu" => "кв", "wh" => "в", "ng" => "нг",
                _ => null,
            };
            if (two is not null) { sb.Append(two); i++; continue; }
            sb.Append(c switch
            {
                'a' => "а", 'b' => "б", 'c' => next is 'e' or 'i' or 'y' ? "с" : "к", 'd' => "д", 'e' => i == w.Length - 1 && w.Length > 2 ? "" : "е",
                'f' => "ф", 'g' => "г", 'h' => "х", 'i' => "и", 'j' => "дж", 'k' => "к", 'l' => "л", 'm' => "м", 'n' => "н", 'o' => "о",
                'p' => "п", 'q' => "к", 'r' => "р", 's' => "с", 't' => "т", 'u' => "у", 'v' => "в", 'w' => "в", 'x' => "кс",
                'y' => i == 0 ? "й" : "и", 'z' => "з",
                _ => "",
            });
        }
        return sb.ToString();
    }

    private static readonly string[] Ones = ["ноль", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять",
        "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать"];
    private static readonly string[] Tens = ["", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто"];
    private static readonly string[] Hundreds = ["", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот"];

    private static string Plural(long n, string one, string few, string many)
    {
        n = Math.Abs(n) % 100;
        if (n is >= 11 and <= 19) return many;
        return (n % 10) switch { 1 => one, 2 or 3 or 4 => few, _ => many };
    }

    /// <summary>Тройка цифр словами; feminine — для тысяч («одна тысяча», «две тысячи»).</summary>
    private static string Triple(int n, bool feminine)
    {
        var parts = new List<string>();
        if (n >= 100) { parts.Add(Hundreds[n / 100]); n %= 100; }
        if (n >= 20) { parts.Add(Tens[n / 10]); n %= 10; }
        if (n > 0)
            parts.Add(feminine && n == 1 ? "одна" : feminine && n == 2 ? "две" : Ones[n]);
        return string.Join(" ", parts);
    }

    /// <summary>Порядковые основы: последнее слово числа → основа и ударное ли окончание («второй», но «пятый»).</summary>
    private static readonly Dictionary<string, (string Stem, bool Stressed)> OrdinalStems = new()
    {
        ["один"] = ("перв", false), ["два"] = ("втор", true), ["четыре"] = ("четвёрт", false), ["пять"] = ("пят", false),
        ["шесть"] = ("шест", true), ["семь"] = ("седьм", true), ["восемь"] = ("восьм", true), ["девять"] = ("девят", false),
        ["десять"] = ("десят", false), ["одиннадцать"] = ("одиннадцат", false), ["двенадцать"] = ("двенадцат", false),
        ["тринадцать"] = ("тринадцат", false), ["четырнадцать"] = ("четырнадцат", false), ["пятнадцать"] = ("пятнадцат", false),
        ["шестнадцать"] = ("шестнадцат", false), ["семнадцать"] = ("семнадцат", false), ["восемнадцать"] = ("восемнадцат", false),
        ["девятнадцать"] = ("девятнадцат", false), ["двадцать"] = ("двадцат", false), ["тридцать"] = ("тридцат", false),
        ["сорок"] = ("сороков", true), ["пятьдесят"] = ("пятидесят", false), ["шестьдесят"] = ("шестидесят", false),
        ["семьдесят"] = ("семидесят", false), ["восемьдесят"] = ("восьмидесят", false), ["девяносто"] = ("девяност", false),
        ["сто"] = ("сот", false), ["двести"] = ("двухсот", false), ["триста"] = ("трёхсот", false), ["четыреста"] = ("четырёхсот", false),
        ["пятьсот"] = ("пятисот", false), ["шестьсот"] = ("шестисот", false), ["семьсот"] = ("семисот", false),
        ["восемьсот"] = ("восьмисот", false), ["девятьсот"] = ("девятисот", false),
    };

    /// <summary>Порядковое числительное мужского рода для годов: «две тысячи двадцать шестой» / «в … двадцать шестом».</summary>
    public static string Ordinal(int n, bool prepositional)
    {
        if (n <= 0) return Num(n);
        var end = prepositional ? "ом" : null;
        string Tail(string stem, bool stressed) => stem + (end ?? (stressed ? "ой" : "ый"));
        var thousands = n / 1000;
        var rest = n % 1000;
        if (rest == 0)
        {
            // круглые тысячи: «двухтысячный», «тысячный»
            var pre = thousands switch { 1 => "", 2 => "двух", 3 => "трёх", 4 => "четырёх", 5 => "пяти", _ => Num(thousands) + " " };
            return Tail(pre + "тысячн", false);
        }
        var head = thousands switch { 0 => "", 1 => "тысяча ", _ => Num(thousands * 1000L) + " " };
        var words = Triple(rest, false).Split(' ');
        var last = words[^1];
        string tail = last == "три" ? (prepositional ? "третьем" : "третий")
            : OrdinalStems.TryGetValue(last, out var o) ? Tail(o.Stem, o.Stressed) : last;
        return head + string.Join(" ", words[..^1].Append(tail));
    }

    public static string Num(long n)
    {
        if (n == 0) return "ноль";
        if (n < 0) return "минус " + Num(-n);
        var parts = new List<string>();
        (long div, string one, string few, string many, bool fem)[] scales =
        [
            (1_000_000_000_000, "триллион", "триллиона", "триллионов", false),
            (1_000_000_000, "миллиард", "миллиарда", "миллиардов", false),
            (1_000_000, "миллион", "миллиона", "миллионов", false),
            (1_000, "тысяча", "тысячи", "тысяч", true),
        ];
        foreach (var (div, one, few, many, fem) in scales)
        {
            if (n < div) continue;
            var k = (int)(n / div % 1000);
            n %= div;
            if (k == 0) continue;
            parts.Add(Triple(k, fem));
            parts.Add(Plural(k, one, few, many));
        }
        if (n > 0) parts.Add(Triple((int)n, false));
        return string.Join(" ", parts);
    }
}
