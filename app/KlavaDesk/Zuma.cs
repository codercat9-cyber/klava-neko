using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace KlavaDesk;

/// <summary>
/// Клава играет в Зуму сама. Мозг на такое не годится: он думает секунды, а цепочка шаров всё это время ползёт к
/// черепу — через game_action она проигрывала и «заканчивала» игру паузой. Играет автопилот: смотрит на поле,
/// видит шары, лягушку и цвет у неё в пасти, выбирает выстрел и жмёт мышью. Разговор при этом не занят.
///
/// Зрение. Поле — самый большой кусок серого камня на кадре. Шары — пиксели насыщенного цвета (и почти белые),
/// центры — максимумы карты расстояний (касающиеся шары разделяются перешейком), цвет — голосованием пикселей
/// в середине шара. Касающиеся шары — цепочка, одноцветные соседи — группа. Череп — крупное золото.
/// Лягушка ищется один раз: курсор ставится в две точки, и пунктир прицела (чистый цвет текущего шара) каждый раз
/// проходит через лягушку и курсор — пересечение двух прямых и есть её центр. Текущий цвет — по пунктиру (или по
/// шару в пасти), следующий — камень на спине лягушки.
///
/// Ход. Луч от лягушки на каждый шар цепочки (и между двумя одноцветными): первый шар, в который он упрётся, —
/// туда и встанет выстрел. Тройка и больше — лопнет: лучшее, ближе к черепу — ещё лучше. Нет тройки — пара.
/// У следующего шара вариант лучше — поменять шары. Совсем некуда — выстрел в пустоту: шар улетает за край
/// и цепочку не удлиняет. Паузу, меню и магазин не трогает: клики только по полю.
///
/// Руки. Сеанс лапок кончается вместе с ответом Клавы, а партия идёт дольше, поэтому страховка своя: Esc, друг
/// переключился в другое окно, друг двинул мышь (курсор не там, где его поставила Клава) или сказал «стоп».
/// </summary>
internal static partial class ZumaBot
{
    public const string DefaultUrl = "https://yandex.ru/games/app/zuma-deluxe-revege-original-544798";

    private static CancellationTokenSource? _cts;
    private static string _status = "не играю";
    /// <summary>Партия закончилась (итог — для реплики Клавы).</summary>
    public static event Action<string>? Finished;

    public static bool Playing => _cts is not null;
    public static string Status() => _status;

    public static string Stop()
    {
        if (_cts is null) return "Я и не играю в Зуму.";
        _cts.Cancel();
        return "Останавливаю Зуму.";
    }

    // ---------- кадр и цвета ----------

    public enum Tint : byte { None, Red, Yellow, Green, Blue, Purple, White }

    /// <summary>Кадр в байтах BGR — без GetPixel.</summary>
    internal sealed class Frame
    {
        public readonly int W, H, Stride;
        public readonly byte[] Px;

        public Frame(Bitmap bmp)
        {
            W = bmp.Width;
            H = bmp.Height;
            var d = bmp.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            Stride = d.Stride;
            Px = new byte[Stride * H];
            Marshal.Copy(d.Scan0, Px, 0, Px.Length);
            bmp.UnlockBits(d);
        }

        public Tint TintAt(int x, int y)
        {
            var i = y * Stride + x * 3;
            return Classify(Px[i + 2], Px[i + 1], Px[i]);
        }

        /// <summary>Пиксель пунктира прицела: чистый цвет (синий 0E10F1, жёлтый, красный…) или ярко-белый у белых шаров.</summary>
        public bool DotAt(int x, int y)
        {
            var i = y * Stride + x * 3;
            int b = Px[i], g = Px[i + 1], r = Px[i + 2];
            int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            // Снижен порог: mx >= 180 (было 200, браузерная Зума чуть тусклее) и mn >= 200 (было 215)
            return (mx >= 180 && (mx - mn) * 100 >= 82 * mx) || (mn >= 200 && mx - mn < 45);
        }
    }

    /// <summary>
    /// Цвет шара по пикселю. Камень, жёлоб и лягушка серые (разброс каналов до ~40) — не шар. Шары насыщенные:
    /// разброс 70+, по тону — красный, жёлтый (и золото черепа), зелёный, синий (с голубыми бликами), фиолетовый.
    /// Почти белые пиксели — белые шары или блики.
    /// </summary>
    public static Tint Classify(int r, int g, int b)
    {
        int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), ch = mx - mn;
        // Снижен порог насыщенности: ch >= 55 (было 70) — шары могут быть чуть менее насыщены у границ
        // Снижен порог яркости: mx >= 80 (было 90)
        if (ch >= 55 && mx >= 80)
        {
            double h = mx == r ? 60.0 * (g - b) / ch : mx == g ? 120 + 60.0 * (b - r) / ch : 240 + 60.0 * (r - g) / ch;
            if (h < 0) h += 360;
            // Границы: Red 330–25, Yellow 25–72, Green 72–160, Blue 160–260, Purple 260–330
            // Сужен Green (было до 165): голубые шары (бирюза, ~175°) теперь Blue, а не Green
            return h < 25 || h >= 330 ? Tint.Red : h < 72 ? Tint.Yellow : h < 160 ? Tint.Green : h < 260 ? Tint.Blue : Tint.Purple;
        }
        // Снижен порог белого: mn >= 175 (было 190), ch < 55 (было 50) — белые шары бывают чуть серее
        return mn >= 175 && ch < 55 ? Tint.White : Tint.None;
    }

    private static string Name(Tint t) => t switch
    {
        Tint.Red => "красный", Tint.Yellow => "жёлтый", Tint.Green => "зелёный", Tint.Blue => "синий",
        Tint.Purple => "фиолетовый", Tint.White => "белый", _ => "?",
    };

    // ---------- поле ----------

    /// <summary>Поле — самый большой связный кусок серого камня (клетки 16 px, где камня треть и больше).</summary>
    public static Rectangle FindBoard(Frame f)
    {
        const int C = 16;
        int cw = f.W / C, chh = f.H / C;
        if (cw < 4 || chh < 4) return Rectangle.Empty;
        var stone = new bool[cw * chh];
        for (var cy = 0; cy < chh; cy++)
            for (var cx = 0; cx < cw; cx++)
            {
                var n = 0;
                for (var sy = 2; sy < C; sy += 4)
                    for (var sx = 2; sx < C; sx += 4)
                    {
                        var i = (cy * C + sy) * f.Stride + (cx * C + sx) * 3;
                        int b = f.Px[i], g = f.Px[i + 1], r = f.Px[i + 2];
                        int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                        if (mx - mn < 30 && mx is >= 70 and <= 200) n++;
                    }
                stone[cy * cw + cx] = n >= 5;
            }
        var seen = new bool[cw * chh];
        var best = Rectangle.Empty;
        var bestCount = 0;
        var q = new Queue<int>();
        for (var s = 0; s < stone.Length; s++)
        {
            if (!stone[s] || seen[s]) continue;
            int count = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            seen[s] = true;
            q.Enqueue(s);
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                int x = c % cw, y = c / cw;
                count++;
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= cw || ny >= chh) continue;
                    var ni = ny * cw + nx;
                    if (!stone[ni] || seen[ni]) continue;
                    seen[ni] = true;
                    q.Enqueue(ni);
                }
            }
            if (count > bestCount) { bestCount = count; best = new Rectangle(x0, y0, x1 - x0 + 1, y1 - y0 + 1); }
        }
        // поле — это десятки клеток камня; меньше — на кадре просто серое пятно
        if (bestCount < 60) return Rectangle.Empty;
        // рамка вокруг поля (дерево, металлические полоски) цепляется к камню — поле = самый длинный сплошной отрезок
        // строк, где камня половина и больше, потом так же столбцы внутри него. Обрезать просто края не выходило:
        // серая полоска рамки сама «камень» и останавливала обрезку, а узоры дерева сходили за шары
        int L = best.Left, T = best.Top, Rt = best.Right - 1, B = best.Bottom - 1;
        (int From, int To) Longest(int from, int to, Func<int, bool> dense)
        {
            (int, int) run = (from, from - 1);
            var start = -1;
            for (var i = from; i <= to + 1; i++)
            {
                if (i <= to && dense(i)) { if (start < 0) start = i; continue; }
                if (start >= 0 && i - start > run.Item2 - run.Item1 + 1) run = (start, i - 1);
                start = -1;
            }
            return run;
        }
        // порог 30 %: столбец, по которому идёт цепочка, на 60 % в шарах и жёлобе — и всё равно поле; дерево рамки — 5 %
        (T, B) = Longest(T, B, y => { var k = 0; for (var x = L; x <= Rt; x++) if (stone[y * cw + x]) k++; return k * 10 >= 3 * (Rt - L + 1); });
        if (B < T) return Rectangle.Empty;
        (L, Rt) = Longest(L, Rt, x => { var k = 0; for (var y = T; y <= B; y++) if (stone[y * cw + x]) k++; return k * 10 >= 3 * (B - T + 1); });
        if (Rt < L) return Rectangle.Empty;
        return new Rectangle(L * C, T * C, (Rt - L + 1) * C, (B - T + 1) * C);
    }

    // ---------- шары ----------

    public readonly record struct Ball(float X, float Y, Tint C, float Size);

    /// <summary>Что видно на кадре.</summary>
    internal sealed class Scene
    {
        public Rectangle Board;
        public float R;                             // радиус шара, px кадра
        public List<Ball> Balls = [];               // шары цепочки (без шаров лягушки)
        public int[] Group = [];                    // одноцветная группа каждого шара
        public int[] GroupSize = [];                // размер группы по номеру
        public int[] Links = [];                    // сколько соседей у шара в цепочке
        public PointF? Skull;
        public Tint Cur, Next;
        public int CurVotes;                        // сколько точек пунктира подтвердили цвет (0 — узнан по пасти)
        public bool RSure;                          // радиус узнан надёжно: видна цепочка, размер в разумных пределах
        public PointF? Facing;                      // куда смотрит лягушка (единичный вектор)
        // по трассе уровня (локальная PopCap-Зума, ZumaTrack): у каждого шара — кривая, место на пути, опасность 0..1
        // (1 — у самой пасти черепа) и соседи по цепочке (−1 — зазор). Пусто — шары найдены по картинке (браузер)
        public int[] CurveOf = [];
        public float[] Idx = [];
        public float[] Danger = [];
        public int[] PrevB = [], NextB = [];
        // из памяти игры (локальная PopCap-Зума): шар под туннелем — в цепочке есть, но выстрел его не заденет;
        // бонус группы (очки к выстрелу, который её лопнет) — по номеру группы. Пусто — нет данных
        public bool[] Hidden = [];
        public float[] GroupBonus = [];
        public bool[] GroupBusy = [];               // в группу уже встаёт выстрел
    }

    /// <summary>Средняя яркость поля (сетка 24×16) — видно, кончилось ли затемнение при появлении уровня.</summary>
    public static double Luma(Frame f, Rectangle board)
    {
        if (board.Width <= 0 || board.Height <= 0) return 0;
        double sum = 0;
        var n = 0;
        for (var j = 0; j < 16; j++)
            for (var i = 0; i < 24; i++)
            {
                int x = board.X + (int)((i + 0.5) * board.Width / 24), y = board.Y + (int)((j + 0.5) * board.Height / 16);
                if (x < 0 || y < 0 || x >= f.W || y >= f.H) continue;
                var p = y * f.Stride + x * 3;
                sum += (f.Px[p + 2] * 30 + f.Px[p + 1] * 59 + f.Px[p] * 11) / 100.0;
                n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    /// <summary>Доля серого камня на поле (сетка 24×16): у игры ~0,5, у окна «Пройдено» или меню — почти ноль.</summary>
    public static double StoneShare(Frame f, Rectangle board)
    {
        int n = 0, k = 0;
        for (var j = 0; j < 16; j++)
            for (var i = 0; i < 24; i++)
            {
                int x = board.X + (int)((i + 0.5) * board.Width / 24), y = board.Y + (int)((j + 0.5) * board.Height / 16);
                if (x < 0 || y < 0 || x >= f.W || y >= f.H) continue;
                n++;
                var p = y * f.Stride + x * 3;
                int b = f.Px[p], g = f.Px[p + 1], r = f.Px[p + 2];
                int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
                if (mx - mn < 30 && mx is >= 70 and <= 200) k++;
            }
        return n == 0 ? 0 : k / (double)n;
    }

    /// <summary>
    /// Золотые пятна размером с череп: плотные, почти квадратные, больше трёх радиусов шара (8-связность — глазницы
    /// и прожилки не рвут пятно). Череп от дуги жёлтых шаров отличается тем, что стоит на месте — см. PlayCoreAsync.
    /// </summary>
    public static List<Rectangle> SkullBoxes(Frame f, Rectangle board, float r)
    {
        var boxes = new List<Rectangle>();
        var reg = Rectangle.Intersect(board, new Rectangle(0, 0, f.W, f.H));
        if (reg.IsEmpty || r <= 0) return boxes;
        var step = reg.Width > 1300 ? 2 : 1;
        int w = reg.Width / step, h = reg.Height / step;
        var raw = new bool[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                raw[y * w + x] = f.TintAt(reg.X + x * step, reg.Y + y * step) == Tint.Yellow;
        var rs = r / step;
        // тёмные контуры режут череп на лучи и лицо — раздуваем золото на ~0,15 радиуса, чтобы куски склеились
        var grow = Math.Max(1, (int)Math.Round(0.15f * rs));
        var rowMax = new bool[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var any = false;
                for (var d = -grow; d <= grow && !any; d++) { var xx = x + d; any = xx >= 0 && xx < w && raw[y * w + xx]; }
                rowMax[y * w + x] = any;
            }
        var yel = new bool[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var any = false;
                for (var d = -grow; d <= grow && !any; d++) { var yy = y + d; any = yy >= 0 && yy < h && rowMax[yy * w + x]; }
                yel[y * w + x] = any;
            }
        var seen = new bool[w * h];
        var q = new Queue<int>();
        for (var i0 = 0; i0 < w * h; i0++)
        {
            if (seen[i0] || !yel[i0]) continue;
            int area = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
            seen[i0] = true;
            q.Enqueue(i0);
            while (q.Count > 0)
            {
                var c = q.Dequeue();
                int cx = c % w, cy = c / w;
                area++;
                x0 = Math.Min(x0, cx); y0 = Math.Min(y0, cy); x1 = Math.Max(x1, cx); y1 = Math.Max(y1, cy);
                for (var dy = -1; dy <= 1; dy++)
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        var ni = ny * w + nx;
                        if (seen[ni] || !yel[ni]) continue;
                        seen[ni] = true;
                        q.Enqueue(ni);
                    }
            }
            int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
            if (bw >= 3.2f * rs && bh >= 3.2f * rs && Math.Max(bw, bh) <= 1.7f * Math.Min(bw, bh) && area >= 0.3f * bw * bh)
                boxes.Add(new Rectangle(reg.X + x0 * step, reg.Y + y0 * step, bw * step, bh * step));
        }
        return boxes;
    }

    /// <summary>
    /// Разглядеть кадр: шары цепочки, группы; если лягушка и курсор известны — текущий и следующий цвет.
    /// rHint — радиус шара, узнанный раньше (0 — посчитать по этому кадру); skulls — где черепа (там не шары).
    /// </summary>
    public static Scene See(Frame f, Rectangle board, PointF? frog, PointF? aim, float rHint, IReadOnlyList<Rectangle>? skulls = null)
    {
        var s = new Scene { Board = board };
        if (skulls is { Count: > 0 })
        {
            var k = skulls.OrderByDescending(b => b.Width * b.Height).First();
            s.Skull = new PointF(k.X + k.Width / 2f, k.Y + k.Height / 2f);
        }
        // ровно по полю: полоска рамки за краем давала ряд мелких «жёлтых шаров» и сбивала радиус
        var reg = Rectangle.Intersect(board, new Rectangle(0, 0, f.W, f.H));
        if (reg.Width < 40 || reg.Height < 40) return s;
        var step = reg.Width > 1300 ? 2 : 1;
        int w = reg.Width / step, h = reg.Height / step;
        var cls = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                cls[y * w + x] = (byte)f.TintAt(reg.X + x * step, reg.Y + y * step);

        // маска «шар» с залатанными точками: узоры и блики не должны дырявить шар
        var m = new bool[w * h];
        for (var i = 0; i < m.Length; i++) m[i] = cls[i] != 0;
        var patch = new List<int>();
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                if (m[i]) continue;
                var n = 0;
                for (var dy = -1; dy <= 1; dy++) for (var dx = -1; dx <= 1; dx++) if (m[i + dy * w + dx]) n++;
                if (n >= 6) patch.Add(i);
            }
        foreach (var i in patch) m[i] = true;

        // карта расстояний до края маски (фаска 3-4: 3 — шаг по прямой, 4 — по диагонали)
        var dt = new int[w * h];
        const int Big = 1 << 20;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (!m[i]) { dt[i] = 0; continue; }
                var v = Big;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) v = 3;
                else
                {
                    v = Math.Min(v, dt[i - 1] + 3);
                    v = Math.Min(v, dt[i - w] + 3);
                    v = Math.Min(v, dt[i - w - 1] + 4);
                    v = Math.Min(v, dt[i - w + 1] + 4);
                }
                dt[i] = v;
            }
        for (var y = h - 2; y >= 1; y--)
            for (var x = w - 2; x >= 1; x--)
            {
                var i = y * w + x;
                if (dt[i] == 0) continue;
                var v = dt[i];
                v = Math.Min(v, dt[i + 1] + 3);
                v = Math.Min(v, dt[i + w] + 3);
                v = Math.Min(v, dt[i + w + 1] + 4);
                v = Math.Min(v, dt[i + w - 1] + 4);
                dt[i] = v;
            }

        // вершины: не меньше соседей; дальше — подавление повторов внутри одного шара
        var cand = new List<(int X, int Y, int D)>();
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
            {
                var i = y * w + x;
                var d = dt[i];
                if (d < 12) continue;   // меньше 4 px — пунктир прицела и крошки
                if (d < dt[i - 1] || d < dt[i + 1] || d < dt[i - w] || d < dt[i + w] || d < dt[i - w - 1] || d < dt[i - w + 1] || d < dt[i + w - 1] || d < dt[i + w + 1]) continue;
                cand.Add((x, y, d));
            }
        cand.Sort((a, b) => b.D.CompareTo(a.D));
        var peaks = new List<(int X, int Y, float Rad)>();
        foreach (var c in cand)
        {
            var rc = c.D / 3f;
            var keep = true;
            foreach (var p in peaks)
            {
                var lim = 1.2f * Math.Max(rc, p.Rad);
                if ((c.X - p.X) * (c.X - p.X) + (c.Y - p.Y) * (c.Y - p.Y) < lim * lim) { keep = false; break; }
            }
            if (keep) peaks.Add((c.X, c.Y, rc));
        }
        if (peaks.Count == 0) return s;

        // радиус шара: известный или медиана «цепочечных» вершин — у которых рядом вершина того же размера на
        // расстоянии 1,5–2,8 радиуса (соседний шар); крошки узоров, пасть и золото черепа так не стоят
        float rs;
        if (rHint > 0) { rs = rHint / step; s.RSure = true; }
        else
        {
            var rad = new List<float>();
            for (var i = 0; i < peaks.Count; i++)
                for (var j = 0; j < peaks.Count; j++)
                {
                    if (i == j) continue;
                    var dd = Dist(peaks[i].X, peaks[i].Y, peaks[j].X, peaks[j].Y);
                    if (dd > 1.5f * peaks[i].Rad && dd < 2.8f * peaks[i].Rad && Math.Abs(peaks[i].Rad - peaks[j].Rad) < 0.3f * peaks[i].Rad)
                    {
                        rad.Add(peaks[i].Rad);
                        break;
                    }
                }
            // надёжно — когда «цепочечных» вершин хотя бы четыре и шар от 1/90 до 1/25 ширины поля; иначе (начало уровня,
            // пара шаров и детали лягушки) радиус выходил 6 px вместо 16, и настоящие шары отсеивались как «крупные»
            var chainy = rad.Count;
            if (rad.Count == 0) rad = peaks.Select(p => p.Rad).ToList();
            rad.Sort();
            // мелочь (узоры, край рамки) тоже бывает «цепочкой» — считаем только крупные: от 0,6 верхнего десятка
            var top = rad[(int)(0.9 * (rad.Count - 1))];
            rad = rad.Where(v => v >= 0.6f * top).ToList();
            rs = rad[rad.Count / 2];
            var px = rs * step;
            s.RSure = chainy >= 4 && px >= board.Width / 90f && px <= board.Width / 25f;
        }
        s.R = rs * step;

        Tint Vote(int cx, int cy, float radius)
        {
            var cnt = new int[7];
            var ri = (int)Math.Ceiling(radius);
            for (var dy = -ri; dy <= ri; dy++)
                for (var dx = -ri; dx <= ri; dx++)
                {
                    if (dx * dx + dy * dy > radius * radius) continue;
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= w || y >= h) continue;
                    cnt[cls[y * w + x]]++;
                }
            var colored = cnt[1] + cnt[2] + cnt[3] + cnt[4] + cnt[5];
            if (cnt[6] > 1.5 * colored) return Tint.White;
            var best = Tint.None;
            for (var t = 1; t <= 5; t++) if (cnt[t] > 0 && (best == Tint.None || cnt[t] > cnt[(int)best])) best = (Tint)t;
            return best;
        }

        var balls = new List<Ball>();
        foreach (var p in peaks)
        {
            var X = reg.X + (p.X + 0.5f) * step;
            var Y = reg.Y + (p.Y + 0.5f) * step;
            if (p.Rad > 1.45f * rs || p.Rad < 0.55f * rs) continue;
            if (frog is { } fr && Dist(X, Y, fr.X, fr.Y) < 2.6f * s.R) continue;   // шар в пасти и камень на спине
            if (skulls is not null && skulls.Any(b => Rectangle.Inflate(b, (int)(0.3f * s.R), (int)(0.3f * s.R)).Contains((int)X, (int)Y))) continue;
            var t = Vote(p.X, p.Y, 0.7f * rs);
            if (t == Tint.None) continue;
            balls.Add(new Ball(X, Y, t, p.Rad * step));
        }

        // цепочка: касающиеся шары; одиночки без соседей (монетка, случайное пятно) — не шары цепочки
        var n0 = balls.Count;
        var parent = Enumerable.Range(0, n0).ToArray();
        int Find(int a) { while (parent[a] != a) a = parent[a] = parent[parent[a]]; return a; }
        var links = new int[n0];
        // BUG FIX: linkR был 2.4f — при шарах с небольшими зазорами между ними цепочка рвалась.
        // Увеличен до 2.6f: два касающихся шара занимают 2 диаметра = 4r, с учётом погрешности определения центра
        var linkR = 2.6f * s.R;
        for (var i = 0; i < n0; i++)
            for (var j = i + 1; j < n0; j++)
            {
                if (Dist(balls[i].X, balls[i].Y, balls[j].X, balls[j].Y) > linkR) continue;
                links[i]++;
                links[j]++;
                if (balls[i].C == balls[j].C) parent[Find(i)] = Find(j);
            }
        var keepIdx = Enumerable.Range(0, n0).Where(i => links[i] > 0).ToList();
        s.Balls = keepIdx.Select(i => balls[i]).ToList();
        s.Links = keepIdx.Select(i => links[i]).ToArray();
        var roots = keepIdx.Select(Find).ToList();
        var ids = roots.Distinct().Select((root, k) => (root, k)).ToDictionary(t => t.root, t => t.k);
        s.Group = roots.Select(root => ids[root]).ToArray();
        s.GroupSize = new int[ids.Count];
        foreach (var g in s.Group) s.GroupSize[g]++;

        // Цвета лягушки. Текущий — пунктир прицела: точки на одной прямой с лягушкой, куда бы она ни смотрела
        // (поворачивается она плавно, и на курсор пунктир смотрит не всегда — по лучу к курсору цвет терялся, а
        // уровень сливался). Нет пунктира — шар в пасти по направлению к курсору. Следующий — камень на спине.
        if (frog is { } F)
        {
            PointF? u = null;
            if (SightLine(OffBalls(FindDots(f, board, s.R), s, skulls), F, s.R) is { } sl) { u = sl.Dir; s.Cur = sl.T; s.CurVotes = sl.Count; }
            else if (aim is { } A && Dist(A.X, A.Y, F.X, F.Y) > 1)
            {
                var l = Dist(A.X, A.Y, F.X, F.Y);
                u = new PointF((A.X - F.X) / l, (A.Y - F.Y) / l);
                s.Cur = VoteFull(f, F.X + u.Value.X * 2.3f * s.R, F.Y + u.Value.Y * 2.3f * s.R, 0.55f * s.R);
            }
            if (u is { } d)
            {
                s.Facing = d;
                // BUG FIX: радиус голосования для "следующего" шара на спине увеличен с 0.45f до 0.55f:
                // камень на спине виден широко и при слишком маленьком радиусе мы попадали в шов панциря
                s.Next = VoteFull(f, F.X - d.X * 1.2f * s.R, F.Y - d.Y * 1.2f * s.R, 0.55f * s.R);
            }
        }
        return s;
    }

    /// <summary>Цвет голосованием в круге на полном кадре (шар в пасти, камень на спине).</summary>
    private static Tint VoteFull(Frame f, float cx, float cy, float radius)
    {
        var cnt = new int[7];
        var ri = (int)Math.Ceiling(radius);
        for (var dy = -ri; dy <= ri; dy++)
            for (var dx = -ri; dx <= ri; dx++)
            {
                if (dx * dx + dy * dy > radius * radius) continue;
                int x = (int)cx + dx, y = (int)cy + dy;
                if (x < 0 || y < 0 || x >= f.W || y >= f.H) continue;
                cnt[(int)f.TintAt(x, y)]++;
            }
        var colored = cnt[1] + cnt[2] + cnt[3] + cnt[4] + cnt[5];
        if (colored + cnt[6] < 4) return Tint.None;
        if (cnt[6] > 1.5 * colored) return Tint.White;
        var best = Tint.None;
        for (var t = 1; t <= 5; t++) if (cnt[t] > 0 && (best == Tint.None || cnt[t] > cnt[(int)best])) best = (Tint)t;
        return best;
    }

    private static float Dist(float ax, float ay, float bx, float by) => MathF.Sqrt((ax - bx) * (ax - bx) + (ay - by) * (ay - by));

    // ---------- лягушка ----------

    /// <summary>Точка пунктира прицела и её цвет (цвет текущего шара).</summary>
    public readonly record struct Dot(float X, float Y, Tint T);

    /// <summary>Отбросить «точки» на шарах цепочки и на черепе — это блики, а не пунктир.</summary>
    public static List<Dot> OffBalls(List<Dot> dots, Scene s, IReadOnlyList<Rectangle>? skulls) =>
        dots.Where(d => !s.Balls.Any(b => Dist(d.X, d.Y, b.X, b.Y) < 1.15f * s.R)
                        && (skulls is null || !skulls.Any(k => k.Contains((int)d.X, (int)d.Y)))).ToList();

    /// <summary>Точки пунктира прицела: мелкие круглые пятна чистого цвета (или ярко-белые) посреди камня.</summary>
    public static List<Dot> FindDots(Frame f, Rectangle board, float r)
    {
        var reg = Rectangle.Intersect(board, new Rectangle(0, 0, f.W, f.H));
        var dots = new List<Dot>();
        if (reg.IsEmpty) return dots;
        var seen = new bool[reg.Width * reg.Height];
        var maxArea = Math.Max(12, 0.15 * Math.PI * r * r);
        var maxSide = Math.Max(4, 0.7f * r);
        var q = new Queue<(int X, int Y)>();
        for (var y = 0; y < reg.Height; y++)
            for (var x = 0; x < reg.Width; x++)
            {
                var i = y * reg.Width + x;
                if (seen[i] || !f.DotAt(reg.X + x, reg.Y + y)) continue;
                int area = 0, x0 = x, x1 = x, y0 = y, y1 = y;
                double sx = 0, sy = 0;
                seen[i] = true;
                q.Enqueue((x, y));
                while (q.Count > 0)
                {
                    var (cx, cy) = q.Dequeue();
                    area++;
                    sx += cx; sy += cy;
                    x0 = Math.Min(x0, cx); x1 = Math.Max(x1, cx); y0 = Math.Min(y0, cy); y1 = Math.Max(y1, cy);
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nx = cx + dx, ny = cy + dy;
                        if (nx < 0 || ny < 0 || nx >= reg.Width || ny >= reg.Height) continue;
                        var ni = ny * reg.Width + nx;
                        if (seen[ni] || !f.DotAt(reg.X + nx, reg.Y + ny)) continue;
                        seen[ni] = true;
                        q.Enqueue((nx, ny));
                    }
                }
                int bw = x1 - x0 + 1, bh = y1 - y0 + 1;
                if (area < 4 || area > maxArea || bw > maxSide || bh > maxSide) continue;
                if (area < 0.30 * bw * bh) continue;   // не круглое — кусок узора; было 0.45, снижено для продолговатых точек пунктира
                float cx0 = reg.X + (float)(sx / area) + 0.5f, cy0 = reg.Y + (float)(sy / area) + 0.5f;
                dots.Add(new Dot(cx0, cy0, f.TintAt((int)cx0, (int)cy0)));
            }
        return dots;
    }

    /// <summary>
    /// Прямая пунктира по самим точкам, без курсора: пара точек одного цвета и больше всего таких же рядом с их
    /// прямой (4+). Через курсор её строить нельзя — лягушка поворачивается плавно и смотрит туда не сразу.
    /// </summary>
    public static (PointF P, PointF Dir, int Count)? DotLineFree(List<Dot> dots, float r)
    {
        var tol = 2.5f + 0.12f * r;
        (PointF, PointF, int)? best = null;
        for (var i = 0; i < dots.Count; i++)
            for (var j = i + 1; j < dots.Count; j++)
            {
                if (dots[i].T != dots[j].T) continue;
                float ux = dots[j].X - dots[i].X, uy = dots[j].Y - dots[i].Y, len = MathF.Sqrt(ux * ux + uy * uy);
                // Снижен минимум: было 1.5f*r — при мелких r первые две точки могут быть ближе
                if (len < 0.8f * r) continue;
                ux /= len; uy /= len;
                var a = dots[i];
                var inl = dots.Where(e => e.T == a.T && MathF.Abs((e.X - a.X) * uy - (e.Y - a.Y) * ux) < tol).ToList();
                // Снижен порог с 4 до 3: в браузерной Зуме пунктир часто частично перекрыт шарами
                if (inl.Count < 3 || (best is not null && inl.Count <= best.Value.Item3)) continue;
                // у пунктира шаг ровный (часть точек может прятаться за шарами — тогда промежуток кратный);
                // блики на шарах цепочки тоже бывают на одной прямой, но шагают как попало
                var ts = inl.Select(e => (e.X - a.X) * ux + (e.Y - a.Y) * uy).OrderBy(t => t).ToList();
                var gaps = ts.Zip(ts.Skip(1), (p, q) => q - p).Where(g => g > 0.5f).OrderBy(g => g).ToList();
                // При 3 инлайнерах только 2 зазора — достаточно
                if (gaps.Count < 2) continue;
                var step = gaps[0];
                // Расширен допустимый диапазон шага: было 1.5–6, теперь 0.8–8 радиусов
                // (пунктир в разных версиях Зумы имеет разную частоту точек)
                if (step < 0.8f * r || step > 8 * r) continue;
                var regular = gaps.Count(g => MathF.Abs(g / step - MathF.Round(g / step)) < 0.25f);
                // Снижен порог регулярности с 3 до 2
                if (regular < 2) continue;
                // уточнить: центр точек и главная ось
                double mx = inl.Average(e => e.X), my = inl.Average(e => e.Y), sxx = 0, sxy = 0, syy = 0;
                foreach (var e in inl) { double ex = e.X - mx, ey = e.Y - my; sxx += ex * ex; sxy += ex * ey; syy += ey * ey; }
                var ang = 0.5 * Math.Atan2(2 * sxy, sxx - syy);
                best = (new PointF((float)mx, (float)my), new PointF((float)Math.Cos(ang), (float)Math.Sin(ang)), inl.Count);
            }
        return best;
    }

    /// <summary>Пунктир от лягушки: куда она смотрит сейчас и каким шаром стрельнёт (2+ точки на луче из её центра).</summary>
    public static (PointF Dir, Tint T, int Count)? SightLine(List<Dot> dots, PointF frog, float r)
    {
        var tol = 3f + 0.14f * r;
        (PointF, Tint, int)? best = null;
        foreach (var d in dots)
        {
            float ux = d.X - frog.X, uy = d.Y - frog.Y, len = MathF.Sqrt(ux * ux + uy * uy);
            // Снижен минимум: 2.0f*r (было 2.5f) — у PopCap-версии лягушка чуть меньше
            if (len < 2.0f * r) continue;
            ux /= len; uy /= len;
            var inl = dots.Where(e =>
            {
                float ex = e.X - frog.X, ey = e.Y - frog.Y;
                return ex * ux + ey * uy > 2.0f * r && MathF.Abs(ex * uy - ey * ux) < tol;
            }).ToList();
            // Снижен порог с 3 до 2: минимально пунктир — две точки на луче, достаточно чтобы определить направление
            if (inl.Count < 2 || (best is not null && inl.Count <= best.Value.Item3)) continue;
            double ax = 0, ay = 0;
            foreach (var e in inl) { ax += e.X - frog.X; ay += e.Y - frog.Y; }   // дальние точки весят больше — точнее по углу
            var al = Math.Sqrt(ax * ax + ay * ay);
            var tint = inl.GroupBy(e => e.T).OrderByDescending(g => g.Count()).First().Key;
            best = (new PointF((float)(ax / al), (float)(ay / al)), tint, inl.Count);
        }
        return best;
    }

    /// <summary>Ждать, пока пунктир ляжет на цель (промах у цели меньше трети шара): false — не дождалась за max.</summary>
    private static async Task<bool> WaitAimAsync(IIo io, Rectangle board, PointF frog, PointF target, float r, TimeSpan max)
    {
        float tx = target.X - frog.X, ty = target.Y - frog.Y, tl = MathF.Sqrt(tx * tx + ty * ty);
        if (tl < 1) return true;
        tx /= tl; ty /= tl;
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < max)
        {
            var f = await io.GrabAsync();
            // BUG FIX: раньше FindDots передавались без OffBalls — блики на шарах цепочки выдавали ложное направление;
            // теперь фильтруем точки на шарах, как и во всём остальном коде
            var scene = See(f, board, frog, target, r);
            var dots = OffBalls(FindDots(f, board, r), scene, null);
            if (SightLine(dots, frog, r) is { } sl
                && sl.Dir.X * tx + sl.Dir.Y * ty > 0 && tl * MathF.Abs(sl.Dir.X * ty - sl.Dir.Y * tx) < 0.35f * r)
                return true;
            await Task.Delay(25, CancellationToken.None);
        }
        return false;
    }

    /// <summary>Точка, ближайшая ко всем прямым сразу (сумма квадратов расстояний): центр лягушки по всем пробам.</summary>
    public static PointF? NearestToLines(List<(PointF P, PointF Dir, int Count)> lines)
    {
        double a11 = 0, a12 = 0, a22 = 0, b1 = 0, b2 = 0;
        foreach (var (p, d, _) in lines)
        {
            double nx = -d.Y, ny = d.X, c = nx * p.X + ny * p.Y;   // нормаль прямой и её сдвиг
            a11 += nx * nx; a12 += nx * ny; a22 += ny * ny;
            b1 += nx * c; b2 += ny * c;
        }
        var det = a11 * a22 - a12 * a12;
        if (Math.Abs(det) < 1e-6) return null;
        return new PointF((float)((b1 * a22 - b2 * a12) / det), (float)((a11 * b2 - a12 * b1) / det));
    }

    /// <summary>Пересечение двух прямых пунктира — центр лягушки; почти параллельные не годятся.</summary>
    public static PointF? Cross((PointF P, PointF Dir, int Count) a, (PointF P, PointF Dir, int Count) b)
    {
        var den = a.Dir.X * b.Dir.Y - a.Dir.Y * b.Dir.X;
        // Снижен порог с 0.4 до 0.3 (~17°): при крайних позициях курсора угол между пробами может быть меньше 24°
        if (MathF.Abs(den) < 0.3f) return null;   // угол меньше ~17° — точка пересечения гуляет
        float wx = b.P.X - a.P.X, wy = b.P.Y - a.P.Y;
        var t = (wx * b.Dir.Y - wy * b.Dir.X) / den;
        return new PointF(a.P.X + a.Dir.X * t, a.P.Y + a.Dir.Y * t);
    }

    // ---------- ход ----------

    /// <param name="Hit">Шар сцены, в который упрётся выстрел (−1 — в пустоту / смена).</param>
    public sealed record Move(PointF Aim, bool Swap, bool Pop, string Why, int Hit = -1);

    /// <summary>Первый шар цепочки на луче из лягушки (выстрел встанет к нему); −1 — луч уходит в пустоту.</summary>
    private static int FirstHit(Scene s, PointF from, float ux, float uy)
    {
        int best = -1;
        var bestT = float.MaxValue;
        // BUG FIX: hitR был 1.85f*s.R — луч мог проходить мимо шара при диагональных выстрелах.
        // Правильный радиус захвата = 2.0f*s.R (диаметр шара + небольшой допуск 0.1f для надёжности)
        var hitR = 2.1f * s.R;
        var hidden = s.Hidden.Length == s.Balls.Count;
        for (var i = 0; i < s.Balls.Count; i++)
        {
            if (hidden && s.Hidden[i]) continue;    // под туннелем выстрел проходит поверх
            float dx = s.Balls[i].X - from.X, dy = s.Balls[i].Y - from.Y;
            var t = dx * ux + dy * uy;
            if (t < 1.5f * s.R) continue;
            var d = MathF.Abs(dx * uy - dy * ux);
            if (d >= hitR) continue;
            var entry = t - MathF.Sqrt(hitR * hitR - d * d);
            if (entry < bestT) { bestT = entry; best = i; }
        }
        return best;
    }

    /// <summary>
    /// Лопнет группа — сомкнутся ли её соседи одного цвета (в Зуме одноцветные концы разрыва притягиваются и лопают
    /// ещё): 60 — сразу тройка и больше, 15 — пара на будущее. Только для шаров по трассе (соседи известны).
    /// </summary>
    private static float Combo(Scene s, int hit)
    {
        if (s.PrevB.Length != s.Balls.Count) return 0;
        var g = s.Group[hit];
        int first = hit, last = hit;
        while (s.PrevB[first] >= 0 && s.Group[s.PrevB[first]] == g) first = s.PrevB[first];
        while (s.NextB[last] >= 0 && s.Group[s.NextB[last]] == g) last = s.NextB[last];
        int before = s.PrevB[first], after = s.NextB[last];
        if (before < 0 || after < 0 || s.Balls[before].C != s.Balls[after].C) return 0;
        return s.GroupSize[s.Group[before]] + s.GroupSize[s.Group[after]] >= 3 ? 60 : 15;
    }

    /// <summary>Лучший выстрел шаром цвета t: (очки, точка прицела, шар попадания). Очки: 100+ — лопнет, 20+ — пара, −1 — некуда.</summary>
    /// <param name="skip">Шар, куда уже летит прошлый выстрел, — туда второй раз не бить.</param>
    private static (float Score, PointF Aim, int Hit) Best(Scene s, PointF frog, Tint t, Func<PointF, bool> avoid, Func<int, bool>? skip = null)
    {
        (float, PointF, int) best = (-1, default, -1);
        if (t == Tint.None) return best;
        var diag = MathF.Sqrt(s.Board.Width * s.Board.Width + s.Board.Height * s.Board.Height);
        var aims = new List<PointF>();
        var hidden = s.Hidden.Length == s.Balls.Count;
        for (var i = 0; i < s.Balls.Count; i++)
        {
            if (s.Balls[i].C != t || hidden && s.Hidden[i]) continue;
            aims.Add(new PointF(s.Balls[i].X, s.Balls[i].Y));
            // между двумя одноцветными соседями — выстрел встанет прямо в пару
            for (var j = i + 1; j < s.Balls.Count; j++)
                if (s.Balls[j].C == t && s.Group[j] == s.Group[i] && Dist(s.Balls[i].X, s.Balls[i].Y, s.Balls[j].X, s.Balls[j].Y) < 2.4f * s.R)
                    aims.Add(new PointF((s.Balls[i].X + s.Balls[j].X) / 2, (s.Balls[i].Y + s.Balls[j].Y) / 2));
        }
        foreach (var a in aims)
        {
            float ux = a.X - frog.X, uy = a.Y - frog.Y, len = MathF.Sqrt(ux * ux + uy * uy);
            if (len < 2 * s.R) continue;
            ux /= len; uy /= len;
            var hit = FirstHit(s, frog, ux, uy);
            if (hit < 0 || s.Balls[hit].C != t || skip?.Invoke(hit) == true) continue;
            var click = ClickPoint(frog, a, ux, uy, len, s.R, avoid);
            if (click is null) continue;
            var size = s.GroupSize[s.Group[hit]];
            // по трассе опасность точная (сколько пути осталось до черепа); без неё — по прямой до черепа
            var track = s.Danger.Length == s.Balls.Count;
            var danger = track ? s.Danger[hit]
                : s.Skull is { } sk ? 1 - Math.Clamp(Dist(s.Balls[hit].X, s.Balls[hit].Y, sk.X, sk.Y) / (0.6f * diag), 0, 1) : 0;
            // пара у черепа — это +1 шар к пасти (цепочка перед вставкой сдвигается вперёд), по трассе пары строим подальше.
            // Опасность по трассе — круто растущая: у пасти группа важнее любой жирной тройки в хвосте (на быстрых
            // раундах гаунтлета бот набивал тройки в хвосте, а голова цепочки тем временем доезжала до черепа)
            var urgent = track ? 60 * danger + 420 * danger * danger * danger * danger : 40 * danger;
            var bonus = s.GroupBonus.Length > s.Group[hit] ? s.GroupBonus[s.Group[hit]] : 0;
            var score = size >= 2 ? 100 + 15 * size + urgent + Combo(s, hit) + bonus
                      : track ? 20 + 10 * (1 - danger) + 0.25f * bonus : 20 + 10 * danger;
            if (score > best.Item1) best = (score, click.Value, hit);
        }
        return best;
    }

    /// <summary>Куда кликнуть, чтобы лягушка стрельнула по лучу: сама цель или ближе по лучу, если цель под окном Клавы.</summary>
    private static PointF? ClickPoint(PointF frog, PointF target, float ux, float uy, float len, float r, Func<PointF, bool> avoid)
    {
        for (var k = 1.0f; k >= 0.35f; k -= 0.13f)
        {
            var d = Math.Max(3.2f * r, len * k);
            var p = new PointF(frog.X + ux * d, frog.Y + uy * d);
            if (!avoid(p)) return p;
        }
        return null;
    }

    /// <summary>Решить ход: выстрел (куда) или смена шаров.</summary>
    public static Move Plan(Scene s, PointF frog, bool swappedLast, bool canSwap, Func<PointF, bool> avoid, Func<int, bool>? skip = null)
    {
        var cur = Best(s, frog, s.Cur, avoid, skip);
        (float Score, PointF Aim, int Hit) nxt = canSwap && !swappedLast && s.Next != s.Cur && s.Next != Tint.None ? Best(s, frog, s.Next, avoid, skip) : (-1f, default, -1);
        string Grp(int hit) => $"группа {s.GroupSize[s.Group[hit]]} ({Name(s.Balls[hit].C)})";
        if (cur.Score >= 100) return new Move(cur.Aim, false, true, $"тройка: {Grp(cur.Hit)}", cur.Hit);
        if (nxt.Score >= 100) return new Move(default, true, false, $"меняю: у {Name(s.Next)} есть тройка");
        if (cur.Score > 0) return new Move(cur.Aim, false, false, $"пара к {Name(s.Cur)}", cur.Hit);
        if (nxt.Score > 0) return new Move(default, true, false, $"меняю: {Name(s.Cur)} некуда");
        // некуда — в пустоту: шар улетает за край и цепочку не удлиняет
        for (var k = 0; k < 36; k++)
        {
            var a = k * MathF.PI / 18;
            float ux = MathF.Cos(a), uy = MathF.Sin(a);
            if (FirstHit(s, frog, ux, uy) >= 0) continue;
            var p = new PointF(frog.X + ux * 4 * s.R, frog.Y + uy * 4 * s.R);
            if (s.Board.Contains(Point.Round(p)) && !avoid(p)) return new Move(p, false, false, $"в пустоту: {Name(s.Cur)} некуда");
        }
        // и пустоты нет — в хвост, подальше от черепа (по трассе — по пути, иначе по прямой): самый дальний шар, до
        // которого луч долетает сам (иначе выстрел встал бы в первый шар по дороге — может, у самой пасти)
        var order = s.Danger.Length == s.Balls.Count
            ? Enumerable.Range(0, s.Balls.Count).OrderBy(i => s.Danger[i]).ToList()
            : Enumerable.Range(0, s.Balls.Count).OrderByDescending(i => s.Skull is { } sk ? Dist(s.Balls[i].X, s.Balls[i].Y, sk.X, sk.Y) : 0).ToList();
        foreach (var i in order)
        {
            float ux = s.Balls[i].X - frog.X, uy = s.Balls[i].Y - frog.Y, len = MathF.Sqrt(ux * ux + uy * uy);
            if (len < 2 * s.R || FirstHit(s, frog, ux / len, uy / len) != i) continue;
            if (ClickPoint(frog, new PointF(s.Balls[i].X, s.Balls[i].Y), ux / len, uy / len, len, s.R, avoid) is { } cp)
                return new Move(cp, false, false, "некуда — в хвост", i);
        }
        var t = s.Balls.Count > 0 ? new PointF(s.Balls[order[0]].X, s.Balls[order[0]].Y) : new PointF(frog.X, frog.Y - 4 * s.R);
        return new Move(t, false, false, "некуда — в хвост");
    }

    // ---------- партия ----------

    /// <summary>Глаза и руки партии: живой экран с мышью или безголовый браузер (проверка).</summary>
    internal interface IIo
    {
        Task<Frame> GrabAsync();
        Task AimAsync(PointF p);
        Task ClickAsync(PointF p, bool right);
        /// <summary>Причина остановиться (друг нажал Backspace/Esc, другое окно); null — играем.</summary>
        string? Interrupted();
        /// <summary>Точка кадра под окном самой Клавы — туда не кликать.</summary>
        bool Avoid(PointF p);
        /// <summary>Лягушка по файлам игры (PopCap: levels.xml), в координатах кадра; null — неизвестна, искать пунктиром.</summary>
        PointF? KnownFrog() => null;
        /// <summary>Поле без служебных полос игры (у PopCap — верхняя панель с полоской прогресса, похожей на шары).</summary>
        Rectangle PlayArea(Rectangle board) => board;
    }

    /// <summary>Сыграть: найти поле, радиус шара, лягушку — и стрелять до конца уровня, времени или стопа.</summary>
    public static async Task<string> PlayCoreAsync(IIo io, TimeSpan limit, CancellationToken ct, Action<Frame, Scene, Move?, PointF?>? debug = null)
    {
        var clock = Stopwatch.StartNew();
        // Поле — только устоявшееся: уровень появляется из затемнения, и в полутьме дерево рамки сходит за камень
        // (поле раздувалось на рамку, её узоры шли в «шары» — и в них летели выстрелы). Два одинаковых поля подряд
        // через 0,4 с при той же яркости — затемнение кончилось
        Frame f = await io.GrabAsync();
        var board = Rectangle.Empty;
        var prev = FindBoard(f);
        var prevLuma = Luma(f, prev);
        for (var i = 0; i < 40 && board.IsEmpty; i++)
        {
            await Task.Delay(400, CancellationToken.None);
            f = await io.GrabAsync();
            var cur = FindBoard(f);
            var luma = Luma(f, cur);
            // края могут дрогнуть на клетку (цепочка ползёт по краю поля) — это то же поле
            var same = Math.Abs(cur.Left - prev.Left) <= 16 && Math.Abs(cur.Top - prev.Top) <= 16
                       && Math.Abs(cur.Right - prev.Right) <= 16 && Math.Abs(cur.Bottom - prev.Bottom) <= 16;
            if (cur.Width >= 300 && same && Math.Abs(luma - prevLuma) < 0.05 * Math.Max(1, prevLuma)) board = Rectangle.Intersect(cur, prev);
            (prev, prevLuma) = (cur, luma);
        }
        if (board.IsEmpty) return "не нашла поле Зумы — открой уровень (пройди меню и рекламу)";
        board = io.PlayArea(board);

        // радиус — только надёжный: в начале уровня цепочка ещё вкатывается, и по паре шаров он выходил втрое меньше
        float r = 0;
        for (var i = 0; i < 24 && r == 0; i++)
        {
            var s0 = See(f, board, null, null, 0);
            if (s0.RSure && s0.Balls.Count >= 5) r = s0.R;
            else { await Task.Delay(400, CancellationToken.None); f = await io.GrabAsync(); }
        }
        if (r == 0) return "поле вижу, а шаров нет — уровень ещё не начался или уже кончился";

        // череп стоит на месте, дуга жёлтых шаров едет: золотое пятно, не сдвинувшееся за 0,7 с, — череп.
        // Без черепа радиус шара пересчитывается начисто: осколки золота мешали медиане
        var skullsA = SkullBoxes(f, board, r);
        await Task.Delay(700, CancellationToken.None);
        f = await io.GrabAsync();
        var skulls = SkullBoxes(f, board, r).Where(b => skullsA.Any(a => Math.Abs(a.X - b.X) <= 3 && Math.Abs(a.Y - b.Y) <= 3
            && Math.Abs(a.Width - b.Width) <= 4 && Math.Abs(a.Height - b.Height) <= 4)).ToList();
        var clean = See(f, board, null, null, 0, skulls);
        if (clean.RSure && clean.Balls.Count >= 5) r = clean.R;

        // лягушка: курсор в разные края поля, ждём, пока повернётся (она плавная), и строим прямую пунктира по самим
        // точкам — любая такая прямая идёт через центр лягушки, пересечение двух и есть он. Прямая «через курсор»
        // промахивалась на 25 px, когда лягушка ещё не довернулась
        var probes = new[] { (0.06f, 0.5f), (0.5f, 0.07f), (0.94f, 0.5f), (0.5f, 0.93f) };
        var lines = new List<(PointF P, PointF Dir, int Count)>();
        // PopCap-версия: пунктира нет, но место лягушки записано в файлах игры — пробы курсором не нужны
        PointF? frog = io.KnownFrog();
        var noDots = frog is not null;
        PointF lastAim = frog is { } kf0 ? new PointF(kf0.X, board.Top + board.Height * 0.1f) : default;
        if (noDots) await io.AimAsync(lastAim);
        foreach (var (fx, fy) in noDots ? [] : probes)
        {
            if (ct.IsCancellationRequested) return "остановилась, не начав";
            lastAim = new PointF(board.X + fx * board.Width, board.Y + fy * board.Height);
            await io.AimAsync(lastAim);
            // Увеличено с 900 до 1200 мс: лягушка поворачивается плавно, может не успеть за 0,9 с
            await Task.Delay(1200, CancellationToken.None);
            f = await io.GrabAsync();
            var seen = See(f, board, null, null, r, skulls);
            if (DotLineFree(OffBalls(FindDots(f, board, r), seen, skulls), r) is { } line) lines.Add(line);
        }
        // все пробы разом: точка, ближайшая ко всем прямым (наименьшие квадраты) — точнее пересечения первых двух
        var hasSpread = false;
        for (var a = 0; a < lines.Count; a++)
            for (var b = a + 1; b < lines.Count; b++)
                if (Cross(lines[a], lines[b]) is not null) hasSpread = true;
        if (!noDots)
        {
            // BUG FIX: если прямых >= 2 но все параллельны (hasSpread=false), всё равно пробуем NearestToLines —
            // у параллельных прямых NearestToLines вернёт null (det≈0), но при lines.Count==1 хоть прямая есть.
            // Если lines.Count==1 и нет пары — берём точку на этой прямой ближе к центру поля как кандидата лягушки.
            if (hasSpread && NearestToLines(lines) is { } c0 && board.Contains(Point.Round(c0))) frog = c0;
            else if (lines.Count >= 2 && NearestToLines(lines) is { } c1 && board.Contains(Point.Round(c1))) frog = c1;
            else if (lines.Count == 1)
            {
                // единственная прямая: проецируем центр поля на неё — приближение лягушки
                var ln = lines[0];
                var cx = board.X + board.Width / 2f;
                var cy = board.Y + board.Height / 2f;
                var proj = (cx - ln.P.X) * ln.Dir.X + (cy - ln.P.Y) * ln.Dir.Y;
                var candidate = new PointF(ln.P.X + ln.Dir.X * proj, ln.P.Y + ln.Dir.Y * proj);
                if (board.Contains(Point.Round(candidate)))
                {
                    frog = candidate;
                    Log.Write($"зума: лягушка по единственной прямой (приближение) ({candidate.X:F0},{candidate.Y:F0})");
                }
            }
        }
        if (frog is not { } F) return "не нашла лягушку: пунктира прицела не видно";
        Log.Write($"зума: поле {board}, шар r={r:F1}, лягушка ({F.X:F0},{F.Y:F0})");

        int shots = 0, pops = 0, swaps = 0, swapFails = 0;
        var swapped = false;
        var swapByFrog = false;   // смена шаров: правая кнопка; не сработала — клик по лягушке
        (Tint Cur, Tint Next)? beforeSwap = null;
        var quiet = Stopwatch.StartNew();
        var covered = Stopwatch.StartNew();    // сколько поле закрыто окном (итоги уровня, меню)
        var colorless = Stopwatch.StartNew();  // сколько не видно цвета в пасти
        var baseStone = StoneShare(f, board);
        while (!ct.IsCancellationRequested)
        {
            if (clock.Elapsed > limit) return $"время вышло. {Tally(shots, pops, swaps)}";
            if (io.Interrupted() is { } why) return $"{why} — остановилась. {Tally(shots, pops, swaps)}";
            f = await io.GrabAsync();
            // камня на поле не стало — сверху окно итогов («Пройдено» или проигрыш) или меню: партия кончилась.
            // Кнопки там не жмём: «+очков», «+монет» — это просмотр рекламы
            if (StoneShare(f, board) < 0.4 * baseStone)
            {
                if (covered.Elapsed > TimeSpan.FromSeconds(1.5))
                    return $"уровень кончился — на поле окно итогов (пройден или проигран — видно на экране). {Tally(shots, pops, swaps)}";
                await Task.Delay(200, CancellationToken.None);
                continue;
            }
            covered.Restart();
            var s = See(f, board, F, lastAim, r, skulls);
            if (beforeSwap is { } bs)
            {
                // поменялись ли шары: иначе пробуем другой способ, дважды мимо обоими — больше не меняем
                if (s.Cur == bs.Next && s.Next == bs.Cur) swapFails = 0;
                else { swapFails++; swapByFrog = !swapByFrog; }
                beforeSwap = null;
            }
            if (s.Balls.Count == 0)
            {
                if (quiet.Elapsed > TimeSpan.FromSeconds(6)) return $"шаров на поле больше нет — уровень кончился. {Tally(shots, pops, swaps)}";
                await Task.Delay(200, CancellationToken.None);
                continue;
            }
            quiet.Restart();
            if (s.Cur == Tint.None)
            {
                // цвет в пасти не разглядела (шар летит / пунктир погас без движения мыши)
                if (colorless.ElapsedMilliseconds > 600 && lastAim != default)
                {
                    // шевелим мышь по микро-кругу, чтобы разбудить пунктир в игре
                    var wiggleX = (float)(Math.Sin(colorless.ElapsedMilliseconds / 120.0) * 10f);
                    var wiggleY = (float)(Math.Cos(colorless.ElapsedMilliseconds / 120.0) * 10f);
                    await io.AimAsync(new PointF(lastAim.X + wiggleX, lastAim.Y + wiggleY));
                }
                if (colorless.ElapsedMilliseconds > 2500 && s.Next != Tint.None)
                {
                    // пробуем перезарядить шар кликом правой кнопки
                    await io.ClickAsync(lastAim, true);
                    await Task.Delay(200, CancellationToken.None);
                }
                if (colorless.Elapsed > TimeSpan.FromSeconds(12))
                    return $"потеряла цвет шара из виду уже 12 с — похоже, пауза или меню. {Tally(shots, pops, swaps)}";
                debug?.Invoke(f, s, null, F);
                await Task.Delay(100, CancellationToken.None);
                continue;
            }
            colorless.Restart();
            var mv = Plan(s, F, swapped, swapFails < 4, io.Avoid);
            debug?.Invoke(f, s, mv, F);
            if (mv.Swap)
            {
                beforeSwap = (s.Cur, s.Next);
                await io.ClickAsync(swapByFrog ? F : lastAim, !swapByFrog);
                swaps++;
                swapped = true;
                await Task.Delay(180, CancellationToken.None);
                continue;
            }
            swapped = false;
            await io.AimAsync(mv.Aim);
            // стрелять, когда пунктир лёг на цель: лягушка доворачивается плавно, и сразу после движения курсора
            // шар ушёл бы туда, куда она ещё смотрела
            // лягушка PopCap поворачивается за курсором сразу; у браузерной — плавно, там ждём пунктир на цели
            if (noDots) await Task.Delay(70, CancellationToken.None);
            else await WaitAimAsync(io, board, F, mv.Aim, r, TimeSpan.FromMilliseconds(900));
            await io.ClickAsync(mv.Aim, false);
            lastAim = mv.Aim;
            shots++;
            if (mv.Pop) pops++;
            _status = $"играю в Зуму: выстрелов {shots}, в тройку {pops}";
            // PopCap перезаряжает лягушку быстро: лишняя пауза — это медленная игра (3:12 на уровне против эталона 0:35)
            await Task.Delay(noDots ? 110 : 320, CancellationToken.None);
        }
        return $"остановили. {Tally(shots, pops, swaps)}";
    }

    private static string Tally(int shots, int pops, int swaps) => $"Выстрелов: {shots}, в тройку: {pops}, смен шара: {swaps}";

    // ---------- проверка ----------

    /// <summary>Картинка для отладки: шары с цветом и размером группы, череп, лягушка, выбранный выстрел.</summary>
    public static Bitmap Annotate(Bitmap src, Scene s, Move? mv, PointF? frog)
    {
        var bmp = new Bitmap(src);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var font = new Font("Segoe UI", 9, FontStyle.Bold);
        g.DrawRectangle(Pens.Lime, s.Board);
        for (var i = 0; i < s.Balls.Count; i++)
        {
            var b = s.Balls[i];
            var col = b.C switch
            {
                Tint.Red => Color.Red, Tint.Yellow => Color.Yellow, Tint.Green => Color.Lime, Tint.Blue => Color.DeepSkyBlue,
                Tint.Purple => Color.Magenta, Tint.White => Color.White, _ => Color.Gray,
            };
            using var pen = new Pen(col, 2);
            g.DrawEllipse(pen, b.X - s.R, b.Y - s.R, 2 * s.R, 2 * s.R);
            g.DrawString(s.GroupSize[s.Group[i]].ToString(), font, Brushes.White, b.X - 4, b.Y - 7);
        }
        if (s.Skull is { } sk) g.DrawEllipse(new Pen(Color.Gold, 3), sk.X - 2 * s.R, sk.Y - 2 * s.R, 4 * s.R, 4 * s.R);
        if (frog is { } F)
        {
            g.DrawLine(new Pen(Color.Lime, 2), F.X - 10, F.Y, F.X + 10, F.Y);
            g.DrawLine(new Pen(Color.Lime, 2), F.X, F.Y - 10, F.X, F.Y + 10);
            g.DrawString($"сейчас {Name(s.Cur)} ({s.CurVotes}), след. {Name(s.Next)}", font, Brushes.Lime, F.X + 12, F.Y + 12);
            if (mv is { Swap: false })
            {
                g.DrawLine(new Pen(Color.Orange, 2), F, mv.Aim);
                g.DrawEllipse(new Pen(Color.Orange, 3), mv.Aim.X - 6, mv.Aim.Y - 6, 12, 12);
            }
            if (mv is not null) g.DrawString(mv.Why, font, Brushes.Orange, s.Board.X + 6, s.Board.Y + 6);
        }
        return bmp;
    }

    /// <summary>KlavaDesk.exe --zuma-look снимок.png [лягушка_x лягушка_y курсор_x курсор_y] — что видит автопилот.</summary>
    public static int LookTest(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length == 0) { Console.WriteLine("нужен снимок: --zuma-look кадр.png [fx fy cx cy]"); return 2; }
        using var img = new Bitmap(args[0]);
        var f = new Frame(img);
        var board = FindBoard(f);
        Console.WriteLine($"поле {board}");
        if (board.Width < 300) return 1;
        PointF? frog = args.Length >= 3 ? new PointF(float.Parse(args[1]), float.Parse(args[2])) : null;
        PointF? aim = args.Length >= 5 ? new PointF(float.Parse(args[3]), float.Parse(args[4])) : null;
        // по одному кадру не понять, что стоит на месте, — черепом считаем любое крупное плотное золото
        var skulls = SkullBoxes(f, board, See(f, board, null, null, 0).R);
        var s = See(f, board, frog, aim, 0, skulls);
        Console.WriteLine($"шар r={s.R:F1}, шаров в цепочке {s.Balls.Count}, групп {s.GroupSize.Length}, череп {(s.Skull is { } k ? $"({k.X:F0},{k.Y:F0})" : "—")}");
        foreach (var t in Enum.GetValues<Tint>().Skip(1))
        {
            var n = s.Balls.Count(b => b.C == t);
            if (n > 0) Console.WriteLine($"  {Name(t)}: {n}");
        }
        var dots = OffBalls(FindDots(f, board, s.R), s, skulls);
        Console.WriteLine($"точек пунктира: {dots.Count}");
        if (DotLineFree(dots, s.R) is { } line) Console.WriteLine($"прямая пунктира: {line.Count} точек через ({line.P.X:F0},{line.P.Y:F0}), направление ({line.Dir.X:F2},{line.Dir.Y:F2})");
        if (frog is { } fr && SightLine(dots, fr, s.R) is { } sl) Console.WriteLine($"лягушка смотрит ({sl.Dir.X:F2},{sl.Dir.Y:F2}), точек {sl.Count}, цвет {Name(sl.T)}");
        Move? mv = null;
        if (frog is { } F)
        {
            Console.WriteLine($"сейчас {Name(s.Cur)} (точек {s.CurVotes}), следующий {Name(s.Next)}");
            mv = Plan(s, F, false, true, _ => false);
            Console.WriteLine($"ход: {(mv.Swap ? "сменить шары" : $"стрелять в ({mv.Aim.X:F0},{mv.Aim.Y:F0})")} — {mv.Why}");
        }
        var outFile = Path.ChangeExtension(args[0], null) + "_look.png";
        using (var ann = Annotate(img, s, mv, frog)) ann.Save(outFile, ImageFormat.Png);
        Console.WriteLine($"картинка: {outFile}");
        return s.Balls.Count > 0 ? 0 : 1;
    }
}
