using System.Text;

namespace KlavaDesk;

/// <summary>
/// Запись памяти без обрубков (30.09 22:40: Клаву закрыли, пока граф памяти писался прямо в graph.json — файл обрезало на
/// 250 КБ из 1,7 МБ, и при следующем запуске она бы сочла его битым и начала с нуля; так же писались банк, друзья и
/// история разговора). Порядок: текст целиком во временный файл рядом, сброс на диск, подмена одним действием —
/// прежний файл остаётся как .bak. Первый удачный файл каждого дня копируется в backups\ рядом. Чтение: основной файл,
/// не разобрался — .bak, потом свежая дневная копия; битый основной не трётся, а откладывается с пометкой .broken.
/// </summary>
internal static class SafeFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    /// <param name="snapshot">Копия дня в backups\ (для памяти — да; для журнала, который только дописывается, — нет).</param>
    public static void Write(string file, string text, bool snapshot = true)
    {
        var dir = Path.GetDirectoryName(file)!;
        Directory.CreateDirectory(dir);
        var tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
        {
            fs.Write(Utf8.GetBytes(text));
            fs.Flush(true);
        }
        Swap(tmp, file);
        if (snapshot) Snapshot(file);
    }

    public static void WriteBytes(string file, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var tmp = file + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
        {
            fs.Write(bytes);
            fs.Flush(true);
        }
        Swap(tmp, file);
    }

    /// <summary>Подмена файла: прежний — в .bak. Файл на миг держит антивирус или индексатор — три попытки.</summary>
    private static void Swap(string tmp, string file)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (File.Exists(file)) File.Replace(tmp, file, file + ".bak", ignoreMetadataErrors: true);
                else File.Move(tmp, file);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(60);
            }
        }
    }

    /// <summary>Копия дня: backups\имя.ГГГГ-ММ-ДД.расш — первый записанный за день файл (то, с чем день начался).</summary>
    private static void Snapshot(string file)
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(file)!, "backups");
            var copy = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(file)}.{DateTime.Now:yyyy-MM-dd}{Path.GetExtension(file)}");
            if (File.Exists(copy)) return;
            Directory.CreateDirectory(dir);
            File.Copy(file, copy);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"память: копия дня {Path.GetFileName(file)} не записалась ({ex.Message})");
        }
    }

    /// <summary>
    /// Прочитать и разобрать: основной файл, потом .bak, потом дневные копии от свежей к старой. parse бросает или
    /// возвращает null — берём следующую. Битый основной откладывается как .broken-время (не стирается).
    /// </summary>
    public static T? Load<T>(string file, Func<string, T?> parse) where T : class
    {
        foreach (var (path, what) in Candidates(file))
        {
            if (!File.Exists(path)) continue;
            try
            {
                if (parse(File.ReadAllText(path, Utf8)) is { } got)
                {
                    if (path != file)
                    {
                        // основной снова цел: иначе следующая запись увела бы битый в .bak поверх целого
                        File.Copy(path, file, overwrite: true);
                        Log.Write($"память: {Path.GetFileName(file)} поднят из {what} ({Path.GetFileName(path)})");
                    }
                    return got;
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or FormatException or InvalidOperationException)
            {
                Log.Write($"память: {Path.GetFileName(path)} не читается ({ex.Message})");
            }
            if (path == file) SetAside(file);
        }
        return null;
    }

    private static IEnumerable<(string Path, string What)> Candidates(string file)
    {
        yield return (file, "основного");
        yield return (file + ".bak", "прошлой записи");
        var dir = Path.Combine(Path.GetDirectoryName(file)!, "backups");
        if (!Directory.Exists(dir)) yield break;
        var name = Path.GetFileNameWithoutExtension(file);
        foreach (var copy in Directory.GetFiles(dir, $"{name}.*{Path.GetExtension(file)}").OrderByDescending(x => x, StringComparer.Ordinal))
            yield return (copy, "копии дня");
    }

    /// <summary>Битый файл — в сторону, под своим именем с пометкой; следующей записи он не мешает и остаётся для разбора.</summary>
    public static void SetAside(string file)
    {
        try
        {
            if (File.Exists(file)) File.Copy(file, $"{file}.broken-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"память: битый {Path.GetFileName(file)} не отложился ({ex.Message})");
        }
    }
}
