using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Облик страницы (web\config.json) при обновлении. В репозитории лежит только web\config.default.json — облик «как у
/// автора»; config.json у каждого свой: туда Клава пишет свои вещи (craft), сшитые наряды (wardrobe), правки редактора.
/// Обновление сливает три версии: прошлый дефолт (base), свой (theirs) и новый дефолт (next): что друг не трогал —
/// берётся новое, что поменял — остаётся его, новые разделы доезжают, удалённое им не воскресает. Без прошлого
/// дефолта (первое обновление со старой раздачи) — его значения главнее, кроме настроек рендера.
/// </summary>
internal static class WebConfigMerge
{
    /// <summary>Настройки рендера и маски окна: при слиянии без прошлого дефолта всегда берутся новые.</summary>
    private static readonly HashSet<string> RenderKeys = new(StringComparer.Ordinal) { "maskCell", "maskIntervalMs", "supersample" };

    private static readonly JsonSerializerOptions Out = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // кириллица — как есть, не \uXXXX
    };

    public static JsonNode Merge(JsonNode? baseCfg, JsonNode? theirs, JsonNode next)
    {
        if (theirs is null) return next.DeepClone();
        return MergeNode(baseCfg, theirs, next, top: true, hasBase: baseCfg is not null) ?? next.DeepClone();
    }

    private static JsonNode? MergeNode(JsonNode? b, JsonNode? t, JsonNode? n, bool top, bool hasBase)
    {
        if (t is JsonObject to && n is JsonObject no)
        {
            var bo = b as JsonObject;
            var res = new JsonObject();
            foreach (var (key, nv) in no)
            {
                var bv = bo?[key];
                var inBase = bo is not null && bo.ContainsKey(key);
                if (!to.ContainsKey(key))
                {
                    // было в прошлом дефолте, а у друга нет — значит, убрал сам (выкинутая вещь): не возвращаем
                    if (hasBase && inBase) continue;
                    res[key] = nv?.DeepClone();
                    continue;
                }
                var tv = to[key];
                if (top && !hasBase && RenderKeys.Contains(key)) { res[key] = nv?.DeepClone(); continue; }
                res[key] = MergeNode(inBase ? bv : null, tv, nv, top: false, hasBase: hasBase && inBase);
            }
            // его собственные разделы и вещи, которых в дефолте нет
            foreach (var (key, tv) in to)
                if (!no.ContainsKey(key) && !(hasBase && bo is not null && bo.ContainsKey(key) && Same(bo[key], tv)))
                    res[key] = tv?.DeepClone();
            return res;
        }
        // лист (число, строка, массив) или поменялся тип: друг не трогал — новое, иначе его
        if (hasBase && Same(b, t)) return n?.DeepClone();
        return t?.DeepClone();
    }

    private static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    private static JsonNode? Read(string path)
    {
        if (path == "-" || !File.Exists(path)) return null;
        return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
    }

    /// <summary>
    /// `--web-config-merge base|- theirs next out`: слить облик (обновление, tools\setup.ps1). Пишет атомарно, прошлый
    /// config.json — в web\config.backups.
    /// </summary>
    public static int Run(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("--web-config-merge <прошлый дефолт|-> <свой config.json> <новый дефолт> <куда>");
            return 2;
        }
        try
        {
            var next = Read(args[2]) ?? throw new FileNotFoundException("нет нового дефолта", args[2]);
            var theirs = Read(args[1]);
            var merged = Merge(Read(args[0]), theirs, next);
            var outPath = Path.GetFullPath(args[3]);
            if (theirs is not null && File.Exists(outPath))
            {
                var backups = Path.Combine(Path.GetDirectoryName(outPath)!, "config.backups");
                Directory.CreateDirectory(backups);
                File.Copy(outPath, Path.Combine(backups, $"config_{DateTime.Now:yyyyMMdd_HHmmss}_update.json"), overwrite: true);
            }
            var tmp = outPath + ".tmp";
            File.WriteAllText(tmp, merged.ToJsonString(Out));
            File.Move(tmp, outPath, overwrite: true);
            Console.WriteLine(theirs is null ? "облик: взят новый дефолт" : args[0] == "-" || !File.Exists(args[0])
                ? "облик: свои вещи и наряды сохранены, новое добавлено (без прошлого дефолта)"
                : "облик: свои вещи и наряды сохранены, нетронутое обновлено");
            return 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine($"облик не слился: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Нет web\config.json (свежая установка, распаковка без него) — завести из дефолта.</summary>
    public static void EnsureExists(string webRoot)
    {
        var cfg = Path.Combine(webRoot, "config.json");
        var def = Path.Combine(webRoot, "config.default.json");
        if (File.Exists(cfg) || !File.Exists(def)) return;
        try { File.Copy(def, cfg); Log.Write("облик: config.json заведён из config.default.json"); }
        catch (IOException ex) { Log.Error("облик: config.json не завёлся", ex); }
    }

    /// <summary>`--selftest`: три случая слияния на маленьких конфигах.</summary>
    public static string? Test()
    {
        var b = JsonNode.Parse("""{"cameraFov":30,"maskCell":4,"craft":{"items":{"hat":{"c":1},"old":{"c":2}}},"wardrobe":{"custom":{"a":{"x":1}}}}""");
        var t = JsonNode.Parse("""{"cameraFov":30,"maskCell":4,"craft":{"items":{"hat":{"c":5},"mine":{"c":3}}},"wardrobe":{"custom":{"a":{"x":1},"my":{"y":2}}}}""");
        var n = JsonNode.Parse("""{"cameraFov":28,"maskCell":2,"tail":{"enabled":true},"craft":{"items":{"hat":{"c":1},"old":{"c":2},"phones":{"c":9}}},"wardrobe":{"custom":{"a":{"x":7},"pajama":{"z":1}}}}""")!;
        var m = Merge(b, t, n);
        string? Check(bool ok, string what) => ok ? null : what;
        var r = Check((int?)m["cameraFov"] == 28, "нетронутое не обновилось")
             ?? Check((int?)m["craft"]?["items"]?["hat"]?["c"] == 5, "своя правка затёрлась")
             ?? Check(m["craft"]?["items"]?["old"] is null, "выкинутая вещь воскресла")
             ?? Check(m["craft"]?["items"]?["mine"] is not null && m["craft"]?["items"]?["phones"] is not null, "своя или новая вещь пропала")
             ?? Check((int?)m["wardrobe"]?["custom"]?["a"]?["x"] == 7 && m["wardrobe"]?["custom"]?["my"] is not null && m["wardrobe"]?["custom"]?["pajama"] is not null, "наряды слились не так")
             ?? Check(m["tail"] is not null, "новый раздел не доехал");
        if (r is not null) return "три версии: " + r;
        var m2 = Merge(null, t, n);
        return Check((int?)m2["maskCell"] == 2, "без базы: рендер не обновился")
            ?? Check((int?)m2["cameraFov"] == 30, "без базы: своё затёрлось")
            ?? Check(m2["craft"]?["items"]?["old"] is not null && (int?)m2["craft"]?["items"]?["hat"]?["c"] == 5, "без базы: вещи слились не так");
    }
}
