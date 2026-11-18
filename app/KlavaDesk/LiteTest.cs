using System.Diagnostics;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// KlavaDesk.exe --lite-test — проверка лёгкого комплекта на этой машине: грузятся ли модели, как быстро говорит
/// и слышит (синтез → распознавание по кругу), какой голос ближе к её эталону и где порог голосовых профилей.
/// Записи для прослушивания — data\logs\lite_test\.
/// </summary>
internal static class LiteTest
{
    private static readonly string[] Phrases =
    [
        "Здарова, братан! Я Клава, твоя кошкодевочка. Мур!",
        "В 2026 году вышла Qwen 3.6, а у нас на GitHub уже 25 звёзд.",
        "Щас открою Paint и нарисую котика, погнали!",
    ];

    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\lite_test.log"));
        var outDir = cfg.Abs(@"data\logs\lite_test");
        Directory.CreateDirectory(outDir);
        var m = Lite.Models(cfg);
        Console.WriteLine($"models\\lite: {m}");
        Console.WriteLine($"режим речи: {cfg.SpeechBackend} → {(cfg.LiteSpeech ? "лёгкий" : "большой")}; потоков на модель {Lite.Threads(cfg)}, ядер {Environment.ProcessorCount}");
        if (!m.CanHear || !m.CanSpeak) { Console.WriteLine("Нет моделей — запусти tools\\fetch_lite.ps1"); return 1; }
        var fails = 0;
        try
        {
            var sw = Stopwatch.StartNew();
            var voice = Lite.Voice(cfg);
            Console.WriteLine($"голос {voice.Name}: загрузка {sw.ElapsedMilliseconds} мс, голосов {voice.Voices}");
            sw.Restart();
            var ears = Lite.Ears(cfg);
            Console.WriteLine($"слух GigaAM: загрузка {sw.ElapsedMilliseconds} мс");
            voice.Synth("Мур.", cfg.LiteVoiceId, cfg.LiteVoiceSpeed, cfg.LiteVoiceSteps, cfg.LiteVoicePitch);   // прогрев

            for (int i = 0; i < Phrases.Length; i++)
            {
                sw.Restart();
                var wav = voice.Synth(Phrases[i], cfg.LiteVoiceId, cfg.LiteVoiceSpeed, cfg.LiteVoiceSteps, cfg.LiteVoicePitch);
                var synthMs = sw.ElapsedMilliseconds;
                var dur = (wav.Length - 44) / 2.0 / LiteVoice.OutRate;
                File.WriteAllBytes(Path.Combine(outDir, $"phrase{i + 1}.wav"), wav);
                sw.Restart();
                var segs = ears.Transcribe(Lite.ReadWav16k(wav));
                var heard = string.Join(" | ", segs.Select(s => s.Text));
                Console.WriteLine($"[{i + 1}] «{LiteText.Normalize(Phrases[i])}»");
                Console.WriteLine($"    голос {dur:F1} с за {synthMs} мс (RTF {synthMs / 1000.0 / Math.Max(dur, 0.01):F2}); слух {sw.ElapsedMilliseconds} мс: «{heard}»");
                if (segs.Count == 0 || dur < 0.5) fails++;
            }

            // живые записи друзей — как слух справляется с настоящим микрофоном
            var speakersDir = cfg.Abs(@"data\speakers");
            if (Directory.Exists(speakersDir))
            {
                foreach (var f in Directory.GetFiles(speakersDir, "*.wav").Take(3))
                {
                    var a = Lite.ReadWav16k(File.ReadAllBytes(f));
                    sw.Restart();
                    var segs = ears.Transcribe(a);
                    Console.WriteLine($"запись {Path.GetFileName(f)} ({a.Length / 16000.0:F1} с) за {sw.ElapsedMilliseconds} мс: «{string.Join(" | ", segs.Select(s => $"{s.Start:F1}-{s.End:F1}: {s.Text}"))}»");
                }
            }

            if (m.CanTellSpeakers)
            {
                var spk = Lite.Speakers(cfg);
                foreach (var line in spk.Calibrate()) Console.WriteLine($"профили: {line}");
                var voiceJson = cfg.Abs(@"tts\voice.json");
                var refRel = File.Exists(voiceJson) ? JsonNode.Parse(File.ReadAllText(voiceJson))?["ref"]?.GetValue<string>() : null;
                var refWav = refRel is null ? null : cfg.Abs(refRel.Replace('/', '\\'));
                if (refWav is not null && File.Exists(refWav))
                {
                    // какой из женских голосов Supertonic и на какой высоте ближе к её большому голосу (среднее по двум фразам)
                    var r = File.ReadAllBytes(refWav);
                    string[] probes = ["Приветик! Я Клава, твоя кошечка. Мур-мур! Как же я рада тебя видеть.", "Ну чё, братан, погнали что-нибудь замутим? Я сегодня в настроении!"];
                    var best = (Sid: 0, Pitch: 1.0, Sim: -1.0);
                    for (var sid = 0; sid < Math.Min(5, voice.Voices); sid++)
                    {
                        var line = new System.Text.StringBuilder($"голос F{sid + 1}:");
                        foreach (var pitch in new[] { 1.0, 1.03, 1.06, 1.09 })
                        {
                            var sims = probes.Select((pr, k) =>
                            {
                                var w = voice.Synth(pr, sid, cfg.LiteVoiceSpeed, cfg.LiteVoiceSteps, pitch);
                                if (k == 0) File.WriteAllBytes(Path.Combine(outDir, $"voice_F{sid + 1}_p{pitch:F2}.wav".Replace(',', '.')), w);
                                return spk.Similarity(r, w);
                            }).Average();
                            line.Append($"  {pitch:F2}→{sims:F3}");
                            if (sims > best.Sim) best = (sid, pitch, sims);
                        }
                        Console.WriteLine(line);
                    }
                    Console.WriteLine($"ближе всех к её голосу: F{best.Sid + 1} на высоте {best.Pitch:F2} (сходство {best.Sim:F3}); сейчас в klava.json F{cfg.LiteVoiceId + 1} на {cfg.LiteVoicePitch:F2}");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException or DllNotFoundException or BadImageFormatException)
        {
            Console.WriteLine($"ОШИБКА: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        Console.WriteLine(fails == 0 ? "лёгкий комплект: OK" : $"лёгкий комплект: провалов {fails}");
        return fails == 0 ? 0 : 1;
    }
}
