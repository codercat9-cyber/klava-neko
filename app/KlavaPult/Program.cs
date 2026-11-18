namespace KlavaPult;

internal static class Program
{
    /// <summary>
    /// KlavaPult.exe — пульт. KlavaPult.exe --shot папка — снять каждую вкладку в PNG (папка\pult_N.png) и выйти:
    /// проверка вида без чужого экрана и картинки для README.
    /// </summary>
    [STAThread]
    private static void Main(string[] args)
    {
        var shotDir = args.Length >= 2 && args[0] == "--shot" ? args[1] : null;
        // один пульт на сессию: второй запуск просто выходит (первый уже открыт); снимок живёт рядом с открытым
        using var single = new Mutex(true, shotDir is null ? @"Local\KlavaPult.single" : @"Local\KlavaPult.shot", out var first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        // тёмные полосы прокрутки и поля ввода (в .NET 9 тёмный режим помечен экспериментальным — карточки и кнопки рисуем сами)
#pragma warning disable WFO5001
        Application.SetColorMode(SystemColorMode.Dark);
#pragma warning restore WFO5001
        Application.Run(new PultForm(shotDir));
    }
}
