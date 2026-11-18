using System.Diagnostics;
using SherpaOnnx;

namespace KlavaDesk;

/// <summary>
/// KlavaDesk.exe --speakers-bench — какая модель отпечатков голоса (или их связка) лучше всего различает ИМЕННО этих
/// людей. Записи профилей (data\speakers\имя\*.wav, только чтение) режутся на куски по 2 и 1,2 с — как настоящие
/// реплики. Каждый кусок сравнивается с профилем своего человека, собранным БЕЗ его записи, и с профилями остальных.
/// Считается EER (где ошибки «чужой принят» и «свой не узнан» равны) и точность «кто из комнаты» (лучший профиль
/// = правильный человек). Связки моделей: оценки каждой приводятся к общей шкале по распределению чужих и усредняются.
/// </summary>
internal static class SpeakersBench
{
    private const int Rate = 16000;

    private sealed record Piece(int Person, int File, float[] Audio);

    public static int Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\speakers_bench.log"));
        var store = cfg.Abs(@"data\speakers");
        var people = Directory.GetDirectories(store)
            .Where(d => !Path.GetFileName(d).StartsWith('_'))
            .Select(d => (Name: Path.GetFileName(d), Files: Directory.GetFiles(d, "*.wav").Order(StringComparer.Ordinal).ToArray()))
            .Where(p => p.Files.Length > 0)
            .ToList();
        if (people.Count(p => p.Files.Length >= 2) < 2) { Console.WriteLine("мало людей с двумя и больше записями — сравнивать не на чем"); return 1; }

        var models = Directory.Exists(cfg.Abs(@"models\speaker\sherpa"))
            ? Directory.GetFiles(cfg.Abs(@"models\speaker\sherpa"), "*.onnx").ToList()
            : [];
        var lite = Directory.Exists(cfg.Abs(cfg.LiteDir)) ? Directory.GetFiles(cfg.Abs(cfg.LiteDir), "*speech_*sv*.onnx") : [];
        models.AddRange(lite);
        if (args.Length > 0) models = models.Where(m => args.Any(a => Path.GetFileName(m).Contains(a, StringComparison.OrdinalIgnoreCase))).ToList();
        if (models.Count == 0) { Console.WriteLine("нет моделей отпечатков голоса"); return 1; }

        Console.WriteLine($"люди: {string.Join(", ", people.Select(p => $"{p.Name} ({p.Files.Length})"))}");
        foreach (var len in new[] { 2.0, 1.2 })
        {
            var pieces = Cut(people.Select(p => p.Files).ToList(), len);
            Console.WriteLine($"\n== куски по {len:F1} с: {pieces.Count} (по людям: " +
                              string.Join(", ", people.Select((p, i) => $"{p.Name} {pieces.Count(x => x.Person == i)}")) + ")");
            var results = new List<(string Model, double[] Own, double[] Imp, int[] Top, double Ms)>();
            foreach (var m in models)
            {
                var r = Score(m, pieces, people.Count, out var emb);
                if (r is null || emb is null) continue;
                results.Add(r.Value);
                Report(Path.GetFileNameWithoutExtension(m), r.Value.Own, r.Value.Imp, r.Value.Top, pieces.Count, r.Value.Ms);
                foreach (var shrink in new[] { 0.2, 0.4, 0.7 })
                {
                    var b = Trained(emb, pieces, people.Count, shrink, snorm: false);
                    Report($"   + обученный слух (усадка {shrink:F1})", b.Own, b.Imp, b.Top, pieces.Count, 0);
                }
                var bs = Trained(emb, pieces, people.Count, 0.4, snorm: true);
                Report("   + обученный слух + S-norm", bs.Own, bs.Imp, bs.Top, pieces.Count, 0);
            }
            // связки двух и трёх лучших моделей: общая шкала — (оценка − среднее чужих) / разброс чужих
            var ranked = results.OrderBy(r => Eer(r.Own, r.Imp).Eer).ToList();
            foreach (var k in new[] { 2, 3 })
            {
                if (ranked.Count < k) continue;
                var set = ranked.Take(k).ToList();
                var own = new double[set[0].Own.Length];
                var imp = new double[set[0].Imp.Length];
                foreach (var r in set)
                {
                    var mu = r.Imp.Average();
                    var sd = Math.Sqrt(r.Imp.Select(x => (x - mu) * (x - mu)).Average()) + 1e-9;
                    for (var i = 0; i < own.Length; i++) own[i] += (r.Own[i] - mu) / sd / k;
                    for (var i = 0; i < imp.Length; i++) imp[i] += (r.Imp[i] - mu) / sd / k;
                }
                var (eer, thr) = Eer(own, imp);
                Console.WriteLine($"  связка {string.Join(" + ", set.Select(r => Short(r.Model)))}: EER {eer * 100:F1} % (порог {thr:F2} в шкале чужих)");
            }
        }
        return 0;
    }

    private static string Short(string model) => LiteSpeakers.ModelTag(model);

    /// <summary>Записи → куски речи заданной длины (тишина по краям срезается, тихие куски выкидываются).</summary>
    private static List<Piece> Cut(List<string[]> files, double len)
    {
        var res = new List<Piece>();
        var n = (int)(len * Rate);
        for (var p = 0; p < files.Count; p++)
            for (var f = 0; f < files[p].Length; f++)
            {
                float[] a;
                try { a = Lite.ReadWav16k(File.ReadAllBytes(files[p][f])); }
                catch (Exception ex) when (ex is IOException or InvalidDataException) { continue; }
                a = Trim(a);
                if (a.Length < n) { if (a.Length >= Rate) res.Add(new Piece(p, f, a)); continue; }
                for (var s = 0; s + n <= a.Length; s += n)
                {
                    var seg = a[s..(s + n)];
                    double e = 0;
                    foreach (var v in seg) e += v * v;
                    if (Math.Sqrt(e / seg.Length) > 0.004) res.Add(new Piece(p, f, seg));
                }
            }
        return res;
    }

    private static float[] Trim(float[] a)
    {
        const int frame = 480;
        var nf = a.Length / frame;
        if (nf < 4) return a;
        var rms = new double[nf];
        for (var i = 0; i < nf; i++)
        {
            double s = 0;
            for (var j = 0; j < frame; j++) { var v = a[i * frame + j]; s += v * v; }
            rms[i] = Math.Sqrt(s / frame);
        }
        var thr = Math.Max(rms.Max() * 0.08, 0.004);
        int first = Array.FindIndex(rms, x => x > thr), last = Array.FindLastIndex(rms, x => x > thr);
        return first < 0 ? a : a[(Math.Max(0, first - 3) * frame)..(Math.Min(nf, last + 4) * frame)];
    }

    /// <summary>
    /// Оценки одной модели: для куска — близость к профилю своего (без его записи) и к профилям чужих;
    /// Top — индекс лучшего профиля для каждого куска с «своим» профилем (для точности «кто говорит»).
    /// </summary>
    private static (string Model, double[] Own, double[] Imp, int[] Top, double Ms)? Score(string model, List<Piece> pieces, int nPeople, out float[][]? embOut)
    {
        embOut = null;
        SpeakerEmbeddingExtractor ex;
        try { ex = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig { Model = model, NumThreads = 4, Provider = "cpu", Debug = 0 }); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.WriteLine($"  {Path.GetFileName(model)}: не загрузилась ({e.Message})");
            return null;
        }
        using (ex)
        {
            var sw = Stopwatch.StartNew();
            var emb = pieces.Select(p => Embed(ex, p.Audio)).ToArray();
            embOut = emb;
            var ms = sw.Elapsed.TotalMilliseconds / Math.Max(1, pieces.Count);
            var own = new List<double>();
            var imp = new List<double>();
            var top = new List<int>();
            for (var i = 0; i < pieces.Count; i++)
            {
                var me = pieces[i];
                // профиль своего без этой записи: другие записи того же человека
                var mine = Enumerable.Range(0, pieces.Count).Where(j => pieces[j].Person == me.Person && pieces[j].File != me.File).ToList();
                if (mine.Count == 0) continue;
                var ownScore = Dot(emb[i], Mean(mine.Select(j => emb[j])));
                own.Add(ownScore);
                var best = (me.Person, ownScore);
                for (var q = 0; q < nPeople; q++)
                {
                    if (q == me.Person) continue;
                    var theirs = Enumerable.Range(0, pieces.Count).Where(j => pieces[j].Person == q).ToList();
                    if (theirs.Count == 0) continue;
                    var s = Dot(emb[i], Mean(theirs.Select(j => emb[j])));
                    imp.Add(s);
                    if (s > best.ownScore) best = (q, s);
                }
                top.Add(best.Item1 == me.Person ? 1 : 0);
            }
            return (model, own.ToArray(), imp.ToArray(), top.ToArray(), ms);
        }
    }

    /// <summary>
    /// Честная проверка обученного слуха: для каждой записи слух обучается на ВСЕХ ОСТАЛЬНЫХ записях (её кусков в
    /// обучении нет), профили — тоже без неё. S-norm: оценка нормируется по «когорте» — кускам людей, кроме
    /// проверяемого профиля, с двух сторон (кусок против когорты и профиль против когорты).
    /// </summary>
    private static (double[] Own, double[] Imp, int[] Top) Trained(float[][] emb, List<Piece> pieces, int nPeople, double shrink, bool snorm)
    {
        var own = new List<double>();
        var imp = new List<double>();
        var top = new List<int>();
        foreach (var grp in Enumerable.Range(0, pieces.Count).GroupBy(i => (pieces[i].Person, pieces[i].File)))
        {
            var test = grp.ToList();
            var train = Enumerable.Range(0, pieces.Count).Where(i => !(pieces[i].Person == grp.Key.Person && pieces[i].File == grp.Key.File)).ToList();
            if (!train.Any(i => pieces[i].Person == grp.Key.Person)) continue;
            var be = TrainedEar.Train(train.Select(i => emb[i]).ToList(), train.Select(i => pieces[i].Person).ToList(), shrink);
            if (be is null) continue;
            var z = new Dictionary<int, float[]>();
            foreach (var i in train.Concat(test)) z[i] = be.Apply(emb[i]);
            var profiles = new Dictionary<int, float[]>();
            for (var q = 0; q < nPeople; q++)
            {
                var theirs = train.Where(i => pieces[i].Person == q).Select(i => z[i]).ToList();
                if (theirs.Count > 0) profiles[q] = Mean(theirs);
            }
            // когорта для S-norm: куски обучения, по профилю — без кусков этого человека
            (double Mu, double Sd) Stat(float[] v, int exceptPerson)
            {
                var s = train.Where(i => pieces[i].Person != exceptPerson).Select(i => Dot(v, z[i])).ToList();
                if (s.Count < 3) return (0, 1);
                var mu = s.Average();
                return (mu, Math.Sqrt(s.Select(x => (x - mu) * (x - mu)).Average()) + 1e-6);
            }
            var profStat = profiles.ToDictionary(kv => kv.Key, kv => Stat(kv.Value, kv.Key));
            foreach (var t in test)
            {
                double S(int q)
                {
                    var raw = Dot(z[t], profiles[q]);
                    if (!snorm) return raw;
                    var (mt, st) = Stat(z[t], q);
                    var (mp, sp) = profStat[q];
                    return ((raw - mt) / st + (raw - mp) / sp) / 2;
                }
                var me = grp.Key.Person;
                var ownScore = S(me);
                own.Add(ownScore);
                var bestQ = me;
                var bestS = ownScore;
                foreach (var q in profiles.Keys.Where(q => q != me))
                {
                    var s = S(q);
                    imp.Add(s);
                    if (s > bestS) { bestS = s; bestQ = q; }
                }
                top.Add(bestQ == me ? 1 : 0);
            }
        }
        return (own.ToArray(), imp.ToArray(), top.ToArray());
    }

    private static void Report(string name, double[] own, double[] imp, int[] top, int pieces, double ms)
    {
        var (eer, thr) = Eer(own, imp);
        Console.WriteLine($"  {name,-58} EER {eer * 100,5:F1} % (порог {thr:F2}) · кто из комнаты: {(top.Length == 0 ? 0 : top.Average()) * 100:F1} % · " +
                          $"свой {Median(own):F2}, чужой {Median(imp):F2}{(ms > 0 ? $" · {ms:F0} мс на кусок" : "")}");
    }

    /// <summary>Равные ошибки: порог, где доля непринятых своих равна доле принятых чужих.</summary>
    private static (double Eer, double Threshold) Eer(double[] own, double[] imp)
    {
        if (own.Length == 0 || imp.Length == 0) return (1, 0);
        var cand = own.Concat(imp).Order().ToArray();
        var best = (Eer: 1.0, Thr: 0.0, Gap: double.MaxValue);
        foreach (var t in cand)
        {
            var frr = own.Count(x => x < t) / (double)own.Length;
            var far = imp.Count(x => x >= t) / (double)imp.Length;
            var gap = Math.Abs(frr - far);
            if (gap < best.Gap) best = ((frr + far) / 2, t, gap);
        }
        return (best.Eer, best.Thr);
    }

    private static double Median(double[] v) => v.Length == 0 ? 0 : v.Order().ElementAt(v.Length / 2);

    private static float[] Embed(SpeakerEmbeddingExtractor ex, float[] audio)
    {
        var peak = audio.Max(Math.Abs);
        if (peak > 1e-4f) { var k = 0.9f / peak; audio = audio.Select(v => v * k).ToArray(); }
        using var s = ex.CreateStream();
        s.AcceptWaveform(Rate, audio);
        s.InputFinished();
        var e = ex.Compute(s);
        var norm = MathF.Sqrt(e.Sum(x => x * x));
        if (norm > 1e-9f) for (var i = 0; i < e.Length; i++) e[i] /= norm;
        return e;
    }

    private static float[] Mean(IEnumerable<float[]> v)
    {
        float[]? m = null;
        foreach (var x in v)
        {
            m ??= new float[x.Length];
            for (var i = 0; i < m.Length; i++) m[i] += x[i];
        }
        m ??= [];
        var norm = MathF.Sqrt(m.Sum(x => x * x));
        if (norm > 1e-9f) for (var i = 0; i < m.Length; i++) m[i] /= norm;
        return m;
    }

    private static double Dot(float[] a, float[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s;
    }
}
