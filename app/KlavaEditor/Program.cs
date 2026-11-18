namespace KlavaEditor;

/// <summary>
/// Редактор облика Клавы: окно с WebView2, в нём web\editor.html — та же модель и тот же config.json,
/// что у живой Клавы. Хост только пишет файлы и открывает диалоги, всё редактирование — на странице.
///   KlavaEditor.exe            — обычный запуск
///   KlavaEditor.exe --export   — без окна-диалогов собрать GLB для Blender и пакет для VRoid и выйти
///   KlavaEditor.exe --swap "x.blend|x.vrm" / --restore — примерить модель на живую Клаву / вернуть оригинал
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var web = Paths.FindWeb();
        if (web is null)
        {
            MessageBox.Show("Не нашла папку web с editor.html рядом с программой.\nЗадай KLAVA_HOME — корень Neko.", "Редактор Клавы",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        // примерка модели на Клаву и возврат — без окна, итог в data\logs\swap.log
        if (args.Contains("--swap") || args.Contains("--restore")) return Swap.Run(web, args);
        var auto = args.Contains("--export");
        var form = new EditorForm(web, auto);
        Application.Run(form);
        return form.ExitCode;
    }
}

/// <summary>Где что лежит: папка web Клавы и папка «3D редактор аватара» на рабочем столе.</summary>
internal static class Paths
{
    /// <summary>web с editor.html: KLAVA_HOME\web или вверх от exe (exe лежит в Neko\editor_bin).</summary>
    public static string? FindWeb()
    {
        var home = Environment.GetEnvironmentVariable("KLAVA_HOME");
        if (!string.IsNullOrWhiteSpace(home) && File.Exists(Path.Combine(home, "web", "editor.html"))) return Path.Combine(home, "web");
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "web", "editor.html"))) return Path.Combine(d.FullName, "web");
        return null;
    }

    public static string Output => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "3D редактор аватара");

    /// <summary>Подпапки выгрузки по ключу со страницы.</summary>
    public static string? OutputFolder(string key) => key switch
    {
        "root" => Output,
        "blender" => Path.Combine(Output, "Для Blender"),
        "vroid" => Path.Combine(Output, "Для VRoid Studio"),
        "textures" => Path.Combine(Output, "Текстуры"),
        "shots" => Path.Combine(Output, "Скриншоты"),
        _ => null,
    };

    private static readonly char[] Bad = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Относительное имя со страницы («Текстуры Виты/лицо.png») → путь внутри base. Никаких «..», дисков и
    /// абсолютных путей: страница может писать только в свои папки.
    /// </summary>
    public static string? SafeJoin(string baseDir, string relative)
    {
        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 3) return null;
        foreach (var p in parts)
            if (p is "." or ".." || p.IndexOfAny(Bad) >= 0 || p.EndsWith(' ') || p.EndsWith('.')) return null;
        var full = Path.GetFullPath(Path.Combine([baseDir, .. parts]));
        var root = Path.GetFullPath(baseDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
