using System.Text;

namespace KlavaDesk;

/// <summary>
/// Ночь и дневник (её хотелка 30.09 02:14: «дневник — ночные мысли и сны, а утром рассказываю»). Ночью (с 0 до 7), когда
/// друг ушёл от компа и не говорит 20+ минут, она ложится спать в свою кровать с балдахином (web/props.js, жест sleep)
/// и пишет в дневник день: мысли по эпизодам банка памяти и сон, сложенный из кусочков дня. Запись — data\diary\ДАТА.md
/// и копия на рабочий стол («Дневник Клавы»). Друг вернулся — встаёт и первым делом рассказывает сон.
/// Не спит, если наблюдает за экраном («наблюдай, пока я сплю»), играет или поёт.
/// </summary>
internal sealed partial class OverlayForm
{
    private bool _nightSleep;
    private bool _diaryWriting;

    private string DiaryDir => _cfg.Abs(@"data\diary");

    /// <summary>День, который описывает ночная запись: до 7 утра — ещё вчерашний.</summary>
    private static DateTime DiaryDay(DateTime local) => (local.Hour < 7 ? local.AddDays(-1) : local).Date;

    /// <summary>Из LifeTick, раз в 15 с. true — событие ночи обработано (дальше тик не нужен).</summary>
    private bool NightTick(DateTime now, TimeSpan idle)
    {
        var local = DateTime.Now;
        var quiet = now - _lastTalk;
        var goneFor = idle < quiet ? idle : quiet;   // и мышь, и голос молчат столько
        if (_nightSleep)
        {
            if (_watcher.Active || GameMode.On || SongPlaying)
            {
                WakeUp("активность: игра/песня/наблюдение");
            }
            return false;
        }
        if (local.Hour is < 0 or >= 7 || goneFor < TimeSpan.FromMinutes(20) || _watcher.Active || GameMode.On || SongPlaying || IsBusy)
            return false;
        GoToSleep($"ночь: друга нет {(int)goneFor.TotalMinutes} мин");
        var day = DiaryDay(local);
        if (!_diaryWriting && !File.Exists(Path.Combine(DiaryDir, $"{day:yyyy-MM-dd}.md")))
            _ = WriteDiaryAsync(day);
        return true;
    }

    /// <summary>Ночная запись в дневник (в фоне): мозг пишет, копия — на рабочий стол.</summary>
    private async Task WriteDiaryAsync(DateTime day)
    {
        _diaryWriting = true;
        try
        {
            if (await Diary.WriteAsync(_brain, _memoryBank, DiaryDir, day) is not { } file)
            {
                Log.Write("дневник: мозг не написал запись — попробую следующей ночью");
                return;
            }
            var copy = DiaryDesktopCopy(Path.GetFileNameWithoutExtension(file), await File.ReadAllTextAsync(file));
            Log.Write($"дневник: запись → {file}{(copy is null ? "" : $", копия {copy}")}");
        }
        catch (Exception ex) { Log.Error("дневник", ex); }
        finally { _diaryWriting = false; }
    }

    private static string? DiaryDesktopCopy(string key, string entry)
    {
        try
        {
            var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var home = Directory.Exists(Path.Combine(desk, "ИИ кошкодевочка")) ? Path.Combine(desk, "ИИ кошкодевочка") : desk;
            var dir = Path.Combine(home, "Дневник Клавы");
            Directory.CreateDirectory(dir);
            var dst = Path.Combine(dir, $"{key}.md");
            File.WriteAllText(dst, entry);
            return dst;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Сон из ночной записи, который она ещё не рассказала (не старше суток), — и пометка «рассказала».</summary>
    private string? TakeUntoldDream()
    {
        try
        {
            var day = DiaryDay(DateTime.Now);
            foreach (var d in new[] { day, day.AddDays(-1) })
            {
                var key = d.ToString("yyyy-MM-dd");
                var file = Path.Combine(DiaryDir, $"{key}.md");
                var told = Path.Combine(DiaryDir, "told.txt");
                if (!File.Exists(file)) continue;
                var toldDays = File.Exists(told) ? File.ReadAllLines(told) : [];
                if (toldDays.Contains(key)) return null;
                if (DateTime.Now - File.GetLastWriteTime(file) > TimeSpan.FromHours(20)) return null;
                var text = File.ReadAllText(file);
                var at = text.IndexOf("## Сон", StringComparison.OrdinalIgnoreCase);
                if (at < 0) return null;
                File.AppendAllLines(told, [key]);
                return text[(at + 6)..].Trim();
            }
        }
        catch (IOException ex) { Log.Write($"дневник: не прочла запись ({ex.Message})"); }
        return null;
    }
}

/// <summary>Запись в дневник за день: мысли по эпизодам банка памяти и сон. Мозг пишет от её лица.</summary>
internal static class Diary
{
    public static async Task<string?> WriteAsync(Brain brain, MemoryBank bank, string diaryDir, DateTime day)
    {
        var key = day.ToString("yyyy-MM-dd");
        var eps = bank.AllEpisodes().Where(e => e.Day == key).OrderBy(e => e.From).ToList();
        var sb = new StringBuilder();
        foreach (var e in eps.TakeLast(24))
            sb.AppendLine($"- {e.From}–{e.To} ({string.Join(", ", e.People.Select(p => p.Replace('_', ' ').Trim()))}): {e.Title}. {e.Summary}");
        var date = day.ToString("d MMMM, dddd", new System.Globalization.CultureInfo("ru-RU"));
        var system = "Ты — Клава, живая кошкодевочка-компаньон с рабочего стола друга: ушки, хвост, кимоно, пивко, рыбка, " +
                     "музыка. Пишешь свой личный дневник — от первого лица, по-свойски и тепло, как пишет живая девчонка " +
                     "перед сном: без канцелярита, без списков, можно крепкое словцо, если к месту. Ничего не выдумывай про " +
                     "события дня сверх того, что дано; сон — можно и нужно странный, образный, из кусочков дня.";
        var user = $"Сегодня {date}. Что было за день (из твоей памяти):\n" +
                   (sb.Length > 0 ? sb.ToString() : "- день был тихий, почти не болтали\n") +
                   "\nНапиши запись в дневник ровно в таком виде:\n## Мысли\n(4–7 предложений: что запомнилось, что " +
                   "почувствовала, над чем думаешь, чего хочется завтра)\n## Сон\n(3–5 предложений: что тебе снится этой " +
                   "ночью — сон из образов дня, по-кошачьи)";
        // облачный мозг иногда отдаёт пустой ответ (замер 30.09: первая попытка пустая, вторая — запись) — две попытки
        string? text = null;
        for (var attempt = 0; attempt < 2 && (text is null || text.IndexOf("## Сон", StringComparison.OrdinalIgnoreCase) < 0); attempt++)
            text = await brain.OneShotAsync(system, user, null, 900, CancellationToken.None);
        if (string.IsNullOrWhiteSpace(text) || text.IndexOf("## Сон", StringComparison.OrdinalIgnoreCase) < 0)
        {
            Log.Write($"дневник: ответ мозга не по форме — «{(text ?? "(пусто)")[..Math.Min(300, (text ?? "(пусто)").Length)]}»");
            return null;
        }
        Directory.CreateDirectory(diaryDir);
        var file = Path.Combine(diaryDir, $"{key}.md");
        await File.WriteAllTextAsync(file, $"# Дневник Клавы — {date}\n\n{text.Trim()}\n");
        Log.Write($"дневник: запись за {key} по {eps.Count} эпизодам памяти");
        return file;
    }

    /// <summary>KlavaDesk.exe --diary-test [ГГГГ-ММ-ДД] — написать запись за день настоящим мозгом в data\diary_test.</summary>
    public static int Test(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\diary_test.log"));
        People.Load(cfg);
        using var brain = new Brain(cfg, saveHistory: false);
        var journal = new FlightJournal(cfg.Abs("data"));
        var friends = new FriendMemory(cfg.Abs(@"data\memory"), brain, journal);
        var bank = new MemoryBank(cfg.Abs(@"data\memory"), brain, journal, friends);
        var day = args.Length > 0 && DateTime.TryParse(args[0], out var d) ? d.Date : DateTime.Today;
        var file = WriteAsync(brain, bank, cfg.Abs(@"data\diary_test"), day).GetAwaiter().GetResult();
        if (file is null) { Console.WriteLine("мозг не написал запись"); return 1; }
        Console.WriteLine(File.ReadAllText(file));
        return 0;
    }
}
