using NAudio.Wave;

namespace KlavaDesk;

/// <summary>
/// Слух на колонки: огибающая громкости того, что играет сам компьютер (видео, игра, её собственный голос), за последние
/// 90 с — по 10 мс. Микрофон услышал фразу — сверяем её огибающую с огибающей колонок за тот же отрезок (со сдвигом до
/// ±0,6 с на задержки): совпала — это говорил компьютер, а не человек в комнате.
///
/// Друг 10.10: Куплинов с Ютуба шёл в журнал словами Димочки («дайте хоть по отелю погулять», подсказки самой игры),
/// её же фраза из колонок возвращалась через 10 с как реплика друга, а «Паша пришёл» из чужих уст возвращало в комнату
/// того, кто на работе. Распознавание голоса тут бессильно: голос из колонок похож на кого угодно. Звук хранится не
/// весь, а только огибающая (100 чисел в секунду) — на 90 с меньше 40 КБ, сверка фразы — доли миллисекунды.
/// </summary>
internal sealed class SpeakerTap : IDisposable
{
    public const int FrameMs = 10;
    private const int Capacity = 90 * 1000 / FrameMs;

    private readonly float[] _env = new float[Capacity];       // log10(rms) кадра
    private readonly long[] _at = new long[Capacity];          // UtcTicks конца кадра
    private long _count;                                        // всего кадров записано (позиция в кольце — _count % Capacity)
    private readonly object _gate = new();
    private DateTime _lastEnd = DateTime.MinValue;
    private WasapiLoopbackCapture? _cap;
    private double _acc;
    private int _accN, _frameLen, _channels, _rate;
    private bool _float;

    public bool Running => _cap is not null;
    public DateTime LastSound { get; private set; } = DateTime.MinValue;

#pragma warning disable CS0618   // нужен весь звук колонок (вместе с её голосом), а не звук одного процесса
    public static SpeakerTap? Start()
    {
        var tap = new SpeakerTap();
        try
        {
            var cap = new WasapiLoopbackCapture();
            tap._channels = cap.WaveFormat.Channels;
            tap._float = cap.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat || cap.WaveFormat.BitsPerSample == 32;
            tap._rate = cap.WaveFormat.SampleRate;
            tap._frameLen = Math.Max(1, cap.WaveFormat.SampleRate * FrameMs / 1000);
            cap.DataAvailable += (_, e) => tap.Feed(e.Buffer, e.BytesRecorded);
            cap.RecordingStopped += (_, e) => { if (e.Exception is not null) Log.Error("слух на колонки остановился", e.Exception); };
            cap.StartRecording();
            tap._cap = cap;
            Log.Write($"слух на колонки: {cap.WaveFormat.SampleRate} Гц, {tap._channels} кан. — звук компьютера не путаю с людьми");
            return tap;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            Log.Error("слух на колонки не запустился", ex);
            tap.Dispose();
            return null;
        }
    }
#pragma warning restore CS0618

    /// <summary>Пакет звука колонок → кадры по 10 мс. Пауза в пакетах (в тишине WASAPI их не шлёт) — тихие кадры.</summary>
    private void Feed(byte[] buf, int bytes)
    {
        var now = DateTime.UtcNow;
        var bytesPerSample = _float ? 4 : 2;
        var samples = bytes / bytesPerSample / Math.Max(1, _channels);
        lock (_gate)
        {
            var start = now - TimeSpan.FromSeconds(samples / (double)Math.Max(1, _rate));
            // часы звуковой карты и системные расходятся (~0,2 с за час видео) — ушли вперёд больше чем на 80 мс: заново
            if (_lastEnd != DateTime.MinValue && _lastEnd - start > TimeSpan.FromMilliseconds(80))
            {
                _lastEnd = start;
                _acc = 0; _accN = 0;
            }
            if (_lastEnd == DateTime.MinValue || start - _lastEnd > TimeSpan.FromMilliseconds(40))
            {
                // пакетов не было — это тишина: заполнить разрыв (не больше кольца)
                if (_lastEnd != DateTime.MinValue)
                {
                    var gap = (int)Math.Min(Capacity, (start - _lastEnd).TotalMilliseconds / FrameMs);
                    for (var g = 0; g < gap; g++) Push(-4f, _lastEnd.AddMilliseconds((g + 1) * FrameMs));
                }
                _lastEnd = start;
                _acc = 0; _accN = 0;
            }
            var loud = false;
            for (var i = 0; i < samples; i++)
            {
                double m = 0;
                for (var c = 0; c < _channels; c++)
                {
                    var o = (i * _channels + c) * bytesPerSample;
                    m += _float ? BitConverter.ToSingle(buf, o) : BitConverter.ToInt16(buf, o) / 32768.0;
                }
                m /= _channels;
                _acc += m * m;
                if (++_accN < _frameLen) continue;
                var rms = Math.Sqrt(_acc / _accN);
                if (rms > 0.003) loud = true;
                _lastEnd = _lastEnd.AddMilliseconds(FrameMs);
                Push((float)Math.Log10(rms + 1e-4), _lastEnd);
                _acc = 0; _accN = 0;
            }
            if (loud) LastSound = now;
        }
    }

    private void Push(float v, DateTime end)
    {
        var i = (int)(_count % Capacity);
        _env[i] = v;
        _at[i] = end.Ticks;
        _count++;
    }

    /// <summary>Огибающая колонок за [from, to] (UTC) по 10 мс; кадров нет (не писали) — тишина. null — окно вне кольца.</summary>
    public float[]? Slice(DateTime from, DateTime to)
    {
        var n = (int)Math.Round((to - from).TotalMilliseconds / FrameMs);
        if (n <= 0) return null;
        var res = new float[n];
        Array.Fill(res, -4f);
        lock (_gate)
        {
            if (_count == 0) return res;
            var oldest = _count > Capacity ? _count - Capacity : 0;
            if (new DateTime(_at[(int)(oldest % Capacity)]) > to) return null;
            for (var k = oldest; k < _count; k++)
            {
                var t = new DateTime(_at[(int)(k % Capacity)]);
                if (t <= from || t > to) continue;
                var j = (int)((t - from).TotalMilliseconds / FrameMs) - 1;
                if (j >= 0 && j < n) res[j] = _env[(int)(k % Capacity)];
            }
        }
        return res;
    }

    /// <summary>Огибающая фразы микрофона (WAV 16 бит моно с заголовком 44 байта) по 10 мс.</summary>
    public static float[] Envelope(byte[] wav, int rate)
    {
        var frame = rate * FrameMs / 1000;
        var samples = (wav.Length - 44) / 2;
        var n = samples / frame;
        var res = new float[n];
        for (var k = 0; k < n; k++)
        {
            double acc = 0;
            for (var i = 0; i < frame; i++)
            {
                var s = BitConverter.ToInt16(wav, 44 + (k * frame + i) * 2) / 32768.0;
                acc += s * s;
            }
            res[k] = (float)Math.Log10(Math.Sqrt(acc / frame) + 1e-4);
        }
        return res;
    }

    /// <summary>
    /// Насколько огибающая микрофона повторяет колонки: лучшая корреляция Пирсона по сдвигам ±maxLag кадров.
    /// spk длиннее mic на 2·maxLag (окно колонок взято с запасом по краям). Колонки молчат (разброс ничтожный) — 0.
    /// </summary>
    public static (double R, int Lag) Match(float[] mic, int micFrom, int micLen, float[] spk, int maxLag)
    {
        if (micLen < 15) return (0, 0);
        var best = (R: 0.0, Lag: 0);
        for (var lag = -maxLag; lag <= maxLag; lag++)
        {
            double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
            var n = 0;
            for (var i = 0; i < micLen; i++)
            {
                var j = micFrom + i + maxLag + lag;
                if (j < 0 || j >= spk.Length) continue;
                double x = mic[micFrom + i], y = spk[j];
                sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
                n++;
            }
            if (n < 15) continue;
            var vx = sxx - sx * sx / n;
            var vy = syy - sy * sy / n;
            if (vx <= 1e-6 || vy <= 0.05 * n * 0.01) continue;   // колонки почти без перепадов — молчат или гул
            var r = (sxy - sx * sy / n) / Math.Sqrt(vx * vy);
            if (r > best.R) best = (r, lag);
        }
        return best;
    }

    /// <summary>
    /// Весь кусок микрофона — только звук колонок: совпадает всем окном и каждыми 0,8 с (шаг 0,4), кроме ровного фона
    /// без перепадов. Слово человека на секунду поверх эха всего окна не перебьёт, а свой кусок 0,8 с — перебьёт.
    /// </summary>
    public static bool OnlyEcho(float[] mic, float[] spk, int maxLag, double minR) => WorstEcho(mic, spk, maxLag) >= minR;

    /// <summary>Худшее совпадение с колонками: всё окно и каждые 0,8 с (шаг 0,4) со словами; ровный фон не в счёт.</summary>
    public static double WorstEcho(float[] mic, float[] spk, int maxLag)
    {
        const int piece = 80, step = 40;
        var worst = Match(mic, 0, mic.Length, spk, maxLag).R;
        for (var from = 0; from + piece <= mic.Length; from += step)
        {
            double sum = 0, sq = 0;
            for (var i = from; i < from + piece; i++) { sum += mic[i]; sq += mic[i] * mic[i]; }
            var mean = sum / piece;
            if (sq / piece - mean * mean < 0.01) continue;   // разброс log-огибающей < 0,1 — ровный фон, слов нет
            worst = Math.Min(worst, Match(mic, from, piece, spk, maxLag).R);
        }
        return worst;
    }

    public void Dispose()
    {
        try { _cap?.StopRecording(); } catch (InvalidOperationException) { }
        _cap?.Dispose();
        _cap = null;
    }
}
