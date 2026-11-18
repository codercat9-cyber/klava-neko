using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace KlavaDesk;

/// <summary>
/// Память локальной Zuma Deluxe (32-битный zuma.exe PopCap): чтение и запись. Друг разрешил для этой игры всё —
/// читы, аим, инъекции (одиночная, без мультиплеера, задача — «чтобы Клава играла имбово»). Здесь — слепки
/// записываемой памяти процесса и поиск полей по поведению игры (смена шаров правой кнопкой меняет местами
/// «текущий» и «следующий» в объекте лягушки).
/// </summary>
internal sealed partial class ZumaMemory : IDisposable
{
    private const uint PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020, PROCESS_VM_OPERATION = 0x0008, PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint MEM_COMMIT = 0x1000, PAGE_GUARD = 0x100, PAGE_NOACCESS = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);

    private readonly IntPtr _h;
    public int Pid { get; }

    private ZumaMemory(IntPtr h, int pid) { _h = h; Pid = pid; }

    public static ZumaMemory? Open(IntPtr win)
    {
        Native.GetWindowThreadProcessId(win, out var pid);
        var h = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION, false, (int)pid);
        return h == IntPtr.Zero ? null : new ZumaMemory(h, (int)pid);
    }

    public void Dispose() => CloseHandle(_h);

    internal sealed record Region(uint Base, byte[] Data);

    /// <summary>Слепок записываемой памяти (куча, данные exe): ~90 МБ у Зумы, читается за доли секунды.</summary>
    public List<Region> Snapshot()
    {
        var res = new List<Region>();
        long addr = 0x10000;
        while (addr < 0x7FFF0000)
        {
            if (VirtualQueryEx(_h, (IntPtr)addr, out var mbi, (IntPtr)Marshal.SizeOf<MEMORY_BASIC_INFORMATION>()) == IntPtr.Zero) break;
            long size = mbi.RegionSize.ToInt64();
            if (size <= 0) break;
            var writable = (mbi.Protect & 0xCC) != 0;   // READWRITE 0x04, WRITECOPY 0x08, EXECUTE_READWRITE 0x40, EXECUTE_WRITECOPY 0x80
            if (mbi.State == MEM_COMMIT && writable && (mbi.Protect & PAGE_GUARD) == 0 && (mbi.Protect & PAGE_NOACCESS) == 0 && size <= 256L << 20)
            {
                var buf = new byte[size];
                if (ReadProcessMemory(_h, mbi.BaseAddress, buf, (IntPtr)size, out var read) && read.ToInt64() == size)
                    res.Add(new Region((uint)mbi.BaseAddress.ToInt64(), buf));
            }
            addr = mbi.BaseAddress.ToInt64() + size;
        }
        return res;
    }

    public uint? ReadU32(uint addr)
    {
        var b = new byte[4];
        return ReadProcessMemory(_h, (IntPtr)addr, b, 4, out var n) && n == 4 ? BitConverter.ToUInt32(b) : null;
    }

    public byte[]? Read(uint addr, int len)
    {
        var b = new byte[len];
        return ReadProcessMemory(_h, (IntPtr)addr, b, len, out var n) && n == len ? b : null;
    }

    public bool WriteU32(uint addr, uint value) =>
        WriteProcessMemory(_h, (IntPtr)addr, BitConverter.GetBytes(value), 4, out var n) && n == 4;

    public static uint? At(List<Region> snap, uint addr)
    {
        foreach (var r in snap)
            if (addr >= r.Base && addr + 4 <= r.Base + (uint)r.Data.Length)
                return BitConverter.ToUInt32(r.Data, (int)(addr - r.Base));
        return null;
    }

    /// <summary>
    /// Пары ячеек, обменявшихся значениями между слепками A и B и вернувшихся в C (A→swap→B→swap→C):
    /// поля «текущий/следующий» лягушки. Соседство — до 64 байт, значения разные.
    /// </summary>
    public static List<(uint X, uint Y, uint VX, uint VY)> SwapPairs(List<Region> a, List<Region> b, List<Region> c)
    {
        var res = new List<(uint, uint, uint, uint)>();
        foreach (var ra in a)
        {
            var rb = b.FirstOrDefault(r => r.Base == ra.Base && r.Data.Length == ra.Data.Length);
            var rc = c.FirstOrDefault(r => r.Base == ra.Base && r.Data.Length == ra.Data.Length);
            if (rb is null || rc is null) continue;
            var len = ra.Data.Length - 3;
            for (var i = 0; i < len; i += 4)
            {
                var ai = BitConverter.ToUInt32(ra.Data, i);
                var bi = BitConverter.ToUInt32(rb.Data, i);
                if (ai == bi || BitConverter.ToUInt32(rc.Data, i) != ai) continue;
                for (var d = 4; d <= 64; d += 4)
                {
                    foreach (var j in new[] { i + d, i - d })
                    {
                        if (j < 0 || j >= len) continue;
                        var aj = BitConverter.ToUInt32(ra.Data, j);
                        if (aj == ai || BitConverter.ToUInt32(rb.Data, j) != ai || bi != aj || BitConverter.ToUInt32(rc.Data, j) != aj) continue;
                        if (j > i) res.Add((ra.Base + (uint)i, ra.Base + (uint)j, ai, aj));
                    }
                }
            }
        }
        return res;
    }

    // ---------------- лягушка: текущий и следующий шар ----------------

    /// <summary>
    /// Где у лягушки лежат указатели на текущий и следующий шар (разведка 27.09: объект лягушки, +64 и +6C, смена
    /// шаров меняет их местами). Шар — маленький объект 0x18 байт: +00..+08 нули, +0C тип 0..5 (порядок картинок
    /// в resources.xml: BLUE, YELLOW, RED, GREEN, PURPLE, WHITE), +10 кадр вращения.
    /// </summary>
    /// <param name="Off">−1 — тип шара лежит прямо в ячейке; иначе ячейка — указатель на шар, тип — по этому смещению.</param>
    internal sealed record Gun(uint CurSlot, uint NextSlot, int Off);

    /// <summary>Тип шара PopCap → цвет автопилота.</summary>
    public static ZumaBot.Tint TypeTint(int t) => t switch
    {
        0 => ZumaBot.Tint.Blue, 1 => ZumaBot.Tint.Yellow, 2 => ZumaBot.Tint.Red,
        3 => ZumaBot.Tint.Green, 4 => ZumaBot.Tint.Purple, 5 => ZumaBot.Tint.White, _ => ZumaBot.Tint.None,
    };

    /// <summary>
    /// Найти, где лягушка держит текущий и следующий шар. Правда — глазами: в пасти тип <paramref name="curType"/>, на
    /// спине <paramref name="nextType"/> (разные цвета). Три слепка памяти, между ними две смены шаров правой кнопкой
    /// (итог нулевой); из пар ячеек, обменявшихся значениями и вернувшихся, годится только та, где в момент первого
    /// слепка у «текущей» ровно тип из пасти, у «следующей» — со спины: сами числа или по одному смещению в объектах,
    /// на которые ячейки указывают. Первая попытка 27.09 без сверки с глазами взяла двойной буфер, который
    /// переключается сам (там всегда «жёлтый/зелёный»), — поэтому только так.
    /// </summary>
    /// <param name="swap">Смена шаров (правая кнопка) — настоящей мышью: сообщения окну полноэкранная игра не принимает.</param>
    public async Task<Gun?> FindGunAsync(Func<Task> swap, int curType, int nextType, CancellationToken ct)
    {
        // объект лягушки живёт всю жизнь процесса — найденное помним по процессу
        lock (KnownGuns)
            if (KnownGuns.TryGetValue(Pid, out var known) && ReadGun(known) is not null) return known;
        if (curType == nextType) return null;
        var a = Snapshot();
        await swap();
        await Task.Delay(250, ct);
        var b = Snapshot();
        await swap();
        await Task.Delay(250, ct);
        var c = Snapshot();
        foreach (var p in SwapPairs(a, b, c).OrderBy(p => p.Y - p.X))
        {
            Gun? g = null;
            if (p.VX == curType && p.VY == nextType) g = new Gun(p.X, p.Y, -1);
            else if (p.VX == nextType && p.VY == curType) g = new Gun(p.Y, p.X, -1);
            else
                for (var off = 0; off <= 0x60 && g is null; off += 4)
                {
                    if (At(a, p.VX + (uint)off) is not { } tx || At(a, p.VY + (uint)off) is not { } ty) continue;
                    if (tx == curType && ty == nextType) g = new Gun(p.X, p.Y, off);
                    else if (tx == nextType && ty == curType) g = new Gun(p.Y, p.X, off);
                }
            if (g is null) continue;
            lock (KnownGuns) KnownGuns[Pid] = g;
            return g;
        }
        return null;
    }

    private int? TypeAt(uint slot, int off)
    {
        if (ReadU32(slot) is not { } v) return null;
        if (off < 0) return v <= 5 ? (int)v : null;
        return v > 0x10000 && ReadU32(v + (uint)off) is { } t && t <= 5 ? (int)t : null;
    }

    private static readonly Dictionary<int, Gun> KnownGuns = new();

    /// <summary>Лягушка, уже найденная в этом процессе игры (и ещё читается); null — искать.</summary>
    public Gun? Known()
    {
        lock (KnownGuns)
            return KnownGuns.TryGetValue(Pid, out var g) && ReadGun(g) is not null ? g : null;
    }

    /// <summary>Запомнить уточнённые ячейки (текущий/следующий поменяны местами по сверке с пастью).</summary>
    public void Remember(Gun g)
    {
        lock (KnownGuns) KnownGuns[Pid] = g;
    }

    /// <summary>Типы текущего и следующего шара; null — лягушки по этим адресам больше нет (новый уровень).</summary>
    public (int Cur, int Next)? ReadGun(Gun g) =>
        TypeAt(g.CurSlot, g.Off) is { } c && TypeAt(g.NextSlot, g.Off) is { } n ? (c, n) : null;

    /// <summary>
    /// Что лежит в ячейках лягушки как есть: у шаров-объектов — указатели. Выстрел меняет их всегда (в пасть встаёт
    /// другой объект), а цвета могут и совпасть — по цветам «выстрел ушёл?» ошибался при повторе цвета.
    /// </summary>
    public (uint Cur, uint Next)? ReadGunRaw(Gun g) =>
        ReadU32(g.CurSlot) is { } c && ReadU32(g.NextSlot) is { } n ? (c, n) : null;

    /// <summary>Цвет автопилота → тип шара PopCap (−1 — не шар).</summary>
    public static int TintType(ZumaBot.Tint t) => t switch
    {
        ZumaBot.Tint.Blue => 0, ZumaBot.Tint.Yellow => 1, ZumaBot.Tint.Red => 2,
        ZumaBot.Tint.Green => 3, ZumaBot.Tint.Purple => 4, ZumaBot.Tint.White => 5, _ => -1,
    };

    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    /// <summary>Правый клик сообщением окну игры (смена шаров у лягушки).</summary>
    public static async Task RightClickAsync(IntPtr win, Point p)
    {
        var lp = ZumaMenu.MouseLParam(p);
        PostMessage(win, 0x0200, IntPtr.Zero, lp);
        await Task.Delay(60);
        PostMessage(win, 0x0204, (IntPtr)2, lp);
        await Task.Delay(60);
        PostMessage(win, 0x0205, IntPtr.Zero, lp);
    }

    /// <summary>Все выровненные адреса слепка, где лежит значение <paramref name="value"/> (поиск объектов по vtable).</summary>
    public static List<uint> FindValue(List<Region> snap, uint value)
    {
        var res = new List<uint>();
        foreach (var r in snap)
            for (var i = 0; i + 4 <= r.Data.Length; i += 4)
                if (BitConverter.ToUInt32(r.Data, i) == value) res.Add(r.Base + (uint)i);
        return res;
    }

    /// <summary>Строка разбора куска объекта: смещение, hex, как float (если похоже на число).</summary>
    private static string DumpRow(byte[] b, int off)
    {
        var u = BitConverter.ToUInt32(b, off);
        var f = BitConverter.ToSingle(b, off);
        var fs = float.IsFinite(f) && MathF.Abs(f) > 1e-3f && MathF.Abs(f) < 1e6f ? $"{f,10:F2}" : "";
        return $"+{off:X2} {u:X8} {(int)u,11} {fs}";
    }

    /// <summary>
    /// KlavaDesk.exe --zuma-mem balls — разведка шаров: указатели «текущий/следующий» у лягушки (меняются при смене
    /// шаров) → vtable шара → все объекты с этой vtable в памяти → их поля два раза с паузой (что движется).
    /// </summary>
    private static int BallsTest(ZumaMemory mem, IntPtr win)
    {
        var center = new Point(320, 300);
        var a = mem.Snapshot();
        RightClickAsync(win, center).GetAwaiter().GetResult();
        Thread.Sleep(250);
        var b = mem.Snapshot();
        RightClickAsync(win, center).GetAwaiter().GetResult();
        Thread.Sleep(250);
        var c = mem.Snapshot();
        var pairs = SwapPairs(a, b, c);
        uint vt = 0;
        foreach (var p in pairs)
        {
            var va = At(c, p.VX);
            var vb = At(c, p.VY);
            Console.WriteLine($"пара {p.X:X8} ↔ {p.Y:X8}: {p.VX:X8} / {p.VY:X8}, первые слова объектов {va:X8} / {vb:X8}");
            if (va is { } x && x == vb && x > 0x400000 && x < 0x800000) { vt = x; Console.WriteLine($"  → vtable шара {vt:X8}, лягушка держит шары в {p.X:X8} и {p.Y:X8}"); }
        }
        if (vt == 0) { Console.WriteLine("vtable шара не нашла"); return 1; }
        var objs = FindValue(c, vt);
        Console.WriteLine($"объектов с vtable {vt:X8}: {objs.Count}");
        var first = objs.Take(40).Select(o => (o, mem.Read(o, 0x80))).ToList();
        Thread.Sleep(500);
        var second = objs.Take(40).Select(o => (o, mem.Read(o, 0x80))).ToList();
        for (var i = 0; i < Math.Min(8, first.Count); i++)
        {
            var (o, d1) = first[i];
            var d2 = second[i].Item2;
            if (d1 is null || d2 is null) continue;
            Console.WriteLine($"— шар {o:X8}");
            for (var off = 0; off < 0x80; off += 4)
            {
                var changed = BitConverter.ToUInt32(d1, off) != BitConverter.ToUInt32(d2, off);
                Console.WriteLine($"   {DumpRow(d1, off)}{(changed ? "   →  " + DumpRow(d2, off)[12..] : "")}");
            }
        }
        // одно и то же поле у всех шаров — какие значения (цвет — 0..5 у всех, координаты — разные)
        Console.WriteLine("поля по всем шарам (мин..макс, различных значений):");
        for (var off = 4; off < 0x80; off += 4)
        {
            var vals = second.Where(t => t.Item2 is not null).Select(t => BitConverter.ToUInt32(t.Item2!, off)).ToList();
            if (vals.Count == 0) continue;
            Console.WriteLine($"   +{off:X2}: {vals.Min():X8}..{vals.Max():X8}, различных {vals.Distinct().Count()}: {string.Join(" ", vals.Take(16).Select(v => v < 100000 ? v.ToString() : v.ToString("X8")))}");
        }
        return 0;
    }

    /// <summary>KlavaDesk.exe --zuma-mem swap|balls — разведка: какие ячейки меняются местами при смене шаров; шары.</summary>
    public static int Test(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var win = Uia.ZumaWindow();
        if (win == IntPtr.Zero) { Console.WriteLine("окна Zuma Deluxe нет"); return 1; }
        using var mem = Open(win);
        if (mem is null) { Console.WriteLine("процесс игры не открылся на чтение/запись"); return 1; }
        if (args.Contains("balls")) { Uia.Focus(win); Thread.Sleep(400); return BallsTest(mem, win); }
        if (args.Contains("rtti")) return RttiTest(mem, args);
        if (args.Length >= 2 && args[0] == "who")
        {
            // --zuma-mem who адрес [размах] — кто хранит указатель на [адрес−размах, адрес] (цепочка от статики exe)
            var target = Convert.ToUInt32(args[1], 16);
            var span = args.Length > 2 ? Convert.ToUInt32(args[2], 16) : 0x100u;
            var snap = mem.Snapshot();
            foreach (var r in snap)
                for (var i = 0; i + 4 <= r.Data.Length; i += 4)
                {
                    var v = BitConverter.ToUInt32(r.Data, i);
                    if (v <= target && target - v <= span)
                        Console.WriteLine($"  {r.Base + (uint)i:X8} → {v:X8} (+{target - v:X}){(r.Base < 0x01000000 ? "   ← образ exe/статика" : "")}");
                }
            return 0;
        }
        if (args.Length >= 2 && args[0] == "find")
        {
            // --zuma-mem find значение[,значение…] [сколько показать] — где в памяти лежат эти числа (объекты по vtable)
            var snap = mem.Snapshot();
            var show = args.Length > 2 ? int.Parse(args[2]) : 12;
            foreach (var v in args[1].Split(',').Select(s => Convert.ToUInt32(s, 16)))
            {
                var hits = FindValue(snap, v);
                Console.WriteLine($"{v:X8}: {hits.Count} — {string.Join(" ", hits.Take(show).Select(h => h.ToString("X8")))}");
            }
            return 0;
        }
        if (args.Length >= 2 && args[0] == "objs")
        {
            // --zuma-mem objs vtable [длина] — все объекты класса одной строкой: поля int/float, два раза через 0,3 с
            var vt = Convert.ToUInt32(args[1], 16);
            var len = args.Length > 2 ? Convert.ToInt32(args[2], 16) : 0x70;
            var objs = FindValue(mem.Snapshot(), vt);
            var d1 = objs.Select(o => mem.Read(o, len)).ToList();
            Thread.Sleep(300);
            var d2 = objs.Select(o => mem.Read(o, len)).ToList();
            string Cell(byte[] d, int off)
            {
                var u = BitConverter.ToUInt32(d, off);
                var f = BitConverter.ToSingle(d, off);
                return u < 0x10000 ? u.ToString() : float.IsFinite(f) && MathF.Abs(f) > 1e-3f && MathF.Abs(f) < 1e6f ? f.ToString("F1", CultureInfo.InvariantCulture) : u.ToString("X8");
            }
            Console.WriteLine($"{objs.Count} объектов {vt:X8}");
            for (var i = 0; i < objs.Count; i++)
            {
                if (d1[i] is not { } a || d2[i] is not { } b) continue;
                var row = new StringBuilder($"{objs[i]:X8}");
                for (var off = 4; off + 4 <= len; off += 4)
                {
                    var ch = BitConverter.ToUInt32(a, off) != BitConverter.ToUInt32(b, off);
                    row.Append(ch ? $" {off:X2}:{Cell(a, off)}>{Cell(b, off)}" : $" {off:X2}:{Cell(a, off)}");
                }
                Console.WriteLine(row);
            }
            return 0;
        }
        if (args.Contains("chain"))
        {
            // места шаров на трассе: float 0..4000, у всех за 0,3 с выросли на одно и то же (цепочка ползёт)
            var a = mem.Snapshot();
            var t0 = DateTime.UtcNow;
            Thread.Sleep(300);
            var b = mem.Snapshot();
            var dt = (DateTime.UtcNow - t0).TotalSeconds;
            var cand = new List<(uint Addr, float A, float B)>();
            foreach (var ra in a)
            {
                var rb = b.FirstOrDefault(r => r.Base == ra.Base && r.Data.Length == ra.Data.Length);
                if (rb is null) continue;
                for (var i = 0; i + 4 <= ra.Data.Length; i += 4)
                {
                    float fa = BitConverter.ToSingle(ra.Data, i), fb = BitConverter.ToSingle(rb.Data, i);
                    if (!(fa > 1 && fa < 4000 && fb - fa > 0.5f && fb - fa < 250)) continue;
                    cand.Add((ra.Base + (uint)i, fa, fb));
                }
            }
            Console.WriteLine($"кандидатов {cand.Count} за {dt:F2} с");
            foreach (var g in cand.GroupBy(c => MathF.Round((c.B - c.A) * 2) / 2).OrderByDescending(g => g.Count()).Take(6))
            {
                Console.WriteLine($"сдвиг {g.Key:F1} ({g.Key / dt:F0}/с): {g.Count()} ячеек");
                var list = g.OrderBy(c => c.Addr).ToList();
                for (var i = 0; i < Math.Min(24, list.Count); i++)
                    Console.WriteLine($"   {list[i].Addr:X8} {list[i].A,9:F2} → {list[i].B,9:F2}{(i > 0 ? $"   шаг адреса {list[i].Addr - list[i - 1].Addr:X}" : "")}");
            }
            return 0;
        }
        if (args.Length >= 2 && args[0] == "dump")
        {
            // --zuma-mem dump адрес[,адрес…] [длина] [раз] — поля объектов (hex/int/float); повтор через 0,4 с — что меняется
            var addrs = args[1].Split(',').Select(s => Convert.ToUInt32(s, 16)).ToList();
            var len = args.Length > 2 ? Convert.ToInt32(args[2], 16) : 0x80;
            var times = args.Length > 3 ? int.Parse(args[3]) : 2;
            var shots = new List<List<byte[]?>>();
            for (var t = 0; t < times; t++) { shots.Add(addrs.Select(a => mem.Read(a, len)).ToList()); Thread.Sleep(400); }
            for (var i = 0; i < addrs.Count; i++)
            {
                Console.WriteLine($"— {addrs[i]:X8}");
                if (shots[0][i] is not { } d0) { Console.WriteLine("   не читается"); continue; }
                for (var off = 0; off + 4 <= len; off += 4)
                {
                    var row = DumpRow(d0, off);
                    var later = shots.Skip(1).Select(s => s[i]).Where(d => d is not null && BitConverter.ToUInt32(d, off) != BitConverter.ToUInt32(d0, off))
                        .Select(d => DumpRow(d!, off)[4..]).ToList();
                    Console.WriteLine($"   {row}{(later.Count > 0 ? "   →  " + string.Join("  →  ", later) : "")}");
                }
            }
            return 0;
        }
        var gameDir = ZumaMenu.GameDir(win) ?? @"D:\Games\Zuma Deluxe";
        Task<bool> Ready() => Task.FromResult(ZumaBot.OnLocalLevel(win, gameDir));
        Uia.Focus(win);
        Thread.Sleep(500);
        var nav = ZumaMenu.ToLevelAsync(win, Ready, () => null, Console.WriteLine, CancellationToken.None, 60).GetAwaiter().GetResult();
        if (nav is not null) { Console.WriteLine($"до уровня не дошла: {nav}"); return 1; }
        Uia.Focus(win);
        Thread.Sleep(700);
        var center = new Point(320, 300);
        var all = new List<(uint X, uint Y, uint VX, uint VY)>();
        for (var round = 0; round < 2; round++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var a = mem.Snapshot();
            RightClickAsync(win, center).GetAwaiter().GetResult();
            Thread.Sleep(250);
            var b = mem.Snapshot();
            RightClickAsync(win, center).GetAwaiter().GetResult();
            Thread.Sleep(250);
            var c = mem.Snapshot();
            var pairs = SwapPairs(a, b, c);
            Console.WriteLine($"раунд {round + 1}: слепок {a.Sum(r => r.Data.Length) / 1048576.0:F0} МБ, {sw.ElapsedMilliseconds} мс, пар {pairs.Count}");
            all.AddRange(pairs);
        }
        foreach (var g in all.GroupBy(p => (p.X, p.Y)).OrderByDescending(g => g.Count()).Take(40))
        {
            var p = g.First();
            Console.WriteLine($"  {p.X:X8} ↔ {p.Y:X8} (+{p.Y - p.X}): {p.VX:X8} / {p.VY:X8}  ×{g.Count()}  сейчас {mem.ReadU32(p.X):X8} / {mem.ReadU32(p.Y):X8}");
        }
        return 0;
    }
}
