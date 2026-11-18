using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Проверки без живой Клавы: `--presence-test` — разбор фраз «кто ушёл» на настоящих репликах из журнала 02.10;
/// `--game-look &lt;папка|кадр&gt;` — сколько ярких красных/синих пикселей (гильзы Бакшота) на кадрах;
/// `--game-apply` — счёт патронов Бакшота по выдуманным ответам глаз.
/// </summary>
internal static class GameWatchTest
{
    public static int Presence()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;   // вывод читает tools\build.ps1 -Test
        string[] profiles = ["Аксюта", "Кибер_Кот", "Лиса_Лис_или_Рыжий_нахуй", "Настя_Сова", "Паша"];
        People.Load(profiles, null);
        var fails = 0;
        void Case(string text, string? speaker, string[] away, string[] here, string[]? start = null)
        {
            var p = new Presence(null);
            foreach (var s in start ?? []) p.SetAway(s, "тест");
            var ch = p.Hear(text, speaker, profiles);
            var gotAway = p.AwayList().Select(a => a.Profile).OrderBy(x => x).ToArray();
            var want = away.OrderBy(x => x).ToArray();
            var ok = gotAway.SequenceEqual(want) && here.All(h => !p.IsAway(h));
            if (!ok) fails++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} «{text}» → ушли: [{string.Join(", ", gotAway)}]{(ok ? "" : $" (ждала [{string.Join(", ", want)}])")} | {string.Join("; ", ch)}");
        }
        Case("Да Лиса в комнате тоже нету, он на работу ушёл. А я про руку говорил, что когда я кровь сдаю и руку разгибаю резко, то она болеть начинает.", "Кибер_Кот", ["Лиса_Лис_или_Рыжий_нахуй"], []);
        Case("Клава, ты дурочка, вообще-то Паши в комнате, блять, нету. И это я с тобой разговариваю.", "Паша", ["Паша"], []);
        Case("Клава, Паши в комнате, нету вообще.", null, ["Паша"], []);
        Case("Клава, смотри, тебе уже сказали, что Паши в комнате нету. Лис ушёл на работу. Тебе движок подсовывает голос Паши. Так игнорируй то, что тебе движок наклейку Паши лепит.", "Кибер_Кот", ["Паша", "Лиса_Лис_или_Рыжий_нахуй"], []);
        Case("Смотри, в комнате сейчас Аксюта, Настя и Киберкот. Лиса нету, он ушёл на работу. Паши вообще со вчерашнего дня не было.", "Кибер_Кот", ["Паша", "Лиса_Лис_или_Рыжий_нахуй"], ["Аксюта", "Кибер_Кот", "Настя_Сова"]);
        Case("Алис говорит, его начальника на работе сегодня нету.", "Кибер_Кот", [], []);
        Case("Лис, нет, я не про то.", "Кибер_Кот", [], []);
        Case("Ну Лис, нет, давай потом.", "Кибер_Кот", [], []);
        Case("Лис вернулся с работы.", "Кибер_Кот", [], ["Лиса_Лис_или_Рыжий_нахуй"], ["Лиса_Лис_или_Рыжий_нахуй"]);
        Case("Лис тут?", "Кибер_Кот", ["Лиса_Лис_или_Рыжий_нахуй"], [], ["Лиса_Лис_или_Рыжий_нахуй"]);
        Case("Ля, ты можешь играть в эту игру.", "Кибер_Кот", [], []);
        Case("Всё, я на работу пошёл, вечером буду.", "Лиса_Лис_или_Рыжий_нахуй", ["Лиса_Лис_или_Рыжий_нахуй"], []);
        Case("Настя спать пошла.", "Аксюта", ["Настя_Сова"], []);
        Case("Аксюта и Настя тут спорят, кто прав.", "Кибер_Кот", [], []);
        Case("Паша пришёл, наливай.", "Кибер_Кот", [], ["Паша"], ["Паша"]);
        Case("Похоронили пидораса. Всё, следующий раунд.", "Кибер_Кот", [], []);
        // 10.10 16:30: уговор «когда зайдёт — скажу» вернул в комнату Пашу, который на работе
        Case("Слушай, он когда зайдёт, я тебе скажу, Клава... О, Паша пришёл.", "Кибер_Кот", ["Паша"], [], ["Паша"]);
        Case("Паша пришёл.", null, ["Паша"], [], ["Паша"]);                      // голос не узнан — не факт
        Case("Паша пришёл.", "Кибер_Кот", [], ["Паша"], ["Паша"]);               // узнанный человек из комнаты — факт
        Case("Лис на работе, будет вечером.", "Аксюта", ["Лиса_Лис_или_Рыжий_нахуй"], []);
        Case("Если Лис уйдёт, скажи мне.", "Аксюта", [], []);
        Case("Может, Паша уже пришёл?", "Кибер_Кот", ["Паша"], [], ["Паша"]);
        Console.WriteLine(fails == 0 ? "== присутствие: всё сходится" : $"== присутствие: ошибок {fails}");
        // Карамельдансен: метка мозга и просьба друга
        var cut = Brain.CutForTest("[радость] Ушки на макушку! [карамельдансен] У-у-уа-уа!");
        var dance = new (string Text, string? Want)[] { ("станцуй карамельдансен", "caramell"), ("давай карамельку", "caramell"), ("Клава, врубай Caramelldansen!", "caramell"),
            ("карамельдансен — это мем из нулевых", null), ("не танцуй карамельдансен", null), ("потанцуй", "danceBrief") };
        var okCut = cut.Any(p => p.Fx == "caramell") && cut.All(p => !p.Text.Contains('['));
        if (!okCut) fails++;
        Console.WriteLine($"{(okCut ? "ok  " : "FAIL")} метка [карамельдансен] → caramell");
        foreach (var (t, want) in dance)
        {
            var got = OverlayForm.PlaceOf(t);
            if (got != want) fails++;
            Console.WriteLine($"{(got == want ? "ok  " : "FAIL")} «{t}» → {got ?? "—"}");
        }
        // гардероб: наряд из вещей разных нарядов
        var root = (JsonObject)JsonNode.Parse("""{"wardrobe":{}}""")!;
        var turn = new OverlayForm.WardrobeTurn(root, "kimono", "");
        var made = OverlayForm.WardrobeCore(turn, "make", new JsonObject { ["name"] = "микс", ["pieces"] = new JsonArray("топ от готики", "косуха", "штаны от кибера") });
        var g = root["wardrobe"]?["custom"]?["микс"]?["garments"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
        var okMix = g.SequenceEqual(["offShoulder", "jacket", "leggings"]) && root["wardrobe"]!["custom"]!["микс"]!["base"]!.GetValue<string>() == "gothic" && turn.Wear == "микс";
        if (!okMix) fails++;
        Console.WriteLine($"{(okMix ? "ok  " : "FAIL")} наряд из вещей: [{string.Join(", ", g)}] — {made}");
        var clash = OverlayForm.WardrobeCore(new OverlayForm.WardrobeTurn(root, "kimono", ""), "make", new JsonObject { ["name"] = "два", ["pieces"] = new JsonArray("кроп", "кардиган") });
        var okClash = clash.Contains("НЕ СРАБОТАЛО");
        if (!okClash) fails++;
        Console.WriteLine($"{(okClash ? "ok  " : "FAIL")} два верха — отказ: {clash}");
        // швейная мастерская: худи поверх кибера — новый наряд «cyber худи», кроп-топ уступает место
        var sroot = (JsonObject)JsonNode.Parse("""{"wardrobe":{}}""")!;
        var st = new OverlayForm.WardrobeTurn(sroot, "cyber", "");
        var sewText = OverlayForm.WardrobeCore(st, "sew", new JsonObject
        {
            ["name"] = "худи", ["torso_from"] = "горло", ["torso_to"] = "бёдра", ["sleeves"] = "до запястья", ["fit"] = "2",
            ["color"] = "фиолетовый", ["pattern"] = "рубчик", ["trim"] = "белый", ["neckline"] = "водолазка",
        });
        var hood = sroot["wardrobe"]?["sewn"]?["худи"];
        var outfitG = sroot["wardrobe"]?["custom"]?[st.Wear ?? ""]?["garments"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
        var okSew = hood?["torso"]?["from"]?.GetValue<string>() == "high" && hood["torso"]!["to"]!.GetValue<string>() == "hips" && hood["sleeves"]!.GetValue<string>() == "wrist"
                    && hood["pattern"]!.GetValue<string>() == "rib" && st.Wear == "cyber худи" && outfitG.SequenceEqual(["jacket", "leggings", "sewn:худи"]);
        if (!okSew) fails++;
        Console.WriteLine($"{(okSew ? "ok  " : "FAIL")} sew худи → наряд «{st.Wear}» [{string.Join(", ", outfitG)}] — {sewText}");
        var st2 = new OverlayForm.WardrobeTurn(sroot, "cyber худи", "");
        var pants = OverlayForm.WardrobeCore(st2, "sew", new JsonObject { ["name"] = "пижамные штаны", ["legs_from"] = "талия", ["legs_to"] = "щиколотка", ["pattern"] = "клетка", ["color"] = "серый", ["color2"] = "красный" });
        var outfitG2 = sroot["wardrobe"]?["custom"]?["cyber худи"]?["garments"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
        var okPants = outfitG2.SequenceEqual(["jacket", "leggings", "sewn:худи", "sewn:пижамные штаны"]) && st2.Wear == "cyber худи";
        if (!okPants) fails++;
        Console.WriteLine($"{(okPants ? "ok  " : "FAIL")} sew штаны в тот же наряд: [{string.Join(", ", outfitG2)}] — {pants}");
        var bad = OverlayForm.WardrobeCore(new OverlayForm.WardrobeTurn(sroot, "cyber", ""), "sew", new JsonObject { ["name"] = "ерунда", ["legs_from"] = "щиколотка", ["legs_to"] = "колено" });
        var okBad = bad.Contains("НЕ СРАБОТАЛО");
        if (!okBad) fails++;
        Console.WriteLine($"{(okBad ? "ok  " : "FAIL")} штанины снизу вверх — отказ: {bad}");
        // капс-подписи whisper
        var caps = new[] { ("ТРЕВОЖНАЯ МУЗЫКА", true), ("МУЗЫКА", true), ("СМЕХ", false), ("Тревожная музыка", false), ("ок", false), ("GTA", false), ("Ну ВОТ", false) };
        foreach (var (t, want) in caps)
        {
            var got = Ears.IsCaptionCaps(t);
            if (got != want) fails++;
            Console.WriteLine($"{(got == want ? "ok  " : "FAIL")} капс «{t}» → {(got ? "подпись" : "речь")}");
        }
        return fails == 0 ? 0 : 1;
    }

    public static int Look(string path)
    {
        var files = Directory.Exists(path) ? Directory.GetFiles(path, "*.jpg").Concat(Directory.GetFiles(path, "*.png")).OrderBy(f => f).ToList() : [path];
        foreach (var f in files)
        {
            using var src = new Bitmap(f);
            using var small = new Bitmap(src, 640, (int)(640.0 * src.Height / src.Width));
            var (r, b) = GameWatch.Vivid(small);
            Console.WriteLine($"{Path.GetFileName(f)}: красных {r}, синих {b}");
            if (!Directory.Exists(path))
            {
                using var mid = new Bitmap(src, 1280, (int)(1280.0 * src.Height / src.Width));
                var text = new Eyes(IntPtr.Zero).ReadTextAsync(mid, 800, ruOnly: true).GetAwaiter().GetResult();
                Console.WriteLine($"  текст (ru): {text.Replace("\n", " | ")}");
            }
        }
        return 0;
    }

    /// <summary>
    /// `--game-watch-live [сек] [кадры…]`: окно «Buckshot Roulette» показывает кадры по очереди (каждые 3 с), GameWatch
    /// смотрит его настоящими глазами (захват окна, распознавание текста, мозг с глазами из klava.json) — в конце счёт и
    /// журнал. Кадр с подписью «shells» в имени — патроны на столе. Окно выходит вперёд (наблюдатель смотрит только
    /// активное окно игры), клавиш никуда не шлёт.
    /// </summary>
    public static int Live(string[] args)
    {
        var sec = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 30;
        var frames = args.Skip(1).Where(File.Exists).ToList();
        if (frames.Count == 0) { Console.WriteLine("нужны кадры: --game-watch-live 30 a.jpg b.jpg …"); return 2; }
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\game_watch_test.log"));
        ApplicationConfiguration.Initialize();
        using var form = new Form { Text = "Buckshot Roulette", FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Location = new Point(60, 60), Size = new Size(1280, 720), TopMost = true, BackColor = Color.Black };
        var box = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.StretchImage };
        form.Controls.Add(box);
        var images = frames.Select(f => Image.FromFile(f)).ToList();
        var i = 0;
        box.Image = images[0];
        var flip = new System.Windows.Forms.Timer { Interval = 3000 };
        flip.Tick += (_, _) => { i = (i + 1) % images.Count; box.Image = images[i]; form.Activate(); Console.WriteLine($"  кадр: {Path.GetFileName(frames[i])}"); };
        GameWatch? watch = null;
        var code = 1;
        form.Shown += async (_, _) =>
        {
            form.Activate();
            var eyes = new Eyes(IntPtr.Zero);
            watch = new GameWatch(cfg, () => eyes, new Brain(cfg, saveHistory: false), Path.Combine(Path.GetTempPath(), "klava_gamewatch_live"))
            {
                Say = text => { Console.WriteLine($"  СКАЗАЛА БЫ: {text}"); return true; },
            };
            Console.WriteLine(watch.Start(GameWatch.ById("buckshot"), form.Handle, form.Text));
            flip.Start();
            await Task.Delay(TimeSpan.FromSeconds(sec));
            flip.Stop();
            await Task.Delay(6000);   // последний ответ глаз
            var note = watch.ContextNote("сколько патронов?") ?? "";
            Console.WriteLine(note);
            Console.WriteLine(watch.History(null, 40));
            watch.Stop("тест");
            code = note.Contains("заряд №") ? 0 : 1;
            form.Close();
        };
        Application.Run(form);
        foreach (var im in images) im.Dispose();
        return code;
    }

    /// <summary>
    /// `--capture-test [часть заголовка] [кадров]`: снять окно новым захватом (Windows.Graphics.Capture), как это делают
    /// глаза игры, — время на кадр, размер; кадры в %TEMP%\klava_capture_test.
    /// </summary>
    public static int Capture(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var title = args.Length > 0 ? args[0] : "Claude";
        var n = args.Length > 1 && int.TryParse(args[1], out var k) ? k : 4;
        var h = Uia.FindWindow(title);
        if (h == IntPtr.Zero) { Console.WriteLine($"окно «{title}» не найдено"); return 1; }
        Console.WriteLine($"окно: «{Uia.WindowTitle(h)}»; захват окон поддерживается: {WindowCapture.Supported}");
        using var cap = WindowCapture.StartAsync(h).GetAwaiter().GetResult();
        if (cap is null) { Console.WriteLine("захват не дался"); return 1; }
        var dir = Path.Combine(Path.GetTempPath(), "klava_capture_test");
        Directory.CreateDirectory(dir);
        var ok = 0;
        for (var i = 0; i < n; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var bmp = cap.GrabAsync().GetAwaiter().GetResult();
            sw.Stop();
            if (bmp is null) { Console.WriteLine($"  кадр {i}: нет ({sw.ElapsedMilliseconds} мс)"); continue; }
            ok++;
            var file = Path.Combine(dir, $"frame{i}.png");
            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"  кадр {i}: {bmp.Width}×{bmp.Height} за {sw.ElapsedMilliseconds} мс → {file}");
            Thread.Sleep(1000);
        }
        Console.WriteLine(ok == n ? "== захват окна работает" : $"== кадров {ok} из {n}");
        return ok > 0 ? 0 : 1;
    }

    public static int Apply()
    {
        var root = Path.Combine(Path.GetTempPath(), "klava_gamewatch_test");
        var cfg = new KlavaConfig();
        var w = new GameWatch(cfg, () => null, new Brain(cfg, saveHistory: false), root);
        w.Start(GameWatch.ById("buckshot"), IntPtr.Zero, "Buckshot Roulette");
        void J(string json) { w.Apply((JsonObject)JsonNode.Parse(json)!); Thread.Sleep(3100); }
        J("""{"scene":"патроны на столе","shells":{"live":2,"blank":3}}""");
        J("""{"shells":{"live":2,"blank":3}}""");   // тот же показ на соседнем кадре — не новый заряд
        J("""{"shot":{"by":"you","at":"self","result":"blank"}}""");
        J("""{"shot":{"by":"dealer","at":"other","result":"live"},"hp":{"you":3,"dealer":4}}""");
        J("""{"peek":"live"}""");
        J("""{"ejected":"live","items":{"you":["лупа","пиво"],"dealer":["наручники"]}}""");
        var note = w.ContextNote("сколько патронов осталось?") ?? "";
        Console.WriteLine(note);
        w.Stop("тест");
        var ok = note.Contains("заряд №1") && note.Contains("осталось 2: 0 боевых, 2 холостых") && note.Contains("шанс, что следующий боевой, 0%");
        Console.WriteLine(ok ? "== Бакшот: счёт сходится" : "== Бакшот: счёт НЕ сходится");

        // катка 04.10: последний боевой первого заряда вылетел мимо глаз, новый заряд его затёр, жизни не пересчитывались,
        // смерть и новую жизнь она не заметила — «думает, у нас 2 хп, а одно; думает, что мы в прошлом раунде»
        var w2 = new GameWatch(cfg, () => null, new Brain(cfg, saveHistory: false), root);
        w2.Start(GameWatch.ById("buckshot"), IntPtr.Zero, "Buckshot Roulette");
        void K(string json) { w2.Apply((JsonObject)JsonNode.Parse(json)!); Thread.Sleep(3100); }
        w2.NewText("ПОДПИШИ ОТКАЗ ОТ ПРЕТЕНЗИЙ.");
        K("""{"shells":{"live":1,"blank":2},"hp":{"you":2,"dealer":2}}""");
        K("""{"shot":{"by":"you","at":"other","result":"blank"}}""");
        K("""{"shot":{"by":"dealer","at":"self","result":"blank"}}""");
        K("""{"shells":{"live":3,"blank":2}}""");
        var gapNote = w2.ContextNote("сколько у меня жизней?") ?? "";
        K("""{"shot":{"by":"you","at":"self","result":"live"}}""");
        var hitNote = w2.ContextNote("сколько у меня жизней?") ?? "";
        w2.NewText("ПОДНИМАЙСЯ, !\nНОЧЬ ТОЛЬКО НАЧАЛАСЬ.");
        var deadNote = w2.ContextNote("какой раунд?") ?? "";
        K("""{"shells":{"live":2,"blank":2},"hp":{"you":2,"dealer":2}}""");
        var freshNote = w2.ContextNote("сколько патронов?") ?? "";
        K("""{"turn":"you","items":{"you":["лупа","пиво"],"dealer":["ножовка"]}}""");
        var adviceLoupe = w2.BuckshotAdvice() ?? "";
        K("""{"peek":"live","items":{"you":["ножовка","пиво"],"dealer":["ножовка"]}}""");
        var adviceLive = w2.BuckshotAdvice() ?? "";
        w2.Stop("тест");
        Console.WriteLine($"-- совет с лупой: {adviceLoupe}\n-- совет после лупы: {adviceLive}");
        Console.WriteLine("-- после пропуска:\n" + gapNote + "\n-- после боевого в себя:\n" + hitNote + "\n-- после «поднимайся»:\n" + deadNote + "\n-- новый показ:\n" + freshNote);
        bool Check(string what, bool good) { Console.WriteLine($"  [{(good ? "ок" : "ПЛОХО")}] {what}"); return good; }
        // строка «Сейчас:» — счёт; ниже идут прошлые события, там «пропуск» законно остаётся
        static string Now(string note) => note.Split('\n').FirstOrDefault(l => l.StartsWith("Сейчас:")) ?? "";
        var ok2 = Check("пропущенный боевой замечен", gapNote.Contains("мимо глаз вылетело 1"))
                & Check("жизни после пропуска — под сомнением", gapNote.Contains("друг 2??") && gapNote.Contains("Дилер 2??"))
                & Check("боевой в себя снял заряд другу", hitNote.Contains("друг 1??") && hitNote.Contains("по счёту выстрелов"))
                & Check("смерть: старый счёт не выдаётся за правду", deadNote.Contains("друга убили") && deadNote.Contains("НЕ видела") && deadNote.Contains("друг не знаю"))
                & Check("лид: есть лупа — сначала лупа", adviceLoupe.StartsWith("сначала лупа"))
                & Check("лид: лупа показала боевой — ножовка и в Дилера", adviceLive.Contains("ножовкой") && adviceLive.Contains("в Дилера"))
                & Check("новый показ после смерти — чистый счёт без «пропуска»", Now(freshNote).Contains("заряд №3") && !Now(freshNote).Contains("мимо глаз") && Now(freshNote).Contains("друг 2 (табло"));
        Console.WriteLine(ok2 ? "== Бакшот 04.10: дыры и смерть замечает" : "== Бакшот 04.10: НЕ сходится");

        // катка 05.10: 1 боевой и 2 холостых, жизни 2/2, ход друга — она сказала «стреляй в себя», а там боевой.
        // Расчёт до конца заряда: в себя — друг теряет заряд в 2/3 случаев (−1/3), в Дилера — Дилер в 2/3 (+1/3)
        var self12 = GameWatch.BuckshotSolver.Shot(1, 2, 2, 2, me: true, atSelf: true);
        var other12 = GameWatch.BuckshotSolver.Shot(1, 2, 2, 2, me: true, atSelf: false);
        Console.WriteLine($"-- расчёт 1 боевой + 2 холостых: в себя {self12:+0.000;-0.000}, в Дилера {other12:+0.000;-0.000}");
        var w3 = new GameWatch(cfg, () => null, new Brain(cfg, saveHistory: false), root);
        w3.Start(GameWatch.ById("buckshot"), IntPtr.Zero, "Buckshot Roulette");
        w3.Fix((JsonObject)JsonNode.Parse("""{"live":1,"blank":2,"hp_you":2,"hp_dealer":2}""")!);
        var advice12 = w3.BuckshotAdvice() ?? "";
        w3.Fix((JsonObject)JsonNode.Parse("""{"live":0,"blank":2}""")!);
        var advice02 = w3.BuckshotAdvice() ?? "";
        w3.Fix((JsonObject)JsonNode.Parse("""{"live":2,"blank":1}""")!);
        var advice21 = w3.BuckshotAdvice() ?? "";
        w3.Stop("тест");
        Console.WriteLine($"-- совет 1+2: {advice12}\n-- совет 0+2: {advice02}\n-- совет 2+1: {advice21}");
        var ok3 = Check("расчёт 1+2: в себя −1/3, в Дилера +1/3", Math.Abs(self12 + 1.0 / 3) < 0.01 && Math.Abs(other12 - 1.0 / 3) < 0.01)
                & Check("лид 1+2 (холостых больше): в Дилера, а не в себя", advice12.StartsWith("стреляй в Дилера"))
                & Check("лид 0+2: только холостые — в себя", advice02.Contains("в себя"))
                & Check("лид 2+1: в Дилера", advice21.StartsWith("стреляй в Дилера"));
        Console.WriteLine(ok3 ? "== Бакшот 05.10: расчёт хода верный" : "== Бакшот 05.10: расчёт НЕ сходится");

        // катка 05.10/09.10: друг проговаривает ход вслух — его слова главнее глаз; смерть не пропускать никогда
        GameWatch Fresh(string fix)
        {
            var g = new GameWatch(cfg, () => null, new Brain(cfg, saveHistory: false), root);
            g.Start(GameWatch.ById("buckshot"), IntPtr.Zero, "Buckshot Roulette");
            if (fix.Length > 0) g.Fix((JsonObject)JsonNode.Parse(fix)!);
            return g;
        }
        string NoteOf(GameWatch g) => Now(g.ContextNote("сколько патронов?") ?? "");
        var w4 = Fresh("""{"live":3,"blank":2,"hp_you":2,"hp_dealer":2}""");
        var heard = w4.HearFriend("Я шмальнул в дилера холостым, а он в себя боевым.");
        var afterTwo = NoteOf(w4);
        var heardDmg = w4.HearFriend("Минус одна жизнь у дилера");
        var afterKill = NoteOf(w4);
        w4.HearFriend("Похоронили пидораса. Всё, следующий раунд.");
        var afterFuneral = NoteOf(w4);
        w4.Stop("тест");
        Console.WriteLine($"-- слова: {string.Join(" | ", heard)}\n   {afterTwo}\n-- урон: {string.Join(" | ", heardDmg)}\n   {afterKill}\n-- похороны:\n   {afterFuneral}");

        var w5 = Fresh("""{"live":2,"blank":2,"hp_you":3,"hp_dealer":3}""");
        var notDead = w5.HearFriend("Я не сдох ещё, держусь");
        var killed = w5.HearFriend("Всё, меня убили, сука");
        var afterMe = NoteOf(w5);
        w5.Stop("тест");
        Console.WriteLine($"-- «не сдох»: [{string.Join(" | ", notDead)}]; «меня убили»: [{string.Join(" | ", killed)}]\n   {afterMe}");

        var w6 = Fresh("");
        w6.Apply((JsonObject)JsonNode.Parse("""{"hp":{"you":2,"dealer":1}}""")!);
        w6.Apply((JsonObject)JsonNode.Parse("""{"hp":{"you":2,"dealer":0}}""")!);
        var afterZero = NoteOf(w6);
        w6.Stop("тест");
        var w7 = Fresh("");
        w7.Apply((JsonObject)JsonNode.Parse("""{"hp":{"you":1,"dealer":1}}""")!);
        w7.Apply((JsonObject)JsonNode.Parse("""{"hp":{"you":3,"dealer":4}}""")!);
        var afterJump = NoteOf(w7);
        var jumpHistory = w7.ContextNote("раунд?") ?? "";
        w7.Stop("тест");
        Console.WriteLine($"-- табло 0:\n   {afterZero}\n-- табло прыгнуло 1/1 → 3/4:\n   {afterJump}");

        var w8 = Fresh("""{"live":2,"blank":1,"hp_you":2,"hp_dealer":2}""");
        w8.Heard();
        w8.Apply((JsonObject)JsonNode.Parse("""{"shot":{"by":"you","at":"other","result":"unknown"}}""")!);
        var afterBang = NoteOf(w8);
        w8.Stop("тест");
        Console.WriteLine($"-- бах + неопознанный выстрел:\n   {afterBang}");

        var ok4 = Check("«…холостым, а он в себя боевым» — два выстрела", heard.Count(h => h.StartsWith("выстрел")) == 2 && afterTwo.Contains("осталось 3: 2 боевых, 1 холостых"))
                & Check("Дилер в себя боевым — у Дилера 1", afterTwo.Contains("Дилер 1 ("))
                & Check("«минус одна жизнь у дилера» с 1 до 0 — Дилер повержен, раунд 2", afterKill.Contains("Раунд 2 — Дилер повержен"))
                & Check("«похоронили… следующий раунд» — та же смерть, раунд не 3", afterFuneral.StartsWith("Сейчас: Раунд 2"))
                & Check("«не сдох» — не смерть", notDead.Count == 0)
                & Check("«меня убили» — друга убили", afterMe.Contains("друга убили"))
                & Check("табло Дилера 0 — Дилер повержен", afterZero.Contains("Раунд 2 — Дилер повержен"))
                & Check("табло 1/1 → 3/4 — смерть не видела, но поняла", jumpHistory.Contains("сам момент не видела") && afterJump.StartsWith("Сейчас: Раунд 2"))
                & Check("бах + неопознанный выстрел в Дилера — боевой", afterBang.Contains("осталось 2: 1 боевых, 1 холостых") && afterBang.Contains("Дилер 1 ("));
        Console.WriteLine(ok4 ? "== Бакшот 09.10: слова друга и смерти сходятся" : "== Бакшот 09.10: НЕ сходится");
        return ok && ok2 && ok3 && ok4 ? 0 : 1;
    }
}

