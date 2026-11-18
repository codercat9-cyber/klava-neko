using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace KlavaDesk;

/// <summary>
/// Слух игры: звук только процесса игры (захват по процессу, Windows 10 2004+) — её собственный голос сюда не попадает.
/// Ловит «бах»: окно 10 мс в разы громче фона последних ~2 с и с высоким пиком. В Бакшоте боевой выстрел — громкий бах,
/// холостой — щелчок: бах — повод глазам посмотреть сразу (друг 09.10: «выстрелы пусть пропускает, а смерть Дилера
/// или мою — никогда»). Уровни каждого баха — в лог, чтобы подстроить пороги по живой игре.
/// </summary>
internal sealed class GameSound : IDisposable
{
    private const double MinPeak = 0.30;        // пик баха (доля полной шкалы)
    private const double MinRatio = 6.0;        // во сколько раз громче фона
    private const double MinRms = 0.06;         // и сам по себе не шёпот
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(1.5);

    private readonly CancellationTokenSource _cts = new();
    private DateTime _lastBang = DateTime.MinValue;

    /// <summary>Бах: пик, во сколько раз громче фона.</summary>
    public event Action<double, double>? Bang;

    public int Pid { get; }

    private GameSound(int pid) => Pid = pid;

    /// <summary>Слушать процесс окна игры; null — захват по процессу не поддерживается или окно без процесса.</summary>
    public static GameSound? Start(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return null;
        var s = new GameSound((int)pid);
        _ = Task.Run(() => s.LoopAsync(s._cts.Token));
        return s;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var fmt = new WaveFormat(44100, 16, 2);
        const int window = 441;                  // 10 мс
        double slow = 0;                         // фон: средний уровень окна за ~2 с
        var winSum = 0.0; var winPeak = 0.0; var winN = 0;
        try
        {
            await using var rec = await new WasapiRecorderBuilder()
                .WithProcessLoopback((uint)Pid, ProcessLoopbackMode.IncludeTargetProcessTree)
                .WithFormat(fmt)
                .BuildAsync();
            Log.Write($"игра: слышу звук игры (процесс {Pid}) — бах выстрела будит глаза");
            await foreach (var buf in rec.CaptureAsync(ct))
            {
                var data = buf.Data.Span;
                for (var i = 0; i + 3 < data.Length; i += 4)
                {
                    var l = BitConverter.ToInt16(data.Slice(i, 2)) / 32768.0;
                    var r = BitConverter.ToInt16(data.Slice(i + 2, 2)) / 32768.0;
                    var m = (Math.Abs(l) + Math.Abs(r)) * 0.5;
                    winSum += m * m;
                    if (m > winPeak) winPeak = m;
                    if (++winN < window) continue;
                    var rms = Math.Sqrt(winSum / winN);
                    var ratio = rms / Math.Max(slow, 0.004);
                    if (rms >= MinRms && winPeak >= MinPeak && ratio >= MinRatio && DateTime.UtcNow - _lastBang > Debounce)
                    {
                        _lastBang = DateTime.UtcNow;
                        Bang?.Invoke(winPeak, ratio);
                    }
                    slow = slow * 0.995 + rms * 0.005;   // ~2 с при окне 10 мс
                    winSum = 0; winPeak = 0; winN = 0;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
        {
            Log.Write($"игра: звук игры не слышу ({ex.Message}) — смотрю только глазами");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
