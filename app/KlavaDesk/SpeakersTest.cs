namespace KlavaDesk;

/// <summary>
/// KlavaDesk.exe --speakers-test — проверка голосовых профилей на КОПИИ data\speakers (настоящие не трогаются):
/// калибровка порога модели на записях друзей, поиск чужих записей в профилях, опознание каждой записи профилем без
/// неё, и долгая запись: склейка «разговора» — записи одного человека вперемешку с чужими голосами и эхом Клавы;
/// в профиль должны попасть только его куски.
/// </summary>
internal static class SpeakersTest
{
    public static int Run()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\speakers_test.log"));
        var model = Lite.SpeakerModelPath(cfg);
        if (model is null) { Console.WriteLine("нет модели голосовых профилей"); return 1; }
        var src = cfg.Abs(@"data\speakers");
        var tmp = Path.Combine(Path.GetTempPath(), "klava_speakers_test");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
        foreach (var d in Directory.GetDirectories(src).Where(d => !Path.GetFileName(d).StartsWith('_')))
        {
            var dst = Path.Combine(tmp, Path.GetFileName(d));
            Directory.CreateDirectory(dst);
            foreach (var f in Directory.GetFiles(d, "*.wav")) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)));
        }
        var tag = LiteSpeakers.ModelTag(model);
        using var spk = new LiteSpeakers(model, tmp, cfg.SpeakerThreshold > 0 ? cfg.SpeakerThreshold : LiteSpeakers.DefaultThreshold(tag));
        Console.WriteLine($"модель {spk.Model}, порог {spk.Threshold:F2}, профили: {string.Join(", ", spk.List().Select(p => $"{p.Name} ({p.Samples})"))}");
        var fails = 0;

        foreach (var line in spk.Calibrate()) Console.WriteLine($"  {line}");
        var bad = spk.Audit();
        Console.WriteLine(bad.Count == 0 ? "  [ок] чужих записей в профилях нет"
            : "  [ВНИМАНИЕ] сомнительные записи:\n" + string.Join("\n", bad.Select(b => $"    {b.Name}/{Path.GetFileName(b.File)}: свой {b.Own:F2}, «{b.OtherName}» {b.Other:F2}")));

        // обучение слуха: самопроверка «было / стало» и пороги; после него опознание должно работать в новом пространстве
        var (earOk, earReport) = spk.TrainEar(null);
        Console.WriteLine("  обучение слуха: " + earReport.Replace("\n", "\n    "));
        if (!earOk) { fails++; Console.WriteLine("  [ПЛОХО] слух не обучился"); }
        else
        {
            var probeFile = Directory.GetFiles(Path.Combine(tmp, "Кибер_Кот"), "*.wav").FirstOrDefault()
                            ?? Directory.GetDirectories(tmp).SelectMany(d => Directory.GetFiles(d, "*.wav")).First();
            var m = spk.Identify(File.ReadAllBytes(probeFile));
            Console.WriteLine($"  опознание после обучения: {m.Name ?? "не узнан"} ({m.Similarity:F2}, порог {spk.DecisionThreshold:F2}) — запись {Path.GetFileName(Path.GetDirectoryName(probeFile))}");
            using var again = new LiteSpeakers(model, tmp, spk.Threshold);
            if (Math.Abs(again.DecisionThreshold - spk.DecisionThreshold) > 1e-3) { fails++; Console.WriteLine("  [ПЛОХО] пороги не сохранились"); }
            else Console.WriteLine($"  [ок] пороги сохранились и читаются при запуске ({again.DecisionThreshold:F2} / {again.ShortThreshold:F2})");
        }

        // долгая запись: Настя (или у кого записей больше, кроме Клавы) вперемешку с чужими
        var people = spk.List().Where(p => p.Name != "Клава" && p.Samples >= 4).OrderByDescending(p => p.Samples).ToList();
        var target = people.FirstOrDefault(p => p.Name.StartsWith("Настя")) ?? people.FirstOrDefault();
        if (target is null) { Console.WriteLine("  мало записей для проверки долгой записи"); return fails == 0 ? 0 : 1; }
        var mine = Directory.GetFiles(Path.Combine(tmp, LiteSpeakers.Sanitize(target.Name)), "*.wav").ToList();
        var others = Directory.GetDirectories(tmp).Where(d => Path.GetFileName(d) != LiteSpeakers.Sanitize(target.Name))
            .SelectMany(d => Directory.GetFiles(d, "*.wav").Take(2)).ToList();
        var conv = new List<float>();
        var silence = new float[16000 / 2];
        var otherSec = 0.0;
        for (var i = 0; i < mine.Count; i++)
        {
            conv.AddRange(Lite.ReadWav16k(File.ReadAllBytes(mine[i])));
            conv.AddRange(silence);
            if (i < others.Count)
            {
                var o = Lite.ReadWav16k(File.ReadAllBytes(others[i]));
                otherSec += o.Length / 16000.0;
                conv.AddRange(o);
                conv.AddRange(silence);
            }
        }
        Console.WriteLine($"  «разговор»: {conv.Count / 16000.0 / 60:F1} мин, из них чужих голосов {otherSec:F0} с ({Math.Min(others.Count, mine.Count)} вставок)");
        // профиль человека пишется заново из «разговора»: опора — только сама запись (самый «густой» голос)
        var probe = LiteSpeakers.Sanitize(target.Name);
        var r = spk.LongEnroll(probe, conv.ToArray(), replace: true, new Progress<string>(_ => { }));
        Console.WriteLine($"  долгая запись: {r.Report}");
        if (!r.Ok) { fails++; Console.WriteLine("  [ПЛОХО] долгая запись не собрала профиль"); }
        else
        {
            // каждый кусок нового профиля должен узнаваться как тот же человек, а не как вставленные чужие
            var dir = Path.Combine(tmp, LiteSpeakers.Sanitize(probe));
            int right = 0, wrong = 0;
            foreach (var f in Directory.GetFiles(dir, "*.wav"))
            {
                var m = spk.Identify(File.ReadAllBytes(f));
                var sc = m.Scores ?? [];
                var vsTarget = sc.GetValueOrDefault(probe);
                var bestOther = sc.Where(kv => kv.Key != probe).Select(kv => kv.Value).DefaultIfEmpty(0).Max();
                if (vsTarget >= bestOther) right++; else wrong++;
            }
            Console.WriteLine($"  куски нового профиля: голос «{target.Name}» — {right}, чужой — {wrong}");
            if (wrong > 0) { fails++; Console.WriteLine("  [ПЛОХО] в профиль попал чужой голос"); }
            else Console.WriteLine("  [ок] в профиль попал только свой голос");
            // и новый профиль узнаёт исходные записи человека (они теперь в карантине копии — читаются оттуда)
            var qdir = Path.Combine(tmp, LiteSpeakers.QuarantineDir, probe);
            var originals = mine.Select(f => File.Exists(f) ? f : Path.Combine(qdir, Path.GetFileName(f))).Where(File.Exists).ToList();
            var hits = originals.Count(f => spk.Identify(File.ReadAllBytes(f)).Name == probe);
            Console.WriteLine($"  новый профиль узнаёт прежние записи «{target.Name}»: {hits} из {originals.Count}");
            if (hits < originals.Count * 0.8) { fails++; Console.WriteLine("  [ПЛОХО] узнаёт мало"); }
        }
        Directory.Delete(tmp, recursive: true);
        Console.WriteLine(fails == 0 ? "speakers: OK" : $"speakers: провалов {fails}");
        return fails == 0 ? 0 : 1;
    }
}
