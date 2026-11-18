using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Мастерская Клавы: 3D-вещи, которые она делает себе сама (друг 01.10: «инструменты по моделькам гардероба дал, а 3D
/// модельки она себе сама делать не может»; из журнала 30.09: «сделать 3D-модель наушников для Клавы»). Гардероб
/// (wardrobe) перекрашивал и пересобирал готовые наряды; здесь — новые предметы с нуля. Вещь — описание в config.json
/// (раздел craft: к чему крепится, где стоит, из каких простых форм собрана, или готовая модель .glb); страница
/// (web/craft.js) собирает её на кости и возвращает снимок с четырёх сторон — мозг видит, что вышло, и поправляет.
/// Три пути сделать вещь: make — из простых форм; blender — её собственный скрипт Blender (bpy) → .glb; import —
/// готовая модель .glb с диска. export — отдать вещь файлом .glb на рабочий стол (Blender, VRoid, печать).
/// </summary>
internal static class CraftStore
{
    public static readonly (string Id, string Fields)[] Shapes =
    [
        ("sphere", "r"), ("box", "size:[x,y,z]"), ("cylinder", "r (или rTop, rBottom), h, open"), ("cone", "r, h"),
        ("torus", "r, tube, arc (градусы; 180 — дуга)"), ("capsule", "r, h"), ("ring", "r, rIn (0 — диск)"),
        ("lathe", "profile:[[радиус,высота],…] — тело вращения вокруг y"), ("tube", "path:[[x,y,z],…], r, closed — трубка по точкам"),
        ("extrude", "outline:[[x,y],…], depth, bevel — выдавленный контур"),
    ];

    public static readonly (string Id, string What)[] Anchors =
    [
        ("head", "центр головы"), ("neck", "шея"), ("chest", "грудь"), ("waist", "талия"), ("hips", "таз"),
        ("shoulderL", "левое плечо"), ("shoulderR", "правое плечо"), ("elbowL", "левый локоть"), ("elbowR", "правый локоть"),
        ("handL", "левая ладонь"), ("handR", "правая ладонь"), ("thighL", "левое бедро"), ("thighR", "правое бедро"),
        ("kneeL", "левое колено"), ("kneeR", "правое колено"), ("footL", "левая щиколотка"), ("footR", "правая щиколотка"),
        ("room", "пол комнаты у её ног (вещь стоит рядом, а не на ней)"),
    ];

    /// <summary>Памятка инструмента: как описывать вещь.</summary>
    public const string Describe =
        "Твоя мастерская: делаешь себе 3D-вещи САМА — наушники, очки, бантик, рожки, крылья, серьги, меч в лапу, вазу на пол. " +
        "make — собрать вещь из простых форм: name, title, attach (куда крепится: head, neck, chest, waist, hips, shoulderL/R, " +
        "elbowL/R, handL/R, thighL/R, kneeL/R, footL/R, room — на пол рядом), at:[x,y,z] — сдвиг вещи от точки крепления, " +
        "rot:[x,y,z] в градусах, scale, parts — список частей. Координаты — ТВОИ, в метрах: x — к твоей левой руке, y — вверх, " +
        "z — вперёд (куда смотришь); начало — точка крепления (голова — центр головы). Причёска у тебя пышная: от центра головы " +
        "≈ ±0.13 в стороны, +0.15 вверх, ±0.13 вперёд-назад (точно — action body, раздел hair) — вещь на голову делай чуть шире " +
        "и выше причёски, иначе утонет в волосах. Часть: shape (sphere r | box size:[x,y,z] | cylinder r,h | cone r,h | torus r,tube,arc | capsule r,h | " +
        "ring r,rIn | lathe profile:[[радиус,высота],…] | tube path:[[x,y,z],…],r | extrude outline:[[x,y],…],depth), pos:[x,y,z], " +
        "rot:[x,y,z], scale, color (#rrggbb), material (toon — по умолчанию | metal | glass | glow), opacity, mirror:'x' — " +
        "зеркальная пара с другой стороны (второе ухо, второй рог). Пример — наушники: attach head, parts: " +
        "[{shape:'torus',r:0.145,tube:0.011,arc:180,pos:[0,0,0],color:'#1a1a22'} — дуга через макушку, " +
        "{shape:'cylinder',r:0.045,h:0.035,pos:[0.145,0,0],rot:[0,0,90],color:'#1a1a22',mirror:'x'} — чашки на ушах, " +
        "{shape:'ring',r:0.034,rIn:0.022,pos:[0.164,0,0],rot:[0,90,0],color:'#38e8ff',material:'glow',mirror:'x'} — светящиеся кольца]. " +
        "(Тор и кольцо лежат в плоскости x–y, цилиндр и конус стоят вдоль y — поворачивай rot.) " +
        "В ответ придёт снимок: ты в полный рост и вещь крупно спереди, слева и сзади-сверху — посмотри, где криво, и поправь: " +
        "move (name + at/rot/scale/attach — подвинуть, не пересылая части) или make с тем же name (пересобрать). " +
        "body — твои мерки (где что на теле, в сантиметрах); look — снимок вещи; wear / unwear — надеть / снять; remove — " +
        "выкинуть; list — что уже сделано. blender — сложная форма настоящим Blender: script — твой скрипт на bpy (сцена пустая, " +
        "переменная OUT — куда сохранить .glb; не сохранишь сама — выгрузятся все сетки сцены), потом вещь встаёт как при " +
        "import (в .glb верх Blender'а z становится верхом вещи y). import — готовая модель: file — путь к .glb, size — " +
        "наибольший размер в метрах. skin=true (make, import, blender) — ПРИШИТЬ вещь к коже: она гнётся вместе с телом (рукав, " +
        "штанина, перчатка, тапок, хвостик-ленточка), а не висит на одной кости; делай её по телу в позе покоя (руки в стороны), " +
        "вплотную к коже. Одежда по фигуре в Blender: в скрипте klava_body() — импорт твоего тела (T-поза, стоишь на полу, лицо к " +
        "−Y Blender, твоя левая рука — +X), кроишь вокруг (shrinkwrap, solidify), тело в вещь не попадёт; потом blender c skin=true — " +
        "встанет как скроено (attach room, без подгонки размера). Простую одежду (кофта, штаны, чулки, юбка) быстрее сшить " +
        "wardrobe sew. Вещь в ладони (handL/handR) считается в позе «руки в стороны»; обычно ты стоишь, опустив " +
        "руки, и вещь уходит вместе с кистью — чтобы меч в опущенной левой руке смотрел вверх, дай rot ≈ [0,0,75], в правой — " +
        "[0,0,-75]. export — отдать вещь файлом " +
        ".glb на рабочий стол (папка «3D модели Клавы»). Вещи остаются на тебе после перезапуска.";

    /// <summary>Число из аргумента (число или строка с запятой).</summary>
    private static double? Num(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        return v.TryGetValue<string>(out var s) && double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null;
    }

    /// <summary>Тройка чисел: массив [x,y,z], строка «0, 0.1, 0» или одно число (для scale).</summary>
    public static JsonArray? Triple(JsonNode? n)
    {
        if (n is JsonArray a && a.Count >= 3 && a.Take(3).Select(Num).ToList() is var xs && xs.All(x => x is not null))
            return [Math.Round(xs[0]!.Value, 4), Math.Round(xs[1]!.Value, 4), Math.Round(xs[2]!.Value, 4)];
        if (n is JsonValue v && v.TryGetValue<string>(out var s))
        {
            var bits = s.Trim('[', ']', ' ').Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries);
            if (bits.Length >= 3 && bits.Take(3).All(b => double.TryParse(b.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out _)))
                return new JsonArray(bits.Take(3).Select(b => (JsonNode)Math.Round(double.Parse(b, CultureInfo.InvariantCulture), 4)).ToArray());
        }
        return null;
    }

    /// <summary>Части вещи из аргумента: массив объектов (или тот же массив строкой JSON). Проверка форм; ошибка — словами.</summary>
    public static (JsonArray? Parts, string? Error) Parts(JsonNode? n)
    {
        if (n is JsonValue v && v.TryGetValue<string>(out var s))
        {
            try { n = JsonNode.Parse(s); }
            catch (System.Text.Json.JsonException ex) { return (null, $"parts — не JSON ({ex.Message.Split('.')[0]}): нужен список частей [{{shape:…}}, …]"); }
        }
        if (n is not JsonArray arr || arr.Count == 0) return (null, "parts — список частей вещи, хотя бы одна: [{shape:'sphere', r:0.03, pos:[0,0.1,0], color:'#ff6fb5'}]");
        if (arr.Count > 80) return (null, $"частей {arr.Count} — это слишком, предел 80: упрости или собери форму в Blender (action:'blender')");
        var parts = new JsonArray();
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JsonObject p) return (null, $"часть {i + 1} — не объект");
            var shape = (p["shape"] as JsonValue)?.TryGetValue<string>(out var sh) == true ? sh.Trim().ToLowerInvariant() : "";
            if (Shapes.All(x => x.Id != shape))
                return (null, $"часть {i + 1}: формы «{shape}» нет. Есть: {string.Join("; ", Shapes.Select(x => $"{x.Id} ({x.Fields})"))}");
            var copy = (JsonObject)p.DeepClone();
            copy["shape"] = shape;
            if (copy["color"] is JsonValue c && c.TryGetValue<string>(out var color))
            {
                try { copy["color"] = WardrobeStore.Hex(color); }
                catch (ArgumentException ex) { return (null, $"часть {i + 1}: {ex.Message}"); }
            }
            parts.Add(copy);
        }
        return (parts, null);
    }

    /// <summary>Что из самодельного на ней — словами (для «как ты выглядишь»).</summary>
    public static string WornText(JsonObject? craft)
    {
        if (craft?["items"] is not JsonObject items || items.Count == 0) return "";
        var worn = items.Where(kv => kv.Value?["worn"]?.GetValue<bool>() != false)
            .Select(kv => $"«{kv.Value?["title"]?.GetValue<string>() ?? kv.Key}» ({Anchors.FirstOrDefault(a => a.Id == (kv.Value?["attach"]?.GetValue<string>() ?? "head")).What ?? "на тебе"})").ToList();
        return worn.Count == 0 ? "" : $" Вещи, которые ты сделала себе сама и носишь: {string.Join(", ", worn)}.";
    }

    /// <summary>Самый свежий Blender в Program Files.</summary>
    public static string? FindBlender()
    {
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        if (!Directory.Exists(baseDir)) return null;
        return Directory.EnumerateDirectories(baseDir, "Blender*")
            .Select(d => Path.Combine(d, "blender.exe"))
            .Where(File.Exists)
            .OrderByDescending(p => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(p))!.Replace("Blender", "").Trim(), out var v) ? v : new Version(0, 0))
            .FirstOrDefault();
    }

    /// <summary>Имя файла модели из имени вещи: латиница, цифры, дефис.</summary>
    public static string FileName(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c is ' ' or '-' or '_') sb.Append('-');
            else if (Cyrillic.IndexOf(c) is var i and >= 0) sb.Append(Latin[i]);
        }
        var s = sb.ToString().Trim('-');
        return s.Length > 0 ? s : "item";
    }

    private const string Cyrillic = "абвгдеёжзийклмнопрстуфхцчшщъыьэюя";
    private static readonly string[] Latin =
        ["a", "b", "v", "g", "d", "e", "e", "zh", "z", "i", "y", "k", "l", "m", "n", "o", "p", "r", "s", "t", "u", "f", "h", "c", "ch", "sh", "sch", "", "y", "", "e", "yu", "ya"];
}

internal sealed partial class OverlayForm
{
    private readonly Dictionary<int, TaskCompletionSource<JsonObject>> _craftWait = new();
    private int _craftId;

    /// <summary>Сообщение мастерской страницы с ответом (снимок, мерки, файл модели); null — страница не ответила.</summary>
    private async Task<JsonObject?> CraftAskAsync(JsonObject message, int timeoutMs = 6000)
    {
        if (!_pageReady) return null;
        var id = ++_craftId;
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _craftWait[id] = tcs;
        message["type"] = "craft";
        message["id"] = id;
        Post(message);
        var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        _craftWait.Remove(id);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    /// <summary>Ответ страницы {type:'craft_reply', id, data | body | error}.</summary>
    private void OnCraftReply(JsonObject m)
    {
        if (m["id"]?.GetValue<int>() is { } id && _craftWait.TryGetValue(id, out var tcs)) tcs.TrySetResult(m);
    }

    private static byte[]? DataUrlBytes(JsonNode? n)
    {
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s) || s.Length == 0) return null;
        var i = s.IndexOf(',');
        try { return Convert.FromBase64String(s.StartsWith("data:", StringComparison.Ordinal) && i > 0 ? s[(i + 1)..] : s); }
        catch (FormatException) { return null; }
    }

    /// <summary>
    /// Инструмент craft. Вещь сразу появляется на ней (сообщение странице), описание ложится в config.json → craft
    /// (страница ради этого раздела не перезагружается); в ответ — снимок вещи, если мозг видит картинки.
    /// </summary>
    internal async Task<ToolResult> CraftToolAsync(string action, JsonObject args, Func<ToolRequest, Task<bool>> allowScript)
    {
        string Arg(string n) => args[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : args[n] is JsonValue v2 ? v2.ToString() : "";
        try
        {
            var root = _pendingLook ?? WardrobeStore.Load(_cfg);
            if (root["craft"] is not JsonObject craft) { craft = new JsonObject(); root["craft"] = craft; }
            if (craft["items"] is not JsonObject items) { items = new JsonObject(); craft["items"] = items; }
            // гардероб копит свои правки до конца фразы (перезагрузка страницы оборвала бы её) — тогда и наши уйдут с ними
            void Save() { if (_pendingLook is null) WardrobeStore.Save(_cfg, root); }
            string? Find(string name)
            {
                var t = WardrobeStore.CleanName(name);
                if (t.Length == 0) return items.Count == 1 ? items.First().Key : null;
                if (items.ContainsKey(t)) return t;
                return items.FirstOrDefault(kv => kv.Key.Contains(t, StringComparison.Ordinal) || kv.Value?["title"]?.GetValue<string>()?.Contains(t, StringComparison.OrdinalIgnoreCase) == true).Key;
            }
            string Listing() => items.Count == 0 ? "Самодельных вещей пока нет."
                : "Твои вещи: " + string.Join("; ", items.Select(kv => $"{kv.Key} — «{kv.Value?["title"]?.GetValue<string>() ?? kv.Key}», {kv.Value?["attach"]?.GetValue<string>() ?? "head"}" +
                                                                       $"{(kv.Value?["glb"] is not null ? ", модель .glb" : $", частей {(kv.Value?["parts"] as JsonArray)?.Count ?? 0}")}{(kv.Value?["worn"]?.GetValue<bool>() == false ? ", снята" : ", на тебе")}")) + ".";
            async Task<ToolResult> Shown(string name, JsonObject spec, string text)
            {
                var reply = await CraftAskAsync(new JsonObject { ["op"] = "set", ["name"] = name, ["spec"] = spec.DeepClone() }, spec["glb"] is not null ? 12000 : 6000);
                if (reply?["error"]?.GetValue<string>() is { Length: > 0 } err) return new ToolResult($"[НЕ СРАБОТАЛО] страница не собрала вещь: {err}");
                if (reply?["skin"] is JsonObject skin)
                    text += skin["error"]?.GetValue<string>() is { Length: > 0 } serr ? $" Пришить к телу не вышло: {serr}."
                        : $" Пришита к телу (сеток {skin["count"]}), гнётся вместе с тобой; дальше всего от кожи {skin["far"]} см" +
                          (skin["far"]?.GetValue<int>() > 12 ? " — далековато, такие куски тянутся за ближайшей костью: придвинь вещь к телу." : ".");
                var jpeg = DataUrlBytes(reply?["data"]);
                var p = _cfg.ActiveBrain;
                var sees = p.IsLocal ? _services.BrainSees : p.Vision;
                return new ToolResult(text + (reply is null ? " (Страница не ответила снимком — вещь появится, когда она загрузится.)"
                    : jpeg is not null && sees ? " Снимок приложен: слева ты целиком, дальше вещь спереди, слева и сзади-сверху — посмотри, ровно ли сидит и того ли размера; криво — move или make ещё раз."
                    : " Глянь на себя (action:'look'), ровно ли сидит."), sees ? jpeg : null);
            }
            // пришить к коже: вещь гнётся вместе с телом (одежда, тапки, перчатки), а не висит на одной кости
            void Skin(JsonObject spec)
            {
                var s = Arg("skin").ToLowerInvariant();
                if (s is "true" or "1" or "да") spec["skin"] = true;
                else if (s is "false" or "0" or "нет") spec.Remove("skin");
            }
            void Placement(JsonObject spec)
            {
                if (Arg("attach") is { Length: > 0 } at)
                    spec["attach"] = CraftStore.Anchors.Any(a => a.Id == at) ? at
                        : throw new ArgumentException($"крепления «{at}» нет. Есть: {string.Join(", ", CraftStore.Anchors.Select(a => $"{a.Id} — {a.What}"))}");
                if (args["at"] is not null) spec["at"] = CraftStore.Triple(args["at"]) ?? throw new ArgumentException("at — три числа в метрах: [x, y, z]");
                if (args["rot"] is not null) spec["rot"] = CraftStore.Triple(args["rot"]) ?? throw new ArgumentException("rot — три угла в градусах: [x, y, z]");
                if (Arg("scale") is { Length: > 0 } sc)
                    spec["scale"] = double.TryParse(sc.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var k) && k is > 0.01 and < 50 ? Math.Round(k, 3)
                        : throw new ArgumentException("scale — число больше нуля (1 — как есть)");
            }
            JsonObject Model(string name, string glbFile, string title)
            {
                var spec = new JsonObject { ["title"] = title, ["attach"] = "head", ["glb"] = $"models/{Path.GetFileName(glbFile)}", ["worn"] = true };
                if (double.TryParse(Arg("size").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && size is > 0.005 and < 5) spec["size"] = Math.Round(size, 3);
                Skin(spec);
                // одежда по выгрузке её тела (BODY в Blender) — в координатах от пола, без подгонки размера
                if (spec["skin"] is not null && Arg("attach").Length == 0) spec["attach"] = "room";
                Placement(spec);
                items[name] = spec;
                Save();
                return spec;
            }
            var modelsDir = Path.Combine(_cfg.Abs(_cfg.WebRoot), "models");

            switch (action.Trim().ToLowerInvariant())
            {
                case "list" or "":
                    return new ToolResult($"{Listing()}\nКрепления (attach): {string.Join(", ", CraftStore.Anchors.Select(a => $"{a.Id} — {a.What}"))}.\n" +
                                          $"Формы (shape): {string.Join("; ", CraftStore.Shapes.Select(s => $"{s.Id} ({s.Fields})"))}.\n" +
                                          $"Blender для сложных форм: {(CraftStore.FindBlender() is { } b ? "есть (" + Path.GetFileName(Path.GetDirectoryName(b)) + ")" : "не установлен")}.");
                case "body":
                {
                    var reply = await CraftAskAsync(new JsonObject { ["op"] = "body" });
                    return new ToolResult(reply?["body"] is { } body
                        ? "Твои мерки (сантиметры от пола между ступнями; x — к левой руке, y — вверх, z — вперёд). В make координаты считаются от точки крепления, в метрах:\n" + body.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })
                        : "[НЕ СРАБОТАЛО] страница не ответила мерками — попробуй через пару секунд.");
                }
                case "make":
                {
                    var name = WardrobeStore.CleanName(Arg("name"));
                    if (name.Length < 2) throw new ArgumentException("дай вещи имя (name), хоть пару букв");
                    var (parts, error) = CraftStore.Parts(args["parts"]);
                    if (parts is null) throw new ArgumentException(error);
                    var spec = new JsonObject { ["title"] = Arg("title").Length > 0 ? Arg("title") : name, ["attach"] = (items[name]?["attach"]?.GetValue<string>()) ?? "head", ["parts"] = parts, ["worn"] = !Arg("wear").Equals("false", StringComparison.OrdinalIgnoreCase) };
                    foreach (var keep in new[] { "at", "rot", "scale", "skin" })
                        if (items[name]?[keep] is { } old) spec[keep] = old.DeepClone();
                    Placement(spec);
                    Skin(spec);
                    items[name] = spec;
                    Save();
                    Log.Write($"мастерская: «{name}» — частей {parts.Count}, крепление {spec["attach"]}");
                    return await Shown(name, spec, $"Собрала «{spec["title"]}» ({name}): частей {parts.Count}, крепление {spec["attach"]}.");
                }
                case "move":
                {
                    if (Find(Arg("name")) is not { } name || items[name] is not JsonObject spec) return new ToolResult($"[НЕ СРАБОТАЛО] Вещи «{Arg("name")}» нет. {Listing()}");
                    Placement(spec);
                    Skin(spec);
                    Save();
                    return await Shown(name, spec, $"Подвинула «{name}»: крепление {spec["attach"]}, сдвиг {spec["at"]?.ToJsonString() ?? "[0,0,0]"}, поворот {spec["rot"]?.ToJsonString() ?? "[0,0,0]"}, размер {spec["scale"]?.ToString() ?? "1"}.");
                }
                case "wear" or "unwear":
                {
                    if (Find(Arg("name")) is not { } name || items[name] is not JsonObject spec) return new ToolResult($"[НЕ СРАБОТАЛО] Вещи «{Arg("name")}» нет. {Listing()}");
                    var on = action.Trim().ToLowerInvariant() == "wear";
                    spec["worn"] = on;
                    Save();
                    Post(new JsonObject { ["type"] = "craft", ["op"] = "wear", ["name"] = name, ["on"] = on });
                    return new ToolResult(on ? $"Надела «{name}»." : $"Сняла «{name}» — лежит в мастерской.");
                }
                case "remove":
                {
                    if (Find(Arg("name")) is not { } name) return new ToolResult($"[НЕ СРАБОТАЛО] Вещи «{Arg("name")}» нет. {Listing()}");
                    items.Remove(name);
                    Save();
                    Post(new JsonObject { ["type"] = "craft", ["op"] = "remove", ["name"] = name });
                    return new ToolResult($"Выкинула «{name}» (прошлый config.json — в web\\config.backups).");
                }
                case "look":
                {
                    if (Find(Arg("name")) is not { } name) return new ToolResult($"[НЕ СРАБОТАЛО] Вещи «{Arg("name")}» нет. {Listing()}");
                    var reply = await CraftAskAsync(new JsonObject { ["op"] = "shot", ["name"] = name });
                    var jpeg = DataUrlBytes(reply?["data"]);
                    return jpeg is null ? new ToolResult("[НЕ СРАБОТАЛО] снимок не получился (вещь снята или страница занята).")
                        : new ToolResult($"Снимок «{name}»: слева ты целиком, дальше вещь спереди, слева и сзади-сверху.", jpeg);
                }
                case "export":
                {
                    if (Find(Arg("name")) is not { } name) return new ToolResult($"[НЕ СРАБОТАЛО] Вещи «{Arg("name")}» нет. {Listing()}");
                    var reply = await CraftAskAsync(new JsonObject { ["op"] = "export", ["name"] = name }, 15000);
                    if (DataUrlBytes(reply?["data"]) is not { Length: > 100 } glb) return new ToolResult("[НЕ СРАБОТАЛО] страница не отдала модель.");
                    var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    var dir = Path.Combine(Directory.Exists(Path.Combine(desk, "ИИ кошкодевочка")) ? Path.Combine(desk, "ИИ кошкодевочка") : desk, "3D модели Клавы");
                    Directory.CreateDirectory(dir);
                    var file = Path.Combine(dir, $"{CraftStore.FileName(name)}.glb");
                    await File.WriteAllBytesAsync(file, glb);
                    return new ToolResult($"Модель «{name}» лежит файлом: {file} ({glb.Length / 1024.0:F0} КБ, .glb — открывается в Blender и любом просмотрщике 3D).");
                }
                case "import":
                {
                    var name = WardrobeStore.CleanName(Arg("name"));
                    var src = Environment.ExpandEnvironmentVariables(Arg("file").Trim('"'));
                    if (name.Length < 2) throw new ArgumentException("дай вещи имя (name)");
                    if (!File.Exists(src) || !Path.GetExtension(src).Equals(".glb", StringComparison.OrdinalIgnoreCase))
                        return new ToolResult($"[НЕ СРАБОТАЛО] Нужен файл .glb (file): «{src}» — такого нет или это не .glb. Из Blender: Файл → Экспорт → glTF 2.0, формат GLB.");
                    Directory.CreateDirectory(modelsDir);
                    var dst = Path.Combine(modelsDir, $"{CraftStore.FileName(name)}.glb");
                    File.Copy(src, dst, overwrite: true);
                    var spec = Model(name, dst, Arg("title").Length > 0 ? Arg("title") : name);
                    return await Shown(name, spec, $"Взяла модель «{name}» из {Path.GetFileName(src)} ({new FileInfo(dst).Length / 1024.0:F0} КБ), крепление {spec["attach"]}.");
                }
                case "blender":
                {
                    var name = WardrobeStore.CleanName(Arg("name"));
                    var script = Arg("script");
                    if (name.Length < 2 || script.Length < 20) throw new ArgumentException("нужны name и script — твой скрипт Blender на bpy (сцена пустая; OUT — путь, куда сохранить .glb)");
                    if (CraftStore.FindBlender() is not { } blender) return new ToolResult("[НЕ СРАБОТАЛО] Blender не установлен (C:\\Program Files\\Blender Foundation) — собери вещь из простых форм (make).");
                    if (!await allowScript(new ToolRequest("Скрипт Blender", script, false, null, "shell"))) return new ToolResult("Друг НЕ разрешил запускать скрипт Blender.");
                    Directory.CreateDirectory(modelsDir);
                    var dst = Path.Combine(modelsDir, $"{CraftStore.FileName(name)}.glb");
                    // её тело в позе покоя — чтобы кроить по фигуре (в скрипте: klava_body() или BODY — путь к .glb)
                    string? bodyFile = null;
                    if (script.Contains("klava_body", StringComparison.Ordinal) || script.Contains("BODY", StringComparison.Ordinal))
                    {
                        var bodyReply = await CraftAskAsync(new JsonObject { ["op"] = "bodymesh" }, 20000);
                        if (DataUrlBytes(bodyReply?["data"]) is { Length: > 100 } bodyBytes)
                        {
                            bodyFile = Path.Combine(Path.GetTempPath(), "klava_blender", "klava_body.glb");
                            Directory.CreateDirectory(Path.GetDirectoryName(bodyFile)!);
                            await File.WriteAllBytesAsync(bodyFile, bodyBytes);
                        }
                        else return new ToolResult("[НЕ СРАБОТАЛО] страница не отдала твоё тело для Blender — попробуй через пару секунд.");
                    }
                    var (ok, log) = await RunBlenderAsync(blender, script, dst, bodyFile);
                    if (!ok) return new ToolResult($"[НЕ СРАБОТАЛО] Blender не выдал модель. Конец его вывода:\n{log}");
                    var spec = Model(name, dst, Arg("title").Length > 0 ? Arg("title") : name);
                    return await Shown(name, spec, $"Blender собрал «{name}» ({new FileInfo(dst).Length / 1024.0:F0} КБ), крепление {spec["attach"]}{(spec["size"] is null ? "; размер — как в скрипте (метры), поправить — move scale или size при import" : "")}.");
                }
                default:
                    return new ToolResult("action: list | body | make | move | look | wear | unwear | remove | export | import | blender");
            }
        }
        catch (ArgumentException ex) { return new ToolResult("[НЕ СРАБОТАЛО] " + ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return new ToolResult($"[НЕ СРАБОТАЛО] мастерская: {ex.Message}");
        }
    }

    /// <summary>
    /// Её скрипт Blender без окна: пустая сцена, скрипт, потом — если он сам не сохранил OUT — выгрузка всех сеток в .glb.
    /// Не дольше трёх минут. (успех, хвост вывода).
    /// </summary>
    internal static async Task<(bool Ok, string Log)> RunBlenderAsync(string blender, string script, string outGlb, string? bodyGlb = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "klava_blender");
        Directory.CreateDirectory(dir);
        var user = Path.Combine(dir, $"model_{DateTime.Now:HHmmss}.py");
        var wrap = Path.Combine(dir, $"run_{DateTime.Now:HHmmss}.py");
        await File.WriteAllTextAsync(user, script, new UTF8Encoding(false));
        await File.WriteAllTextAsync(wrap,
            "import bpy, math, os, sys, traceback\n" +
            $"OUT = r\"{outGlb}\"\n" +
            $"BODY = r\"{bodyGlb ?? ""}\"\n" +
            "bpy.ops.wm.read_factory_settings(use_empty=True)\n" +
            // её тело в позе покоя (T-поза, от пола, лицом к -Y Blender): объекты KLAVA_BODY_* — мерка, в вещь не идут
            "def klava_body():\n" +
            "    if not BODY or not os.path.exists(BODY):\n" +
            "        raise RuntimeError('тело Клавы не выгружено')\n" +
            "    before = set(bpy.data.objects)\n" +
            "    bpy.ops.import_scene.gltf(filepath=BODY)\n" +
            "    made = [o for o in bpy.data.objects if o not in before]\n" +
            "    for o in made:\n" +
            "        o.name = 'KLAVA_BODY_' + o.name\n" +
            "    return [o for o in made if o.type == 'MESH']\n" +
            "try:\n" +
            $"    exec(compile(open(r\"{user}\", encoding=\"utf-8\").read(), \"klava_model.py\", \"exec\"), {{\"bpy\": bpy, \"math\": math, \"OUT\": OUT, \"BODY\": BODY, \"klava_body\": klava_body, \"__name__\": \"__main__\"}})\n" +
            "except Exception:\n" +
            "    traceback.print_exc()\n" +
            "    sys.exit(3)\n" +
            "if not os.path.exists(OUT):\n" +
            "    meshes = [o for o in bpy.data.objects if o.type == 'MESH' and not o.name.startswith('KLAVA_BODY_')]\n" +
            "    if not meshes:\n" +
            "        print('KLAVA: в сцене нет ни одной сетки')\n" +
            "        sys.exit(4)\n" +
            "    for o in bpy.data.objects: o.select_set(o in meshes)\n" +
            "    bpy.ops.export_scene.gltf(filepath=OUT, export_format='GLB', use_selection=True, export_apply=True)\n" +
            "print('KLAVA: готово', os.path.getsize(OUT))\n", new UTF8Encoding(false));
        try { if (File.Exists(outGlb)) File.Move(outGlb, outGlb + ".prev", overwrite: true); }
        catch (IOException) { }
        var psi = new ProcessStartInfo(blender)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-b", "--factory-startup", "--python-exit-code", "5", "--python", wrap }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Blender не запустился");
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return (false, "Blender работал дольше трёх минут — остановила.");
        }
        string text;
        lock (output) text = output.ToString();
        var tail = string.Join("\n", text.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).TakeLast(14));
        return (File.Exists(outGlb) && new FileInfo(outGlb).Length > 100, tail.Length > 1600 ? tail[^1600..] : tail);
    }
}
