using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace KlavaDesk;

/// <summary>
/// Локальная Zuma Deluxe (PopCap; у друга — через cnc-ddraw в окне 1920×1032 с полями 4:3). Прицел тот же (Plan, Best,
/// FirstHit), а глаза и руки свои:
///
/// Глаза — по трассе уровня из файлов игры (<see cref="ZumaTrack"/>): шары ищутся только на пути, фон уровня вычитается
/// (рыжий камень и белые шары не путаются), соседи в цепочке — по пути, а не по расстоянию на экране: на спирали
/// и треугольнике соседний виток ближе двух радиусов, и группы склеивались через виток — выстрелы уходили «в тройку»,
/// которой нет. Опасность — сколько пути осталось до черепа. Лягушка — gx, gy из levels.xml; цвет в пасти и на спине —
/// по направлению, которое задала сама Клава (PopCap-лягушка поворачивается за курсором сразу, пунктира у неё нет).
///
/// Упреждение: цепочка ползёт 50–150 px/с (speed в levels.xml — px за тик 1/100 с), шар летит ~600 px/с (firespeed 6):
/// пока шар пересекает поле, цепочка съезжает на шар и больше — без поправки выстрелы ложились мимо. Скорость каждого
/// куска цепочки меряется по кадрам 0,1–0,45 с назад, прицел — туда, где шар будет, когда долетит выстрел.
///
/// Руки — сообщения окну игры (WM_MOUSEMOVE / WM_LBUTTONDOWN, правая — смена шаров): мышь друга не дёргается, окна
/// Клавы поверх игры не мешают, выстрел уходит сразу, без перетаскивания курсора. После выстрела курсор игры уводится
/// на край поля по тому же лучу — стрелка не закрывает шары, а лягушка смотрит туда же.
/// </summary>
internal static partial class ZumaBot
{
    /// <summary>
    /// Режим демона (по умолчанию; друг 28.09: «чтобы ебашила как демон», читы в этой одиночной игре разрешены): перед
    /// выстрелом шар в пасти перекрашивается в цвет лучшего выстрела на поле (ZumaMemory.SetFrogBall). false — честно,
    /// только прицел и чтение памяти.
    /// </summary>
    private static volatile bool _demon = true;

    /// <summary>Скорость выстрела PopCap, px/с в координатах 640×480 (firespeed 6 за тик 10 мс).</summary>
    private const float ShotSpeed = 600f;
    /// <summary>От снимка до вылета шара: снимок, разбор, сообщения окну.</summary>
    private const float LatencySec = 0.06f;
    /// <summary>
    /// С памятью: чтение ~2 мс, 15 мс курсору до щелчка, тик игры и плевок (State 1 ~60 мс — шар выкатывается к краю пасти
    /// и только потом летит). Сдвиг проверяется в журнале: «прицел по памяти — промах в среднем …».
    /// </summary>
    private const float MemLatencySec = 0.085f;

    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    /// <summary>
    /// Развёрнутое окно PopCap без cnc-ddraw рисует игру 640×480 в левом верхнем углу, а рядом остаётся призрак
    /// старого кадра (скрин друга 27.09) — ни меню, ни поля там не разобрать. Вернуть окну обычный размер.
    /// </summary>
    private static async Task UnzoomAsync(IntPtr win)
    {
        if (Native.IsIconic(win) || IsZoomed(win))
        {
            Native.ShowWindow(win, Native.SW_RESTORE);
            await Task.Delay(700);
            Log.Write("зума: окно игры было свёрнуто/развёрнуто — вернула обычный размер");
        }
    }

    // ---------- руки и глаза в окне игры ----------

    /// <summary>Окно игры: снимок картинки (без полей), мышь — сообщениями окну.</summary>
    private sealed class LocalIo(IntPtr win)
    {
        public IntPtr Win => win;
        /// <summary>Картинка игры в клиентских координатах окна.</summary>
        public Rectangle Game { get; private set; }
        /// <summary>Последний кадр картинкой (зелёные кнопки итогов, узнавание уровня, разметка).</summary>
        public Bitmap? Last { get; private set; }

        public Frame? Grab()
        {
            var bmp = ZumaMenu.GrabGame(win, out var g);
            if (bmp is null) return null;
            Game = g;
            Last?.Dispose();
            Last = bmp;
            return new Frame(bmp);
        }

        private IntPtr LParam(PointF p) => ZumaMenu.MouseLParam(new Point(Game.X + (int)MathF.Round(p.X), Game.Y + (int)MathF.Round(p.Y)));

        [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point p);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr h, ref Point p);

        private Point? _placed;   // куда Клава поставила курсор (экран)

        private Point ScreenOf(PointF p)
        {
            var o = Point.Empty;
            ClientToScreen(win, ref o);
            return new Point(o.X + Game.X + (int)MathF.Round(p.X), o.Y + Game.Y + (int)MathF.Round(p.Y));
        }

        /// <summary>
        /// Курсор в точку кадра. Настоящий курсор — мгновенно (SetCursorPos, без перетаскивания): лягушка PopCap смотрит
        /// туда, где стоит НАСТОЯЩАЯ мышь (проверено 27.09 — на одни сообщения она не поворачивалась и глядела на
        /// курсор друга), плюс сообщение окну — если сверху окно Клавы, игра всё равно узнает, куда смотреть.
        /// </summary>
        public void Move(PointF p)
        {
            var s = ScreenOf(p);
            ZumaMenu.PlaceCursor(s);
            _placed = s;
            PostMessage(win, 0x0200, IntPtr.Zero, LParam(p));
        }

        /// <summary>Друг двигает мышь сам: курсор не там, где его оставила Клава.</summary>
        public bool UserMoved()
        {
            if (_placed is not { } pl || !GetCursorPos(out var cur)) return false;
            return Math.Abs(cur.X - pl.X) + Math.Abs(cur.Y - pl.Y) > 40;
        }

        /// <summary>Ждать, пока друг отпустит мышь (1,5 с на месте): true — отпустил, false — водит дольше max.</summary>
        public async Task<bool> WaitUserReleasedAsync(TimeSpan max, CancellationToken ct)
        {
            var clock = Stopwatch.StartNew();
            var still = Stopwatch.StartNew();
            GetCursorPos(out var last);
            while (clock.Elapsed < max)
            {
                await Task.Delay(250, ct);
                GetCursorPos(out var p);
                if (Math.Abs(p.X - last.X) + Math.Abs(p.Y - last.Y) > 3) { still.Restart(); last = p; }
                else if (still.Elapsed > TimeSpan.FromSeconds(1.5)) return true;
                if (Interrupted() is not null) return true;   // Esc/другое окно — разберётся основной цикл
            }
            return false;
        }

        /// <summary>Память игры (цвета лягушки) и где в ней лягушка держит шары; null — не открылась / не нашлась.</summary>
        public ZumaMemory? Mem { get; set; }
        public ZumaMemory.Gun? GunMem { get; set; }
        /// <summary>Сколько раз за партию искали лягушку в памяти (поиск — 3 с стоянки, больше двух не тратим).</summary>
        public int MemTries { get; set; }

        /// <summary>
        /// Выстрел: курсор в точку и щелчок левой настоящей мышью (SendInput). Щелчки сообщениями окну игра
        /// в полноэкранном режиме cnc-ddraw не принимает (27.09: 90 с жала PLAY — экран не сменился), настоящий
        /// щелчок доходит всегда. Точка никогда не под окнами Клавы — см. <see cref="UnderKlava"/>.
        /// </summary>
        public async Task ShootAsync(PointF p)
        {
            Move(p);
            await Task.Delay(15);   // тик игры 10 мс — пусть лягушка повернётся на курсор до щелчка
            Click(Native.MOUSEEVENTF_LEFTDOWN);
            await Task.Delay(18);
            Click(Native.MOUSEEVENTF_LEFTUP);
        }

        /// <summary>Смена текущего и следующего шара — правая кнопка там, где стоит курсор (не над Клавой: у неё на
        /// правой кнопке меню).</summary>
        public async Task SwapAsync(PointF p)
        {
            Move(p);
            await Task.Delay(15);
            Click(Native.MOUSEEVENTF_RIGHTDOWN);
            await Task.Delay(18);
            Click(Native.MOUSEEVENTF_RIGHTUP);
        }

        private static void Click(uint flags)
        {
            var input = new[] { new Native.INPUT { type = Native.INPUT_MOUSE, u = new Native.INPUTUNION { mi = new Native.MOUSEINPUT { dwFlags = flags } } } };
            Native.SendInput(1, input, Marshal.SizeOf<Native.INPUT>());
        }

        // окна Клавы (аватар, пузыри, панели) на экране — и этого процесса, и живой Клавы, если проверка идёт отдельно
        private List<Rectangle> _klava = [];
        private readonly Stopwatch _klavaAge = new();

        /// <summary>Точка кадра под окном Клавы: настоящий щелчок ушёл бы ей (правая кнопка — её меню).</summary>
        public bool UnderKlava(PointF p)
        {
            if (!_klavaAge.IsRunning || _klavaAge.Elapsed > TimeSpan.FromSeconds(2))
            {
                _klava = KlavaWindows();
                _klavaAge.Restart();
            }
            var s = ScreenOf(p);
            return _klava.Any(r => r.Contains(s));
        }

        private static bool IsKlavaWindow(IntPtr h)
        {
            if (h == IntPtr.Zero) return false;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == (uint)Environment.ProcessId) return true;
            try
            {
                using var p = Process.GetProcessById((int)pid);
                return p.ProcessName.Equals("KlavaDesk", StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
        }

        private static List<Rectangle> KlavaWindows()
        {
            var pids = new HashSet<uint> { (uint)Environment.ProcessId };
            foreach (var pr in Process.GetProcessesByName("KlavaDesk")) { pids.Add((uint)pr.Id); pr.Dispose(); }
            var list = new List<Rectangle>();
            Native.EnumWindows((h, _) =>
            {
                Native.GetWindowThreadProcessId(h, out var pid);
                if (pids.Contains(pid) && Native.IsWindowVisible(h) && Native.GetWindowRect(h, out var r) && r.Right > r.Left && r.Bottom > r.Top)
                    list.Add(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom));
                return true;
            }, IntPtr.Zero);
            return list;
        }

        private readonly Stopwatch _away = new();

        public string? Interrupted()
        {
            if (!IsWindow(win)) return "окно Зумы закрылось";
            var fg = Native.GetForegroundWindow();
            // Esc/Backspace — только нажатые в окне самой игры: состояние клавиш общее на всю систему, и Backspace в
            // окошке ввода Клавы или в чужом чате обрывал партию (друг 30.09: «бот срывается в Зуме»)
            if (fg == win && KeysOnly(win) is { } key) return key;
            // PopCap без фокуса встаёт на паузу — значит, друг забрал игру себе. Окна самой Клавы (пузырь, «думаю…»,
            // её панели) не в счёт: 27.09 её же окно на миг вышло вперёд и оборвало партию. Чужое окно — только если
            // держится впереди дольше 1,5 с: всплывшее и тут же закрытое уведомление партию не рвёт
            if (fg != win && !IsKlavaWindow(fg))
            {
                if (!_away.IsRunning) _away.Restart();
                if (_away.ElapsedMilliseconds > 1500) return "друг переключился на другое окно";
            }
            else _away.Reset();
            return null;
        }
    }

    /// <summary>Esc/Backspace, если зажаты (вызывающий сам проверяет, что впереди окно игры).</summary>
    private static string? KeysOnly(IntPtr win) =>
        (Native.GetAsyncKeyState(0x08) & 0x8000) != 0 ? "друг нажал Backspace"
        : (Native.GetAsyncKeyState(0x1B) & 0x8000) != 0 ? "друг нажал Esc" : null;

    /// <summary>Папка игры по окну (рядом с её exe), иначе — установленная.</summary>
    private static string? LocalGameDir(IntPtr win)
    {
        if (ZumaMenu.GameDir(win) is { } dir) return dir;
        return FindInstalled() is { } exe ? Path.GetDirectoryName(exe) : null;
    }

    /// <summary>На экране уровень с шарами: фон узнаётся, окон с зелёными кнопками нет.</summary>
    public static bool OnLocalLevel(IntPtr win, string gameDir)
    {
        using var b = ZumaMenu.GrabClient(win);
        return b is not null && ZumaMenu.GreenButtons(b).Count == 0 && ZumaTrack.Identify(b, gameDir) is not null;
    }

    // ---------- что на поле ----------

    /// <summary>Разбор по трассе → сцена для прицела (в координатах кадра).</summary>
    private static Scene TrackScene(ZumaTrack lv, List<ZumaTrack.TrackBall> balls, int frameW, int frameH)
    {
        float kx = frameW / (float)ZumaTrack.W, ky = frameH / (float)ZumaTrack.H;
        var hud = (int)(ZumaTrack.Hud * ky);
        var s = new Scene { Board = new Rectangle(0, hud, frameW, frameH - hud), R = ZumaTrack.BallR * ky, RSure = true };
        var sorted = balls.OrderBy(b => b.Curve).ThenBy(b => b.Idx).ToList();
        var n = sorted.Count;
        s.CurveOf = new int[n];
        s.Idx = new float[n];
        s.Danger = new float[n];
        s.PrevB = new int[n];
        s.NextB = new int[n];
        s.Links = new int[n];
        s.Group = new int[n];
        s.Hidden = new bool[n];
        var groups = 0;
        for (var i = 0; i < n; i++)
        {
            var b = sorted[i];
            var cv = lv.Curves[b.Curve];
            var p = cv.At(b.Idx);
            s.Balls.Add(new Ball(p.X * kx, p.Y * ky, b.T, ZumaTrack.BallR * ky));
            s.CurveOf[i] = b.Curve;
            s.Idx[i] = b.Idx;
            // под туннелем или ещё за краем экрана — выстрел туда не долетит
            var at = Math.Clamp((int)b.Idx, 0, cv.Count - 1);
            s.Hidden[i] = cv.Hidden[at] || p.X < 8 || p.Y < 8 || p.X >= ZumaTrack.W - 8 || p.Y >= ZumaTrack.H - 8;
            // последние 700 px пути — чем ближе к пасти, тем опаснее
            s.Danger[i] = 1 - Math.Clamp((cv.Count - b.Idx) / 700f, 0, 1);
            s.PrevB[i] = s.NextB[i] = -1;
            var touch = i > 0 && sorted[i - 1].Curve == b.Curve && b.Idx - sorted[i - 1].Idx <= 2.5f * ZumaTrack.BallR;
            if (touch)
            {
                s.PrevB[i] = i - 1;
                s.NextB[i - 1] = i;
                s.Links[i]++;
                s.Links[i - 1]++;
            }
            s.Group[i] = touch && sorted[i - 1].T == b.T ? s.Group[i - 1] : groups++;
        }
        s.GroupSize = new int[groups];
        foreach (var g in s.Group) s.GroupSize[g]++;
        // бонус в группе — очки выстрелу, который её лопнет. «Назад» и «замедление» у черепа спасают уровень (на SG цепочка
        // летит быстрее, чем лягушка успевает стрелять — ~4 выстрела в секунду), бомба сносит соседей, меткость — мелочь
        s.GroupBonus = new float[groups];
        s.GroupBusy = new bool[groups];
        for (var i = 0; i < n; i++)
        {
            if (sorted[i].Busy) s.GroupBusy[s.Group[i]] = true;
            var danger = s.Danger[i];
            var bonus = sorted[i].Power switch
            {
                ZumaTrack.PowerBackwards => 90 + 220 * danger,
                ZumaTrack.PowerSlow => 70 + 160 * danger,
                ZumaTrack.PowerBomb => 80 + 100 * danger,
                ZumaTrack.PowerAccuracy => 40,
                _ => 0f,
            };
            s.GroupBonus[s.Group[i]] = Math.Max(s.GroupBonus[s.Group[i]], bonus);
        }
        var c0 = lv.Curves[0];
        s.Skull = new PointF(c0.X[^1] * kx, c0.Y[^1] * ky);
        return s;
    }

    /// <summary>
    /// Цвет шара в пасти (MouthD от центра лягушки по направлению взгляда) и камня на спине (BackD назад).
    /// Лягушка серая, у неё разброс каналов мал — цветом её части не считаются.
    /// </summary>
    private static (Tint Cur, Tint Next) FrogColors(Frame f, PointF gun, PointF u, float k)
    {
        // white — доля «белых» пикселей, с которой круг белый: у камня на спине тёмный ободок, и белый камень
        // набирает всего ~четверть круга (бежевое тело лягушки рядом тоже «белое» — цветной камень его перевешивает)
        Tint Vote(float cx, float cy, float rad, float white = 0.3f)
        {
            var cnt = new int[7];
            var total = 0;
            var ri = (int)MathF.Ceiling(rad);
            for (var dy = -ri; dy <= ri; dy++)
                for (var dx = -ri; dx <= ri; dx++)
                {
                    if (dx * dx + dy * dy > rad * rad) continue;
                    int x = (int)cx + dx, y = (int)cy + dy;
                    if (x < 0 || y < 0 || x >= f.W || y >= f.H) continue;
                    var i = y * f.Stride + x * 3;
                    cnt[(int)ZumaTrack.BallTint(f.Px[i + 2], f.Px[i + 1], f.Px[i])]++;
                    total++;
                }
            if (total == 0) return Tint.None;
            var best = Tint.None;
            int bestN = 0, second = 0;
            for (var t = 1; t <= 5; t++)
            {
                if (cnt[t] > bestN) { second = bestN; bestN = cnt[t]; best = (Tint)t; }
                else if (cnt[t] > second) second = cnt[t];
            }
            if (bestN >= 0.25f * total && bestN >= 2 * second) return best;
            return cnt[(int)Tint.White] >= white * total && bestN < 0.1f * total ? Tint.White : Tint.None;
        }
        var cur = Vote(gun.X + u.X * ZumaTrack.MouthD * k, gun.Y + u.Y * ZumaTrack.MouthD * k, 7 * k);
        var next = Vote(gun.X - u.X * ZumaTrack.BackD * k, gun.Y - u.Y * ZumaTrack.BackD * k, 3.5f * k, 0.2f);
        return (cur, next);
    }

    /// <summary>
    /// Лучший общий сдвиг набора шаров по пути между прошлым и нынешним разбором (px 640×480): для каждого сдвига —
    /// сколько шаров нашли себе в прошлом разборе шар того же цвета на своём месте минус сдвиг. null — не совпало.
    /// </summary>
    private static float? ChainShift(List<ZumaTrack.TrackBall> prev, List<ZumaTrack.TrackBall> cur, int maxShift)
    {
        if (cur.Count < 3) return null;
        int bestD = 0, bestN = -1;
        for (var d = -10; d <= maxShift; d++)
        {
            var n = 0;
            foreach (var b in cur)
                foreach (var p in prev)
                    if (p.Curve == b.Curve && p.T == b.T && MathF.Abs(p.Idx + d - b.Idx) <= 5) { n++; break; }
            if (n > bestN || n == bestN && Math.Abs(d) < Math.Abs(bestD)) { bestN = n; bestD = d; }
        }
        return bestN >= Math.Max(3, (int)(0.6f * cur.Count)) ? bestD : null;
    }

    /// <summary>
    /// Скорость (px/с по пути) каждого шара: куски цепочки (касающиеся шары) — каждый своей скоростью (передний кусок
    /// после разрыва стоит, задний толкается), мелкие куски — скоростью всей кривой.
    /// </summary>
    private static float[] Velocities(List<ZumaTrack.TrackBall> sorted, Queue<(double T, List<ZumaTrack.TrackBall> B)> history, double now)
    {
        var v = new float[sorted.Count];
        (double T, List<ZumaTrack.TrackBall> B)? old = null;
        foreach (var h in history)
            if (now - h.T >= 0.1) { old = h; break; }   // самый старый кадр из окна 0,1–0,45 с
        if (old is not { } o) return v;
        var dt = (float)(now - o.T);
        var maxShift = (int)(220 * dt) + 6;             // быстрее 220 px/с цепочка не ходит (кроме вкатывания)
        var curveV = new Dictionary<int, float>();
        foreach (var c in sorted.Select(b => b.Curve).Distinct())
            if (ChainShift(o.B, sorted.Where(b => b.Curve == c).ToList(), maxShift) is { } d) curveV[c] = Math.Max(0, d / dt);
        var i = 0;
        while (i < sorted.Count)
        {
            var j = i + 1;
            while (j < sorted.Count && sorted[j].Curve == sorted[i].Curve && sorted[j].Idx - sorted[j - 1].Idx <= 2.5f * ZumaTrack.BallR) j++;
            var seg = sorted.GetRange(i, j - i);
            float sv = curveV.TryGetValue(sorted[i].Curve, out var cvv) ? cvv : 0;
            if (seg.Count >= 4 && ChainShift(o.B, seg, maxShift) is { } d) sv = d / dt;
            for (var k = i; k < j; k++) v[k] = sv;
            i = j;
        }
        return v;
    }

    /// <summary>Шары туда, где они будут, когда до них долетит выстрел из лягушки (по пути, со скоростью своего куска).</summary>
    /// <param name="latency">От разбора до вылета шара из пасти, с.</param>
    /// <param name="speed">Скорость выстрела, px/с (640×480).</param>
    private static List<ZumaTrack.TrackBall> Lead(ZumaTrack lv, List<ZumaTrack.TrackBall> sorted, float[] vel, float latency = LatencySec, float speed = ShotSpeed)
    {
        var res = new List<ZumaTrack.TrackBall>(sorted.Count);
        for (var i = 0; i < sorted.Count; i++)
        {
            var b = sorted[i];
            var cv = lv.Curves[b.Curve];
            var p = cv.At(b.Idx);
            var dist = MathF.Sqrt((p.X - lv.Gun.X) * (p.X - lv.Gun.X) + (p.Y - lv.Gun.Y) * (p.Y - lv.Gun.Y));
            var t = latency + Math.Max(0, dist - ZumaTrack.MouthD) / speed;
            // скорость бывает и отрицательной: бонус «назад», смыкание разрыва (из памяти видно и это)
            res.Add(b with { Idx = Math.Clamp(b.Idx + vel[i] * t, 0, cv.Count - 1) });
        }
        return res;
    }

    /// <summary>
    /// Какой кривой уровня (файл .dat) соответствует каждая кривая в памяти: x/y шаров против точки трассы на их месте.
    /// null — шаров ещё нет (сверить не по чему); −1 — кривая в памяти ни на одну из файла не легла.
    /// </summary>
    private static int[]? MapCurves(ZumaTrack lv, List<ZumaMemory.MemBall> chain)
    {
        if (chain.Count == 0) return null;
        var map = new int[chain.Max(b => b.Curve) + 1];
        Array.Fill(map, -1);
        foreach (var grp in chain.GroupBy(b => b.Curve))
        {
            var sample = grp.Where(b => b.Way >= 0).Take(12).ToList();
            if (sample.Count == 0) continue;
            var best = (K: -1, D: float.MaxValue);
            for (var k = 0; k < lv.Curves.Count; k++)
            {
                var cv = lv.Curves[k];
                var d = sample.Average(b => { var p = cv.At(b.Way); return MathF.Sqrt((p.X - b.X) * (p.X - b.X) + (p.Y - b.Y) * (p.Y - b.Y)); });
                if (d < best.D) best = (k, d);
            }
            map[grp.Key] = best.D < 12 ? best.K : -1;
            Log.Write($"зума: кривая {grp.Key} в памяти → {(best.D < 12 ? $"кривая {best.K} уровня" : "ни одна")} (расхождение {best.D:F1} px)");
        }
        return map;
    }

    /// <summary>
    /// Скорость каждого шара по пути (px/с) из памяти: тот же объект шара (адрес) в чтении 0,06–0,4 с назад — без
    /// угадывания сдвига по картинке; у только что вставленного шара — скорость соседа по куску.
    /// </summary>
    private static float[] MemVelocities(List<ZumaMemory.MemBall> balls, Queue<(double T, Dictionary<uint, float> W)> ways, double now)
    {
        var v = new float[balls.Count];
        var known = new bool[balls.Count];
        Dictionary<uint, float>? old = null;
        double dt = 0;
        foreach (var h in ways)
            if (now - h.T >= 0.06) { old = h.W; dt = now - h.T; }   // самое свежее из тех, что старше 0,06 с
        if (old is not null)
            for (var i = 0; i < balls.Count; i++)
                if (old.TryGetValue(balls[i].Addr, out var w) && MathF.Abs(balls[i].Way - w) < 400 * dt)
                {
                    v[i] = (float)((balls[i].Way - w) / dt);
                    known[i] = true;
                }
        for (var i = 0; i < balls.Count; i++)
        {
            if (known[i]) continue;
            var got = false;
            for (var d = 1; d < 4 && !got; d++)
                foreach (var j in new[] { i - d, i + d })
                    if (j >= 0 && j < balls.Count && known[j] && balls[j].Curve == balls[i].Curve
                        && MathF.Abs(balls[j].Way - balls[i].Way) <= 2.5f * ZumaTrack.BallR * d)
                    {
                        v[i] = v[j];
                        got = true;
                        break;
                    }
        }
        var cur = new Dictionary<uint, float>(balls.Count);
        foreach (var b in balls) cur[b.Addr] = b.Way;
        ways.Enqueue((now, cur));
        while (ways.Count > 0 && now - ways.Peek().T > 0.4) ways.Dequeue();
        return v;
    }

    /// <summary>Точка на краю кадра по лучу из лягушки — туда уводится курсор игры после выстрела.</summary>
    private static PointF Edge(PointF gun, PointF u, int w, int h)
    {
        var t = float.MaxValue;
        if (u.X > 1e-4f) t = Math.Min(t, (w - 3 - gun.X) / u.X);
        if (u.X < -1e-4f) t = Math.Min(t, (2 - gun.X) / u.X);
        if (u.Y > 1e-4f) t = Math.Min(t, (h - 3 - gun.Y) / u.Y);
        if (u.Y < -1e-4f) t = Math.Min(t, (2 - gun.Y) / u.Y);
        if (t == float.MaxValue || t < 0) t = 0;
        return new PointF(gun.X + u.X * t, gun.Y + u.Y * t);
    }

    /// <summary>
    /// Адреса «текущий/следующий шар» лягушки в памяти игры: прежние ещё живы — оставить, иначе искать заново
    /// (две смены шаров правой кнопкой сообщениями окну, ~3 с — как раз пока цепочка вкатывается).
    /// </summary>
    /// <param name="at">Где стоит курсор (не над окном Клавы) — там же и правая кнопка.</param>
    private static async Task FindGunColorsAsync(LocalIo io, PointF at, Tint cur, Tint next, CancellationToken ct)
    {
        io.Mem ??= ZumaMemory.Open(io.Win);
        if (io.Mem is not { } mem) { Log.Write("зума: память игры не открылась — цвета глазами"); return; }
        if (io.GunMem is { } old && mem.ReadGun(old) is not null) return;
        io.MemTries++;
        var sw = Stopwatch.StartNew();
        io.GunMem = await mem.FindGunAsync(() => io.SwapAsync(at), ZumaMemory.TintType(cur), ZumaMemory.TintType(next), ct);
        Log.Write(io.GunMem is { } g
            ? $"зума: лягушка в памяти — шары в {g.CurSlot:X8} / {g.NextSlot:X8}, смещение {g.Off} ({sw.ElapsedMilliseconds} мс), цвета {mem.ReadGun(g)}"
            : $"зума: лягушку в памяти не нашла ({sw.ElapsedMilliseconds} мс, попытка {io.MemTries}) — цвета глазами");
    }

    // ---------- уровень ----------

    /// <summary>Сыграть один уровень локальной Зумы: до окна итогов, конца шаров, стопа или времени.</summary>
    private static async Task<string> PlayLocalLevelAsync(LocalIo io, string gameDir, TimeSpan limit, CancellationToken ct,
        Action<Frame, Scene, Move?, PointF?>? debug)
    {
        var clock = Stopwatch.StartNew();
        ZumaTrack? lv = null;
        Frame? f = null;
        for (var i = 0; i < 30 && lv is null; i++)
        {
            ct.ThrowIfCancellationRequested();
            f = io.Grab();
            if (f is not null && io.Last is { } first && ZumaTrack.Identify(first, gameDir) is { } id)
            {
                lv = id.Level;
                Log.Write($"зума: уровень «{lv.Name}» ({lv.Id}), кривых {lv.Curves.Count}, лягушка ({lv.Gun.X:F0},{lv.Gun.Y:F0}), " +
                          $"кадр {f.W}×{f.H} в окне {io.Game}, отличие фона {id.Diff:F1}");
            }
            else await Task.Delay(200, ct);
        }
        if (lv is null || f is null) return "не узнала уровень по фону — на экране не поле с шарами";

        int fw = f.W, fh = f.H;
        float kx = fw / (float)ZumaTrack.W, ky = fh / (float)ZumaTrack.H;
        var gun = new PointF(lv.Gun.X * kx, lv.Gun.Y * ky);
        var u = new PointF(0, -1);
        bool Avoid(PointF p) => p.Y < ZumaTrack.Hud * ky || p.X < 0 || p.X >= fw || p.Y >= fh || io.UnderKlava(p);
        // курсор после выстрела — на краю поля по тому же лучу (стрелка не закрывает шары), но не на окне Клавы
        PointF Park(PointF dir)
        {
            var e = Edge(gun, dir, fw, fh);
            for (var t = 1f; t >= 0.15f; t -= 0.05f)
            {
                var p = new PointF(gun.X + (e.X - gun.X) * t, gun.Y + (e.Y - gun.Y) * t);
                if (!Avoid(p)) return p;
            }
            return new PointF(gun.X + dir.X * 3 * ZumaTrack.BallR * ky, gun.Y + dir.Y * 3 * ZumaTrack.BallR * ky);
        }
        var park = Park(u);
        io.Move(park);
        PointF CursorL() => new(park.X / kx, park.Y / ky);

        // цвета лягушки — из памяти игры (одиночная, друг разрешил): на картинке белый шар в пасти трудно отличить от
        // бежевой морды. Адреса ищутся, когда глаза уверенно видят разные цвета в пасти и на спине (правда для сверки),
        // один раз на процесс игры; не нашлись — цвета глазами
        io.Mem ??= ZumaMemory.Open(io.Win);
        // а лучше всё из памяти (28.09, классы движка по RTTI, ZumaChain.cs): цепочка целиком — с туннелями, бонусами,
        // лопающимися шарами и точной скоростью каждого куска; лягушка — цвета, готовность к выстрелу, скорость шара.
        // Глаза тогда только на окна поверх поля и «тот ли уровень» — 4 раза в секунду
        // шаров в пасти в первые доли секунды уровня ещё нет — лягушку ждёт сам цикл (28.09: из-за этого уровень шёл глазами)
        var memChain = io.Mem is { } mc0 && mc0.FindBoard();
        int[]? curveMap = null;   // кривая в памяти → кривая уровня (сверка x/y шаров с трассой из файла)
        Log.Write(memChain ? "зума: цепочка и лягушка — из памяти игры" : "зума: цепочку в памяти не нашла — играю глазами");
        if (!memChain && io.Mem is { } m0 && io.GunMem is null && m0.Known() is { } kg) io.GunMem = kg;
        var levelStart = clock.Elapsed.TotalSeconds;
        var memVotes = (Cur: 0, Next: 0, Miss: 0);
        var demon = _demon;

        int shots = 0, pops = 0, swaps = 0, swapFails = 0, frameNo = 0, looks = 0;
        double lastFire = -10, reload = 0.22;   // когда лягушка стрельнула в последний раз и сколько ей перезаряжаться (подстраивается)
        double lastLook = -10, lastLog = 0;
        var swapped = false;
        (Tint Cur, Tint Next)? beforeSwap = null;
        var history = new Queue<(double T, List<ZumaTrack.TrackBall> B)>();
        var ways = new Queue<(double T, Dictionary<uint, float> W)>();
        var pending = new List<(int Curve, float From, float To, float V, double T0, double Until)>();
        // проверка прицела по памяти: куда встал выстрел (шар с Bullet*) против места, куда целились, — px пути
        (int Curve, float Idx, float V, double T)? aimed = null;
        var aimErr = new List<float>();
        // лопнула ли задуманная тройка: у места попадания шары стали «лопающимися» (Ball+34) — честный счёт попаданий
        var popChecks = new List<(int Curve, float Idx, float V, double T)>();
        int popOk = 0, popMiss = 0, demonShots = 0;
        var noBalls = Stopwatch.StartNew();
        var colorless = Stopwatch.StartNew();
        var lost = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested)
        {
            // время партии уровень НЕ обрывает (28.09 друг: «раньше времени мышь отпускает» — по таймеру она бросала мышь
            // посреди уровня, и цепочка доезжала до черепа): начатый уровень доигрывается, время проверяется между
            // уровнями. Здесь только страховка от зависания — столько один уровень не идёт
            if (clock.Elapsed > limit + TimeSpan.FromMinutes(10)) return $"время вышло. {Tally(shots, pops, swaps)}";
            if (io.Interrupted() is { } why) return $"{why} — остановилась. {Tally(shots, pops, swaps)}";
            if (io.UserMoved())
            {
                // друг схватил мышь: не воюем за курсор — ждём, пока отпустит (1,5 с без движения); случайный толчок
                // игру не останавливает, а 20 с своей игры — значит, играет сам
                if (await io.WaitUserReleasedAsync(TimeSpan.FromSeconds(20), ct) is false)
                    return $"друг взял мышь и играет сам — отдала. {Tally(shots, pops, swaps)}";
                io.Move(park);
                history.Clear();
                ways.Clear();
                continue;
            }
            var tFrame = Stopwatch.StartNew();
            var now = clock.Elapsed.TotalSeconds;
            // глаза: без памяти — каждый круг; с памятью — раз в 0,25 с (окна поверх поля, тот ли уровень)
            if (!memChain || now - lastLook >= 0.25)
            {
                f = io.Grab();
                if (f is null || io.Last is not { } bmp) { await Task.Delay(100, ct); continue; }
                lastLook = now;
                frameNo++;
                looks++;
                // окно поверх поля: итоги уровня, «пройдено», пауза, конец игры — дальше разберётся меню (проверка не каждый
                // кадр: заливка по всему кадру стоит десятки мс, а на быстрых раундах дорог каждый выстрел)
                if ((memChain || frameNo % 6 == 0) && ZumaMenu.GreenButtons(bmp).Count > 0)
                    return $"уровень кончился — на экране окно игры. {Tally(shots, pops, swaps)}";
                if (memChain ? looks % 4 == 0 : frameNo % 15 == 0)
                {
                    if (ZumaTrack.Identify(bmp, gameDir)?.Level.Id == lv.Id) lost.Restart();
                    else if (lost.Elapsed > TimeSpan.FromSeconds(2.5)) return $"экран сменился — это уже не поле «{lv.Name}». {Tally(shots, pops, swaps)}";
                }
            }
            var tGrab = tFrame.ElapsedMilliseconds;

            // лягушка из памяти: не готова (плевок, вкатывание нового шара) — щелчок игра проглотит, ждём по 3 мс
            ZumaMemory.MemFrog? frog = null;
            if (memChain && io.Mem is { } mf)
            {
                if (!mf.OnBoard)
                {
                    memChain = false;
                    Log.Write("зума: Board в памяти пропал — дальше глазами");
                    continue;
                }
                frog = mf.ReadFrog();
                if (frog is not { State: 0 })
                {
                    if (frog is not null) colorless.Restart();
                    else if (colorless.Elapsed > TimeSpan.FromSeconds(25)) return $"25 с нет шара в пасти лягушки — пауза или меню. {Tally(shots, pops, swaps)}";
                    await Task.Delay(frog is null ? 20 : 3, ct);
                    continue;
                }
            }

            List<ZumaTrack.TrackBall> sorted;
            float[] vel;
            if (memChain && io.Mem is { } mr)
            {
                if (mr.ReadChain() is not { } chain) { memChain = false; Log.Write("зума: цепочка в памяти не читается — дальше глазами"); continue; }
                curveMap ??= MapCurves(lv, chain);
                for (var pi = popChecks.Count - 1; pi >= 0; pi--)
                {
                    var pc = popChecks[pi];
                    if (now < pc.T - 0.15) continue;
                    var exp = pc.Idx + pc.V * (float)(now - pc.T);
                    if (chain.Any(b => b.Dying && curveMap is { } cm && b.Curve < cm.Length && cm[b.Curve] == pc.Curve && MathF.Abs(b.Way - exp) < 90))
                    { popOk++; popChecks.RemoveAt(pi); }
                    else if (now > pc.T + 0.7) { popMiss++; popChecks.RemoveAt(pi); }
                }
                var mb = chain.Where(b => b.Curve < (curveMap?.Length ?? 0) && curveMap![b.Curve] >= 0 && !b.Dying && b.Type is >= 0 and <= 5)
                    .Select(b => b with { Curve = curveMap![b.Curve] }).OrderBy(b => b.Curve).ThenBy(b => b.Way).ToList();
                sorted = mb.Select(b => new ZumaTrack.TrackBall(b.Curve, b.Way, ZumaMemory.TypeTint(b.Type),
                    b.Power == ZumaMemory.PowerNone ? -1 : b.Power, b.Incoming)).ToList();
                vel = MemVelocities(mb, ways, now);
                // куда встал наш прошлый выстрел: новый шар с Bullet* на той же кривой — против места, куда целились
                if (aimed is { } am && now > am.T - 0.1)
                {
                    var exp = am.Idx + am.V * (float)(now - am.T);
                    var near = mb.Where(b => b.Incoming && b.Curve == am.Curve).Select(b => b.Way - exp).OrderBy(MathF.Abs).ToList();
                    if (near.Count > 0 && MathF.Abs(near[0]) < 200) { aimErr.Add(near[0]); aimed = null; }
                    else if (now > am.T + 0.6) aimed = null;
                }
                if (now - lastLog > 10)
                {
                    lastLog = now;
                    var err = aimErr.Count > 0 ? $", встал от цели в среднем {aimErr.Average(MathF.Abs):F0} px (сдвиг {aimErr.Average():+0;-0}, по {aimErr.Count})" : "";
                    var hit = popOk + popMiss > 0 ? $", лопнуло {popOk} из {popOk + popMiss} задуманных троек" : "";
                    var dm = demon ? $", перекрашено {demonShots}" : "";
                    Log.Write($"зума: память: шаров {mb.Count}, чтение {tFrame.ElapsedMilliseconds - tGrab} мс, выстрелов {shots}, в тройку {pops}{hit}{dm}{err}");
                    aimErr.Clear();
                    popOk = popMiss = 0;
                }
            }
            else
            {
                if (f is null) { await Task.Delay(50, ct); continue; }
                sorted = lv.Scan(f, CursorL()).OrderBy(b => b.Curve).ThenBy(b => b.Idx).ToList();
                if (frameNo % 50 == 0) Log.Write($"зума: кадр {frameNo}: снимок {tGrab} мс, до разбора {tFrame.ElapsedMilliseconds} мс, шаров {sorted.Count}, выстрелов {shots}");
                history.Enqueue((now, sorted));
                while (history.Count > 0 && now - history.Peek().T > 0.45) history.Dequeue();
                vel = Velocities(sorted, history, now);
            }
            if (sorted.Count == 0)
            {
                if (noBalls.Elapsed > TimeSpan.FromSeconds(8)) return $"шаров на поле нет — уровень кончился. {Tally(shots, pops, swaps)}";
                history.Clear();
                await Task.Delay(memChain ? 30 : 120, ct);
                continue;
            }
            noBalls.Restart();

            Tint seenCur = Tint.None, seenNext = Tint.None, cur, next;
            if (frog is { } fq) (cur, next) = (ZumaMemory.TypeTint(fq.Cur), ZumaMemory.TypeTint(fq.Next));
            else
            {
                if (f is null) { await Task.Delay(50, ct); continue; }
                (seenCur, seenNext) = FrogColors(f, gun, u, ky);
                (cur, next) = (seenCur, seenNext);
            }
            if (frog is null && io.GunMem is null && io.MemTries < 2 && now - levelStart > 2 && seenCur != seenNext
                && seenCur is not (Tint.None or Tint.White) && seenNext is not (Tint.None or Tint.White))
            {
                await FindGunColorsAsync(io, park, seenCur, seenNext, ct);
                history.Clear();
                continue;
            }
            if (frog is null && io.Mem is { } mem && io.GunMem is { } gm)
            {
                if (mem.ReadGun(gm) is { } mc)
                {
                    // Память — правда: замер 28.09 (--zuma-local fire) — она меняется в миг щелчка, а глаза ещё ~200 мс
                    // видят в пасти старый шар (плевок), потом пасть пустая, новый шар встаёт к ~350 мс. Раньше «спор с
                    // глазами» шесть раз подряд отключал память — а спорили как раз отстающие глаза
                    (cur, next) = (ZumaMemory.TypeTint(mc.Cur), ZumaMemory.TypeTint(mc.Next));
                    if (seenCur is not (Tint.None or Tint.White) && clock.Elapsed.TotalSeconds - lastFire > 0.45)
                    {
                        // сверка только в покое, когда глаза уже догнали
                        memVotes = seenCur == cur ? (memVotes.Cur + 1, memVotes.Next, memVotes.Miss) : (memVotes.Cur, memVotes.Next, memVotes.Miss + 1);
                        if ((memVotes.Cur + memVotes.Miss) % 40 == 0)
                            Log.Write($"зума: память и глаза в покое согласны {memVotes.Cur} раз, спорят {memVotes.Miss}");
                    }
                }
                else { io.GunMem = null; Log.Write("зума: лягушка в памяти пропала — дальше цвета глазами"); }
            }
            if (beforeSwap is { } bs && cur != Tint.None)
            {
                // смена прошла — текущий стал прежним следующим; нет — ещё раз не пробуем бесконечно
                swapFails = cur == bs.Next ? 0 : swapFails + 1;
                beforeSwap = null;
            }
            // первые 2 с уровня поверх трассы висит «LEVEL 12-1 / название» — её буквы сходят за шары (памяти всё равно)
            if (cur == Tint.None || frog is null && (now - levelStart < 2.0 || seenCur == Tint.None && io.GunMem is null))
            {
                // шар ещё не встал в пасть (перезарядка, вкатывание цепочки) или пасть закрыта взрывом — ждём
                if (colorless.Elapsed > TimeSpan.FromSeconds(25)) return $"25 с не вижу шар в пасти лягушки — пауза или меню. {Tally(shots, pops, swaps)}";
                await Task.Delay(25, ct);
                continue;
            }
            colorless.Restart();

            var speed = frog?.Speed ?? ShotSpeed;
            var scene = TrackScene(lv, Lead(lv, sorted, vel, frog is null ? LatencySec : MemLatencySec, speed), fw, fh);
            scene.Cur = cur;
            scene.Next = next;
            scene.Facing = u;
            pending.RemoveAll(p => now > p.Until);
            bool Skip(int i) => scene.GroupBusy.Length > scene.Group[i] && scene.GroupBusy[scene.Group[i]] || pending.Any(p =>
            {
                if (p.Curve != scene.CurveOf[i]) return false;
                var drift = p.V * (float)(now - p.T0);
                return scene.Idx[i] >= p.From + drift - 20 && scene.Idx[i] <= p.To + drift + 20;
            });
            var mv = Plan(scene, gun, swapped, swapFails < 3, Avoid, Skip);
            if (demon && frog is { } fd && io.Mem is { } mw)
            {
                // режим демона: лучший выстрел среди ВСЕХ цветов на поле (опасность у черепа, бонусы, комбо уже в очках
                // Best) — и шар в пасти перекрашивается под него. Своим цветом стреляем, если он почти не хуже
                var own = mv.Pop && !mv.Swap ? Best(scene, gun, cur, Avoid, Skip).Score : -1;
                var top = (Score: -1f, Aim: default(PointF), Hit: -1, T: Tint.None);
                foreach (var t in scene.Balls.Select(b => b.C).Where(c => c is not (Tint.None)).Distinct())
                {
                    var b = Best(scene, gun, t, Avoid, Skip);
                    if (b.Score > top.Score) top = (b.Score, b.Aim, b.Hit, t);
                }
                if (top.Score >= 100 && top.T != cur && top.Score > own + 25 && mw.SetFrogBall(ZumaMemory.TintType(top.T)))
                {
                    demonShots++;
                    mv = new Move(top.Aim, false, true, $"демон: {Name(cur)}→{Name(top.T)}, группа {scene.GroupSize[scene.Group[top.Hit]]}", top.Hit);
                    scene.Cur = cur = top.T;
                }
            }
            if (f is not null) debug?.Invoke(f, scene, mv, gun);
            if (mv.Swap)
            {
                beforeSwap = (cur, next);
                await io.SwapAsync(park);
                swaps++;
                swapped = true;
                await Task.Delay(90, ct);
                continue;
            }
            swapped = false;
            float ax = mv.Aim.X - gun.X, ay = mv.Aim.Y - gun.Y, al = MathF.Sqrt(ax * ax + ay * ay);
            if (al < 1) { await Task.Delay(50, ct); continue; }
            u = new PointF(ax / al, ay / al);
            if (frog is { } f0 && io.Mem is { } mq)
            {
                // с памятью: лягушка готова (State 0) — щелчок сразу; ушёл ли — по State/смене шара в пасти (опрос 5 мс)
                await io.ShootAsync(mv.Aim);
                park = Park(u);
                io.Move(park);
                var fired = false;
                for (var k = 0; k < 30 && !fired; k++)
                {
                    await Task.Delay(5, ct);
                    fired = mq.ReadFrog() is { } f1 && (f1.State != 0 || f1.CurPtr != f0.CurPtr);
                }
                if (!fired) continue;
                lastFire = clock.Elapsed.TotalSeconds;
                if (mv.Hit >= 0)
                {
                    var hi = sorted.FindIndex(b => b.Curve == scene.CurveOf[mv.Hit] && MathF.Abs(b.Idx - scene.Idx[mv.Hit]) < 60);
                    var fly = MemLatencySec + Math.Max(0, al / ky - ZumaTrack.MouthD) / speed;
                    aimed = (scene.CurveOf[mv.Hit], scene.Idx[mv.Hit], hi >= 0 ? vel[hi] : 0, now + fly);
                    if (mv.Pop) popChecks.Add(aimed.Value);
                }
            }
            else
            {
                var memBefore = io.Mem is { } mb && io.GunMem is { } gb ? mb.ReadGunRaw(gb) : null;
                if (memBefore is not null)
                {
                    // перезарядка: щелчок раньше неё игра глотает (28.09: каждое второе решение повторялось — выстрел
                    // не уходил, и на повтор тратилось ~250 мс). Ждём ровно столько, сколько лягушке надо с прошлого выстрела
                    var wait = lastFire + reload - clock.Elapsed.TotalSeconds;
                    if (wait > 0.005) await Task.Delay(TimeSpan.FromSeconds(wait), ct);
                }
                var tryAt = clock.Elapsed.TotalSeconds;
                await io.ShootAsync(mv.Aim);
                park = Park(u);
                io.Move(park);
                if (memBefore is { } bf && io.Mem is { } ma && io.GunMem is { } ga)
                {
                    // ушёл ли выстрел: в ячейках лягушки другие шары-объекты. Опрос каждые 10 мс — сразу дальше, без
                    // лишней паузы; не ушёл за 250 мс — перезарядка дольше, чем думали: её отодвинуть, выстрел не считать
                    var fired = false;
                    for (var k = 0; k < 25 && !fired; k++)
                    {
                        await Task.Delay(10, ct);
                        fired = ma.ReadGunRaw(ga) is { } af && af != bf;
                    }
                    var since = tryAt - lastFire;
                    if (!fired)
                    {
                        reload = Math.Min(0.6, Math.Max(reload, since) + 0.03);
                        continue;
                    }
                    lastFire = clock.Elapsed.TotalSeconds;
                    reload = Math.Max(0.06, reload * 0.97);   // прошло — в следующий раз чуть раньше
                    if (shots % 20 == 0) Log.Write($"зума: перезарядка ≈{reload * 1000:F0} мс (прошлый выстрел через {since * 1000:F0} мс)");
                }
                else
                {
                    // без памяти — по старинке: пауза на перезарядку, дальше решит следующий кадр
                    await Task.Delay(80, ct);
                }
            }
            shots++;
            if (mv.Pop) pops++;
            // куда полетел шар — в ту группу следующим выстрелом не бить, пока не долетит и не лопнет
            if (mv.Hit >= 0)
            {
                var g = scene.Group[mv.Hit];
                var members = Enumerable.Range(0, scene.Balls.Count).Where(i => scene.Group[i] == g).ToList();
                var flight = Math.Max(0, al / ky - ZumaTrack.MouthD) / speed;
                var ti = sorted.FindIndex(b => b.Curve == scene.CurveOf[mv.Hit] && MathF.Abs(b.Idx - scene.Idx[mv.Hit]) < 60);
                pending.Add((scene.CurveOf[mv.Hit], members.Min(i => scene.Idx[i]), members.Max(i => scene.Idx[i]), ti >= 0 ? vel[ti] : 0, now, now + flight + 0.35));
            }
            _status = $"играю в Зуму («{lv.Name}»): выстрелов {shots}, в тройку {pops}";
        }
        return $"остановили. {Tally(shots, pops, swaps)}";
    }

    // ---------- партия ----------

    /// <summary>
    /// Партия: меню до уровня (ZumaMenu, щелчки сообщениями) → уровень → итоги → следующий, пока не выйдет время
    /// или друг не заберёт игру (Esc/Backspace, другое окно, «стоп», пункт меню Клавы).
    /// </summary>
    private static async Task<string> PlayLocalAsync(IntPtr win, TimeSpan limit, CancellationToken token, Action<Frame, Scene, Move?, PointF?>? debug = null)
    {
        var gameDir = LocalGameDir(win);
        if (gameDir is null || ZumaTrack.All(gameDir).Count == 0)
            return "не нашла файлы уровней игры (levels\\levels.xml и трассы .dat) — без трасс не играю";
        var io = new LocalIo(win);
        try { return await PlayLocalLevelsAsync(io, gameDir, limit, token, debug); }
        finally
        {
            io.Mem?.Dispose();
            io.Last?.Dispose();
        }
    }

    private static async Task<string> PlayLocalLevelsAsync(LocalIo io, string gameDir, TimeSpan limit, CancellationToken token, Action<Frame, Scene, Move?, PointF?>? debug)
    {
        var win = io.Win;
        var clock = Stopwatch.StartNew();
        var levels = new List<string>();
        var idle = 0;
        string? MenuKeys() => Native.GetForegroundWindow() == win ? KeysOnly(win) : null;
        Task<bool> OnLevel() => Task.FromResult(OnLocalLevel(win, gameDir));
        while (!token.IsCancellationRequested && clock.Elapsed < limit)
        {
            _status = $"Зума: прохожу меню (уровней сыграно {levels.Count})";
            var nav = await ZumaMenu.ToLevelAsync(win, OnLevel, MenuKeys, s => { Log.Write(s); _status = s; }, token, 90);
            if (nav is not null) return levels.Count == 0 ? $"до уровня не дошла: {nav}" : $"{nav}. Сыграно уровней: {levels.Count} ({string.Join("; ", levels.TakeLast(3))})";
            Uia.Focus(win);   // PopCap без фокуса стоит на паузе
            await Task.Delay(200, token);
            // уровень уже пошёл (меню пройдено — цепочка катится): доигрываем его, даже если время партии вышло,
            // иначе брошенная лягушка сливает жизнь. Время проверяется в while — до меню следующего уровня
            var left = limit - clock.Elapsed;
            var r = await PlayLocalLevelAsync(io, gameDir, left > TimeSpan.Zero ? left : TimeSpan.Zero, token, debug);
            levels.Add(r);
            Log.Write($"зума: уровень — {r}");
            if (r.StartsWith("остановил", StringComparison.Ordinal) || r.StartsWith("время вышло", StringComparison.Ordinal)
                || r.Contains("друг нажал", StringComparison.Ordinal) || r.Contains("другое окно", StringComparison.Ordinal)
                || r.Contains("закрылось", StringComparison.Ordinal))
                return $"{r}. Сыграно уровней: {levels.Count}";
            idle = r.Contains("Выстрелов: 0", StringComparison.Ordinal) || !r.Contains("Выстрелов", StringComparison.Ordinal) ? idle + 1 : 0;
            if (idle >= 3) return $"три раза подряд уровень не пошёл ({r}). Сыграно уровней: {levels.Count}";
        }
        return $"время вышло — последний уровень доиграла, дальше не начинаю. Сыграно уровней: {levels.Count} ({string.Join("; ", levels.TakeLast(3))})";
    }

    /// <summary>
    /// Начать: локальное окно Zuma Deluxe (нет — запустить установленную) и играть в фоне. Разрешения не спрашивает —
    /// забрать игру можно в любой момент: Esc/Backspace, переключиться в другое окно, «стоп» голосом или пункт
    /// «⏹ Забрать игру у Клавы» в её меню.
    /// </summary>
    public static async Task<string> StartAsync(int seconds, bool demon = true)
    {
        if (_cts is not null) return $"Уже играю: {_status}";
        _demon = demon;
        var win = Uia.ZumaWindow();
        if (win == IntPtr.Zero)
        {
            var exe = FindInstalled();
            if (exe is null)
                return "[НЕ СРАБОТАЛО] Zuma Deluxe не запущена, и установленной игры не нашла (искала X:\\Games, Program Files). " +
                       "Запусти zuma.exe и позови меня снова.";
            try { Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose(); }
            catch (System.ComponentModel.Win32Exception ex) { return $"[НЕ СРАБОТАЛО] Не запустилась {exe}: {ex.Message}"; }
            for (var i = 0; i < 40 && win == IntPtr.Zero; i++) { await Task.Delay(500); win = Uia.ZumaWindow(); }
            if (win == IntPtr.Zero) return $"[НЕ СРАБОТАЛО] Запустила {exe}, но окно игры не появилось за 20 с.";
            await Task.Delay(1500);
        }
        // окно НЕ разворачиваем: PopCap-игра от «развернуть» ломает картинку (угол 640×480 + призрак кадра)
        await UnzoomAsync(win);
        Uia.Focus(win);
        await Task.Delay(300);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var limit = TimeSpan.FromSeconds(Math.Clamp(seconds <= 0 ? 3600 : seconds, 30, 7200));
        _status = "начинаю Зуму: прохожу меню";
        GameMode.Enter("zuma");
        _ = Task.Run(async () =>
        {
            var result = "остановили";
            try { result = await PlayLocalAsync(win, limit, token); }
            catch (OperationCanceledException) { result = "остановили"; }
            catch (Exception ex)
            {
                // раньше ловились только три вида ошибок: любая другая убивала фоновую партию молча, а флаг «играю»
                // оставался — бот стоял, и новая партия отвечала «Уже играю»
                Log.Error("зума: автопилот упал", ex);
                result = $"сломалось: {ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                GameMode.Leave("zuma");
                _cts?.Dispose();
                _cts = null;
                _status = $"не играю (прошлая партия: {result})";
            }
            Log.Write($"зума: {result}");
            Finished?.Invoke(result);
        });
        return "Играю в Зуму: сама прохожу меню (заставка → ADVENTURE → PLAY → OK), потом стреляю, после уровня — дальше. " +
               "Мышь друга не трогаю — стреляю сообщениями прямо в окно игры. Забрать игру: Esc или Backspace, переключиться " +
               "в другое окно, сказать «стоп» или пункт «⏹ Забрать игру у Клавы» в моём меню.";
    }

    /// <summary>Установленная Zuma Deluxe: обычные места установки.</summary>
    private static string? FindInstalled()
    {
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new List<string>();
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Games", "Zuma Deluxe", "zuma.exe"));
        candidates.Add(Path.Combine(pf86, "Zuma Deluxe", "zuma.exe"));
        candidates.Add(Path.Combine(pf, "Zuma Deluxe", "zuma.exe"));
        candidates.Add(Path.Combine(pf86, "PopCap Games", "Zuma Deluxe", "zuma.exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    // ---------- проверка ----------

    /// <summary>
    /// KlavaDesk.exe --zuma-play [сек] [out=папка] — живой прогон автопилота в открытом окне Zuma Deluxe без мозга
    /// (меню, уровни, стрельба). Ход игры — в консоль, кадры с разметкой (трасса, шары, лягушка, выстрел) — в out.
    /// </summary>
    public static int LocalTest(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 60;
        var outDir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..]
            ?? Path.Combine(Path.GetTempPath(), "zuma_play");
        Directory.CreateDirectory(outDir);
        // dir= — окно Зумы из этой папки (своя тестовая копия, когда рядом открыта игра друга)
        var dir = args.FirstOrDefault(a => a.StartsWith("dir=", StringComparison.Ordinal))?[4..];
        var win = dir is null ? Uia.ZumaWindow() : ZumaWindowIn(dir);
        if (win == IntPtr.Zero) { Console.WriteLine("окна Zuma Deluxe нет"); return 1; }
        _demon = !args.Contains("honest");
        Uia.Focus(win);
        Thread.Sleep(400);
        var n = 0;
        var sw = Stopwatch.StartNew();
        var result = PlayLocalAsync(win, TimeSpan.FromSeconds(Math.Clamp(seconds, 10, 3600)), CancellationToken.None, (f, sc, mv, frog) =>
        {
            n++;
            Console.WriteLine($"{sw.Elapsed.TotalSeconds,6:F1} #{n,3} шаров {sc.Balls.Count,3}, сейчас {Name(sc.Cur)}, след. {Name(sc.Next)} → " +
                              (mv is null ? "жду" : mv.Swap ? $"смена: {mv.Why}" : $"({mv.Aim.X:F0},{mv.Aim.Y:F0}) {mv.Why}"));
            // PNG кадра — десятки мс прямо в игровом цикле: пишем редко, чтобы проверка играла как живая
            if (n <= 3 || n % 40 == 0)
            {
                using var bmp = FrameBitmap(f);
                using var ann = Annotate(bmp, sc, mv, frog);
                ann.Save(Path.Combine(outDir, $"z{n:D4}.png"), ImageFormat.Png);
            }
        }).GetAwaiter().GetResult();
        Console.WriteLine($"итог: {result}");
        Console.WriteLine($"кадры: {outDir}");
        return result.Contains("Выстрелов: 0") ? 1 : 0;
    }

    /// <summary>
    /// KlavaDesk.exe --zuma-local colors [раз] — на открытом уровне: найти лягушку в памяти и сверить цвета из памяти
    /// с шаром в пасти (глазами) через смены шаров; кадры лягушки — в %TEMP%\zuma_colors.
    /// </summary>
    private static int ColorsTest(int rounds)
    {
        var win = Uia.ZumaWindow();
        if (win == IntPtr.Zero) { Console.WriteLine("окна Zuma Deluxe нет"); return 1; }
        var gameDir = LocalGameDir(win);
        if (gameDir is null) { Console.WriteLine("папки игры нет"); return 1; }
        Uia.Focus(win);
        Thread.Sleep(300);
        var io = new LocalIo(win);
        try
        {
            var f = io.Grab();
            if (f is null || io.Last is null || ZumaTrack.Identify(io.Last, gameDir) is not { } id) { Console.WriteLine("не уровень"); return 1; }
            float kx = f.W / (float)ZumaTrack.W, ky = f.H / (float)ZumaTrack.H;
            var gun = new PointF(id.Level.Gun.X * kx, id.Level.Gun.Y * ky);
            var up = new PointF(0, -1);
            var aim = new PointF(gun.X, gun.Y - 110 * ky);
            io.Move(aim);
            // правда для поиска — глазами; одинаковые цвета в пасти и на спине — сменить ничего не даст, ждём другие
            for (var t = 0; t < 40 && io.GunMem is null; t++)
            {
                Thread.Sleep(250);
                if (io.Grab() is not { } f0) continue;
                var (c0, n0) = FrogColors(f0, gun, up, ky);
                Console.WriteLine($"глазами: пасть {Name(c0)}, спина {Name(n0)}");
                if (c0 == n0 || c0 is Tint.None or Tint.White || n0 is Tint.None or Tint.White) continue;
                FindGunColorsAsync(io, aim, c0, n0, CancellationToken.None).GetAwaiter().GetResult();
                if (io.GunMem is null && io.MemTries >= 3) break;
            }
            Console.WriteLine($"уровень «{id.Level.Name}», лягушка ({gun.X:F0},{gun.Y:F0}), в памяти: {(io.GunMem is { } g ? $"{g.CurSlot:X8}/{g.NextSlot:X8}" : "нет")}");
            var dir = Path.Combine(Path.GetTempPath(), "zuma_colors");
            Directory.CreateDirectory(dir);
            for (var i = 0; i < rounds; i++)
            {
                Uia.Focus(win);
                io.Move(aim);
                Thread.Sleep(250);
                f = io.Grab();
                if (f is null || io.Last is null) break;
                var (vc, vn) = FrogColors(f, gun, up, ky);
                var mc = io.Mem is { } m && io.GunMem is { } gm ? m.ReadGun(gm) : null;
                Console.WriteLine($"#{i}: глазами пасть {Name(vc)}, спина {Name(vn)}; память {(mc is { } c ? $"{c.Cur}={Name(ZumaMemory.TypeTint(c.Cur))}, {c.Next}={Name(ZumaMemory.TypeTint(c.Next))}" : "—")}");
                var box = Rectangle.Intersect(new Rectangle((int)(gun.X - 60 * ky), (int)(gun.Y - 60 * ky), (int)(120 * ky), (int)(120 * ky)), new Rectangle(0, 0, f.W, f.H));
                using (var crop = io.Last.Clone(box, PixelFormat.Format24bppRgb)) crop.Save(Path.Combine(dir, $"frog{i}.png"), ImageFormat.Png);
                io.SwapAsync(aim).GetAwaiter().GetResult();
                Thread.Sleep(300);
            }
            Console.WriteLine($"кадры лягушки: {dir}");
            return 0;
        }
        finally
        {
            io.Mem?.Dispose();
            io.Last?.Dispose();
        }
    }

    /// <summary>
    /// KlavaDesk.exe --zuma-local fire [выстрелов] — когда шар на самом деле уходит: после щелчка каждые 20 мс
    /// память лягушки (типы и указатели) и глаза (пасть, спина) — видно, кто опережает и на сколько.
    /// </summary>
    private static int FireTest(int shotsCount)
    {
        var win = Uia.ZumaWindow();
        if (win == IntPtr.Zero) { Console.WriteLine("окна Zuma Deluxe нет"); return 1; }
        var gameDir = LocalGameDir(win);
        if (gameDir is null) { Console.WriteLine("папки игры нет"); return 1; }
        Uia.Focus(win);
        Thread.Sleep(300);
        var io = new LocalIo(win);
        try
        {
            var f = io.Grab();
            if (f is null || io.Last is null || ZumaTrack.Identify(io.Last, gameDir) is not { } id) { Console.WriteLine("не уровень"); return 1; }
            float kx = f.W / (float)ZumaTrack.W, ky = f.H / (float)ZumaTrack.H;
            var gun = new PointF(id.Level.Gun.X * kx, id.Level.Gun.Y * ky);
            var up = new PointF(0, -1);
            var aim = new PointF(gun.X, gun.Y - 150 * ky);
            io.Move(aim);
            for (var t = 0; t < 40 && io.GunMem is null; t++)
            {
                Thread.Sleep(250);
                if (io.Grab() is not { } f0) continue;
                var (c0, n0) = FrogColors(f0, gun, up, ky);
                if (c0 == n0 || c0 is Tint.None or Tint.White || n0 is Tint.None or Tint.White) continue;
                FindGunColorsAsync(io, aim, c0, n0, CancellationToken.None).GetAwaiter().GetResult();
                if (io.GunMem is null && io.MemTries >= 3) break;
            }
            if (io.Mem is not { } mem || io.GunMem is not { } gm) { Console.WriteLine("лягушку в памяти не нашла"); return 1; }
            for (var s = 0; s < shotsCount; s++)
            {
                Thread.Sleep(700);
                var sw = Stopwatch.StartNew();
                var line = new List<string>();
                string Probe()
                {
                    var fr = io.Grab();
                    var (vc, vn) = fr is null ? (Tint.None, Tint.None) : FrogColors(fr, gun, up, ky);
                    var mt = mem.ReadGun(gm);
                    var raw = mem.ReadGunRaw(gm);
                    return $"{sw.ElapsedMilliseconds,4} мс: глаза {Name(vc)}/{Name(vn)}, память {(mt is { } m ? $"{Name(ZumaMemory.TypeTint(m.Cur))}/{Name(ZumaMemory.TypeTint(m.Next))}" : "—")} [{raw?.Cur:X8}]";
                }
                line.Add("до щелчка " + Probe());
                io.ShootAsync(aim).GetAwaiter().GetResult();
                for (var k = 0; k < 20; k++) { Thread.Sleep(20); line.Add(Probe()); }
                Console.WriteLine($"— выстрел {s + 1}");
                foreach (var l in line) Console.WriteLine("   " + l);
            }
            return 0;
        }
        finally
        {
            io.Mem?.Dispose();
            io.Last?.Dispose();
        }
    }

    /// <summary>Окно Zuma Deluxe, чей exe лежит в папке dir (своя копия игры рядом с игрой друга).</summary>
    private static IntPtr ZumaWindowIn(string dir)
    {
        var found = IntPtr.Zero;
        var full = Path.GetFullPath(dir).TrimEnd('\\');
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || !Uia.WindowTitle(h).Contains("Zuma", StringComparison.OrdinalIgnoreCase)) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (p.MainModule?.FileName is { } exe && exe.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase)) { found = h; return false; }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Кадр обратно в картинку (для разметки).</summary>
    private static Bitmap FrameBitmap(Frame f)
    {
        var bmp = new Bitmap(f.W, f.H, PixelFormat.Format24bppRgb);
        var d = bmp.LockBits(new Rectangle(0, 0, f.W, f.H), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            for (var y = 0; y < f.H; y++) Marshal.Copy(f.Px, y * f.Stride, d.Scan0 + y * d.Stride, Math.Min(f.Stride, d.Stride));
        }
        finally { bmp.UnlockBits(d); }
        return bmp;
    }

    /// <summary>
    /// KlavaDesk.exe --zuma-local кадр.png [game=папка игры] [aim=x,y] — что видит автопилот на сохранённом кадре
    /// (окно целиком, с полями — они отрежутся): уровень, отрезки цвета на трассе, шары, цвета лягушки, выбранный ход.
    /// </summary>
    public static int LocalLook(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length > 0 && args[0] == "colors") return ColorsTest(args.Length > 1 && int.TryParse(args[1], out var n) ? n : 6);
        if (args.Length > 0 && args[0] == "fire") return FireTest(args.Length > 1 && int.TryParse(args[1], out var nf) ? nf : 4);
        var file = args.FirstOrDefault(a => a.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        if (file is null) { Console.WriteLine("нужен снимок: --zuma-local кадр.png [game=папка] [aim=x,y]"); return 2; }
        var gameDir = args.FirstOrDefault(a => a.StartsWith("game=", StringComparison.Ordinal))?[5..] ?? @"D:\Games\Zuma Deluxe";
        using var src = new Bitmap(file);
        var rect = ZumaMenu.GameRect(src, new Rectangle(0, 0, src.Width, src.Height));
        using var game = src.Clone(rect, PixelFormat.Format24bppRgb);
        var sw = Stopwatch.StartNew();
        var id = ZumaTrack.Identify(game, gameDir);
        Console.WriteLine($"картинка игры {rect} ({game.Width}×{game.Height}); уровень: {(id is { } i0 ? $"«{i0.Level.Name}» ({i0.Level.Id}), отличие фона {i0.Diff:F1}" : "не узнан")}, {sw.ElapsedMilliseconds} мс");
        if (id is not { } idn) return 1;
        var lv = idn.Level;
        var f = new Frame(game);
        float kx = f.W / (float)ZumaTrack.W, ky = f.H / (float)ZumaTrack.H;
        var gun = new PointF(lv.Gun.X * kx, lv.Gun.Y * ky);
        var aimArg = args.FirstOrDefault(a => a.StartsWith("aim=", StringComparison.Ordinal))?[4..].Split(',');
        PointF? aim = aimArg is { Length: 2 } ? new PointF(float.Parse(aimArg[0]), float.Parse(aimArg[1])) : null;
        var runs = new List<(int Curve, int From, int To, Tint T)>();
        sw.Restart();
        var balls = lv.Scan(f, aim is { } a0 ? new PointF(a0.X / kx, a0.Y / ky) : null, runs).OrderBy(b => b.Curve).ThenBy(b => b.Idx).ToList();
        Console.WriteLine($"разбор трассы: {sw.ElapsedMilliseconds} мс, отрезков {runs.Count}, шаров {balls.Count}");
        foreach (var r in runs) Console.WriteLine($"  кривая {r.Curve} {r.From,5}–{r.To,5} ({r.To - r.From,3} px) {Name(r.T),-10} → {ZumaTrack.BallsInRun(r.To - r.From)} шар.");
        var scene = TrackScene(lv, balls, f.W, f.H);
        var u = aim is { } a ? new PointF(a.X - gun.X, a.Y - gun.Y) : new PointF(0, -1);
        var ul = MathF.Max(1e-3f, MathF.Sqrt(u.X * u.X + u.Y * u.Y));
        u = new PointF(u.X / ul, u.Y / ul);
        (scene.Cur, scene.Next) = FrogColors(f, gun, u, ky);
        Console.WriteLine($"лягушка ({gun.X:F0},{gun.Y:F0}), в пасти {Name(scene.Cur)}, на спине {Name(scene.Next)}");
        var mv = Plan(scene, gun, false, true, p => p.Y < ZumaTrack.Hud * ky);
        Console.WriteLine($"ход: {(mv.Swap ? "сменить шары" : $"стрелять в ({mv.Aim.X:F0},{mv.Aim.Y:F0})")} — {mv.Why}");
        using (var ann = Annotate(game, scene, mv, gun))
        {
            using (var g = Graphics.FromImage(ann))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var cv in lv.Curves)
                    for (var i = 0; i + 8 < cv.Count; i += 8)
                        g.DrawLine(cv.Hidden[i] ? Pens.DimGray : Pens.Cyan, cv.X[i] * kx, cv.Y[i] * ky, cv.X[i + 8] * kx, cv.Y[i + 8] * ky);
                using var mouth = new Pen(Color.White, 2);
                g.DrawEllipse(mouth, gun.X + u.X * ZumaTrack.MouthD * ky - 7 * ky, gun.Y + u.Y * ZumaTrack.MouthD * ky - 7 * ky, 14 * ky, 14 * ky);
                g.DrawEllipse(mouth, gun.X - u.X * ZumaTrack.BackD * ky - 3.5f * ky, gun.Y - u.Y * ZumaTrack.BackD * ky - 3.5f * ky, 7 * ky, 7 * ky);
            }
            var outFile = Path.ChangeExtension(file, null) + "_local.png";
            ann.Save(outFile, ImageFormat.Png);
            Console.WriteLine($"картинка: {outFile}");
        }
        return balls.Count > 0 ? 0 : 1;
    }
}
