using System.Text;

namespace KlavaDesk;

/// <summary>Журнал KlavaDesk: data\logs\klava.log (перезаписывается при каждом запуске).</summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _w;

    public static void Open(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        // прошлый запуск — в .prev.log: зависшую Клаву перезапускают, и без этого лог перед зависанием пропадал
        try { if (File.Exists(file)) File.Move(file, Path.ChangeExtension(file, ".prev.log"), overwrite: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        _w = new StreamWriter(new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        Write("KlavaDesk запущен");
    }

    public static void Write(string message)
    {
        lock (Gate) _w?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {message}");
    }

    /// <summary>
    /// Ошибка с местом: два верхних кадра стека и первый кадр из кода самой Клавы (у исключений WinForms верхние
    /// кадры — внутри WinForms). Без этого «ОШИБКА UI» из общего обработчика не найти — было «форму нельзя
    /// показать модально» на каждом ответе, и ни слова, откуда.
    /// </summary>
    public static void Error(string what, Exception ex)
    {
        var frames = (ex.StackTrace ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var at = frames.Take(2).ToList();
        if (frames.Skip(2).FirstOrDefault(f => f.Contains("KlavaDesk.")) is { } own) at.Add(own);
        Write($"ОШИБКА {what}: {ex.GetType().Name}: {ex.Message}{(at.Count > 0 ? " | " + string.Join(" | ", at) : "")}");
    }
}
