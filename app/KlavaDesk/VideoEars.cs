using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KlavaDesk;

/// <summary>
/// Уши видео: звук только процесса браузера (видео, стрим — без её голоса и без комнаты), нарезанный по паузам речи,
/// распознаётся тем же whisper, что слушает людей, — и идёт в журнал просмотра как «говорят в видео» с временем.
/// Друг 10.10: «она вместо этого не может понять, чё кто когда выпил» — по картинке раз в несколько секунд это не
/// понять, а по словам блогера с временем — да. Кусок: от 3 до 12 с, режется на паузе ≥0,35 с; пока whisper занят
/// прошлым куском, звук копится (до 20 с), ничего не теряется.
/// </summary>
internal sealed class VideoEars : IDisposable
{
    private const int Rate = 16000;
    private const int FrameMs = 30;
    private const int FrameBytes = Rate * 2 * FrameMs / 1000;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<byte[], CancellationToken, Task<string?>> _transcribe;

    /// <summary>Распознанная речь видео: (начало куска UTC, текст).</summary>
    public event Action<DateTime, string>? Heard;

    public int Pid { get; }

    private VideoEars(int pid, Func<byte[], CancellationToken, Task<string?>> transcribe)
    {
        Pid = pid;
        _transcribe = transcribe;
    }

    /// <summary>Слушать процесс окна (браузер); null — окно без процесса или Windows без захвата по процессу.</summary>
    public static VideoEars? Start(IntPtr hwnd, Func<byte[], CancellationToken, Task<string?>> transcribe)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return null;
        var ears = new VideoEars((int)pid, transcribe);
        _ = Task.Run(() => ears.LoopAsync(ears._cts.Token));
        return ears;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var chunk = new MemoryStream();
        var chunkStart = DateTime.MinValue;
        var quietMs = 0;
        double noise = 0.004;
        var pending = new byte[FrameBytes];
        var have = 0;
        Task? busy = null;
        try
        {
            await using var rec = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)Pid, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(new WaveFormat(Rate, 16, 1))
                .BuildAsync();
            Log.Write($"видео: слышу звук браузера (процесс {Pid}) — речь из видео в журнал просмотра");
            await foreach (var buf in rec.CaptureAsync(ct))
            {
                // копия пакета: дальше в цикле есть await (ждём whisper), а Span через await не живёт
                var data = buf.Data.ToArray();
                var pos = 0;
                while (pos < data.Length)
                {
                    var n = Math.Min(FrameBytes - have, data.Length - pos);
                    Buffer.BlockCopy(data, pos, pending, have, n);
                    have += n;
                    pos += n;
                    if (have < FrameBytes) continue;
                    have = 0;
                    double acc = 0;
                    for (var i = 0; i + 1 < FrameBytes; i += 2) { var s = BitConverter.ToInt16(pending, i) / 32768.0; acc += s * s; }
                    var rms = Math.Sqrt(acc / (FrameBytes / 2));
                    var voiced = rms > Math.Max(0.006, noise * 2.2);
                    if (!voiced) noise = noise * 0.98 + rms * 0.02;
                    if (chunk.Length == 0)
                    {
                        if (!voiced) continue;   // тишину до речи не копим
                        chunkStart = DateTime.UtcNow - TimeSpan.FromMilliseconds(FrameMs);
                    }
                    chunk.Write(pending, 0, FrameBytes);
                    quietMs = voiced ? 0 : quietMs + FrameMs;
                    var ms = chunk.Length / 2 * 1000 / Rate;
                    var cut = (ms >= 3000 && quietMs >= 350) || ms >= 12000;
                    // whisper занят прошлым куском — копим дальше (до 20 с), потом всё равно отдаём
                    if (!cut || (busy is { IsCompleted: false } && ms < 20000)) continue;
                    if (quietMs >= ms - 200) { chunk.SetLength(0); quietMs = 0; continue; }   // одна тишина
                    var pcm = chunk.ToArray();
                    var at = chunkStart;
                    chunk = new MemoryStream();
                    quietMs = 0;
                    if (busy is { IsCompleted: false }) await busy;
                    busy = Task.Run(() => RecognizeAsync(pcm, at, ct), ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Write($"видео: звук браузера не слышу ({ex.Message}) — смотрю только глазами");
        }
    }

    private async Task RecognizeAsync(byte[] pcm, DateTime at, CancellationToken ct)
    {
        try
        {
            var text = await _transcribe(Wav(pcm), ct);
            if (string.IsNullOrWhiteSpace(text) || text == "[неразборчиво]" || Ears.IsJunk(text)) return;
            Heard?.Invoke(at, text.Trim());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            Log.Write($"видео: кусок речи не распознан ({ex.Message})");
        }
    }

    private static byte[] Wav(byte[] pcm)
    {
        var wav = new byte[44 + pcm.Length];
        var s = wav.AsSpan();
        "RIFF"u8.CopyTo(s);
        BitConverter.TryWriteBytes(s[4..], 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(s[8..]);
        BitConverter.TryWriteBytes(s[16..], 16);
        BitConverter.TryWriteBytes(s[20..], (short)1);
        BitConverter.TryWriteBytes(s[22..], (short)1);
        BitConverter.TryWriteBytes(s[24..], Rate);
        BitConverter.TryWriteBytes(s[28..], Rate * 2);
        BitConverter.TryWriteBytes(s[32..], (short)2);
        BitConverter.TryWriteBytes(s[34..], (short)16);
        "data"u8.CopyTo(s[36..]);
        BitConverter.TryWriteBytes(s[40..], pcm.Length);
        pcm.CopyTo(s[44..]);
        return wav;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
