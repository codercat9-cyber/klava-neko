using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace KlavaDesk;

/// <summary>
/// Подпись Клавы от руки (друг 30.09: «чтобы в Paint не ставила текстовую автоподпись — всегда подписывала картины
/// вручную кисточкой»). Прежний вензель рисовался программой (буквы — DrawString шрифтом) и вставлялся слоем — это и
/// была «текстовая автоподпись». Теперь — только штрихи, которые она ведёт мышью кистью Paint:
/// имя рукописным шрифтом рисуется в памяти крупно → утончение Чжана–Суэня оставляет среднюю линию букв → линия
/// разбирается на штрихи (от концов, с продолжением по направлению, петли отдельно) → штрихи слева направо, как
/// пишут; потом росчерк-волна с петлёй и лапка точками (подушечка и четыре пальчика).
/// </summary>
internal static class HandSignature
{
    private const int F = 150;   // высота шрифта в памяти: скелет тоньше и ровнее, чем у мелкого

    /// <summary>Что рисовать: штрихи букв и росчерка, толщина пера, точки лапки (центр, диаметр), чернила, место.</summary>
    internal sealed record Plan(List<Point[]> Strokes, int Pen, List<(Point At, int Size)> Paw, Color Ink, Rectangle Area);

    /// <param name="picture">Что видно на холсте (для цвета — контрастом к месту подписи); null — вишнёвые чернила.</param>
    /// <param name="W">Ширина области (видимого холста) в пикселях экрана.</param>
    /// <param name="place">bottom-right | bottom-left | top-right | top-left | center | bottom</param>
    /// <param name="scale">Высота букв — доля высоты области (0,08 по умолчанию).</param>
    public static Plan Make(Bitmap? picture, int W, int H, string name, string place, float scale, Color? color)
    {
        var raw = Skeleton(name, out var box);
        var hgt = Math.Clamp(scale * H, 26f, Math.Max(26f, H * 0.4f));
        var k = hgt / Math.Max(1, box.Height);
        var sigW = box.Width * k;
        var flourish = hgt * 0.55f;   // росчерк и лапка правее и ниже имени
        var totalW = sigW + flourish;
        var totalH = hgt * 1.35f;
        var (x0, y0) = place switch
        {
            "bottom-left" => (W * 0.035f, H - totalH - H * 0.035f),
            "top-right" => (W - totalW - W * 0.035f, H * 0.035f),
            "top-left" => (W * 0.035f, H * 0.035f),
            "center" => ((W - totalW) / 2, (H - totalH) / 2),
            "bottom" => ((W - totalW) / 2, H - totalH - H * 0.035f),
            _ => (W - totalW - W * 0.035f, H - totalH - H * 0.035f),
        };
        Point Map(PointF p) => new((int)Math.Round(x0 + (p.X - box.Left) * k), (int)Math.Round(y0 + (p.Y - box.Top) * k));
        var strokes = raw.Select(s => s.Select(Map).ToArray()).Where(s => s.Length >= 2).ToList();

        // росчерк: волна под именем от первой буквы за последнюю и петля на конце
        var sy = y0 + hgt * 1.08f;
        var sx = x0 + sigW * 0.08f;
        var len = sigW * 0.98f;
        var wave = new List<Point>();
        for (var t = 0f; t <= 1.0001f; t += 0.02f)
            wave.Add(new Point((int)(sx + len * t), (int)(sy + MathF.Sin(t * MathF.PI * 1.5f) * hgt * 0.09f * (1 - t * 0.4f))));
        var end = wave[^1];
        var loop = hgt * 0.1f;
        for (var a = 0.2f; a < MathF.PI * 1.8f; a += 0.2f)
            wave.Add(new Point((int)(end.X + loop * MathF.Sin(a)), (int)(end.Y - loop * (1 - MathF.Cos(a)))));
        strokes.Add([.. wave]);

        // лапка: подушечка и четыре пальчика дугой — точками круглой кисти
        var u = hgt * 0.14f;   // лапка заметная: подушечка ~треть высоты букв
        var px = end.X + hgt * 0.2f;
        var py = end.Y - hgt * 0.75f;
        var paw = new List<(Point, int)>
        {
            (new Point((int)(px + u * 1.9f), (int)(py + u * 2.7f)), Math.Max(4, (int)(u * 2.2f))),
            (new Point((int)(px + u * 0.45f), (int)(py + u * 1.2f)), Math.Max(3, (int)(u * 1.0f))),
            (new Point((int)(px + u * 1.35f), (int)(py + u * 0.45f)), Math.Max(3, (int)(u * 1.0f))),
            (new Point((int)(px + u * 2.45f), (int)(py + u * 0.45f)), Math.Max(3, (int)(u * 1.0f))),
            (new Point((int)(px + u * 3.35f), (int)(py + u * 1.2f)), Math.Max(3, (int)(u * 1.0f))),
        };
        var area = Rectangle.FromLTRB((int)x0, (int)y0, (int)(x0 + totalW + u * 4), (int)(y0 + totalH));
        var ink = color ?? AutoInk(picture, area);
        var pen = Math.Clamp((int)Math.Round(hgt * 0.085f), 2, 40);
        return new Plan(strokes, pen, paw, ink, area);
    }

    /// <summary>Нарисовать план в картинку (превью и проверки без Paint): круглое перо по штрихам, лапка кружками.</summary>
    public static void Render(Graphics g, Plan plan)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var pen = new Pen(plan.Ink, plan.Pen) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        foreach (var s in plan.Strokes) if (s.Length >= 2) g.DrawLines(pen, s);
        using var br = new SolidBrush(plan.Ink);
        foreach (var (at, size) in plan.Paw) g.FillEllipse(br, at.X - size / 2f, at.Y - size / 2f, size, size);
    }

    // ---------- имя → средние линии букв ----------

    /// <summary>Штрихи имени в координатах большой картинки и рамка его чернил.</summary>
    private static List<PointF[]> Skeleton(string name, out RectangleF box)
    {
        using var installed = new InstalledFontCollection();
        var have = installed.Families.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var family = new[] { "Segoe Script", "Segoe Print", "Gabriola" }.FirstOrDefault(have.Contains) ?? FontFamily.GenericSerif.Name;
        using var font = new Font(family, F, FontStyle.Regular, GraphicsUnit.Pixel);
        Size size;
        using (var probe = new Bitmap(1, 1))
        using (var g = Graphics.FromImage(probe))
            size = Size.Ceiling(g.MeasureString(name, font));
        int w = size.Width + 40, h = size.Height + 40;
        var ink = new bool[h, w];
        using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(name, font, Brushes.Black, 20, 20);
            }
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                var row = new byte[data.Stride];
                for (var y = 0; y < h; y++)
                {
                    System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, data.Stride);
                    for (var x = 0; x < w; x++) ink[y, x] = row[x * 3] < 128;
                }
            }
            finally { bmp.UnlockBits(data); }
        }
        float minX = w, minY = h, maxX = 0, maxY = 0;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (ink[y, x]) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
        box = maxX > minX ? RectangleF.FromLTRB(minX, minY, maxX, maxY) : new RectangleF(0, 0, 1, 1);
        Thin(ink);
        var paths = Trace(ink);
        var minLen = F * 0.05f;   // огрызки утончения («усики» на изгибах) — не штрихи
        return [.. paths.Where(p => PathLength(p) >= minLen).Select(p => Simplify(Smooth(p), 0.9f)).OrderBy(p => p.Min(q => q.X))];
    }

    /// <summary>Утончение Чжана–Суэня: от линии остаётся средняя линия толщиной в пиксель, связность сохраняется.</summary>
    private static void Thin(bool[,] img)
    {
        int h = img.GetLength(0), w = img.GetLength(1);
        var kill = new List<(int Y, int X)>();
        bool changed;
        do
        {
            changed = false;
            for (var step = 0; step < 2; step++)
            {
                kill.Clear();
                for (var y = 1; y < h - 1; y++)
                    for (var x = 1; x < w - 1; x++)
                    {
                        if (!img[y, x]) continue;
                        bool p2 = img[y - 1, x], p3 = img[y - 1, x + 1], p4 = img[y, x + 1], p5 = img[y + 1, x + 1],
                             p6 = img[y + 1, x], p7 = img[y + 1, x - 1], p8 = img[y, x - 1], p9 = img[y - 1, x - 1];
                        var ring = new[] { p2, p3, p4, p5, p6, p7, p8, p9, p2 };
                        var b = ring.Take(8).Count(v => v);
                        if (b is < 2 or > 6) continue;
                        var a = 0;
                        for (var i = 0; i < 8; i++) if (!ring[i] && ring[i + 1]) a++;
                        if (a != 1) continue;
                        if (step == 0 ? (p2 && p4 && p6) || (p4 && p6 && p8) : (p2 && p4 && p8) || (p2 && p6 && p8)) continue;
                        kill.Add((y, x));
                    }
                foreach (var (y, x) in kill) img[y, x] = false;
                changed |= kill.Count > 0;
            }
        } while (changed);
    }

    private static readonly (int Dx, int Dy)[] Around8 = [(1, 0), (0, 1), (-1, 0), (0, -1), (1, 1), (-1, 1), (-1, -1), (1, -1)];

    /// <summary>
    /// Скелет → штрихи: от каждого конца идём, пока есть непройденные соседи, выбирая того, кто меньше всего
    /// сворачивает; развилки становятся концами других штрихов; оставшиеся петли (о, а) — с любой точки.
    /// </summary>
    private static List<List<PointF>> Trace(bool[,] img)
    {
        int h = img.GetLength(0), w = img.GetLength(1);
        var seen = new bool[h, w];
        int Deg(int x, int y) => Around8.Count(d => x + d.Dx >= 0 && x + d.Dx < w && y + d.Dy >= 0 && y + d.Dy < h && img[y + d.Dy, x + d.Dx]);
        var ends = new List<(int X, int Y)>();
        var rest = new List<(int X, int Y)>();
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                if (img[y, x]) (Deg(x, y) == 1 ? ends : rest).Add((x, y));
        var paths = new List<List<PointF>>();
        foreach (var start in ends.OrderBy(p => p.X).Concat(rest.OrderBy(p => p.X)))
        {
            if (seen[start.Y, start.X]) continue;
            var path = new List<PointF> { new(start.X, start.Y) };
            seen[start.Y, start.X] = true;
            int cx = start.X, cy = start.Y, dx = 0, dy = 0;
            while (true)
            {
                (int X, int Y, int Dx, int Dy)? best = null;
                var bestScore = double.MaxValue;
                foreach (var (ox, oy) in Around8)
                {
                    int nx = cx + ox, ny = cy + oy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h || !img[ny, nx] || seen[ny, nx]) continue;
                    // меньше поворот — лучше; при равном — ближний (по стороне, а не по диагонали)
                    var turn = dx == 0 && dy == 0 ? 0 : 1 - (ox * dx + oy * dy) / (Math.Sqrt(ox * ox + oy * oy) * Math.Sqrt(dx * dx + dy * dy));
                    var score = turn + (ox != 0 && oy != 0 ? 0.01 : 0);
                    if (score < bestScore) { bestScore = score; best = (nx, ny, ox, oy); }
                }
                if (best is not { } nb) break;
                seen[nb.Y, nb.X] = true;
                path.Add(new PointF(nb.X, nb.Y));
                (cx, cy, dx, dy) = (nb.X, nb.Y, nb.Dx, nb.Dy);
            }
            // упёрлись в пройденную развилку рядом — дотянуть штрих до неё, чтобы буквы не рвались
            foreach (var (ox, oy) in Around8)
            {
                int nx = cx + ox, ny = cy + oy;
                if (path.Count > 2 && nx >= 0 && nx < w && ny >= 0 && ny < h && img[ny, nx] && seen[ny, nx] &&
                    (Math.Abs(nx - path[^2].X) > 1.5f || Math.Abs(ny - path[^2].Y) > 1.5f))
                {
                    path.Add(new PointF(nx, ny));
                    break;
                }
            }
            paths.Add(path);
        }
        return paths;
    }

    private static float PathLength(List<PointF> p)
    {
        var s = 0f;
        for (var i = 1; i < p.Count; i++) s += MathF.Sqrt((p[i].X - p[i - 1].X) * (p[i].X - p[i - 1].X) + (p[i].Y - p[i - 1].Y) * (p[i].Y - p[i - 1].Y));
        return s;
    }

    /// <summary>Скользящее среднее по 5 точкам: пиксельная лесенка скелета → плавная линия (концы на месте).</summary>
    private static List<PointF> Smooth(List<PointF> p)
    {
        if (p.Count < 5) return p;
        var o = new List<PointF> { p[0] };
        for (var i = 1; i < p.Count - 1; i++)
        {
            int a = Math.Max(0, i - 2), b = Math.Min(p.Count - 1, i + 2);
            float sx = 0, sy = 0;
            for (var j = a; j <= b; j++) { sx += p[j].X; sy += p[j].Y; }
            o.Add(new PointF(sx / (b - a + 1), sy / (b - a + 1)));
        }
        o.Add(p[^1]);
        return o;
    }

    /// <summary>Рамер–Дуглас–Пекер: лишние точки прямых участков долой, изгибы остаются.</summary>
    private static PointF[] Simplify(List<PointF> p, float eps)
    {
        if (p.Count < 3) return [.. p];
        var keep = new bool[p.Count];
        keep[0] = keep[^1] = true;
        var stack = new Stack<(int A, int B)>();
        stack.Push((0, p.Count - 1));
        while (stack.Count > 0)
        {
            var (a, b) = stack.Pop();
            float ax = p[a].X, ay = p[a].Y, bx = p[b].X, by = p[b].Y;
            var len = MathF.Max(1e-3f, MathF.Sqrt((bx - ax) * (bx - ax) + (by - ay) * (by - ay)));
            var (far, farD) = (-1, 0f);
            for (var i = a + 1; i < b; i++)
            {
                var d = MathF.Abs((bx - ax) * (ay - p[i].Y) - (ax - p[i].X) * (by - ay)) / len;
                if (d > farD) (far, farD) = (i, d);
            }
            if (far < 0 || farD <= eps) continue;
            keep[far] = true;
            stack.Push((a, far));
            stack.Push((far, b));
        }
        return [.. p.Where((_, i) => keep[i])];
    }

    /// <summary>Чернила контрастом к месту: на светлом — тёмно-вишнёвые, на тёмном — светлое золото.</summary>
    private static Color AutoInk(Bitmap? picture, Rectangle area)
    {
        if (picture is null) return Color.FromArgb(110, 18, 42);
        var r = Rectangle.Intersect(area, new Rectangle(0, 0, picture.Width, picture.Height));
        if (r.IsEmpty) return Color.FromArgb(110, 18, 42);
        double sum = 0;
        var n = 0;
        for (var y = r.Top; y < r.Bottom; y += 5)
            for (var x = r.Left; x < r.Right; x += 5)
            {
                var c = picture.GetPixel(x, y);
                if (c.A < 20) continue;
                sum += 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
                n++;
            }
        var lum = n > 0 ? sum / n : 255;
        return lum > 135 ? Color.FromArgb(110, 18, 42) : Color.FromArgb(255, 222, 150);
    }
}
