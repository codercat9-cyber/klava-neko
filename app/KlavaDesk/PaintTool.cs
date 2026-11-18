using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace KlavaDesk;

/// <summary>
/// Рисование в Paint (Windows 11) «по-человечески»: Клава не угадывает пиксели по картинке, а работает с холстом
/// через UI Automation. Холст находится сам (элемент image), координаты — в единицах холста: ширина 0..1000,
/// высота 0..(1000·H/W), так круг остаётся кругом.
///  - Живые мазки и фигуры Paint (brush, shape, fill, text) — лапками, инструменты и цвета выбираются по именам.
///  - Точные фигуры (rect, ellipse, polygon, line) рисуются пиксель в пиксель в копии холста и вставляются обратно:
///    ровные края, точный цвет (#RRGGBB или взятый прямо с холста), никаких мягких краёв кисти. Этим же чинится
///    испорченное: закрасить место цветом фона, взятым рядом, и нарисовать заново.
/// После рисования — снимок холста с сеткой координат, чтобы мозг видел, куда попал, и поправил; look — лупа.
/// </summary>
internal static partial class PaintTool
{
    private static readonly Dictionary<string, string> Shapes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["line"] = "Линия", ["линия"] = "Линия", ["curve"] = "Кривая", ["кривая"] = "Кривая",
        ["ellipse"] = "Овал", ["oval"] = "Овал", ["circle"] = "Овал", ["овал"] = "Овал", ["круг"] = "Овал", ["эллипс"] = "Овал",
        ["rect"] = "Прямоугольник", ["rectangle"] = "Прямоугольник", ["square"] = "Прямоугольник", ["прямоугольник"] = "Прямоугольник", ["квадрат"] = "Прямоугольник",
        ["roundrect"] = "Скругленный прямоугольник", ["polygon"] = "Многоугольник", ["многоугольник"] = "Многоугольник",
        ["triangle"] = "Треугольник", ["треугольник"] = "Треугольник", ["righttriangle"] = "Прямоугольный треугольник",
        ["diamond"] = "Ромб", ["rhombus"] = "Ромб", ["ромб"] = "Ромб", ["pentagon"] = "Пятиугольник", ["пятиугольник"] = "Пятиугольник",
        ["hexagon"] = "Шестиугольник", ["шестиугольник"] = "Шестиугольник",
        ["arrow"] = "Стрелка вправо", ["arrowright"] = "Стрелка вправо", ["arrowleft"] = "Стрелка влево", ["arrowup"] = "Стрелка вверх", ["arrowdown"] = "Стрелка вниз",
        ["star"] = "Пятиконечная звезда", ["звезда"] = "Пятиконечная звезда", ["star4"] = "Четырехконечная звезда", ["star6"] = "Шестиконечная звезда",
        ["heart"] = "Сердце", ["сердце"] = "Сердце", ["lightning"] = "Молния", ["молния"] = "Молния",
        ["cloud"] = "Выноска-облако", ["облако"] = "Выноска-облако", ["callout"] = "Овальная выноска",
    };

    // палитра Paint Windows 11 (имена в UI Automation и цвета — для подбора ближайшего к #RRGGBB)
    private static readonly (string Name, Color Rgb)[] Palette =
    [
        ("Черный", Color.FromArgb(0, 0, 0)), ("Серый", Color.FromArgb(127, 127, 127)), ("Темно-красный", Color.FromArgb(136, 0, 21)),
        ("Красный", Color.FromArgb(237, 28, 36)), ("Оранжевый", Color.FromArgb(255, 127, 39)), ("Желтый", Color.FromArgb(255, 242, 0)),
        ("Зеленый", Color.FromArgb(34, 177, 76)), ("Бирюзовый", Color.FromArgb(0, 162, 232)), ("Индиго", Color.FromArgb(63, 72, 204)),
        ("Сиреневый", Color.FromArgb(163, 73, 164)), ("Белый", Color.FromArgb(255, 255, 255)), ("Светло-серый", Color.FromArgb(195, 195, 195)),
        ("Коричневый", Color.FromArgb(185, 122, 87)), ("Розовый", Color.FromArgb(255, 174, 201)), ("Золотистый", Color.FromArgb(255, 201, 14)),
        ("Светло-желтый", Color.FromArgb(239, 228, 176)), ("Салатовый", Color.FromArgb(181, 230, 29)), ("Светло-бирюзовый", Color.FromArgb(153, 217, 234)),
        ("Сизый", Color.FromArgb(112, 146, 190)), ("Бледный розовато-лиловый", Color.FromArgb(200, 191, 231)),
    ];

    private static readonly Dictionary<string, string> ColorWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "Черный", ["черный"] = "Черный", ["чёрный"] = "Черный", ["gray"] = "Серый", ["grey"] = "Серый", ["серый"] = "Серый",
        ["darkred"] = "Темно-красный", ["бордовый"] = "Темно-красный", ["red"] = "Красный", ["красный"] = "Красный",
        ["orange"] = "Оранжевый", ["оранжевый"] = "Оранжевый", ["yellow"] = "Желтый", ["желтый"] = "Желтый", ["жёлтый"] = "Желтый",
        ["green"] = "Зеленый", ["зеленый"] = "Зеленый", ["зелёный"] = "Зеленый", ["cyan"] = "Бирюзовый", ["turquoise"] = "Бирюзовый", ["голубой"] = "Бирюзовый", ["бирюзовый"] = "Бирюзовый",
        ["blue"] = "Индиго", ["синий"] = "Индиго", ["indigo"] = "Индиго", ["purple"] = "Сиреневый", ["violet"] = "Сиреневый", ["фиолетовый"] = "Сиреневый", ["сиреневый"] = "Сиреневый",
        ["white"] = "Белый", ["белый"] = "Белый", ["lightgray"] = "Светло-серый", ["brown"] = "Коричневый", ["коричневый"] = "Коричневый",
        ["pink"] = "Розовый", ["розовый"] = "Розовый", ["gold"] = "Золотистый", ["золотой"] = "Золотистый", ["lime"] = "Салатовый", ["салатовый"] = "Салатовый",
        ["lightblue"] = "Светло-бирюзовый", ["skyblue"] = "Светло-бирюзовый", ["steelblue"] = "Сизый", ["lavender"] = "Бледный розовато-лиловый",
    };

    /// <summary>Операции, которые рисуются точно в копии холста (не мышью).</summary>
    private static readonly HashSet<string> PreciseOps = new(StringComparer.OrdinalIgnoreCase)
        { "rect", "ellipse", "circle", "polygon", "poly", "line" };

    public static string Describe =>
        "Рисование в Paint по холсту (Paint откроется сам). Координаты — единицы холста: x 0..1000 слева направо, y 0..H сверху вниз " +
        "(H скажет вызов без ops; круг остаётся кругом). На присланном снимке — сетка: тонкие линии каждые 50, подписи каждые 100 " +
        "(x сверху, y слева); координаты бери с неё, не на глаз. look:[x,y,w,h] — лупа: увеличенный кусок с мелкой сеткой. ops по порядку:\n" +
        "ТОЧНЫЕ (ровно, пиксель в пиксель, для форм и ПОЧИНКИ): {op:'rect', from:[x,y], to:[x,y], color, outline?, width?}; " +
        "{op:'ellipse', from, to, color, outline?, width?} (круг — квадратная рамка); {op:'polygon', points:[[x,y],…], color, outline?, width?} " +
        "(крыша-треугольник, звезда); {op:'line', points:[[x,y],…], color, width}; {op:'pick', at:[x,y]} — узнать точный цвет в точке. " +
        "color — #RRGGBB, название или [x,y] — взять цвет с холста в этой точке (цвет неба, стены). width — в единицах холста. " +
        "Рисуются в ТЕКУЩИЙ слой; прозрачное на слое остаётся прозрачным.\n" +
        "ЖИВЫЕ (мышью, мягкие края): {op:'brush', kind?, color, size, opacity?, points:[[x,y],…]} — мазок; kind — кисть Paint: " +
        "'кисть', 'каллиграфия', 'перо', 'распылитель', 'масло', 'цветной карандаш', 'маркер', 'текстурный карандаш', 'акварель' " +
        "(без kind — та, что выбрана сейчас: если друг сам выбрал кисть, не указывай); opacity 1..100 — прозрачность мазка. " +
        "{op:'pencil'|'eraser', color, size, points}; {op:'shape', shape:'circle|rect|line|triangle|star|heart|arrow|diamond|hexagon|cloud|lightning', " +
        "from, to, color, size}; {op:'fill', at:[x,y], color} — заливка замкнутой области; {op:'text', at:[x,y], text, color}; " +
        "{op:'clear'} — очистить ТЕКУЩИЙ слой. Цвет живых — точный #RRGGBB (или название).\n" +
        "ПОДПИСЬ: {op:'signature', text?:'Клава', place?:'bottom-right|bottom-left|top-right|top-left|center', size?:0.08 (высота букв — " +
        "доля холста), color?, opacity?:1} — ты расписываешься ОТ РУКИ каллиграфической кистью Paint: имя, росчерк, лапка, на НОВОМ верхнем " +
        "слое — картину не трогает. Картины подписываешь ТОЛЬКО так: инструментом «Текст» не подписывайся никогда, ничего не закрашивай под подпись.\n" +
        "СЛОИ: {op:'layer', action:'add'} — новый слой поверх всех, он становится текущим; {op:'layer', action:'select'|'hide'|'show'|" +
        "'up'|'down'|'duplicate'|'merge'|'delete', layer:'Слой 2'} — выбрать / скрыть / показать / выше / ниже / копия / слить с нижним / " +
        "удалить. Рисуется всегда в текущий слой; список слоёв (сверху вниз, какой текущий) — в ответе. Фон под объектами — отдельным " +
        "слоем снизу, объекты — слоями выше: тогда их можно двигать и чинить, не трогая фон.\n" +
        "ПОЧИНКА: не мажь кистью по кривому — закрой испорченный кусок rect цветом фона, взятым рядом (color:[x,y] точки фона), " +
        "потом нарисуй деталь заново точной фигурой. Возвращает снимок холста с сеткой — сверь с задумкой.";

    /// <summary>Кисти Paint (пункты меню «Кисти») по словам, которыми их называют.</summary>
    private static readonly (string[] Words, string Menu)[] BrushKinds =
    [
        (["акварел", "watercolor", "watercolour"], "Кисть для акварели"),
        (["масл", "oil"], "Кисть для масляных красок"),
        (["каллиграф", "calligraph"], "Каллиграфическая кисть"),
        (["перо", "перьев", "ручк", "pen"], "Перьевая ручка"),
        (["распыл", "аэрограф", "спрей", "airbrush", "spray"], "Распылитель"),
        (["текстур", "natural", "графит"], "Текстурный карандаш"),
        (["цветн", "crayon", "мелок", "пастел"], "Цветной карандаш"),
        (["маркер", "marker"], "Маркер"),
        (["кисть", "обычн", "brush", "default"], "Кисть"),
    ];

    /// <summary>Действия со слоем — пункты его контекстного меню в Paint.</summary>
    private static readonly Dictionary<string, string> LayerMenu = new(StringComparer.OrdinalIgnoreCase)
    {
        ["up"] = "Переместить вверх", ["выше"] = "Переместить вверх", ["вверх"] = "Переместить вверх",
        ["down"] = "Переместить вниз", ["ниже"] = "Переместить вниз", ["вниз"] = "Переместить вниз",
        ["duplicate"] = "Дублировать слой", ["copy"] = "Дублировать слой", ["копия"] = "Дублировать слой", ["дублировать"] = "Дублировать слой",
        ["merge"] = "Объединить вниз", ["merge_down"] = "Объединить вниз", ["слить"] = "Объединить вниз", ["объединить"] = "Объединить вниз",
        ["delete"] = "Удалить слой", ["remove"] = "Удалить слой", ["удалить"] = "Удалить слой",
        ["hide"] = "Скрыть слой", ["скрыть"] = "Скрыть слой",
        ["show"] = "Показать слой", ["показать"] = "Показать слой",
    };

    /// <summary>Холст: видимая часть на экране (куда рисует мышь) и весь элемент image (для перевода в пиксели холста).</summary>
    private readonly record struct Canvas(Rectangle Visible, Rectangle Image);

    public static async Task<ToolResult> RunAsync(JsonObject args, ToolBox box, CancellationToken ct)
    {
        var h = box.Hands;
        var win = await EnsurePaintAsync(ct);
        if (win == IntPtr.Zero) return new ToolResult("Ошибка: Paint не открылся (mspaint не найден).");
        Uia.Focus(win);
        await Task.Delay(250, ct);
        // панель слоёв открыта всегда: холст не съезжает посреди рисования (панель сдвигает его влево),
        // и список слоёв виден в каждом ответе
        await EnsureLayersPanelAsync(win);
        // UI Automation у Paint иногда не отдаёт дерево с первого раза (окно только что вышло вперёд, друг в нём кликал)
        Canvas? canvas = null;
        for (var attempt = 0; attempt < 4 && canvas is null; attempt++)
        {
            if (attempt > 0) { Uia.Focus(win); await Task.Delay(400, ct); }
            canvas = await CanvasAsync(win);
        }
        if (canvas is null) return new ToolResult("Ошибка: не нашла холст Paint (элемент image) — глянь look_screen.");
        var cv = canvas.Value;
        _hideSelf = box.HideSelfFromCapture;
        var c = cv.Visible;
        var H = (int)Math.Round(1000.0 * c.Height / c.Width);
        var ops = args["ops"] as JsonArray ?? (args["ops"] is JsonValue v && v.TryGetValue<string>(out var s) ? JsonNode.Parse(s) as JsonArray : null);
        var look = Nums(args["look"]);
        var log = new StringBuilder($"Холст {c.Width}×{c.Height} пикселей на экране — координаты x 0..1000, y 0..{H}.\n");
        if (ops is null || ops.Count == 0)
            return new ToolResult(log + await LayersSummaryAsync(win) + "Команд рисования не было — вот холст сейчас.", await SnapshotAsync(c, H, look, log));

        // единицы холста → точка экрана (дробная — для точных фигур)
        (double X, double Y) U(JsonNode? n)
        {
            var a = Nums(n);
            var x = a.Length >= 2 ? a[0] : 0;
            var y = a.Length >= 2 ? a[1] : 0;
            return (Math.Clamp(x, 0, 1000), Math.Clamp(y, 0, H));
        }
        PointF ScreenF((double X, double Y) u) => new((float)(c.Left + u.X / 1000.0 * c.Width), (float)(c.Top + u.Y / H * c.Height));
        Point P(JsonNode? n) { var f = ScreenF(U(n)); return new Point((int)Math.Round(f.X), (int)Math.Round(f.Y)); }

        var done = 0;
        for (var i = 0; i < ops.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (h.Token.IsCancellationRequested) { log.AppendLine("Друг забрал мышь — остановилась."); break; }
            if (ops[i] is not JsonObject op) continue;
            var kind = (op["op"]?.GetValue<string>() ?? "brush").ToLowerInvariant();
            var color = op["color"] is JsonValue cvv && cvv.TryGetValue<string>(out var cs) ? cs : null;
            var size = op["size"]?.GetValue<double>();
            try
            {
                // подряд идущие точные фигуры — одной вставкой: один круг «скопировал холст → нарисовал → вставил»
                if (PreciseOps.Contains(kind))
                {
                    var batch = new List<JsonObject>();
                    while (i < ops.Count && ops[i] is JsonObject po && PreciseOps.Contains(po["op"]?.GetValue<string>() ?? ""))
                    {
                        batch.Add(po);
                        i++;
                    }
                    i--;
                    var made = await PreciseAsync(win, cv, H, batch, U, ScreenF, h, log);
                    done += made;
                    continue;
                }
                switch (kind)
                {
                    case "pick":
                    {
                        var at = U(op["at"]);
                        var px = ScreenPixel(ScreenF(at));
                        log.AppendLine($"цвет в ({at.X:F0},{at.Y:F0}): {Hex(px)} (в палитре ближе всего «{Nearest(px)}»)");
                        break;
                    }
                    case "clear":
                        await FocusCanvasAsync(win, cv, h);
                        await h.PressAsync([0x11, 0x41], h.Token);          // Ctrl+A — весь текущий слой
                        await Task.Delay(150, h.Token);
                        await h.PressAsync([0x2E], h.Token);                // Delete
                        await Task.Delay(200, h.Token);
                        await SelectAsync(win, "Кисти", h);
                        log.AppendLine("очистила текущий слой");
                        break;
                    case "signature" or "подпись" or "вензель" or "watermark" or "монограмма":
                        if (!await SignatureAsync(win, cv, op, h, log)) continue;
                        break;
                    case "layer" or "слой":
                    {
                        var action = (op["action"]?.GetValue<string>() ?? "add").Trim().ToLowerInvariant();
                        var name = op["layer"] is JsonValue lv ? lv.ToString() : op["name"]?.ToString();
                        var res = await LayerAsync(win, action, name, h);
                        log.AppendLine(res.Message);
                        if (!res.Ok) continue;
                        break;
                    }
                    case "brush" or "pencil" or "eraser" or "кисть" or "карандаш" or "ластик":
                    {
                        var tool = kind switch { "pencil" or "карандаш" => "Карандаш", "eraser" or "ластик" => "Ластик", _ => "Кисти" };
                        var brushName = "";
                        if (tool == "Кисти" && op["kind"]?.ToString() is { Length: > 0 } want)
                        {
                            var menu = BrushKinds.FirstOrDefault(b => b.Words.Any(w => want.Contains(w, StringComparison.OrdinalIgnoreCase))).Menu;
                            if (menu is null) log.AppendLine($"кисти «{want}» в Paint нет — рисую той, что выбрана");
                            else if (await BrushKindAsync(win, menu, h)) brushName = menu;
                            else log.AppendLine($"[НЕ СРАБОТАЛО] не смогла выбрать «{menu}» — рисую той кистью, что выбрана");
                        }
                        if (brushName.Length == 0) await SelectAsync(win, tool, h);
                        if (color is not null && tool != "Ластик") log.Append(await ColorAsync(win, color, h));
                        if (size is not null) await SliderAsync(win, "Размер", size.Value);
                        if (op["opacity"] is not null && Nums(op["opacity"]) is [var opacity, ..]) await SliderAsync(win, "Прозрачность", opacity);
                        var pts = (op["points"] as JsonArray ?? []).Select(P).ToList();
                        if (pts.Count == 1) pts.Add(pts[0] with { X = pts[0].X + 1 });
                        if (pts.Count < 2) { log.AppendLine($"{kind}: нет точек"); continue; }
                        await h.DrawStrokeAsync(Dense(pts, 4), MouseButtons.Left, 3, h.Token);
                        // последний отпечаток кисти Paint закрепляет только после следующего движения мыши — иначе он
                        // проявлялся позже, поверх того, что нарисовано следом (точные фигуры его не закрывали)
                        await h.MoveAsync(new Point(c.Left - 12, c.Top + c.Height / 2), h.Token);
                        await Task.Delay(150, h.Token);
                        log.AppendLine($"{(brushName.Length > 0 ? brushName.ToLowerInvariant() : tool.ToLowerInvariant())}: мазок по {pts.Count} точкам{(color is not null ? $", {color}" : "")}");
                        break;
                    }
                    case "shape":
                    {
                        var sh = op["shape"]?.GetValue<string>() ?? "ellipse";
                        var name = Shapes.TryGetValue(sh.Replace(" ", "").Replace("_", ""), out var n) ? n : sh;
                        if (!await SelectAsync(win, name, h)) { log.AppendLine($"фигура «{sh}»: нет такой в Paint"); continue; }
                        if (color is not null) log.Append(await ColorAsync(win, color, h));
                        if (size is not null) await SliderAsync(win, "Размер", size.Value);
                        await h.DragAsync(P(op["from"]), P(op["to"]), h.Token);
                        await Task.Delay(150, h.Token);
                        await SelectAsync(win, "Кисти", h);   // смена инструмента закрепляет фигуру на холсте
                        log.AppendLine($"фигура {name}{(color is not null ? $", {color}" : "")}");
                        break;
                    }
                    case "fill" or "заливка":
                    {
                        await SelectAsync(win, "Заливка", h);
                        if (color is not null) log.Append(await ColorAsync(win, color, h));
                        await h.ClickAtAsync(P(op["at"]), MouseButtons.Left, 1, h.Token);
                        log.AppendLine($"залила{(color is not null ? $" {color}" : "")}");
                        break;
                    }
                    case "text" or "текст":
                    {
                        var text = op["text"]?.GetValue<string>() ?? "";
                        if (text.Length == 0) continue;
                        // подпись текстом — нет: друг 30.09 «картины подписывай вручную кисточкой»
                        if (IsSignatureText(text))
                        {
                            var at = U(op["at"]);
                            var side = at.X < 500 ? "left" : "right";   // холст в единицах 0..1000 × 0..H
                            var corner = at.Y < H / 2.0 ? $"top-{side}" : $"bottom-{side}";
                            log.AppendLine($"«{text}» — это подпись: ставлю её рукой кистью, не текстом");
                            if (!await SignatureAsync(win, cv, new JsonObject { ["text"] = text, ["place"] = corner, ["color"] = color }, h, log)) continue;
                            break;
                        }
                        await SelectAsync(win, "Текст", h);
                        if (color is not null) log.Append(await ColorAsync(win, color, h));
                        await h.ClickAtAsync(P(op["at"]), MouseButtons.Left, 1, h.Token);
                        await Task.Delay(300, h.Token);
                        await h.TypeAsync(text, h.Token);
                        await Task.Delay(150, h.Token);
                        await SelectAsync(win, "Кисти", h);   // закрепить текст
                        log.AppendLine($"текст «{text}»");
                        break;
                    }
                    default:
                        log.AppendLine($"неизвестная операция «{kind}»");
                        continue;
                }
                done++;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { log.AppendLine("Друг забрал мышь — остановилась."); break; }
        }
        await Task.Delay(200, CancellationToken.None);
        log.Append(await LayersSummaryAsync(win));
        log.Append($"Готово операций: {done} из {ops.Count}. Снимок холста с сеткой приложен — сверь с задумкой.");
        return new ToolResult(log.ToString(), await SnapshotAsync(c, H, look, log));
    }

    // ---------------- точные фигуры: копия холста → рисунок → вставка ----------------

    /// <summary>
    /// Ctrl+A, Ctrl+C — весь ТЕКУЩИЙ слой в буфер (PNG с альфа-каналом: пустое на слое прозрачно); фигуры рисуются
    /// в этой копии по тем же единицам, что и мышь; копия вставляется обратно тем же PNG (Ctrl+V встаёт в левый верхний
    /// угол, прокрутка заранее сброшена) и закрепляется сменой инструмента. Прозрачное остаётся прозрачным — нижние
    /// слои не заливает белым. Потом проверка по экрану в точках, где картинка изменилась, — иначе Ctrl+Z и честное
    /// «не легло». Буфер обмена друга сохраняется и возвращается.
    /// </summary>
    private static async Task<int> PreciseAsync(IntPtr win, Canvas cv, int H, List<JsonObject> batch,
        Func<JsonNode?, (double X, double Y)> U, Func<(double X, double Y), PointF> screenF, Hands h, StringBuilder log)
    {
        var backup = Sta(BackupClipboard);
        Bitmap? bmp = null;
        try
        {
            (bmp, var kx) = await CopyLayerAsync(win, cv, h);
            if (bmp is null)
            {
                log.AppendLine("[НЕ СРАБОТАЛО] точные фигуры: Paint не отдал холст в буфер обмена (Ctrl+A, Ctrl+C) — ничего не нарисовано.");
                return 0;
            }
            var ky = kx;
            PointF Px((double X, double Y) u)
            {
                var sp = screenF(u);
                return new PointF((float)((sp.X - cv.Image.Left) * kx), (float)((sp.Y - cv.Image.Top) * ky));
            }
            var unitPx = cv.Visible.Width / 1000.0 * kx;   // одна единица холста в пикселях картинки
            // цвет с холста — то, что видно (все слои вместе): на текущем слое в этой точке может быть пусто
            Color Sample((double X, double Y) u)
            {
                var p = Px(u);
                var own = bmp.GetPixel(Math.Clamp((int)p.X, 0, bmp.Width - 1), Math.Clamp((int)p.Y, 0, bmp.Height - 1));
                var sp = screenF(u);
                return own.A >= 250 || !cv.Visible.Contains(Point.Round(sp)) ? Color.FromArgb(own.R, own.G, own.B) : ScreenPixel(sp);
            }
            Color? ColorOf(JsonNode? n)
            {
                if (n is null) return null;
                if (n is JsonArray) return Sample(U(n));
                return n is JsonValue jv && jv.TryGetValue<string>(out var s) ? ExactColor(s) : null;
            }

            // точки для проверки вставки: центры нарисованного (сверяются потом с ИТОГОВОЙ картинкой, а не с цветом фигуры —
            // следующая фигура в пачке может перекрыть предыдущую)
            var probes = new List<PointF>();
            using var original = (Bitmap)bmp.Clone();
            var made = 0;
            using (var g = Graphics.FromImage(bmp))
            {
                foreach (var op in batch)
                {
                    var kind = op["op"]!.GetValue<string>().ToLowerInvariant();
                    var fill = ColorOf(op["color"]);
                    var outline = ColorOf(op["outline"]);
                    var width = Math.Max(0.5, (op["width"]?.GetValue<double>() ?? (kind == "line" ? 3 : 2)) * unitPx);
                    var smooth = op["smooth"]?.GetValue<bool>() ?? kind != "rect";
                    g.SmoothingMode = smooth ? SmoothingMode.AntiAlias : SmoothingMode.None;
                    g.PixelOffsetMode = smooth ? PixelOffsetMode.HighQuality : PixelOffsetMode.None;
                    switch (kind)
                    {
                        case "rect" or "ellipse" or "circle":
                        {
                            var a = Px(U(op["from"]));
                            var b = Px(U(op["to"]));
                            var r = RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
                            if (r.Width < 1 || r.Height < 1) { log.AppendLine($"{kind}: пустая рамка — пропустила"); continue; }
                            if (kind == "rect")
                            {
                                if (fill is { } f) { using var br = new SolidBrush(f); g.FillRectangle(br, r); }
                                if (outline is { } o) { using var pen = new Pen(o, (float)width); g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height); }
                            }
                            else
                            {
                                if (fill is { } f) { using var br = new SolidBrush(f); g.FillEllipse(br, r); }
                                if (outline is { } o) { using var pen = new Pen(o, (float)width); g.DrawEllipse(pen, r); }
                            }
                            if (fill is not null && r.Width > 6 && r.Height > 6) probes.Add(new PointF(r.X + r.Width / 2, r.Y + r.Height / 2));
                            log.AppendLine($"{(kind == "rect" ? "прямоугольник" : "эллипс")} точно{(fill is { } f2 ? $", заливка {Hex(f2)}" : "")}{(outline is { } o2 ? $", контур {Hex(o2)}" : "")}");
                            break;
                        }
                        case "polygon" or "poly":
                        {
                            var pts = (op["points"] as JsonArray ?? []).Select(n => Px(U(n))).ToArray();
                            if (pts.Length < 3) { log.AppendLine("polygon: нужно хотя бы 3 точки"); continue; }
                            if (fill is { } f) { using var br = new SolidBrush(f); g.FillPolygon(br, pts); }
                            if (outline is { } o) { using var pen = new Pen(o, (float)width) { LineJoin = LineJoin.Round }; g.DrawPolygon(pen, pts); }
                            if (fill is not null)
                            {
                                var cxp = new PointF(pts.Average(p => p.X), pts.Average(p => p.Y));
                                if (Inside(pts, cxp)) probes.Add(cxp);
                            }
                            log.AppendLine($"многоугольник по {pts.Length} точкам точно{(fill is { } f2 ? $", заливка {Hex(f2)}" : "")}");
                            break;
                        }
                        case "line":
                        {
                            var pts = (op["points"] as JsonArray ?? []).Select(n => Px(U(n))).ToArray();
                            if (pts.Length < 2) { log.AppendLine("line: нужно хотя бы 2 точки"); continue; }
                            var lc = fill ?? outline ?? Color.Black;
                            using var pen = new Pen(lc, (float)width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
                            g.DrawLines(pen, pts);
                            probes.Add(new PointF((pts[0].X + pts[1].X) / 2, (pts[0].Y + pts[1].Y) / 2));
                            log.AppendLine($"линия по {pts.Length} точкам точно, {Hex(lc)}, толщина {op["width"]?.GetValue<double>() ?? 3:F0}");
                            break;
                        }
                    }
                    made++;
                }
            }
            if (made == 0) return 0;

            await PasteLayerAsync(win, bmp, h);

            // проверка: легла ли вставка ровно — в точках, где картинка реально изменилась, экран должен совпасть с итогом
            var checks = new List<(PointF Screen, Color Want)>();
            foreach (var pp in probes)
            {
                var ix = Math.Clamp((int)pp.X, 0, bmp.Width - 1);
                var iy = Math.Clamp((int)pp.Y, 0, bmp.Height - 1);
                var want = bmp.GetPixel(ix, iy);
                var was = original.GetPixel(ix, iy);
                if (want.A < 250 || was.A >= 250 && Dist(want, was) < 30) continue;   // тут ничего не поменялось — не показатель
                var screen = new PointF((float)(cv.Image.Left + (ix + 0.5) / kx), (float)(cv.Image.Top + (iy + 0.5) / ky));
                if (cv.Visible.Contains(Point.Round(screen))) checks.Add((screen, want));
            }
            if (checks.Count > 0)
            {
                var bad = checks.Select(c => (c.Want, Got: ScreenPixel(c.Screen))).Where(c => Dist(c.Got, c.Want) > 60).ToList();
                if (bad.Count * 2 > checks.Count)
                {
                    await h.PressAsync([0x11, 0x5A], h.Token);   // Ctrl+Z
                    log.AppendLine($"[НЕ СРАБОТАЛО] точные фигуры: вставка легла не туда ({bad.Count} из {checks.Count} проверочных точек: ждала {Hex(bad[0].Want)}, на экране {Hex(bad[0].Got)}) — отменила Ctrl+Z.");
                    return 0;
                }
            }
            return made;
        }
        finally
        {
            bmp?.Dispose();
            Sta(() => { RestoreClipboard(backup); return 0; });
        }
    }

    private static bool Inside(PointF[] poly, PointF p)
    {
        var inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y) &&
                p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        return inside;
    }

    private static double Dist(Color a, Color b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Надпись, которая на деле — подпись под картиной: её имя (Клава, Klava, «© Клава», «Клава 2026»…).</summary>
    internal static bool IsSignatureText(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        return t.Length <= 28 && (t.Contains("клав") || t.Contains("klava") || t.StartsWith('©') || t.Contains("подпись"));
    }

    private static string Nearest(Color c) => Palette.MinBy(p => Dist(p.Rgb, c)).Name;

    /// <summary>Точный цвет для точных фигур: #RRGGBB как есть, название — цвет палитры Paint; «none» — без цвета.</summary>
    private static Color? ExactColor(string s)
    {
        var t = s.Trim();
        if (t.Length == 0 || t.Equals("none", StringComparison.OrdinalIgnoreCase) || t == "нет") return null;
        if (t.StartsWith('#') && t.Length == 7 && int.TryParse(t[1..], System.Globalization.NumberStyles.HexNumber, null, out var hex))
            return Color.FromArgb((hex >> 16) & 255, (hex >> 8) & 255, hex & 255);
        var name = ColorWords.TryGetValue(t.Replace(" ", "").Replace("-", ""), out var mapped) ? mapped : t;
        foreach (var p in Palette) if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Rgb;
        var known = Color.FromName(t.Replace(" ", ""));
        return known.IsKnownColor ? Color.FromArgb(known.R, known.G, known.B) : null;
    }

    /// <summary>Спрятать окно Клавы из захвата (из ToolBox): она стоит поверх холста, а снимать нужно то, что под ней.</summary>
    private static Action<bool>? _hideSelf;

    /// <summary>Захват экрана без окна Клавы.</summary>
    private static T Unseen<T>(Func<T> capture)
    {
        var hide = _hideSelf;
        if (hide is null) return capture();
        hide(true);
        try
        {
            Thread.Sleep(40);   // исключение из захвата вступает со следующего кадра DWM
            return capture();
        }
        finally { hide(false); }
    }

    private static Bitmap CaptureScreen(Rectangle r)
    {
        var b = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(b)) g.CopyFromScreen(r.Location, Point.Empty, b.Size);
        return b;
    }

    private static Color ScreenPixel(PointF p) => Unseen(() =>
    {
        using var b = CaptureScreen(new Rectangle((int)p.X, (int)p.Y, 1, 1));
        return b.GetPixel(0, 0);
    });

    /// <summary>
    /// Доля несовпадений копии холста с тем, что Paint показывает на экране (сетка точек видимой части, масштаб 1:1).
    /// Paint дорисовывает последний мазок с задержкой — копия, снятая раньше, затёрла бы его вставкой.
    /// </summary>
    private static double CopyMismatch(Bitmap bmp, Canvas cv)
    {
        using var scr = Unseen(() => CaptureScreen(cv.Visible));
        int total = 0, bad = 0;
        for (var y = 2; y < scr.Height - 2; y += 17)
            for (var x = 2; x < scr.Width - 2; x += 17)
            {
                var bx = cv.Visible.Left + x - cv.Image.Left;
                var by = cv.Visible.Top + y - cv.Image.Top;
                if (bx < 0 || by < 0 || bx >= bmp.Width || by >= bmp.Height) continue;
                var own = bmp.GetPixel(bx, by);
                if (own.A < 250) continue;   // пусто на слое — на экране там нижние слои, сравнивать не с чем
                total++;
                if (Dist(scr.GetPixel(x, y), own) > 30) bad++;
            }
        return total < 20 ? 0 : bad / (double)total;
    }

    private static double[] Nums(JsonNode? n)
    {
        // числа из JSON модели и числа, созданные кодом (JsonValue<int>), читаются одинаково — через текст
        static double Num(JsonNode? x) =>
            x is not null && double.TryParse(x.ToJsonString().Trim('"'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        if (n is JsonArray a) return a.Select(Num).ToArray();
        if (n is JsonObject o) return [Num(o["x"]), Num(o["y"])];
        if (n is JsonValue js && js.TryGetValue<string>(out var str))
        {
            try { return Nums(JsonNode.Parse(str)); }
            catch (System.Text.Json.JsonException) { return []; }
        }
        return [];
    }

    // ---------------- буфер обмена (только в STA-потоке) ----------------

    private static T Sta<T>(Func<T> f)
    {
        T result = default!;
        ExceptionDispatchInfo? err = null;
        var t = new Thread(() =>
        {
            try { result = f(); }
            catch (Exception ex) { err = ExceptionDispatchInfo.Capture(ex); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        if (!t.Join(TimeSpan.FromSeconds(15))) throw new TimeoutException("буфер обмена не ответил");
        err?.Throw();
        return result;
    }

    private sealed record ClipBackup(string? Text, Image? Picture, System.Collections.Specialized.StringCollection? Files);

    private static ClipBackup BackupClipboard()
    {
        try
        {
            if (Clipboard.ContainsText()) return new ClipBackup(Clipboard.GetText(), null, null);
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img) return new ClipBackup(null, (Image)img.Clone(), null);
            if (Clipboard.ContainsFileDropList()) return new ClipBackup(null, null, Clipboard.GetFileDropList());
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
        return new ClipBackup(null, null, null);
    }

    private static void RestoreClipboard(ClipBackup b)
    {
        try
        {
            if (b.Text is not null) Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, b.Text), true, 10, 50);
            else if (b.Picture is not null) Clipboard.SetDataObject(new DataObject(DataFormats.Bitmap, b.Picture), true, 10, 50);
            else if (b.Files is not null) Clipboard.SetFileDropList(b.Files);
            else Clipboard.Clear();
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
        finally { b.Picture?.Dispose(); }
    }

    private static Bitmap? WaitClipboardImage(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            try
            {
                // PNG с альфа-каналом: на слое пустое прозрачно. Плоская картинка (DIB) отдаёт его белым — вставка
                // такой копии заливала белым всё, что ниже текущего слоя («альфа-канал на белый»)
                if (Clipboard.GetData("PNG") is MemoryStream png)
                {
                    using var src = new Bitmap(png);
                    var copy = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(copy)) { g.CompositingMode = CompositingMode.SourceCopy; g.DrawImageUnscaled(src, 0, 0); }
                    return copy;
                }
                if (Clipboard.ContainsImage() && Clipboard.GetImage() is Bitmap img)
                {
                    var copy = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(copy)) g.DrawImageUnscaled(img, 0, 0);
                    img.Dispose();
                    return copy;
                }
            }
            catch (System.Runtime.InteropServices.ExternalException) { }
            Thread.Sleep(60);
        }
        return null;
    }

    /// <summary>Только PNG: Paint берёт его с альфа-каналом (проверено на слоях); плоский DIB превратил бы прозрачное в чёрное.</summary>
    private static void SetClipboardImage(Bitmap bmp)
    {
        var data = new DataObject();
        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        data.SetData("PNG", false, new MemoryStream(png.ToArray()));
        Clipboard.SetDataObject(data, true, 10, 60);
    }

    // ---------------- Paint: окно, холст, инструменты ----------------

    /// <summary>Рисовать только в этом окне (проверка --paint-test на своём холсте, не трогая рисунок друга).</summary>
    internal static IntPtr TargetWindow { get; set; }

    /// <summary>Окно Paint: уже открытое или новое.</summary>
    private static async Task<IntPtr> EnsurePaintAsync(CancellationToken ct)
    {
        if (TargetWindow != IntPtr.Zero) return TargetWindow;
        // окно её последней картины (своё, открыто paint_art) — дорисовка идёт туда, а не в окно друга
        if (LastArtWindow != IntPtr.Zero && Native.IsWindowVisible(LastArtWindow)) return LastArtWindow;
        IntPtr Find()
        {
            foreach (var p in Process.GetProcessesByName("mspaint"))
                using (p) if (p.MainWindowHandle != IntPtr.Zero) return p.MainWindowHandle;
            return IntPtr.Zero;
        }
        var w = Find();
        if (w != IntPtr.Zero) return w;
        try { Process.Start(new ProcessStartInfo("mspaint.exe") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { return IntPtr.Zero; }
        for (var i = 0; i < 40 && w == IntPtr.Zero; i++) { await Task.Delay(250, ct); w = Find(); }
        if (w != IntPtr.Zero) await Task.Delay(1200, ct);   // холсту нужно дорисоваться
        return w;
    }

    /// <summary>
    /// Холст: видимая часть на экране (элемент image, обрезанный областью прокрутки) и весь элемент. Прокрутка сперва
    /// сбрасывается в начало — тогда вставка из буфера (она встаёт в левый верхний угол видимого) попадает в угол холста.
    /// </summary>
    private static Task<Canvas?> CanvasAsync(IntPtr win) => Task.Run<Canvas?>(() =>
    {
        try
        {
            var root = AutomationElement.FromHandle(win);
            var sv = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "scrollViewer"));
            if (sv?.TryGetCurrentPattern(ScrollPattern.Pattern, out var sp) == true)
            {
                var scroll = (ScrollPattern)sp;
                var hs = scroll.Current.HorizontallyScrollable && scroll.Current.HorizontalScrollPercent > 0 ? 0 : ScrollPattern.NoScroll;
                var vs = scroll.Current.VerticallyScrollable && scroll.Current.VerticalScrollPercent > 0 ? 0 : ScrollPattern.NoScroll;
                if (hs == 0 || vs == 0) { scroll.SetScrollPercent(hs, vs); Thread.Sleep(200); }
            }
            var img = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "image"));
            if (img is null) return null;
            var r = img.Current.BoundingRectangle;
            var full = new Rectangle((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
            var vis = full;
            if (sv is not null)
            {
                var v = sv.Current.BoundingRectangle;
                vis.Intersect(new Rectangle((int)v.X, (int)v.Y, (int)v.Width - 14, (int)v.Height - 14));   // без полос прокрутки
            }
            vis.Inflate(-3, -3);   // край холста — ручки изменения размера, туда не рисуем
            return vis.Width > 20 && vis.Height > 20 && full.Width > 0 && full.Height > 0 ? new Canvas(vis, full) : null;
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return null; }
    });

    /// <summary>Выбрать инструмент/фигуру/цвет по имени: без мыши, если элемент умеет; иначе клик лапкой в центр.</summary>
    private static async Task<bool> SelectAsync(IntPtr win, string name, Hands h)
    {
        var el = await Uia.FindElementAsync(win, name, TimeSpan.FromSeconds(4));
        if (el is null) return false;
        if (!el.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && !el.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase)) return false;
        if (!await Uia.InvokeAsync(el, TimeSpan.FromSeconds(3)))
            await h.ClickAtAsync(new Point(el.Bounds.Left + el.Bounds.Width / 2, el.Bounds.Top + el.Bounds.Height / 2), MouseButtons.Left, 1, h.Token);
        await Task.Delay(120, h.Token);
        return true;
    }

    /// <summary>
    /// Цвет 1 (рисования) для живых инструментов. Название палитры — её клеточкой; #RRGGBB — точно, через окно
    /// «Изменить цвета»: поле HEX. Внутри этого окна UI Automation трогать нельзя — обход его дерева вешает Paint
    /// насмерть (проверено), поэтому туда только клавиши: Tab от спектра до поля HEX, проверка фокуса по
    /// AutomationId, значение, Tab/Shift+Tab (поле принимает), Enter (OK). Возвращает строку для журнала (пусто — всё ок).
    /// </summary>
    private static async Task<string> ColorAsync(IntPtr win, string color, Hands h)
    {
        var want = color.Trim();
        await SelectFirstColorSlotAsync(win);
        if (ColorWords.TryGetValue(want.Replace(" ", "").Replace("-", ""), out var mapped)) want = mapped;
        if (!(want.StartsWith('#') && want.Length == 7 && int.TryParse(want[1..], System.Globalization.NumberStyles.HexNumber, null, out var hex)))
            return await PaletteAsync(win, want, h) ? "" : $"цвета «{color}» в палитре нет — рисую прежним\n";
        var rgb = Color.FromArgb((hex >> 16) & 255, (hex >> 8) & 255, hex & 255);
        var near = Palette.MinBy(p => Dist(p.Rgb, rgb));
        if (Dist(near.Rgb, rgb) < 6) return await PaletteAsync(win, near.Name, h) ? "" : $"цвет {want}: палитра не нажалась\n";

        var btn = await Uia.FindElementAsync(win, "Изменить цвета", TimeSpan.FromSeconds(4));
        if (btn is null || !await Uia.InvokeAsync(btn, TimeSpan.FromSeconds(3)))
            return await PaletteAsync(win, near.Name, h) ? $"точный цвет {want} не вышел (нет кнопки «Изменить цвета») — взяла ближайший «{near.Name}»\n" : "";
        await Task.Delay(700, h.Token);
        string? id = null;
        for (var tab = 0; tab < 4 && id != "HexTextBox"; tab++)
        {
            await h.PressAsync([0x09], h.Token);
            await Task.Delay(250, h.Token);
            id = await Task.Run(() => Uia.FocusedAutomationId(TimeSpan.FromSeconds(2)));
        }
        if (id != "HexTextBox")
        {
            await h.PressAsync([0x1B], h.Token);   // закрыть окно, ничего не меняя
            await Task.Delay(300, h.Token);
            return await PaletteAsync(win, near.Name, h) ? $"точный цвет {want} не вышел (поле HEX не нашлось) — взяла ближайший «{near.Name}»\n" : "";
        }
        if (!await Task.Run(() => Uia.SetFocusedValue(want.ToUpperInvariant(), TimeSpan.FromSeconds(2))))
        {
            await h.PressAsync([0x11, 0x41], h.Token);
            await h.TypeAsync(want.ToUpperInvariant(), h.Token);
        }
        await h.PressAsync([0x09], h.Token);                 // поле принимает значение, когда теряет фокус
        await Task.Delay(200, h.Token);
        await h.PressAsync([0x10, 0x09], h.Token);           // назад в поле: Enter там = «OK»
        await Task.Delay(200, h.Token);
        await h.PressAsync([0x0D], h.Token);
        await Task.Delay(500, h.Token);
        return "";
    }

    /// <summary>Рисуют «Цветом 1»: если друг переключился на «Цвет 2», выбор ушёл бы туда.</summary>
    private static Task SelectFirstColorSlotAsync(IntPtr win) => Task.Run(() =>
    {
        try
        {
            var root = AutomationElement.FromHandle(win);
            foreach (AutomationElement rb in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.RadioButton)))
            {
                if (!rb.Current.Name.StartsWith("Цвет 1", StringComparison.OrdinalIgnoreCase)) continue;
                if (rb.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp) && !((SelectionItemPattern)sp).Current.IsSelected)
                    ((SelectionItemPattern)sp).Select();
                return;
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    });

    /// <summary>Клеточка палитры по точному имени: Invoke, иначе клик в центр.</summary>
    private static async Task<bool> PaletteAsync(IntPtr win, string name, Hands h)
    {
        var el = await Uia.FindElementAsync(win, name, TimeSpan.FromSeconds(4));
        if (el is null || el.Name.StartsWith("Цвет", StringComparison.OrdinalIgnoreCase) || !el.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return false;
        if (!await Uia.InvokeAsync(el, TimeSpan.FromSeconds(3)))
            await h.ClickAtAsync(new Point(el.Bounds.Left + el.Bounds.Width / 2, el.Bounds.Top + el.Bounds.Height / 2), MouseButtons.Left, 1, h.Token);
        await Task.Delay(120, h.Token);
        return true;
    }

    /// <summary>Ползунок слева («Размер», «Прозрачность»): RangeValue, значение в его пределах.</summary>
    private static Task SliderAsync(IntPtr win, string name, double value) => Task.Run(() =>
    {
        try
        {
            var root = AutomationElement.FromHandle(win);
            var cond = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider),
                                        new PropertyCondition(AutomationElement.NameProperty, name));
            var slider = root.FindFirst(TreeScope.Descendants, cond);
            if (slider?.TryGetCurrentPattern(RangeValuePattern.Pattern, out var rp) == true)
            {
                var r = (RangeValuePattern)rp;
                r.SetValue(Math.Clamp(value, r.Current.Minimum, r.Current.Maximum));
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException) { }
    });

    /// <summary>Кисть из меню «Кисти» (акварель, масло, маркер…): раскрыть список и нажать пункт.</summary>
    private static async Task<bool> BrushKindAsync(IntPtr win, string menu, Hands h)
    {
        var opened = await Task.Run(() =>
        {
            try
            {
                var root = AutomationElement.FromHandle(win);
                var split = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "BrushesSplitButton"));
                if (split?.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ep) != true) return false;
                ((ExpandCollapsePattern)ep).Expand();
                return true;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
        });
        if (!opened) return false;
        await Task.Delay(450, h.Token);
        var ok = await PopupMenuItemAsync(win, menu);
        if (!ok) await h.PressAsync([0x1B], h.Token);
        await Task.Delay(250, h.Token);
        return ok;
    }

    /// <summary>
    /// Пункт всплывающего меню Paint (меню кистей, меню слоя): оно живёт в отдельном окне PopupHost внутри окна Paint,
    /// обычный обход дерева туда не заходит — ищем прямо в нём.
    /// </summary>
    private static Task<bool> PopupMenuItemAsync(IntPtr win, string item) => Task.Run(() =>
    {
        try
        {
            var root = AutomationElement.FromHandle(win);
            foreach (AutomationElement pop in root.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.NameProperty, "PopupHost")))
            {
                var cond = new AndCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                                            new PropertyCondition(AutomationElement.NameProperty, item));
                if (pop.FindFirst(TreeScope.Descendants, cond) is { } mi && mi.TryGetCurrentPattern(InvokePattern.Pattern, out var ip))
                {
                    ((InvokePattern)ip).Invoke();
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        return false;
    });

    /// <summary>
    /// Фокус клавиатуры на холст, ничего не нарисовав: инструмент «Выбрать» и щелчок в угол видимого холста.
    /// Без этого Ctrl+A после окна цвета или меню слоя уходил в ленту и копировать было нечего.
    /// </summary>
    private static async Task FocusCanvasAsync(IntPtr win, Canvas cv, Hands h)
    {
        if (!await SelectAsync(win, "Выбрать", h)) return;
        await h.ClickAtAsync(new Point(cv.Visible.Left + 3, cv.Visible.Top + 3), MouseButtons.Left, 1, h.Token);
        await Task.Delay(150, h.Token);
    }

    // ---------------- слои ----------------

    private sealed record LayerItem(string Name, bool Selected, bool Hidden, AutomationElement Element);

    /// <summary>Открыть панель слоёв, если закрыта (кнопка в группе «Слои» без своего имени — переключатель).</summary>
    private static Task EnsureLayersPanelAsync(IntPtr win) => Task.Run(() =>
    {
        try
        {
            var root = AutomationElement.FromHandle(win);
            if (root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "layersList")) is not null) return;
            var group = root.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group), new PropertyCondition(AutomationElement.NameProperty, "Слои")));
            var toggle = group?.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            if (toggle?.TryGetCurrentPattern(TogglePattern.Pattern, out var tp) == true)
            {
                ((TogglePattern)tp).Toggle();
                Thread.Sleep(500);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
    });

    /// <summary>Слои сверху вниз (как в панели), без «Фона».</summary>
    private static Task<List<LayerItem>> LayersAsync(IntPtr win) => Task.Run(() =>
    {
        var res = new List<LayerItem>();
        try
        {
            var root = AutomationElement.FromHandle(win);
            var list = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "layersList"));
            if (list is null) return res;
            foreach (AutomationElement li in list.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ListItem)))
            {
                var selected = li.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp) && ((SelectionItemPattern)sp).Current.IsSelected;
                var eye = li.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "HideShowLayerButton"));
                // глазок «скрыть/показать»: включён — слой виден (проверено: у свежего слоя On)
                var hidden = eye?.TryGetCurrentPattern(TogglePattern.Pattern, out var tp) == true && ((TogglePattern)tp).Current.ToggleState == ToggleState.Off;
                res.Add(new LayerItem(li.Current.Name, selected, hidden, li));
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        return res;
    });

    private static async Task<string> LayersSummaryAsync(IntPtr win)
    {
        var layers = await LayersAsync(win);
        if (layers.Count == 0) return "";
        return "Слои сверху вниз: " + string.Join(", ", layers.Select(l => l.Name + (l.Selected ? " ◀ текущий" : "") + (l.Hidden ? " (скрыт)" : ""))) + ".\n";
    }

    /// <summary>Действие со слоем: add — кнопка «Создать слой»; select — выделить в списке; остальное — пункт меню слоя.
    /// Меню открывается с клавиатуры (Shift+F10 на слое в фокусе): мышью туда не попасть — поверх панели часто стоит сама Клава.</summary>
    private static async Task<(bool Ok, string Message)> LayerAsync(IntPtr win, string action, string? name, Hands h)
    {
        await EnsureLayersPanelAsync(win);
        var before = await LayersAsync(win);
        if (before.Count == 0) return (false, "[НЕ СРАБОТАЛО] панель слоёв Paint не открылась — слоёв не вижу");
        if (action is "add" or "new" or "создать" or "новый")
        {
            var add = await Uia.FindElementAsync(win, "Создать слой", TimeSpan.FromSeconds(4));
            if (add is null || !await Uia.InvokeAsync(add, TimeSpan.FromSeconds(3))) return (false, "[НЕ СРАБОТАЛО] кнопка «Создать слой» не нажалась");
            await Task.Delay(400, h.Token);
            var after = await LayersAsync(win);
            var fresh = after.FirstOrDefault(l => before.All(b => b.Name != l.Name));
            return fresh is null ? (false, "[НЕ СРАБОТАЛО] новый слой не появился") : (true, $"новый слой «{fresh.Name}» — сверху, рисую в него");
        }
        // какой слой: по имени («Слой 2» или просто «2»), иначе текущий
        LayerItem? target = null;
        if (!string.IsNullOrWhiteSpace(name))
        {
            var n = name.Trim();
            target = before.FirstOrDefault(l => l.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                     ?? before.FirstOrDefault(l => int.TryParse(n, out _) && l.Name.EndsWith(" " + n, StringComparison.Ordinal));
            if (target is null) return (false, $"[НЕ СРАБОТАЛО] слоя «{n}» нет. Есть: {string.Join(", ", before.Select(l => l.Name))}");
        }
        target ??= before.FirstOrDefault(l => l.Selected) ?? before[0];
        var picked = target;
        var selected = await Task.Run(() =>
        {
            try
            {
                if (picked.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var sp)) ((SelectionItemPattern)sp).Select();
                picked.Element.SetFocus();
                return true;
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException) { return false; }
        });
        if (!selected) return (false, $"[НЕ СРАБОТАЛО] слой «{target.Name}» не выбрался");
        await Task.Delay(200, h.Token);
        if (action is "select" or "выбрать" or "current")
            return (true, $"текущий слой — «{target.Name}», рисую в него");
        if (!LayerMenu.TryGetValue(action, out var item)) return (false, $"не знаю, что сделать со слоем: «{action}»");
        if (item == "Скрыть слой" && target.Hidden) return (true, $"«{target.Name}» уже скрыт");
        if (item == "Показать слой" && !target.Hidden) return (true, $"«{target.Name}» и так виден");
        await h.PressAsync([0x10, 0x79], h.Token);          // Shift+F10 — контекстное меню слоя в фокусе
        await Task.Delay(500, h.Token);
        var ok = await PopupMenuItemAsync(win, item);
        if (!ok && item == "Показать слой") ok = await PopupMenuItemAsync(win, "Скрыть слой");   // у скрытого пункт может называться так же
        if (!ok)
        {
            await h.PressAsync([0x1B], h.Token);
            return (false, $"[НЕ СРАБОТАЛО] в меню слоя «{target.Name}» нет пункта «{item}»");
        }
        await Task.Delay(400, h.Token);
        var now = await LayersAsync(win);
        return (true, $"«{target.Name}»: {item.ToLowerInvariant()} — готово. Порядок сверху вниз: {string.Join(", ", now.Select(l => l.Name))}");
    }

    /// <summary>Промежуточные точки через каждые step пикселей: Paint на быстрых рывках рисует пунктир.</summary>
    private static List<Point> Dense(List<Point> pts, int step)
    {
        var dense = new List<Point> { pts[0] };
        for (var i = 1; i < pts.Count; i++)
        {
            var (a, b) = (pts[i - 1], pts[i]);
            var n = Math.Max(1, (int)(Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)) / step));
            for (var k = 1; k <= n; k++) dense.Add(new Point(a.X + (b.X - a.X) * k / n, a.Y + (b.Y - a.Y) * k / n));
        }
        return dense;
    }

    // ---------------- снимок с сеткой и лупа ----------------

    /// <summary>
    /// Снимок холста мозгу — с сеткой координат в единицах холста: тонкие линии каждые 50, подписи каждые 100.
    /// look:[x,y,w,h] — вместо всего холста увеличенный кусок с мелкой сеткой (1–25 единиц на клетку).
    /// </summary>
    private static Task<byte[]> SnapshotAsync(Rectangle c, int H, double[] look, StringBuilder log) => Task.Run(() =>
    {
        double ux = 0, uy = 0, uw = 1000, uh = H;
        if (look.Length >= 4 && look[2] > 0 && look[3] > 0)
        {
            ux = Math.Clamp(look[0], 0, 999); uy = Math.Clamp(look[1], 0, H - 1);
            uw = Math.Clamp(look[2], 5, 1000 - ux); uh = Math.Clamp(look[3], 5, H - uy);
            log.AppendLine($"Лупа: кусок x {ux:F0}..{ux + uw:F0}, y {uy:F0}..{uy + uh:F0}.");
        }
        var src = Rectangle.FromLTRB(
            c.Left + (int)Math.Floor(ux / 1000 * c.Width), c.Top + (int)Math.Floor(uy / H * c.Height),
            c.Left + (int)Math.Ceiling((ux + uw) / 1000 * c.Width), c.Top + (int)Math.Ceiling((uy + uh) / H * c.Height));
        using var raw = Unseen(() => CaptureScreen(src));

        var zoomed = uw < 1000 || uh < H;
        var k = zoomed ? Math.Min(8.0, 1000.0 / Math.Max(src.Width, src.Height)) : Math.Min(1.0, 1200.0 / Math.Max(src.Width, src.Height));
        var ow = Math.Max(1, (int)(src.Width * k));
        var oh = Math.Max(1, (int)(src.Height * k));
        using var img = new Bitmap(ow, oh, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(img))
        {
            // в лупе пиксели крупными квадратами — видно каждую ступеньку и полоску
            g.InterpolationMode = zoomed ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(raw, 0, 0, ow, oh);
            DrawGrid(g, ow, oh, ux, uy, uw, uh, zoomed);
        }
        var codec = ImageCodecInfo.GetImageEncoders().First(e => e.FormatID == ImageFormat.Jpeg.Guid);
        using var prm = new EncoderParameters(1);
        prm.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
        using var ms = new MemoryStream();
        img.Save(ms, codec, prm);
        return ms.ToArray();
    });

    private static void DrawGrid(Graphics g, int w, int h, double ux, double uy, double uw, double uh, bool zoomed)
    {
        // шаг: на весь холст — 50 (подписи через 100); в лупе — чтобы клеток было ~10
        double step = 50, label = 100;
        if (zoomed)
        {
            var want = Math.Max(uw, uh) / 10;
            step = new[] { 1.0, 2, 5, 10, 20, 25, 50, 100 }.First(s => s >= want || s == 100);
            label = step;
        }
        g.SmoothingMode = SmoothingMode.None;
        using var dark = new Pen(Color.FromArgb(110, 0, 0, 0), 1);
        using var light = new Pen(Color.FromArgb(110, 255, 255, 255), 1);
        using var darkBold = new Pen(Color.FromArgb(170, 0, 0, 0), 1);
        using var lightBold = new Pen(Color.FromArgb(170, 255, 255, 255), 1);
        using var font = new Font("Segoe UI", zoomed ? 11f : 10f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var fg = new SolidBrush(Color.White);
        using var bg = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        void Label(string text, float x, float y)
        {
            var sz = g.MeasureString(text, font);
            x = Math.Clamp(x, 0, w - sz.Width);
            y = Math.Clamp(y, 0, h - sz.Height);
            g.FillRectangle(bg, x, y, sz.Width, sz.Height);
            g.DrawString(text, font, fg, x, y);
        }
        for (var v = Math.Ceiling(ux / step) * step; v <= ux + uw + 1e-6; v += step)
        {
            var x = (float)((v - ux) / uw * w);
            var major = Math.Abs(v / label - Math.Round(v / label)) < 1e-6;
            g.DrawLine(major ? darkBold : dark, x, 0, x, h);
            g.DrawLine(major ? lightBold : light, x + 1, 0, x + 1, h);
            if (major) Label(v.ToString("0"), x + 2, 0);
        }
        for (var v = Math.Ceiling(uy / step) * step; v <= uy + uh + 1e-6; v += step)
        {
            var y = (float)((v - uy) / uh * h);
            var major = Math.Abs(v / label - Math.Round(v / label)) < 1e-6;
            g.DrawLine(major ? darkBold : dark, 0, y, w, y);
            g.DrawLine(major ? lightBold : light, 0, y + 1, w, y + 1);
            if (major && v > uy + 1e-6) Label(v.ToString("0"), 0, y + 2);
        }
    }
}
