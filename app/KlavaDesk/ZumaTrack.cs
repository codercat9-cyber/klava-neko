using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Уровень локальной Zuma Deluxe прямо из файлов игры — вместо угадывания по картинке:
/// levels\levels.xml (&lt;Graphics id image curve curve2 gx gy dispname&gt;), трасса levels\id\curve.dat, фон levels\id\image.jpg.
///
/// Формат трассы (разобран 27.09 по spiral.dat/underover.dat): "CURV", int версия (2), int 1, int размер блока
/// опорных точек в байтах (с их счётчиком), блок опорных точек редактора (int n, по 10 байт) — пропускаем; дальше
/// плотный путь: int N, float x, float y, 2 байта флагов, затем N−1 записей по 4 байта: sbyte dx, sbyte dy (в сотых
/// пикселя), байт «под туннелем» (шар под картинкой-крышкой, его не видно и в него не попасть), байт слоя.
/// Шаг пути ≈ 1 px, поэтому номер точки ≈ пройденный путь в пикселях 640×480. Первая точка — за краем экрана
/// (оттуда выкатывается цепочка), последняя — в пасти черепа.
/// </summary>
internal sealed class ZumaTrack
{
    public const int W = 640, H = 480;
    /// <summary>Радиус шара PopCap в координатах 640×480 (замер: 48 px шара на окне ×1,5).</summary>
    public const float BallR = 16f;
    /// <summary>Центр шара в пасти и камень-подсказка на спине — от центра лягушки (замер по кадру 12-1: 28 и 25 px).</summary>
    public const float MouthD = 28f, BackD = 25f;
    /// <summary>Верхняя панель игры (жизни, уровень, очки, MENU) — в координатах 640×480.</summary>
    public const int Hud = 34;

    internal sealed class Curve
    {
        public required float[] X, Y;
        public required bool[] Hidden;
        public int Count => X.Length;
        public PointF At(float idx)
        {
            var i = Math.Clamp((int)idx, 0, Count - 1);
            var j = Math.Min(i + 1, Count - 1);
            var t = Math.Clamp(idx - i, 0, 1);
            return new PointF(X[i] + (X[j] - X[i]) * t, Y[i] + (Y[j] - Y[i]) * t);
        }
    }

    public required string Id { get; init; }
    public required string Name { get; init; }
    public PointF Gun { get; init; }
    public required List<Curve> Curves { get; init; }
    /// <summary>Фон уровня, BGR 640×480.</summary>
    public required byte[] Bg { get; init; }
    /// <summary>Яркость фона 80×60 — узнавать уровень по кадру.</summary>
    public required float[] Thumb { get; init; }

    private const int TW = 80, TH = 60;
    private static readonly Dictionary<string, List<ZumaTrack>> Cache = new(StringComparer.OrdinalIgnoreCase);

    // ---------------- загрузка ----------------

    /// <summary>Все уровни игры (кэш на папку игры). Битые/недостающие файлы пропускаются.</summary>
    public static List<ZumaTrack> All(string gameDir)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(gameDir, out var list)) return list;
            list = [];
            var xml = Path.Combine(gameDir, "levels", "levels.xml");
            if (File.Exists(xml))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(xml), @"<Graphics\s[^>]*>", RegexOptions.IgnoreCase))
                {
                    string? A(string name) => Regex.Match(m.Value, $@"\b{name}\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase) is { Success: true } a ? a.Groups[1].Value : null;
                    var id = A("id");
                    if (id is null || !int.TryParse(A("gx"), out var gx) || !int.TryParse(A("gy"), out var gy)) continue;
                    var dir = Path.Combine(gameDir, "levels", id);
                    var curves = new List<Curve>();
                    foreach (var name in new[] { A("curve"), A("curve2") })
                        if (name is not null && ReadCurve(Path.Combine(dir, name + ".dat")) is { } c) curves.Add(c);
                    var jpg = Path.Combine(dir, (A("image") ?? id) + ".jpg");
                    if (curves.Count == 0 || !File.Exists(jpg)) continue;
                    try
                    {
                        using var src = new Bitmap(jpg);
                        var (bg, thumb) = Background(src);
                        list.Add(new ZumaTrack { Id = id, Name = A("dispname") ?? id, Gun = new PointF(gx, gy), Curves = curves, Bg = bg, Thumb = thumb });
                    }
                    catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
                    {
                        Log.Write($"зума: фон {jpg} не прочитался: {ex.Message}");
                    }
                }
            }
            Cache[gameDir] = list;
            return list;
        }
    }

    /// <summary>Плотный путь из .dat; null — не тот формат.</summary>
    public static Curve? ReadCurve(string file)
    {
        if (!File.Exists(file)) return null;
        var b = File.ReadAllBytes(file);
        if (b.Length < 32 || b[0] != 'C' || b[1] != 'U' || b[2] != 'R' || b[3] != 'V') return null;
        var ctrl = BitConverter.ToInt32(b, 12);
        var o = 16 + ctrl;
        if (ctrl < 4 || o + 14 > b.Length) return null;
        var n = BitConverter.ToInt32(b, o);
        if (n < 10 || o + 14 + (long)(n - 1) * 4 > b.Length) return null;
        var X = new float[n];
        var Y = new float[n];
        var hid = new bool[n];
        float x = BitConverter.ToSingle(b, o + 4), y = BitConverter.ToSingle(b, o + 8);
        X[0] = x; Y[0] = y;
        hid[0] = b[o + 12] != 0;
        var p = o + 14;
        for (var i = 1; i < n; i++, p += 4)
        {
            x += (sbyte)b[p] / 100f;
            y += (sbyte)b[p + 1] / 100f;
            X[i] = x; Y[i] = y;
            hid[i] = b[p + 2] != 0;
        }
        return new Curve { X = X, Y = Y, Hidden = hid };
    }

    private static (byte[] Bg, float[] Thumb) Background(Bitmap src)
    {
        using var full = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, new Rectangle(0, 0, W, H));
        }
        var bg = new byte[W * H * 3];
        var d = full.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (var y = 0; y < H; y++) Marshal.Copy(d.Scan0 + y * d.Stride, bg, y * W * 3, W * 3);
        }
        finally { full.UnlockBits(d); }
        return (bg, ThumbOf(full));
    }

    /// <summary>Яркость картинки, ужатой до 80×60 (среднее по клеткам).</summary>
    public static float[] ThumbOf(Bitmap bmp)
    {
        using var small = new Bitmap(TW, TH, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(bmp, new Rectangle(0, 0, TW, TH));
        }
        var t = new float[TW * TH];
        var d = small.LockBits(new Rectangle(0, 0, TW, TH), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[d.Stride];
            for (var y = 0; y < TH; y++)
            {
                Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, d.Stride);
                for (var x = 0; x < TW; x++) t[y * TW + x] = 0.114f * row[x * 3] + 0.587f * row[x * 3 + 1] + 0.299f * row[x * 3 + 2];
            }
        }
        finally { small.UnlockBits(d); }
        return t;
    }

    /// <summary>
    /// Какой уровень на кадре игры: по фону, устойчиво к шарам — берётся 40-й перцентиль разницы яркости (шары и
    /// лягушка закрывают меньше половины поля даже на поздних уровнях), верхняя панель не считается. null — не уровень
    /// (меню, карта, затемнение между экранами).
    /// </summary>
    public static (ZumaTrack Level, float Diff)? Identify(Bitmap game, string gameDir)
    {
        var all = All(gameDir);
        if (all.Count == 0) return null;
        var t = ThumbOf(game);
        var y0 = (int)Math.Ceiling(Hud / (float)H * TH) + 1;
        (ZumaTrack L, float D)? best = null;
        var diffs = new float[(TH - y0) * TW];
        foreach (var lv in all)
        {
            var k = 0;
            for (var y = y0; y < TH; y++)
                for (var x = 0; x < TW; x++, k++)
                    diffs[k] = Math.Abs(lv.Thumb[y * TW + x] - t[y * TW + x]);
            Array.Sort(diffs);
            var p40 = diffs[(int)(0.4 * (diffs.Length - 1))];
            if (best is null || p40 < best.Value.D) best = (lv, p40);
        }
        return best is { } b && b.D < 14 ? b : null;
    }

    // ---------------- шары на трассе ----------------

    /// <summary>Шар на трассе: номер кривой, место на пути (px 640×480 от входа), цвет, бонус (−1 — нет; из памяти игры).</summary>
    /// <param name="Busy">В шар уже встаёт выстрел (память игры) — по его группе второй раз не бить.</param>
    internal readonly record struct TrackBall(int Curve, float Idx, ZumaBot.Tint T, int Power = -1, bool Busy = false);

    /// <summary>Бонусы шаров PopCap (поле Ball+60; по картинкам ловли 28.09): 0 бомба, 1 замедление, 2 меткость, 3 назад.</summary>
    public const int PowerBomb = 0, PowerSlow = 1, PowerAccuracy = 2, PowerBackwards = 3;

    /// <summary>
    /// Цвет пикселя шара PopCap. Замер 27.09 (12-1): синий (47,233,247)…(41,37,172), жёлтый (247,247,28)…(201,189,16),
    /// красный (248,10,19), зелёный (75,247,108), фиолетовый (228,145,228)…(138,24,142), белый — тёплый светло-серый
    /// (218,198,175). Камень поля бывает рыжим (173,146,108 — разброс 65), поэтому порог насыщенности 70, а белый
    /// отличается от камня ещё и фоном (см. <see cref="Scan"/>).
    /// </summary>
    public static ZumaBot.Tint BallTint(int r, int g, int b)
    {
        int mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b)), ch = mx - mn;
        if (ch >= 70 && mx >= 110)
        {
            double h = mx == r ? 60.0 * (g - b) / ch : mx == g ? 120 + 60.0 * (b - r) / ch : 240 + 60.0 * (r - g) / ch;
            if (h < 0) h += 360;
            return h < 20 || h >= 330 ? ZumaBot.Tint.Red : h < 75 ? ZumaBot.Tint.Yellow : h < 160 ? ZumaBot.Tint.Green
                 : h < 262 ? ZumaBot.Tint.Blue : ZumaBot.Tint.Purple;
        }
        // белый шар — тёплый светло-серый с тёмной резьбой: на камне его держит ещё и разница с фоном
        return ch <= 60 && mn >= 140 ? ZumaBot.Tint.White : ZumaBot.Tint.None;
    }

    // круг голосования вокруг точки пути: центр, 6 точек на 4,5 px и 6 на 9 px (шар — 16)
    private static readonly (float X, float Y)[] Disk = BuildDisk();
    private static (float, float)[] BuildDisk()
    {
        var d = new List<(float, float)> { (0, 0) };
        for (var a = 0; a < 6; a++) d.Add((4.5f * MathF.Cos(a * MathF.PI / 3), 4.5f * MathF.Sin(a * MathF.PI / 3)));
        for (var a = 0; a < 6; a++) d.Add((9f * MathF.Cos((a + 0.5f) * MathF.PI / 3), 9f * MathF.Sin((a + 0.5f) * MathF.PI / 3)));
        return d.ToArray();
    }

    /// <summary>Сколько подряд шаров одного цвета даёт отрезок длины L (px пути): одиночный шар виден на ~26 px.</summary>
    public static int BallsInRun(float len) => Math.Max(1, (int)MathF.Round((len + 6) / (2 * BallR)));

    /// <summary>
    /// Шары вдоль трассы. Каждые 2 px пути — круг из 13 точек: пиксель как на фоне (разница &lt; 38) — пусто, иначе цвет
    /// шара или «непонятно» (резьба белого шара, взрыв, летящий шар, стык двух шаров). Отрезки одного цвета → шары
    /// через 32 px; «непонятное» до 12 px внутри одного цвета отрезок не рвёт (белые шары с тёмной резьбой иначе
    /// рассыпались на пять кусочков и давали лишние шары). Невидимое (туннель, за краем, под панелью, пасть черепа,
    /// лягушка, курсор) отрезок закрывает.
    /// </summary>
    /// <param name="f">Кадр игры (без полей), любого масштаба 4:3.</param>
    /// <param name="cursor">Где нарисован курсор игры (640×480) — его стрелка не шар.</param>
    public List<TrackBall> Scan(ZumaBot.Frame f, PointF? cursor, List<(int Curve, int From, int To, ZumaBot.Tint T)>? runs = null)
    {
        float kx = f.W / (float)W, ky = f.H / (float)H;
        var balls = new List<TrackBall>();
        var cnt = new int[8];
        for (var c = 0; c < Curves.Count; c++)
        {
            var cv = Curves[c];
            int runFrom = -1, runTo = -1;
            var runT = ZumaBot.Tint.None;
            void Close()
            {
                if (runFrom >= 0 && runT != ZumaBot.Tint.None)
                {
                    var len = runTo - runFrom;
                    var n = BallsInRun(len);
                    var mid = (runFrom + runTo) / 2f;
                    for (var j = 0; j < n; j++) balls.Add(new TrackBall(c, mid + (j - (n - 1) / 2f) * 2 * BallR, runT));
                    runs?.Add((c, runFrom, runTo, runT));
                }
                runFrom = runTo = -1;
                runT = ZumaBot.Tint.None;
            }
            var end = cv.Count - 40;   // последние 40 px пути — в пасти черепа (его золото — «жёлтый шар»)
            for (var i = 0; i < end; i += 2)
            {
                float x = cv.X[i], y = cv.Y[i];
                var unseen = cv.Hidden[i] || x < 4 || y < Hud + 4 || x > W - 5 || y > H - 5
                             || (x - Gun.X) * (x - Gun.X) + (y - Gun.Y) * (y - Gun.Y) < 44 * 44
                             || cursor is { } cu && x > cu.X - 6 && x < cu.X + 18 && y > cu.Y - 6 && y < cu.Y + 26;
                if (unseen) { Close(); continue; }
                Array.Clear(cnt);
                foreach (var (dx, dy) in Disk)
                {
                    int lx = (int)(x + dx), ly = (int)(y + dy);
                    if (lx < 0 || ly < 0 || lx >= W || ly >= H) { cnt[7]++; continue; }
                    int fx = Math.Min(f.W - 1, (int)((x + dx) * kx)), fy = Math.Min(f.H - 1, (int)((y + dy) * ky));
                    var pi = fy * f.Stride + fx * 3;
                    int pb = f.Px[pi], pg = f.Px[pi + 1], pr = f.Px[pi + 2];
                    var bi = (ly * W + lx) * 3;
                    var diff = Math.Max(Math.Abs(pb - Bg[bi]), Math.Max(Math.Abs(pg - Bg[bi + 1]), Math.Abs(pr - Bg[bi + 2])));
                    if (diff < 38) { cnt[0]++; continue; }
                    var pt = BallTint(pr, pg, pb);
                    cnt[pt == ZumaBot.Tint.None ? 7 : (int)pt]++;   // 0 — фон, 7 — не фон и не шар
                }
                // решение по кругу: цвет большинством, белый — только без цветных (блики цветных шаров белые)
                var best = ZumaBot.Tint.None;
                int bestN = 0, second = 0;
                for (var t = 1; t <= 5; t++)
                {
                    if (cnt[t] > bestN) { second = bestN; bestN = cnt[t]; best = (ZumaBot.Tint)t; }
                    else if (cnt[t] > second) second = cnt[t];
                }
                if (bestN >= 7 && bestN >= 2 * second) { }
                else if (cnt[(int)ZumaBot.Tint.White] >= 5 && bestN + second <= 2) best = ZumaBot.Tint.White;
                else if (cnt[0] >= 8) { Close(); continue; }          // пусто: жёлоб как на фоне
                else
                {
                    // непонятно: внутри отрезка до 12 px терпим (резьба белого, стык, блик), дальше — отрезок кончился
                    if (runFrom >= 0 && i - runTo <= 12) continue;
                    Close();
                    continue;
                }
                if (best == runT && runFrom >= 0 && i - runTo <= 12) { runTo = i; continue; }
                Close();
                runFrom = runTo = i;
                runT = best;
            }
            Close();
        }
        return balls;
    }
}
