using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Гардероб Клавы (web/wardrobe.js). Друг 29.09: «чтобы она сама костюмчик подобрать могла»; что она хотела — из журнала
/// 28.09: готика (юбка в складку, топ с открытыми плечами, чокер с колечком, сетка) и киберпанк (кроп-топ, косуха,
/// легинсы с ремешками). Наряд помнится в klava.json (Outfit), страница собирается с ?outfit=имя.
/// </summary>
internal static class Wardrobe
{
    /// <summary>Имя для страницы, пункт меню, что сказать, когда переоделась по меню.</summary>
    public static readonly (string Id, string Title, string Bubble)[] All =
    [
        ("kimono", "👘 Кимоно", "Любимое кимоно — как вторая шкурка, мур."),
        ("gothic", "🖤 Готика: юбка в складку, топ, сетка", "Юбочка в складку, чокер с колечком и сеточка — ну как, готично?"),
        ("cyber", "🌃 Киберпанк: косуха, кроп-топ, легинсы", "Косуха, неон и ремешки — кошка из будущего вышла на охоту."),
        ("classic", "🍷 Классика: элегантное платье", "Платье и жемчуг — сегодня я леди. Ну, почти."),
        ("native", "🤖 Родной киберкостюм", "Вернулась в то, в чём родилась, — киберкостюм, ностальгия."),
        ("rope", "🪢 Готика с верёвочками", "Готика и красная верёвочка поверх — портупея как у настоящей рок-звезды."),
        ("pajama", "🐱 Уютная пижамка: кардиган, штанишки, тапочки", "Уютная домашняя пижамка: кардиган с рюшечками и пуговичками, прямые пушистые штанишки и мягкие тапочки-лапки — чисто домашний кайф, мур."),
    ];

    /// <summary>Обувь и шляпа встроенных нарядов по умолчанию (web/wardrobe.js: OUTFITS.*.shoes / hat).</summary>
    private static readonly Dictionary<string, (string? Shoes, string? Hat)> Defaults = new()
    {
        ["kimono"] = (null, "beret"), ["gothic"] = ("combat", null), ["rope"] = ("combat", null), ["cyber"] = ("knee", null),
        ["classic"] = ("ankle", null), ["native"] = (null, null), ["pajama"] = ("slippers", null),
    };

    internal static string ShoesText(string style, string? color) => style switch
    {
        "combat" => "высокие чёрные берцы на шнуровке крест-накрест с серебряными люверсами",
        "knee" => "лаковые сапоги до колена с неоновым кантом по бокам и платформой",
        "ankle" => "ботильоны на каблуке с ремешком и пряжкой",
        "slippers" => "пушистые мягкие тапочки-лапки с подушечками",
        _ => "родные ботинки",
    } + (color is { Length: > 0 } ? $" (цвет {color})" : "");

    internal static string HatText(string kind, string? color) => kind switch
    {
        "beret" => "берет художника набок между ушками, с бусиной-булавкой",
        "witch" => "ведьминская шляпа с заломленным кончиком",
        _ => "",
    } + (color is { Length: > 0 } && kind != "none" ? $" (цвет {color})" : "");

    /// <summary>Обувь и шляпа наряда с учётом того, что она сменила сама (config.json → wardrobe).</summary>
    private static string Extras(string id, JsonObject? custom)
    {
        var w = WardrobeStore.Snapshot;
        var def = Defaults.GetValueOrDefault(custom?["base"]?.GetValue<string>() ?? id);
        var shoes = custom?["shoes"] ?? w?["shoes"]?[id];
        var hat = custom?["hat"] ?? w?["hat"]?[id];
        var shoeStyle = shoes?["style"]?.GetValue<string>() ?? def.Shoes;
        var hatKind = hat?["kind"]?.GetValue<string>() ?? def.Hat;
        var parts = new List<string>();
        if (shoeStyle is { Length: > 0 } && shoeStyle != "native") parts.Add(ShoesText(shoeStyle, shoes?["color"]?.GetValue<string>()));
        if (hatKind is { Length: > 0 } && hatKind != "none") parts.Add(HatText(hatKind, hat?["color"]?.GetValue<string>()));
        return parts.Count > 0 ? ", " + string.Join(", ", parts) : "";
    }

    /// <summary>
    /// Вещи, из которых она собирает наряд сама (друг 02.10: «шмотки себе собирает, а 3D-модельку собрать не может» —
    /// она жаловалась: «топик от готики, а штаны от кибера на одну тушку не могу, модельки цельными сетами идут»).
    /// Вещи — отдельные слои по телу (web/wardrobe.js addGarments), их можно брать из любых нарядов; на каждое место —
    /// одна вещь. Id, место, как звать, слова.
    /// </summary>
    internal static readonly (string Id, string Slot, string Text, string Pattern)[] Pieces =
    [
        ("offShoulder", "верх", "чёрный топ в рубчик с открытыми плечами и кружевом (из готики)", @"открыт\w*\s+плеч|offshoulder|топ\w*\s+(от\s+)?гот|готич\w*\s+топ|кружев"),
        ("cropTop", "верх", "чёрный кроп-топ с неоновой окантовкой (из кибера)", @"кроп|crop"),
        ("pajamaTop", "верх", "сиреневый пушистый кардиган с рюшами (из пижамки)", @"кардиган|пижамн\w*\s+(верх|кофт)|pajamatop|кофт"),
        ("jacket", "куртка", "кожаная косуха с заклёпками и неоновым кантом (из кибера)", @"косух|куртк|jacket"),
        ("pleatSkirt", "низ", "короткая юбка в складку (из готики)", @"юб(к|оч)|плисс|skirt"),
        // «штаны от кибера» — это легинсы: проверяются раньше пижамных штанишек
        ("leggings", "ноги", "глянцевые легинсы с ремешками (из кибера)", @"легинс|лосин|leggings|штан\w*\s+(от\s+|из\s+)?кибер|кибер\w*\s+штан"),
        ("pajamaPants", "низ", "прямые пушистые штанишки (из пижамки)", @"штан|брюк|pants|пижамн\w*\s+низ"),
        ("fishnet", "ноги", "чулки в сеточку (из готики)", @"сетк|сеточк|чул(к|оч)|fishnet"),
        ("harness", "обвязка", "красная верёвочная обвязка поверх (из готики с верёвочками)", @"верёвоч|веревоч|обвязк|портупе|шибари|harness"),
    ];

    // ---------- швейная мастерская: вещи её собственного кроя (web/wardrobe.js sewnGarment) ----------

    /// <summary>Слово → мерка кроя (как в SEW на странице). Английское имя проходит как есть.</summary>
    internal static string? SewAt(string word, string[] allowed)
    {
        var t = word.Trim().ToLowerInvariant().Replace('ё', 'е');
        if (allowed.Contains(t)) return t;
        string? m = t switch
        {
            _ when Regex.IsMatch(t, @"горл|водолаз|стойк") => "high",
            _ when Regex.IsMatch(t, @"под\s*груд") => "underbust",
            _ when Regex.IsMatch(t, @"ше[яиюей]") => "neck",
            _ when Regex.IsMatch(t, @"груд") => "chest",
            _ when Regex.IsMatch(t, @"тали|пояс") => "waist",
            _ when Regex.IsMatch(t, @"пах|промеж|шорт") => "crotch",
            _ when Regex.IsMatch(t, @"середин\w*\s+бедр|полбедр|мини") => "thigh",
            _ when Regex.IsMatch(t, @"бедр|таз") => allowed.Contains("hips") ? "hips" : "thigh",
            _ when Regex.IsMatch(t, @"колен|миди") => "knee",
            _ when Regex.IsMatch(t, @"икр|голен") => "calf",
            _ when Regex.IsMatch(t, @"щиколот|лодыж|макси|пол\b|в пол") => "ankle",
            _ when Regex.IsMatch(t, @"стоп|ступн|носк|пятк") => "foot",
            _ when Regex.IsMatch(t, @"без|нет|none") => "none",
            _ when Regex.IsMatch(t, @"корот|плеч|cap") => "cap",
            _ when Regex.IsMatch(t, @"локт") => "elbow",
            _ when Regex.IsMatch(t, @"три\s*четверт|предплеч") => "forearm",
            _ when Regex.IsMatch(t, @"запяст|длинн|кист") => "wrist",
            _ => null,
        };
        return m is not null && allowed.Contains(m) ? m : null;
    }

    internal static readonly string[] SewTorsoFrom = ["neck", "high", "chest", "underbust", "waist"];
    internal static readonly string[] SewTorsoTo = ["underbust", "waist", "hips", "crotch", "thigh"];
    internal static readonly string[] SewSleeves = ["none", "cap", "elbow", "forearm", "wrist"];
    internal static readonly string[] SewLegsFrom = ["waist", "hips", "crotch", "thigh", "knee", "calf"];
    internal static readonly string[] SewLegsTo = ["crotch", "thigh", "knee", "calf", "ankle", "foot"];
    internal static readonly string[] SewSkirtTo = ["crotch", "thigh", "knee", "calf", "ankle"];

    internal static readonly (string Id, string Pattern)[] SewPatterns =
    [
        ("solid", @"однотон|гладк|solid|без узор"), ("vstripes", @"вертикал\w*\s+полос|vstripe"), ("stripes", @"полос|тельняш|stripe"),
        ("plaid", @"клет|шотланд|plaid|тартан"), ("dots", @"горош|точк|dots"), ("rib", @"рубчик|вязк|трикотаж|rib|косичк"),
        ("fur", @"мех|пушист|плюш|fur|флис"), ("mesh", @"сетк|сеточк|mesh|ажур"), ("camo", @"камуфл|camo|хаки"),
        ("hearts", @"сердеч|hearts|love"), ("checker", @"шахмат|checker"),
    ];

    internal static readonly (string Id, string Pattern)[] SewNecklines =
    [
        ("off", @"открыт\w*\s+плеч|off|спущен"), ("v", @"^v$|v-|мыс|клин|v\s*образ|треуг"), ("high", @"горл|водолаз|стойк|high"),
        ("square", @"каре|квадрат|square"), ("round", @"кругл|round|обычн"),
    ];

    /// <summary>Какие места тела занимает сшитая вещь (по одной вещи на место, как у готовых).</summary>
    internal static List<string> SewSlots(JsonObject spec)
    {
        var slots = new List<string>();
        if (spec["torso"] is JsonObject) slots.Add("верх");
        if (spec["skirt"] is JsonObject) slots.Add("низ");
        if (spec["legs"] is JsonObject legs)
            slots.Add(legs["from"]?.GetValue<string>() is "waist" or "hips" or "crotch" ? "низ" : "ноги");
        return slots.Distinct().ToList();
    }

    /// <summary>Как выглядит сшитая вещь — словами (для мозга и «как ты выглядишь»).</summary>
    internal static string SewText(string name, JsonObject spec)
    {
        if (spec["look"]?.GetValue<string>() is { Length: > 0 } look) return $"«{spec["title"]?.GetValue<string>() ?? name}» (сшила сама): {look}";
        var bits = new List<string>();
        if (spec["torso"] is JsonObject) bits.Add($"верх {spec["torso"]!["from"]}→{spec["torso"]!["to"]}, рукава {spec["sleeves"] ?? "none"}");
        if (spec["legs"] is JsonObject) bits.Add($"штанины {spec["legs"]!["from"]}→{spec["legs"]!["to"]}");
        if (spec["skirt"] is JsonObject) bits.Add($"юбка до {spec["skirt"]!["to"]}");
        return $"«{spec["title"]?.GetValue<string>() ?? name}» (сшила сама): {string.Join(", ", bits)}, {spec["color"]}{(spec["pattern"] is { } p && p.ToString() != "solid" ? $", узор {p}" : "")}";
    }

    internal static string? PieceId(string word)
    {
        var t = word.Trim().ToLowerInvariant();
        foreach (var p in Pieces) if (p.Id.Equals(word.Trim(), StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(t, p.Pattern)) return p.Id;
        return null;
    }

    /// <summary>Основа (что под вещами: кожа, перчатки, чокер) по первой вещи: готика, кибер или пижамка.</summary>
    internal static string BaseOfPiece(string id) => id switch
    {
        "cropTop" or "jacket" or "leggings" => "cyber",
        "pajamaTop" or "pajamaPants" => "pajama",
        _ => "gothic",
    };

    /// <summary>Как она выглядит в этом наряде — словами, для мозга (когда её спрашивают про внешность).</summary>
    public static string Look(string id)
    {
        if (WardrobeStore.Snapshot?["custom"]?[id] is JsonObject custom)
        {
            var baseId = custom["base"]?.GetValue<string>() ?? "kimono";
            var sewn = WardrobeStore.Snapshot?["sewn"] as JsonObject;
            string? PieceLook(string? g) => g is null ? null
                : g.StartsWith("sewn:", StringComparison.Ordinal) ? (sewn?[g[5..]] is JsonObject sp ? SewText(g[5..], sp) : null)
                : Pieces.FirstOrDefault(p => p.Id == g).Text;
            var pieceText = custom["garments"] is JsonArray ga && ga.Count > 0
                ? "собран из вещей: " + string.Join(", ", ga.Select(g => PieceLook(g?.GetValue<string>())).Where(s => s is not null))
                : null;
            var own = custom["look"]?.GetValue<string>() is { Length: > 0 } l ? (pieceText is null ? l : $"{l} ({pieceText})") : pieceText ?? BaseLook(baseId);
            var colors = custom["colors"] is JsonObject c && c.Count > 0 ? $"; перекрашено: {string.Join(", ", c.Select(kv => $"{kv.Key} {kv.Value}"))}" : "";
            return $"свой наряд «{custom["title"]?.GetValue<string>() ?? id}» (сама собрала): {own}{colors}{Extras(id, custom)}";
        }
        return BaseLook(id) + Extras(id, null);
    }

    private static string BaseLook(string id) => id switch
    {
        "pajama" => "уютная домашняя пижамка: нежно-сиреневый мягкий кардиган с рюшечками и перламутровыми пуговичками, прямые свободные пушистые штанишки пастельного цвета, пушистые тапочки-лапки",
        "rope" => "чёрный топ в рубчик с открытыми плечами и белым кружевом, поверх — декоративная обвязка из красной кручёной верёвки с узелками (под грудью, по талии, ромб спереди, лямки к шее), короткая чёрная юбка в складку, чулки в сеточку, длинные перчатки, чокер с колечком",
        "gothic" => "чёрный топ в рубчик с открытыми плечами и белым кружевом по краю, короткая чёрная юбка в складку с бордовой полоской у подола, чулки в сеточку, длинные чёрные перчатки, кожаный чокер с серебряным колечком",
        "cyber" => "чёрная кожаная косуха нараспашку с серебряными молниями и заклёпками на плечах, неоновый голубой кант по манжетам, чёрный кроп-топ с неоновой окантовкой и светящимися кошачьими ушками на груди, голый животик, чёрные глянцевые легинсы с ремешками и пряжками на бёдрах, чокер с колечком",
        "classic" => "длинное винно-бордовое платье с запахом и золотой каймой, светлый воротничок в вырезе, тонкий чёрный пояс с золотом, узкие рукава до локтя, небольшой разрез спереди, нитка жемчуга на шее",
        "native" => "родной облегающий киберкостюм: тёмно-синее боди с неоновыми голубыми узорами, открытые плечи, длинные перчатки и высокие сапоги-чулки в неоне",
        _ => "длинное кимоно с узором волн и сакурой, пояс оби, чокер с золотым колокольчиком",
    };

    // слова наряда: друг 30.09 — Сова просила «юбочку с чулками», а старый поиск знал только «юбк» и не нашёл ничего
    private static readonly (string Id, string Pattern)[] Words =
    [
        ("pajama", @"пижам|пижамк|домашн|кардиган|рюш|пугович|носочк|уютн|спать|тапочк|тапки|лапки"),
        ("rope", @"верёвоч|веревоч|шибари|обвязк|портупе"),
        ("gothic", @"готик|готич|гот\b|сеточк|сетк[аеуи]|юб(к|оч)|\bмини(?![а-яё])|чул(к|оч)|складк|топ\w* с открыт|открыт\w* плеч"),
        ("cyber", @"кибер(?!костюм)|косух|неон|легинс|лосин|кроп|ремешк"),
        ("classic", @"классик|плать|вечерн|элегант|жемчуг|леди"),
        ("native", @"родн\w* (?:костюм|прикид|наряд)|киберкостюм|исходн|в ч[её]м родил"),
        ("kimono", @"кимоно|халат|юкат|сакур|оби\b"),
    ];

    /// <summary>Наряд по словам («в готику», «юбочку с чулками», «косуху», «платье», «родное») → id или null.
    /// Упомянуто несколько — берётся то, что сказано раньше («в юбку, а не в кимоно» → готика).</summary>
    public static string? ByWords(string text)
    {
        var t = text.ToLowerInvariant();
        // свой наряд — по имени или названию; «готика с верёвочками» — верёвочки важнее слова «готика»
        if (WardrobeStore.Snapshot?["custom"] is JsonObject own)
            foreach (var (name, v) in own)
                if (t.Contains(name, StringComparison.Ordinal) || (v?["title"]?.GetValue<string>() is { Length: > 2 } title && t.Contains(title.ToLowerInvariant(), StringComparison.Ordinal)))
                    return name;
        // верёвочки важнее слова «готика»: ищем шаблон rope по имени (пижамку вставили в начало списка — Words[0] стал ею)
        if (Regex.IsMatch(t, Words.First(w => w.Id == "rope").Pattern)) return "rope";
        string? best = null;
        var at = int.MaxValue;
        foreach (var (id, _, _) in All)
            if (t.IndexOf(id, StringComparison.Ordinal) is var i and >= 0 && i < at) { at = i; best = id; }
        foreach (var (id, pat) in Words)
            if (Regex.Match(t, pat) is { Success: true } m && m.Index < at) { at = m.Index; best = id; }
        return best;
    }
}

internal sealed partial class OverlayForm
{
    /// <summary>Переодеться после текущей фразы (метка [наряд …] или просьба друга).</summary>
    private string? _pendingOutfit;

    private string PageUrl() => $"https://klava.local/index.html?outfit={Uri.EscapeDataString(_cfg.Outfit)}&v={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

    private TaskCompletionSource<byte[]?>? _selfie;
    private int _selfieId;

    /// <summary>
    /// Зеркало: страница рисует её саму (в полный рост и лицо крупно, JPEG) — друг 29.09: «она себя не видит, пусть
    /// знает, как выглядит». На снимках экрана её нет (окно исключено из захвата), поэтому свой кадр. null — не успела.
    /// </summary>
    private async Task<byte[]?> SelfieAsync()
    {
        if (!_pageReady) return null;
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _selfie = tcs;
        var id = ++_selfieId;
        Post(new { type = "selfie", id });
        var done = await Task.WhenAny(tcs.Task, Task.Delay(3000));
        _selfie = null;
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    /// <summary>Ответ страницы на «selfie»: data:image/jpeg;base64,….</summary>
    private void OnSelfie(string? data)
    {
        byte[]? jpeg = null;
        if (data is not null && data.IndexOf(',') is var i and > 0)
            try { jpeg = Convert.FromBase64String(data[(i + 1)..]); } catch (FormatException) { }
        _selfie?.TrySetResult(jpeg);
    }

    /// <summary>Строка контекста про её собственный вид (и снимок, если мозг видит картинки).</summary>
    private async Task<(string Note, byte[]? Image)> SelfViewAsync()
    {
        var p = _cfg.ActiveBrain;
        var sees = p.IsLocal ? _services.BrainSees : p.Vision;
        var jpeg = sees ? await SelfieAsync() : null;
        Log.Write($"зеркало: {(jpeg is null ? "без снимка" : $"снимок {jpeg.Length / 1024} КБ")}, наряд {_cfg.Outfit}");
        var crafted = "";
        try { crafted = CraftStore.WornText(WardrobeStore.Load(_cfg)["craft"] as JsonObject); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { }
        var note = $"(Это про тебя саму. Сейчас на тебе: {Wardrobe.Look(_cfg.Outfit)}.{crafted} " +
                   (jpeg is not null ? "Снимок приложен — это ТЫ, как тебя видит друг: слева в полный рост спереди, посередине со спины (хвост, попа, как " +
                                     "сидит наряд сзади), справа лицо крупно. " : "") +
                   "Говори о своей внешности от себя, живо и с кайфом, как девчонка перед зеркалом, — не как экскурсовод.)";
        return (note, jpeg);
    }

    // «как ты выглядишь», «посмотри на себя», «что на тебе надето», «ты себя видишь», «опиши себя», «глянь в зеркало»
    [GeneratedRegex(@"(как\s+ты\s+(сейчас\s+)?выгляд|посмотри\s+на\s+себя|глянь\s+на\s+себя|видишь\s+себя|себя\s+видишь|что\s+(на\s+тебе|ты)\s+(надето|надела|одета)|во\s+что\s+ты\s+(одета|оделась)|опиши\s+себя|в\s+зеркал|твой\s+(наряд|прикид|вид)|как\s+я\s+тебе\s+в)", RegexOptions.IgnoreCase)]
    private static partial Regex SelfLookRx();

    /// <summary>Надеть наряд: запомнить и сказать странице — она вспыхивает «хэнсином» и собирается заново.</summary>
    private void ApplyOutfit(string id, bool announce = false)
    {
        var known = Wardrobe.All.FirstOrDefault(o => o.Id == id);
        var custom = WardrobeStore.Snapshot?["custom"]?[id];
        if (known.Id is null && custom is null) { Log.Write($"гардероб: нет наряда «{id}»"); return; }
        if (_cfg.Outfit == id && !announce) return;
        _cfg.Outfit = id;
        _cfg.Save();
        Log.Write($"гардероб: надела {id}");
        Post(new { type = "outfit", name = id });
        if (announce) Bubble(known.Id is not null ? known.Bubble : $"Мой собственный наряд — «{custom?["title"]?.GetValue<string>() ?? id}». Сама собрала!");
    }

    // ---------- её собственный гардероб (инструмент wardrobe) ----------

    /// <summary>Правка config.json — после того, как договорит: страница перечитывает конфиг и перезагружается, фраза бы оборвалась.</summary>
    private JsonObject? _pendingLook;

    private void ApplyPendingLook()
    {
        if (_pendingLook is not { } root) return;
        _pendingLook = null;
        try { WardrobeStore.Save(_cfg, root); Log.Write("гардероб: config.json обновлён (свои вещи)"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("гардероб: config.json не записался", ex); }
    }

    /// <summary>Наряд по имени или словам: встроенный, её собственный; null — нет такого.</summary>
    private static string? ResolveOutfit(JsonObject? wardrobe, string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t.Length == 0) return null;
        if (Wardrobe.All.Any(o => o.Id == t)) return t;
        if (wardrobe?["custom"] is JsonObject own)
        {
            if (own.ContainsKey(t)) return t;
            foreach (var (name, v) in own)
                if (v?["title"]?.GetValue<string>()?.ToLowerInvariant() == t || name.Contains(t, StringComparison.Ordinal)) return name;
        }
        return Wardrobe.ByWords(t);
    }

    private static string? ShoeStyle(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        if (WardrobeStore.Shoes.Any(x => x.Id == t)) return t;
        return Regex.IsMatch(t, "берц|шнур|combat|армейск|гриндер") ? "combat" : Regex.IsMatch(t, "сапог|колен|ботфорт|лаков|knee") ? "knee"
            : Regex.IsMatch(t, "ботильон|каблук|полусапож|ankle") ? "ankle"
            : Regex.IsMatch(t, "тапочк|тапк|лапк|пушист|slippers") ? "slippers"
            : Regex.IsMatch(t, "родн|обычн|свои") ? "native" : null;
    }

    private static string? HatKind(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        if (WardrobeStore.Hats.Any(x => x.Id == t)) return t;
        return Regex.IsMatch(t, "берет|beret|художни") ? "beret" : Regex.IsMatch(t, "ведьм|witch|колпак") ? "witch"
            : Regex.IsMatch(t, "без|сними|none|нет") ? "none" : null;
    }

    private static string WardrobeList(JsonObject root, string current)
    {
        var w = root["wardrobe"] as JsonObject;
        var sb = new StringBuilder($"Сейчас на тебе: «{current}».\nНаряды: {string.Join(", ", Wardrobe.All.Select(o => o.Id))}");
        if (w?["custom"] is JsonObject own && own.Count > 0)
            sb.Append($"\nТвои собственные: {string.Join(", ", own.Select(kv => $"{kv.Key} (основа {kv.Value?["base"]}){(kv.Value?["title"] is { } tt ? $" «{tt}»" : "")}"))}");
        sb.Append($"\nОбувь (style): {string.Join(", ", WardrobeStore.Shoes.Select(x => $"{x.Id} — {x.Title}"))}");
        sb.Append($"\nШляпы (kind): {string.Join(", ", WardrobeStore.Hats.Select(x => $"{x.Id} — {x.Title}"))}");
        sb.Append($"\nВещи, из которых собрать свой наряд (make pieces, по одной на место): {string.Join("; ", Wardrobe.Pieces.Select(p => $"{p.Id} [{p.Slot}] — {p.Text}"))}");
        if (w?["sewn"] is JsonObject sewn && sewn.Count > 0)
            sb.Append($"\nСшитые тобой (sew; в make pieces — sewn:имя): {string.Join("; ", sewn.Select(kv => $"{kv.Key} [{string.Join("/", Wardrobe.SewSlots(kv.Value as JsonObject ?? new JsonObject()))}] — {Wardrobe.SewText(kv.Key, kv.Value as JsonObject ?? new JsonObject())}"))}");
        else sb.Append("\nСшить новую вещь своего кроя — action sew (верх, штаны, чулки, юбка: цвет, узор, окантовка, неон).");
        sb.Append($"\nПерекрасить части своего наряда: {string.Join(", ", WardrobeStore.Parts.Select(x => $"{x.Id} — {x.Title}"))}");
        sb.Append($"\nКак в редакторе (tweak path): {string.Join(", ", WardrobeStore.Tweakable.Select(x => $"{x.Path} — {x.Title}"))}");
        return sb.ToString();
    }

    /// <summary>
    /// Инструмент wardrobe: сама делает себе вещи и правит облик (друг 30.09). Изменения конфига копятся за ход и
    /// пишутся, когда договорит (_pendingLook), переодевание — так же (_pendingOutfit).
    /// </summary>
    internal Task<string> WardrobeToolAsync(string action, JsonObject args)
    {
        try
        {
            var turn = new WardrobeTurn(_pendingLook ?? WardrobeStore.Load(_cfg), _cfg.Outfit, _cfg.Abs(@"editor_bin\KlavaEditor.exe"));
            var text = WardrobeCore(turn, action, args);
            if (turn.Changed) _pendingLook = turn.Root;
            if (turn.Wear is { } wear) _pendingOutfit = wear;
            return Task.FromResult(text);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return Task.FromResult($"[НЕ СРАБОТАЛО] config.json: {ex.Message}");
        }
    }

    /// <summary>Что копится за ход: конфиг с её правками, что надеть после, путь к редактору.</summary>
    internal sealed class WardrobeTurn(JsonObject root, string current, string editorExe)
    {
        public JsonObject Root { get; } = root;
        public string Current { get; } = current;
        public string EditorExe { get; } = editorExe;
        public bool Changed { get; set; }
        public string? Wear { get; set; }
    }

    /// <summary>Места вещи: готовой — из Pieces, своей сшитой — по крою.</summary>
    private static List<string> SlotsOf(string pid, JsonObject? sewn) =>
        pid.StartsWith("sewn:", StringComparison.Ordinal)
            ? (sewn?[pid[5..]] is JsonObject sp ? Wardrobe.SewSlots(sp) : [])
            : Wardrobe.Pieces.Where(p => p.Id == pid).Select(p => p.Slot).ToList();

    private static string PieceTitle(string pid, JsonObject? sewn) =>
        pid.StartsWith("sewn:", StringComparison.Ordinal)
            ? (sewn?[pid[5..]] is JsonObject sp ? Wardrobe.SewText(pid[5..], sp) : pid)
            : Wardrobe.Pieces.FirstOrDefault(p => p.Id == pid).Text ?? pid;

    /// <summary>Готовые вещи встроенных нарядов (web/wardrobe.js OUTFITS.*.garments) — чтобы сшитое заменяло вещь на том же месте.</summary>
    private static readonly Dictionary<string, string[]> BaseGarments = new()
    {
        ["gothic"] = ["offShoulder", "pleatSkirt", "fishnet"], ["rope"] = ["offShoulder", "pleatSkirt", "fishnet", "harness"],
        ["cyber"] = ["cropTop", "jacket", "leggings"], ["pajama"] = ["pajamaTop", "pajamaPants"],
        ["kimono"] = [], ["classic"] = [], ["native"] = [],
    };

    private static readonly string[] SewOrder = ["high", "neck", "chest", "underbust", "waist", "hips", "crotch", "thigh", "knee", "calf", "ankle", "foot"];

    private static double Cm(string s, double lo, double hi, double d) =>
        double.TryParse(s.Replace(',', '.').Replace("см", "").Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? Math.Round(Math.Clamp(v, lo, hi), 2) : d;

    /// <summary>
    /// wardrobe sew — сшить вещь своего кроя (друг 02.10: «новую вещь она себе создать и сконструировать не может»).
    /// Описание → config.json wardrobe.sewn[имя]; надеть (по умолчанию) — в свой наряд (текущий, если свой; иначе новый
    /// «нынешний+вещь»), где она заменяет вещь на том же месте.
    /// </summary>
    private static string SewCore(WardrobeTurn t, JsonObject w, Func<string, string> arg, Func<string, JsonObject?> custom, Func<string, JsonObject> section)
    {
        var name = WardrobeStore.CleanName(arg("name"));
        if (name.Length < 2) throw new ArgumentException("дай вещи имя (name): «худи», «пижамные штаны», «гольфы в полоску»");
        var spec = new JsonObject { ["title"] = arg("title").Length > 0 ? arg("title") : name };
        if (arg("look").Length > 0) spec["look"] = arg("look");
        string At(string key, string[] allowed, string d, string what)
        {
            var v = arg(key);
            if (v.Length == 0) return d;
            return Wardrobe.SewAt(v, allowed) ?? throw new ArgumentException($"{key} «{v}» — {what}: {string.Join(", ", allowed)}");
        }
        void Order(string from, string to, string what)
        {
            if (Array.IndexOf(SewOrder, from) >= Array.IndexOf(SewOrder, to)) throw new ArgumentException($"{what}: «{from}» должно быть выше «{to}»");
        }
        if (arg("torso_from").Length > 0 || arg("torso_to").Length > 0 || (arg("sleeves").Length > 0 && Wardrobe.SewAt(arg("sleeves"), Wardrobe.SewSleeves) is not (null or "none")))
        {
            var from = At("torso_from", Wardrobe.SewTorsoFrom, "neck", "верх откуда");
            var to = At("torso_to", Wardrobe.SewTorsoTo, "hips", "верх докуда");
            Order(from, to, "верх");
            spec["torso"] = new JsonObject { ["from"] = from, ["to"] = to };
            spec["sleeves"] = At("sleeves", Wardrobe.SewSleeves, "none", "рукава");
        }
        if (arg("legs_from").Length > 0 || arg("legs_to").Length > 0)
        {
            var from = At("legs_from", Wardrobe.SewLegsFrom, "waist", "штанины откуда");
            var to = At("legs_to", Wardrobe.SewLegsTo, "ankle", "штанины докуда");
            Order(from, to, "штанины");
            spec["legs"] = new JsonObject { ["from"] = from, ["to"] = to };
        }
        if (arg("skirt_to").Length > 0)
            spec["skirt"] = new JsonObject
            {
                ["to"] = At("skirt_to", Wardrobe.SewSkirtTo, "thigh", "юбка докуда"),
                ["pleats"] = (int)Cm(arg("pleats"), 0, 48, 0), ["flare"] = Cm(arg("skirt_flare"), 0, 30, 5),
            };
        if (spec["torso"] is null && spec["legs"] is null && spec["skirt"] is null)
            throw new ArgumentException("скажи, что закрыть: torso_from/torso_to (+ sleeves) — верх, legs_from/legs_to — штаны, шорты, чулки, гольфы; skirt_to — юбка");
        spec["fit"] = Cm(arg("fit"), 0.15, 6, spec["legs"] is JsonObject lg && lg["from"]?.GetValue<string>() is "thigh" or "knee" or "calf" ? 0.25 : 0.8);
        spec["flare"] = Cm(arg("flare"), 0, 12, 0);
        spec["color"] = arg("color").Length > 0 ? WardrobeStore.Hex(arg("color")) : "#3a3a48";
        if (arg("color2").Length > 0) spec["color2"] = WardrobeStore.Hex(arg("color2"));
        if (arg("pattern").Length > 0)
            spec["pattern"] = Wardrobe.SewPatterns.FirstOrDefault(p => p.Id == arg("pattern").ToLowerInvariant() || Regex.IsMatch(arg("pattern").ToLowerInvariant(), p.Pattern)).Id
                              ?? throw new ArgumentException($"узора «{arg("pattern")}» нет — есть: {string.Join(", ", Wardrobe.SewPatterns.Select(p => p.Id))}");
        if (arg("pattern_size").Length > 0) spec["scale"] = Cm(arg("pattern_size"), 0.3, 30, 3);
        if (arg("trim").Length > 0) { spec["trim"] = WardrobeStore.Hex(arg("trim")); spec["trimWidth"] = Cm(arg("trim_width"), 0.2, 5, 0.8); }
        if (arg("glow").Equals("true", StringComparison.OrdinalIgnoreCase) || arg("glow") is "да" or "1") spec["glow"] = true;
        if (arg("shine").Length > 0) spec["shine"] = Cm(arg("shine"), 0, 1, 0);
        if (arg("opacity").Length > 0) spec["opacity"] = Cm(arg("opacity"), 0.15, 1, 1);
        if (arg("neckline").Length > 0)
            spec["neckline"] = Wardrobe.SewNecklines.FirstOrDefault(p => p.Id == arg("neckline").ToLowerInvariant() || Regex.IsMatch(arg("neckline").ToLowerInvariant(), p.Pattern)).Id
                               ?? throw new ArgumentException($"выреза «{arg("neckline")}» нет — есть: {string.Join(", ", Wardrobe.SewNecklines.Select(p => p.Id))}");
        section("sewn")[name] = spec;
        t.Changed = true;
        var text = $"Сшила {Wardrobe.SewText(name, spec)}.";
        if (arg("wear").Equals("false", StringComparison.OrdinalIgnoreCase)) return text + " Лежит в гардеробе: надеть — make с pieces (sewn:" + name + ") или sew ещё раз.";
        // куда надеть: свой наряд (названный или нынешний), иначе новый — нынешний наряд плюс эта вещь
        var target = arg("outfit").Length > 0 ? ResolveOutfit(w, arg("outfit")) ?? t.Current : t.Current;
        var own = custom(target);
        string outfitName;
        if (own is null)
        {
            outfitName = WardrobeStore.CleanName($"{target} {name}");
            var baseId = target is "kimono" or "classic" or "native" ? "pajama" : target;
            var start = BaseGarments.GetValueOrDefault(baseId, []);
            own = new JsonObject { ["base"] = baseId, ["title"] = $"{target} + {spec["title"]}", ["garments"] = new JsonArray(start.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray()) };
            if (baseId == "pajama" && target != "pajama") own["shoes"] = new JsonObject { ["style"] = "combat" };
            section("custom")[outfitName] = own;
        }
        else outfitName = target;
        var list = (own["garments"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList()
                   ?? BaseGarments.GetValueOrDefault(own["base"]?.GetValue<string>() ?? "gothic", []).ToList();
        var mine = Wardrobe.SewSlots(spec);
        var sewnAll = w["sewn"] as JsonObject;
        var removed = list.Where(g => g == "sewn:" + name || SlotsOf(g, sewnAll).Intersect(mine).Any()).ToList();
        list = list.Except(removed).Append("sewn:" + name).ToList();
        own["garments"] = new JsonArray(list.Select(g => (JsonNode)JsonValue.Create(g)!).ToArray());
        t.Wear = outfitName;
        return text + $" Надену в наряд «{outfitName}», как договорю{(removed.Count > 0 ? $" (вместо: {string.Join(", ", removed.Where(r => r != "sewn:" + name))})" : "")}. " +
               "Посмотри на себя (как ты выглядишь), ровно ли сидит; поправить — sew с тем же name.";
    }

    /// <summary>Ядро инструмента wardrobe над config.json в памяти (проверяется в --selftest на копии).</summary>
    internal static string WardrobeCore(WardrobeTurn t, string action, JsonObject args)
    {
        string Arg(string n) => args[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        try
        {
            var root = t.Root;
            if (root["wardrobe"] is not JsonObject w) { w = new JsonObject(); root["wardrobe"] = w; }
            JsonObject Section(string key) { if (w[key] is JsonObject o) return o; var n = new JsonObject(); w[key] = n; return n; }
            JsonObject? Custom(string id) => w["custom"]?[id] as JsonObject;
            switch (action.Trim().ToLowerInvariant())
            {
                case "list":
                    return WardrobeList(root, t.Current);
                case "wear":
                {
                    var id = ResolveOutfit(w, Arg("name"));
                    if (id is null) return $"[НЕ СРАБОТАЛО] Нет наряда «{Arg("name")}».\n{WardrobeList(root, t.Current)}";
                    t.Wear = id;
                    return $"Переоденешься в «{id}», как договоришь: {Wardrobe.Look(id)}. Скажи об этом коротко, живо.";
                }
                case "shoes":
                {
                    var style = ShoeStyle(Arg("style")) ?? throw new ArgumentException($"не знаю такую обувь «{Arg("style")}» — {string.Join(", ", WardrobeStore.Shoes.Select(x => x.Id))}");
                    var id = Arg("outfit").Length > 0 ? ResolveOutfit(w, Arg("outfit")) ?? throw new ArgumentException($"нет наряда «{Arg("outfit")}»") : t.Current;
                    var shoe = new JsonObject { ["style"] = style };
                    if (Arg("color").Length > 0) shoe["color"] = WardrobeStore.Hex(Arg("color"));
                    if (Arg("accent").Length > 0) shoe["accent"] = WardrobeStore.Hex(Arg("accent"));
                    if (Custom(id) is { } own) own["shoes"] = shoe; else Section("shoes")[id] = shoe;
                    t.Changed = true;
                    return $"Обувь к «{id}»: {Wardrobe.ShoesText(style, shoe["color"]?.GetValue<string>())}. Сменится, как договоришь (страница перерисуется сама).";
                }
                case "hat":
                {
                    var kind = HatKind(Arg("kind")) ?? throw new ArgumentException($"не знаю такую шляпу «{Arg("kind")}» — {string.Join(", ", WardrobeStore.Hats.Select(x => x.Id))}");
                    var id = Arg("outfit").Length > 0 ? ResolveOutfit(w, Arg("outfit")) ?? throw new ArgumentException($"нет наряда «{Arg("outfit")}»") : t.Current;
                    var hat = new JsonObject { ["kind"] = kind };
                    if (Arg("color").Length > 0) hat["color"] = WardrobeStore.Hex(Arg("color"));
                    if (Arg("accent").Length > 0) hat["accent"] = WardrobeStore.Hex(Arg("accent"));
                    if (Custom(id) is { } own) own["hat"] = hat; else Section("hat")[id] = hat;
                    t.Changed = true;
                    return kind == "none" ? $"Шляпу с «{id}» сняла — как договоришь." : $"Шляпа к «{id}»: {Wardrobe.HatText(kind, hat["color"]?.GetValue<string>())}. Сменится, как договоришь.";
                }
                case "make":
                {
                    var name = WardrobeStore.CleanName(Arg("name"));
                    if (name.Length < 2) throw new ArgumentException("дай наряду имя (name), хоть пару букв");
                    if (Wardrobe.All.Any(o => o.Id == name)) throw new ArgumentException($"«{name}» — имя встроенного наряда, придумай своё");
                    // вещи из разных нарядов: «топ от готики, косуха и легинсы от кибера» — по одной на место
                    var pieceWords = args["pieces"] switch
                    {
                        JsonArray arr => arr.Select(n => n is JsonValue pv && pv.TryGetValue<string>(out var ps) ? ps : "").Where(s => s.Length > 0).ToList(),
                        JsonValue pv when pv.TryGetValue<string>(out var ps) => ps.Split([',', ';', '+'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                        _ => [],
                    };
                    var pieces = new List<string>();
                    var sewnAll = w["sewn"] as JsonObject;
                    foreach (var word in pieceWords)
                    {
                        // своя сшитая вещь — по имени («sewn:худи» или просто «худи»), иначе готовая
                        var key = word.StartsWith("sewn:", StringComparison.Ordinal) ? word[5..] : WardrobeStore.CleanName(word);
                        var pid = sewnAll?[key] is JsonObject ? "sewn:" + key
                            : Wardrobe.PieceId(word) ?? throw new ArgumentException($"не знаю вещь «{word}» — есть: {string.Join(", ", Wardrobe.Pieces.Select(p => $"{p.Id} ({p.Text})"))}" +
                                                                                  (sewnAll is { Count: > 0 } ? $"; сшитые тобой: {string.Join(", ", sewnAll.Select(kv => kv.Key))}" : ""));
                        var slots = SlotsOf(pid, sewnAll);
                        if (pieces.FirstOrDefault(x => x != pid && SlotsOf(x, sewnAll).Intersect(slots).Any()) is { } clash)
                            throw new ArgumentException($"место «{string.Join("/", SlotsOf(clash, sewnAll).Intersect(slots))}» уже заняла {clash} — одна вещь на место (верх, куртка, низ, ноги, обвязка)");
                        if (!pieces.Contains(pid)) pieces.Add(pid);
                    }
                    var baseId = Arg("base").Length > 0 ? ResolveOutfit(null, Arg("base")) ?? throw new ArgumentException($"основа «{Arg("base")}» не найдена — {string.Join(", ", Wardrobe.All.Select(o => o.Id))}")
                        : pieces.Count > 0 ? Wardrobe.BaseOfPiece(pieces[0]) : "gothic";
                    if (Custom(baseId) is { } nested) baseId = nested["base"]?.GetValue<string>() ?? "kimono";
                    // кимоно и платье — это не слои по телу: вещи поверх них не надеваются, основа — голая готика
                    if (pieces.Count > 0 && baseId is "kimono" or "classic" or "native") baseId = Wardrobe.BaseOfPiece(pieces[0]);
                    var item = new JsonObject { ["base"] = baseId, ["title"] = Arg("title").Length > 0 ? Arg("title") : name };
                    if (pieces.Count > 0) item["garments"] = new JsonArray(pieces.Select(p => (JsonNode)JsonValue.Create(p)!).ToArray());
                    if (Arg("look").Length > 0) item["look"] = Arg("look");
                    if (Arg("shoes").Length > 0)
                    {
                        var shoe = new JsonObject { ["style"] = ShoeStyle(Arg("shoes")) ?? throw new ArgumentException($"не знаю обувь «{Arg("shoes")}»") };
                        if (Arg("shoes_color").Length > 0) shoe["color"] = WardrobeStore.Hex(Arg("shoes_color"));
                        if (Arg("shoes_accent").Length > 0) shoe["accent"] = WardrobeStore.Hex(Arg("shoes_accent"));
                        item["shoes"] = shoe;
                    }
                    if (Arg("hat").Length > 0)
                    {
                        var hat = new JsonObject { ["kind"] = HatKind(Arg("hat")) ?? throw new ArgumentException($"не знаю шляпу «{Arg("hat")}»") };
                        if (Arg("hat_color").Length > 0) hat["color"] = WardrobeStore.Hex(Arg("hat_color"));
                        item["hat"] = hat;
                    }
                    var colors = new JsonObject();
                    foreach (var (part, _) in WardrobeStore.Parts)
                        if (Arg(part).Length > 0) colors[part] = WardrobeStore.Hex(Arg(part));
                    if (colors.Count > 0) item["colors"] = colors;
                    Section("custom")[name] = item;
                    t.Changed = true;
                    if (!Arg("wear").Equals("false", StringComparison.OrdinalIgnoreCase)) t.Wear = name;
                    return $"Сшила наряд «{name}» " + (pieces.Count > 0 ? $"из вещей: {string.Join(", ", pieces.Select(p => PieceTitle(p, sewnAll)))}" : $"на основе «{baseId}»") +
                           $". {(Arg("wear") == "false" ? "Лежит в гардеробе." : "Наденешь, как договоришь.")} Скажи другу коротко и с гордостью, что сделала сама.";
                }
                case "sew":
                    return SewCore(t, w, Arg, Custom, Section);
                case "remove":
                {
                    // своя сшитая вещь — выкинуть из шкафа и из всех своих нарядов
                    if (w["sewn"] is JsonObject sewnBox && WardrobeStore.CleanName(Arg("name")) is var sn && sewnBox.ContainsKey(sn))
                    {
                        sewnBox.Remove(sn);
                        if (w["custom"] is JsonObject outfits)
                            foreach (var (_, o) in outfits)
                                if (o?["garments"] is JsonArray ga)
                                    o["garments"] = new JsonArray(ga.Select(g => g?.GetValue<string>()).Where(g => g is not null && g != "sewn:" + sn).Select(g => (JsonNode)JsonValue.Create(g!)!).ToArray());
                        t.Changed = true;
                        return $"Выкинула сшитую вещь «{sn}» (и из своих нарядов).";
                    }
                    var id = ResolveOutfit(w, Arg("name"));
                    if (id is null || Custom(id) is null) return $"[НЕ СРАБОТАЛО] Своего наряда «{Arg("name")}» нет — встроенные не удаляются.";
                    ((JsonObject)w["custom"]!).Remove(id);
                    t.Changed = true;
                    if (t.Current == id) t.Wear = "kimono";
                    return $"Выкинула «{id}» из гардероба (копия конфига осталась в web\\config.backups).";
                }
                case "tweak":
                {
                    var path = Arg("path");
                    if (!WardrobeStore.Tweakable.Any(x => x.Path == path))
                        return $"[НЕ СРАБОТАЛО] Так не правится: «{path}». Можно: {string.Join(", ", WardrobeStore.Tweakable.Select(x => x.Path))}";
                    JsonNode value = path == "bellVolume"
                        ? JsonValue.Create(Math.Clamp(double.TryParse(Arg("value").Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0.6, 0, 1))!
                        : WardrobeStore.Linear(Arg("value"));
                    var bits = path.Split('.');
                    if (bits.Length == 2) { if (root[bits[0]] is not JsonObject sub) { sub = new JsonObject(); root[bits[0]] = sub; } sub[bits[1]] = value; }
                    else root[path] = value;
                    t.Changed = true;
                    return $"Поправила «{WardrobeStore.Tweakable.First(x => x.Path == path).Title}» → {Arg("value")}. Страница перерисуется, как договоришь.";
                }
                case "editor":
                {
                    var exe = t.EditorExe;
                    if (!File.Exists(exe)) return "[НЕ СРАБОТАЛО] Редактора облика нет (editor_bin\\KlavaEditor.exe).";
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
                    return "Открыла другу редактор облика (KlavaEditor): там твоя модель, тело, кимоно, текстуры. Что в нём сохранят — ты подхватишь сама.";
                }
                default:
                    return "action: list | wear | shoes | hat | make | remove | tweak | editor";
            }
        }
        catch (ArgumentException ex) { return "[НЕ СРАБОТАЛО] " + ex.Message; }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return $"[НЕ СРАБОТАЛО] config.json: {ex.Message}"; }
    }

    // «переоденься в готику», «надень косуху», «смени наряд на платье», «давай в кимоно», «переодевайся давай»
    [GeneratedRegex(@"(переоде|переодень|надень|наден|нарядись|приоденься|смени\s+(наряд|одежд|прикид)|одень\w*\s+в|прикид|в\s+кимоно|в\s+готик|в\s+платье|сними\s+(кимоно|платье|косуху))", RegexOptions.IgnoreCase)]
    private static partial Regex DressAskRx();

    // она сама говорит, что переоделась: «переоделась», «надела», «накидываю кимоно», «наряжаюсь», «влезла в косуху»
    [GeneratedRegex(@"\b(переоделась|переодеваюсь|переоденусь|надела|надеваю|накидываю|накинула|нацепила|напяливаю|наряжаюсь|нарядилась|облачилась|влезла\s+в|влезаю\s+в|сменила\s+(наряд|прикид))", RegexOptions.IgnoreCase)]
    private static partial Regex SheDressesRx();

    /// <summary>
    /// Во что переодеться после её ответа, если мозг не поставил метку [наряд …] (друг 30.09: «переодеваться сама пока
    /// плохо получается» — она описывала юбочку словами и оставалась в косухе): по просьбе друга, а если в просьбе
    /// названия нет («ну переодевайся давай») или она сама сказала «переоделась» — по её же словам.
    /// </summary>
    internal static string? OutfitAfterReply(string userText, string reply, string current)
    {
        var asked = DressAskRx().IsMatch(userText);
        var pick = (asked ? Wardrobe.ByWords(userText) : null)
                   ?? (asked || SheDressesRx().IsMatch(reply) ? Wardrobe.ByWords(reply) : null);
        return pick is not null && pick != current ? pick : null;
    }
}
