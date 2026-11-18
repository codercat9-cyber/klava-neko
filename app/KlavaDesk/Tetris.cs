using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Клава играет в тетрис сама. Мозг на такое не годится — он думает секунды, а фигура падает за доли секунды,
/// поэтому играет автопилот: смотрит на поле (снимок окна браузера), узнаёт фигуру, выбирает, куда её положить,
/// и жмёт клавиши. Разговор при этом не занят: игра идёт в фоне, закончилась — Клава рассказывает, как сыграла.
///
/// Поле ищется по рамке: две длинные вертикальные серые линии на расстоянии 10 клеток, высота рамки — 20 клеток
/// (так рисует Jstris, куда автопилот и ходит по умолчанию — спринт на 40 линий). Клетка занята, если в её середине
/// яркий цвет; «тень» падения тусклая и в счёт не идёт. Фигура — верхняя связная четвёрка клеток, её форма в
/// начальном повороте SRS даёт тип. Выбор места — эвристика El-Tetris (высота посадки, убранные линии, переходы
/// по строкам и столбцам, дыры, колодцы). Клавиши — как в гайдлайне: ↑ поворот по часовой, Z — против, ←/→, пробел.
/// Остановка: время вышло, фигуры нет 4 с (конец игры), друг нажал Esc или переключился в другое окно.
/// </summary>
internal static class TetrisBot
{
    public const string DefaultUrl = "https://jstris.jezevec10.com/?play=1&mode=1";

    private static CancellationTokenSource? _cts;
    private static string _status = "не играю";
    /// <summary>Игра закончилась (итог — для реплики Клавы).</summary>
    public static event Action<string>? Finished;

    public static bool Playing => _cts is not null;

    // SRS: клетки фигур в своих коробках для поворотов 0, R, 2, L (x вправо, y вниз)
    private static readonly Dictionary<char, (int X, int Y)[][]> Shapes = new()
    {
        ['I'] = [[(0, 1), (1, 1), (2, 1), (3, 1)], [(2, 0), (2, 1), (2, 2), (2, 3)], [(0, 2), (1, 2), (2, 2), (3, 2)], [(1, 0), (1, 1), (1, 2), (1, 3)]],
        ['O'] = [[(1, 0), (2, 0), (1, 1), (2, 1)], [(1, 0), (2, 0), (1, 1), (2, 1)], [(1, 0), (2, 0), (1, 1), (2, 1)], [(1, 0), (2, 0), (1, 1), (2, 1)]],
        ['T'] = [[(1, 0), (0, 1), (1, 1), (2, 1)], [(1, 0), (1, 1), (2, 1), (1, 2)], [(0, 1), (1, 1), (2, 1), (1, 2)], [(1, 0), (0, 1), (1, 1), (1, 2)]],
        ['S'] = [[(1, 0), (2, 0), (0, 1), (1, 1)], [(1, 0), (1, 1), (2, 1), (2, 2)], [(1, 1), (2, 1), (0, 2), (1, 2)], [(0, 0), (0, 1), (1, 1), (1, 2)]],
        ['Z'] = [[(0, 0), (1, 0), (1, 1), (2, 1)], [(2, 0), (1, 1), (2, 1), (1, 2)], [(0, 1), (1, 1), (1, 2), (2, 2)], [(1, 0), (0, 1), (1, 1), (0, 2)]],
        ['J'] = [[(0, 0), (0, 1), (1, 1), (2, 1)], [(1, 0), (2, 0), (1, 1), (1, 2)], [(0, 1), (1, 1), (2, 1), (2, 2)], [(1, 0), (1, 1), (0, 2), (1, 2)]],
        ['L'] = [[(2, 0), (0, 1), (1, 1), (2, 1)], [(1, 0), (1, 1), (1, 2), (2, 2)], [(0, 1), (1, 1), (2, 1), (0, 2)], [(0, 0), (1, 0), (1, 1), (1, 2)]],
    };

    public sealed record Board(Rectangle Rect, int Cell, int Rows, bool[,] Filled);

    public static string Status() => _status;

    /// <summary>Почему поле не узналось на последних попытках (для журнала, когда старт не удался).</summary>
    private static readonly List<string> _whyNot = [];
    private static void Why(string reason)
    {
        lock (_whyNot)
        {
            _whyNot.Add(reason);
            if (_whyNot.Count > 40) _whyNot.RemoveRange(0, 20);
        }
    }

    /// <summary>KlavaDesk.exe --tetris-play [секунд] — живая партия без мозга: старт, игра, итог в консоль.</summary>
    public static int PlayTest(int seconds)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var done = new TaskCompletionSource<string>();
        Finished += r => done.TrySetResult(r);
        var start = StartAsync(null, seconds, 3).GetAwaiter().GetResult();
        Console.WriteLine($"старт: {start}");
        if (start.StartsWith("[НЕ СРАБОТАЛО]"))
        {
            lock (_whyNot) foreach (var w in _whyNot.Distinct().Where(x => !x.Contains("рядов 1") && !x.Contains("рядов 5") && !x.Contains("рядов 7") && !x.Contains("рядов 8"))) Console.WriteLine($"  {w}");
            return 1;
        }
        var result = done.Task.GetAwaiter().GetResult();
        Console.WriteLine($"итог: {result}");
        if (result.StartsWith("поле пропало")) lock (_whyNot) foreach (var w in _whyNot.Distinct().Where(x => !x.Contains("рядов 1") && !x.Contains("рядов 5") && !x.Contains("рядов 7") && !x.Contains("рядов 8"))) Console.WriteLine($"  {w}");
        return result.Contains("линий: 0") || result.StartsWith("поле пропало") ? 1 : 0;
    }

    public static string Stop()
    {
        if (_cts is null) return "Я и не играю.";
        _cts.Cancel();
        return "Останавливаю игру.";
    }

    /// <summary>Начать: найти окно с игрой (или открыть Jstris), развернуть и играть в фоне до seconds.</summary>
    public static async Task<string> StartAsync(string? url, int seconds, int speed)
    {
        if (_cts is not null) return $"Уже играю: {_status}";
        var win = Uia.BrowserWindow();
        Board? board = null;
        if (win != IntPtr.Zero)
        {
            // уже открытая вкладка в маленьком окне: низ стакана за краем окна — поле «не видно» и игра рвалась на 2-й
            // фигуре (26.09). Играем всегда в развёрнутом окне.
            Uia.Focus(win);
            Native.ShowWindow(win, 3);   // SW_MAXIMIZE
            await Task.Delay(900);
            // «сыграй» — это новая партия: в старой вкладке Jstris могла остаться брошенная засыпанная игра. F4 — рестарт
            if (Uia.WindowTitle(win).Contains("Jstris", StringComparison.OrdinalIgnoreCase))
            {
                Hands.GameKey(0x73, true);
                await Task.Delay(60);
                Hands.GameKey(0x73, false);
                await Task.Delay(1000);   // отсчёт перед стартом
            }
            // заставка отсчёта «3-2-1» поверх поля мешает его узнать — несколько попыток за ~4 с
            for (var i = 0; i < 10 && board is null; i++)
            {
                board = Detect(Capture(win, out _), Why);
                if (board is null) await Task.Delay(400);
            }
        }
        if (board is null)
        {
            Process.Start(new ProcessStartInfo(string.IsNullOrWhiteSpace(url) ? DefaultUrl : url.Trim()) { UseShellExecute = true });
            for (var i = 0; i < 30 && board is null; i++)
            {
                await Task.Delay(500);
                win = Uia.BrowserWindow();
                if (win == IntPtr.Zero) continue;
                if (i == 4) { Native.ShowWindow(win, 3); await Task.Delay(800); }   // SW_MAXIMIZE: поле целиком на экране
                board = Detect(Capture(win, out _), Why);
            }
        }
        if (win == IntPtr.Zero || board is null)
        {
            Log.Write($"тетрис: поле не узнано ({Uia.WindowTitle(win)}): {string.Join(" | ", _whyNot.TakeLast(6))}");
            return "[НЕ СРАБОТАЛО] не нашла поле тетриса на экране. Открыла Jstris — если там меню, нажми «Играть» (или скажи другу), и позови меня снова.";
        }
        _whyNot.Clear();
        _cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Clamp(seconds <= 0 ? 150 : seconds, 20, 900)));
        var token = _cts.Token;
        var gameWin = win;
        _status = "начинаю";
        GameMode.Enter("tetris");
        _ = Task.Run(async () =>
        {
            string result;
            try { result = await PlayAsync(gameWin, board, Math.Clamp(speed <= 0 ? 3 : speed, 1, 8), token); }
            catch (Exception ex) when (ex is ExternalException or ArgumentException or InvalidOperationException) { result = $"сломалось: {ex.Message}"; }
            finally { GameMode.Leave("tetris"); }
            _cts?.Dispose();
            _cts = null;
            _status = $"не играю (прошлая игра: {result})";
            Log.Write($"тетрис: {result}");
            Finished?.Invoke(result);
        });
        return $"Играю в тетрис: поле {board.Rows}×10 нашла, клетка {board.Cell} px. Остановить — Esc, переключиться в другое окно или сказать мне «стоп».";
    }

    private static Bitmap Capture(IntPtr win, out Rectangle area)
    {
        Native.GetWindowRect(win, out var r);
        area = Rectangle.Intersect(new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top), SystemInformation.VirtualScreen);
        var bmp = new Bitmap(Math.Max(1, area.Width), Math.Max(1, area.Height), PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(area.Location, Point.Empty, bmp.Size);
        return bmp;
    }

    private static int Lum(Color c) => (c.R * 30 + c.G * 59 + c.B * 11) / 100;

    /// <summary>Поле по рамке: пара длинных серых вертикалей на расстоянии 10 клеток, высота — 20 клеток.</summary>
    public static Board? Detect(Bitmap bmp, Action<string>? why = null)
    {
        using (bmp)
        {
            var w = bmp.Width;
            var h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var stride = data.Stride;
            var px = new byte[stride * h];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);
            bool Gray(int x, int y)
            {
                var i = y * stride + x * 3;
                int b = px[i], g = px[i + 1], r = px[i + 2];
                var l = (r * 30 + g * 59 + b * 11) / 100;
                return l is >= 30 and <= 140 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 40;
            }
            // самая длинная серая вертикаль в каждом столбце
            var runs = new List<(int X, int Top, int Len)>();
            for (var x = 0; x < w; x++)
            {
                int best = 0, bestTop = 0, cur = 0;
                for (var y = 0; y < h; y++)
                {
                    if (Gray(x, y)) { cur++; if (cur > best) { best = cur; bestTop = y - cur + 1; } }
                    else cur = 0;
                }
                if (best >= 200) runs.Add((x, bestTop, best));
            }
            Board? found = null;
            foreach (var a in runs)
                foreach (var b in runs)
                {
                    var d = b.X - a.X;
                    if (d < 120 || d > 900) continue;
                    if (Math.Abs(a.Top - b.Top) > 6 || Math.Abs(a.Len - b.Len) > 12) { if (d is > 200 and < 300) why?.Invoke($"пара {a.X}–{b.X}: разный верх/длина ({a.Top}/{a.Len} и {b.Top}/{b.Len})"); continue; }
                    var cell = d / 10.0;
                    var rows = (int)Math.Round(a.Len / cell);
                    if (cell < 16 || rows is < 19 or > 22 || Math.Abs(a.Len - rows * cell) > cell * 0.35) { why?.Invoke($"пара {a.X}–{b.X}: клетка {cell:F1}, рядов {rows}, длина {a.Len}"); continue; }
                    // низ рамки — сплошная горизонталь во всю ширину поля: не пустой чёрный фон. Серая линия, лежащие на
                    // дне фигуры и тёмная «тень» падающей фигуры — всё годится. Раньше засчитывался только серый: фигуры и
                    // тень на нижнем ряду закрывали 30 % линии, и поле «пропадало» на первых же фигурах — живой тетрис не
                    // играл вовсе (симуляция этого не видит).
                    var bottom = a.Top + a.Len - 1;
                    bool Solid(int x, int y)
                    {
                        var i = y * stride + x * 3;
                        return (px[i + 2] * 30 + px[i + 1] * 59 + px[i] * 11) / 100 >= 20;
                    }
                    var along = 0;
                    for (var x = a.X; x <= b.X; x++)
                        if (Solid(x, Math.Min(h - 1, bottom)) || Solid(x, Math.Min(h - 1, bottom + 1)) || Solid(x, Math.Max(0, bottom - 1))) along++;
                    if (along < d * 0.9) { why?.Invoke($"пара {a.X}–{b.X}: низ рамки на y={bottom} серый только на {along} из {d}"); continue; }
                    // внутри рамки поле тёмное. Проверка — по ЦЕНТРАМ клеток: точки «по долям высоты» (1/10, 3/10…) при
                    // клетке 24 px попадали ровно на серые линии сетки, и в развёрнутом окне поле не находилось (26.09)
                    var inner = new Rectangle(a.X + 1, a.Top, d - 1, (int)Math.Round(rows * cell));
                    var dark = 0;
                    for (var i = 0; i < 40; i++)
                    {
                        var col = i % 8 * 10 / 8;
                        var row = (i / 8) * rows / 5 + rows / 10;
                        var sx = Math.Min(w - 1, inner.Left + (int)((col + 0.5) * cell));
                        var sy = Math.Min(h - 1, inner.Top + (int)((row + 0.5) * cell));
                        var j = sy * stride + sx * 3;
                        if ((px[j + 2] * 30 + px[j + 1] * 59 + px[j] * 11) / 100 < 50) dark++;
                    }
                    // пустое поле — почти все точки тёмные; к концу партии стопка занимает до половины и больше
                    if (dark < 16) { why?.Invoke($"пара {a.X}–{b.X}: внутри тёмных точек {dark} из 40"); continue; }
                    if (found is null || d > found.Rect.Width)
                        found = new Board(inner, (int)Math.Round(cell), rows, new bool[rows, 10]);
                }
            if (found is null) return null;
            FillCells(px, stride, w, h, found);
            return found;
        }
    }

    /// <summary>Клетки поля: занята, если середина яркая (тень падения тусклая и в счёт не идёт).</summary>
    private static void FillCells(byte[] px, int stride, int w, int h, Board b)
    {
        var c = b.Rect.Width / 10.0;
        for (var row = 0; row < b.Rows; row++)
            for (var col = 0; col < 10; col++)
            {
                int sum = 0, n = 0;
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        var x = (int)(b.Rect.Left + (col + 0.5) * c) + dx * (int)(c / 8);
                        var y = (int)(b.Rect.Top + (row + 0.5) * c) + dy * (int)(c / 8);
                        if (x < 0 || y < 0 || x >= w || y >= h) continue;
                        var j = y * stride + x * 3;
                        sum += Math.Max(px[j], Math.Max(px[j + 1], px[j + 2]));
                        n++;
                    }
                b.Filled[row, col] = n > 0 && sum / n > 140;
            }
    }

    /// <summary>
    /// Поле на месте, где его нашли на старте (за партию оно не двигается): жива ли рамка — по её ВЕРХНЕЙ части (4 ряда),
    /// куда фигуры не достают; клетки — заново. Раньше рамка искалась на каждом кадре целиком, и фигура в крайнем
    /// столбце на дне, перекрыв линию рамки, «прятала» поле посреди партии. null — рамки на месте нет (окно сдвинули).
    /// </summary>
    public static Board? Read(Bitmap bmp, Board known)
    {
        using (bmp)
        {
            var w = bmp.Width;
            var h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            var stride = data.Stride;
            var px = new byte[stride * h];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            bmp.UnlockBits(data);
            int left = known.Rect.Left - 1, right = known.Rect.Right, top = known.Rect.Top;
            var span = Math.Min(h - top, known.Cell * 4);
            if (left < 0 || right >= w || top < 0 || span <= 0) return null;
            var gray = 0;
            for (var y = top; y < top + span; y++)
            {
                if (GrayAt(px, stride, left, y)) gray++;
                if (GrayAt(px, stride, right, y)) gray++;
            }
            if (gray < span * 2 * 0.85) return null;
            var b = new Board(known.Rect, known.Cell, known.Rows, new bool[known.Rows, 10]);
            FillCells(px, stride, w, h, b);
            return b;
        }
    }

    private static bool GrayAt(byte[] px, int stride, int x, int y)
    {
        var i = y * stride + x * 3;
        int b = px[i], g = px[i + 1], r = px[i + 2];
        var l = (r * 30 + g * 59 + b * 11) / 100;
        return l is >= 30 and <= 140 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 40;
    }

    /// <summary>Активная фигура: самая верхняя связная группа из 4 клеток в верхних 6 рядах (замеченная чуть позже появления — тоже); тип — по форме поворота 0.</summary>
    private static (char Type, int BoxX, int BoxY, List<(int R, int C)> Cells)? FindPiece(Board b)
    {
        var seen = new bool[b.Rows, 10];
        for (var r0 = 0; r0 < Math.Min(6, b.Rows); r0++)
            for (var c0 = 0; c0 < 10; c0++)
            {
                if (!b.Filled[r0, c0] || seen[r0, c0]) continue;
                var group = new List<(int R, int C)>();
                var q = new Queue<(int R, int C)>();
                q.Enqueue((r0, c0));
                seen[r0, c0] = true;
                while (q.Count > 0 && group.Count <= 4)
                {
                    var (r, c) = q.Dequeue();
                    group.Add((r, c));
                    foreach (var (dr, dc) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nr = r + dr, nc = c + dc;
                        if (nr < 0 || nc < 0 || nr >= b.Rows || nc >= 10 || seen[nr, nc] || !b.Filled[nr, nc]) continue;
                        seen[nr, nc] = true;
                        q.Enqueue((nr, nc));
                    }
                }
                if (group.Count != 4) continue;
                var minR = group.Min(g => g.R);
                var minC = group.Min(g => g.C);
                var norm = group.Select(g => (g.C - minC, g.R - minR)).OrderBy(p => p).ToArray();
                foreach (var (type, states) in Shapes)
                {
                    var s0 = states[0];
                    var sx = s0.Min(p => p.X);
                    var sy = s0.Min(p => p.Y);
                    var ns = s0.Select(p => (p.X - sx, p.Y - sy)).OrderBy(p => p).ToArray();
                    if (ns.SequenceEqual(norm)) return (type, minC - sx, minR - sy, group);
                }
            }
        return null;
    }

    private static async Task<string> PlayAsync(IntPtr win, Board known, int speed, CancellationToken ct)
    {
        var pieces = 0;
        var lines = 0;
        var lastSeen = Stopwatch.StartNew();
        var tap = Math.Max(12, 45 / speed);
        var boardLost = Stopwatch.StartNew();
        var restored = false;
        while (!ct.IsCancellationRequested)
        {
            if (Native.GetForegroundWindow() != win) return $"друг переключился на другое окно — остановилась. Фигур: {pieces}, линий: {lines}";
            if ((Native.GetAsyncKeyState(0x1B) & 0x8000) != 0 || (Native.GetAsyncKeyState(0x08) & 0x8000) != 0) return $"друг забрал управление — остановилась. Фигур: {pieces}, линий: {lines}";
            // поле там же, где нашли на старте; рамки на месте нет (окно сдвинули) — искать заново целиком
            var board = Read(Capture(win, out _), known) ?? Detect(Capture(win, out _), Why);
            if (board is not null) known = board;
            if (board is not null) boardLost.Restart();
            else if (!restored && boardLost.Elapsed > TimeSpan.FromSeconds(1.5))
            {
                // поле пропало из вида (окно уменьшили, страницу прокрутили) — один раз развернуть и поискать снова
                restored = true;
                Native.ShowWindow(win, 3);
                await Task.Delay(700, CancellationToken.None);
                lastSeen.Restart();
                continue;
            }
            var piece = board is null ? null : FindPiece(board);
            if (board is null || piece is null)
            {
                if (lastSeen.Elapsed > TimeSpan.FromSeconds(4))
                    return board is null ? $"поле пропало из вида — остановилась. Фигур: {pieces}, линий: {lines}" : $"игра закончилась. Фигур: {pieces}, линий: {lines}";
                await Task.Delay(40, CancellationToken.None);
                continue;
            }
            lastSeen.Restart();
            var (type, boxX, boxY, cells) = piece.Value;
            var field = (bool[,])board.Filled.Clone();
            foreach (var (r, c) in cells) field[r, c] = false;
            var best = Choose(field, board.Rows, type);
            if (best is null) return $"некуда класть — проиграла. Фигур: {pieces}, линий: {lines}";
            var (rot, targetX, cleared) = best.Value;
            // поворот, сдвиг, сброс
            if (rot == 3) await Tap(0x5A, tap);                         // Z — против часовой
            else for (var i = 0; i < rot; i++) await Tap(0x26, tap);    // ↑ — по часовой
            var dx = targetX - boxX;
            for (var i = 0; i < Math.Abs(dx); i++) await Tap((ushort)(dx < 0 ? 0x25 : 0x27), tap);
            await Tap(0x20, tap);                                           // пробел — сбросить
            pieces++;
            lines += cleared;
            _status = $"играю: фигур {pieces}, линий {lines}";
            await Task.Delay(Math.Max(30, 260 / speed), CancellationToken.None);
        }
        return $"время вышло. Фигур: {pieces}, линий: {lines}";
    }

    private static async Task Tap(ushort vk, int ms)
    {
        Hands.GameKey(vk, true);
        await Task.Delay(ms);
        Hands.GameKey(vk, false);
        await Task.Delay(ms);
    }

    /// <summary>Лучший поворот и столбец коробки фигуры по El-Tetris; и сколько линий уберёт.</summary>
    private static (int Rot, int BoxX, int Cleared)? Choose(bool[,] field, int rows, char type)
    {
        (int, int, int)? best = null;
        var bestScore = double.MinValue;
        var rotations = type == 'O' ? 1 : type is 'I' or 'S' or 'Z' ? 2 : 4;
        for (var rot = 0; rot < rotations; rot++)
        {
            var st = Shapes[type][rot];
            var minX = st.Min(p => p.X);
            var maxX = st.Max(p => p.X);
            for (var bx = -minX; bx + maxX < 10; bx++)
            {
                // бросок сверху: опускаем, пока не упрётся
                var by = -st.Min(p => p.Y);
                if (!Fits(field, rows, st, bx, by)) continue;
                while (Fits(field, rows, st, bx, by + 1)) by++;
                var f = (bool[,])field.Clone();
                foreach (var (x, y) in st) f[by + y, bx + x] = true;
                var landing = rows - (by + st.Average(p => p.Y));
                var cleared = Clear(f, rows);
                var score = -4.500158825082766 * landing + 3.4181268101392694 * cleared - 3.2178882868487753 * RowTransitions(f, rows)
                            - 9.348695305445199 * ColTransitions(f, rows) - 7.899265427351652 * Holes(f, rows) - 3.3855972247263626 * Wells(f, rows);
                if (score > bestScore) { bestScore = score; best = (rot, bx, cleared); }
            }
        }
        return best;
    }

    private static bool Fits(bool[,] f, int rows, (int X, int Y)[] st, int bx, int by)
    {
        foreach (var (x, y) in st)
        {
            int c = bx + x, r = by + y;
            if (c is < 0 or > 9 || r >= rows) return false;
            if (r >= 0 && f[r, c]) return false;
        }
        return true;
    }

    private static int Clear(bool[,] f, int rows)
    {
        var cleared = 0;
        for (var r = rows - 1; r >= 0; r--)
        {
            var full = true;
            for (var c = 0; c < 10 && full; c++) full &= f[r, c];
            if (!full) continue;
            cleared++;
            for (var rr = r; rr > 0; rr--)
                for (var c = 0; c < 10; c++) f[rr, c] = f[rr - 1, c];
            for (var c = 0; c < 10; c++) f[0, c] = false;
            r++;
        }
        return cleared;
    }

    private static int RowTransitions(bool[,] f, int rows)
    {
        var t = 0;
        for (var r = 0; r < rows; r++)
        {
            var prev = true;
            for (var c = 0; c < 10; c++) { if (f[r, c] != prev) t++; prev = f[r, c]; }
            if (!prev) t++;
        }
        return t;
    }

    private static int ColTransitions(bool[,] f, int rows)
    {
        var t = 0;
        for (var c = 0; c < 10; c++)
        {
            var prev = false;
            for (var r = 0; r < rows; r++) { if (f[r, c] != prev) t++; prev = f[r, c]; }
            if (!prev) t++;
        }
        return t;
    }

    private static int Holes(bool[,] f, int rows)
    {
        var holes = 0;
        for (var c = 0; c < 10; c++)
        {
            var roof = false;
            for (var r = 0; r < rows; r++) { if (f[r, c]) roof = true; else if (roof) holes++; }
        }
        return holes;
    }

    private static int Wells(bool[,] f, int rows)
    {
        var sum = 0;
        for (var c = 0; c < 10; c++)
        {
            var depth = 0;
            for (var r = 0; r < rows; r++)
            {
                var left = c == 0 || f[r, c - 1];
                var right = c == 9 || f[r, c + 1];
                if (!f[r, c] && left && right) { depth++; sum += depth; }
                else depth = 0;
            }
        }
        return sum;
    }

    /// <summary>Для проверки: найденное поле текстом (# — занято).</summary>
    public static string Dump(Board b)
    {
        var sb = new StringBuilder($"поле {b.Rect} клетка {b.Cell} рядов {b.Rows}\n");
        for (var r = 0; r < b.Rows; r++)
        {
            for (var c = 0; c < 10; c++) sb.Append(b.Filled[r, c] ? '#' : '.');
            sb.Append('\n');
        }
        var p = FindPiece(b);
        sb.Append(p is null ? "фигуры не вижу" : $"фигура {p.Value.Type}, коробка x={p.Value.BoxX} y={p.Value.BoxY}");
        return sb.ToString();
    }

    /// <summary>
    /// KlavaDesk.exe --tetris-sim — партия без экрана: фигуры случайным мешком по 7, появляются в повороте 0 в
    /// коробке x=3 (как в SRS), автопилот выбирает место, ходы применяются так же, как клавиши (повороты, сдвиги,
    /// сброс). Проверка мозгов автопилота: сколько линий уберёт за 300 фигур и не проиграет ли.
    /// </summary>
    public static int SimTest()
    {
        Console.OutputEncoding = Encoding.UTF8;
        const int rows = 20;
        var field = new bool[rows, 10];
        var rnd = new Random(7);
        var bag = new List<char>();
        int pieces = 0, lines = 0;
        for (; pieces < 300; pieces++)
        {
            if (bag.Count == 0) bag.AddRange("IOTSZJL".OrderBy(_ => rnd.Next()));
            var type = bag[0];
            bag.RemoveAt(0);
            var st0 = Shapes[type][0];
            if (!Fits(field, rows, st0, 3, 0)) { Console.WriteLine($"не влезла новая фигура — проигрыш на {pieces}-й"); break; }
            var best = Choose(field, rows, type);
            if (best is null) { Console.WriteLine($"некуда класть на {pieces}-й"); break; }
            var (rot, bx, _) = best.Value;
            // как клавиши: сначала поворот на месте появления, потом сдвиг, потом сброс
            var st = Shapes[type][rot];
            var by = 0;
            if (!Fits(field, rows, st, 3, by)) { Console.WriteLine($"поворот не влез на {pieces}-й"); break; }
            var x = 3;
            while (x != bx)
            {
                var nx = x + Math.Sign(bx - x);
                if (!Fits(field, rows, st, nx, by)) break;
                x = nx;
            }
            while (Fits(field, rows, st, x, by + 1)) by++;
            foreach (var (px, py) in st) field[by + py, x + px] = true;
            lines += Clear(field, rows);
        }
        var height = 0;
        for (var r = 0; r < rows; r++) { var any = false; for (var c = 0; c < 10; c++) any |= field[r, c]; if (any) { height = rows - r; break; } }
        Console.WriteLine($"фигур {pieces}, линий {lines}, высота стопки в конце {height}");
        var ok = pieces == 300 && lines >= 100;
        Console.WriteLine(ok ? "tetris-sim: OK" : "tetris-sim: плохо");
        return ok ? 0 : 1;
    }

    /// <summary>KlavaDesk.exe --tetris-look [снимок.png] — найти поле (в окне браузера или на снимке) и показать, что видит автопилот.</summary>
    public static int LookTest(string? file)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Board? b;
        if (!string.IsNullOrEmpty(file))
        {
            using var img = new Bitmap(file);
            b = Detect(img.Clone(new Rectangle(0, 0, img.Width, img.Height), PixelFormat.Format24bppRgb), s => Console.WriteLine("  " + s));
            Console.WriteLine($"снимок {file}");
        }
        else
        {
            var win = Uia.BrowserWindow();
            if (win == IntPtr.Zero) { Console.WriteLine("браузера нет"); return 1; }
            b = Detect(Capture(win, out var area), s => Console.WriteLine("  " + s));
            Console.WriteLine($"окно {area}");
        }
        Console.WriteLine(b is null ? "поле не найдено" : Dump(b));
        return b is null ? 1 : 0;
    }
}
