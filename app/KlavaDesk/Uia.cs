using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace KlavaDesk;

/// <summary>
/// Руки без пикселей: Windows UI Automation. Кнопку, пункт меню, вкладку, ссылку можно найти по имени и нажать
/// через InvokePattern — без мыши и без угадывания координат по картинке (главная причина «мышкой не попадает»).
/// Если элемент не умеет Invoke — отдаём его прямоугольник, и лапки кликают точно в центр.
/// Тут же — адрес активной вкладки браузера (адресная строка Chrome/Edge/Яндекса/Firefox читается через UIA).
/// Всё с таймаутом: у некоторых программ дерево UIA отвечает секундами.
/// </summary>
internal static class Uia
{
    // имена процессов настоящих браузеров — точным совпадением: msedgewebview2 (окно самой Клавы, Claude, Teams) — не браузер
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "chrome", "msedge", "firefox", "browser", "opera", "opera_gx", "brave", "vivaldi", "yandex", "arc", "thorium", "chromium", "floorp", "librewolf", "waterfox", "zen" };

    /// <summary>Окно верхнего уровня по кусочку заголовка (без регистра) или активное, если пусто.</summary>
    public static IntPtr FindWindow(string? titlePart)
    {
        if (string.IsNullOrWhiteSpace(titlePart)) return Native.GetForegroundWindow();
        var needle = titlePart.Trim();
        IntPtr best = IntPtr.Zero;
        var bestScore = int.MinValue;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            if (Native.GetWindowTextW(h, sb, sb.Capacity) <= 0) return true;
            var title = sb.ToString();
            var proc = ProcessName(h);
            var inTitle = title.Contains(needle, StringComparison.OrdinalIgnoreCase);
            var inProc = proc.Contains(needle, StringComparison.OrdinalIgnoreCase);
            if (!inTitle && !inProc) return true;
            var score = (title.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ? 3 : 0) + (inTitle ? 2 : 0) + (inProc ? 1 : 0) - title.Length / 60;
            if (Native.IsIconic(h)) score -= 1;
            if (score > bestScore) { bestScore = score; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static string WindowTitle(IntPtr h)
    {
        var sb = new StringBuilder(512);
        Native.GetWindowTextW(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string ProcessName(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out var pid);
        try { using var p = Process.GetProcessById((int)pid); return p.ProcessName; }
        catch (ArgumentException) { return ""; }
        catch (InvalidOperationException) { return ""; }
    }

    /// <summary>Видимые окна с заголовками — чтобы мозг знал, что вообще открыто.</summary>
    public static List<(IntPtr Handle, string Title, string Process)> ListWindows(int max = 40)
    {
        var list = new List<(IntPtr, string, string)>();
        var self = Environment.ProcessId;
        Native.EnumWindows((h, _) =>
        {
            if (list.Count >= max || !Native.IsWindowVisible(h)) return true;
            var t = WindowTitle(h);
            if (t.Length == 0) return true;
            Native.GetWindowThreadProcessId(h, out var pid);
            if (pid == self) return true;
            var pn = ProcessName(h);
            if (pn is "TextInputHost" or "ApplicationFrameHost" && t is "Microsoft Text Input Application" or "") return true;
            list.Add((h, t, pn));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Поднять окно на передний план (развернуть, если свёрнуто).</summary>
    public static bool Focus(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
        // уже впереди — ничего не нажимать: лишний Alt включал в Paint подсказки ленты, и Ctrl+V после него пропадал
        if (Native.GetForegroundWindow() == h) return true;
        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
        // Windows не отдаёт фокус чужому окну просто так: снимает запрет любое наше нажатие. Раньше жали Alt — он
        // открывал меню в окне, которое было впереди. F24 есть в системе, но ни одна программа на неё не реагирует.
        const ushort VkF24 = 0x87;
        var key = new[]
        {
            new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = VkF24 } } },
            new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = VkF24, dwFlags = Native.KEYEVENTF_KEYUP } } },
        };
        Native.SendInput(2, key, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
        return Native.SetForegroundWindow(h);
    }

    /// <summary>Найденный элемент: что это, где он на экране и умеет ли нажиматься без мыши.</summary>
    internal sealed record Found(string Name, string Type, Rectangle Bounds, bool CanInvoke, AutomationElement Element);

    private static readonly ControlType[] Clickable =
    [
        ControlType.Button, ControlType.MenuItem, ControlType.Hyperlink, ControlType.TabItem, ControlType.ListItem,
        ControlType.TreeItem, ControlType.CheckBox, ControlType.RadioButton, ControlType.SplitButton, ControlType.ComboBox,
        ControlType.Edit, ControlType.Document, ControlType.Text, ControlType.Image, ControlType.DataItem, ControlType.Custom,
    ];

    /// <summary>
    /// Найти элемент по имени внутри окна: сначала точное совпадение, потом «начинается с», потом «содержит».
    /// Среди равных — кликабельные типы и видимые на экране.
    /// </summary>
    public static Task<Found?> FindElementAsync(IntPtr window, string name, TimeSpan timeout)
    {
        if (window == IntPtr.Zero || string.IsNullOrWhiteSpace(name)) return Task.FromResult<Found?>(null);
        return RunAsync<Found?>(() =>
        {
            var root = AutomationElement.FromHandle(window);
            var needle = Norm(name);
            Found? best = null;
            var bestScore = int.MinValue;
            var walker = TreeWalker.ControlViewWalker;
            var stack = new Stack<(AutomationElement El, int Depth)>();
            stack.Push((root, 0));
            var visited = 0;
            while (stack.Count > 0 && visited < 4000)
            {
                var (el, depth) = stack.Pop();
                visited++;
                string elName;
                ControlType type;
                Rectangle rect;
                bool offscreen;
                try
                {
                    var cur = el.Current;
                    elName = cur.Name ?? "";
                    type = cur.ControlType;
                    offscreen = cur.IsOffscreen;
                    var r = cur.BoundingRectangle;
                    rect = r.IsEmpty || double.IsInfinity(r.Width) ? Rectangle.Empty : new Rectangle((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
                }
                catch (ElementNotAvailableException) { continue; }
                catch (InvalidOperationException) { continue; }
                var n = Norm(elName);
                if (n.Length > 0 && !offscreen && rect.Width > 0)
                {
                    var match = n == needle ? 3 : n.StartsWith(needle, StringComparison.Ordinal) ? 2 : n.Contains(needle, StringComparison.Ordinal) ? 1 : 0;
                    if (match > 0)
                    {
                        var clickable = Array.IndexOf(Clickable, type) is var ix and >= 0 && ix < 10;
                        var score = match * 100 + (clickable ? 30 : 0) - Math.Abs(n.Length - needle.Length) - depth;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            var canInvoke = el.TryGetCurrentPattern(InvokePattern.Pattern, out _)
                                            || el.TryGetCurrentPattern(TogglePattern.Pattern, out _)
                                            || el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _)
                                            || el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _);
                            best = new Found(elName, type.ProgrammaticName.Replace("ControlType.", ""), rect, canInvoke, el);
                        }
                    }
                }
                if (depth > 40) continue;
                try
                {
                    for (var c = walker.GetLastChild(el); c is not null; c = walker.GetPreviousSibling(c)) stack.Push((c, depth + 1));
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
            }
            return best;
        }, timeout);
    }

    /// <summary>Нажать элемент без мыши: Invoke / Toggle / Select / Expand. false — не умеет (тогда кликают лапки).</summary>
    public static Task<bool> InvokeAsync(Found f, TimeSpan timeout) => RunAsync(() =>
    {
        var el = f.Element;
        try
        {
            if (el.TryGetCurrentPattern(InvokePattern.Pattern, out var ip)) { ((InvokePattern)ip).Invoke(); return true; }
            if (el.TryGetCurrentPattern(TogglePattern.Pattern, out var tp)) { ((TogglePattern)tp).Toggle(); return true; }
            if (el.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) { ((SelectionItemPattern)sp).Select(); return true; }
            if (el.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep))
            {
                var e = (ExpandCollapsePattern)ep;
                if (e.Current.ExpandCollapseState == ExpandCollapseState.Expanded) e.Collapse(); else e.Expand();
                return true;
            }
            // группа с подписью, а кнопка внутри без имени (в Paint «Слои» — так): жмём её единственную кнопку
            if (el.Current.ControlType == ControlType.Group)
            {
                var inner = el.FindAll(TreeScope.Descendants, new OrCondition(
                    new PropertyCondition(AutomationElement.IsInvokePatternAvailableProperty, true),
                    new PropertyCondition(AutomationElement.IsTogglePatternAvailableProperty, true)));
                if (inner.Count == 1)
                {
                    var b = inner[0];
                    if (b.TryGetCurrentPattern(InvokePattern.Pattern, out var bi)) ((InvokePattern)bi).Invoke();
                    else ((TogglePattern)b.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
                    return true;
                }
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        return false;
    }, timeout);

    /// <summary>Вписать текст в поле (ValuePattern) — надёжнее печати, если поле это умеет.</summary>
    public static Task<bool> SetValueAsync(Found f, string text, TimeSpan timeout) => RunAsync(() =>
    {
        try
        {
            if (f.Element.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) && !((ValuePattern)vp).Current.IsReadOnly)
            {
                ((ValuePattern)vp).SetValue(text);
                return true;
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        catch (ArgumentException) { }
        return false;
    }, timeout);

    /// <summary>
    /// Кнопки, пункты, ссылки, поля активного окна — текстом с координатами центра на экране. Это «карта» окна для
    /// мозга: по ней он зовёт click_element по имени, а не гадает пиксели по снимку.
    /// </summary>
    /// <param name="map">Перевод экранной точки в координаты снимка (для look_screen); null — экранные.</param>
    public static async Task<string> DescribeAsync(IntPtr window, int max, TimeSpan timeout, Func<Point, Point>? map = null)
    {
        if (window == IntPtr.Zero) return "Окно не найдено.";
        return await RunAsync<string?>(() =>
        {
            var root = AutomationElement.FromHandle(window);
            var sb = new StringBuilder();
            var walker = TreeWalker.ControlViewWalker;
            var stack = new Stack<(AutomationElement El, int Depth)>();
            stack.Push((root, 0));
            var n = 0;
            var visited = 0;
            var seen = new HashSet<string>();
            while (stack.Count > 0 && n < max && visited < 5000)
            {
                var (el, depth) = stack.Pop();
                visited++;
                try
                {
                    var cur = el.Current;
                    var type = cur.ControlType;
                    var ix = Array.IndexOf(Clickable, type);
                    if (ix is >= 0 and < 10 && !cur.IsOffscreen && !string.IsNullOrWhiteSpace(cur.Name))
                    {
                        var r = cur.BoundingRectangle;
                        if (!r.IsEmpty && r.Width > 0)
                        {
                            var key = $"{type.Id}|{cur.Name}|{(int)r.X}|{(int)r.Y}";
                            if (seen.Add(key))
                            {
                                var nm = cur.Name.Length > 70 ? cur.Name[..70] + "…" : cur.Name;
                                var center = new Point((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                                if (map is not null) center = map(center);
                                sb.Append(type.ProgrammaticName.Replace("ControlType.", "")).Append(" «").Append(nm.Replace('\n', ' ')).Append("» @")
                                  .Append(center.X).Append(',').Append(center.Y).Append('\n');
                                n++;
                            }
                        }
                    }
                    if (depth > 40) continue;
                    for (var c = walker.GetLastChild(el); c is not null; c = walker.GetPreviousSibling(c)) stack.Push((c, depth + 1));
                }
                catch (ElementNotAvailableException) { }
                catch (InvalidOperationException) { }
            }
            return sb.Length == 0 ? "В окне не нашлось именованных кнопок/ссылок (программа не отдаёт UI Automation) — работай по снимку экрана." : sb.ToString();
        }, timeout) ?? "UI Automation не ответил вовремя — работай по снимку экрана.";
    }

    /// <summary>Окно браузера: активное, если это браузер, иначе самое верхнее окно браузера.</summary>
    public static IntPtr BrowserWindow()
    {
        var fg = Native.GetForegroundWindow();
        if (IsBrowser(fg)) return fg;
        IntPtr found = IntPtr.Zero;
        Native.EnumWindows((h, _) =>
        {
            if (!Native.IsWindowVisible(h) || WindowTitle(h).Length == 0) return true;
            if (IsBrowser(h)) { found = h; return false; }   // EnumWindows идёт сверху вниз по Z — первое и есть верхнее
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static bool IsBrowser(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(h, out var pid);
        if (pid == Environment.ProcessId) return false;
        return Browsers.Contains(ProcessName(h));
    }

    /// <summary>
    /// Локальное окно Зумы (Zuma Deluxe и любые포гАп-клоны): сначала проверяем процессы с «zuma» в имени
    /// (Zuma.exe, ZumaDeluxe.exe, popcap…), потом окна с «Zuma» в заголовке.
    /// Браузер намеренно пропускаем — автопилот работает с локальной игрой, а не с веб-версией.
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenDesktop(string lpszDesktop, uint dwFlags, bool fInherit, uint dwDesiredAccess);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetThreadDesktop(IntPtr hDesktop);

    public static void EnsureDefaultDesktop()
    {
        try
        {
            var hDesk = OpenDesktop("Default", 0, false, 0x01FF);
            if (hDesk != IntPtr.Zero) SetThreadDesktop(hDesk);
        }
        catch { }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr hDesktop);

    public static IntPtr ZumaWindow()
    {
        // 1. Прямой поиск по заголовку
        foreach (var name in new[] { "Zuma Deluxe 1.0", "Zuma Deluxe", "Zuma" })
        {
            var direct = FindWindow(null, name);
            if (direct != IntPtr.Zero && IsLocalZuma(direct)) return direct;
        }

        // 2. Активное окно
        var fg = Native.GetForegroundWindow();
        if (IsLocalZuma(fg)) return fg;

        // 3. Поиск через поток, подключённый к рабочему столу Default
        IntPtr found = IntPtr.Zero;
        var t = new Thread(() =>
        {
            try
            {
                var hDesk = OpenDesktop("Default", 0, false, 0x01FF);
                if (hDesk != IntPtr.Zero)
                {
                    SetThreadDesktop(hDesk);
                    foreach (var name in new[] { "Zuma Deluxe 1.0", "Zuma Deluxe", "Zuma" })
                    {
                        var direct = FindWindow(null, name);
                        if (direct != IntPtr.Zero && IsLocalZuma(direct)) { found = direct; break; }
                    }
                    if (found == IntPtr.Zero)
                    {
                        Native.EnumWindows((h, _) =>
                        {
                            if (WindowTitle(h).Length == 0) return true;
                            if (IsLocalZuma(h)) { found = h; return false; }
                            return true;
                        }, IntPtr.Zero);
                    }
                    CloseDesktop(hDesk);
                }
            }
            catch { }
        });
        t.Start();
        t.Join(1000);
        if (found != IntPtr.Zero) return found;

        // 4. Обычный EnumWindows на текущем десктопе
        Native.EnumWindows((h, _) =>
        {
            if (WindowTitle(h).Length == 0) return true;
            if (IsLocalZuma(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Это окно локальной Зумы (не браузер).</summary>
    private static bool IsLocalZuma(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(h, out var pid);
        if ((uint)pid == (uint)Environment.ProcessId) return false;
        // процессы браузеров — пропускаем: нам нужна именно нативная игра
        var proc = ProcessName(h);
        if (Browsers.Contains(proc)) return false;
        // нативный процесс с «zuma» в имени
        if (proc.Contains("zuma", StringComparison.OrdinalIgnoreCase)) return true;
        // popcap-процессы и exe рядом
        if (proc.Contains("popcap", StringComparison.OrdinalIgnoreCase)) return true;
        // заголовок окна содержит Zuma
        var title = WindowTitle(h);
        return title.Contains("Zuma", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Зума", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Адрес и заголовок активной вкладки браузера (адресная строка — первое поле ввода в окне).</summary>
    public static async Task<(string? Url, string Title, string Browser)> BrowserTabAsync(TimeSpan timeout)
    {
        var h = BrowserWindow();
        if (h == IntPtr.Zero) return (null, "", "");
        var title = WindowTitle(h);
        var browser = ProcessName(h);
        var url = await RunAsync<string?>(() =>
        {
            var root = AutomationElement.FromHandle(h);
            // Firefox: urlbar-input; Chromium-браузеры: первое Edit с ValuePattern в тулбаре
            var ff = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "urlbar-input"));
            var edit = ff ?? root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
            if (edit is null) return null;
            if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var vp))
            {
                var v = ((ValuePattern)vp).Current.Value?.Trim();
                return string.IsNullOrEmpty(v) ? null : v;
            }
            return null;
        }, timeout);
        if (url is not null && !url.Contains("://", StringComparison.Ordinal) && url.Contains('.') && !url.Contains(' ')) url = "https://" + url;
        // хвост «— Google Chrome» в заголовке — не часть страницы
        foreach (var sep in new[] { " — ", " - " })
        {
            var i = title.LastIndexOf(sep, StringComparison.Ordinal);
            if (i > 0 && title.Length - i < 40) { title = title[..i]; break; }
        }
        return (url, title, browser);
    }

    /// <summary>
    /// Текст открытой страницы прямо из браузера (документ веб-страницы через UI Automation, TextPattern): то, что
    /// сейчас нарисовано, — с результатом Google Переводчика, погодой, чатами, всем, что страница дорисовала скриптами
    /// (повторное скачивание адреса этого не видит). Chromium включает доступность по первому запросу — вторая попытка.
    /// </summary>
    public static async Task<string?> BrowserDocumentTextAsync(int maxChars, TimeSpan timeout)
    {
        var h = BrowserWindow();
        if (h == IntPtr.Zero) return null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var text = await RunAsync<string?>(() =>
            {
                var root = AutomationElement.FromHandle(h);
                var docs = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                string? best = null;
                foreach (AutomationElement d in docs)
                {
                    if (d.Current.IsOffscreen || !d.TryGetCurrentPattern(TextPattern.Pattern, out var tp)) continue;
                    var t = ((TextPattern)tp).DocumentRange.GetText(maxChars);
                    if (t.Length > (best?.Length ?? 0)) best = t;
                }
                return best;
            }, timeout);
            if (!string.IsNullOrWhiteSpace(text)) return text;
            await Task.Delay(700);
        }
        return null;
    }

    /// <summary>
    /// Текст любого окна через UI Automation — чат другой нейронки (Claude, ChatGPT — это Chromium: документ с
    /// TextPattern), мессенджер, поля и надписи обычных программ. Берётся самый большой документ; если документа нет —
    /// значения полей ввода и надписи по порядку дерева. Chromium включает доступность по первому запросу — вторая попытка.
    /// </summary>
    public static async Task<string?> WindowTextAsync(IntPtr window, int maxChars, TimeSpan timeout)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var text = await RunAsync<string?>(() =>
            {
                var root = AutomationElement.FromHandle(window);
                string? doc = null;
                foreach (AutomationElement d in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)))
                {
                    if (!d.TryGetCurrentPattern(TextPattern.Pattern, out var tp)) continue;
                    var t = ((TextPattern)tp).DocumentRange.GetText(maxChars * 4);
                    if (t.Length > (doc?.Length ?? 0)) doc = t;
                }
                if (!string.IsNullOrWhiteSpace(doc) && doc.Trim().Length > 40) return doc;
                var parts = new List<string>();
                var seen = new HashSet<string>();
                var cond = new OrCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem));
                foreach (AutomationElement e in root.FindAll(TreeScope.Descendants, cond))
                {
                    string v;
                    if (e.TryGetCurrentPattern(ValuePattern.Pattern, out var vp)) v = ((ValuePattern)vp).Current.Value ?? "";
                    else if (e.TryGetCurrentPattern(TextPattern.Pattern, out var tp2)) v = ((TextPattern)tp2).DocumentRange.GetText(maxChars);
                    else v = e.Current.Name ?? "";
                    v = v.Trim();
                    if (v.Length > 0 && seen.Add(v)) parts.Add(v);
                }
                var all = string.Join("\n", parts);
                return all.Length > 0 ? all : doc;
            }, timeout);
            if (!string.IsNullOrWhiteSpace(text)) return text;
            await Task.Delay(700);
        }
        return null;
    }

    private static string Norm(string s) => s.Trim().ToLowerInvariant().Replace('ё', 'е').Replace("&", "");

    /// <summary>AutomationId элемента с фокусом клавиатуры (null — не ответил за timeout). Дерево не обходит — годится
    /// для окон, обход которых вешает программу (окно «Изменить цвета» в Paint).</summary>
    public static string? FocusedAutomationId(TimeSpan timeout) =>
        RunAsync(() => AutomationElement.FocusedElement?.Current.AutomationId, timeout).GetAwaiter().GetResult();

    /// <summary>Вписать значение в поле с фокусом (ValuePattern), без обхода дерева.</summary>
    public static bool SetFocusedValue(string text, TimeSpan timeout) =>
        RunAsync(() =>
        {
            var el = AutomationElement.FocusedElement;
            if (el?.TryGetCurrentPattern(ValuePattern.Pattern, out var vp) != true || ((ValuePattern)vp).Current.IsReadOnly) return false;
            ((ValuePattern)vp).SetValue(text);
            return true;
        }, timeout).GetAwaiter().GetResult();

    /// <summary>UIA в фоне с таймаутом: зависшая программа не должна вешать Клаву (и UI-поток с хуком мыши).</summary>
    private static async Task<T?> RunAsync<T>(Func<T> f, TimeSpan timeout)
    {
        var task = Task.Run(() =>
        {
            try { return f(); }
            catch (ElementNotAvailableException) { return default; }
            catch (InvalidOperationException) { return default; }
            catch (System.Runtime.InteropServices.COMException) { return default; }
            catch (ArgumentException) { return default; }
        });
        // ConfigureAwait(false): FocusedAutomationId/SetFocusedValue ждут это синхронно — с UI-потока был бы дедлок
        return await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) == task ? await task.ConfigureAwait(false) : default;
    }
}
