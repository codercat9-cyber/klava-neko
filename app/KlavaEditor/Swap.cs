using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KlavaEditor;

/// <summary>
/// Примерка модели на живую Клаву и возврат:
///   KlavaEditor.exe --swap "модель.blend|модель.vrm"   — .blend выгружается в VRM через Blender + аддон VRM,
///                                                        Клава переключается на неё;
///   KlavaEditor.exe --restore                           — вернуть оригинал.
/// Перед первой примеркой оригинал сохраняется: web\config.backups\ОРИГИНАЛ_config.json и копия на рабочий
/// стол («Модель\Оригинал Клавы (бэкап)» — конфиг и сама модель). Повторные примерки его не трогают.
/// Живая Клава замечает новый config.json и перезагружает вид сама (klava.js опрашивает файл).
/// Итог пишется в data\logs\swap.log — его показывает обёртка .ps1.
/// </summary>
internal static class Swap
{
    private const string OriginalName = "ОРИГИНАЛ_config.json";

    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static int Run(string web, string[] args)
    {
        var root = Path.GetDirectoryName(web)!;
        var logFile = Path.Combine(root, "data", "logs", "swap.log");
        Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
        var log = new StringBuilder();
        void Say(string s) => log.AppendLine(s);
        int code;
        try
        {
            var i = Array.IndexOf(args, "--swap");
            code = i >= 0
                ? SwapTo(web, i + 1 < args.Length ? args[i + 1] : "", Say)
                : Restore(web, Say);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Say($"ОШИБКА: {ex.Message}");
            code = 1;
        }
        File.WriteAllText(logFile, log.ToString(), new UTF8Encoding(true));
        return code;
    }

    /// <summary>Сохранить оригинал один раз: пока ОРИГИНАЛ_config.json нет — текущий конфиг и есть оригинал.</summary>
    private static string BackupOriginal(string web, Action<string> say)
    {
        var backups = Path.Combine(web, "config.backups");
        Directory.CreateDirectory(backups);
        var original = Path.Combine(backups, OriginalName);
        if (File.Exists(original)) { say($"Оригинал уже сохранён: {original}"); return original; }
        var cfgFile = Path.Combine(web, "config.json");
        File.Copy(cfgFile, original);
        // копия на рабочий стол — чтобы оригинал не потерялся, даже если что-то случится с папкой Клавы
        var desk = Path.Combine(Paths.Output, "Модель", "Оригинал Клавы (бэкап)");
        Directory.CreateDirectory(desk);
        File.Copy(cfgFile, Path.Combine(desk, "config.json"), overwrite: true);
        var model = ModelFile(web, JsonNode.Parse(File.ReadAllText(cfgFile))?["model"]?.GetValue<string>());
        if (model is not null && File.Exists(model)) File.Copy(model, Path.Combine(desk, Path.GetFileName(model)), overwrite: true);
        say($"Сохранила оригинал: {original}");
        say($"Копия на рабочем столе: {desk}");
        return original;
    }

    /// <summary>«./assets/Vita.vrm» из конфига → полный путь внутри web.</summary>
    private static string? ModelFile(string web, string? model) =>
        string.IsNullOrWhiteSpace(model) ? null : Paths.SafeJoin(web, model.Replace("./", "").Replace('/', Path.DirectorySeparatorChar));

    private static int SwapTo(string web, string input, Action<string> say)
    {
        if (string.IsNullOrWhiteSpace(input)) input = Path.Combine(Paths.Output, "Unskined.blend");
        input = Path.GetFullPath(input);
        if (!File.Exists(input)) { say($"Нет файла: {input}"); return 2; }
        var ext = Path.GetExtension(input).ToLowerInvariant();
        if (ext is not (".blend" or ".vrm")) { say("Примерить можно только .blend или .vrm"); return 2; }

        var original = BackupOriginal(web, say);
        var origCfg = JsonNode.Parse(File.ReadAllText(original))!.AsObject();
        var sourceVrm = ModelFile(web, origCfg["model"]?.GetValue<string>() ?? "./assets/Vita.vrm")
                        ?? throw new InvalidOperationException("в оригинальном конфиге странный путь модели");

        var assets = Path.Combine(web, "assets");
        Directory.CreateDirectory(assets);
        var name = SafeFileName(Path.GetFileNameWithoutExtension(input)) + ".vrm";
        var outVrm = Path.Combine(assets, name);
        if (ext == ".blend")
        {
            var blender = FindBlender() ?? throw new InvalidOperationException("не нашла Blender в C:\\Program Files\\Blender Foundation");
            var script = Path.Combine(AppContext.BaseDirectory, "blender", "export_vrm.py");
            if (!File.Exists(script)) throw new InvalidOperationException($"нет {script}");
            say($"Выгружаю {input} в VRM через {blender} …");
            var (exit, output) = RunBlender(blender, input, script, outVrm, sourceVrm);
            foreach (var line in output.Split('\n').Where(l => l.Contains("VRMEXPORT") || l.Contains("Error") || l.Contains("SystemExit")))
                say("  " + line.Trim());
            if (exit != 0 || !File.Exists(outVrm)) { say($"Blender не выгрузил VRM (код {exit})."); return 3; }
        }
        else if (!string.Equals(input, outVrm, StringComparison.OrdinalIgnoreCase)) File.Copy(input, outVrm, overwrite: true);

        // конфиг: новая модель; кимоно, ушки, хвост и прочий облик в ней уже свои — кодовые выключаем,
        // иначе наденутся вторые поверх; спрятанные куски и свои текстуры относились к старой модели
        var cfgFile = Path.Combine(web, "config.json");
        var cfg = JsonNode.Parse(File.ReadAllText(cfgFile))!.AsObject();
        BackupCurrent(web, cfgFile);
        cfg["model"] = $"./assets/{name}";
        foreach (var part in new[] { "kimono", "ears", "tail" })
        {
            if (cfg[part] is not JsonObject o) cfg[part] = o = new JsonObject();
            o["enabled"] = false;
        }
        cfg["hideMeshes"] = new JsonArray();
        cfg["bust"] = new JsonObject { ["scale"] = new JsonArray(1, 1, 1), ["shift"] = new JsonArray(0, 0, 0) };
        cfg["hairTint"] = new JsonArray(1, 1, 1);
        cfg.Remove("textureOverrides");
        cfg["примерка"] = $"{Path.GetFileName(input)} — вернуть оригинал: «Вернуть оригинальную Клаву»";
        File.WriteAllText(cfgFile, cfg.ToJsonString(Pretty), new UTF8Encoding(false));
        say($"Клава теперь в модели {name}.");
        say(Process.GetProcessesByName("KlavaDesk").Length > 0
            ? "Клава запущена — перерисуется сама через пару секунд."
            : "Клава не запущена — увидишь при следующем запуске.");
        return 0;
    }

    private static int Restore(string web, Action<string> say)
    {
        var original = Path.Combine(web, "config.backups", OriginalName);
        if (!File.Exists(original)) { say("Оригинал не сохранялся — значит, Клава и так в своей модели."); return 0; }
        var cfgFile = Path.Combine(web, "config.json");
        BackupCurrent(web, cfgFile);
        File.Copy(original, cfgFile, overwrite: true);
        say("Вернула оригинальную Клаву.");
        say(Process.GetProcessesByName("KlavaDesk").Length > 0 ? "Перерисуется сама через пару секунд." : "Увидишь при следующем запуске.");
        return 0;
    }

    private static void BackupCurrent(string web, string cfgFile)
    {
        var dir = Path.Combine(web, "config.backups");
        Directory.CreateDirectory(dir);
        File.Copy(cfgFile, Path.Combine(dir, $"config_{DateTime.Now:yyyyMMdd_HHmmss}.json"), overwrite: true);
    }

    private static string SafeFileName(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Select(c => bad.Contains(c) || c is '#' or '?' or '%' ? '_' : c).ToArray()).Trim();
        return clean.Length > 0 ? clean : "model";
    }

    /// <summary>Самый свежий Blender в Program Files.</summary>
    private static string? FindBlender()
    {
        var baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        if (!Directory.Exists(baseDir)) return null;
        return Directory.EnumerateDirectories(baseDir, "Blender*")
            .Select(d => Path.Combine(d, "blender.exe"))
            .Where(File.Exists)
            .OrderByDescending(p => Version.TryParse(Path.GetFileName(Path.GetDirectoryName(p))!.Replace("Blender", "").Trim(), out var v) ? v : new Version(0, 0))
            .FirstOrDefault();
    }

    private static (int Exit, string Output) RunBlender(string blender, string blend, string script, string outVrm, string sourceVrm)
    {
        var psi = new ProcessStartInfo(blender)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-b", blend, "--python", script, "--", outVrm, sourceVrm }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Blender не запустился");
        var err = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(TimeSpan.FromMinutes(5))) { p.Kill(entireProcessTree: true); return (-1, output + "\nBlender не уложился в 5 минут"); }
        return (p.ExitCode, output + err.Result);
    }
}
