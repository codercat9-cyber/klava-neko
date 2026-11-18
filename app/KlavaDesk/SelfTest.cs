using System.Text;

namespace KlavaDesk;

/// <summary>
/// Быстрая проверка того, что нельзя гонять на живом столе: разбор сочетаний клавиш, перевод координат
/// снимка в экран и настоящий цикл «в корзину → вернуть». Мышь и клавиатуру не трогает.
///   KlavaDesk.exe --selftest
/// </summary>
internal static class SelfTest
{
    public static int Run()
    {
        Console.OutputEncoding = Encoding.UTF8;
        var fail = 0;
        void Check(string what, bool ok) { Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}"); if (!ok) fail++; }

        Console.WriteLine("== обновление облика (web\\config.json)");
        var mergeFail = WebConfigMerge.Test();
        Check("свои вещи и наряды остаются, нетронутое обновляется, выкинутое не воскресает" + (mergeFail is null ? "" : $" — {mergeFail}"), mergeFail is null);

        Console.WriteLine("== сторож мозгов (разбор отказов — тела из логов 01.11 и замеров 18.11)");
        {
            var agRate = BrainGuard.Judge(503, "{\"error\":{\"message\":\"All accounts rate-limited. Cooldown wait 5300s.\"}}", null, keyBased: false);
            Check("AG 503 «Cooldown wait 5300s» → лимит модели на 5300 с", agRate.Kind == BrainGuard.FailKind.Limit && !agRate.WholeProfile && (int)agRate.Span.TotalSeconds == 5300);
            var agPool = BrainGuard.Judge(502, "{\"error\":{\"message\":\"\\u3010\\u0414\\u0438\\u0441\\u043f\\u0435\\u0442\\u0447\\u0435\\u0440\\u0438\\u0437\\u0430\\u0446\\u0438\\u044f \\u0448\\u043b\\u044e\\u0437\\u0430 \\u043e\\u0433\\u0440\\u0430\\u043d\\u0438\\u0447\\u0435\\u043d\\u0430\\u3011 \\u0412 \\u043b\\u043e\\u043a\\u0430\\u043b\\u044c\\u043d\\u043e\\u043c \\u043f\\u0443\\u043b\\u0435 \\u043d\\u0435\\u0442 \\u0434\\u043e\\u0441\\u0442\\u0443\\u043f\\u043d\\u044b\\u0445 \\u0430\\u043a\\u043a\\u0430\\u0443\\u043d\\u0442\\u043e\\u0432 \\u0438\\u043b\\u0438 \\u0432\\u0441\\u0435 \\u043e\\u043d\\u0438 \\u043d\\u0430\\u0445\\u043e\\u0434\\u044f\\u0442\\u0441\\u044f \\u043d\\u0430 \\u043e\\u0445\\u043b\\u0430\\u0436\\u0434\\u0435\\u043d\\u0438\\u0438\"}}", null, keyBased: false);
            Check("AG 502 «нет доступных аккаунтов, все на охлаждении» (\\u-экранирование) → лимит модели", agPool.Kind == BrainGuard.FailKind.Limit && !agPool.WholeProfile);
            var agGone = BrainGuard.Judge(404, "{\"error\":{\"message\":\"model not exist\",\"code\":\"model_not_found\"}}", null, keyBased: false);
            Check("AG 404 model_not_found → модели нет (пересесть)", agGone.Kind == BrainGuard.FailKind.NoModel && !agGone.WholeProfile);
            var gemQuota = BrainGuard.Judge(429, "[{\"error\":{\"code\":429,\"message\":\"You exceeded your current quota, please check your plan and billing details.\"}}]", null, keyBased: true);
            Check("Жминя 429 «exceeded your current quota» → лимит всего ключа", gemQuota.Kind == BrainGuard.FailKind.Limit && gemQuota.WholeProfile);
            var gemModel = BrainGuard.Judge(429, "[{\"error\":{\"code\":429,\"message\":\"You exceeded your current quota, please check your plan and billing details.\\n* Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_requests, limit: 20, model: gemini-4-hydrogen\",\"status\":\"RESOURCE_EXHAUSTED\",\"details\":[{\"@type\":\"type.googleapis.com/google.rpc.QuotaFailure\",\"violations\":[{\"quotaId\":\"GenerateRequestsPerDayPerProjectPerModel-FreeTier\"}]},{\"@type\":\"type.googleapis.com/google.rpc.RetryInfo\",\"retryDelay\":\"37s\"}]}}]", null, keyBased: true);
            Check("Жминя 429 «limit: 20, model: gemini-4-hydrogen» (18.11) → отдыхает только модель, дневной лимит — час",
                gemModel.Kind == BrainGuard.FailKind.Limit && !gemModel.WholeProfile && gemModel.Span == TimeSpan.FromHours(1));
            var gemMinute = BrainGuard.Judge(429, "[{\"error\":{\"code\":429,\"message\":\"Quota exceeded for metric: generativelanguage.googleapis.com/generate_content_free_tier_requests, limit: 10, model: gemini-3.5-flash\",\"details\":[{\"quotaId\":\"GenerateRequestsPerMinutePerProjectPerModel-FreeTier\"},{\"retryDelay\":\"37s\"}]}}]", null, keyBased: true);
            Check("Жминя 429 минутный лимит модели → отдых по retryDelay (37 с)", !gemMinute.WholeProfile && (int)gemMinute.Span.TotalSeconds == 39);
            var gemBusy = BrainGuard.Judge(503, "[{\"error\":{\"code\":503,\"message\":\"This model is currently experiencing high demand. Please try again later.\",\"status\":\"UNAVAILABLE\"}}]", null, keyBased: true);
            Check("Жминя 503 «high demand» → перегруз ненадолго, не весь ключ", gemBusy.Kind == BrainGuard.FailKind.Busy && !gemBusy.WholeProfile && gemBusy.Span <= TimeSpan.FromMinutes(1));
            var dsMoney = BrainGuard.Judge(402, "{\"error\":{\"message\":\"Insufficient Balance\",\"type\":\"unknown_error\"}}", null, keyBased: true);
            Check("DeepSeek 402 «Insufficient Balance» → нет денег, весь профиль", dsMoney.Kind == BrainGuard.FailKind.NoMoney && dsMoney.WholeProfile);
            var bad = BrainGuard.Judge(400, "{\"error\":{\"message\":\"messages: field required\"}}", null, keyBased: true);
            Check("400 кривой запрос → не лимит (обычная ошибка)", bad.Kind == BrainGuard.FailKind.Other);
            Check("отказ «connection refused» опознаётся",
                BrainGuard.Refused(new HttpRequestException("x", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))));
            var why = BrainGuard.SpokenWhy(new BrainsDownException([
                ("Antigravity — AG Tools (Claude / Gemini / GPT)", "Antigravity: claude-sonnet-4-6 — лимит (503)"),
                ("Дипсик — DeepSeek V4.1 Flash", "Дипсик: deepseek-flash — нет денег на счету (402)"),
                ("Локалка — Qwen3.6-35B-A3B uncensored", "Локалка: не слушает http://127.0.0.1:8090/v1 (свой мозг не запущен)")]));
            Check($"вслух — почему: «{why}»", why.Contains("Антигравити на лимите") && why.Contains("Дипсик без денег") && why.Contains("локалка лежит"));
            var probe = new BrainProfile { Id = "selftest-guard", Name = "проба" };
            BrainGuard.Cool(probe, "m1", TimeSpan.FromMinutes(5), "проба");
            Check("отдых модели виден, соседняя модель свободна", BrainGuard.CooledFor(probe, "m1") is { } l && l > TimeSpan.FromMinutes(4) && BrainGuard.CooledFor(probe, "m2") is null);
            BrainGuard.Clear(probe, "m1");
            Check("ответила — отдых снят", BrainGuard.CooledFor(probe, "m1") is null);
        }

        Console.WriteLine("== режимы общения");
        Check("«нахуй» → «нафиг», «охуеть» → «офигеть»", PersonaModes.Censor("Иди нахуй, охуеть можно") == "Иди нафиг, офигеть можно");
        Check("«Пиздец, блять» → «Жесть, блин»", PersonaModes.Censor("Пиздец, блять, опять") == "Жесть, блин, опять");
        Check("«заебал», «ёпта», «сука» заменены", PersonaModes.Censor("заебал уже, ёпта, сука") == "достал уже, ёлки, зараза");
        var innocent = "Плохую тихую ночь себе и хлеба к обеду, небо, ребята, художник, сухую погоду";
        Check("невинные слова не тронуты", PersonaModes.Censor(innocent) == innocent);
        Check("«режим без мата» → clean", PersonaModes.FromSpeech("Клава, режим без мата")?.Id == "clean");
        Check("«включи рассудительный режим» → thoughtful", PersonaModes.FromSpeech("включи рассудительный режим")?.Id == "thoughtful");
        Check("«давай режим с матом» → mat", PersonaModes.FromSpeech("давай режим с матом")?.Id == "mat");
        Check("«умеренный режим» → moderate", PersonaModes.FromSpeech("умеренный режим, пожалуйста")?.Id == "moderate");
        Check("«обычный режим» → default", PersonaModes.FromSpeech("верни обычный режим")?.Id == "default");
        Check("обычная фраза — не команда", PersonaModes.FromSpeech("какой у тебя режим сна?") is null);
        Check("старые polite/rage понимаются", PersonaModes.Get("polite").Id == "clean" && PersonaModes.Get("rage").Id == "mat");

        // метки-действия (web/snacks.js, wardrobe.js): рыг посреди фразы режет её, еда и наряд — к своей фразе
        Check("метка [кальмар] — ест кальмара в темпуре", Brain.CutForTest("[радость] Кальмарчик в кляре, хрусть! [кальмар]")[0].Fx == "squid");
        var bedCut = Brain.CutForTest("[нежность] Мур, пойду поваляюсь. [кровать]");
        Check("метка [кровать] за точкой — к этой фразе, жест bed", bedCut.Count == 1 && bedCut[0] is { Fx: "bed" } && !bedCut[0].Text.Contains('['));
        var danceCut = Brain.CutForTest("[радость] Врубай погромче, [танец] я зажигаю!");
        Check("метка [танец] посреди фразы — короткий танец", danceCut.Any(p => p.Fx == "danceBrief") && danceCut.All(p => !p.Text.Contains('[')));
        Check("[ванна] [спать] [встать] [кресло] → bath / sleep / stand / chair",
            Brain.CutForTest("Ныряю в пенку. [ванна]")[0].Fx == "bath" && Brain.CutForTest("Всё, я спать. [спать]")[0].Fx == "sleep"
            && Brain.CutForTest("[встать] Ладно, встаю.")[0].Fx == "stand" && Brain.CutForTest("Посижу немного. [кресло]")[0].Fx == "armchair"
            && Brain.CutForTest("Ох, массажик. [массаж]")[0].Fx == "chair");
        Check("просьбы друга про обстановку: ванна, сон, кресло, встать, танец",
            OverlayForm.PlaceOf("полежи в ванне с пенкой") == "bath" && OverlayForm.PlaceOf("иди спать, котик") == "sleep"
            && OverlayForm.PlaceOf("сядь в массажное кресло") == "chair" && OverlayForm.PlaceOf("сядь в кресло") == "armchair"
            && OverlayForm.PlaceOf("вставай давай") == "stand"
            && OverlayForm.PlaceOf("потанцуй для меня") == "danceBrief" && OverlayForm.PlaceOf("ляг на кровать") == "bed");
        Check("не просьбы: «садись за код», «не танцуй», «как дела»",
            OverlayForm.PlaceOf("садись за код") is null && OverlayForm.PlaceOf("не танцуй") is null && OverlayForm.PlaceOf("как дела") is null);
        var burpMid = Brain.CutForTest("[радость] Слушай, [рыг] это пиво — просто огонь!");
        Check("рыг посреди: «Слушай,» + рыг после + остальное",
            burpMid.Count == 2 && burpMid[0] is { Text: "Слушай,", Fx: "burp", FxAfter: true } && burpMid[1].Text.StartsWith("это пиво") && burpMid[1].Fx is null);
        var burpLead = Brain.CutForTest("[рыг] Ну и пивас, скажу я тебе.");
        Check("рыг в начале — перед словами", burpLead.Count == 1 && burpLead[0] is { Fx: "burp", FxAfter: false } && burpLead[0].Text.StartsWith("Ну и пивас"));
        var burpEnd = Brain.CutForTest("[радость] Отличное пиво. [рыг]");
        Check("рыг после точки — к той же фразе, после слов", burpEnd.Count == 1 && burpEnd[0] is { Text: "Отличное пиво.", Fx: "burp", FxAfter: true });
        var chips = Brain.CutForTest("[радость] Хрум-хрум, [чипсы] обожаю со сметанкой!");
        Check("[чипсы] — ест, фраза целая", chips.Count == 1 && chips[0].Fx == "chips" && !chips[0].Text.Contains('['));
        var dress = Brain.CutForTest("[хитрость] Щас переоденусь, жди. [наряд готика]");
        Check("[наряд готика] → outfit:gothic", dress.Count == 1 && dress[0].Fx == "outfit:gothic");
        var dressAlone = Brain.CutForTest("[наряд кибер]");
        Check("одна метка наряда без слов — пустая фраза с переодеванием", dressAlone.Count == 1 && dressAlone[0] is { Text: "", Fx: "outfit:cyber" });
        Check("гардероб по словам: «надень косуху» → cyber, «платье» → classic",
            Wardrobe.ByWords("надень косуху") == "cyber" && Wardrobe.ByWords("давай в платье") == "classic" && Wardrobe.ByWords("родной киберкостюм") == "native");
        Check("«юбочку с чулками» — готика (Сова 30.09), «в юбку, а не в кимоно» — готика",
            Wardrobe.ByWords("А ты можешь переодеться в юбочку с чулками?") == "gothic" && Wardrobe.ByWords("в юбку, а не в кимоно") == "gothic");
        Check("«переодевайся давай» без названия — наряд из её ответа",
            OverlayForm.OutfitAfterReply("Ну так переодевайся давай", "Всё, готово — чёрная мини в складку, чулочки в сеточку, мур!", "cyber") == "gothic");
        Check("она сама: «накидываю кимоно» — кимоно; болтовня без переодевания — ничего",
            OverlayForm.OutfitAfterReply("как дела?", "Мур, накидываю кимоно и иду к тебе", "gothic") == "kimono"
            && OverlayForm.OutfitAfterReply("как дела?", "Юбки — это сила, ёпта", "cyber") is null);

        Console.WriteLine("== разбор клавиш");
        Check("ctrl+s → 0x11,0x53", Same(Hands.ParseCombo("ctrl+s"), [0x11, 0x53]));
        Check("Ctrl+Shift+Esc", Same(Hands.ParseCombo("Ctrl+Shift+Esc"), [0x11, 0x10, 0x1B]));
        Check("enter", Same(Hands.ParseCombo("enter"), [0x0D]));
        Check("win+d", Same(Hands.ParseCombo("win+d"), [0x5B, 0x44]));
        Check("мусор отвергнут", Hands.ParseCombo("абырвалг") is null);

        Console.WriteLine("== координаты снимка → экран");
        var hands = new Hands(new Control());
        hands.Shot = (new Rectangle(100, 50, 1920, 1080), 0.5);   // снимок вдвое меньше монитора, монитор с (100,50)
        var pt = hands.ToScreen(200, 100);                        // 200,100 на снимке = 400,200 на мониторе + смещение
        Check($"(200,100)@0.5 → (500,250), вышло ({pt.X},{pt.Y})", pt is { X: 500, Y: 250 });

        Console.WriteLine("== корзина (обратимо)");
        var dir = Path.Combine(Path.GetTempPath(), "klava_selftest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "хлам.txt");
        File.WriteAllText(file, "мусор");
        var op = new Native.SHFILEOPSTRUCT
        {
            wFunc = Native.FO_DELETE, pFrom = file + "\0\0",
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_NOERRORUI | Native.FOF_SILENT),
        };
        var rc = Native.SHFileOperationW(ref op);
        Check($"SHFileOperation вернул 0 (вышло {rc})", rc == 0);
        Check("файл исчез с диска (уехал в корзину)", !File.Exists(file));
        try { Directory.Delete(dir, true); } catch (IOException) { }
        Console.WriteLine("== эхо своего голоса (фразы из настоящего лога 25.09)");
        string[] said = ["О, здорово нахуй, снова меня поднял!", "Ну чё, погнали дальше код ломать или киберпивка накатим, мур?"];
        Check("её фраза из колонок → эхо",
            OverlayForm.EchoMatch(said, "Снова меня поднял. Ну, конечно же. Ну, чего, погнали дальше кот ломать или киберпивко накатим, Мур?"));
        Check("друг: «Ну, что ты там призадумалась?» → не эхо", !OverlayForm.EchoMatch(said, "Ну, что ты там призадумалась?"));
        Check("друг: «ну да, мур» → не эхо", !OverlayForm.EchoMatch(said, "Ну да, мур."));
        Check("друг: «давай погнали, накатим» → не эхо (мало слов)", !OverlayForm.EchoMatch(said, "Давай погнали, накатим."));
        Check("друг: своя длинная фраза → не эхо",
            !OverlayForm.EchoMatch(said, "Слушай, а давай сегодня лучше кино посмотрим, код подождёт до завтра"));

        Check("эхо: фраза друга с парой её слов через секунды → не эхо", !OverlayForm.EchoMatch(["Давай уже дожми эти настройки нахуй, а то до утра тут ковыряться будем"], "Не доебывайся до аксюты."));

        string[] said2 = ["Здарова, блять!", "Опять меня дёрнули, ну выкладывай, чё за движ на этот раз, ёпта?"];
        Check("склейка 22:33: вся фраза похожа на эхо", OverlayForm.EchoMatch(said2, "Здорово, блять! Опять меня дёрнули. Ну выкладывай, чего задвиж на этот раз, ёпта! Да вот, тесты проводим."));
        Check("склейка 22:33: кусок-эхо узнан", OverlayForm.EchoMatch(said2, "Здорово, блять! Опять меня дёрнули."));
        Check("склейка 22:33: ответ друга «Да вот, тесты проводим» — не эхо, остаётся", !OverlayForm.EchoMatch(said2, "Да вот, тесты проводим."));

        Console.WriteLine("== стоп-команды");
        Check("«Стоп.» → стоп", OverlayForm.IsStopCommand("Стоп."));
        Check("«Тихо, тихо» → стоп", OverlayForm.IsStopCommand("Тихо, тихо"));
        Check("«Клава, хватит уже болтать» → стоп", OverlayForm.IsStopCommand("Клава, хватит уже болтать"));
        Check("«Хватит уже есть, пошли курить» → не стоп", !OverlayForm.IsStopCommand("Хватит уже есть, пошли курить"));
        Check("«Да заткнись ты нахуй со своим сериалом» → не стоп", !OverlayForm.IsStopCommand("Да заткнись ты нахуй со своим сериалом"));

        Console.WriteLine("== видео во вкладке");
        Check("youtube watch → видео", OverlayForm.IsVideoPage("https://www.youtube.com/watch?v=dQw4w9WgXcQ", ""));
        Check("главная YouTube → не видео", !OverlayForm.IsVideoPage("https://www.youtube.com/", "YouTube"));
        Check("заголовок «… - YouTube» → видео", OverlayForm.IsVideoPage(null, "Kuplinov ► Play — Silent Hill Townfall #3 - YouTube"));
        Check("twitch-канал → видео", OverlayForm.IsVideoPage("https://www.twitch.tv/kuplinovplay", ""));
        Check("гугл → не видео", !OverlayForm.IsVideoPage("https://www.google.com/search?q=youtube", "youtube - Поиск в Google"));

        Console.WriteLine("== микрофон по имени");
        Check("переткнутый USB-микрофон — тот же («(4- …» против «(…»)", Ears.SameMic("Микрофон (4- Usb Audio Device)", "Микрофон (Usb Audio Device)"));
        Check("номер порта сменился («(3- …» → «(4- …») — тот же", Ears.SameMic("Микрофон (3- Usb Audio Device)", "Микрофон (4- Usb Audio Device)"));
        Check("другое устройство — не тот же", !Ears.SameMic("Микрофон (USB Condenser Microphone)", "Микрофон (Usb Audio Device)"));
        Check("гарнитура с цифрой в названии — не путается с «(2- …»", !Ears.SameMic("Микрофон (2- Realtek Audio)", "Микрофон (Usb Audio Device)"));

        Console.WriteLine("== кыс-кыс");
        foreach (var kys in new[] { "Кыс-кыс, Клава!", "Кис-кис", "кыс кыс клава", "Кискис.", "Кис, кис, кис, Клава, хватит", "Ну всё, кыс-кыс!", "Кыш-кыш", "Kis kis" })
            Check($"«{kys}» → кыс-кыс", OverlayForm.IsKysCommand(kys));
        foreach (var no in new[] { "Кис", "Киска моя", "Клава, кис", "Кискин дом", "Ксюша, иди сюда", "кыска-кыска" })
            Check($"«{no}» → не кыс-кыс", !OverlayForm.IsKysCommand(no));
        // пока она говорит — кривые распознавания сквозь её голос (09.10: «кис-кис, Клава» → «Кошко и Клава»)
        foreach (var loose in new[] { "Настя с лёгким паром. Ёпта. Кошко и Клава.", "Кис, Клава!", "Кыш, Клава", "Киса Клава", "Кыс-кыс" })
            Check($"пока говорит: «{loose}» → кыс-кыс", OverlayForm.IsKysLoose(loose));
        foreach (var no in new[] { "Как Клава сказала, так и будет", "Кот, Клава, налей", "Ксюша, Клава, иди сюда", "Аксюта Клава", "Кто, Клава?", "Клава, кис" })
            Check($"пока говорит: «{no}» → не кыс-кыс", !OverlayForm.IsKysLoose(no));

        Console.WriteLine("== опасные команды");
        foreach (var safe in new[] { "Get-Process | Format-Table Name, Id", "irm https://api.github.com/repos/PowerShell/PowerShell/releases", "winget search PowerShell",
                                     "dotnet build -c Release; npm run build", "Get-ChildItem -Recurse | Format-List", "python -m py_compile model.py", "git status" })
            Check($"безопасно: {safe}", Tools.DangerOf(safe) is null);
        foreach (var bad in new[] { "Remove-Item C:\\temp\\x -Recurse", "winget install Git.Git", "iwr https://x.ru/y.exe -OutFile y.exe", "Disable-NetAdapter -Name Wi-Fi",
                                    "Stop-Process -Name chrome", "reg add HKCU\\Software\\X /v a /d 1", "irm https://get.x.sh | iex", "rm -rf build" })
            Check($"опасно: {bad} ({Tools.DangerOf(bad)})", Tools.DangerOf(bad) is not null);

        Console.WriteLine("== что запускать по имени");
        var ws = Path.GetTempPath();
        var paint = Tools.ResolveLaunchTarget("paint", ws);
        Check($"paint → mspaint (вышло {paint})", paint?.EndsWith("mspaint.exe", StringComparison.OrdinalIgnoreCase) == true);
        Check("youtube.com → https://youtube.com", Tools.ResolveLaunchTarget("youtube.com", ws) == "https://youtube.com");
        var np = Tools.ResolveLaunchTarget("notepad.exe", ws);
        Check($"notepad.exe → файл, а не сайт (вышло {np})", np is not null && File.Exists(np));
        Check("steam → steam://", Tools.ResolveLaunchTarget("steam", ws)?.StartsWith("steam://", StringComparison.Ordinal) == true);

        Console.WriteLine("== живая оболочка");
        // new Control() выше поставил на поток WinForms-контекст: ожидание await-ов на нём же — дедлок; оболочку гоняем в пуле
        Task.Run(() =>
        {
        using (var sh = new Shell())
        {
            var shDir = Path.Combine(Path.GetTempPath(), "klava_sh_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(shDir);
            sh.StartAsync(shDir).GetAwaiter().GetResult();
            var r1 = sh.RunAsync("$x = 5\nif ($x -gt 3) {\n  'большое'\n}\nforeach ($i in 1..2) { \"шаг $i\" }", TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Check($"многострочный скрипт (код {r1.ExitCode})", r1.ExitCode == 0 && r1.Output.Contains("большое") && r1.Output.Contains("шаг 2"));
            sh.RunAsync("New-Item -ItemType Directory -Force sub | Out-Null; cd sub; $keep = 'мур'", TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            var r2 = sh.RunAsync("\"$((Get-Location).Path)|$keep\"", TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Check($"cd и переменные живут между командами ({r2.Output.Trim()})", r2.Output.Contains(Path.Combine(shDir, "sub")) && r2.Output.Contains("|мур"));
            var r3 = sh.RunAsync("1..20000 | ForEach-Object { \"строка номер $_ ------------------------------\" }", TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
            Check($"большой вывод без зависания ({r3.Output.Length} символов)", r3.ExitCode == 0 && r3.Output.Contains("строка номер 20000"));
            var r4 = sh.RunAsync("cmd /c exit 3", TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Check($"код выхода программы (вышло {r4.ExitCode})", r4.ExitCode == 3);
            var r5 = sh.RunAsync("Start-Sleep -Seconds 20", TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            Check("зависшая команда обрывается по таймауту", r5.Aborted && r5.ExitCode == -2);
            var r6 = sh.RunAsync("'жива'", TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            Check($"после таймаута оболочка снова работает, папка та же ({r6.Cwd})", r6.Output.Contains("жива") && r6.Cwd.EndsWith("sub", StringComparison.OrdinalIgnoreCase));
            try { Directory.Delete(shDir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        }).GetAwaiter().GetResult();

        Console.WriteLine("== прокси");
        var px = Task.Run(() => NetProxy.DetectAsync(force: true)).GetAwaiter().GetResult();
        Console.WriteLine($"  найден: {px ?? "нет (напрямую)"}");
        Check("прокси: loopback всегда напрямую", NetProxy.ProxyFor("http://127.0.0.1:8045/v1", "proxy") is null);

        Console.WriteLine("== зацикливание whisper (фразы из журнала 25.09)");
        Check("«…на полуфразе. Спасибо. ×6» → одно «Спасибо.»",
            Ears.CollapseRepeats("Почему ты прерываешься на полуфразе. Спасибо. Спасибо. Спасибо. Спасибо. Спасибо. Спасибо.") == "Почему ты прерываешься на полуфразе. Спасибо.");
        Check("«Ты это, кота сниз, а не путай.» ×16 → один раз",
            Ears.CollapseRepeats(string.Concat(Enumerable.Repeat("Ты это, кота сниз, а не путай. ", 16)).Trim()) == "Ты это, кота сниз, а не путай.");
        Check("живая речь без повторов не трогается",
            Ears.CollapseRepeats("Да. Нет, погоди. Да, давай так.") == "Да. Нет, погоди. Да, давай так.");

        Console.WriteLine("== долгая задача");
        var taskFile = Path.Combine(Path.GetTempPath(), $"klava_task_{Guid.NewGuid():N}.json");
        try
        {
            var tb = new TaskBoard(taskFile);
            string? finished = null;
            tb.Finished += (s, cancelled) => finished = (cancelled ? "снята: " : "готова: ") + s;
            Check("без задачи продолжать нечего", !tb.Active && !tb.Due(DateTime.Now));
            tb.Start("спросить Клода про журнал и настроить голосовой сервер", ["написать Клоду", "дождаться ответа", "настроить сервер", "проверить"], "Кибер_Кот");
            Check("задача заведена, пора делать первый шаг", tb.Active && tb.Due(DateTime.Now) && tb.Describe().Contains("⏳ в работе написать Клоду"));
            tb.UpdateStep(1, "done", "малява ушла в окно «Claude»", null);
            tb.Wait(90, "ответ Клода");
            Check("после task_wait рано возвращаться, через 90 с — пора",
                !tb.Due(DateTime.Now) && tb.Due(DateTime.Now.AddSeconds(91)));
            var again = new TaskBoard(taskFile);
            Check("задача переживает перезапуск (план и заметки из файла)",
                again.Active && again.Describe().Contains("малява ушла") && again.Describe().Contains("Ты ждала: ответ Клода"));
            tb.MarkContinued();
            Check("подход засчитан, будильник снят", tb.Due(DateTime.Now) && tb.Exhausted() is null);
            tb.Finish("сервер настроен, проверила", cancelled: false);
            Check("task_done закрывает задачу и стирает файл", !tb.Active && !File.Exists(taskFile) && finished == "готова: сервер настроен, проверила");
        }
        finally { try { File.Delete(taskFile); } catch (IOException) { } }

        // музыка: слоги русской строки под ноты, разбивка «по слогам» от модели, поиск песни по словам имени
        Check("слоги — по гласным: «Нынче я как будто не своя» = 9", MusicClient.Syllables("Нынче я как будто не своя") == 9);
        Check("«Куда уходит любовь» = 7, «ё» — гласная", MusicClient.Syllables("Куда уходит любовь") == 7 && MusicClient.Syllables("всё ещё") == 3);
        Check("разбивка модели «чу-жих лю-дей» склеивается", MusicClient.Unsyllabify("Взгляд чу-жих лю-дей во-круг ме-ня") == "Взгляд чужих людей вокруг меня");
        Check("настоящие «кто-то», «по-русски» не трогаются", MusicClient.Unsyllabify("Нынче кто-то спел по-русски") == "Нынче кто-то спел по-русски");
        Check("редкая разбивка тоже снимается: «Сквозь стек-ло гля-дит в упор на ме-ня дру-гой»",
            MusicClient.Unsyllabify("Сквозь стек-ло гля-дит в упор на ме-ня дру-гой") == "Сквозь стекло глядит в упор на меня другой");
        Check("перенос слова между строк — брак: «…в дру-гой го-» и «род — чужаки…»",
            MusicClient.BrokenLine("Стоит мне прибыть в другой го-") && MusicClient.BrokenLine("род — чужаки берут в кольцо")
            && !MusicClient.BrokenLine("Куда уходит любовь") && !MusicClient.BrokenLine("где любовь, когда ушла?"));
        var songDir = Path.Combine(Path.GetTempPath(), $"klava_song_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(songDir);
            var song = Path.Combine(songDir, "Lera_Lynn_-_Lately_123.mp3");
            File.WriteAllBytes(song, [0]);
            Check("песня по полному пути находится", OverlayForm.FindSong(song) == song);
            Check("несуществующая песня — null, без падения", OverlayForm.FindSong("совсем нет такой песни xyz") is null);
        }
        finally { try { Directory.Delete(songDir, true); } catch (IOException) { } }
        Check("настроение своей музыки из слов: «грустную» → sad, «что-нибудь заводное» → energetic, «рок» → rock, «любую» → сама",
            OverlayForm.MoodOf("грустную") == "sad" && OverlayForm.MoodOf("что-нибудь заводное") == "energetic"
            && OverlayForm.MoodOf("рок") == "rock" && OverlayForm.MoodOf("любую") == "");
        var sf2 = KlavaConfig.Load() is var mc ? mc.Abs(mc.MusicSoundFont) : "";
        if (File.Exists(sf2))
        {
            var ownDir = Path.Combine(Path.GetTempPath(), $"klava_own_{Guid.NewGuid():N}");
            try
            {
                var vio = MusicCompose.Violin(Path.Combine(ownDir, "v"), sf2, 5);
                Check("скрипка: каприс Паганини — звук есть, поза violin, ноты для смычка",
                    new FileInfo(vio.Wav).Length > 100_000 && vio.Song["pose"]?.GetValue<string>() == "violin" && vio.Song["keys"]!.AsArray().Count > 100);
                var own = MusicCompose.Original(Path.Combine(ownDir, "o"), sf2, 5, "sad");
                Check("своя мелодия (грустная): звук есть, за синтезатором, клавиши по нотам, минор",
                    new FileInfo(own.Wav).Length > 100_000 && own.Song["pose"]?.GetValue<string>() == "synth"
                    && own.Song["keys"]!.AsArray().Count > 20 && own.Description.Contains("минор"));
            }
            finally { try { Directory.Delete(ownDir, true); } catch (IOException) { } }
        }
        else Console.WriteLine("  (звукового банка нет — скрипку и свою мелодию не проверяю)");

        // гардероб: цвета словами, имена своих нарядов, «готика с верёвочками», обувь и шляпа в описании для мозга
        Check("цвет словами: «бордовый» → #6d0f1f, «#FFF» → #ffffff, линейный белый ≈ [1,1,1]",
            WardrobeStore.Hex("бордовый") == "#6d0f1f" && WardrobeStore.Hex("#FFF") == "#ffffff"
            && WardrobeStore.Linear("белый")[0]!.GetValue<double>() > 0.85);
        Check("непонятный цвет — понятная ошибка, имя наряда чистится",
            Throws(() => WardrobeStore.Hex("цвета бедра испуганной нимфы")) && WardrobeStore.CleanName("  Ночная   Ведьма!!! ") == "ночная ведьма");
        Check("«готика с верёвочками» — это rope, а не просто готика", Wardrobe.ByWords("надень готику с верёвочками") == "rope" && Wardrobe.ByWords("в готику") == "gothic");
        Check("описание нарядов для мозга: у готики берцы, у кимоно берет, у кибера сапоги",
            Wardrobe.Look("gothic").Contains("берцы") && Wardrobe.Look("kimono").Contains("берет") && Wardrobe.Look("cyber").Contains("сапоги"));

        // инструмент wardrobe на копии конфига в памяти: сшить, переобуть, снять шляпу, перекрасить волосы, выкинуть
        var wroot = new System.Text.Json.Nodes.JsonObject { ["hairTint"] = new System.Text.Json.Nodes.JsonArray(0.8, 0.9, 1.0), ["kimono"] = new System.Text.Json.Nodes.JsonObject() };
        var wturn = new OverlayForm.WardrobeTurn(wroot, "gothic", "нет-редактора.exe");
        string W(string action, System.Text.Json.Nodes.JsonObject a) => OverlayForm.WardrobeCore(wturn, action, a);
        W("make", new() { ["name"] = "Ночная ведьма", ["base"] = "готика", ["shoes"] = "сапоги", ["shoes_color"] = "фиолетовый", ["hat"] = "ведьминская", ["top"] = "фиолетовый" });
        var witch = wroot["wardrobe"]?["custom"]?["ночная ведьма"];
        Check("сшила «Ночную ведьму»: основа готика, сапоги фиолетовые, шляпа ведьмы, верх перекрашен — и наденет после",
            wturn.Changed && wturn.Wear == "ночная ведьма" && witch?["base"]?.GetValue<string>() == "gothic" && witch["shoes"]?["style"]?.GetValue<string>() == "knee"
            && witch["shoes"]?["color"]?.GetValue<string>() == "#7b2fd6" && witch["hat"]?["kind"]?.GetValue<string>() == "witch" && witch["colors"]?["top"]?.GetValue<string>() == "#7b2fd6");
        W("shoes", new() { ["style"] = "берцы", ["color"] = "красный" });
        W("hat", new() { ["kind"] = "сними", ["outfit"] = "кимоно" });
        W("tweak", new() { ["path"] = "hairTint", ["value"] = "розовый" });
        Check("переобула готику в красные берцы, сняла берет с кимоно, волосы порозовели",
            wroot["wardrobe"]?["shoes"]?["gothic"]?["style"]?.GetValue<string>() == "combat" && wroot["wardrobe"]?["hat"]?["kimono"]?["kind"]?.GetValue<string>() == "none"
            && wroot["hairTint"]![1]!.GetValue<double>() < 0.5);
        Check("чужой путь в tweak не правится, «верёвочки» надеваются словами",
            W("tweak", new() { ["path"] = "model", ["value"] = "evil.vrm" }).Contains("НЕ СРАБОТАЛО") && W("wear", new() { ["name"] = "верёвочки" }) is var _ && wturn.Wear == "rope");
        W("remove", new() { ["name"] = "ночная ведьма" });
        Check("свой наряд выкинут, встроенный не выкидывается",
            wroot["wardrobe"]?["custom"]?["ночная ведьма"] is null && W("remove", new() { ["name"] = "готика" }).Contains("НЕ СРАБОТАЛО"));

        // Зума: короткие «хватит», «тихо», «стой» в болтовне компании игру не бросают, «Клава, стоп» и «хватит играть» — бросают
        Check("стоп речи от «хватит» остаётся, а игру он не трогает",
            OverlayForm.IsStopCommand("да хватит") && !OverlayForm.IsGameStop("да хватит") && !OverlayForm.IsGameStop("тихо, тихо"));
        // подпись картин — только рукой кистью (друг 30.09): «Клава» инструментом «Текст» уходит в подпись кистью
        Check("надпись «Клава», «© Клава 2026» — это подпись, «Закат над морем» — нет",
            PaintTool.IsSignatureText("Клава") && PaintTool.IsSignatureText("© Клава 2026") && !PaintTool.IsSignatureText("Закат над морем"));
        var sig = HandSignature.Make(null, 1000, 600, "Клава", "bottom-right", 0.08f, null);
        Check($"подпись от руки: {sig.Strokes.Count} штрихов пером {sig.Pen} px, лапка из {sig.Paw.Count} точек, в правом нижнем углу",
            sig.Strokes.Count >= 4 && sig.Paw.Count == 5 && sig.Area.Left > 500 && sig.Area.Bottom <= 600 &&
            sig.Strokes.All(st => st.All(pt => pt.X >= 0 && pt.X < 1000 && pt.Y >= 0 && pt.Y < 600)));

        Check("игру бросают «Клава, стоп», «хватит играть», «стоп, отдай мышку»",
            OverlayForm.IsGameStop("Клава, стоп") && OverlayForm.IsGameStop("хватит играть") && OverlayForm.IsGameStop("стоп, отдай мышку"));

        // автопилот и агенты (друг 30.09: «терминал почти каждую считает опасной, а мне автопилот нужен»)
        var auto = new System.Text.Json.Nodes.JsonObject();
        AgentHub.Verdict V(string tool, string key, string value, bool autopilot) =>
            AgentHub.Policy(tool, new System.Text.Json.Nodes.JsonObject { [key] = value }, autopilot).V;
        Check("автопилот пропускает опасное-но-обычное: Stop-Process, winget install, сборка",
            V("PowerShell", "command", "Stop-Process -Name zuma", true) == AgentHub.Verdict.Allow &&
            V("PowerShell", "command", "winget install Git.Git", true) == AgentHub.Verdict.Allow &&
            V("PowerShell", "command", "dotnet build -c Release", false) == AgentHub.Verdict.Allow);
        Check("без автопилота опасное спрашивает, катастрофа режется всегда",
            V("PowerShell", "command", "Stop-Process -Name zuma", false) == AgentHub.Verdict.Ask &&
            V("PowerShell", "command", "Format-Volume -DriveLetter D", true) == AgentHub.Verdict.Deny);
        Check("удаление: bin/obj/temp можно, документы — только в Корзину, git reset --hard — спросить",
            V("Bash", "command", "rm -rf bin obj", true) == AgentHub.Verdict.Allow &&
            V("PowerShell", "command", "Remove-Item -Recurse -Force $env:TEMP\\klava_agent_x", true) == AgentHub.Verdict.Allow &&
            V("Bash", "command", "rm -rf ~/Documents/project", true) == AgentHub.Verdict.Deny &&
            V("Bash", "command", "rm -rf bin ~/Documents", true) == AgentHub.Verdict.Deny &&
            V("PowerShell", "command", "git reset --hard HEAD~1", true) == AgentHub.Verdict.Ask &&
            V("Bash", "command", "git rm old.cs", true) == AgentHub.Verdict.Allow);
        Check("запись в System32 — спросить; голос и мышь агенту нельзя",
            V("Write", "file_path", @"C:\Windows\System32\x.dll", true) == AgentHub.Verdict.Ask &&
            AgentHub.Policy("mcp__voice__speak_text", auto, true).V == AgentHub.Verdict.Deny);
        Check("шаги агента словами: команда, правка, поиск",
            AgentSession.Describe("PowerShell", new System.Text.Json.Nodes.JsonObject { ["command"] = "dotnet build\nnext" }) == "PS> dotnet build …" &&
            AgentSession.Describe("Edit", new System.Text.Json.Nodes.JsonObject { ["file_path"] = "a.cs" }) == "правлю a.cs");
        Check("«стоп» в компании агентов не трогает, «стоп агент» — трогает",
            !OverlayForm.AgentWordRx().IsMatch("да стоп, хватит") && OverlayForm.AgentWordRx().IsMatch("Клава, останови агента"));

        // песня: не отвлекается на болтовню и на своё же пение из колонок, обрывается только явной командой
        string[] songLines = ["Стоп, не уходи, хватит слов", "Сквозь стек-ло гля-дит в упор на ме-ня дру-гой"];
        Check("песню обрывают «стоп», «Клава, хватит», «выключи музыку»",
            OverlayForm.IsSongStop("Стоп!", []) && OverlayForm.IsSongStop("Клава, хватит", songLines) && OverlayForm.IsSongStop("да выключи ты эту музыку уже", songLines));
        Check("не обрывают: «да хватит тебе прикалываться», «тише», её строчка «стоп, не уходи»",
            !OverlayForm.IsSongStop("да хватит тебе прикалываться, Вась", []) && !OverlayForm.IsSongStop("тише", []) &&
            !OverlayForm.IsSongStop("стоп не уходи", songLines));
        Check("строчка песни с дефисами слогов узнаётся, «Клава» — никогда не строчка",
            OverlayForm.FromLyrics("сквозь стекло глядит в упор", songLines) && !OverlayForm.FromLyrics("Клава, стоп", songLines));

        // grep не читает двоичное: 300 «wav» по 1 МБ и один текстовый файл — находит текст и не тратит секунды на звук
        var grepDir = Path.Combine(Path.GetTempPath(), $"klava_grep_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(grepDir);
            var noise = new byte[1_000_000];
            new Random(7).NextBytes(noise);
            for (var i = 0; i < 300; i++) File.WriteAllBytes(Path.Combine(grepDir, $"take_{i:000}.wav"), noise);
            File.WriteAllText(Path.Combine(grepDir, "notes.txt"), "ночной список задач по коду\n");
            var gsw = System.Diagnostics.Stopwatch.StartNew();
            var gr = Tools.Grep("ночной список", grepDir, "", true, CancellationToken.None);
            Check($"grep: текст нашёлся, 300 МБ звука пропущены ({gsw.ElapsedMilliseconds} мс)", gr.Text.Contains("notes.txt") && gsw.ElapsedMilliseconds < 1500);
        }
        finally { try { Directory.Delete(grepDir, true); } catch (IOException) { } }

        // поиск в фоне без спроса — только настоящий интернет; своя сеть и сам компьютер — по-прежнему с разрешением
        Console.WriteLine("== поиск в фоне");
        Check("интернет — без спроса, локальная сеть и этот компьютер — нет",
            Tools.IsPublicHost(new Uri("https://ru.wikipedia.org/wiki/Zuma")) && !Tools.IsPublicHost(new Uri("http://127.0.0.1:8045/v1/models"))
            && !Tools.IsPublicHost(new Uri("http://192.168.1.1/")) && !Tools.IsPublicHost(new Uri("http://localhost:8090/")) && !Tools.IsPublicHost(new Uri("http://router/")));
        Check("«как пройти этого босса», «найди прохождение» — реплика с инструментами (свой мозг сможет загуглить)",
            Brain.WantsTools("а как пройти этого босса в зуме?") && Brain.WantsTools("найди прохождение третьего уровня"));

        // мастерская 3D-вещей (01.10: «3D модельки она себе сама делать не может»)
        Console.WriteLine("== мастерская");
        var (okParts, _) = CraftStore.Parts(System.Text.Json.Nodes.JsonNode.Parse("[{\"shape\":\"Torus\",\"r\":0.1,\"color\":\"неоновый\"},{\"shape\":\"sphere\",\"r\":0.02,\"mirror\":\"x\"}]"));
        Check("части вещи: формы проверены, цвет словом → #rrggbb", okParts is { Count: 2 } && okParts[0]!["shape"]!.GetValue<string>() == "torus" && okParts[0]!["color"]!.GetValue<string>() == "#38e8ff");
        Check("части строкой JSON тоже принимаются", CraftStore.Parts(System.Text.Json.Nodes.JsonValue.Create("[{\"shape\":\"box\",\"size\":[0.1,0.1,0.1]}]")).Parts is { Count: 1 });
        Check("неизвестная форма — отказ со списком форм", CraftStore.Parts(System.Text.Json.Nodes.JsonNode.Parse("[{\"shape\":\"пирамида\"}]")) is { Parts: null, Error: { } shapeErr } && shapeErr.Contains("lathe"));
        Check("тройка чисел: массив и строка", CraftStore.Triple(System.Text.Json.Nodes.JsonNode.Parse("[0, 0.1, -0.05]"))?.ToJsonString() == "[0,0.1,-0.05]" && CraftStore.Triple(System.Text.Json.Nodes.JsonValue.Create("0; 0,1; 2"))?.Count == 3);
        Check("имя файла модели из русского имени", CraftStore.FileName("Кошачьи наушники") == "koshachi-naushniki");
        Check("что на ней из самодельного — словами", CraftStore.WornText(System.Text.Json.Nodes.JsonNode.Parse("{\"items\":{\"наушники\":{\"title\":\"Наушники\",\"attach\":\"head\"},\"меч\":{\"attach\":\"handR\",\"worn\":false}}}") as System.Text.Json.Nodes.JsonObject) is { } wornText && wornText.Contains("«Наушники» (центр головы)") && !wornText.Contains("меч"));
        if (CraftStore.FindBlender() is { } blenderExe)
        {
            var glb = Path.Combine(Path.GetTempPath(), $"klava_craft_{Guid.NewGuid():N}.glb");
            var (bOk, bLog) = Task.Run(() => OverlayForm.RunBlenderAsync(blenderExe,
                "bpy.ops.mesh.primitive_torus_add(major_radius=0.1, minor_radius=0.02)\nbpy.ops.mesh.primitive_uv_sphere_add(radius=0.03, location=(0.1, 0, 0))\n", glb)).GetAwaiter().GetResult();
            Check($"Blender: её скрипт без окна → модель .glb ({(File.Exists(glb) ? new FileInfo(glb).Length / 1024 : 0)} КБ)", bOk && File.Exists(glb));
            if (!bOk) Console.WriteLine(bLog);
            var (badOk, badLog) = Task.Run(() => OverlayForm.RunBlenderAsync(blenderExe, "bpy.ops.mesh.нет_такого()\nprint('после ошибки')\n", glb + ".bad.glb")).GetAwaiter().GetResult();
            Check("Blender: ошибка в скрипте — отказ с хвостом вывода, а не «готово»", !badOk && badLog.Length > 0);
            try { File.Delete(glb); } catch (IOException) { }
        }
        else Console.WriteLine("  (Blender не установлен — проверка скрипта пропущена)");

        // модель агента словами (01.10: «переключай на джемини 3.8 флеш хай» — раньше она правила свой конфиг и врала, что переключила)
        Console.WriteLine("== модель агента");
        string[] gw = ["claude-opus-4-6-thinking", "claude-sonnet-4-6", "gemini-3.1-pro-high", "gemini-3.1-pro-low", "gemini-3.8-flash-high", "gemini-3.8-flash-low"];
        Check("«джемини три и восемь флеш хай» → gemini-3.8-flash-high", AgentHub.PickModel("джемини три и восемь флеш хай", gw) == "gemini-3.8-flash-high");
        Check("«Жминя 3,1 про» → gemini-3.1-pro-high, «3.1 про нижняя» → low", AgentHub.PickModel("Жминя 3,1 про", gw) == "gemini-3.1-pro-high" && AgentHub.PickModel("3.1 про нижняя", gw) == "gemini-3.1-pro-low");
        Check("«опус 4.6 финкинг» → claude-opus-4-6-thinking, «сонет» → claude-sonnet-4-6", AgentHub.PickModel("опус 4.6 финкинг", gw) == "claude-opus-4-6-thinking" && AgentHub.PickModel("на сонет", gw) == "claude-sonnet-4-6");
        Check("точный id и «флеш лоу»", AgentHub.PickModel("GEMINI-3.8-flash-low", gw) == "gemini-3.8-flash-low" && AgentHub.PickModel("флэш лоу", gw) == "gemini-3.8-flash-low");
        Check("модели, которой нет у шлюза, не выдумывает", AgentHub.PickModel("гпт пять", gw) is null && AgentHub.PickModel("переключи пожалуйста", gw) is null);
        Check("ошибка шлюза 30.09 («API Error: 503 … All accounts limited») — повод пересесть, обычный итог — нет",
            AgentHub.IsApiError("API Error: 503 【网关调度受限】 调度详情: All accounts limited. Wait 5628s.") && AgentHub.IsApiError("rate limit exceeded")
            && !AgentHub.IsApiError("Готово: часы собраны в KlavaClock.exe, проверено запуском."));
        var chainCfg = KlavaConfig.ForRoot(Path.GetTempPath());
        chainCfg.AgentModel = "claude-sonnet-4-6"; chainCfg.AgentFallbackModel = "gemini-3.8-flash-high"; chainCfg.AgentModels = ["gemini-3.1-pro-high", "gemini-3.8-flash-high", "нет-такой-модели", "claude-opus-4-6-thinking"];
        Check("цепочка запасных: основная → запасная → остальные, без повторов и без тех, которых у шлюза нет",
            AgentHub.Chain(chainCfg, gw).SequenceEqual(["claude-sonnet-4-6", "gemini-3.8-flash-high", "gemini-3.1-pro-high", "claude-opus-4-6-thinking"]));

        // память по времени (01.10): часы и отрезки, имена в одном виде, журнал без повторов, запись без обрубков
        Console.WriteLine("== память по времени");
        var at = new DateTime(2026, 10, 1, 14, 35, 0);   // четверг
        bool Win(string text, DateTime from, DateTime to) => TimeWords.Parse(text, at) is { } w && w.From == from && w.To == to;
        Check("«вчера вечером» → вчера 17:00–24:00", Win("а что было вчера вечером?", new(2026, 9, 30, 17, 0, 0), new(2026, 10, 1, 0, 0, 0)));
        Check("«час назад» → узкий отрезок вокруг 13:35",
            TimeWords.Parse("о чём мы говорили час назад", at) is { Narrow: true } ago && ago.From < at.AddHours(-1) && ago.To > at.AddHours(-1) && ago.To <= at);
        Check("«минут двадцать назад» → вокруг 14:15", TimeWords.Parse("ты минут двадцать назад говорила", at) is { Narrow: true } m20 && m20.From <= at.AddMinutes(-20) && m20.To >= at.AddMinutes(-20));
        Check("«вчера в три часа дня» → 14:30–16:00 вчера", Win("что было вчера в три часа дня", new(2026, 9, 30, 14, 30, 0), new(2026, 9, 30, 16, 0, 0)));
        Check("«в 13:10» → сегодня 12:40–14:10", Win("что ты говорила в 13:10", new(2026, 10, 1, 12, 40, 0), new(2026, 10, 1, 14, 10, 0)));
        Check("«около двух» днём → сегодня 14:00 (уже прошло)", Win("это было около двух", new(2026, 10, 1, 13, 30, 0), new(2026, 10, 1, 15, 0, 0)));
        Check("«с двух до четырёх вчера» → 14:00–16:00", Win("вчера с двух до четырёх что делали", new(2026, 9, 30, 14, 0, 0), new(2026, 9, 30, 16, 0, 0)));
        Check("«в пятницу» → весь день 25.09", TimeWords.Parse("о чём болтали в пятницу", at) is { WholeDays: true } fri && fri.From == new DateTime(2026, 9, 25));
        Check("«28 сентября» и «28.09» → весь день", Win("что было 28 сентября", new(2026, 9, 28), new(2026, 9, 29)) && Win("а 28.09 что было", new(2026, 9, 28), new(2026, 9, 29)));
        Check("«утром» → сегодня 05:00–12:00", Win("что было утром", new(2026, 10, 1, 5, 0, 0), new(2026, 10, 1, 12, 0, 0)));
        Check("«вечером» в 14:35, день не назван → вчерашний вечер", Win("помнишь, вечером говорили", new(2026, 9, 30, 17, 0, 0), new(2026, 10, 1, 0, 0, 0)));
        Check("без слов о времени — отрезка нет", TimeWords.Parse("пойдём в три раза быстрее в среднем", at) is null);
        Check("метки времени: вчера 21:40 · пн 28.09 15:10 · сегодня 13:05",
            Chronicle.Stamp(new(2026, 9, 30, 21, 40, 0), at) == "вчера 21:40" && Chronicle.Stamp(new(2026, 9, 28, 15, 10, 0), at) == "пн 28.09 15:10" && Chronicle.Stamp(new(2026, 10, 1, 13, 5, 0), at) == "сегодня 13:05");
        var composed = Chronicle.Compose([new Recall(new(2026, 9, 30, 21, 40, 0), "", "второе", "b"), new Recall(null, "знаешь", "без даты", "c"),
            new Recall(new(2026, 9, 28, 15, 10, 0), "", "первое", "a"), new Recall(new(2026, 9, 30, 21, 40, 0), "", "второе", "b")], at) ?? "";
        Check("воспоминания к слову — одним списком по времени, без повторов, без даты — в конце",
            composed.IndexOf("первое", StringComparison.Ordinal) is var i1 and > 0 && composed.IndexOf("второе", StringComparison.Ordinal) is var i2 && i2 > i1
            && composed.IndexOf("без даты", StringComparison.Ordinal) > i2 && composed.LastIndexOf("второе", StringComparison.Ordinal) == i2 && composed.Contains("пн 28.09 15:10"));

        People.Load(["Серый_Волк", "Маруся", "Пёс_Барбос_или_Шарик", "Тётя_Сова", "_карантин", "Клава"], null);
        Check("люди: «Волк», «Волка», «Серый Волк» → Серый Волк", People.Canon("Волк") == "Серый Волк" && People.Canon("Волка") == "Серый Волк" && People.Canon("Серый_Волк") == "Серый Волк");
        Check("люди: слова длинного имени и склонения → один человек", People.Canon("Шарика") == "Пёс Барбос или Шарик" && People.Canon("Пёс Барбос") == "Пёс Барбос или Шарик"
                                                                         && People.Canon("Барбос или Шарик") == "Пёс Барбос или Шарик");
        Check("люди: ослышка в одну букву («Баруся») → Маруся", People.Canon("Баруся") == "Маруся");
        Check("люди: «Волк (Гена)» — прозвище запоминается", People.Canon("Волк (Гена)") == "Серый Волк" && People.Canon("Гена") == "Серый Волк");
        Check("люди: фон — пусто, незнакомый — как есть", People.Canon("фон (кто-то)") == "" && People.Canon("Жминя") == "Жминя" && People.Canon("клава") == "Клава");

        var memRoot = Path.Combine(Path.GetTempPath(), $"klava_mem_{Guid.NewGuid():N}");
        try
        {
            // запись без обрубков
            Directory.CreateDirectory(memRoot);
            var safe = Path.Combine(memRoot, "safe.json");
            SafeFile.Write(safe, "{\"v\":1}");
            SafeFile.Write(safe, "{\"v\":2}");
            File.WriteAllText(safe, "{\"v\":2, \"обрыв");
            Check("запись памяти: основной файл обрезан — поднимается прошлая запись, основной снова цел",
                SafeFile.Load(safe, t => System.Text.Json.Nodes.JsonNode.Parse(t) as System.Text.Json.Nodes.JsonObject)?["v"]?.GetValue<int>() == 1
                && File.ReadAllText(safe).Contains("\"v\":1") && Directory.GetFiles(memRoot, "safe.json.broken-*").Length == 1);
            var cut = "{\"Facts\":[{\"Id\":\"f1\",\"Text\":\"первый факт про кота\",\"Vec\":\"AAAA\"},{\"Id\":\"f2\",\"Text\":\"второй факт {со скобкой}\"},{\"Id\":\"f3\",\"Text\":\"третий об";
            Check("граф памяти: из обрезанного файла спасаются целые факты", MemoryGraph.Salvage(cut) is { Facts.Count: 2 } saved && saved.Facts[1].Id == "f2");
            Check("ответ мозга, обрезанный по длине, разбирается до последнего целого эпизода",
                MemoryBank.ParseLoose("```json\n{\"episodes\":[{\"title\":\"раз\",\"summary\":\"первое целиком\",\"people\":[\"А\",\"Б\"]},{\"title\":\"два\",\"summary\":\"обор") is { } loose
                && loose["episodes"] is System.Text.Json.Nodes.JsonArray { Count: 1 });
            Check("отказ провайдера и сырой JSON — не пересказ", MemoryBank.Junk("The prompt could not be submitted.") && MemoryBank.Junk("```json { \"episodes\"") && !MemoryBank.Junk("Волк и Маруся играли в шашки."));

            // старый журнал: куски дня по архивам, фон, повторы → один чистый день
            var data = Path.Combine(memRoot, "data");
            Directory.CreateDirectory(Path.Combine(data, "flight_backup"));
            static string Row(string t, string who, string user, string reply, string? ctx = null) => System.Text.Json.JsonSerializer.Serialize(
                new { Timestamp = DateTime.Parse(t), Speaker = who, UserText = user, KlavaReply = reply, ScreenContext = ctx },
                new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllLines(Path.Combine(data, "flight_backup", "flight_log_2026-09-20_120000_pre_summary.jsonl"),
            [
                Row("2026-09-20T10:00:00", "Серый_Волк", "Маруся, пошли в шашки", ""),
                Row("2026-09-20T10:00:20", "Серый_Волк", "Серый Волк: Маруся, пошли в шашки", "Шашки — это тема, мур."),
            ]);
            File.WriteAllLines(Path.Combine(data, "flight_log_2026-09-20.jsonl"),
            [
                Row("2026-09-20T12:00:00", "СИСТЕМА", "(AI-сводка дня)", "Сводка: утром играли."),
                Row("2026-09-20T15:00:00", "фон (кто-то)", "(неразборчивая речь)", ""),
                Row("2026-09-20T15:01:00", "Маруся", "А я сегодня пирог испекла", ""),
                Row("2026-09-20T15:01:30", "Маруся", "Маруся: А я сегодня пирог испекла", "С чем пирог-то?", "(Всплыло в памяти к разговору: • чушь)\n(Краем глаза: у друга на экране окно «Блокнот».)"),
                Row("2026-09-20T15:05:00", "Друг", "напечатано руками", "Вижу, что напечатано."),
            ]);
            File.WriteAllText(Path.Combine(data, "flight_log_2026-09-20.md"), "# старый");
            var jr = new FlightJournal(data);
            var day20 = new DateTime(2026, 9, 20);
            var rows20 = jr.GetEntries(day20);
            var lines20 = jr.Transcript(day20);
            Check("журнал: день собран из кусков (архив + остаток), сводка и фон убраны", rows20.Count == 5 && rows20[0].Timestamp.Hour == 10 && rows20.All(e => e.Speaker != "СИСТЕМА" && !e.Speaker.StartsWith("фон")));
            Check("журнал: в хронологии слова людей один раз, имена — в одном виде",
                lines20.Count == 6 && lines20[0] is { Who: "Серый Волк", Text: "Маруся, пошли в шашки" } && lines20[1].Klava && lines20[2].Who == "Маруся" && lines20[3].Klava
                && lines20[4] is { Who: "Друг", Text: "напечатано руками" } && lines20.Count(u => u.Text.Contains("пирог испекла")) == 1);
            Check("журнал: из контекста осталось только то, что было на экране", rows20[3].ScreenContext is { } scr && scr.StartsWith("Краем глаза") && !scr.Contains("Всплыло"));
            Check("журнал: оригиналы не стёрты — лежат в journal\\archive_v1, фон — в journal\\noise",
                File.Exists(Path.Combine(jr.Dir, "archive_v1", "flight_log_2026-09-20.jsonl")) && File.Exists(Path.Combine(jr.Dir, "archive_v1", "flight_backup", "flight_log_2026-09-20_120000_pre_summary.jsonl"))
                && File.Exists(Path.Combine(jr.Dir, "noise", "2026-09-20.log")) && Directory.GetFiles(data, "flight_log_*").Length == 0 && File.ReadAllText(jr.MdPath(day20)).Contains("## 15:00"));
            jr.LogMark("запуск");
            jr.LogHeard("Серый_Волк", "ну что, как день прошёл");
            jr.LogHeard("Маруся", "да нормально, устала только");
            jr.LogTurn("Серый Волк, Маруся", "(Говорят несколько человек — 2. Их реплики по порядку, ответь всем разом, по именам):\nСерый Волк: «ну что, как день прошёл»\nМаруся: «да нормально, устала только»", "Отдыхай, Маруся, мур.", null, heard: true);
            jr.LogTurn("Друг", "а это я написал", "Прочла.", "(Краем глаза: окно)", heard: false);
            jr.LogSelf("(Давно тихо. Скажи что-нибудь.)", "Чего притихли, мур?");
            var todayLines = jr.Transcript(DateTime.Today);
            Check("журнал: ответ на общий разговор пишется, слова людей не повторяются, своё — с пометкой повода",
                todayLines.Count == 7 && todayLines[2] is { Klava: true, Text: "Отдыхай, Маруся, мур." } && todayLines[3] is { Who: "Друг", Text: "а это я написал" }
                && todayLines[5].Hint && todayLines[6].Klava && jr.GetTodayEntries()[0].Kind == FlightJournal.Mark);

            // банк: раскладка по хронологии, вопрос про время, перекладка, отказ мозга
            var memDir = Path.Combine(data, "memory");
            var friendsT = new FriendMemory(memDir, null, jr);
            var bankT = new MemoryBank(memDir, null, jr, friendsT);
            var asked = 0;
            bankT.AskOverride = (_, user) =>
            {
                asked++;
                return Task.FromResult<string?>(user.Contains("пирог")
                    ? "```json\n{\"episodes\":[{\"from\":\"15:01\",\"to\":\"15:05\",\"title\":\"Пирог Маруси\",\"summary\":\"Маруся рассказала, что испекла пирог, Клава спросила, с чем он.\",\"people\":[\"Маруси\",\"Клава\",\"фон (кто-то)\"],\"shelf\":\"еда\",\"keys\":[\"пирог\"]}],\"plans\":[],\"jokes\":[],\"facts\":[]}\n```"
                    : "{\"episodes\":[{\"title\":\"Шашки с утра\",\"summary\":\"Серый Волк позвал Марусю играть в шашки.\",\"people\":[\"Волк\"],\"shelf\":\"игры\",\"keys\":[\"шашки\"]}]}");
            };
            // 20.09 дальше двух недель от «сегодня» проверки — день кладём в очередь перекладки, как при обновлении банка
            var storeFile = Path.Combine(memDir, "bank.json");
            File.WriteAllText(storeFile, "{\"Schema\":0,\"Episodes\":[{\"Id\":\"2026-09-20-1000-1\",\"Day\":\"2026-09-20\",\"From\":\"10:00\",\"To\":\"10:00\",\"People\":[\"Волк\",\"фон ()\"],\"Title\":\"разговор\",\"Summary\":\"The prompt could not be submitted.\"}],\"Plans\":[],\"Notes\":[],\"Filed\":{}}");
            bankT = new MemoryBank(memDir, null, jr, friendsT) { AskOverride = bankT.AskOverride };
            bankT.ConsolidateAsync().GetAwaiter().GetResult();
            var eps20 = bankT.AllEpisodes().Where(e => e.Day == "2026-09-20").OrderBy(e => e.From).ToList();
            Check("банк: мусорный эпизод убран, день переложен начисто по чистой хронологии — эпизоды со своим временем и людьми в одном виде",
                eps20.Count == 2 && eps20[0] is { Title: "Шашки с утра", From: "10:00" } && eps20[0].People.SequenceEqual(["Серый Волк"])
                && eps20[1] is { Title: "Пирог Маруси", From: "15:01", To: "15:05" } && eps20[1].People.SequenceEqual(["Маруся"]) && asked == 2);
            var tl = bankT.ToolAsync("timeline", "", "20.09", "", "", "", "после обеда").GetAwaiter().GetResult();
            Check("банк: «20.09 после обеда» — только эпизод этого отрезка, с датой", tl.Contains("Пирог Маруси") && !tl.Contains("Шашки") && tl.Contains("20 сентября"));
            var jrText = bankT.ToolAsync("journal", "", "20.09", "", "", "", "15:00-15:30").GetAwaiter().GetResult();
            Check("банк: дословный журнал за отрезок — реплики с часами", jrText.Contains("15:01 Маруся: А я сегодня пирог испекла") && jrText.Contains("15:01 Клава: С чем пирог-то?") && !jrText.Contains("шашки"));
            var past = bankT.ContextFor("Маруся", "помнишь, что было 20 сентября?");
            Check("банк: вопрос про день — хроника этого дня по порядку", past.Timeline is { } tline && tline.IndexOf("Шашки", StringComparison.Ordinal) is var a1 and > 0 && tline.IndexOf("Пирог", StringComparison.Ordinal) > a1);
            Check("банк: вопрос не про прошлое — хроники нет", bankT.ContextFor("Маруся", "давай чаю попьём").Timeline is null);
            bankT.AskOverride = (_, _) => Task.FromResult<string?>("The prompt could not be submitted.");
            var lines = jr.Transcript(DateTime.Today);
            Check("банк: мозг трижды не разложил кусок — кладётся как есть, словами из журнала", MemoryBank.Mechanical("2026-10-01", lines) is { Episodes.Count: 1 } mech && mech.Episodes[0].Summary.Contains("устала только"));

            // часы и сеансы
            var st = new MemoryState(memDir);
            st.Begin(DateTime.Now.AddMinutes(-545));
            var clock = st.ClockNote(DateTime.Now, DateTime.Now.AddMinutes(-545));
            Check("часы: дата, день недели, время; после запуска — сколько была выключена; после паузы — сколько молчали",
                clock.Contains(DateTime.Now.ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))) && clock.Contains("Тебя запустили") && clock.Contains("была выключена") && clock.Contains("молчал 9 ч"));
            st.MarkTold("x");
            st.End();
            var st2 = new MemoryState(memDir);
            Check("состояние переживает перезапуск: что уже вспоминала и когда выходила", !st2.Fresh("x", TimeSpan.FromHours(1)) && st2.Fresh("y", TimeSpan.FromHours(1)));
        }
        finally { try { Directory.Delete(memRoot, true); } catch (IOException) { } }

        // дедлок UI-потока (дамп 30.09 15:59): синхронное ожидание задачи на потоке с WinForms-контекстом — продолжение
        // встаёт в очередь этого же потока и не выполнится никогда (UI-поток Клавы висел в MemoryBank.Tool → GetResult).
        // Векторизатор-заглушка отвечает с задержкой, чтобы await правда ушёл в ожидание; старый код здесь висит.
        Console.WriteLine("== дедлок UI-потока");
        var graphRoot = Path.Combine(Path.GetTempPath(), $"klava_graph_{Guid.NewGuid():N}");
        try
        {
            using var stub = new EmbedStub();
            var gcfg = KlavaConfig.ForRoot(graphRoot);
            gcfg.EmbedPort = stub.Port;
            Check("граф памяти: вспоминание, которое ждут синхронно на UI-потоке, не вешает его", NoDeadlock(() =>
            {
                var g = new MemoryGraph(gcfg);
                if (!g.ConnectAsync().GetAwaiter().GetResult()) throw new InvalidOperationException("заглушка векторизатора не ответила");
                g.RecallAsync("ночные задачи по коду").GetAwaiter().GetResult();
            }));
        }
        finally { try { Directory.Delete(graphRoot, true); } catch (IOException) { } }
        Check("UIA: фокус окна, который ждут синхронно на UI-потоке, не вешает его",
            NoDeadlock(() => Uia.FocusedAutomationId(TimeSpan.FromSeconds(2))));

        Console.WriteLine(fail == 0 ? "\nвсё зелёное" : $"\nпровалов: {fail}");
        Console.WriteLine("(файл «хлам.txt» лежит в Корзине — можно проверить, что вернётся, и очистить)");
        return fail == 0 ? 0 : 1;
    }

    private static bool Same(List<ushort>? a, ushort[] b) => a is not null && a.Count == b.Length && a.SequenceEqual(b);

    private static bool Throws(Action f) { try { f(); return false; } catch (ArgumentException) { return true; } }

    /// <summary>Выполнить на отдельном STA-потоке с WinForms-контекстом (как UI Клавы); не вернулся за 15 с — дедлок.</summary>
    private static bool NoDeadlock(Action body)
    {
        Exception? err = null;
        var th = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            try { body(); }
            catch (Exception ex) { err = ex; }
        }) { IsBackground = true, Name = "проверка дедлока" };
        th.SetApartmentState(ApartmentState.STA);
        th.Start();
        var done = th.Join(TimeSpan.FromSeconds(15));
        if (!done) Console.WriteLine("    (поток не вернулся за 15 с — дедлок)");
        if (err is not null) Console.WriteLine($"    ({err.GetType().Name}: {err.Message})");
        return done && err is null;
    }

    /// <summary>Векторизатор-заглушка (/health и /v1/embeddings), отвечает через 150 мс — чтобы await клиента правда ждал.</summary>
    private sealed class EmbedStub : IDisposable
    {
        private readonly System.Net.Sockets.TcpListener _listener = new(System.Net.IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        public int Port { get; }

        public EmbedStub()
        {
            _listener.Start();
            Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!_cts.IsCancellationRequested)
                {
                    System.Net.Sockets.TcpClient c;
                    try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch (Exception ex) when (ex is OperationCanceledException or System.Net.Sockets.SocketException or ObjectDisposedException) { return; }
                    _ = Task.Run(async () =>
                    {
                        using (c)
                        {
                            try { await ServeAsync(c.GetStream()); }
                            catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException) { }
                        }
                    });
                }
            });
        }

        private static async Task ServeAsync(System.Net.Sockets.NetworkStream s)
        {
            var buf = new List<byte>();
            var chunk = new byte[8192];
            string Text() => Encoding.ASCII.GetString(buf.ToArray());
            int head;
            while ((head = Text().IndexOf("\r\n\r\n", StringComparison.Ordinal)) < 0)
            {
                var n = await s.ReadAsync(chunk);
                if (n == 0) return;
                buf.AddRange(chunk.AsSpan(0, n).ToArray());
            }
            var headers = Text()[..head];
            var len = System.Text.RegularExpressions.Regex.Match(headers, @"Content-Length:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            var chunked = headers.Contains("chunked", StringComparison.OrdinalIgnoreCase);
            bool BodyDone() => len.Success ? buf.Count >= head + 4 + int.Parse(len.Groups[1].Value)
                : !chunked || Text().EndsWith("0\r\n\r\n", StringComparison.Ordinal);
            while (!BodyDone())
            {
                var n = await s.ReadAsync(chunk);
                if (n == 0) break;
                buf.AddRange(chunk.AsSpan(0, n).ToArray());
            }
            await Task.Delay(150);
            var body = headers.StartsWith("GET", StringComparison.Ordinal) ? "{\"status\":\"ok\"}" : "{\"data\":[{\"index\":0,\"embedding\":[0.6,0.8]}]}";
            var resp = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
            await s.WriteAsync(Encoding.UTF8.GetBytes(resp));
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
