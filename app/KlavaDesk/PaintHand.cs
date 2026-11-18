using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace KlavaDesk;

/// <summary>
/// Академический рисунок карандашом «рукой». Друзья 28.09 разнесли paint_art в карандаше: «ты не используешь инструмент
/// карандаша», «опять стиль комикса», «мазня». Он вставлял готовые слои из буфера обмена — на экране проявлялись куски
/// гравюры: жирный контур по Собелю и параллельная штриховка под 45° по всему листу, без формы; образец подрезался под
/// широкий холст, и яблоко вылезало за края. Здесь по образцу строится план штрихов, как учат в художке:
///  1) построение — габарит, оси, вписанный овал тонкой светлой линией;
///  2) лёгкий контур — только значимые края (подавление немаксимумов, гистерезис), без комиксовой жирности;
///  3) тон штрихом по форме, от светлого к тёмному: каждый тон — свой нажим (цвет графита) и своё направление поверх
///     формы (перекрёстно), штрих идёт вдоль линии равного тона (структурный тензор) — по шару он огибает шар; где формы
///     нет — привычной диагональю правой руки;
///  4) акценты в самых тёмных местах и уточнение контура с теневой стороны; блик — чистая бумага.
/// Образец вписывается в лист целиком, с полями, бумага образца (серая, с виньеткой) приводится к белой.
/// </summary>
internal static class PencilPlan
{
    /// <param name="Color">#RRGGBB — «нажим»: у инструмента «Карандаш» Paint нет прозрачности, тон — цветом графита.</param>
    internal sealed record Pass(string Name, string Color, int Size, List<Point[]> Strokes);

    private sealed record Level(string Name, string Color, float Threshold, float Spacing, float AngleDeg, float Length, int Size);

    /// <summary>План рисунка на листе W×H (пиксели экрана = пиксели холста при 100 %). detail: fast | fine | обычно.</summary>
    public static List<Pass> Make(Bitmap reference, int W, int H, string? detail, int seed = 7)
    {
        var dens = detail switch { "fast" => 1.35f, "fine" => 0.85f, _ => 1f };
        var tone = Prepare(reference, W, H);
        var t1 = Blur(tone, W, H, 1.2f);
        var rnd = new Random(seed);
        var passes = new List<Pass>();

        var box = SubjectBox(t1, W, H);
        if (box is { } b) passes.Add(new Pass("построение: габарит, оси, овал", "#C8C8C8", 1, Construction(b, rnd)));

        // контур — по сильнее размытому тону: фактура бумаги и штрихи образца не должны давать зазубрин
        var (edges, dark) = Contours(Blur(tone, W, H, Math.Max(2f, Math.Min(W, H) / 330f)), W, H);
        passes.Add(new Pass("лёгкий контур", "#9A9A9A", 2, Order(edges, W)));

        var (fx, fy) = FormField(tone, W, H);
        var size = Math.Min(W, H);
        var baseLen = size * 0.075f;
        // тон образца, каким его видит глаз с расстояния — с ним сверяется нарисованное
        var target = Blur(tone, W, H, 3f);
        Level[] levels =
        [
            new("первый тон — штрих по форме", "#BEBEBE", 0.88f, 6.0f, 0, baseLen * 1.15f, 2),
            new("полутень — перекрёстный штрих", "#9E9E9E", 0.70f, 6.5f, 42, baseLen, 3),
            new("собственная тень", "#7A7A7A", 0.52f, 6.0f, -38, baseLen * 0.95f, 3),
            new("глубокие и падающие тени", "#565656", 0.36f, 5.5f, 80, baseLen * 0.85f, 3),
            new("акценты", "#363636", 0.22f, 5.0f, 12, baseLen * 0.6f, 3),
        ];
        foreach (var lv in levels)
        {
            if (detail == "fast" && lv.Threshold < 0.25f) continue;
            // как художник: отошла, сравнила с натурой — штрихую только там, где ещё светлее образца; не хватило —
            // тот же нажим ещё раз под другим углом (не больше трёх заходов на тон)
            var strokes = new List<Point[]>();
            for (var round = 0; round < 2; round++)
            {
                var now = Seen(passes, strokes, lv, W, H);
                var need = new float[now.Length];
                var lighter = 0;
                var inside = 0;
                for (var i = 0; i < need.Length; i++)
                {
                    need[i] = now[i] - target[i];
                    if (target[i] < lv.Threshold) { inside++; if (need[i] > 0.08f) lighter++; }
                }
                if (inside == 0 || lighter < inside * 0.08) break;
                var turn = lv with { AngleDeg = lv.AngleDeg + round * 55 };
                var got = Hatch(t1, need, fx, fy, W, H, turn, dens, box, rnd);
                if (got.Count == 0) break;
                strokes.AddRange(got);
            }
            if (strokes.Count > 0) passes.Add(new Pass(lv.Name, lv.Color, lv.Size, Order(strokes, W)));
        }
        if (dark.Count > 0) passes.Add(new Pass("уточнение контура с теневой стороны", "#474747", 2, Order(dark, W)));
        return passes;
    }

    /// <summary>Что уже на листе (все проходы + штрихи текущего тона), размыто как на расстоянии: 0 — грифель, 1 — бумага.</summary>
    private static float[] Seen(List<Pass> passes, List<Point[]> current, Level lv, int W, int H)
    {
        var all = new List<Pass>(passes) { new(lv.Name, lv.Color, lv.Size, current) };
        using var bmp = Render(all, W, H);
        using var argb = bmp.Clone(new Rectangle(0, 0, W, H), PixelFormat.Format32bppArgb);
        return Blur(Lum(argb), W, H, 3f);
    }

    // ---------------- образец → тон листа ----------------

    /// <summary>Тон листа 0 (грифель) … 1 (бумага): образец вписан целиком с полями, его бумага приведена к белой.</summary>
    private static float[] Prepare(Bitmap reference, int W, int H)
    {
        var tone = new float[W * H];
        Array.Fill(tone, 1f);
        float mx = W * 0.06f, my = H * 0.07f;
        var k = Math.Min((W - 2 * mx) / reference.Width, (H - 2 * my) / reference.Height);
        int rw = Math.Max(8, (int)Math.Round(reference.Width * k)), rh = Math.Max(8, (int)Math.Round(reference.Height * k));
        int ox = (W - rw) / 2, oy = (H - rh) / 2;
        using var fitted = new Bitmap(rw, rh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(fitted))
        {
            g.Clear(Color.White);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(reference, 0, 0, rw, rh);
        }
        var lum = Lum(fitted);
        // бумага образца — медиана полосы по краю (серый лист фотографии, виньетка): она станет белой
        var bw = Math.Max(2, Math.Min(rw, rh) / 25);
        var ring = new List<float>();
        for (var y = 0; y < rh; y++)
            for (var x = 0; x < rw; x++)
                if (x < bw || y < bw || x >= rw - bw || y >= rh - bw) ring.Add(lum[y * rw + x]);
        var paper = Math.Max(0.35f, Percentile([.. ring], 0.5));
        for (var i = 0; i < lum.Length; i++) lum[i] = Math.Clamp(lum[i] / paper, 0, 1);
        var black = Percentile(lum, 0.01);
        for (var i = 0; i < lum.Length; i++)
        {
            var t = Math.Clamp((lum[i] - black) / Math.Max(0.2f, 1 - black), 0, 1);
            lum[i] = t > 0.94f ? 1f : t;
        }
        for (var y = 0; y < rh; y++) Array.Copy(lum, y * rw, tone, (oy + y) * W + ox, rw);
        return tone;
    }

    private static float[] Lum(Bitmap bmp)
    {
        var r = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[data.Stride * bmp.Height];
            Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            var lum = new float[bmp.Width * bmp.Height];
            for (var y = 0; y < bmp.Height; y++)
                for (var x = 0; x < bmp.Width; x++)
                {
                    var o = y * data.Stride + x * 4;
                    var a = bytes[o + 3] / 255f;   // прозрачное — бумага
                    var l = (0.114f * bytes[o] + 0.587f * bytes[o + 1] + 0.299f * bytes[o + 2]) / 255f;
                    lum[y * bmp.Width + x] = l * a + (1 - a);
                }
            return lum;
        }
        finally { bmp.UnlockBits(data); }
    }

    // ---------------- построение ----------------

    /// <summary>Габарит предмета (всё, что темнее бумаги), без одиночных соринок по краям; null — пустой лист.</summary>
    private static RectangleF? SubjectBox(float[] t, int W, int H)
    {
        var xs = new List<float>();
        var ys = new List<float>();
        for (var y = 0; y < H; y += 2)
            for (var x = 0; x < W; x += 2)
                if (t[y * W + x] < 0.88f) { xs.Add(x); ys.Add(y); }
        if (xs.Count < W * H / 4 / 200) return null;
        float x0 = Percentile([.. xs], 0.005), x1 = Percentile([.. xs], 0.995);
        float y0 = Percentile([.. ys], 0.005), y1 = Percentile([.. ys], 0.995);
        return x1 - x0 < 10 || y1 - y0 < 10 ? null : RectangleF.FromLTRB(x0, y0, x1, y1);
    }

    /// <summary>Как на первом листе в художке: габарит чуть за углы, оси через центр, овал, вписанный в габарит.</summary>
    private static List<Point[]> Construction(RectangleF b, Random rnd)
    {
        var s = new List<Point[]>();
        float ex = b.Width * 0.05f, ey = b.Height * 0.05f;
        Point P(float x, float y) => new((int)Math.Round(x + (rnd.NextSingle() - 0.5f) * 1.5f), (int)Math.Round(y + (rnd.NextSingle() - 0.5f) * 1.5f));
        s.Add([P(b.Left - ex, b.Top), P(b.Right + ex, b.Top)]);
        s.Add([P(b.Right, b.Top - ey), P(b.Right, b.Bottom + ey)]);
        s.Add([P(b.Right + ex, b.Bottom), P(b.Left - ex, b.Bottom)]);
        s.Add([P(b.Left, b.Bottom + ey), P(b.Left, b.Top - ey)]);
        float cx = (b.Left + b.Right) / 2, cy = (b.Top + b.Bottom) / 2;
        s.Add([P(cx, b.Top - ey * 1.6f), P(cx, b.Bottom + ey * 1.6f)]);
        s.Add([P(b.Left - ex * 1.6f, cy), P(b.Right + ex * 1.6f, cy)]);
        // овал двумя дугами внахлёст, радиус чуть «гуляет» — рука, а не циркуль
        for (var arc = 0; arc < 2; arc++)
        {
            var a0 = -MathF.PI / 2 + arc * MathF.PI - 0.15f;
            var pts = new List<Point>();
            for (var i = 0; i <= 28; i++)
            {
                var a = a0 + (MathF.PI + 0.3f) * i / 28;
                var wob = 1 + (rnd.NextSingle() - 0.5f) * 0.012f;
                pts.Add(P(cx + MathF.Cos(a) * b.Width / 2 * wob, cy + MathF.Sin(a) * b.Height / 2 * wob));
            }
            s.Add([.. pts]);
        }
        return s;
    }

    // ---------------- контур ----------------

    /// <summary>
    /// Значимые края: градиент тона, подавление немаксимумов, гистерезис, цепочки из соседних пикселей. Возвращает
    /// лёгкий контур (все значимые) и тёмный (где рядом глубокая тень — «уточнение с теневой стороны»).
    /// </summary>
    private static (List<Point[]> Light, List<Point[]> Dark) Contours(float[] t, int W, int H)
    {
        var (gx, gy) = Sobel(t, W, H);
        var mag = new float[W * H];
        for (var i = 0; i < mag.Length; i++) mag[i] = MathF.Sqrt(gx[i] * gx[i] + gy[i] * gy[i]);
        var nms = new float[W * H];
        for (var y = 1; y < H - 1; y++)
            for (var x = 1; x < W - 1; x++)
            {
                var i = y * W + x;
                if (mag[i] < 0.02f) continue;
                var ang = MathF.Atan2(gy[i], gx[i]) * 180 / MathF.PI;
                if (ang < 0) ang += 180;
                int dx, dy;
                if (ang < 22.5f || ang >= 157.5f) (dx, dy) = (1, 0);
                else if (ang < 67.5f) (dx, dy) = (1, 1);
                else if (ang < 112.5f) (dx, dy) = (0, 1);
                else (dx, dy) = (-1, 1);
                if (mag[i] >= mag[i + dy * W + dx] && mag[i] >= mag[i - dy * W - dx]) nms[i] = mag[i];
            }
        var vals = nms.Where(v => v > 0).ToArray();
        if (vals.Length == 0) return ([], []);
        var hi = Percentile(vals, 0.9);
        var lo = hi * 0.45f;
        // гистерезис: слабые края живут, только если связаны с сильными
        var edge = new bool[W * H];
        var stack = new Stack<int>();
        for (var i = 0; i < nms.Length; i++)
            if (nms[i] >= hi && !edge[i]) { edge[i] = true; stack.Push(i); }
        while (stack.Count > 0)
        {
            var i = stack.Pop();
            int x = i % W, y = i / W;
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                    var j = ny * W + nx;
                    if (!edge[j] && nms[j] >= lo) { edge[j] = true; stack.Push(j); }
                }
        }
        // цепочки: от концов (у кого один сосед), потом всё остальное
        var used = new bool[W * H];
        int Neighbours(int i)
        {
            int x = i % W, y = i / W, n = 0;
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int nx = x + dx, ny = y + dy;
                    if (nx >= 0 && ny >= 0 && nx < W && ny < H && edge[ny * W + nx]) n++;
                }
            return n;
        }
        var chains = new List<List<Point>>();
        void Walk(int start)
        {
            var chain = new List<Point>();
            var cur = start;
            int pdx = 0, pdy = 0;
            while (true)
            {
                used[cur] = true;
                int x = cur % W, y = cur / W;
                chain.Add(new Point(x, y));
                int best = -1, bdx = 0, bdy = 0, bscore = int.MinValue;
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= W || ny >= H) continue;
                        var j = ny * W + nx;
                        if (!edge[j] || used[j]) continue;
                        var score = dx * pdx + dy * pdy;   // держать направление
                        if (score > bscore) { bscore = score; best = j; bdx = dx; bdy = dy; }
                    }
                if (best < 0) break;
                (pdx, pdy) = (bdx, bdy);
                cur = best;
            }
            if (chain.Count >= 22) chains.Add(chain);
        }
        for (var i = 0; i < edge.Length; i++)
            if (edge[i] && !used[i] && Neighbours(i) == 1) Walk(i);
        for (var i = 0; i < edge.Length; i++)
            if (edge[i] && !used[i]) Walk(i);
        chains.Sort((a, b) => b.Count.CompareTo(a.Count));
        var budget = 3 * (W + H);
        var light = new List<Point[]>();
        var dark = new List<Point[]>();
        var total = 0;
        foreach (var c in chains)
        {
            if (total > budget) break;
            total += c.Count;
            var simple = Rdp(c, 0.9f);
            // тон по обе стороны края: самое тёмное рядом — глубокая тень → контур уточняется и с теневой стороны
            var shade = c.Where((_, k) => k % 4 == 0).Select(p => MinAround(t, W, H, p.X, p.Y, 3)).DefaultIfEmpty(1).Average();
            foreach (var seg in Split(simple, 70))
            {
                light.Add(seg);
                if (shade < 0.4f) dark.Add(seg);
            }
        }
        return (light, dark);
    }

    private static float MinAround(float[] t, int W, int H, int x, int y, int r)
    {
        var m = 1f;
        for (var dy = -r; dy <= r; dy += r)
            for (var dx = -r; dx <= r; dx += r)
            {
                int nx = Math.Clamp(x + dx, 0, W - 1), ny = Math.Clamp(y + dy, 0, H - 1);
                m = Math.Min(m, t[ny * W + nx]);
            }
        return m;
    }

    /// <summary>Рамер — Дуглас — Пекер: ломаная из цепочки пикселей.</summary>
    private static List<Point> Rdp(List<Point> pts, float eps)
    {
        if (pts.Count < 3) return pts;
        var keep = new bool[pts.Count];
        keep[0] = keep[^1] = true;
        var st = new Stack<(int A, int B)>();
        st.Push((0, pts.Count - 1));
        while (st.Count > 0)
        {
            var (a, b) = st.Pop();
            float ax = pts[a].X, ay = pts[a].Y, bx = pts[b].X, by = pts[b].Y;
            var len = MathF.Max(1e-3f, MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)));
            int far = -1;
            var fd = eps;
            for (var i = a + 1; i < b; i++)
            {
                var d = MathF.Abs((bx - ax) * (ay - pts[i].Y) - (ax - pts[i].X) * (by - ay)) / len;
                if (d > fd) { fd = d; far = i; }
            }
            if (far < 0) continue;
            keep[far] = true;
            st.Push((a, far));
            st.Push((far, b));
        }
        return pts.Where((_, i) => keep[i]).ToList();
    }

    /// <summary>Длинную линию — на штрихи до maxLen пикселей (рука не ведёт весь контур одним движением).</summary>
    private static IEnumerable<Point[]> Split(List<Point> pts, float maxLen)
    {
        var cur = new List<Point> { pts[0] };
        float acc = 0;
        for (var i = 1; i < pts.Count; i++)
        {
            float dx = pts[i].X - pts[i - 1].X, dy = pts[i].Y - pts[i - 1].Y;
            acc += MathF.Sqrt(dx * dx + dy * dy);
            cur.Add(pts[i]);
            if (acc >= maxLen && i < pts.Count - 1)
            {
                yield return [.. cur];
                cur = [pts[i]];
                acc = 0;
            }
        }
        if (cur.Count >= 2) yield return [.. cur];
    }

    // ---------------- штриховка по форме ----------------

    /// <summary>
    /// Направление штриха в каждой точке (единичный вектор): вдоль линии равного тона — структурный тензор крупно
    /// размытого тона, так штрих огибает форму; где формы нет — диагональ «/» правой руки.
    /// </summary>
    private static (float[] Fx, float[] Fy) FormField(float[] tone, int W, int H)
    {
        var size = Math.Min(W, H);
        var tb = Blur(tone, W, H, size / 70f);
        var (gx, gy) = Sobel(tb, W, H);
        var j11 = new float[W * H];
        var j22 = new float[W * H];
        var j12 = new float[W * H];
        for (var i = 0; i < j11.Length; i++) { j11[i] = gx[i] * gx[i]; j22[i] = gy[i] * gy[i]; j12[i] = gx[i] * gy[i]; }
        j11 = Blur(j11, W, H, size / 45f);
        j22 = Blur(j22, W, H, size / 45f);
        j12 = Blur(j12, W, H, size / 45f);
        var str = new float[W * H];
        for (var i = 0; i < str.Length; i++) str[i] = MathF.Sqrt((j11[i] - j22[i]) * (j11[i] - j22[i]) + 4 * j12[i] * j12[i]);
        var k = Math.Max(1e-6f, Percentile(str.Where(v => v > 1e-7f).DefaultIfEmpty(1e-6f).ToArray(), 0.4));
        float c0 = MathF.Cos(2 * -MathF.PI / 4), s0 = MathF.Sin(2 * -MathF.PI / 4);   // «/» в удвоенных углах
        var fx = new float[W * H];
        var fy = new float[W * H];
        for (var i = 0; i < fx.Length; i++)
        {
            // ориентация градиента в удвоенных углах; касательная к линии тона — плюс 90°
            var phi2 = MathF.Atan2(2 * j12[i], j11[i] - j22[i]);
            var tan2 = phi2 + MathF.PI;
            var vx = str[i] * MathF.Cos(tan2) + k * c0;
            var vy = str[i] * MathF.Sin(tan2) + k * s0;
            var th = 0.5f * MathF.Atan2(vy, vx);
            fx[i] = MathF.Cos(th);
            fy[i] = MathF.Sin(th);
        }
        return (fx, fy);
    }

    /// <summary>
    /// Штрихи одного тона: затравки по сетке вразброс, от каждой — линия тока поля формы (с поворотом на угол слоя),
    /// пока тон темнее порога, не упёрлась в соседний штрих и не вышла длина. Соседство — по сетке занятости.
    /// </summary>
    private static List<Point[]> Hatch(float[] t, float[] need, float[] fx, float[] fy, int W, int H, Level lv, float dens, RectangleF? box, Random rnd)
    {
        var sp = lv.Spacing * dens;
        var cs = sp;   // клетка занятости
        int gw = (int)(W / cs) + 1, gh = (int)(H / cs) + 1;
        var occ = new bool[gw * gh];
        var off = lv.AngleDeg * MathF.PI / 180;
        float co = MathF.Cos(off), so = MathF.Sin(off);
        var area = box is { } b ? RectangleF.Inflate(b, b.Width * 0.08f, b.Height * 0.08f) : new RectangleF(0, 0, W, H);
        area.Intersect(new RectangleF(2, 2, W - 4, H - 4));
        var seeds = new List<PointF>();
        for (var y = area.Top; y < area.Bottom; y += sp)
            for (var x = area.Left; x < area.Right; x += sp)
                seeds.Add(new PointF(x + rnd.NextSingle() * sp, y + rnd.NextSingle() * sp));
        for (var i = seeds.Count - 1; i > 0; i--) { var j = rnd.Next(i + 1); (seeds[i], seeds[j]) = (seeds[j], seeds[i]); }

        float T(float x, float y) => t[Math.Clamp((int)y, 0, H - 1) * W + Math.Clamp((int)x, 0, W - 1)];
        float Need(float x, float y) => need[Math.Clamp((int)y, 0, H - 1) * W + Math.Clamp((int)x, 0, W - 1)];
        int Cell(float x, float y) => Math.Clamp((int)(y / cs), 0, gh - 1) * gw + Math.Clamp((int)(x / cs), 0, gw - 1);
        (float X, float Y) Dir(float x, float y)
        {
            var i = Math.Clamp((int)y, 0, H - 1) * W + Math.Clamp((int)x, 0, W - 1);
            return (fx[i] * co - fy[i] * so, fx[i] * so + fy[i] * co);
        }
        List<PointF> Side(PointF p0, float sx, float sy, float maxLen, HashSet<int> own)
        {
            var pts = new List<PointF>();
            float x = p0.X, y = p0.Y, px = sx, py = sy, len = 0;
            const float ds = 1.5f;
            while (len < maxLen)
            {
                var (dx, dy) = Dir(x, y);
                if (dx * px + dy * py < 0) { dx = -dx; dy = -dy; }
                if (dx * px + dy * py < 0.75f) break;   // крутой поворот — штрих кончился
                x += dx * ds;
                y += dy * ds;
                len += ds;
                if (x < 2 || y < 2 || x >= W - 2 || y >= H - 2) break;
                if (T(x, y) > lv.Threshold + 0.04f || Need(x, y) < 0) break;
                var c = Cell(x, y);
                if (occ[c] && !own.Contains(c)) break;
                own.Add(c);
                pts.Add(new PointF(x, y));
                (px, py) = (dx, dy);
            }
            return pts;
        }

        var result = new List<Point[]>();
        foreach (var s in seeds)
        {
            if (T(s.X, s.Y) > lv.Threshold - 0.02f || Need(s.X, s.Y) < 0.08f || occ[Cell(s.X, s.Y)]) continue;
            var maxLen = lv.Length * (0.7f + 0.6f * rnd.NextSingle());
            var own = new HashSet<int> { Cell(s.X, s.Y) };
            var (dx, dy) = Dir(s.X, s.Y);
            var fwd = Side(s, dx, dy, maxLen / 2, own);
            var back = Side(s, -dx, -dy, maxLen / 2, own);
            back.Reverse();
            back.Add(s);
            back.AddRange(fwd);
            float len = 0;
            for (var i = 1; i < back.Count; i++)
                len += MathF.Sqrt((back[i].X - back[i - 1].X) * (back[i].X - back[i - 1].X) + (back[i].Y - back[i - 1].Y) * (back[i].Y - back[i - 1].Y));
            if (len < Math.Max(5f, lv.Length * 0.25f)) continue;
            foreach (var c in own) occ[c] = true;
            // точки реже — через ~5 px, концы на месте
            var pts = new List<Point> { Point.Round(back[0]) };
            float acc = 0;
            for (var i = 1; i < back.Count; i++)
            {
                acc += MathF.Sqrt((back[i].X - back[i - 1].X) * (back[i].X - back[i - 1].X) + (back[i].Y - back[i - 1].Y) * (back[i].Y - back[i - 1].Y));
                if (acc >= 5 || i == back.Count - 1) { pts.Add(Point.Round(back[i])); acc = 0; }
            }
            if (pts.Count >= 2) result.Add([.. pts]);
        }
        return result;
    }

    /// <summary>
    /// Порядок, как у руки: лист по клеткам змейкой (рука работает участком, а не прыгает по листу), в клетке — от
    /// ближайшего к ближайшему; штрих может идти в обратную сторону, если так ближе.
    /// </summary>
    private static List<Point[]> Order(List<Point[]> strokes, int W, int cell = 110)
    {
        var cols = W / cell + 1;
        var groups = strokes.GroupBy(s =>
        {
            var m = s[s.Length / 2];
            int cx = m.X / cell, cy = m.Y / cell;
            return cy * cols + (cy % 2 == 0 ? cx : cols - 1 - cx);
        }).OrderBy(g => g.Key);
        var result = new List<Point[]>(strokes.Count);
        var pen = new Point(0, 0);
        foreach (var g in groups)
        {
            var left = g.ToList();
            while (left.Count > 0)
            {
                var bi = 0;
                var brev = false;
                var bd = long.MaxValue;
                for (var i = 0; i < left.Count; i++)
                {
                    long d0 = D2(pen, left[i][0]), d1 = D2(pen, left[i][^1]);
                    if (d0 < bd) { bd = d0; bi = i; brev = false; }
                    if (d1 < bd) { bd = d1; bi = i; brev = true; }
                }
                var s = left[bi];
                left.RemoveAt(bi);
                if (brev) s = s.Reverse().ToArray();
                result.Add(s);
                pen = s[^1];
            }
        }
        return result;
    }

    private static long D2(Point a, Point b) => (long)(a.X - b.X) * (a.X - b.X) + (long)(a.Y - b.Y) * (a.Y - b.Y);

    // ---------------- поля ----------------

    /// <summary>Размытие, близкое к гауссу: три прохода скользящего среднего по строкам и столбцам.</summary>
    private static float[] Blur(float[] src, int W, int H, float sigma)
    {
        if (sigma < 0.5f) return (float[])src.Clone();
        var r = Math.Max(1, (int)Math.Round((MathF.Sqrt(4 * sigma * sigma + 1) - 1) / 2));
        var a = (float[])src.Clone();
        var b = new float[a.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxH(a, b, W, H, r);
            BoxV(b, a, W, H, r);
        }
        return a;
    }

    private static void BoxH(float[] s, float[] d, int W, int H, int r)
    {
        var n = 2 * r + 1;
        Parallel.For(0, H, y =>
        {
            var row = y * W;
            float acc = 0;
            for (var k = -r; k <= r; k++) acc += s[row + Math.Clamp(k, 0, W - 1)];
            for (var x = 0; x < W; x++)
            {
                d[row + x] = acc / n;
                acc += s[row + Math.Min(W - 1, x + r + 1)] - s[row + Math.Max(0, x - r)];
            }
        });
    }

    private static void BoxV(float[] s, float[] d, int W, int H, int r)
    {
        var n = 2 * r + 1;
        Parallel.For(0, W, x =>
        {
            float acc = 0;
            for (var k = -r; k <= r; k++) acc += s[Math.Clamp(k, 0, H - 1) * W + x];
            for (var y = 0; y < H; y++)
            {
                d[y * W + x] = acc / n;
                acc += s[Math.Min(H - 1, y + r + 1) * W + x] - s[Math.Max(0, y - r) * W + x];
            }
        });
    }

    private static (float[] Gx, float[] Gy) Sobel(float[] v, int W, int H)
    {
        var gx = new float[W * H];
        var gy = new float[W * H];
        Parallel.For(1, H - 1, y =>
        {
            for (var x = 1; x < W - 1; x++)
            {
                var i = y * W + x;
                gx[i] = (v[i - W + 1] + 2 * v[i + 1] + v[i + W + 1] - v[i - W - 1] - 2 * v[i - 1] - v[i + W - 1]) / 8;
                gy[i] = (v[i + W - 1] + 2 * v[i + W] + v[i + W + 1] - v[i - W - 1] - 2 * v[i - W] - v[i - W + 1]) / 8;
            }
        });
        return (gx, gy);
    }

    private static float Percentile(float[] v, double q)
    {
        if (v.Length == 0) return 0;
        var c = (float[])v.Clone();
        Array.Sort(c);
        return c[Math.Clamp((int)(q * (c.Length - 1)), 0, c.Length - 1)];
    }

    /// <summary>Как план ляжет на белый лист: карандаш Paint — без сглаживания, толщина в пикселях, цвет-нажим.</summary>
    public static Bitmap Render(List<Pass> passes, int W, int H, int upto = int.MaxValue)
    {
        var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.None;
        foreach (var p in passes.Take(upto))
        {
            using var pen = new Pen(ColorTranslator.FromHtml(p.Color), p.Size) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            foreach (var s in p.Strokes)
                if (s.Length >= 2) g.DrawLines(pen, s);
        }
        return bmp;
    }

    /// <summary>
    /// KlavaDesk.exe --paint-hand src=образец.png [out=папка] [fast|fine] [W=1554 H=741] — план без Paint: кадр после
    /// каждого прохода и сколько штрихов (глазами проверить рисунок до того, как лапы пойдут по холсту).
    /// </summary>
    public static int Test(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string? Arg(string k) => args.FirstOrDefault(a => a.StartsWith(k + "=", StringComparison.Ordinal))?[(k.Length + 1)..];
        var src = Arg("src");
        if (src is null || !File.Exists(src)) { Console.WriteLine("нужен src=образец.png"); return 2; }
        var outDir = Arg("out") ?? Path.Combine(Path.GetTempPath(), "paint_hand");
        Directory.CreateDirectory(outDir);
        int W = int.TryParse(Arg("W"), out var w) ? w : 1554, H = int.TryParse(Arg("H"), out var h) ? h : 741;
        var detail = args.Contains("fast") ? "fast" : args.Contains("fine") ? "fine" : null;
        using var reference = new Bitmap(src);
        var sw = Stopwatch.StartNew();
        var passes = Make(reference, W, H, detail);
        Console.WriteLine($"план за {sw.ElapsedMilliseconds} мс, лист {W}×{H}");
        var strokes = 0;
        var points = 0;
        for (var i = 0; i < passes.Count; i++)
        {
            var p = passes[i];
            strokes += p.Strokes.Count;
            points += p.Strokes.Sum(s => s.Length);
            Console.WriteLine($"{i + 1}. {p.Name}: {p.Strokes.Count} штрихов, {p.Color}, {p.Size} px");
            using var frame = Render(passes, W, H, i + 1);
            frame.Save(Path.Combine(outDir, $"{i + 1}_{p.Name.Split(' ')[0].TrimEnd(':', ',')}.png"), ImageFormat.Png);
        }
        Console.WriteLine($"всего {strokes} штрихов, {points} точек; кадры: {outDir}");
        // похож ли тон: по корзинам тона образца — средний тон нарисованного (оба размыты, как с расстояния)
        var target = Blur(Prepare(reference, W, H), W, H, 3f);
        using var done = Render(passes, W, H);
        using var argb = done.Clone(new Rectangle(0, 0, W, H), PixelFormat.Format32bppArgb);
        var seen = Blur(Lum(argb), W, H, 3f);
        var line = new StringBuilder("тон образца → нарисовано:");
        for (var b = 0; b < 5; b++)
        {
            float lo = b * 0.2f, hi = lo + 0.2f, sum = 0;
            var n = 0;
            for (var i = 0; i < target.Length; i++)
                if (target[i] >= lo && target[i] < hi) { sum += seen[i]; n++; }
            if (n > 0) line.Append($"  {lo:F1}–{hi:F1}: {sum / n:F2}");
        }
        Console.WriteLine(line);
        return 0;
    }
}

internal static partial class PaintTool
{
    /// <summary>
    /// Рисунок карандашом рукой: чистый лист (новый слой залит белым — старое остаётся в слоях под ним), план штрихов
    /// PencilPlan под видимый холст, инструмент «Карандаш», на каждый проход — свой цвет-нажим и толщина, штрихи мышью.
    /// </summary>
    private static async Task HandDrawAsync(IntPtr win, Canvas cv, Bitmap reference, string? detail, bool paper, int layerW, int layerH,
        Hands h, StringBuilder log, Stopwatch clock)
    {
        var c = cv.Visible;
        if (paper)
        {
            using var white = new Bitmap(layerW, layerH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(white)) g.Clear(Color.White);
            await PasteLayerAsync(win, white, h, 600);
            log.AppendLine("чистый лист: новый слой залит белым (прежний рисунок цел — в слоях под ним)");
        }
        var plan = await Task.Run(() => PencilPlan.Make(reference, c.Width, c.Height, detail), h.Token);
        log.AppendLine($"план: {plan.Sum(p => p.Strokes.Count)} штрихов в {plan.Count} проходах ({clock.Elapsed.TotalSeconds:F0} с)");
        if (!await SelectAsync(win, "Карандаш", h))
        {
            log.AppendLine("[НЕ СРАБОТАЛО] не нашла инструмент «Карандаш» в Paint — рисунок не начат.");
            return;
        }
        var size = -1;
        foreach (var pass in plan)
        {
            h.Token.ThrowIfCancellationRequested();
            if (pass.Strokes.Count == 0) continue;
            var col = await ColorAsync(win, pass.Color, h);
            if (col.Length > 0) log.Append(col);
            if (pass.Size != size)
            {
                var got = await SliderValueAsync(win, "Размер", pass.Size);
                if (got is null && size < 0) log.AppendLine("у карандаша не нашлось ползунка «Размер» — толщина та, что стоит в Paint");
                size = pass.Size;
            }
            var screen = pass.Strokes.Select(s => s.Select(p => new Point(c.Left + p.X, c.Top + p.Y)).ToArray()).ToList();
            var t0 = clock.Elapsed.TotalSeconds;
            var n = await h.DrawStrokesAsync(screen, c, h.Token);
            log.AppendLine($"{pass.Name}: {n} штрихов, {pass.Color}, {pass.Size} px ({clock.Elapsed.TotalSeconds - t0:F0} с)");
        }
    }

    /// <summary>Ползунок по имени → значение (с поправкой на его пределы); вернёт, что встало, или null — ползунка нет.</summary>
    private static Task<double?> SliderValueAsync(IntPtr win, string name, double value) => Task.Run<double?>(() =>
    {
        try
        {
            var root = AutomationElement.FromHandle(win);
            var cond = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider),
                                        new PropertyCondition(AutomationElement.NameProperty, name));
            var slider = root.FindFirst(TreeScope.Descendants, cond);
            if (slider?.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rp) != true) return null;
            var r = (RangeValuePattern)rp;
            r.SetValue(Math.Clamp(value, r.Current.Minimum, r.Current.Maximum));
            return r.Current.Value;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or COMException) { return null; }
    });

    /// <summary>
    /// Образец со снимка экрана: box — [x, y, w, h] в координатах последнего look_screen (как zoom). Снимается до того,
    /// как Paint выйдет вперёд и закроет браузер с образцом.
    /// </summary>
    private static Bitmap? ReferenceFromScreen(JsonArray box, Hands h)
    {
        if (h.Shot is null || box.Count < 4) return null;
        double N(int i) => box[i] is JsonValue v && v.TryGetValue<double>(out var d) ? d : 0;
        var tl = h.ToScreen(N(0), N(1));
        var br = h.ToScreen(N(0) + N(2), N(1) + N(3));
        var r = Rectangle.FromLTRB(Math.Min(tl.X, br.X), Math.Min(tl.Y, br.Y), Math.Max(tl.X, br.X), Math.Max(tl.Y, br.Y));
        return r.Width < 16 || r.Height < 16 ? null : Unseen(() => CaptureScreen(r));
    }

    /// <summary>Снимок для честной сверки: слева образец, справа то, что вышло на холсте (одной высоты).</summary>
    private static Task<byte[]> CompareShotAsync(Rectangle c, Bitmap reference) => Task.Run(() =>
    {
        using var raw = Unseen(() => CaptureScreen(c));
        const int hh = 560;
        int rw = (int)Math.Round(reference.Width * (double)hh / reference.Height), cw = (int)Math.Round(raw.Width * (double)hh / raw.Height);
        using var img = new Bitmap(rw + cw + 12, hh + 30, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(img))
        {
            g.Clear(Color.FromArgb(40, 40, 46));
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(reference, 0, 30, rw, hh);
            g.DrawImage(raw, rw + 12, 30, cw, hh);
            using var font = new Font("Segoe UI", 13, FontStyle.Bold);
            g.DrawString("образец", font, Brushes.White, 6, 4);
            g.DrawString("у меня на холсте", font, Brushes.White, rw + 18, 4);
        }
        var codec = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var prm = new EncoderParameters(1);
        prm.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        using var ms = new MemoryStream();
        img.Save(ms, codec, prm);
        return ms.ToArray();
    });
}
