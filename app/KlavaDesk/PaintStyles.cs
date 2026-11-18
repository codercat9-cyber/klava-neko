using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace KlavaDesk;

/// <summary>
/// Стили картины, у каждого свой способ письма (раньше всё писалось мазками Херцмана, и «акварель» была теми же
/// мазками, только полупрозрачными):
///  - акварель — заливки по цветовым пятнам: пигмент скапливается у края пятна (тёмная кайма), соседние пятна
///    мягко затекают друг в друга, зерно бумаги, светлое — просто не закрашено; потом лессировка теней и детали;
///  - карандаш — контуры и штриховка по тону: светлое не трогается, в полутенях одна штриховка, в тенях
///    перекрёстная, линии чуть дрожат, как от руки;
///  - тушь/комикс — плоские цвета по пятнам, тени, чёрный контур;
///  - пиксель-арт — крупные пиксели ограниченной палитрой;
///  - реализм — мазки мелких кистей и лессировка деталей образцом поверх.
/// Каждый возвращает проходы (название, прозрачный слой размером с картину) — их по одному кладут в Paint.
/// </summary>
internal static class PaintStyles
{
    // ---------------- общие части ----------------

    internal sealed class Img
    {
        public readonly int W, H;
        public readonly float[] R, G, B;
        public Img(int w, int h) { W = w; H = h; R = new float[w * h]; G = new float[w * h]; B = new float[w * h]; }
        public static Img From(Bitmap bmp)
        {
            var img = new Img(bmp.Width, bmp.Height);
            var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var row = new byte[bmp.Width * 4];
                for (var y = 0; y < bmp.Height; y++)
                {
                    Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, row.Length);
                    for (var x = 0; x < bmp.Width; x++)
                    {
                        var i = y * bmp.Width + x;
                        img.B[i] = row[x * 4]; img.G[i] = row[x * 4 + 1]; img.R[i] = row[x * 4 + 2];
                    }
                }
            }
            finally { bmp.UnlockBits(d); }
            return img;
        }
        public float Lum(int i) => 0.299f * R[i] + 0.587f * G[i] + 0.114f * B[i];
        public Img Blurred(float sigma)
        {
            var o = new Img(W, H);
            Array.Copy(Box3(R, W, H, sigma), o.R, R.Length);
            Array.Copy(Box3(G, W, H, sigma), o.G, G.Length);
            Array.Copy(Box3(B, W, H, sigma), o.B, B.Length);
            return o;
        }
    }

    /// <summary>Слой из цветов и прозрачности (0..1) по пикселям.</summary>
    private static Bitmap Layer(int w, int h, Func<int, (float R, float G, float B, float A)> px)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var rows = new byte[h][];
            Parallel.For(0, h, y =>
            {
                var row = new byte[w * 4];
                for (var x = 0; x < w; x++)
                {
                    var (r, g, b, a) = px(y * w + x);
                    if (a <= 0.003f) continue;
                    row[x * 4] = (byte)Math.Clamp(b, 0, 255);
                    row[x * 4 + 1] = (byte)Math.Clamp(g, 0, 255);
                    row[x * 4 + 2] = (byte)Math.Clamp(r, 0, 255);
                    row[x * 4 + 3] = (byte)Math.Clamp(a * 255, 0, 255);
                }
                rows[y] = row;
            });
            for (var y = 0; y < h; y++) Marshal.Copy(rows[y], 0, d.Scan0 + y * d.Stride, w * 4);
        }
        finally { bmp.UnlockBits(d); }
        return bmp;
    }

    /// <summary>Слой расчёта (маленький) растянуть до размера картины.</summary>
    private static Bitmap Up(Bitmap small, int W, int H, bool pixelated = false)
    {
        if (small.Width == W && small.Height == H) return small;
        var full = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(full))
        {
            g.InterpolationMode = pixelated ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(small, new Rectangle(0, 0, W, H));
        }
        small.Dispose();
        return full;
    }

    private static float[] Box3(float[] src, int w, int h, float sigma)
    {
        var r = Math.Max(1, (int)Math.Round(sigma * 0.9f));
        var a = (float[])src.Clone();
        var tmp = new float[a.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            Parallel.For(0, h, y =>
            {
                var o = y * w;
                float acc = 0;
                var n = 0;
                for (var x = -r; x <= r; x++) if (x >= 0 && x < w) { acc += a[o + x]; n++; }
                for (var x = 0; x < w; x++)
                {
                    tmp[o + x] = acc / n;
                    int xo = x - r, xi = x + r + 1;
                    if (xo >= 0) { acc -= a[o + xo]; n--; }
                    if (xi < w) { acc += a[o + xi]; n++; }
                }
            });
            Parallel.For(0, w, x =>
            {
                float acc = 0;
                var n = 0;
                for (var y = -r; y <= r; y++) if (y >= 0 && y < h) { acc += tmp[y * w + x]; n++; }
                for (var y = 0; y < h; y++)
                {
                    a[y * w + x] = acc / n;
                    int yo = y - r, yi = y + r + 1;
                    if (yo >= 0) { acc -= tmp[yo * w + x]; n--; }
                    if (yi < h) { acc += tmp[yi * w + x]; n++; }
                }
            });
        }
        return a;
    }

    /// <summary>Плавный шум (сумма октав решётки со сглаживанием) 0..1 — зерно бумаги, дрожь линий.</summary>
    private static float[] Noise(int w, int h, int seed, float scale, int octaves = 3)
    {
        var res = new float[w * h];
        var rnd = new Random(seed);
        float amp = 1, total = 0;
        for (var o = 0; o < octaves; o++)
        {
            var cell = Math.Max(2f, scale / (1 << o));
            int gw = (int)(w / cell) + 2, gh = (int)(h / cell) + 2;
            var grid = new float[gw * gh];
            for (var i = 0; i < grid.Length; i++) grid[i] = (float)rnd.NextDouble();
            var a = amp;
            Parallel.For(0, h, y =>
            {
                var fy = y / cell;
                var y0 = (int)fy;
                var ty = fy - y0;
                ty = ty * ty * (3 - 2 * ty);
                for (var x = 0; x < w; x++)
                {
                    var fx = x / cell;
                    var x0 = (int)fx;
                    var tx = fx - x0;
                    tx = tx * tx * (3 - 2 * tx);
                    float v00 = grid[y0 * gw + x0], v10 = grid[y0 * gw + x0 + 1], v01 = grid[(y0 + 1) * gw + x0], v11 = grid[(y0 + 1) * gw + x0 + 1];
                    res[y * w + x] += a * (v00 + (v10 - v00) * tx + (v01 - v00) * ty + (v00 - v10 - v01 + v11) * tx * ty);
                }
            });
            total += amp;
            amp *= 0.5f;
        }
        for (var i = 0; i < res.Length; i++) res[i] /= total;
        return res;
    }

    /// <summary>k-средних по цветам (по выборке пикселей): палитра и номер цвета у каждого пикселя.</summary>
    private static ((float R, float G, float B)[] Palette, int[] Label) KMeans(Img img, int k, int seed)
    {
        var n = img.W * img.H;
        var rnd = new Random(seed);
        var sample = Enumerable.Range(0, Math.Min(24000, n)).Select(_ => rnd.Next(n)).ToArray();
        var pal = new (float R, float G, float B)[k];
        // начало: k-means++ по яркости — разные тона, а не k оттенков неба
        pal[0] = (img.R[sample[0]], img.G[sample[0]], img.B[sample[0]]);
        for (var c = 1; c < k; c++)
        {
            var best = sample[0];
            float bestD = -1;
            foreach (var s in sample)
            {
                var dmin = float.MaxValue;
                for (var j = 0; j < c; j++) dmin = Math.Min(dmin, D(img, s, pal[j]));
                if (dmin > bestD) { bestD = dmin; best = s; }
            }
            pal[c] = (img.R[best], img.G[best], img.B[best]);
        }
        for (var it = 0; it < 8; it++)
        {
            var sum = new double[k * 3];
            var cnt = new int[k];
            foreach (var s in sample)
            {
                var c = Nearest(img, s, pal);
                sum[c * 3] += img.R[s]; sum[c * 3 + 1] += img.G[s]; sum[c * 3 + 2] += img.B[s];
                cnt[c]++;
            }
            for (var c = 0; c < k; c++)
                if (cnt[c] > 0) pal[c] = ((float)(sum[c * 3] / cnt[c]), (float)(sum[c * 3 + 1] / cnt[c]), (float)(sum[c * 3 + 2] / cnt[c]));
        }
        var label = new int[n];
        Parallel.For(0, n, i => label[i] = Nearest(img, i, pal));
        return (pal, label);
    }

    private static float D(Img img, int i, (float R, float G, float B) p)
    {
        float dr = img.R[i] - p.R, dg = img.G[i] - p.G, db = img.B[i] - p.B;
        return dr * dr * 0.3f + dg * dg * 0.59f + db * db * 0.11f;
    }

    private static int Nearest(Img img, int i, (float R, float G, float B)[] pal)
    {
        var best = 0;
        var bd = float.MaxValue;
        for (var c = 0; c < pal.Length; c++)
        {
            var d = D(img, i, pal[c]);
            if (d < bd) { bd = d; best = c; }
        }
        return best;
    }

    /// <summary>Сгладить карту пятен: у каждого пикселя — самый частый номер в окне 3×3 (убирает крошку).</summary>
    private static int[] Majority(int[] label, int w, int h, int k, int passes)
    {
        var cur = label;
        for (var p = 0; p < passes; p++)
        {
            var next = new int[cur.Length];
            var src = cur;
            Parallel.For(0, h, y =>
            {
                var cnt = new int[k];
                for (var x = 0; x < w; x++)
                {
                    Array.Clear(cnt);
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            int xx = Math.Clamp(x + dx, 0, w - 1), yy = Math.Clamp(y + dy, 0, h - 1);
                            cnt[src[yy * w + xx]]++;
                        }
                    var best = src[y * w + x];
                    for (var c = 0; c < k; c++) if (cnt[c] > cnt[best]) best = c;
                    next[y * w + x] = best;
                }
            });
            cur = next;
        }
        return cur;
    }

    /// <summary>Граница пятна: 1 — у самого края (есть сосед другого цвета в радиусе r), к середине пятна — 0.</summary>
    private static float[] EdgeMap(int[] label, int w, int h, float r)
    {
        var edge = new float[w * h];
        Parallel.For(1, h - 1, y =>
        {
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                var l = label[i];
                if (label[i - 1] != l || label[i + 1] != l || label[i - w] != l || label[i + w] != l) edge[i] = 1;
            }
        });
        var soft = Box3(edge, w, h, r);
        for (var i = 0; i < soft.Length; i++) soft[i] = Math.Clamp(soft[i] * 3.2f, 0, 1);
        return soft;
    }

    private static (float[] Gx, float[] Gy, float[] Mag) Sobel(float[] lum, int w, int h)
    {
        float[] gx = new float[w * h], gy = new float[w * h], mag = new float[w * h];
        Parallel.For(1, h - 1, y =>
        {
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                float tl = lum[i - w - 1], t = lum[i - w], tr = lum[i - w + 1], l = lum[i - 1], r = lum[i + 1], bl = lum[i + w - 1], b = lum[i + w], br = lum[i + w + 1];
                gx[i] = (tr + 2 * r + br) - (tl + 2 * l + bl);
                gy[i] = (bl + 2 * b + br) - (tl + 2 * t + tr);
                mag[i] = MathF.Sqrt(gx[i] * gx[i] + gy[i] * gy[i]);
            }
        });
        return (gx, gy, mag);
    }

    private static float Percentile(float[] v, double q)
    {
        var s = v.Where(x => x > 0).ToArray();
        if (s.Length == 0) return 1;
        Array.Sort(s);
        return s[(int)(Math.Clamp(q, 0, 1) * (s.Length - 1))];
    }

    private static float Smooth(float e0, float e1, float x)
    {
        var t = Math.Clamp((x - e0) / (e1 - e0), 0, 1);
        return t * t * (3 - 2 * t);
    }

    /// <summary>Картинку для расчёта — не больше max по большей стороне.</summary>
    private static Img Work(Bitmap reference, int max, out int pw, out int ph)
    {
        var s = Math.Min(1f, max / (float)Math.Max(reference.Width, reference.Height));
        pw = Math.Max(8, (int)Math.Round(reference.Width * s));
        ph = Math.Max(8, (int)Math.Round(reference.Height * s));
        using var small = new Bitmap(pw, ph, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(reference, new Rectangle(0, 0, pw, ph));
        }
        return Img.From(small);
    }

    // ---------------- акварель ----------------

    public static List<(string, Bitmap)> Watercolor(Bitmap reference, CancellationToken ct)
    {
        int W = reference.Width, H = reference.Height;
        var img = Work(reference, 1200, out var w, out var h);
        var size = Math.Max(w, h);
        var passes = new List<(string, Bitmap)>();
        var paper = Noise(w, h, 7, size / 90f, 4);      // зерно бумаги: пигмент ложится неровно
        var bloom = Noise(w, h, 11, size / 12f, 3);     // крупные разводы воды

        // 1. лёгкий набросок карандашом — под краской он чуть просвечивает
        var lum0 = new float[w * h];
        var soft0 = img.Blurred(1.2f);
        for (var i = 0; i < lum0.Length; i++) lum0[i] = soft0.Lum(i);
        var (_, _, mag0) = Sobel(lum0, w, h);
        var t0 = Percentile(mag0, 0.9);
        passes.Add(("набросок карандашом", Up(Layer(w, h, i => mag0[i] < t0 ? (0, 0, 0, 0) : (95, 95, 105, Math.Min(0.45f, (mag0[i] - t0) / Math.Max(1, t0) * 0.35f + 0.12f))), W, H)));
        ct.ThrowIfCancellationRequested();

        // краска акварели прозрачная и чистая: цвета посочнее и светлее — слои лягут друг на друга и не замутятся
        static (float R, float G, float B) Pure(float r, float g, float b, float lighten, float sat)
        {
            var l = 0.299f * r + 0.587f * g + 0.114f * b;
            r = l + (r - l) * sat; g = l + (g - l) * sat; b = l + (b - l) * sat;
            return (255 - (255 - Math.Clamp(r, 0, 255)) * lighten, 255 - (255 - Math.Clamp(g, 0, 255)) * lighten, 255 - (255 - Math.Clamp(b, 0, 255)) * lighten);
        }

        // 2. первая заливка: сильно размытый образец, почти прозрачный, — большие мокрые пятна по белой бумаге
        var wash = img.Blurred(size / 55f);
        passes.Add(("первая заливка по-мокрому", Up(Layer(w, h, i =>
        {
            var (r, g, b) = Pure(wash.R[i], wash.G[i], wash.B[i], 0.5f, 1.2f);
            var a = 0.42f + 0.22f * bloom[i] - 0.16f * paper[i];
            return (r, g, b, Math.Clamp(a, 0.15f, 0.7f));
        }), W, H)));
        ct.ThrowIfCancellationRequested();

        // 3. цветовые пятна: палитра из 12 красок; внутри пятна — переход тона образца (акварель не бывает плоской),
        // у края пигмента чуть больше и он темнее (кайма), светлые места — бумага
        var mid = img.Blurred(size / 260f);
        var (pal, label0) = KMeans(mid, 12, 3);
        var label = Majority(label0, w, h, pal.Length, 2);
        var edge = EdgeMap(label, w, h, Math.Max(1.5f, size / 400f));
        var flat = new Img(w, h);
        for (var i = 0; i < label.Length; i++)
        {
            var p = pal[label[i]];
            flat.R[i] = p.R * 0.45f + mid.R[i] * 0.55f; flat.G[i] = p.G * 0.45f + mid.G[i] * 0.55f; flat.B[i] = p.B * 0.45f + mid.B[i] * 0.55f;
        }
        var bled = flat.Blurred(Math.Max(1.2f, size / 500f));   // затёки: цвет пятна чуть расплывается за край
        passes.Add(("цветовые пятна", Up(Layer(w, h, i =>
        {
            var (r, g, b) = Pure(bled.R[i], bled.G[i], bled.B[i], 0.9f, 1.15f);
            var lum = 0.299f * r + 0.587f * g + 0.114f * b;
            var dark = 1 - 0.12f * edge[i];
            var a = 0.44f + 0.22f * edge[i] - 0.2f * paper[i] + 0.1f * (bloom[i] - 0.5f);
            if (lum > 225) a *= 0.3f;
            return (r * dark, g * dark, b * dark, Math.Clamp(a, 0.04f, 0.78f));
        }), W, H)));
        ct.ThrowIfCancellationRequested();

        // 4. лессировка теней: где образец заметно темнее — полупрозрачный слой по тону образца
        var detail = img.Blurred(size / 700f);
        passes.Add(("лессировка теней", Up(Layer(w, h, i =>
        {
            var lp = 0.299f * bled.R[i] + 0.587f * bled.G[i] + 0.114f * bled.B[i];
            var ld = detail.Lum(i);
            var need = (lp - ld) / 110f;
            if (need <= 0.08f) return (0, 0, 0, 0);
            var (r, g, b) = Pure(detail.R[i], detail.G[i], detail.B[i], 0.95f, 1.1f);
            return (r * 0.9f, g * 0.9f, b * 0.92f, Math.Clamp(need * (0.7f - 0.25f * paper[i]), 0, 0.55f));
        }), W, H)));
        ct.ThrowIfCancellationRequested();

        // 5. детали тонкой кистью: только самые сильные контуры, краской того же тона
        var lum1 = new float[w * h];
        for (var i = 0; i < lum1.Length; i++) lum1[i] = detail.Lum(i);
        var (_, _, mag1) = Sobel(lum1, w, h);
        var t1 = Percentile(mag1, 0.95);
        passes.Add(("детали тонкой кистью", Up(Layer(w, h, i =>
        {
            if (mag1[i] < t1) return (0, 0, 0, 0);
            var a = Math.Clamp((mag1[i] - t1) / Math.Max(1, t1), 0, 1) * 0.45f;
            return (detail.R[i] * 0.6f, detail.G[i] * 0.6f, detail.B[i] * 0.65f, a);
        }), W, H)));
        return passes;
    }

    // ---------------- карандаш ----------------

    public static List<(string, Bitmap)> Pencil(Bitmap reference, CancellationToken ct)
    {
        int W = reference.Width, H = reference.Height;
        var img = Work(reference, 1500, out var w, out var h);
        var size = Math.Max(w, h);
        var soft = img.Blurred(Math.Max(1f, size / 900f));
        var tone = new float[w * h];
        for (var i = 0; i < tone.Length; i++) tone[i] = soft.Lum(i) / 255f;
        // тон растянуть: самое светлое — бумага, самое тёмное — почти чёрный грифель
        var lo = Percentile(tone.Select(t => t + 1e-4f).ToArray(), 0.03) - 1e-4f;
        var hi = Percentile(tone.Select(t => t + 1e-4f).ToArray(), 0.97) - 1e-4f;
        for (var i = 0; i < tone.Length; i++) tone[i] = Math.Clamp((tone[i] - lo) / Math.Max(0.05f, hi - lo), 0, 1);
        var wobble = Noise(w, h, 21, size / 40f, 2);
        var grain = Noise(w, h, 23, 3f, 2);
        var spacing = Math.Max(3f, size / 170f);
        var passes = new List<(string, Bitmap)>();

        // 1. контуры
        var (_, _, mag) = Sobel(tone.Select(t => t * 255).ToArray(), w, h);
        var tm = Percentile(mag, 0.88);
        passes.Add(("контуры", Up(Layer(w, h, i => mag[i] < tm ? (0, 0, 0, 0) : (50, 50, 58, Math.Clamp((mag[i] - tm) / Math.Max(1, tm) * 0.6f + 0.25f, 0, 0.85f) * (0.8f + 0.4f * grain[i]))), W, H)));
        ct.ThrowIfCancellationRequested();

        // штриховка: параллельные линии под углом; где тон темнее порога — линия проступает, линия дрожит от руки
        Bitmap Hatch(float angleDeg, float threshold, float strength, float offset)
        {
            float ca = MathF.Cos(angleDeg * MathF.PI / 180), sa = MathF.Sin(angleDeg * MathF.PI / 180);
            return Up(Layer(w, h, i =>
            {
                int x = i % w, y = i / w;
                var need = Smooth(threshold, threshold - 0.18f, tone[i]);
                if (need <= 0.01f) return (0, 0, 0, 0);
                var u = x * ca + y * sa + (wobble[i] - 0.5f) * spacing * 1.6f + offset;
                var d = u / spacing;
                var frac = d - MathF.Floor(d);
                var line = MathF.Max(0, 1 - MathF.Abs(frac - 0.5f) * 2 / 0.42f);   // профиль линии
                // каждая линия своей силы нажима
                var lineId = (int)MathF.Floor(d);
                var press = 0.65f + 0.35f * (((lineId * 73856093) ^ 19349663) & 1023) / 1023f;
                var a = line * need * strength * press * (0.8f + 0.4f * grain[i]);
                return a <= 0.01f ? (0, 0, 0, 0) : (55, 55, 63, Math.Min(0.85f, a));
            }), W, H);
        }
        // светлое (выше 0,62) — чистая бумага: штрихуются только полутени и тени
        passes.Add(("первая штриховка", Hatch(45, 0.62f, 0.55f, 0)));
        ct.ThrowIfCancellationRequested();
        passes.Add(("перекрёстная штриховка", Hatch(-45, 0.42f, 0.6f, spacing * 0.37f)));
        ct.ThrowIfCancellationRequested();
        passes.Add(("густые тени", Hatch(8, 0.24f, 0.7f, spacing * 0.61f)));
        return passes;
    }

    // ---------------- тушь / комикс ----------------

    public static List<(string, Bitmap)> Ink(Bitmap reference, CancellationToken ct)
    {
        int W = reference.Width, H = reference.Height;
        var img = Work(reference, 1400, out var w, out var h);
        var size = Math.Max(w, h);
        var smooth = img.Blurred(Math.Max(1.2f, size / 450f));
        var (pal, label0) = KMeans(smooth, 9, 5);
        var label = Majority(label0, w, h, pal.Length, 3);
        // цвета посочнее — плакатная заливка
        var vivid = pal.Select(p =>
        {
            var l = 0.299f * p.R + 0.587f * p.G + 0.114f * p.B;
            return (R: Math.Clamp(l + (p.R - l) * 1.25f, 0, 255), G: Math.Clamp(l + (p.G - l) * 1.25f, 0, 255), B: Math.Clamp(l + (p.B - l) * 1.25f, 0, 255));
        }).ToArray();
        var passes = new List<(string, Bitmap)>
        {
            ("плоские цвета", Up(Layer(w, h, i => { var p = vivid[label[i]]; return (p.R, p.G, p.B, 1f); }), W, H)),
        };
        ct.ThrowIfCancellationRequested();
        // тени: где образец темнее плоского цвета — штриховой тенью (полупрозрачный тёмный)
        var detail = img.Blurred(Math.Max(1f, size / 600f));
        passes.Add(("тени", Up(Layer(w, h, i =>
        {
            var p = vivid[label[i]];
            var need = ((0.299f * p.R + 0.587f * p.G + 0.114f * p.B) - detail.Lum(i)) / 60f;
            return need < 0.25f ? (0, 0, 0, 0) : (p.R * 0.45f, p.G * 0.45f, p.B * 0.55f, Math.Clamp(need, 0, 1) * 0.55f);
        }), W, H)));
        ct.ThrowIfCancellationRequested();
        // контур тушью: границы пятен + сильные перепады образца, толщина — по силе
        var edge = EdgeMap(label, w, h, Math.Max(0.8f, size / 1000f));
        var lum = new float[w * h];
        for (var i = 0; i < lum.Length; i++) lum[i] = detail.Lum(i);
        var (_, _, mag) = Sobel(lum, w, h);
        var tm = Percentile(mag, 0.9);
        passes.Add(("контур тушью", Up(Layer(w, h, i =>
        {
            var a = Math.Max(Smooth(0.35f, 0.75f, edge[i]), Smooth(tm, tm * 2.2f, mag[i]));
            return a < 0.05f ? (0, 0, 0, 0) : (12, 10, 18, a);
        }), W, H)));
        return passes;
    }

    // ---------------- пиксель-арт ----------------

    public static List<(string, Bitmap)> Pixel(Bitmap reference, CancellationToken ct)
    {
        int W = reference.Width, H = reference.Height;
        // пиксель ~1/120 ширины: картинка 120×… клеток
        var cells = 128;
        var img = Work(reference, cells, out var w, out var h);
        var (pal, label) = KMeans(img, 20, 9);
        ct.ThrowIfCancellationRequested();
        var flat = Layer(w, h, i => { var p = pal[label[i]]; return (p.R, p.G, p.B, 1f); });
        // обводка: пиксель темнее соседа сверху/слева на палитру — тёмная кромка, как в спрайтах
        var outline = Layer(w, h, i =>
        {
            int x = i % w, y = i / w;
            if (x == 0 || y == 0) return (0, 0, 0, 0);
            var l = label[i];
            var diff = label[i - 1] != l || label[i - w] != l;
            if (!diff) return (0, 0, 0, 0);
            var p = pal[l];
            return (p.R * 0.55f, p.G * 0.55f, p.B * 0.6f, 0.55f);
        });
        return [("пиксели", Up(flat, W, H, pixelated: true)), ("кромки", Up(outline, W, H, pixelated: true))];
    }

    // ---------------- реализм: лессировка деталей поверх мазков ----------------

    /// <summary>Последний проход реализма: сам образец полупрозрачно поверх мазков — детали лица, окон, листвы.</summary>
    public static Bitmap Glaze(Bitmap reference, float alpha)
    {
        var bmp = new Bitmap(reference.Width, reference.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
        g.DrawImage(reference, new Rectangle(0, 0, bmp.Width, bmp.Height), 0, 0, reference.Width, reference.Height, GraphicsUnit.Pixel, attrs);
        return bmp;
    }
}
