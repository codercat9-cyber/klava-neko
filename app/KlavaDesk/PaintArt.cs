using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// «Шедевры в Paint»: картина пишется мазками, как у живописца, а не собирается из квадратов. Сначала — образец:
/// нейросеть-художник (модель картинок у мозгов Клавы с OpenAI-совместимым адресом — у друга это gemini-*-image
/// через AG Tools), а без неё — свой процедурный пейзаж. Потом живописный рендер по Херцману («Painterly Rendering
/// with Curved Brush Strokes», 1998): кисть от крупной к мелкой, мазок идёт вдоль контура и обрывается там, где
/// цвет уже не его. Каждый проход кисти — прозрачный слой мазков; в Paint он кладётся на новый слой точной вставкой
/// (копия слоя → мазки поверх → вставка), так что картина проявляется на глазах: набросок, подмалёвок, детали,
/// подпись «Клава» с отпечатком лапки.
/// </summary>
internal static partial class PaintTool
{
    public static string ArtDescribe =>
        "Нарисовать в Paint НАСТОЯЩУЮ КАРТИНУ по описанию или по образцу, по этапам, как художник, в выбранной технике: масло (мазки), " +
        "акварель (прозрачные заливки, кайма пигмента, бумага), карандаш, тушь/комикс (плоские цвета и контур), пиксель-арт, реализм " +
        "(тонкая детализация), импрессионизм, Ван Гог, пуантилизм. КАРАНДАШ (академический рисунок, штриховка, набросок) рисуется РУКОЙ " +
        "инструментом «Карандаш» Paint, штрих за штрихом, как в художке: построение (габарит, оси, овал) → лёгкий контур → штриховка по " +
        "форме тонами от светлого к тёмному, перекрёстно → акценты; блик — чистая бумага. Показали образец (вкладка, картинка) — " +
        "look_screen и reference_box с рамкой вокруг него: рисуешь его, а не выдумку. Без образца его рисует нейросеть-художник; если " +
        "её нет — свой процедурный образец только для пейзажей, иначе честный отказ. Живопись переносится слоями на НОВЫЙ слой " +
        "(рисунок друга под ним цел), в конце — твоя подпись от руки кистью (имя, росчерк, лапка). Новая картина — в СВОЁМ окне Paint " +
        "на чистом холсте нужного формата (format: вертикальный / квадрат / горизонтальный / широкий; без него — по образцу), рисунок друга " +
        "не трогается; canvas:'current' — писать в открытый холст. В prompt — всё, что просил друг, его словами (сюжет, кто в кадре, " +
        "настроение, формат), не подменяй своей выдумкой. «Готово» говори, только если в ответе «готова» и на сверке всё в кадре; " +
        "«НЕ ДОРИСОВАНО» — скажи честно и дорисуй. Бери, когда просят картину, рисунок, портрет, «шедевр», " +
        "а не схему: точные фигуры paint для картин — это квадраты Малевича. Карандашом рукой — пара минут, скажи другу, что рисуешь. " +
        "Результат оценивай ТОЛЬКО по присланному снимку, честно.";

    /// <summary>Копия ТЕКУЩЕГО слоя (PNG с альфа-каналом) и масштаб «пиксель экрана → пиксель холста».</summary>
    private static async Task<(Bitmap? Bmp, double Kx)> CopyLayerAsync(IntPtr win, Canvas cv, Hands h)
    {
        Uia.Focus(win);
        await h.PressAsync([0x1B], h.Token);                 // Esc — закрыть текстовое поле/выделение
        await Task.Delay(250, h.Token);                      // Paint дорисовывает последний мазок
        await FocusCanvasAsync(win, cv, h);                  // после окна цвета или меню слоя фокус не на холсте — Ctrl+A ушёл бы в ленту
        Bitmap? bmp = null;
        double kx = 1, prevMiss = -1;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            bmp?.Dispose();
            Sta(() => { Clipboard.Clear(); return 0; });
            await h.PressAsync([0x11, 0x41], h.Token);       // Ctrl+A
            await Task.Delay(150, h.Token);
            await h.PressAsync([0x11, 0x43], h.Token);       // Ctrl+C
            bmp = await Task.Run(() => Sta(() => WaitClipboardImage(TimeSpan.FromSeconds(4))));
            await h.PressAsync([0x1B], h.Token);             // снять выделение
            if (bmp is null) break;
            // экран → пиксели холста. Пиксели квадратные: масштаб один на обе оси. Рамку холста UI Automation обрезает
            // видимой областью (холст выше окна — высота меньше настоящей), поэтому верное отношение — меньшее из двух.
            kx = Math.Min(bmp.Width / (double)cv.Image.Width, bmp.Height / (double)cv.Image.Height);
            if (Math.Abs(kx - 1) > 0.01) break;              // не 100 % — попиксельно со экраном не сравнить
            var miss = await Task.Run(() => CopyMismatch(bmp, cv));
            // мало расхождений — копия свежая; расхождение не меняется от попытки к попытке — это что-то поверх холста
            // (чужое окно), а не недорисованный мазок: дальше ждать бесполезно
            if (miss <= 0.02 || Math.Abs(miss - prevMiss) < 0.005) break;
            prevMiss = miss;
            Log.Write($"paint: копия холста расходится с экраном на {miss:P0} — Paint ещё дорисовывает, копирую заново");
            await Task.Delay(400, h.Token);
        }
        return (bmp, kx);
    }

    /// <summary>Вставить картинку слоем (Ctrl+V — в левый верхний угол холста) и закрепить сменой инструмента.</summary>
    private static async Task PasteLayerAsync(IntPtr win, Bitmap bmp, Hands h, int settleMs = 500)
    {
        Sta(() => { SetClipboardImage(bmp); return 0; });
        Uia.Focus(win);
        await Task.Delay(120, h.Token);
        await h.PressAsync([0x11, 0x56], h.Token);           // Ctrl+V — встаёт в левый верхний угол холста
        await Task.Delay(settleMs, h.Token);
        await SelectAsync(win, "Кисти", h);                  // смена инструмента закрепляет вставку
        await Task.Delay(250, h.Token);
    }

    /// <summary>
    /// Операция paint {op:'signature'}: подпись Клавы ОТ РУКИ — каллиграфической кистью Paint, штрих за штрихом (имя,
    /// росчерк, лапка точками), на своём новом верхнем слое: картину под ним не трогает. Чернила — контрастом к углу.
    /// Текстом или готовой картинкой не подписывается никогда (друг 30.09: «не ставь текстовую автоподпись — подписывай
    /// картины вручную кисточкой»); прежний вензель (буквы шрифтом, вставка слоем) убран.
    /// </summary>
    private static async Task<bool> SignatureAsync(IntPtr win, Canvas cv, System.Text.Json.Nodes.JsonObject op, Hands h, StringBuilder log)
    {
        string S(string k) => op[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        double N(string k, double def) => op[k] is JsonValue v && v.TryGetValue<double>(out var d) ? d : def;
        var text = S("text").Length > 0 ? S("text") : "Клава";
        var place = PlaceOf(S("place"));
        if (!(op["new_layer"] is JsonValue nl && nl.TryGetValue<bool>(out var b) && !b))
        {
            var lr = await LayerAsync(win, "add", null, h);
            log.AppendLine(lr.Message);
            if (!lr.Ok) return false;
        }
        var c = cv.Visible;
        HandSignature.Plan plan;
        using (var seen = Unseen(() => CaptureScreen(c)))
        {
            var color = S("color").Length > 0 ? ExactColor(S("color")) : null;
            var scale = (float)Math.Clamp(N("size", place == "center" ? 0.2 : 0.08), 0.03, 0.4);
            plan = await Task.Run(() => HandSignature.Make(seen, c.Width, c.Height, text, place, scale, color), h.Token);
        }
        // перо — каллиграфическая кисть (толщина по наклону, как у настоящего пера); нет её — обычная кисть
        var pen = await BrushKindAsync(win, "Каллиграфическая кисть", h) ? "каллиграфической кистью"
            : await SelectAsync(win, "Кисти", h) ? "кистью" : null;
        if (pen is null) { log.AppendLine("[НЕ СРАБОТАЛО] подпись: не нашла кисть в Paint."); return false; }
        log.Append(await ColorAsync(win, Hex(plan.Ink), h));
        await SliderAsync(win, "Размер", plan.Pen);
        await SliderAsync(win, "Прозрачность", Math.Clamp(N("opacity", 1) * 100, 10, 100));
        Point At(Point p) => new(c.Left + p.X, c.Top + p.Y);
        foreach (var s in plan.Strokes)
        {
            h.Token.ThrowIfCancellationRequested();
            await h.DrawStrokeAsync(Dense([.. s.Select(At)], 3), MouseButtons.Left, 4, h.Token);
        }
        // лапка — точками круглой кисти: подушечка и четыре пальчика
        if (await BrushKindAsync(win, "Кисть", h) || await SelectAsync(win, "Кисти", h))
            foreach (var (p, size) in plan.Paw)
            {
                await SliderAsync(win, "Размер", size);
                var q = At(p);
                await h.DrawStrokeAsync([q, q with { X = q.X + 1 }], MouseButtons.Left, 3, h.Token);
            }
        // последний отпечаток кисти Paint закрепляет после следующего движения мыши
        await h.MoveAsync(new Point(c.Left - 12, c.Top + c.Height / 2), h.Token);
        await Task.Delay(150, h.Token);
        log.AppendLine($"подпись «{text}» от руки {pen}: {plan.Strokes.Count} штрихов, росчерк и лапка ({place}), {Hex(plan.Ink)}");
        return true;
    }

    private static string PlaceOf(string where)
    {
        var p = where.ToLowerInvariant();
        return p.Contains("center") || p.Contains("центр") || p.Contains("середин") ? "center"
            : (p.Contains("top") || p.Contains("верх")) && (p.Contains("left") || p.Contains("лев")) ? "top-left"
            : p.Contains("top") || p.Contains("верх") ? "top-right"
            : p.Contains("left") || p.Contains("лев") ? "bottom-left"
            : p is "bottom" or "низ" or "внизу" ? "bottom" : "bottom-right";
    }

    /// <summary>
    /// Инструмент paint_art: образец (с экрана, из файла или от нейросети-художника) → в Paint. Друг 30.09: «то голову
    /// обрежет, то по номерам, то холст странного формата, размытые, наполовину — и говорит готово». Поэтому:
    /// 1) образец просится с фигурой целиком в кадре; 2) новая картина — в СВОЁМ окне Paint на чистом холсте формата
    /// образца или того, что просил друг (файл в «Картины Клавы»), а не в том, что открыт (там было 1554×741, квадрат
    /// нейросети резался до полосы — голова уходила за край); подрезка, если всё же нужна, — снизу вдвое больше, чем
    /// сверху; 3) живопись до мелкой кисти и лессировка образцом — без «раскраски по номерам» и мыла; 4) «готово» —
    /// только если легли все проходы и холст похож на образец, иначе честное «не дорисовано»; сверка образец/холст.
    /// Карандаш — рукой (HandDrawAsync), остальное — проходами слоями. canvas:'current' — писать в открытый холст.
    /// </summary>
    public static async Task<ToolResult> ArtAsync(JsonObject args, ToolBox box, KlavaConfig cfg, CancellationToken ct)
    {
        string S(string k) => args[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        bool B(string k, bool def) => args[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;
        var prompt = S("prompt");
        var h = box.Hands;
        _hideSelf = box.HideSelfFromCapture;
        // образец, который показали: снять ДО того, как Paint выйдет вперёд и закроет браузер
        Bitmap? given = null;
        var givenFrom = "";
        if (args["reference_box"] is JsonArray rb)
        {
            if (h.Shot is null) return new ToolResult("Сначала look_screen: reference_box — рамка [x, y, w, h] образца по его снимку.");
            given = ReferenceFromScreen(rb, h);
            if (given is null) return new ToolResult("reference_box: рамка слишком мала — дай [x, y, w, h] образца по снимку look_screen.");
            givenFrom = "образец с экрана";
        }
        else if (S("reference_file") is { Length: > 0 } rf)
        {
            if (!File.Exists(rf)) return new ToolResult($"reference_file: нет файла {rf}.");
            given = new Bitmap(rf);
            givenFrom = $"образец из файла {Path.GetFileName(rf)}";
        }
        using var givenRef = given;
        if (prompt.Length == 0 && given is null)
            return new ToolResult("Скажи, что рисовать: prompt — описание картины («закат над морем с маяком»), или покажи образец: reference_box.");
        var style = Painterly.StyleOf(S("style").Length > 0 ? S("style") : prompt);
        var hand = style.Kind == "pencil" && B("hand", true);
        var onCurrent = S("canvas").ToLowerInvariant() is "current" or "текущий" or "этот" or "поверх" or "открытый";
        var format = CanvasFormat.Of(S("format"));
        var log = new StringBuilder();
        var clock = Stopwatch.StartNew();
        Bitmap? sample = null;
        string? file = null;
        IntPtr win;
        // 1. образец — до окна Paint: по нему (или по просьбе друга) выбирается формат холста
        if (!onCurrent)
        {
            var (hintW, hintH) = format.Size(1, 1000);
            if (given is not null) { sample = new Bitmap(given); log.AppendLine($"{givenFrom} {given.Width}×{given.Height}"); }
            else if (await ArtSource.GetAsync(cfg, prompt, style, hintW, hintH, S("source"), ct) is { } got)
            {
                sample = got.Image;
                log.AppendLine($"образец: {got.From}, {sample.Width}×{sample.Height} ({clock.Elapsed.TotalSeconds:F0} с)");
            }
            else return NoArtist(prompt);
            if (format.Name == "auto") format = CanvasFormat.ByAspect(sample.Width / (double)sample.Height);
            var (cw, ch) = format.Size(sample.Width / (double)sample.Height, CanvasFormat.FitHeight());
            file = NewCanvasFile(prompt, cw, ch);
            win = await OpenCanvasWindowAsync(file, ct);
            if (win == IntPtr.Zero) { sample.Dispose(); return new ToolResult($"Ошибка: не открылось окно Paint с новым холстом ({file})."); }
            log.AppendLine($"новый холст {format.Title} {cw}×{ch} в своём окне Paint: {file}");
        }
        else
        {
            win = await EnsurePaintAsync(ct);
            if (win == IntPtr.Zero) return new ToolResult("Ошибка: Paint не открылся (mspaint не найден).");
        }
        Uia.Focus(win);
        await Task.Delay(250, ct);
        await EnsureLayersPanelAsync(win);
        Canvas? canvas = null;
        for (var attempt = 0; attempt < 4 && canvas is null; attempt++)
        {
            if (attempt > 0) { Uia.Focus(win); await Task.Delay(400, ct); }
            canvas = await CanvasAsync(win);
        }
        if (canvas is null) { sample?.Dispose(); return new ToolResult("Ошибка: не нашла холст Paint (элемент image) — глянь look_screen."); }
        var cv = canvas.Value;
        if (B("new_layer", onCurrent))
        {
            var lr = await LayerAsync(win, "add", null, h);
            log.AppendLine(lr.Message);
        }
        var backup = Sta(BackupClipboard);
        var passes = new List<(string Name, Bitmap Layer)>();
        Bitmap? fitted = null;
        var complete = false;
        try
        {
            var (probe, _) = await CopyLayerAsync(win, cv, h);
            if (probe is null) return new ToolResult("[НЕ СРАБОТАЛО] Paint не отдал холст в буфер обмена (Ctrl+A, Ctrl+C) — картину не начала.");
            int W = probe.Width, H = probe.Height;
            probe.Dispose();
            if (sample is null)   // открытый холст: образец под его размер
            {
                if (given is not null) { sample = new Bitmap(given); log.AppendLine($"{givenFrom} {given.Width}×{given.Height}"); }
                else if (await ArtSource.GetAsync(cfg, prompt, style, W, H, S("source"), ct) is { } got)
                {
                    sample = got.Image;
                    log.AppendLine($"образец: {got.From}, {sample.Width}×{sample.Height} ({clock.Elapsed.TotalSeconds:F0} с)");
                }
                else return NoArtist(prompt);
            }
            fitted = ArtSource.Fit(sample, W, H, out var cut);
            if (cut.Length > 0) log.AppendLine(cut);
            if (hand)
            {
                await HandDrawAsync(win, cv, fitted, S("detail"), B("paper", onCurrent), W, H, h, log, clock);
                if (B("sign", true))
                    await SignatureAsync(win, cv, new JsonObject { ["place"] = S("sign_place") is { Length: > 0 } sp ? sp : "bottom-right" }, h, log);
                complete = true;
            }
            else
            {
                passes = await Task.Run(() => Painterly.Render(fitted, style, S("detail"), B("sketch", true), ct), ct);
                log.AppendLine($"живопись: {style.Title}, проходов кисти {passes.Count} ({clock.Elapsed.TotalSeconds:F0} с)");
                var n = 0;
                foreach (var (name, layer) in passes)
                {
                    h.Token.ThrowIfCancellationRequested();
                    var (cur, _) = await CopyLayerAsync(win, cv, h);
                    if (cur is null) { log.AppendLine($"[НЕ СРАБОТАЛО] «{name}»: Paint не отдал холст — дальше не пишу."); break; }
                    using (cur)
                    {
                        using (var g = Graphics.FromImage(cur))
                        {
                            g.CompositingMode = CompositingMode.SourceOver;
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.DrawImage(layer, new Rectangle(0, 0, cur.Width, cur.Height));
                        }
                        await PasteLayerAsync(win, cur, h, 900);
                    }
                    n++;
                    log.AppendLine($"слой {n}: {name}");
                }
                complete = n == passes.Count;
                if (!complete) log.AppendLine($"[НЕ ДОРИСОВАНО] легло {n} из {passes.Count} проходов — картина не закончена, не говори «готово».");
                // подпись — рукой кистью, когда картина уже на холсте (не картинкой-вензелем среди проходов)
                if (B("sign", true) && complete)
                    await SignatureAsync(win, cv, new JsonObject { ["place"] = S("sign_place") is { Length: > 0 } sp ? sp : "bottom-right" }, h, log);
            }
            // холст против образца: большие куски не похожи (пусто, не легло) — не готово
            if (complete && await UnlikeAsync(cv, fitted) is var unlike && unlike > 0.2)
            {
                complete = false;
                log.AppendLine($"[НЕ ДОРИСОВАНО] {unlike:P0} холста не похоже на образец (пусто или не легло) — картина не закончена, не говори «готово».");
            }
            if (complete)
                log.AppendLine($"{(hand ? "Рисунок карандашом готов" : "Картина готова")} за {clock.Elapsed.TotalSeconds:F0} с: «{prompt}», {style.Title}." +
                               (file is not null ? $" Она в своём окне Paint; сохранить в {file} — Ctrl+S." : ""));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { log.AppendLine("Друг забрал мышь — остановилась, рисунок недописан."); }
        finally
        {
            foreach (var (_, layer) in passes) layer.Dispose();
            Sta(() => { RestoreClipboard(backup); return 0; });
        }
        log.Append(await LayersSummaryAsync(win));
        byte[] shot;
        if (fitted is not null)
        {
            shot = await CompareShotAsync(cv.Visible, fitted);
            log.Append("На снимке слева образец, справа твой холст. Сравни ЧЕСТНО, прежде чем говорить: всё ли в кадре (голова, края), " +
                       "форма, тон, детали — что вышло, что нет. Не хвали вслепую и не приписывай картине того, чего на снимке нет.");
        }
        else
        {
            var hh = (int)Math.Round(1000.0 * cv.Visible.Height / cv.Visible.Width);
            shot = await SnapshotAsync(cv.Visible, hh, [], log);
            log.Append("Глянь на снимок и оцени честно, похоже ли на задуманное, — не хвастайся вслепую.");
        }
        fitted?.Dispose();
        sample?.Dispose();
        return new ToolResult(log.ToString(), shot);
    }

    private static ToolResult NoArtist(string prompt) =>
        new("[НЕ СРАБОТАЛО] нейросеть-художник сейчас недоступна, а свой процедурный образец умеет только пейзажи — " +
            $"«{prompt}» им не нарисовать. Скажи другу честно; можно показать образец на экране (look_screen, потом " +
            "paint_art с reference_box) или попробовать позже.");

    /// <summary>Окно Paint, где Клава рисовала последнюю картину, — следующие paint идут туда же, а не в окно друга.</summary>
    internal static IntPtr LastArtWindow { get; private set; }

    /// <summary>Чистый холст нужного размера файлом в «Картины Клавы» (белый PNG): Paint откроет его своим окном.</summary>
    private static string NewCanvasFile(string prompt, int w, int h)
    {
        var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var home = Directory.Exists(Path.Combine(desk, "ИИ кошкодевочка")) ? Path.Combine(desk, "ИИ кошкодевочка") : desk;
        var dir = Path.Combine(home, "Картины Клавы");
        Directory.CreateDirectory(dir);
        var title = new string(prompt.Take(40).Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? ' ' : ch).ToArray()).Trim();
        var path = Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd HH-mm-ss} {(title.Length > 0 ? title : "картина")}.png");
        using var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp)) g.Clear(Color.White);
        bmp.Save(path, ImageFormat.Png);
        return path;
    }

    /// <summary>Paint с этим файлом — новым окном (Paint из Store на каждый запуск — свой процесс); окно друга не трогается.</summary>
    private static async Task<IntPtr> OpenCanvasWindowAsync(string file, CancellationToken ct)
    {
        var before = Process.GetProcessesByName("mspaint").Select(p => { using (p) return p.Id; }).ToHashSet();
        try { Process.Start(new ProcessStartInfo("mspaint.exe") { UseShellExecute = true, ArgumentList = { file } }); }
        catch (System.ComponentModel.Win32Exception) { return IntPtr.Zero; }
        for (var i = 0; i < 80; i++)
        {
            await Task.Delay(250, ct);
            foreach (var p in Process.GetProcessesByName("mspaint"))
                using (p)
                {
                    if (before.Contains(p.Id)) continue;
                    p.Refresh();
                    if (p.MainWindowHandle == IntPtr.Zero) continue;
                    await Task.Delay(1500, ct);   // холсту нужно дорисоваться
                    LastArtWindow = p.MainWindowHandle;
                    return p.MainWindowHandle;
                }
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Какая доля холста не похожа на образец: клетки 24×24 экрана, средняя разница цвета больше 80 из 255 — «не легло»
    /// (пусто, белое, чужое). Только если холст виден целиком; иначе 0 (сверять не с чем).
    /// </summary>
    private static Task<double> UnlikeAsync(Canvas cv, Bitmap fitted) => Task.Run(() =>
    {
        var vis = cv.Visible;
        if (vis.Width < cv.Image.Width - 12 || vis.Height < cv.Image.Height - 12) return 0.0;
        using var seen = Unseen(() => CaptureScreen(vis));
        using var want = new Bitmap(vis.Width, vis.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(want))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            // видимая часть холста — это холст без полоски по краям (ручки размера): та же часть образца
            var kx = fitted.Width / (double)cv.Image.Width;
            var ky = fitted.Height / (double)cv.Image.Height;
            var src = new RectangleF((float)((vis.Left - cv.Image.Left) * kx), (float)((vis.Top - cv.Image.Top) * ky), (float)(vis.Width * kx), (float)(vis.Height * ky));
            g.DrawImage(fitted, new Rectangle(0, 0, vis.Width, vis.Height), src, GraphicsUnit.Pixel);
        }
        const int cell = 24;
        int bad = 0, all = 0;
        for (var y = 0; y + cell <= vis.Height; y += cell)
            for (var x = 0; x + cell <= vis.Width; x += cell)
            {
                double d = 0;
                var n = 0;
                for (var yy = y; yy < y + cell; yy += 4)
                    for (var xx = x; xx < x + cell; xx += 4)
                    {
                        var a = seen.GetPixel(xx, yy);
                        var b = want.GetPixel(xx, yy);
                        d += (Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B)) / 3.0;
                        n++;
                    }
                all++;
                if (d / n > 80) bad++;
            }
        return all == 0 ? 0.0 : bad / (double)all;
    });

    /// <summary>
    /// KlavaDesk.exe --paint-art "описание" [стиль] [procedural] [out=папка] — без Paint: образец, проходы кисти и итог
    /// картинками (проверить живопись глазами). С live — написать картину в своём новом окне Paint.
    /// </summary>
    public static int ArtTest(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        bool Opt(string a) => a.StartsWith("out=", StringComparison.Ordinal) || a.StartsWith("src=", StringComparison.Ordinal) ||
                              a.StartsWith("format=", StringComparison.Ordinal) || a is "procedural" or "live";
        var prompt = args.FirstOrDefault(a => !Opt(a)) ?? "закат над морем с маяком";
        var styleArg = args.Skip(1).FirstOrDefault(a => !Opt(a)) ?? prompt;
        var srcFile = args.FirstOrDefault(a => a.StartsWith("src=", StringComparison.Ordinal))?[4..];
        var outDir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..] ?? Path.Combine(Path.GetTempPath(), "paint_art");
        Directory.CreateDirectory(outDir);
        var cfg = KlavaConfig.Load();
        var style = Painterly.StyleOf(styleArg);
        // холст — как у живой картины: формат по просьбе (format=…) или по образцу, высота под экран
        var format = CanvasFormat.Of(args.FirstOrDefault(a => a.StartsWith("format=", StringComparison.Ordinal))?[7..] ?? "");
        var (hintW, hintH) = format.Size(1, 1000);
        var sw = Stopwatch.StartNew();
        var (src, from) = srcFile is not null && File.Exists(srcFile)
            ? (new Bitmap(srcFile), $"файл {Path.GetFileName(srcFile)}")
            : Task.Run(() => ArtSource.GetAsync(cfg, prompt, style, hintW, hintH, args.Contains("procedural") ? "procedural" : "", CancellationToken.None)).GetAwaiter().GetResult()
              ?? throw new InvalidOperationException("нейросеть-художник недоступна, а процедурно рисуются только пейзажи");
        if (format.Name == "auto") format = CanvasFormat.ByAspect(src.Width / (double)src.Height);
        var (W, H) = format.Size(src.Width / (double)src.Height, CanvasFormat.FitHeight());
        Console.WriteLine($"образец: {from}, {src.Width}×{src.Height}, {sw.ElapsedMilliseconds} мс; холст {format.Title} {W}×{H}");
        using var fitted = ArtSource.Fit(src, W, H, out _);
        src.Dispose();
        fitted.Save(Path.Combine(outDir, "0_образец.png"), ImageFormat.Png);
        sw.Restart();
        var passes = Painterly.Render(fitted, style, "", true, CancellationToken.None);
        Console.WriteLine($"живопись «{style.Title}»: {passes.Count} проходов, {sw.ElapsedMilliseconds} мс");
        using var canvas = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.White);
            var i = 0;
            foreach (var (name, layer) in passes)
            {
                i++;
                g.DrawImage(layer, 0, 0, W, H);
                canvas.Save(Path.Combine(outDir, $"{i}_{name}.png"), ImageFormat.Png);
                layer.Dispose();
            }
            // подпись от руки — те же штрихи, что поведёт кисть в Paint (здесь — круглым пером)
            HandSignature.Render(g, HandSignature.Make(canvas, W, H, "Клава", "bottom-right", 0.08f, null));
            canvas.Save(Path.Combine(outDir, $"{i + 1}_подпись.png"), ImageFormat.Png);
        }
        Console.WriteLine($"кадры: {outDir}");
        return 0;
    }
}

/// <summary>Живописный рендер мазками (Херцман): проходы кисти от крупной к мелкой, мазок — вдоль контура.</summary>
internal static class Painterly
{
    /// <param name="Radii">Радиусы кистей — доли большей стороны картинки.</param>
    /// <param name="Threshold">С какой ошибки цвета (0..441) в клетке сетки нужен мазок.</param>
    /// <param name="Curve">1 — мазок сразу поворачивает за контуром, меньше — плавнее, длинные завитки.</param>
    /// <param name="Blur">Размытие образца на проходе, в радиусах кисти.</param>
    /// <param name="Grid">Шаг сетки, в радиусах кисти.</param>
    /// <param name="Jitter">Разброс цвета мазка (0..255) — живость импрессионистов.</param>
    /// <param name="Kind">Как писать: strokes — мазки Херцмана; watercolor, pencil, ink, pixel — свои алгоритмы
    /// (PaintStyles); realism — мелкие мазки и лессировка образцом поверх.</param>
    /// <param name="Glaze">Лессировка образцом поверх мазков (0 — нет): детали и чёткость проступают, фактура мазков
    /// остаётся. Друг 30.09: «сколько раз просил не делать размытые картины» — у масла было 0, самая мелкая кисть ~6 px
    /// и порог 26: всё, что ближе, оставалось крупными плоскими пятнами — «раскраска по номерам».</param>
    internal sealed record Style(string Name, string Title, string Prompt, float[] Radii, float Threshold, int MinLen, int MaxLen,
        float Curve, float Blur, float Grid, float Alpha, float Jitter, string Kind = "strokes", float Glaze = 0);

    private static readonly Style Oil = new("oil", "маслом", "detailed oil painting on canvas, visible brushstrokes, rich colors, sharp focus",
        [1 / 34f, 1 / 68f, 1 / 130f, 1 / 240f, 1 / 420f], 18, 3, 14, 1f, 0.4f, 1f, 1f, 5, Glaze: 0.5f);
    private static readonly Style Impression = new("impression", "в манере импрессионистов", "impressionist painting in the style of Claude Monet, dappled light, short brushstrokes",
        [1 / 40f, 1 / 80f, 1 / 150f, 1 / 260f], 20, 1, 3, 1f, 0.5f, 0.8f, 0.95f, 18, Glaze: 0.3f);
    private static readonly Style Watercolor = new("watercolor", "акварелью", "delicate watercolor painting on white paper, soft transparent washes, wet edges, lots of light",
        [], 0, 0, 0, 0, 0, 0, 0, 0, "watercolor");
    private static readonly Style Pointillism = new("pointillism", "пуантилизмом", "pointillist painting in the style of Georges Seurat, tiny dots of pure color",
        [1 / 150f, 1 / 260f, 1 / 420f], 14, 0, 0, 1f, 0.4f, 0.9f, 1f, 28);
    private static readonly Style Expressive = new("expressive", "в манере Ван Гога", "expressive post-impressionist painting in the style of Vincent van Gogh, swirling impasto strokes",
        [1 / 40f, 1 / 80f, 1 / 150f, 1 / 260f], 22, 6, 24, 0.35f, 0.6f, 1f, 1f, 12, Glaze: 0.3f);
    private static readonly Style Pencil = new("pencil", "карандашом", "classical academic graphite pencil study on white paper, a single subject centered with wide empty margins, soft light from the upper left, full tonal range from white highlight to deep shadow, cast shadow, realistic proportions, not a comic",
        [], 0, 0, 0, 0, 0, 0, 0, 0, "pencil");
    private static readonly Style Ink = new("ink", "тушью, как комикс", "clean illustration with bold shapes and clear outlines, vivid flat colors, anime / comic look",
        [], 0, 0, 0, 0, 0, 0, 0, 0, "ink");
    private static readonly Style PixelArt = new("pixel", "пиксель-артом", "simple readable illustration with big clear shapes and bright colors, retro video game style",
        [], 0, 0, 0, 0, 0, 0, 0, 0, "pixel");
    private static readonly Style Realism = new("realism", "реалистично, с деталями", "highly detailed realistic painting, fine details, natural light, masterpiece",
        [1 / 40f, 1 / 80f, 1 / 160f, 1 / 320f, 1 / 560f], 18, 2, 10, 1f, 0.45f, 1f, 1f, 3, Glaze: 0.7f);

    /// <summary>Стиль по слову (или по описанию картины): масло по умолчанию.</summary>
    public static Style StyleOf(string? s)
    {
        var t = (s ?? "").ToLowerInvariant();
        if (Regex.IsMatch(t, "гог|gogh|звёздн|звездн|swirl|экспресс|express")) return Expressive;
        if (Regex.IsMatch(t, "акварел|watercol")) return Watercolor;
        if (Regex.IsMatch(t, "карандаш|графит|скетч|эскиз|штрих|pencil|sketch|graphite")) return Pencil;
        if (Regex.IsMatch(t, "туш|комикс|манг|линер|аниме|мульт|ink|comic|manga|cartoon|anime")) return Ink;
        if (Regex.IsMatch(t, "пиксел|pixel|8[- ]?бит|8[- ]?bit|ретро.?игр|денди")) return PixelArt;
        if (Regex.IsMatch(t, "реализм|реалист|фото|детальн|детализ|подробн|realis|photo|detail")) return Realism;
        if (Regex.IsMatch(t, "пуант|point|сёра|сера|seurat|точк")) return Pointillism;
        if (Regex.IsMatch(t, "импрес|impress|моне|monet|ренуар")) return Impression;
        return Oil;
    }

    private readonly record struct Stroke(PointF[] Points, float Radius, Color Color);

    /// <summary>
    /// Проходы кисти поверх белого холста: (название, прозрачный слой мазков размером с картинку). sketch — первым
    /// проходом карандашный набросок по контурам. detail: fast — без самой мелкой кисти, fine — ещё мельче.
    /// </summary>
    public static List<(string Name, Bitmap Layer)> Render(Bitmap reference, Style st, string? detail, bool sketch, CancellationToken ct)
    {
        switch (st.Kind)
        {
            case "watercolor": return PaintStyles.Watercolor(reference, ct);
            case "pencil": return PaintStyles.Pencil(reference, ct);
            case "ink": return PaintStyles.Ink(reference, ct);
            case "pixel": return PaintStyles.Pixel(reference, ct);
            default:
            {
                // мазки всех кистей до самой мелкой, потом лессировка: образец полупрозрачно поверх — детали проступают,
                // а фактура мазков остаётся
                var strokes = Strokes(reference, st, detail, sketch, ct);
                if (st.Glaze > 0) strokes.Add(("лессировка деталей", PaintStyles.Glaze(reference, st.Glaze)));
                return strokes;
            }
        }
    }

    /// <summary>Как картина будет выглядеть на белом холсте (все проходы по порядку) — по ней вензель выбирает цвет.</summary>
    public static Bitmap Preview(List<(string Name, Bitmap Layer)> passes, int W, int H)
    {
        var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        foreach (var (_, layer) in passes) g.DrawImage(layer, 0, 0, W, H);
        return bmp;
    }

    private static List<(string Name, Bitmap Layer)> Strokes(Bitmap reference, Style st, string? detail, bool sketch, CancellationToken ct)
    {
        int W = reference.Width, H = reference.Height;
        // считать на картинке не больше 1400 px по большей стороне, мазки рисовать в полном размере
        var s = Math.Min(1f, 1400f / Math.Max(W, H));
        int pw = Math.Max(8, (int)Math.Round(W * s)), ph = Math.Max(8, (int)Math.Round(H * s));
        using var small = new Bitmap(pw, ph, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(reference, new Rectangle(0, 0, pw, ph));
        }
        var (rr, rg, rb) = Channels(small);
        var passes = new List<(string, Bitmap)>();
        if (sketch) passes.Add(("набросок", Sketch(rr, rg, rb, pw, ph, W, H)));

        var radii = st.Radii.Select(f => Math.Max(1.5f, f * Math.Max(pw, ph))).ToList();
        if (detail is "fast" or "быстро" && radii.Count > 2) radii.RemoveAt(radii.Count - 1);
        if (detail is "fine" or "тонко" or "детально") radii.Add(Math.Max(1.2f, radii[^1] / 1.8f));

        // холст в размере расчёта: по нему ищется, где ещё не так, как на образце
        using var proc = new Bitmap(pw, ph, PixelFormat.Format32bppArgb);
        var canA = new float[pw * ph];
        var canR = new float[pw * ph];
        var canG = new float[pw * ph];
        var canB = new float[pw * ph];
        var rnd = new Random(12345);
        var names = new[] { "подмалёвок крупной кистью", "формы средней кистью", "детали мелкой кистью", "тонкие детали", "самые тонкие мазки" };
        for (var li = 0; li < radii.Count; li++)
        {
            ct.ThrowIfCancellationRequested();
            var R = radii[li];
            var sigma = Math.Max(0.6f, st.Blur * R);
            var br = Blur(rr, pw, ph, sigma);
            var bg = Blur(rg, pw, ph, sigma);
            var bb = Blur(rb, pw, ph, sigma);
            var (gx, gy) = Gradient(br, bg, bb, pw, ph);
            var grid = Math.Max(1, (int)Math.Round(st.Grid * R));
            var rows = (ph + grid - 1) / grid;
            var perRow = new List<Stroke>[rows];
            Parallel.For(0, rows, new ParallelOptions { CancellationToken = ct }, row =>
            {
                var list = new List<Stroke>();
                var local = new Random(li * 100003 + row);
                var y = row * grid + grid / 2;
                for (var x = grid / 2; x < pw; x += grid)
                {
                    int x0 = Math.Max(0, x - grid / 2), x1 = Math.Min(pw - 1, x + grid / 2), y0 = Math.Max(0, y - grid / 2), y1 = Math.Min(ph - 1, y + grid / 2);
                    double sum = 0;
                    var n = 0;
                    float best = -1;
                    int bx = x, by = y;
                    for (var yy = y0; yy <= y1; yy++)
                        for (var xx = x0; xx <= x1; xx++)
                        {
                            var i = yy * pw + xx;
                            var d = canA[i] < 0.5f ? 999f : Dist(canR[i], canG[i], canB[i], br[i], bg[i], bb[i]);
                            sum += d;
                            n++;
                            if (d > best) { best = d; bx = xx; by = yy; }
                        }
                    if (n == 0 || sum / n <= st.Threshold) continue;
                    list.Add(MakeStroke(bx, by, R, st, br, bg, bb, gx, gy, canA, canR, canG, canB, pw, ph, local));
                }
                perRow[row] = list;
            });
            var strokes = perRow.Where(l => l is not null).SelectMany(l => l).ToList();
            // в случайном порядке: иначе видно, как кисть шла строками
            for (var i = strokes.Count - 1; i > 0; i--) { var j = rnd.Next(i + 1); (strokes[i], strokes[j]) = (strokes[j], strokes[i]); }
            var layer = new Bitmap(W, H, PixelFormat.Format32bppArgb);
            using (var gl = Graphics.FromImage(layer))
            using (var gp = Graphics.FromImage(proc))
            {
                foreach (var g in new[] { gl, gp })
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                }
                gl.ScaleTransform(1 / s, 1 / s);
                foreach (var k in strokes)
                {
                    DrawStroke(gl, k, st.Alpha);
                    DrawStroke(gp, k, st.Alpha);
                }
            }
            ReadCanvas(proc, canA, canR, canG, canB);
            passes.Add((li < names.Length ? names[li] : $"проход {li + 1}", layer));
            Log.Write($"живопись: кисть {R:F1} px, мазков {strokes.Count}");
        }
        return passes;
    }

    private static float Dist(float r1, float g1, float b1, float r2, float g2, float b2)
    {
        float dr = r1 - r2, dg = g1 - g2, db = b1 - b2;
        return MathF.Sqrt(dr * dr + dg * dg + db * db);
    }

    /// <summary>Мазок из точки: цвет образца там; дальше — поперёк градиента яркости (вдоль контура), шаг — радиус кисти.</summary>
    private static Stroke MakeStroke(int x0, int y0, float R, Style st, float[] br, float[] bg, float[] bb, float[] gx, float[] gy,
        float[] canA, float[] canR, float[] canG, float[] canB, int w, int h, Random rnd)
    {
        var i0 = y0 * w + x0;
        float cr = br[i0], cg = bg[i0], cb = bb[i0];
        if (st.Jitter > 0)
        {
            var common = (float)(rnd.NextDouble() * 2 - 1) * st.Jitter * 0.5f;
            cr = Math.Clamp(cr + common + (float)(rnd.NextDouble() * 2 - 1) * st.Jitter * 0.5f, 0, 255);
            cg = Math.Clamp(cg + common + (float)(rnd.NextDouble() * 2 - 1) * st.Jitter * 0.5f, 0, 255);
            cb = Math.Clamp(cb + common + (float)(rnd.NextDouble() * 2 - 1) * st.Jitter * 0.5f, 0, 255);
        }
        var pts = new List<PointF> { new(x0 + 0.5f, y0 + 0.5f) };
        float x = x0 + 0.5f, y = y0 + 0.5f, ldx = 0, ldy = 0;
        for (var k = 1; k <= st.MaxLen; k++)
        {
            int ix = Math.Clamp((int)x, 0, w - 1), iy = Math.Clamp((int)y, 0, h - 1), i = iy * w + ix;
            if (k > st.MinLen)
            {
                var toCanvas = canA[i] < 0.5f ? 999f : Dist(br[i], bg[i], bb[i], canR[i], canG[i], canB[i]);
                var toStroke = Dist(br[i], bg[i], bb[i], cr, cg, cb);
                if (toCanvas < toStroke) break;
            }
            float gxv = gx[i], gyv = gy[i];
            var gl = MathF.Sqrt(gxv * gxv + gyv * gyv);
            if (gl < 1e-3f) break;
            float dx = -gyv / gl, dy = gxv / gl;
            if (ldx * dx + ldy * dy < 0) { dx = -dx; dy = -dy; }
            dx = st.Curve * dx + (1 - st.Curve) * ldx;
            dy = st.Curve * dy + (1 - st.Curve) * ldy;
            var dl = MathF.Sqrt(dx * dx + dy * dy);
            if (dl < 1e-4f) break;
            dx /= dl; dy /= dl;
            x += R * dx;
            y += R * dy;
            if (x < 0 || y < 0 || x >= w || y >= h) break;
            ldx = dx; ldy = dy;
            pts.Add(new PointF(x, y));
        }
        return new Stroke(pts.ToArray(), R, Color.FromArgb(255, (int)cr, (int)cg, (int)cb));
    }

    private static void DrawStroke(Graphics g, Stroke k, float alpha)
    {
        var col = Color.FromArgb((int)Math.Clamp(alpha * 255, 1, 255), k.Color);
        if (k.Points.Length == 1)
        {
            using var br = new SolidBrush(col);
            g.FillEllipse(br, k.Points[0].X - k.Radius, k.Points[0].Y - k.Radius, 2 * k.Radius, 2 * k.Radius);
            return;
        }
        using var pen = new Pen(col, 2 * k.Radius) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        if (k.Points.Length == 2) g.DrawLine(pen, k.Points[0], k.Points[1]);
        else g.DrawCurve(pen, k.Points, 0.5f);
    }

    private static (float[] R, float[] G, float[] B) Channels(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        float[] r = new float[w * h], g = new float[w * h], b = new float[w * h];
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, w * 4);
                for (var x = 0; x < w; x++)
                {
                    b[y * w + x] = row[x * 4];
                    g[y * w + x] = row[x * 4 + 1];
                    r[y * w + x] = row[x * 4 + 2];
                }
            }
        }
        finally { bmp.UnlockBits(d); }
        return (r, g, b);
    }

    /// <summary>Холст расчёта обратно в массивы (что уже закрашено и каким цветом).</summary>
    private static void ReadCanvas(Bitmap bmp, float[] a, float[] r, float[] g, float[] b)
    {
        int w = bmp.Width, h = bmp.Height;
        var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (var y = 0; y < h; y++)
            {
                Marshal.Copy(d.Scan0 + y * d.Stride, row, 0, w * 4);
                for (var x = 0; x < w; x++)
                {
                    var i = y * w + x;
                    var al = row[x * 4 + 3] / 255f;
                    a[i] = al;
                    // холст под мазками белый: полупрозрачная акварель — это смесь с белым
                    b[i] = row[x * 4] * al + 255 * (1 - al);
                    g[i] = row[x * 4 + 1] * al + 255 * (1 - al);
                    r[i] = row[x * 4 + 2] * al + 255 * (1 - al);
                }
            }
        }
        finally { bmp.UnlockBits(d); }
    }

    /// <summary>Размытие, близкое к гауссову: три прохода коробочного фильтра по строкам и столбцам.</summary>
    private static float[] Blur(float[] src, int w, int h, float sigma)
    {
        var r = Math.Max(1, (int)Math.Round(sigma * 0.9f));
        var a = (float[])src.Clone();
        var tmp = new float[a.Length];
        for (var pass = 0; pass < 3; pass++)
        {
            BoxH(a, tmp, w, h, r);
            BoxV(tmp, a, w, h, r);
        }
        return a;
    }

    private static void BoxH(float[] s, float[] d, int w, int h, int r)
    {
        Parallel.For(0, h, y =>
        {
            var o = y * w;
            float acc = 0;
            var n = 0;
            for (var x = -r; x <= r; x++) if (x >= 0 && x < w) { acc += s[o + x]; n++; }
            for (var x = 0; x < w; x++)
            {
                d[o + x] = acc / n;
                int xo = x - r, xi = x + r + 1;
                if (xo >= 0) { acc -= s[o + xo]; n--; }
                if (xi < w) { acc += s[o + xi]; n++; }
            }
        });
    }

    private static void BoxV(float[] s, float[] d, int w, int h, int r)
    {
        Parallel.For(0, w, x =>
        {
            float acc = 0;
            var n = 0;
            for (var y = -r; y <= r; y++) if (y >= 0 && y < h) { acc += s[y * w + x]; n++; }
            for (var y = 0; y < h; y++)
            {
                d[y * w + x] = acc / n;
                int yo = y - r, yi = y + r + 1;
                if (yo >= 0) { acc -= s[yo * w + x]; n--; }
                if (yi < h) { acc += s[yi * w + x]; n++; }
            }
        });
    }

    /// <summary>Градиент яркости (Собель).</summary>
    private static (float[] Gx, float[] Gy) Gradient(float[] r, float[] g, float[] b, int w, int h)
    {
        var lum = new float[w * h];
        for (var i = 0; i < lum.Length; i++) lum[i] = 0.299f * r[i] + 0.587f * g[i] + 0.114f * b[i];
        float[] gx = new float[w * h], gy = new float[w * h];
        Parallel.For(1, h - 1, y =>
        {
            for (var x = 1; x < w - 1; x++)
            {
                int i = y * w + x;
                float tl = lum[i - w - 1], t = lum[i - w], tr = lum[i - w + 1], l = lum[i - 1], rr = lum[i + 1], bl = lum[i + w - 1], bt = lum[i + w], brr = lum[i + w + 1];
                gx[i] = (tr + 2 * rr + brr) - (tl + 2 * l + bl);
                gy[i] = (bl + 2 * bt + brr) - (tl + 2 * t + tr);
            }
        });
        return (gx, gy);
    }

    /// <summary>Карандашный набросок: контуры образца графитом, сильнее — темнее.</summary>
    private static Bitmap Sketch(float[] r, float[] g, float[] b, int pw, int ph, int W, int H)
    {
        var (gx, gy) = Gradient(Blur(r, pw, ph, 1.2f), Blur(g, pw, ph, 1.2f), Blur(b, pw, ph, 1.2f), pw, ph);
        var mag = new float[pw * ph];
        for (var i = 0; i < mag.Length; i++) mag[i] = MathF.Sqrt(gx[i] * gx[i] + gy[i] * gy[i]);
        var sorted = mag.Where(v => v > 0).OrderBy(v => v).ToArray();
        var thr = sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.88)] : 1;
        var top = sorted.Length > 0 ? sorted[^1] : 1;
        using var small = new Bitmap(pw, ph, PixelFormat.Format32bppArgb);
        var d = small.LockBits(new Rectangle(0, 0, pw, ph), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[pw * 4];
            for (var y = 0; y < ph; y++)
            {
                Array.Clear(row);
                for (var x = 0; x < pw; x++)
                {
                    var m = mag[y * pw + x];
                    if (m < thr) continue;
                    var a = Math.Clamp((m - thr) / Math.Max(1, top - thr) * 2.2f, 0.15f, 0.85f);
                    row[x * 4] = 60; row[x * 4 + 1] = 62; row[x * 4 + 2] = 70; row[x * 4 + 3] = (byte)(a * 255);
                }
                Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, pw * 4);
            }
        }
        finally { small.UnlockBits(d); }
        var full = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var gr = Graphics.FromImage(full))
        {
            gr.InterpolationMode = InterpolationMode.HighQualityBilinear;
            gr.DrawImage(small, new Rectangle(0, 0, W, H));
        }
        return full;
    }
}

/// <summary>Откуда образец картины: нейросеть-художник у мозгов Клавы или свой процедурный пейзаж.</summary>
/// <summary>Формат холста новой картины: по просьбе друга или по образцу (квадрат нейросети — квадратный холст).</summary>
internal sealed record CanvasFormat(string Name, string Title, double Aspect)
{
    private static readonly CanvasFormat Auto = new("auto", "по образцу", 0);
    private static readonly CanvasFormat[] All =
    [
        new("portrait", "вертикальный 3:4", 3 / 4.0), new("square", "квадратный", 1), new("landscape", "горизонтальный 4:3", 4 / 3.0),
        new("wide", "широкий 16:9", 16 / 9.0),
    ];

    /// <summary>Формат словами друга: «вертикальный», «квадрат», «горизонтальный», «широкий»; пусто — по образцу.</summary>
    public static CanvasFormat Of(string s)
    {
        var t = s.ToLowerInvariant();
        return Regex.IsMatch(t, "вертик|портрет|книжн|в рост|portrait|vertical|3:4|2:3|9:16") ? All[0]
            : Regex.IsMatch(t, "квадрат|square|1:1") ? All[1]
            : Regex.IsMatch(t, "широк|панорам|wide|16:9|21:9|кино") ? All[3]
            : Regex.IsMatch(t, "горизонт|альбомн|landscape|horizontal|4:3|3:2") ? All[2]
            : Auto;
    }

    public static CanvasFormat ByAspect(double aspect) => All.MinBy(f => Math.Abs(Math.Log(f.Aspect / aspect)))!;

    /// <summary>Размер холста: пропорции формата (у «по образцу» — образца), заданная высота, ширина — не шире экрана.</summary>
    public (int W, int H) Size(double sampleAspect, int height)
    {
        var a = Aspect > 0 ? Aspect : sampleAspect;
        var maxW = Math.Clamp((Screen.PrimaryScreen?.WorkingArea.Width ?? 1920) - 160, 640, 1800);
        int hh = height, ww = (int)Math.Round(hh * a);
        if (ww > maxW) { ww = maxW; hh = (int)Math.Round(ww / a); }
        return (ww, hh);
    }

    /// <summary>Высота холста, чтобы он целиком влез в окно Paint на весь экран в 100 % (лента и строка состояния — ~300 px).</summary>
    public static int FitHeight() => Math.Clamp((Screen.PrimaryScreen?.WorkingArea.Height ?? 1040) - 300, 480, 1000);
}

internal static class ArtSource
{
    private static readonly Dictionary<string, (DateTime At, List<string> Models)> ModelCache = new();

    /// <summary>Своим процедурным образцом можно заменить только пейзаж: 28.09 вместо яблока нарисовался закат над морем.</summary>
    public static bool Landscape(string prompt) =>
        Regex.IsMatch(prompt.ToLowerInvariant(), "пейзаж|закат|рассвет|море|океан|озер|река|горы|гора|лес|поле|небо|облак|маяк|город|ночь|звёзд|звезд|landscape|sunset|sea|mountain|forest|lake");

    /// <summary>Образец: нейросеть-художник; без неё — свой пейзаж, но только для пейзажа. null — нечем нарисовать.</summary>
    public static async Task<(Bitmap Image, string From)?> GetAsync(KlavaConfig cfg, string prompt, Painterly.Style style, int w, int h, string mode, CancellationToken ct)
    {
        if (mode is not ("procedural" or "процедурно" or "свой"))
        {
            var aspect = w >= h * 1.6 ? "wide 16:9 landscape" : w >= h * 1.15 ? "4:3 landscape" : w * 1.15 < h ? "portrait" : "square";
            // голову не срезать: главный объект целиком в кадре с полями (друг 30.09: «то голову обрежет»)
            var full = $"{prompt}. {style.Prompt}. Masterpiece fine art painting, {aspect} composition, the whole main subject fits inside " +
                       "the frame with some margin around it (full head visible, nothing important cut off at the edges), no text, no letters, " +
                       "no signature, no watermark, no frame.";
            // сначала свои (loopback: AG Tools и локальные), потом облачные — у бесплатных ключей Gemini картинок часто нет
            foreach (var p in cfg.Brains.Where(b => b.Kind == "openai" && b.Url.Length > 0).OrderBy(b => NetProxy.IsLoopback(b.Url) ? 0 : 1))
            {
                try
                {
                    if (await FromOpenAiAsync(p, full, w, h, ct) is { } got) return got;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or JsonException
                                              or ArgumentException or FormatException or ExternalException)
                {
                    Log.Write($"картина: {p.Name}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        if (mode is not ("procedural" or "процедурно" or "свой") && !Landscape(prompt)) return null;
        return (ProceduralArt.Paint(prompt, w, h), "свой процедурный пейзаж (нейросеть-художник сейчас недоступна)");
    }

    /// <summary>Модели картинок у OpenAI-совместимого мозга: сначала быстрые flash, потом pro с подходящими пропорциями.</summary>
    private static async Task<List<string>> ImageModelsAsync(BrainProfile p, string? key, int w, int h, CancellationToken ct)
    {
        lock (ModelCache)
            if (ModelCache.TryGetValue(p.Url, out var c) && DateTime.UtcNow - c.At < TimeSpan.FromMinutes(20)) return c.Models;
        using var req = new HttpRequestMessage(HttpMethod.Get, p.Url.TrimEnd('/') + "/models");
        if (key is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(12));
        using var resp = await NetProxy.ClientFor(p.Url, p.Proxy).SendAsync(req, cts.Token);
        var list = new List<string>();
        if (resp.IsSuccessStatusCode && JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token))?["data"] is JsonArray data)
        {
            var ratio = w / (double)h;
            var want = ratio >= 1.6 ? "16x9" : ratio >= 1.15 ? "4x3" : ratio * 1.15 < 1 ? "3x4" : "1x1";
            list = data.Select(m => m?["id"]?.GetValue<string>()?.Replace("models/", "") ?? "")
                .Where(id => Regex.IsMatch(id, "image", RegexOptions.IgnoreCase) && !Regex.IsMatch(id, "imagen|edit|embed|4k|2k", RegexOptions.IgnoreCase))
                // сперва модель с нужными пропорциями (pro-…-16x9: без квоты отвечает отказом сразу), потом быстрая flash —
                // она рисует квадрат, и под широкий холст его приходится подрезать
                .OrderBy(id => id.EndsWith(want, StringComparison.OrdinalIgnoreCase) ? 0
                              : id.Contains("flash", StringComparison.OrdinalIgnoreCase) && !id.Contains("lite", StringComparison.OrdinalIgnoreCase) ? 1
                              : id.Contains("lite", StringComparison.OrdinalIgnoreCase) ? 3 : 2)
                .Take(3).ToList();
        }
        lock (ModelCache) ModelCache[p.Url] = (DateTime.UtcNow, list);
        return list;
    }

    private static async Task<(Bitmap Image, string From)?> FromOpenAiAsync(BrainProfile p, string prompt, int w, int h, CancellationToken ct)
    {
        var key = ApiKeys.Resolve(p.Key);
        foreach (var model in await ImageModelsAsync(p, key, w, h, ct))
        {
            var body = new JsonObject
            {
                ["model"] = model,
                ["stream"] = false,
                ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = prompt }),
            };
            using var req = new HttpRequestMessage(HttpMethod.Post, p.Url.TrimEnd('/') + "/chat/completions")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            if (key is not null) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(150));
            var sw = Stopwatch.StartNew();
            using var resp = await NetProxy.ClientFor(p.Url, p.Proxy).SendAsync(req, cts.Token);
            var text = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Write($"картина: {p.Name} / {model}: {(int)resp.StatusCode} {(text.Length > 200 ? text[..200] : text)}");
                continue;
            }
            // картинка приходит ссылкой data:image/…;base64 в тексте ответа (AG Tools — markdown ![image](data:…), иногда
            // несколько черновиков подряд — берём последнюю) или в поле images
            var hits = Regex.Matches(text, @"data:image/(?:png|jpeg|jpg|webp);base64,([A-Za-z0-9+/=]+)");
            if (hits.Count == 0) { Log.Write($"картина: {p.Name} / {model}: в ответе нет картинки"); continue; }
            var bytes = Convert.FromBase64String(hits[^1].Groups[1].Value);
            using var ms = new MemoryStream(bytes);
            using var img = Image.FromStream(ms);
            var bmp = new Bitmap(img);
            Log.Write($"картина: {p.Name} / {model} — {bmp.Width}×{bmp.Height} за {sw.ElapsedMilliseconds} мс");
            return (bmp, $"нейросеть-художник {model}");
        }
        return null;
    }

    /// <summary>
    /// Образец под холст: пропорции совпали — только масштаб; нет — подрезка, бока поровну, а по высоте снизу вдвое
    /// больше, чем сверху (голова и верх в кадре, режется земля). note — что подрезано (для отчёта), пусто — ничего.
    /// </summary>
    public static Bitmap Fit(Bitmap src, int w, int h, out string note)
    {
        var a = src.Width / (double)src.Height;
        var b = w / (double)h;
        note = Math.Abs(Math.Log(a / b)) <= 0.03 ? ""
            : a > b ? $"образец шире холста — подрезала бока поровну ({(1 - b / a):P0} ширины)"
            : $"образец выше холста — подрезала {(1 - a / b):P0} высоты: снизу вдвое больше, чем сверху, чтобы голова и верх остались в кадре";
        return Cover(src, w, h, 1 / 3.0);
    }

    /// <summary>Вписать образец в холст «с обрезкой»: заполняет целиком, лишнее по краям срезается; anchorY — какая доля
    /// лишней высоты срезается сверху (0,5 — поровну, 1/3 — сверху вдвое меньше).</summary>
    public static Bitmap Cover(Bitmap src, int w, int h, double anchorY = 0.5)
    {
        var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var k = Math.Max(w / (double)src.Width, h / (double)src.Height);
        double cw = w / k, ch = h / k;
        var srcRect = new RectangleF((float)((src.Width - cw) / 2), (float)((src.Height - ch) * anchorY), (float)cw, (float)ch);
        g.DrawImage(src, new RectangleF(0, 0, w, h), srcRect, GraphicsUnit.Pixel);
        return dst;
    }
}

/// <summary>
/// Свой пейзаж без нейросети — образец для живописи, когда художника нет: небо по времени суток (закат, день, ночь
/// со звёздами и луной), солнце с ореолом, облака, горы в дымке слоями (дальние светлее и синее), море или озеро
/// с отражением и солнечной дорожкой, луг/лес/снег, город огоньками. Что есть на картине — по словам описания.
/// </summary>
internal static class ProceduralArt
{
    public static Bitmap Paint(string prompt, int w, int h)
    {
        var t = prompt.ToLowerInvariant();
        bool Has(string rx) => Regex.IsMatch(t, rx);
        var night = Has("ноч|звёзд|звезд|луна|night|star|moon");
        var sunset = !night && Has("закат|рассвет|вечер|sunset|sunrise|dusk|dawn");
        var day = !night && !sunset;
        var sea = Has("мор|океан|озер|озёр|река|вод|sea|ocean|lake|river|маяк|lighthouse|пляж|beach");
        var mountains = Has("гор|скал|mountain|alp") || !sea && !Has("город|city|поле|луг|field");
        var winter = Has("зим|снег|snow|winter");
        var forest = Has("лес|сосн|ель|ёлк|дерев|forest|tree|pine");
        var city = Has("город|city|питер|петербург|москв|town|улиц");
        var rnd = new Random(prompt.GetHashCode() & 0x7fffffff);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var horizon = (int)(h * (sea ? 0.58 : 0.66));

        // небо
        Color[] sky = night ? [Color.FromArgb(8, 12, 40), Color.FromArgb(22, 30, 78), Color.FromArgb(48, 56, 110)]
            : sunset ? [Color.FromArgb(46, 38, 110), Color.FromArgb(170, 70, 120), Color.FromArgb(250, 150, 70), Color.FromArgb(255, 215, 120)]
            : [Color.FromArgb(70, 120, 200), Color.FromArgb(130, 180, 235), Color.FromArgb(205, 225, 245)];
        using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, horizon + 2), sky[0], sky[^1], 90f))
        {
            br.InterpolationColors = new ColorBlend { Colors = sky, Positions = Enumerable.Range(0, sky.Length).Select(i => i / (float)(sky.Length - 1)).ToArray() };
            g.FillRectangle(br, 0, 0, w, horizon + 2);
        }
        if (night)
            for (var i = 0; i < w * horizon / 900; i++)
            {
                float sx = rnd.Next(w), sy = rnd.Next(horizon), sr = (float)(rnd.NextDouble() * 1.6 + 0.4);
                using var sb = new SolidBrush(Color.FromArgb(rnd.Next(150, 255), 255, 250, 225));
                g.FillEllipse(sb, sx, sy, sr * 2, sr * 2);
            }

        // солнце или луна с ореолом
        var sunR = h * (night ? 0.05f : 0.07f);
        var sunX = w * (0.25f + (float)rnd.NextDouble() * 0.5f);
        var sunY = night ? h * 0.18f : sunset ? horizon - sunR * 0.9f : h * 0.2f;
        var sunCol = night ? Color.FromArgb(245, 240, 215) : sunset ? Color.FromArgb(255, 200, 90) : Color.FromArgb(255, 250, 220);
        for (var k = 6; k >= 1; k--)
        {
            var rr = sunR * (1 + k * 0.7f);
            using var gb = new SolidBrush(Color.FromArgb(night ? 10 : 18, sunCol));
            g.FillEllipse(gb, sunX - rr, sunY - rr, rr * 2, rr * 2);
        }
        using (var sb = new SolidBrush(sunCol)) g.FillEllipse(sb, sunX - sunR, sunY - sunR, sunR * 2, sunR * 2);

        // облака — мягкие гроздья овалов
        if (!night)
            for (var c = 0; c < 5; c++)
            {
                float cx = rnd.Next(w), cy = rnd.Next((int)(horizon * 0.15), (int)(horizon * 0.6)), cw = w * (0.08f + (float)rnd.NextDouble() * 0.12f);
                var cc = sunset ? Color.FromArgb(70, 255, 190, 170) : Color.FromArgb(90, 255, 255, 255);
                using var cb = new SolidBrush(cc);
                for (var p = 0; p < 9; p++)
                {
                    float ox = (float)(rnd.NextDouble() - 0.5) * cw, oy = (float)(rnd.NextDouble() - 0.5) * cw * 0.18f, pr = cw * (0.18f + (float)rnd.NextDouble() * 0.2f);
                    g.FillEllipse(cb, cx + ox - pr, cy + oy - pr * 0.5f, pr * 2, pr);
                }
            }

        // горы слоями: дальние светлее и ближе к цвету неба
        if (mountains)
        {
            var layers = 3;
            for (var l = 0; l < layers; l++)
            {
                var far = 1 - l / (float)layers;
                var baseCol = winter ? Color.FromArgb(200, 210, 230) : night ? Color.FromArgb(30, 34, 70) : sunset ? Color.FromArgb(90, 60, 110) : Color.FromArgb(70, 100, 130);
                var col = Mix(baseCol, sky[^1], far * 0.55f);
                var top = horizon - h * (0.12f + 0.1f * (layers - l)) ;
                var ridge = Ridge(w, rnd, h * (0.06f + 0.05f * l));
                var pts = new List<PointF> { new(0, horizon + 2) };
                for (var x = 0; x < ridge.Length; x++) pts.Add(new PointF(x * w / (float)(ridge.Length - 1), top + ridge[x]));
                pts.Add(new PointF(w, horizon + 2));
                using var mb = new SolidBrush(col);
                g.FillPolygon(mb, pts.ToArray());
                if (winter || l == 0)
                {
                    // снежные шапки на самых высоких местах
                    using var snow = new SolidBrush(Color.FromArgb(night ? 120 : 200, 245, 248, 255));
                    for (var x = 1; x < ridge.Length - 1; x++)
                        if (ridge[x] < ridge[x - 1] && ridge[x] < ridge[x + 1] && ridge[x] < h * 0.02f)
                        {
                            var px = x * w / (float)(ridge.Length - 1);
                            g.FillPolygon(snow, [new PointF(px, top + ridge[x]), new PointF(px - w * 0.02f, top + ridge[x] + h * 0.04f), new PointF(px + w * 0.02f, top + ridge[x] + h * 0.04f)]);
                        }
                }
            }
        }

        // город силуэтом с огоньками
        if (city)
        {
            var x = 0f;
            var cityCol = night ? Color.FromArgb(18, 20, 40) : sunset ? Color.FromArgb(60, 40, 70) : Color.FromArgb(90, 100, 120);
            using var cb = new SolidBrush(cityCol);
            using var lit = new SolidBrush(Color.FromArgb(night ? 230 : 120, 255, 220, 140));
            while (x < w)
            {
                float bw = w * (0.03f + (float)rnd.NextDouble() * 0.05f), bh = h * (0.08f + (float)rnd.NextDouble() * 0.22f);
                g.FillRectangle(cb, x, horizon - bh, bw, bh + 2);
                if (rnd.NextDouble() < 0.2) g.FillPolygon(cb, [new PointF(x + bw / 2, horizon - bh - h * 0.06f), new PointF(x + bw * 0.3f, horizon - bh), new PointF(x + bw * 0.7f, horizon - bh)]);
                for (var wy = horizon - bh + h * 0.02f; wy < horizon - h * 0.02f; wy += h * 0.035f)
                    for (var wx = x + bw * 0.15f; wx < x + bw * 0.85f; wx += bw * 0.25f)
                        if (rnd.NextDouble() < (night ? 0.55 : 0.2)) g.FillRectangle(lit, wx, wy, bw * 0.1f, h * 0.012f);
                x += bw + w * 0.004f;
            }
        }

        // вода или земля
        if (sea)
        {
            using (var wb = new LinearGradientBrush(new Rectangle(0, horizon, w, h - horizon + 1), Mix(sky[^1], Color.FromArgb(20, 40, 90), 0.35f), Mix(sky[0], Color.FromArgb(5, 15, 45), 0.5f), 90f))
                g.FillRectangle(wb, 0, horizon, w, h - horizon);
            // солнечная (лунная) дорожка — короткие блики, к берегу шире
            for (var i = 0; i < 260; i++)
            {
                var yy = horizon + (float)Math.Pow(rnd.NextDouble(), 1.6) * (h - horizon);
                var spread = sunR * 0.8f + (yy - horizon) * 0.35f;
                var xx = sunX + (float)(rnd.NextDouble() * 2 - 1) * spread;
                var len = w * (0.01f + (float)rnd.NextDouble() * 0.025f) * (1 + (yy - horizon) / (h - horizon));
                using var pen = new Pen(Color.FromArgb(rnd.Next(90, 200), sunCol), Math.Max(1.5f, h * 0.004f));
                g.DrawLine(pen, xx - len / 2, yy, xx + len / 2, yy);
            }
            // волны
            for (var i = 0; i < 140; i++)
            {
                var yy = horizon + (float)rnd.NextDouble() * (h - horizon);
                var xx = (float)rnd.NextDouble() * w;
                var len = w * (0.02f + (float)rnd.NextDouble() * 0.04f);
                using var pen = new Pen(Color.FromArgb(60, 220, 235, 255), Math.Max(1f, h * 0.003f));
                g.DrawArc(pen, xx, yy, len, h * 0.012f, 200, 140);
            }
            if (Has("маяк|lighthouse"))
            {
                // маяк на скале слева
                var bx = w * 0.18f;
                using var rock = new SolidBrush(Color.FromArgb(50, 40, 45));
                g.FillPolygon(rock, [new PointF(0, h), new PointF(0, horizon + h * 0.08f), new PointF(bx - w * 0.06f, horizon - h * 0.01f), new PointF(bx + w * 0.08f, horizon + h * 0.02f), new PointF(bx + w * 0.16f, h)]);
                using var tower = new SolidBrush(Color.FromArgb(240, 236, 228));
                using var red = new SolidBrush(Color.FromArgb(200, 40, 40));
                g.FillPolygon(tower, [new PointF(bx - w * 0.018f, horizon), new PointF(bx + w * 0.018f, horizon), new PointF(bx + w * 0.011f, horizon - h * 0.26f), new PointF(bx - w * 0.011f, horizon - h * 0.26f)]);
                g.FillRectangle(red, bx - w * 0.016f, horizon - h * 0.30f, w * 0.032f, h * 0.045f);
                using var beam = new SolidBrush(Color.FromArgb(night ? 160 : 90, 255, 240, 170));
                g.FillEllipse(beam, bx - w * 0.01f, horizon - h * 0.292f, w * 0.02f, h * 0.03f);
            }
        }
        else
        {
            var ground = winter ? Color.FromArgb(235, 240, 250) : night ? Color.FromArgb(20, 35, 30) : sunset ? Color.FromArgb(90, 80, 50) : Color.FromArgb(90, 150, 70);
            using (var gb = new LinearGradientBrush(new Rectangle(0, horizon, w, h - horizon + 1), Mix(ground, sky[^1], 0.25f), Mix(ground, Color.Black, 0.25f), 90f))
                g.FillRectangle(gb, 0, horizon, w, h - horizon);
            // травинки/цветы переднего плана
            for (var i = 0; i < (winter ? 0 : 500); i++)
            {
                var yy = horizon + (float)Math.Pow(rnd.NextDouble(), 0.7) * (h - horizon);
                var xx = (float)rnd.NextDouble() * w;
                var len = h * 0.01f + (yy - horizon) * 0.05f;
                using var pen = new Pen(Mix(ground, Color.FromArgb(200, 230, 120), (float)rnd.NextDouble() * 0.4f), Math.Max(1f, len * 0.08f));
                g.DrawLine(pen, xx, yy, xx + (float)(rnd.NextDouble() - 0.5) * len * 0.5f, yy - len);
                if (!night && rnd.NextDouble() < 0.06)
                {
                    using var fb = new SolidBrush(new[] { Color.FromArgb(240, 80, 90), Color.FromArgb(250, 220, 90), Color.FromArgb(240, 240, 250), Color.FromArgb(170, 110, 220) }[rnd.Next(4)]);
                    g.FillEllipse(fb, xx - len * 0.12f, yy - len - len * 0.12f, len * 0.24f, len * 0.24f);
                }
            }
        }

        // лес: ёлки силуэтами, ближе — крупнее
        if (forest)
            for (var i = 0; i < 70; i++)
            {
                var depth = (float)rnd.NextDouble();
                var baseY = horizon + depth * (h - horizon) * (sea ? 0.2f : 0.5f) + h * 0.01f;
                var th = h * (0.05f + depth * 0.18f);
                var tx = (float)rnd.NextDouble() * w;
                if (sea && tx > w * 0.3f) continue;
                var tc = Mix(winter ? Color.FromArgb(40, 70, 60) : night ? Color.FromArgb(8, 20, 18) : Color.FromArgb(25, 70, 40), sky[^1], (1 - depth) * 0.35f);
                using var tb = new SolidBrush(tc);
                for (var tier = 0; tier < 3; tier++)
                {
                    var ty = baseY - th * (tier / 3f);
                    var tw = th * (0.45f - tier * 0.1f);
                    g.FillPolygon(tb, [new PointF(tx, ty - th * 0.45f), new PointF(tx - tw, ty), new PointF(tx + tw, ty)]);
                }
            }
        return bmp;
    }

    private static Color Mix(Color a, Color b, float k) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

    /// <summary>Хребет гор: смещение середины, 129 точек; значения — отступ вниз от вершины слоя.</summary>
    private static float[] Ridge(int w, Random rnd, float amp)
    {
        const int n = 129;
        var r = new float[n];
        r[0] = (float)rnd.NextDouble() * amp;
        r[n - 1] = (float)rnd.NextDouble() * amp;
        var a = amp;
        for (var step = n - 1; step > 1; step /= 2)
        {
            for (var i = 0; i + step < n; i += step)
            {
                var m = i + step / 2;
                r[m] = (r[i] + r[i + step]) / 2 + (float)(rnd.NextDouble() - 0.5) * a;
            }
            a *= 0.55f;
        }
        var min = r.Min();
        for (var i = 0; i < n; i++) r[i] -= min;
        return r;
    }
}
