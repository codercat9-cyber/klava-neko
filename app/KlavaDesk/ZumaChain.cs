using System.Diagnostics;
using System.Text;

namespace KlavaDesk;

/// <summary>
/// Цепочка шаров Зумы прямо из памяти игры (одиночная, друг разрешил): классы движка PopCap находятся по RTTI в самом
/// zuma.exe («.?AVBall@Sexy@@» → TypeDescriptor → CompleteObjectLocator → vtable), дальше объекты по vtable.
/// Разведка 28.09 на живом уровне SG 1:
///   Board: +04…+18 — CurveMgr* (до 6 кривых);
///   CurveMgr: +2C — std::list&lt;Ball*&gt; шаров (+30 голова, +34 сколько), узел списка {next, prev, Ball*};
///   Ball (0x70): +04 номер, +08 цвет 0..5, +0C место на трассе (точки плотного пути, шаг шаров 32),
///   +10/+14 поворот, +1C/+20 x/y в координатах 640×480, +24 → список кривой, +28 → свой узел.
/// Всё читается за ~1 мс: глаза не нужны ни для туннелей, ни под лягушкой, ни под курсором.
/// </summary>
internal sealed partial class ZumaMemory
{
    /// <summary>vtable классов движка в этой сборке игры (0 — класс не нашёлся).</summary>
    internal sealed record Classes(uint Ball, uint Bullet, uint CurveMgr, uint Gun, uint Board);

    private static readonly Dictionary<string, Classes?> ClassCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>vtable по RTTI из файла exe (без ASLR: образ всегда по своему ImageBase).</summary>
    public static Classes? ClassesOf(string exe)
    {
        lock (ClassCache)
        {
            if (ClassCache.TryGetValue(exe, out var known)) return known;
            Classes? res = null;
            try { res = ParseClasses(File.ReadAllBytes(exe)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            ClassCache[exe] = res;
            return res;
        }
    }

    private static Classes? ParseClasses(byte[] b)
    {
        if (b.Length < 0x400 || b[0] != 'M' || b[1] != 'Z') return null;
        var pe = BitConverter.ToInt32(b, 0x3C);
        if (pe <= 0 || pe + 0xF8 > b.Length || BitConverter.ToUInt32(b, pe) != 0x4550) return null;
        int sections = BitConverter.ToUInt16(b, pe + 6), optSize = BitConverter.ToUInt16(b, pe + 20), opt = pe + 24;
        if (BitConverter.ToUInt16(b, opt) != 0x10B) return null;                // только 32-битный PE
        var imageBase = BitConverter.ToUInt32(b, opt + 28);
        var secs = new List<(uint Va, uint Raw, uint Size)>();
        for (var i = 0; i < sections; i++)
        {
            var o = opt + optSize + 40 * i;
            secs.Add((BitConverter.ToUInt32(b, o + 12), BitConverter.ToUInt32(b, o + 20), BitConverter.ToUInt32(b, o + 16)));
        }
        uint Va(int off)
        {
            foreach (var s in secs)
                if (off >= s.Raw && off < s.Raw + s.Size) return imageBase + s.Va + (uint)(off - s.Raw);
            return 0;
        }
        List<int> Find(uint v)
        {
            var res = new List<int>();
            for (var i = 0; i + 4 <= b.Length; i += 4)
                if (BitConverter.ToUInt32(b, i) == v) res.Add(i);
            return res;
        }
        uint VTable(string cls)
        {
            var name = Encoding.ASCII.GetBytes($".?AV{cls}@Sexy@@\0");
            var at = b.AsSpan().IndexOf(name);
            if (at < 8) return 0;
            var td = Va(at) - 8;                                                  // TypeDescriptor: pVFTable, spare, name
            foreach (var p in Find(td))
            {
                // CompleteObjectLocator: signature 0, offset (0 — главная база), cdOffset, pTypeDescriptor, pClassHierarchy
                if (p < 12 || BitConverter.ToUInt32(b, p - 12) != 0 || BitConverter.ToUInt32(b, p - 8) != 0) continue;
                var col = Va(p - 12);
                foreach (var q in Find(col))
                    if (Va(q) is var v && v != 0) return v + 4;                   // vtable[-1] = COL
            }
            return 0;
        }
        var c = new Classes(VTable("Ball"), VTable("Bullet"), VTable("CurveMgr"), VTable("Gun"), VTable("Board"));
        return c.Ball == 0 || c.CurveMgr == 0 || c.Board == 0 ? null : c;
    }

    /// <summary>Путь к exe процесса игры.</summary>
    public string? ExePath()
    {
        try { using var p = Process.GetProcessById(Pid); return p.MainModule?.FileName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>Шар цепочки из памяти.</summary>
    /// <param name="Curve">Номер кривой (порядок CurveMgr у Board).</param>
    /// <param name="Way">Место на трассе — индекс точки плотного пути (как ZumaTrack.Idx).</param>
    /// <param name="Power">Бонус шара (+60; у обычного <see cref="PowerNone"/>).</param>
    /// <param name="Dying">Шар уже лопается (+34 — счётчик взрыва): через доли секунды его не будет.</param>
    /// <param name="Incoming">В шар уже встаёт выстрел (+30 — Bullet*): вторым выстрелом туда не бить.</param>
    internal readonly record struct MemBall(int Curve, int Type, float Way, float X, float Y, uint Addr, int Power, bool Dying, bool Incoming);

    /// <summary>
    /// Лягушка (Board → Gun): цвета в пасти и на спине, состояние выстрела. Замер 28.09: щелчок → State 1 (плевок, ~65 мс),
    /// шары в пасти сменяются → State 2 (новый вкатывается, ~160 мс) → 0 — готова. Щелчок не в State 0 игра глотает;
    /// весь цикл ≈ 235 мс, то есть больше ~4 выстрелов в секунду не выйдет ни у кого.
    /// </summary>
    /// <param name="Speed">Скорость выстрела, px/с в 640×480 (Gun+44 — px за тик 10 мс: 6 обычно, больше с бонусом меткости).</param>
    internal readonly record struct MemFrog(int Cur, int Next, int State, uint CurPtr, uint NextPtr, float Speed);

    public const int BoardCurves = 6, BallSize = 0x70, PowerNone = 4;

    private uint _board;
    private int _curveOff, _gunOff;
    private Classes? _cls;

    /// <summary>Board найден и жив (уровень идёт).</summary>
    public bool OnBoard => _cls is not null && _board != 0 && ReadU32(_board) == _cls.Board;

    /// <summary>
    /// Режим демона (одиночная игра, друг разрешил читы): перекрасить шар в пасти — записать тип 0..5 в Bullet+08
    /// (объект по Gun+2C). Игра рисует и вставляет шар по этому полю, так что выстрел ложится как шар нужного цвета.
    /// true — записалось и читается обратно.
    /// </summary>
    public bool SetFrogBall(int type)
    {
        if (type is < 0 or > 5 || _cls is null || _board == 0 || _gunOff == 0) return false;
        if (ReadU32(_board + (uint)_gunOff) is not { } gun || ReadU32(gun) != _cls.Gun) return false;
        if (ReadU32(gun + 0x2C) is not { } cp || cp < 0x10000) return false;
        return WriteU32(cp + 8, (uint)type) && ReadU32(cp + 8) == (uint)type;
    }

    /// <summary>Лягушка сейчас; null — Board/Gun пропали или шары ещё не встали.</summary>
    public MemFrog? ReadFrog()
    {
        if (_cls is null || _board == 0 || _gunOff == 0 || ReadU32(_board + (uint)_gunOff) is not { } gun || gun < 0x10000) return null;
        if (Read(gun, 0x48) is not { } g || BitConverter.ToUInt32(g, 0) != _cls.Gun) return null;
        uint cp = BitConverter.ToUInt32(g, 0x2C), np = BitConverter.ToUInt32(g, 0x30);
        if (cp < 0x10000 || np < 0x10000 || ReadU32(cp + 8) is not { } ct || ReadU32(np + 8) is not { } nt || ct > 5 || nt > 5) return null;
        var speed = BitConverter.ToSingle(g, 0x44);
        return new MemFrog((int)ct, (int)nt, BitConverter.ToInt32(g, 0x34), cp, np, speed is > 1 and < 50 ? speed * 100 : 600);
    }

    /// <summary>Board текущей партии: один раз слепком по vtable, дальше проверка на месте. false — не в игре.</summary>
    public bool FindBoard()
    {
        _cls ??= ExePath() is { } exe ? ClassesOf(exe) : null;
        if (_cls is null) return false;
        if (_board != 0 && ReadU32(_board) == _cls.Board && CurvesOf(_board).Count > 0) return true;
        _board = 0;
        foreach (var a in FindValue(Snapshot(), _cls.Board))
        {
            // массив CurveMgr* внутри Board (28.09: Board+5C) — первое поле, указывающее на объект CurveMgr
            if (Read(a, 0x400) is not { } d) continue;
            for (var off = 4; off < 0x400 && _board == 0; off += 4)
                if (BitConverter.ToUInt32(d, off) is var p && p > 0x10000 && ReadU32(p) == _cls.CurveMgr)
                {
                    _board = a;
                    _curveOff = off;
                }
            if (_board == 0) continue;
            // лягушка — поле Board, указывающее на Gun (28.09: Board+268)
            _gunOff = 0;
            for (var off = 4; off < 0x400 && _gunOff == 0; off += 4)
                if (BitConverter.ToUInt32(d, off) is var p && p > 0x10000 && ReadU32(p) == _cls.Gun) _gunOff = off;
            return true;
        }
        return false;
    }

    /// <summary>CurveMgr, на которые указывает Board (проверены по vtable).</summary>
    private List<uint> CurvesOf(uint board)
    {
        var res = new List<uint>();
        if (_cls is null || _curveOff == 0 || Read(board + (uint)_curveOff, 4 * BoardCurves) is not { } d) return res;
        for (var i = 0; i < BoardCurves; i++)
        {
            var p = BitConverter.ToUInt32(d, 4 * i);
            if (p > 0x10000 && ReadU32(p) == _cls.CurveMgr) res.Add(p);
        }
        return res;
    }

    /// <summary>Все шары всех кривых: цвет, место на трассе, x/y. null — Board пропал (уровень сменился).</summary>
    public List<MemBall>? ReadChain()
    {
        if (_cls is null || _board == 0 || ReadU32(_board) != _cls.Board) return null;
        var res = new List<MemBall>();
        var curves = CurvesOf(_board);
        for (var ci = 0; ci < curves.Count; ci++)
        {
            if (Read(curves[ci] + 0x30, 8) is not { } lst) continue;
            var head = BitConverter.ToUInt32(lst, 0);
            var count = BitConverter.ToInt32(lst, 4);
            if (head < 0x10000 || count is < 0 or > 1000) continue;
            var node = ReadU32(head) ?? head;
            for (var n = 0; n < count && node != head && node > 0x10000; n++)
            {
                if (Read(node, 12) is not { } nd) break;
                var ball = BitConverter.ToUInt32(nd, 8);
                if (Read(ball, BallSize) is { } bd && BitConverter.ToUInt32(bd, 0) == _cls.Ball)
                    res.Add(new MemBall(ci, BitConverter.ToInt32(bd, 8), BitConverter.ToSingle(bd, 0x0C),
                        BitConverter.ToSingle(bd, 0x1C), BitConverter.ToSingle(bd, 0x20), ball,
                        BitConverter.ToInt32(bd, 0x60), BitConverter.ToInt32(bd, 0x34) != 0, BitConverter.ToUInt32(bd, 0x30) != 0));
                node = BitConverter.ToUInt32(nd, 0);
            }
        }
        return res;
    }

    /// <summary>
    /// KlavaDesk.exe --zuma-mem rtti — классы по RTTI, Board, чьи указатели у Board (по vtable), шары каждой кривой.
    /// </summary>
    private static int RttiTest(ZumaMemory mem, string[] args)
    {
        var exe = mem.ExePath();
        Console.WriteLine($"exe {exe}");
        if (exe is null || ClassesOf(exe) is not { } c) { Console.WriteLine("классы по RTTI не нашлись"); return 1; }
        Console.WriteLine($"vtable: Ball {c.Ball:X8}, Bullet {c.Bullet:X8}, CurveMgr {c.CurveMgr:X8}, Gun {c.Gun:X8}, Board {c.Board:X8}");
        var sw = Stopwatch.StartNew();
        if (!mem.FindBoard()) { Console.WriteLine("Board не нашёлся (не в игре?)"); return 1; }
        Console.WriteLine($"Board {mem._board:X8} ({sw.ElapsedMilliseconds} мс)");
        var names = new Dictionary<uint, string> { [c.Ball] = "Ball", [c.Bullet] = "Bullet", [c.CurveMgr] = "CurveMgr", [c.Gun] = "Gun", [c.Board] = "Board" };
        if (mem.Read(mem._board, 0x400) is { } bd)
            for (var off = 4; off < 0x400; off += 4)
            {
                var p = BitConverter.ToUInt32(bd, off);
                if (p > 0x10000 && mem.ReadU32(p) is { } vt && names.TryGetValue(vt, out var nm)) Console.WriteLine($"  Board+{off:X3} → {nm} {p:X8}");
            }
        if (args.Contains("gun"))
        {
            // --zuma-mem rtti gun — поля лягушки (Board → Gun) каждые 5 мс 3 с: что меняется при выстреле
            uint gunObj = 0;
            if (mem.Read(mem._board, 0x400) is { } bb)
                for (var off = 4; off < 0x400 && gunObj == 0; off += 4)
                    if (BitConverter.ToUInt32(bb, off) is var p && p > 0x10000 && mem.ReadU32(p) == c.Gun) gunObj = p;
            Console.WriteLine($"Gun {gunObj:X8}");
            var t0 = Stopwatch.StartNew();
            string last = "";
            while (t0.ElapsedMilliseconds < 3000 && gunObj != 0)
            {
                if (mem.Read(gunObj, 0x48) is { } g)
                {
                    var cur = BitConverter.ToUInt32(g, 0x2C);
                    var nxt = BitConverter.ToUInt32(g, 0x30);
                    var row = $"20:{BitConverter.ToInt32(g, 0x20)} 2C:{cur:X8}({mem.ReadU32(cur + 8)}) 30:{nxt:X8}({mem.ReadU32(nxt + 8)}) 34:{BitConverter.ToInt32(g, 0x34)} 38:{BitConverter.ToSingle(g, 0x38):F2} 3C:{BitConverter.ToInt32(g, 0x3C)} 40:{BitConverter.ToInt32(g, 0x40)}";
                    if (row != last) Console.WriteLine($"{t0.ElapsedMilliseconds,5} мс  {row}");
                    last = row;
                }
                Thread.Sleep(5);
            }
            return 0;
        }
        if (Array.IndexOf(args, "watch") is var wi && wi >= 0 && wi + 1 < args.Length)
        {
            // --zuma-mem rtti watch сек — ловить необычные шары (поля, отличные от обычных), снимок кадра в момент поимки
            var until = DateTime.UtcNow.AddSeconds(double.Parse(args[wi + 1], System.Globalization.CultureInfo.InvariantCulture));
            var win = Uia.ZumaWindow();
            var seen = new HashSet<(uint, uint)>();
            var shots = 0;
            while (DateTime.UtcNow < until)
            {
                foreach (var b in mem.ReadChain() ?? [])
                {
                    if (mem.Read(b.Addr, BallSize) is not { } r) continue;
                    // только бонусы: +60 у обычного шара 4
                    var power = BitConverter.ToUInt32(r, 0x60);
                    if (power == 4 || !seen.Add((b.Addr, power))) continue;
                    var odd = new List<string>();
                    foreach (var o in new[] { 0x30, 0x34, 0x38, 0x3C, 0x44, 0x48, 0x50, 0x54, 0x58, 0x5C, 0x60, 0x64 })
                        odd.Add($"{o:X2}:{BitConverter.ToUInt32(r, o):X}");
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} шар {b.Addr:X8} место {b.Way:F0} цвет {b.Type} ({b.X:F0},{b.Y:F0}) 2C:{BitConverter.ToUInt32(r, 0x2C):X8} 40:{BitConverter.ToUInt32(r, 0x40):X} 4C:{BitConverter.ToUInt32(r, 0x4C)}  {string.Join(" ", odd)}");
                    if (shots < 12 && win != IntPtr.Zero && ZumaMenu.GrabGame(win, out _) is { } bmp)
                        using (bmp)
                        {
                            using (var g = Graphics.FromImage(bmp))
                            {
                                float kx = bmp.Width / 640f, ky = bmp.Height / 480f;
                                g.DrawEllipse(new Pen(Color.Magenta, 4), (b.X - 20) * kx, (b.Y - 20) * ky, 40 * kx, 40 * ky);
                            }
                            bmp.Save(Path.Combine(Path.GetTempPath(), $"zuma_power_{shots++}_{power}.png"));
                        }
                }
                Thread.Sleep(100);
            }
            return 0;
        }
        sw.Restart();
        var chain = mem.ReadChain();
        Console.WriteLine($"шаров {chain?.Count} за {sw.Elapsed.TotalMilliseconds:F2} мс");
        foreach (var b in chain ?? [])
        {
            var raw = mem.Read(b.Addr, BallSize);
            var extra = raw is null ? "" : string.Join(" ", new[] { 0x18, 0x2C, 0x30, 0x40, 0x44, 0x48, 0x4C, 0x50, 0x54, 0x58, 0x5C, 0x60, 0x64 }
                .Select(o => $"{o:X2}:{BitConverter.ToUInt32(raw, o):X}"));
            Console.WriteLine($"  кривая {b.Curve} место {b.Way,7:F1} цвет {b.Type} ({b.X,5:F0},{b.Y,5:F0})  {extra}");
        }
        return 0;
    }
}
