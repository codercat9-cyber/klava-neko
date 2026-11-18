using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KlavaDesk;

/// <summary>
/// `--tap-test`: проверка сверки «микрофон против колонок» на записях профилей голосов. Колонки играют голос Кота
/// (будто видео), в комнате говорит Аксюта. Микрофон = комната: задержка 150 мс, отражения 30/60 мс, шум.
/// </summary>
internal static class SpeakerTapTest
{
    private const int Rate = 16000;

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        float[] Clip(string who)
        {
            var dir = cfg.Abs($@"data\speakers\{who}");
            var parts = new List<float>();
            foreach (var f in Directory.GetFiles(dir, "*.wav").OrderBy(x => x))
            {
                parts.AddRange(Read(f));
                if (parts.Count > Rate * 12) break;
            }
            return [.. parts.Take(Rate * 12)];
        }
        var video = Clip("Кибер_Кот");
        var friend = Clip("Аксюта");
        var rng = new Random(7);

        // комната: задержка, два отражения, шум микрофона
        float[] Room(float[] x, double gain, int delayMs)
        {
            var d = Rate * delayMs / 1000;
            var y = new float[x.Length];
            for (var i = 0; i < x.Length; i++)
            {
                double s = 0;
                if (i - d >= 0) s += x[i - d];
                if (i - d - 480 >= 0) s += 0.45 * x[i - d - 480];
                if (i - d - 960 >= 0) s += 0.25 * x[i - d - 960];
                y[i] = (float)(s * gain);
            }
            return y;
        }
        float[] Mix(params float[][] xs)
        {
            var n = xs.Max(a => a.Length);
            var y = new float[n];
            foreach (var a in xs) for (var i = 0; i < a.Length; i++) y[i] += a[i];
            for (var i = 0; i < n; i++) y[i] += (float)((rng.NextDouble() - 0.5) * 0.006);
            return y;
        }

        var spkEnv = Env(video);                 // колонки — голос «видео»
        var silentEnv = Env(new float[video.Length]);
        const int lag = 60;                      // ±0,6 с
        double R(float[] mic, float[] spk)
        {
            var m = SpeakerTap.Envelope(ToWav(mic), Rate);
            var s = new float[m.Length + 2 * lag];
            Array.Fill(s, -4f);
            Array.Copy(spk, 0, s, lag, Math.Min(spk.Length, m.Length));
            return SpeakerTap.Match(m, 0, m.Length, s, lag).R;
        }

        var echo = R(Mix(Room(video, 0.5, 150)), spkEnv);
        var quietEcho = R(Mix(Room(video, 0.15, 150)), spkEnv);
        var live = R(Mix(Room(friend, 0.8, 5)), spkEnv);
        var over = R(Mix(Room(friend, 0.8, 5), Room(video, 0.35, 150)), spkEnv);
        var silent = R(Mix(Room(friend, 0.8, 5)), silentEnv);
        Console.WriteLine($"видео из колонок в микрофоне:          r = {echo:F2}");
        Console.WriteLine($"видео тихо (в 3 раза тише):            r = {quietEcho:F2}");
        Console.WriteLine($"Аксюта говорит, а колонки играют своё: r = {live:F2}");
        Console.WriteLine($"Аксюта поверх видео:                   r = {over:F2}");
        Console.WriteLine($"Аксюта, колонки молчат:                r = {silent:F2}");

        // полфразы — Аксюта поверх тихого видео, полфразы — одно видео: куски whisper сверяются по отдельности
        var half = Rate * 6;
        var room = Mix(Room(friend.Take(half).ToArray(), 0.8, 5), Room(video, 0.35, 150));
        var m = SpeakerTap.Envelope(ToWav(room), Rate);
        var s = new float[m.Length + 2 * lag];
        Array.Fill(s, -4f);
        Array.Copy(spkEnv, 0, s, lag, Math.Min(spkEnv.Length, m.Length));
        var firstHalf = SpeakerTap.Match(m, 0, half / 160, s, lag).R;
        var secondHalf = SpeakerTap.Match(m, half / 160, m.Length - half / 160, s, lag).R;
        Console.WriteLine($"кусок «Аксюта поверх видео»:           r = {firstHalf:F2}");
        Console.WriteLine($"кусок «одно видео»:                    r = {secondHalf:F2}");

        // окна «кыс-кыс» (2,5 с, шаг 1 с), пока она говорит: колонки — её голос (тут «видео»), в окне либо одно её эхо
        // (whisper не нужен), либо секунда чужих слов поверх эха — такое окно обязано уйти в whisper
        const int win = Rate * 5 / 2;
        int echoSkipped = 0, echoWins = 0, kysMissed = 0, kysWins = 0;
        var herEcho = Room(video, 0.5, 150);
        var loud = Enumerable.Range(0, friend.Length / Rate - 1)
                             .MaxBy(k => friend.Skip(k * Rate).Take(Rate).Sum(v => (double)v * v));
        var near = Room(friend.Skip(loud * Rate).Take(Rate).ToArray(), 0.8, 5);   // самая громкая секунда Аксюты — «кыс-кыс»
        for (var at = 0; at + win <= video.Length; at += Rate)
        {
            var pure = Mix(herEcho.Skip(at).Take(win).ToArray());
            var word = (float[])pure.Clone();
            for (var i = 0; i < near.Length; i++) word[Rate * 3 / 4 + i] += near[i];
            // колонки с запасом ±0,6 с по краям, как OnlySpeakers берёт из кольца: в начале окна микрофон уже слышит
            // эхо того, что колонки играли до окна
            var sp = new float[win / 160 + 2 * lag];
            for (var k = 0; k < sp.Length; k++)
            {
                var src = at / 160 - lag + k;
                sp[k] = src >= 0 && src < spkEnv.Length ? spkEnv[src] : -4f;
            }
            echoWins++; kysWins++;
            var wp = SpeakerTap.WorstEcho(SpeakerTap.Envelope(ToWav(pure), Rate), sp, lag);
            var ww = SpeakerTap.WorstEcho(SpeakerTap.Envelope(ToWav(word), Rate), sp, lag);
            Console.WriteLine($"  окно {at / Rate,2} с: эхо — худший кусок r {wp:F2}; со словами — {ww:F2}");
            if (wp >= OverlayForm.SpeakerEchoR) echoSkipped++;
            if (ww >= OverlayForm.SpeakerEchoR) kysMissed++;
        }
        Console.WriteLine($"окна «кыс-кыс»: одно её эхо — whisper не нужен в {echoSkipped} из {echoWins}; " +
                          $"секунда слов поверх эха — пропущено {kysMissed} из {kysWins}");
        var ok = echo >= OverlayForm.SpeakerEchoR && quietEcho >= OverlayForm.SpeakerEchoR && live < OverlayForm.SpeakerEchoR - 0.15
                 && silent == 0 && over < OverlayForm.SpeakerEchoR && firstHalf < OverlayForm.SpeakerEchoR && secondHalf >= OverlayForm.SpeakerEchoR
                 && kysMissed == 0 && echoSkipped * 2 >= echoWins;
        Console.WriteLine(ok ? $"== слух на колонки отличает компьютер от людей (порог {OverlayForm.SpeakerEchoR:F2})" : "== слух на колонки: НЕ отличает");
        return ok ? 0 : 1;
    }

    private static float[] Env(float[] x) => SpeakerTap.Envelope(ToWav(x), Rate);

    private static byte[] ToWav(float[] x)
    {
        var wav = new byte[44 + x.Length * 2];
        for (var i = 0; i < x.Length; i++)
            BitConverter.TryWriteBytes(wav.AsSpan(44 + i * 2), (short)Math.Clamp(x[i] * 32767, short.MinValue, short.MaxValue));
        return wav;
    }

    private static float[] Read(string file)
    {
        using var reader = new AudioFileReader(file);
        ISampleProvider sp = reader.WaveFormat.Channels > 1 ? new StereoToMonoSampleProvider(reader) : reader;
        if (sp.WaveFormat.SampleRate != Rate) sp = new WdlResamplingSampleProvider(sp, Rate);
        var res = new List<float>();
        var buf = new float[Rate];
        int n;
        while ((n = sp.Read(buf.AsSpan())) > 0) res.AddRange(buf.Take(n));
        return [.. res];
    }
}
