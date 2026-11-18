using System.Drawing.Imaging;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Media.Ocr;

namespace KlavaDesk;

/// <summary>
/// Отложенные дела Клавы: «когда … — сделай …». Раньше она обещала «как лимит сбросится — нажму Enter» и не могла:
/// сама по себе она действует только на реплику друга, а ждать и следить было нечем. Теперь задание живёт в фоне:
///  - когда: во столько-то (at «02:00»), через N минут, когда в окне ПРОПАДЁТ надпись (text_gone «limit») или
///    ПОЯВИТСЯ (text_appears) — текст окна берётся через доступность Windows, а если окно его не отдаёт — OCR снимка;
///  - что: поднять окно, нажать кнопку по имени, напечатать текст, нажать клавиши — строго в это окно: клавиши
///    уходят, только если оно действительно впереди; если друг в этот миг печатает — ждёт паузы в его вводе.
/// Задания лежат в data\later.json и переживают перезапуск. Итог приходит Клаве репликой (Fired).
/// </summary>
internal static class Later
{
    internal sealed record Job(string Id, string Window, DateTime? At, string? TextGone, string? TextAppears,
        string? Click, string? Type, string? Keys, string? Say, DateTime Created, DateTime Until);

    private static readonly List<Job> Jobs = [];
    private static readonly object Gate = new();
    private static string? _file;
    private static CancellationTokenSource? _loop;
    private static readonly Hands Paws = new(null);   // только SendInput: без сеанса, хуков и лапки у курсора

    /// <summary>Задание выполнено или брошено — реплика для Клавы.</summary>
    public static event Action<string>? Fired;

    public static void Start(string file)
    {
        _file = file;
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(file)) is { } saved)
                lock (Gate) Jobs.AddRange(saved.Where(j => j.Until > DateTime.Now));
        }
        catch (Exception ex) when (ex is JsonException or IOException) { Log.Write($"отложенное: {file} не прочитался — {ex.Message}"); }
        _loop = new CancellationTokenSource();
        var token = _loop.Token;
        _ = Task.Run(() => LoopAsync(token));
        lock (Gate) if (Jobs.Count > 0) Log.Write($"отложенное: ждут {Jobs.Count} дел");
    }

    public static void Stop() => _loop?.Cancel();

    private static void Save()
    {
        if (_file is null) return;
        try
        {
            List<Job> copy;
            lock (Gate) copy = [.. Jobs];
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException ex) { Log.Write($"отложенное: не сохранилось — {ex.Message}"); }
    }

    /// <summary>Время «02:00» / «2:30» / «через 40 минут» → момент (сегодня, а если уже прошло — завтра).</summary>
    public static DateTime? ParseAt(string? at, double afterMinutes)
    {
        if (afterMinutes > 0) return DateTime.Now.AddMinutes(afterMinutes);
        if (string.IsNullOrWhiteSpace(at)) return null;
        var m = Regex.Match(at, @"(\d{1,2})[:.\s](\d{2})");
        if (!m.Success) m = Regex.Match(at, @"^\s*(\d{1,2})\s*(?:ч|час|am|pm)?\s*$", RegexOptions.IgnoreCase);
        if (!m.Success) return DateTime.TryParse(at, CultureInfo.GetCultureInfo("ru-RU"), DateTimeStyles.AssumeLocal, out var dt) ? dt : null;
        var h = int.Parse(m.Groups[1].Value);
        var min = m.Groups.Count > 2 && m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        if (Regex.IsMatch(at, "pm", RegexOptions.IgnoreCase) && h < 12) h += 12;
        if (h > 23 || min > 59) return null;
        var t = DateTime.Today.AddHours(h).AddMinutes(min);
        return t <= DateTime.Now ? t.AddDays(1) : t;
    }

    public static string Add(Job j)
    {
        lock (Gate) Jobs.Add(j);
        Save();
        Log.Write($"отложенное: {Describe(j)}");
        return $"Запомнила (дело {j.Id}): {Describe(j)}. Сделаю сама, когда придёт время — и скажу.";
    }

    public static string List()
    {
        lock (Gate)
            return Jobs.Count == 0 ? "Отложенных дел нет." : "Отложенные дела:\n" + string.Join("\n", Jobs.Select(j => $"• {j.Id}: {Describe(j)}"));
    }

    public static string Cancel(string id)
    {
        int n;
        lock (Gate) n = string.IsNullOrWhiteSpace(id) || id is "all" or "все" ? Jobs.RemoveAll(_ => true) : Jobs.RemoveAll(j => j.Id.Equals(id.Trim(), StringComparison.OrdinalIgnoreCase));
        Save();
        return n == 0 ? $"Дела «{id}» нет." : $"Отменила дел: {n}.";
    }

    private static string Describe(Job j)
    {
        var when = new List<string>();
        if (j.At is { } at) when.Add($"в {at:HH:mm}{(at.Date != DateTime.Today ? $" ({at:dd.MM})" : "")}");
        if (j.TextGone is { Length: > 0 }) when.Add($"когда в окне «{j.Window}» пропадёт «{j.TextGone}»");
        if (j.TextAppears is { Length: > 0 }) when.Add($"когда в окне «{j.Window}» появится «{j.TextAppears}»");
        var what = new List<string>();
        if (j.Click is { Length: > 0 }) what.Add($"нажать кнопку «{j.Click}»");
        if (j.Type is { Length: > 0 }) what.Add($"напечатать «{(j.Type.Length > 40 ? j.Type[..40] + "…" : j.Type)}»");
        if (j.Keys is { Length: > 0 }) what.Add($"клавиши {j.Keys}");
        if (what.Count == 0) what.Add("напомнить");
        return $"{string.Join(" и ", when)} → в окне «{j.Window}» {string.Join(", ", what)}";
    }

    private static async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (OperationCanceledException) { return; }
            List<Job> due;
            lock (Gate) due = [.. Jobs];
            foreach (var j in due)
            {
                try
                {
                    if (DateTime.Now > j.Until)
                    {
                        Remove(j);
                        Fired?.Invoke($"Отложенное дело {j.Id} ({Describe(j)}) так и не дождалось условия — бросила.");
                        continue;
                    }
                    if (!await ReadyAsync(j)) continue;
                    var result = await DoAsync(j, ct);
                    if (result is null) continue;   // окно не вышло вперёд / друг печатает — попробуем через 15 с
                    Remove(j);
                    Fired?.Invoke(result);
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Runtime.InteropServices.COMException or ArgumentException)
                {
                    Log.Write($"отложенное {j.Id}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    private static void Remove(Job j)
    {
        lock (Gate) Jobs.Remove(j);
        Save();
    }

    /// <summary>Условие выполнено: время пришло, нужная надпись пропала/появилась (все заданные условия сразу).</summary>
    private static async Task<bool> ReadyAsync(Job j)
    {
        if (j.At is { } at && DateTime.Now < at) return false;
        if (j.TextGone is not { Length: > 0 } && j.TextAppears is not { Length: > 0 }) return true;
        var win = Uia.FindWindow(j.Window);
        if (win == IntPtr.Zero) return false;
        var text = await WindowTextAsync(win);
        if (text is null) return false;   // окно не прочиталось — не гадаем
        var gone = j.TextGone is not { Length: > 0 } || !text.Contains(j.TextGone, StringComparison.OrdinalIgnoreCase);
        var appears = j.TextAppears is not { Length: > 0 } || text.Contains(j.TextAppears, StringComparison.OrdinalIgnoreCase);
        return gone && appears;
    }

    /// <summary>Текст окна: доступность Windows, пусто — OCR снимка окна (Electron-окна часто не отдают дерево).</summary>
    private static async Task<string?> WindowTextAsync(IntPtr win)
    {
        var text = await Uia.WindowTextAsync(win, 30000, TimeSpan.FromSeconds(8));
        if (!string.IsNullOrWhiteSpace(text) && text.Length > 40) return text;
        return await OcrWindowAsync(win);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);

    private static async Task<string?> OcrWindowAsync(IntPtr win)
    {
        if (!Native.GetWindowRect(win, out var r) || r.Right - r.Left < 50 || r.Bottom - r.Top < 50) return null;
        using var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            var hdc = g.GetHdc();
            PrintWindow(win, hdc, 2);
            g.ReleaseHdc(hdc);
        }
        var engine = OcrEngine.TryCreateFromUserProfileLanguages() ?? OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        if (engine is null) return null;
        using var soft = await Eyes.ToSoftwareBitmapAsync(bmp);
        var res = await engine.RecognizeAsync(soft);
        var text = string.Join("\n", res.Lines.Select(l => l.Text));
        return text.Length > 0 ? text : null;
    }

    /// <summary>KlavaDesk.exe --later-test окно [надпись] — что Клава видит в окне (доступность и OCR), есть ли надпись.</summary>
    public static int Test(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var win = Uia.FindWindow(args.Length > 0 ? args[0] : "Claude");
        if (win == IntPtr.Zero) { Console.WriteLine("окна нет"); return 1; }
        Console.WriteLine($"окно «{Uia.WindowTitle(win)}» ({Uia.ProcessName(win)})");
        var uia = Task.Run(() => Uia.WindowTextAsync(win, 30000, TimeSpan.FromSeconds(8))).GetAwaiter().GetResult() ?? "";
        Console.WriteLine($"доступность: {uia.Length} симв. {(uia.Length > 0 ? "…" + uia[^Math.Min(300, uia.Length)..].Replace('\n', ' ') : "")}");
        var ocr = Task.Run(() => OcrWindowAsync(win)).GetAwaiter().GetResult() ?? "";
        Console.WriteLine($"OCR: {ocr.Length} симв. {(ocr.Length > 0 ? "…" + ocr[^Math.Min(300, ocr.Length)..].Replace('\n', ' ') : "")}");
        if (args.Length > 1) Console.WriteLine($"надпись «{args[1]}»: доступность {(uia.Contains(args[1], StringComparison.OrdinalIgnoreCase) ? "есть" : "нет")}, OCR {(ocr.Contains(args[1], StringComparison.OrdinalIgnoreCase) ? "есть" : "нет")}");
        return 0;
    }

    /// <summary>Сделать дело; null — сейчас нельзя (окно не впереди, друг печатает), попробовать позже.</summary>
    private static async Task<string?> DoAsync(Job j, CancellationToken ct)
    {
        var nothing = j.Click is not { Length: > 0 } && j.Type is not { Length: > 0 } && j.Keys is not { Length: > 0 };
        if (nothing) return $"(Пришло время отложенного дела {j.Id}: {Describe(j)}. Напомни другу.)";
        var win = Uia.FindWindow(j.Window);
        if (win == IntPtr.Zero) return null;
        // друг сам сейчас печатает или водит мышью — не вырывать у него клавиатуру посреди слова
        if (Native.UserIdle() < TimeSpan.FromSeconds(4)) return null;
        for (var i = 0; i < 3 && Native.GetForegroundWindow() != win; i++)
        {
            Uia.Focus(win);
            await Task.Delay(400, ct);
        }
        if (Native.GetForegroundWindow() != win) { Log.Write($"отложенное {j.Id}: окно «{Uia.WindowTitle(win)}» не вышло вперёд"); return null; }
        var done = new List<string>();
        if (j.Click is { Length: > 0 })
        {
            var el = await Uia.FindElementAsync(win, j.Click, TimeSpan.FromSeconds(6));
            if (el is not null && await Uia.InvokeAsync(el, TimeSpan.FromSeconds(3))) done.Add($"нажала «{el.Name}»");
            else done.Add($"кнопку «{j.Click}» не нашла");
        }
        if (j.Type is { Length: > 0 })
        {
            if (Native.GetForegroundWindow() != win) return null;
            await Paws.TypeAsync(j.Type, ct);
            done.Add("напечатала текст");
        }
        if (j.Keys is { Length: > 0 } && Hands.ParseCombo(j.Keys) is { } keys)
        {
            if (Native.GetForegroundWindow() != win) return null;
            await Paws.PressAsync(keys, ct);
            done.Add($"нажала {j.Keys}");
        }
        Log.Write($"отложенное {j.Id}: {string.Join(", ", done)} в «{Uia.WindowTitle(win)}»");
        return $"(Ты сделала отложенное дело {j.Id}: {Describe(j)} — {string.Join(", ", done)}." +
               (j.Say is { Length: > 0 } ? $" Скажи другу: {j.Say}" : " Коротко скажи другу, что сделала.") + ")";
    }
}
