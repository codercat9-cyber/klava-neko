namespace KlavaDesk;

/// <summary>
/// «Обученный слух» поверх отпечатков голоса. Нейросеть отпечатков обучена на чужих людях и чужих микрофонах, а тут её
/// подстраивают под ЭТУ комнату и ЭТИХ людей по их же записям (тренировка чтением в окне профилей).
///
/// WCCN (within-class covariance normalization): разброс отпечатков одного и того же человека (громче/тише, ближе к
/// микрофону, смех, хрипота) считается по всем записям всех профилей, и пространство растягивается так, чтобы этот
/// разброс стал одинаковым во все стороны, — различия МЕЖДУ людьми проступают сильнее. Ковариация с усадкой к
/// диагонали: людей мало, а размерность 192–512, без усадки матрица вырождается. Оценка после преобразования — тот же
/// косинус; порог и «стоит ли вообще включать» решает самопроверка в <see cref="LiteSpeakers"/>.
///
/// Обучение — доли секунды (разложение Холецкого), хранится в data\speakers\_слух.&lt;модель&gt;.bin.
/// </summary>
internal sealed class TrainedEar
{
    private const int Magic = 0x4B4C5657;
    public int Dim { get; }
    private readonly float[] _mean;
    private readonly double[] _l;          // нижний треугольник Холецкого, построчно (Dim × Dim)

    private TrainedEar(int dim, float[] mean, double[] l)
    {
        Dim = dim;
        _mean = mean;
        _l = l;
    }

    /// <summary>
    /// Обучить на отпечатках (нормированных) с метками людей. null — мало данных: нужно минимум два человека с двумя
    /// и больше отпечатками. shrink — доля усадки к диагонали (0..1).
    /// </summary>
    public static TrainedEar? Train(IReadOnlyList<float[]> x, IReadOnlyList<int> labels, double shrink = 0.4)
    {
        if (x.Count < 4 || x.Count != labels.Count) return null;
        var groups = Enumerable.Range(0, x.Count).GroupBy(i => labels[i]).Where(g => g.Count() >= 2).Select(g => g.ToList()).ToList();
        if (groups.Count < 2) return null;
        var d = x[0].Length;
        var used = groups.SelectMany(g => g).ToList();
        var mean = new double[d];
        foreach (var i in used) for (var k = 0; k < d; k++) mean[k] += x[i][k];
        for (var k = 0; k < d; k++) mean[k] /= used.Count;

        // внутриклассовый разброс: отклонения от центра своего человека (нижний треугольник)
        var sw = new double[d * d];
        var dev = new double[d];
        foreach (var g in groups)
        {
            var c = new double[d];
            foreach (var i in g) for (var k = 0; k < d; k++) c[k] += x[i][k];
            for (var k = 0; k < d; k++) c[k] /= g.Count;
            foreach (var i in g)
            {
                for (var k = 0; k < d; k++) dev[k] = x[i][k] - c[k];
                for (var r = 0; r < d; r++)
                {
                    var dr = dev[r];
                    if (dr == 0) continue;
                    var row = r * d;
                    for (var k = 0; k <= r; k++) sw[row + k] += dr * dev[k];
                }
            }
        }
        double trace = 0;
        for (var r = 0; r < d; r++) trace += sw[r * d + r];
        var n = used.Count;
        var avgVar = trace / d / n;
        for (var r = 0; r < d; r++)
            for (var k = 0; k <= r; k++)
                sw[r * d + k] = (1 - shrink) * sw[r * d + k] / n + (r == k ? shrink * avgVar + 1e-9 : 0);

        // Холецкий на месте: sw = L Lᵀ
        for (var j = 0; j < d; j++)
        {
            var s = sw[j * d + j];
            for (var k = 0; k < j; k++) s -= sw[j * d + k] * sw[j * d + k];
            if (s <= 1e-12) return null;
            var ljj = Math.Sqrt(s);
            sw[j * d + j] = ljj;
            for (var i = j + 1; i < d; i++)
            {
                var t = sw[i * d + j];
                for (var k = 0; k < j; k++) t -= sw[i * d + k] * sw[j * d + k];
                sw[i * d + j] = t / ljj;
            }
        }
        return new TrainedEar(d, mean.Select(v => (float)v).ToArray(), sw);
    }

    /// <summary>Отпечаток → в пространство обученного слуха (решение L z = x − среднее), нормированный.</summary>
    public float[] Apply(float[] e)
    {
        if (e.Length != Dim) return e;
        var d = Dim;
        var z = new double[d];
        for (var i = 0; i < d; i++)
        {
            var s = e[i] - (double)_mean[i];
            var row = i * d;
            for (var k = 0; k < i; k++) s -= _l[row + k] * z[k];
            z[i] = s / _l[row + i];
        }
        double norm = 0;
        foreach (var v in z) norm += v * v;
        norm = Math.Sqrt(norm) + 1e-12;
        var r = new float[d];
        for (var i = 0; i < d; i++) r[i] = (float)(z[i] / norm);
        return r;
    }

    /// <summary>Равные ошибки: порог, где доля непринятых своих равна доле принятых чужих.</summary>
    public static (double Eer, double Threshold) Eer(IReadOnlyList<double> own, IReadOnlyList<double> imp)
    {
        if (own.Count == 0 || imp.Count == 0) return (1, 0);
        var ownS = own.Order().ToArray();
        var impS = imp.Order().ToArray();
        var best = (Eer: 1.0, Thr: 0.0, Gap: double.MaxValue);
        foreach (var t in ownS.Concat(impS))
        {
            var frr = LowerBound(ownS, t) / (double)ownS.Length;
            var far = (impS.Length - LowerBound(impS, t)) / (double)impS.Length;
            var gap = Math.Abs(frr - far);
            if (gap < best.Gap) best = ((frr + far) / 2, t, gap);
        }
        return (best.Eer, best.Thr);
    }

    private static int LowerBound(double[] sorted, double t)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi) { var mid = (lo + hi) / 2; if (sorted[mid] < t) lo = mid + 1; else hi = mid; }
        return lo;
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        using (var f = new BinaryWriter(File.Create(tmp)))
        {
            f.Write(Magic);
            f.Write(Dim);
            foreach (var v in _mean) f.Write(v);
            for (var r = 0; r < Dim; r++)
                for (var k = 0; k <= r; k++) f.Write(_l[r * Dim + k]);
        }
        File.Move(tmp, path, overwrite: true);
    }

    public static TrainedEar? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var f = new BinaryReader(File.OpenRead(path));
            if (f.ReadInt32() != Magic) return null;
            var d = f.ReadInt32();
            if (d is <= 0 or > 4096) return null;
            var mean = new float[d];
            for (var i = 0; i < d; i++) mean[i] = f.ReadSingle();
            var l = new double[d * d];
            for (var r = 0; r < d; r++)
                for (var k = 0; k <= r; k++) l[r * d + k] = f.ReadDouble();
            return new TrainedEar(d, mean, l);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
