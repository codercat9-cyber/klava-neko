using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Гардероб в config.json страницы (друг 30.09: «добавь ей возможность делать себе самой вещи и лазить в редакторе»):
/// wardrobe.custom — её собственные наряды (основа, обувь, шляпа, расцветки частей, как она сама их описала),
/// wardrobe.shoes / wardrobe.hat — сменённые обувь и шляпа у встроенных нарядов; плюс правка облика теми же полями,
/// что меняет редактор (цвет волос, цвета кимоно, громкость колокольчика). Страница опрашивает config.json и
/// перерисовывается сама; перед записью — копия в web\config.backups, как у редактора.
/// </summary>
internal static partial class WardrobeStore
{
    public static readonly (string Id, string Title)[] Shoes =
    [
        ("combat", "берцы на шнуровке"), ("knee", "лаковые сапоги до колена с неоновым кантом"),
        ("ankle", "ботильоны на каблуке с пряжкой"), ("slippers", "пушистые тапочки-лапки"), ("native", "родные ботинки"),
    ];

    public static readonly (string Id, string Title)[] Hats = [("beret", "берет художника"), ("witch", "ведьминская шляпа"), ("none", "без шляпы")];

    /// <summary>Части наряда, которые можно перекрасить (что из них есть — зависит от основы).</summary>
    public static readonly (string Id, string Title)[] Parts =
        [("top", "верх"), ("skirt", "юбка"), ("jacket", "куртка"), ("legs", "ноги: легинсы, сетка"), ("harness", "верёвочная обвязка")];

    /// <summary>Что можно править «как в редакторе»: путь в config.json → что это.</summary>
    public static readonly (string Path, string Title)[] Tweakable =
    [
        ("hairTint", "цвет волос"), ("hairAccent", "отлив волос"), ("kimono.top", "кимоно: основной цвет"),
        ("kimono.hem", "кимоно: низ"), ("kimono.collar", "кимоно: воротник"), ("kimono.wave", "кимоно: волны"),
        ("kimono.petal", "кимоно: сакура"), ("kimono.obi", "кимоно: пояс оби"), ("kimono.gold", "кимоно: кайма"),
        ("kimono.zip", "кимоно: молния"), ("bellVolume", "громкость колокольчика (0–1)"),
    ];

    private static readonly (string Stem, string Hex)[] Named =
    [
        ("красн", "#c8102e"), ("алый", "#e0102e"), ("бордов", "#6d0f1f"), ("винн", "#7a1c2c"), ("вишн", "#8b1030"),
        ("черн", "#0d0d10"), ("чёрн", "#0d0d10"), ("бел", "#f2f2f2"), ("розов", "#ff6fb5"), ("голуб", "#6ec8ff"),
        ("син", "#1f4fd1"), ("зелен", "#2f9e44"), ("зелён", "#2f9e44"), ("изумруд", "#0f8a5f"), ("золот", "#e0ad2e"),
        ("серебр", "#c0c6cf"), ("фиолет", "#7b2fd6"), ("сирен", "#b39ddb"), ("лаванд", "#b8a6e8"), ("желт", "#f2d024"),
        ("жёлт", "#f2d024"), ("оранж", "#f07f1a"), ("рыж", "#e8762a"), ("коричн", "#5b3a21"), ("шоколад", "#4a2a18"),
        ("беж", "#d9c2a3"), ("сер", "#6b6f78"), ("неон", "#38e8ff"), ("бирюз", "#2ec4b6"), ("мят", "#7fe0c1"),
        ("персик", "#ffb38a"), ("пурпур", "#a0206e"), ("малин", "#d1175c"), ("хаки", "#6b6a3a"), ("оливк", "#6b6a3a"),
        ("red", "#c8102e"), ("black", "#0d0d10"), ("white", "#f2f2f2"), ("pink", "#ff6fb5"), ("blue", "#1f4fd1"),
        ("green", "#2f9e44"), ("gold", "#e0ad2e"), ("silver", "#c0c6cf"), ("purple", "#7b2fd6"), ("cyan", "#38e8ff"),
    ];

    public static string ConfigPath(KlavaConfig cfg) => Path.Combine(cfg.Abs(cfg.WebRoot), "config.json");

    /// <summary>Раздел wardrobe из config.json на момент последнего чтения/записи — для Look и ByWords.</summary>
    public static JsonObject? Snapshot { get; private set; }

    /// <summary>Перечитать раздел wardrobe (при старте и после записи).</summary>
    public static void Refresh(KlavaConfig cfg)
    {
        try { Snapshot = Load(cfg)["wardrobe"]?.DeepClone().AsObject(); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { Snapshot = null; }
    }

    public static JsonObject Load(KlavaConfig cfg) =>
        JsonNode.Parse(File.ReadAllText(ConfigPath(cfg)))?.AsObject() ?? throw new InvalidOperationException("config.json пустой");

    /// <summary>Записать config.json: старый — в web\config.backups (как редактор), новый — через временный файл.</summary>
    public static void Save(KlavaConfig cfg, JsonObject root)
    {
        var file = ConfigPath(cfg);
        var backups = Path.Combine(Path.GetDirectoryName(file)!, "config.backups");
        Directory.CreateDirectory(backups);
        if (File.Exists(file)) File.Copy(file, Path.Combine(backups, $"config_{DateTime.Now:yyyyMMdd_HHmmss}_klava.json"), overwrite: true);
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);
        Snapshot = root["wardrobe"]?.DeepClone().AsObject();
    }

    /// <summary>Её собственные наряды: имя → (основа, название, как выглядит).</summary>
    public static List<(string Name, string Base, string Title, string Look)> Custom(KlavaConfig cfg)
    {
        var res = new List<(string, string, string, string)>();
        try
        {
            if (Load(cfg)["wardrobe"]?["custom"] is JsonObject custom)
                foreach (var (name, v) in custom)
                    res.Add((name, v?["base"]?.GetValue<string>() ?? "kimono", v?["title"]?.GetValue<string>() ?? name, v?["look"]?.GetValue<string>() ?? ""));
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException) { }
        return res;
    }

    /// <summary>Цвет словами («бордовый», «золотой») или #rrggbb → #rrggbb; не понят — ArgumentException.</summary>
    public static string Hex(string s)
    {
        var t = s.Trim().ToLowerInvariant();
        if (HexRx().IsMatch(t)) return t.Length == 4 ? $"#{t[1]}{t[1]}{t[2]}{t[2]}{t[3]}{t[3]}" : t;
        foreach (var (stem, hex) in Named)
            if (t.Contains(stem, StringComparison.Ordinal)) return hex;
        throw new ArgumentException($"не поняла цвет «{s}» — скажи словом (бордовый, золотой, неоновый…) или #rrggbb");
    }

    /// <summary>#rrggbb → линейный [r, g, b] (как хранит редактор цвета кимоно и волос).</summary>
    public static JsonArray Linear(string hex)
    {
        double Lin(int c) { var v = c / 255.0; return Math.Round(v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4), 4); }
        var h = Hex(hex);
        return [Lin(Convert.ToInt32(h[1..3], 16)), Lin(Convert.ToInt32(h[3..5], 16)), Lin(Convert.ToInt32(h[5..7], 16))];
    }

    /// <summary>Имя своего наряда: буквы, цифры, пробел, дефис; до 30 знаков, строчными.</summary>
    public static string CleanName(string s)
    {
        var t = NameRx().Replace(s.Trim().ToLowerInvariant(), "");
        t = SpacesRx().Replace(t, " ").Trim();
        return t.Length > 30 ? t[..30].Trim() : t;
    }

    [GeneratedRegex(@"^#([0-9a-f]{3}|[0-9a-f]{6})$")]
    private static partial Regex HexRx();
    [GeneratedRegex(@"[^\p{L}\p{Nd} \-]")]
    private static partial Regex NameRx();
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRx();
}
