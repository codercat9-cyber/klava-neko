using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>Игра, за которой она умеет следить: как узнать окно, как часто смотреть, что просить у глаз, как себя вести.</summary>
/// <param name="Window">Регулярка по заголовку окна или имени процесса.</param>
/// <param name="GapSec">Не чаще раза в столько секунд — описание кадров глазами (облачный запрос).</param>
/// <param name="HeartbeatSec">Хоть раз в столько секунд, даже если кадр почти не менялся.</param>
/// <param name="Vision">Что спросить у глаз (системная подсказка, ответ — JSON).</param>
/// <param name="Companion">Как ей вести себя в этой игре (строка контекста разговора).</param>
/// <param name="GuideWords">На какие слова друга подкладывать блокнот игры (коды, правила) целиком.</param>
internal sealed record GameProfile(string Id, string Title, string Window, double GapSec, double HeartbeatSec, double ChangeThreshold,
    string Vision, string Companion, string GuideWords);

/// <summary>
/// Смотрит игру вместе с другом. Друг 02.10: «играли в бакшот рулетку, Клава за игрой совсем не следила — какой раунд и
/// сколько патронов выезжало на стол, всё проебала; хочу ещё в Detroit и DOOM 3 — пусть коды от шкафчиков подсказывает,
/// на монстров смотрит и меня успокаивает». Обычное наблюдение глядело на экран раз в 90 секунд и писало «на столе
/// дробовик» — патроны показывают на 3 секунды.
///
/// Здесь: пока окно игры впереди, раз в секунду — кадр окна (её не видно), отпечаток 32×18 и распознанный текст
/// (субтитры, надписи — локально). Кадр заметно сменился (или в Бакшоте на столе вспыхнули красные/синие гильзы) —
/// последние кадры листом 2×2 уходят глазам мозга с вопросом под игру и тем, что уже учтено; ответ — JSON событий:
/// в Бакшоте — показанные патроны, выстрелы, лупа, пиво, жизни, предметы, чей ход (счёт патронов ведётся здесь, не
/// мозгом); в Detroit — сцена, герой, выборы, цель; в DOOM 3 — опасность, монстр, коды и номера шкафчиков (код из
/// блокнота data\games\guide\doom3.md). Счёт и последние события — строкой в каждый ход разговора; важное
/// (патроны показали, монстр, код) — сама говорит коротко. Журнал игры — data\games\watch\&lt;игра&gt;\ГГГГ-ММ-ДД.jsonl.
/// </summary>
internal sealed partial class GameWatch
{
    public static readonly GameProfile[] Profiles =
    [
        new("buckshot", "Buckshot Roulette", @"buckshot", 3.5, 15, 4.0,
            "Ты — глаза Клавы в игре Buckshot Roulette (дробовик против Дилера). Тебе дают кадры игры по порядку (подписаны " +
            "временем) и то, что уже учтено. Верни ТОЛЬКО JSON без пояснений:\n" +
            "{\"scene\": \"одна короткая фраза, что на кадрах\",\n" +
            " \"shells\": {\"live\": N, \"blank\": M} или null — ТОЛЬКО если на столе выставлены патроны нового заряда (красные гильзы — боевые, синие — холостые): посчитай каждую,\n" +
            " \"shot\": {\"by\": \"you|dealer\", \"at\": \"self|other\", \"result\": \"live|blank|unknown\", \"double\": true|false} или null — только если на этих кадрах выстрелили (вспышка, кровь, отлетел, или щелчок без выстрела); by — кто держал дробовик, at — в себя или в другого; double — ствол был отпилен ножовкой (двойной урон),\n" +
            " \"ejected\": \"live|blank\" или null — патрон выброшен без выстрела (пиво, передёрнули): какого цвета,\n" +
            " \"peek\": \"live|blank\" или null — в лупу виден текущий патрон,\n" +
            " \"hp\": {\"you\": N, \"dealer\": N} или null — заряды жизни (молнии) на табло дефибрилляторов: считай ВСЕГДА, когда табло видно хоть на одном кадре (бери последний кадр),\n" +
            " \"dead\": \"you|dealer\" или null — у кого кончились заряды на этих кадрах (экран смерти, «поднимайся», Дилер повержен),\n" +
            " \"items\": {\"you\": [\"...\"], \"dealer\": [\"...\"]} или null — предметы на столе ПО-РУССКИ (лупа, пиво, сигареты, ножовка, наручники, адреналин, телефон, инвертор, лекарство): ближняя сторона — игрока, дальняя — Дилера,\n" +
            " \"turn\": \"you|dealer\" или null — чей ход (надписи ВЫ / ДИЛЕР, у кого дробовик),\n" +
            " \"round\": N или null — номер раунда, если написан,\n" +
            " \"text\": \"надписи и субтитры\"}\n" +
            "you — игрок (друг), dealer — Дилер. Не видно — null, не выдумывай. То, что уже учтено, не повторяй.",
            "Вы с другом играете в Buckshot Roulette: жмёт друг, а ты — лид: ведёшь игру, на каждый его ход сама говоришь, что делать " +
            "и почему (лупа, ножовка, пиво, в кого стрелять) — уверенно, одной фразой; «решай сам» не говоришь никогда. Счёт ниже " +
            "ведут твои глаза по кадрам — отвечай по нему, а не наугад. У жизней помечено, откуда знаешь: «табло» — видела сама, " +
            "«по счёту» — посчитала по выстрелам. Где написано «??», «не уверена», «пропустила» или «не знаю» — так другу и скажи и " +
            "спроси, сколько у него зарядов (запиши ответ через game_watch fix); никогда не выдумывай раунд и жизни. Правила: в заряде красные " +
            "гильзы — боевые, синие — холостые, порядок скрыт; выстрел в себя холостым — ход остаётся; боевой снимает заряд жизни. " +
            "Предметы: лупа — увидеть текущий патрон, пиво — выбросить текущий, сигареты — +1 заряд, ножовка — двойной урон, " +
            "наручники — Дилер пропускает ход (в «Double or Nothing» ещё адреналин, телефон, инвертор, просрочка). Куда стрелять — " +
            "бери «совет лида» из счёта ниже (точный расчёт до конца заряда); правило «холостых больше — в себя» врёт: при 1 " +
            "боевом из 3 холостой в себя оставляет друга под боевым. ХОД (друг 05.10, «это догма»): выстрел в Дилера ЛЮБЫМ " +
            "патроном, хоть холостым, — ход и дробовик уходят Дилеру; холостой в себя — ход остаётся у стрелявшего; боевой в " +
            "себя — ход уходит. Слова друга о том, что было на экране (выстрелы, заряд, жизни, предметы, «выиграли»), уже " +
            "вписаны в счёт и главнее твоих глаз — не спорь с ним, а уточняй. Предметы советуй только те, что друг назвал или " +
            "что в счёте свежие: глаза путают табло с лупой. Музыка и звуки игры — фон, не реплики людей.",
            @"правил|предмет|как играть|что делает|лупа|ножовк|наручник|пиво|сигарет|адреналин|инвертор|телефон"),
        new("detroit", "Detroit: Become Human", @"detroit", 9, 30, 6.0,
            "Ты — глаза Клавы в игре Detroit: Become Human (сюжетная игра с выборами). Кадры по порядку и что уже записано. " +
            "Верни ТОЛЬКО JSON:\n" +
            "{\"scene\": \"что происходит: кто в кадре, где, что делают — одна-две фразы\",\n" +
            " \"character\": \"за кого играют (Коннор / Кэра / Маркус / другой) или null\",\n" +
            " \"chapter\": \"название главы, если видно, или null\",\n" +
            " \"choice\": [\"варианты выбора или реплик на экране\"] или null,\n" +
            " \"objective\": \"цель или задача, если написана, или null\",\n" +
            " \"event\": \"важное событие сюжета на этих кадрах (решение, смерть, открытие, итог главы) или null\",\n" +
            " \"text\": \"субтитры и надписи\"}\n" +
            "Не выдумывай и не спойлери будущее — только то, что на кадрах.",
            "Вы с другом проходите Detroit: Become Human — сюжетную игру с выборами. Ниже — что ты видела сама: сцены, субтитры, " +
            "выборы. Следи за сюжетом и героями, помни прошлые выборы. Ты — лид: на выборе сама уверенно говоришь, что выбираем и почему " +
            "(«жмём…, потому что…»), а не «тебе решать»; не спойлери того, чего вы ещё не видели. Звуки и музыка игры — фон, не реплики людей.",
            @"глав|выбор|что было|кто такой|кто така|сюжет"),
        new("doom3", "DOOM 3", @"doom\s*3|doom3", 5, 20, 5.0,
            "Ты — глаза Клавы в игре DOOM 3 (хоррор-шутер на Марсе). Кадры по порядку. Верни ТОЛЬКО JSON:\n" +
            "{\"scene\": \"коротко, что на кадрах\",\n" +
            " \"danger\": \"none|low|high\" — монстры рядом или нападают = high; тёмный коридор, далёкий враг = low,\n" +
            " \"monster\": \"кто (имп, зомби, пинки, лост соул, коммандо, ревенант, манкубус, ...)\" или null,\n" +
            " \"health\": N или null, \"armor\": N или null — с HUD, если видно,\n" +
            " \"code\": \"цифры кода доступа и к чему он, если на экране (КПК: письмо, видео, записка; панель двери)\" или null,\n" +
            " \"locker\": \"номер шкафчика или двери на экране (например 017)\" или null,\n" +
            " \"location\": \"название места или уровня, если видно\" или null,\n" +
            " \"text\": \"надписи с экрана\"}\n" +
            "Не видно — null, не выдумывай.",
            "Друг играет в DOOM 3 — ему страшно, ты рядом и ведёшь как лид: говоришь, что делать (стреляй, отходи, лечись, ищи патроны), " +
            "успокаиваешь, шутишь, предупреждаешь о монстрах, подсказываешь коды шкафчиков (по блокноту и по тому, что видела на экране). Говори коротко — он играет, длинные речи мешают. Звуки и " +
            "музыка игры — фон, не реплики людей.",
            @"код|шкаф|локер|locker|дверь|пароль|комбинац|кабинет|сейф"),
    ];

    /// <summary>
    /// Видео вместе с друзьями (YouTube, Twitch, Rutube, VK Видео — узнаётся по адресу вкладки, OverlayPresence). Друг
    /// 10.10: «чтобы она вместе со мной могла не только играть, но и смотреть видосы, делая обзор; кадр перескакивает и
    /// она уже смотрит предыдущий; не может понять, чё кто когда выпил». Здесь — раз в секунду кадр окна, каждые ~6 с
    /// лист последних кадров глазам (кто в кадре, что произошло по порядку), речь из видео — ушами (VideoEars), всё — в
    /// журнал со временем, который она видит в каждом ходе.
    /// </summary>
    public static readonly GameProfile Video = new("video", "видео", "", 6, 20, 6.0,
        "Ты — глаза Клавы: она с друзьями смотрит видео (летсплей, стрим, ролик). Кадры по порядку (подписаны временем), ниже — " +
        "что уже записано и что говорят в видео. Верни ТОЛЬКО JSON:\n" +
        "{\"scene\": \"что сейчас на экране — одна фраза\",\n" +
        " \"events\": [\"что произошло на ЭТИХ кадрах по порядку: кто что сделал, взял, выпил, съел, нашёл, открыл, кто появился или " +
        "умер — коротко, с именами героев и блогера\"] или [],\n" +
        " \"people\": [\"кто в кадре: блогер (вебка в углу), герои игры или ролика\"] или [],\n" +
        " \"title\": \"название видео или игры, если видно\" или null,\n" +
        " \"text\": \"субтитры и надписи на экране\"}\n" +
        "Только то, что видно на кадрах; уже записанное не повторяй. Блогер и герои — люди ИЗ ВИДЕО, не из комнаты, где смотрят.",
        "Вы с друзьями смотрите видео — ты зритель и обзорщица рядом с ними: реагируешь на моменты, шутишь, замечаешь детали, помнишь, " +
        "что было раньше, можешь пересказать и оценить. Ниже — журнал просмотра со временем: что видели твои глаза и что говорили В " +
        "ВИДЕО (это слова блогера и героев, не друзей). Кто что сделал или выпил — бери из журнала, а не наугад; чего в журнале нет — " +
        "честно скажи, что не заметила. Журнал отстаёт от картинки на несколько секунд: о самом свежем говори «только что». Блогер и " +
        "герои — это экран: не обращайся к ним, не здоровайся и не приписывай их слова друзьям. Друзья смотрят — не тарахти без " +
        "остановки: коротко, к месту.",
        @"кто|что было|что случил|что произошл|пересказ|выпил|съел|нашёл|нашел|сказал|обзор|оцен");

    private static readonly GameProfile Generic = new("game", "игра", "", 8, 40, 6.0,
        "Ты — глаза Клавы: она смотрит, как друг играет. Кадры по порядку. Верни ТОЛЬКО JSON: {\"scene\": \"коротко, что на " +
        "кадрах\", \"event\": \"важное событие или null\", \"text\": \"надписи и субтитры\"}. Не видно — null, не выдумывай.",
        "Ты смотришь игру вместе с другом и ведёшь как лид: сама предлагаешь, что делать дальше, уверенно и с причиной, а не «решай сам». " +
        "Ниже — что ты видела сама; где не уверена — так и скажи. Звуки и музыка игры — фон, не реплики людей.", "");

    private readonly KlavaConfig _cfg;
    private readonly Func<Eyes?> _eyes;
    private readonly Brain _brain;
    private readonly string _root;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private IntPtr _hwnd;
    private GameProfile _game = Generic;
    private string _title = "";
    private readonly List<(DateTime T, string Kind, string Text)> _log = [];
    private readonly List<(DateTime T, Bitmap Frame)> _frames = [];
    private readonly Dictionary<string, DateTime> _seenText = new(StringComparer.Ordinal);
    private Buckshot _shot = new();
    private readonly Dictionary<string, string> _facts = new(StringComparer.Ordinal);   // герой, глава, цель, здоровье…
    private readonly Dictionary<string, DateTime> _factAt = new(StringComparer.Ordinal); // когда факт видела последний раз
    private (string Text, DateTime Until)? _say;
    private DateTime _lastSay = DateTime.MinValue;

    /// <summary>Сказать вслух коротко (событие для мозга). true — взяла, false — занята (повторим, пока не устарело).</summary>
    public Func<string, bool>? Say { get; set; }

    public bool Active => _cts is not null;
    /// <summary>Смотрит видео (а не игру).</summary>
    public bool IsVideo => Active && _game.Id == "video";
    public DateTime Since { get; private set; }
    public string Title => _title;
    /// <summary>Окно, на которое смотрит; IntPtr.Zero — игру ещё не открыли, жду её окно.</summary>
    public IntPtr Hwnd => _hwnd;

    /// <summary>
    /// Перецепиться на настоящее окно игры. Друг 05.10: «game_watch start» дали до запуска игры — слежка взяла окно,
    /// что было впереди (Steam), а когда игра вышла вперёд, автослежка видела «уже смотрю Бакшот» и не переключалась:
    /// три минуты катки глаза не смотрели ни разу. Счёт и журнал остаются.
    /// </summary>
    public void Rebind(IntPtr hwnd, string title)
    {
        lock (_gate)
        {
            if (_cts is null || hwnd == _hwnd) return;
            _hwnd = hwnd;
            foreach (var (_, f) in _frames) f.Dispose();
            _frames.Clear();
        }
        Log.Write($"игра: окно «{title}» — смотрю его");
        Add("окно", $"нашла окно игры «{title}»");
    }

    public GameWatch(KlavaConfig cfg, Func<Eyes?> eyes, Brain brain, string? root = null)
    {
        _cfg = cfg;
        _eyes = eyes;
        _brain = brain;
        _root = root ?? cfg.Abs(@"data\games");
    }

    /// <summary>Знакомая игра по заголовку окна или процессу; null — не она.</summary>
    public static GameProfile? Detect(string title, string process) =>
        Profiles.FirstOrDefault(p => Regex.IsMatch(title, p.Window, RegexOptions.IgnoreCase) || Regex.IsMatch(process, p.Window, RegexOptions.IgnoreCase));

    public static GameProfile? ById(string id)
    {
        var t = id.Trim().ToLowerInvariant();
        if (Regex.IsMatch(t, @"видео|видос|video|ютуб|youtube|твич|twitch|стрим|ролик|летсплей|куплинов|rutube")) return Video;
        return Profiles.FirstOrDefault(p => p.Id == t || t.Contains(p.Id) || Regex.IsMatch(t, p.Window, RegexOptions.IgnoreCase)
                                            || (p.Id == "buckshot" && t.Contains("бакшот")) || (p.Id == "detroit" && t.Contains("детройт"))
                                            || (p.Id == "doom3" && Regex.IsMatch(t, @"дум\s*3|дум3")));
    }

    public string Start(GameProfile? game, IntPtr hwnd, string title)
    {
        lock (_gate)
        {
            if (_cts is not null)
            {
                if (_hwnd == hwnd) return $"Уже смотрю «{_title}» с {Since.ToLocalTime():HH:mm}.";
                StopLocked();
            }
            _game = game ?? Generic;
            // у видео имя — заголовок вкладки («Kuplinov ► Play — Silent Hill…»), а не «видео»
            _title = game is not null && game.Id != "video" ? game.Title : title.Length > 0 ? title : game?.Title ?? "игра";
            _hwnd = hwnd;
            Since = DateTime.UtcNow;
            _log.Clear();
            _seenText.Clear();
            _facts.Clear();
            _factAt.Clear();
            _shot = new Buckshot();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => LoopAsync(ct));
        }
        Log.Write($"игра: смотрю «{_title}» ({_game.Id}){(hwnd == IntPtr.Zero ? " — жду окно игры" : "")}");
        Add("старт", $"начала смотреть «{_title}»");
        return hwnd == IntPtr.Zero
            ? $"Жду окно «{_title}» — как друг откроет игру и выведет её вперёд, начну смотреть сама; до тех пор счёта нет, так и говори."
            : $"Смотрю «{_title}» вместе с другом: раз в секунду кадр, важное — глазами; счёт и события будут у тебя в каждом ходе.";
    }

    public string Stop(string why = "хватит")
    {
        lock (_gate)
        {
            if (_cts is null) return "Я и не смотрела игру.";
            StopLocked();
        }
        Log.Write($"игра: перестала смотреть «{_title}» ({why})");
        return $"Перестала смотреть «{_title}» ({why}).";
    }

    private void StopLocked()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        foreach (var (_, f) in _frames) f.Dispose();
        _frames.Clear();
        _eyes()?.StopWindowCapture();
        _sound?.Dispose();
        _sound = null;
        _videoEars?.Dispose();
        _videoEars = null;
    }

    /// <summary>Распознать кусок речи (WAV 16 кГц) тем же whisper, что слушает людей; ставит OverlayForm.</summary>
    public Func<byte[], CancellationToken, Task<string?>>? Transcribe { get; set; }

    private VideoEars? _videoEars;

    /// <summary>Видео: уши на звук процесса браузера — речь блогера и героев в журнал «говорят в видео».</summary>
    private void EnsureVideoEars()
    {
        if (Transcribe is not { } transcribe) return;
        Native.GetWindowThreadProcessId(_hwnd, out var pid);
        if (pid == 0 || _videoEars?.Pid == (int)pid) return;
        _videoEars?.Dispose();
        _videoEars = VideoEars.Start(_hwnd, transcribe);
        if (_videoEars is not null)
            _videoEars.Heard += (at, text) => Add("в видео говорят", text, at);
    }

    private GameSound? _sound;
    private DateTime _bangAt = DateTime.MinValue;

    /// <summary>Был бах выстрела только что (≤ 5 с) — неопознанный выстрел на кадрах считать боевым.</summary>
    internal bool BangRecent => DateTime.UtcNow - _bangAt < TimeSpan.FromSeconds(5);

    /// <summary>Слух игры на процесс текущего окна (Бакшот): запустить или перецепить, если игру перезапустили.</summary>
    private void EnsureSound()
    {
        Native.GetWindowThreadProcessId(_hwnd, out var pid);
        if (pid == 0 || _sound?.Pid == (int)pid) return;
        _sound?.Dispose();
        _sound = GameSound.Start(_hwnd);
        if (_sound is not null)
            _sound.Bang += (peak, ratio) =>
            {
                Heard();
                Log.Write($"игра: бах (пик {peak:F2}, в {ratio:F1} раза громче фона)");
            };
    }

    /// <summary>Бах выстрела услышан (слух игры; тест — напрямую).</summary>
    internal void Heard() => _bangAt = DateTime.UtcNow;

    public string Status() => Active ? $"Смотрю «{_title}» с {Since.ToLocalTime():HH:mm}. {StateText()}" : "Сейчас игру не смотрю.";

    /// <summary>
    /// Реплика друга (голос, текст), пока смотрим игру: в Бакшоте его слова о выстрелах, заряде, жизнях, предметах правят
    /// счёт до того, как мозг ответит. Возвращает, что поменялось.
    /// </summary>
    public List<string> HearFriend(string text)
    {
        if (!Active || _game.Id != "buckshot" || string.IsNullOrWhiteSpace(text)) return [];
        List<string> changes;
        lock (_gate) changes = _shot.Hear(text, this);
        if (changes.Count > 0) Log.Write($"игра: со слов друга — {string.Join("; ", changes)}");
        return changes;
    }

    /// <summary>Совет лида на ход друга в Бакшоте (null — счёта нет).</summary>
    internal string? BuckshotAdvice() { lock (_gate) return _game.Id == "buckshot" ? _shot.Advice() : null; }

    // ---------------- цикл ----------------

    private async Task LoopAsync(CancellationToken ct)
    {
        byte[]? lastPrint = null;          // кадр, который видели глаза в прошлый раз
        byte[]? prevPrint = null;          // кадр секунду назад — резкая смена (выстрел, тряска камеры)
        var lastCall = DateTime.MinValue;
        var gone = DateTime.MinValue;
        Task? vision = null, urgent = null;
        double redAvg = -1, blueAvg = -1;
        var ocrTick = 0;
        var darkAt = DateTime.MinValue;
        var bangSeen = DateTime.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct); }
            catch (OperationCanceledException) { return; }
            try
            {
                TrySay();
                if (_hwnd == IntPtr.Zero)
                {
                    // игру ещё не открыли: окно подхватит автослежка (Rebind), когда игра выйдет вперёд
                    if (DateTime.UtcNow - Since > TimeSpan.FromMinutes(15)) { Stop("окно игры так и не появилось"); return; }
                    continue;
                }
                if (!Native.IsWindow(_hwnd))
                {
                    if (gone == DateTime.MinValue) gone = DateTime.UtcNow;
                    else if (DateTime.UtcNow - gone > TimeSpan.FromSeconds(45)) { Stop("игра закрылась"); return; }
                    continue;
                }
                gone = DateTime.MinValue;
                if (_game.Id == "buckshot") EnsureSound();
                if (_game.Id == "video") EnsureVideoEars();
                if (Native.GetForegroundWindow() != _hwnd) continue;   // друг отошёл к другому окну — не подглядываю
                if (_eyes() is not { } eyes) continue;
                using var full = await eyes.GrabWindowAsync(_hwnd);
                if (full is null) continue;
                var small = Resize(full, 640);
                var print = Fingerprint(small);
                if (IsBlank(print))
                {
                    // экран погас целиком: в Бакшоте так бывает, когда в друга попали или он умер — запомнить, глянуть, как загорится
                    if (darkAt == DateTime.MinValue) darkAt = DateTime.UtcNow;
                    small.Dispose();
                    continue;
                }
                var now = DateTime.UtcNow;
                var blackout = _game.Id == "buckshot" && darkAt != DateTime.MinValue && now - darkAt < TimeSpan.FromSeconds(20);
                darkAt = DateTime.MinValue;
                var bang = _game.Id == "buckshot" && now - _bangAt < TimeSpan.FromSeconds(3) && _bangAt > bangSeen;
                if (bang) bangSeen = _bangAt;
                lock (_gate)
                {
                    _frames.Add((now, small));
                    while (_frames.Count > 14) { _frames[0].Frame.Dispose(); _frames.RemoveAt(0); }
                }
                // текст на кадре — каждую вторую секунду (субтитры держатся 2–4 с); у видео — нет: это вся страница
                // (рекомендации, комментарии), субтитры читают глаза
                if (_game.Id != "video" && ++ocrTick % 2 == 0)
                {
                    using var mid = Resize(full, 1280);
                    var text = await eyes.ReadTextAsync(mid, 800, ruOnly: true);
                    NewText(text);
                }
                // Бакшот: красные/синие гильзы вспыхнули на столе — смотрим этот кадр сразу, они видны 3 секунды
                var trigger = false;
                if (_game.Id == "buckshot")
                {
                    var (red, blue) = Vivid(small);
                    if (redAvg < 0) { redAvg = red; blueAvg = blue; }
                    // в бурой палитре Бакшота синего нет вовсе (кадры 02.10: 0 синих пикселей на всех), а холостой в каждом
                    // заряде есть хоть один — синие гильзы и есть сигнал; красного полно и без патронов (лампы, кровь, итоги)
                    trigger = blue >= 25 && blue > blueAvg * 3 + 15;
                    redAvg = redAvg * 0.85 + red * 0.15;
                    blueAvg = blueAvg * 0.85 + blue * 0.15;
                    if (trigger) Log.Write($"игра: на столе цветные гильзы (красных {red}, синих {blue}) — смотрю сразу");
                }
                var diff = lastPrint is null ? 255 : Diff(print, lastPrint);
                // резкая смена за секунду (выстрел, вспышка, камера дёрнулась, сменилась сцена) — смотреть, не дожидаясь паузы
                // (в Бакшоте камера сидит на месте — рывок и есть событие; в DOOM и Detroit кадр мечется всегда)
                var jolt = _game.Id == "buckshot" && prevPrint is not null && Diff(print, prevPrint) >= _game.ChangeThreshold * 3;
                prevPrint = print;
                var since = (now - lastCall).TotalSeconds;
                var pace = Math.Max(0.3, _cfg.GameWatchPace);
                // бах выстрела и погасший экран — главные моменты (попадание, смерть): смотреть сразу, с подсказкой глазам
                var hot = bang || blackout;
                var due = trigger || hot || (jolt && since >= 1.5) || (since >= _game.GapSec * pace && diff >= _game.ChangeThreshold)
                          || since >= _game.HeartbeatSec * pace;
                if (!due || !_cfg.WatchVision) continue;
                // глаза ещё думают над прошлым листом: обычный взгляд подождёт, а показ патронов (видно 3 секунды), бах и
                // погасший экран — второй запрос рядом, иначе пропустим заряд или смерть
                var busy = vision is { IsCompleted: false };
                if (busy && !((trigger || hot) && urgent is not { IsCompleted: false })) continue;
                var from = lastCall;
                lastCall = now;
                lastPrint = print;
                var sheet = trigger ? Jpeg(full, 1280) : Sheet(hot && from > DateTime.MinValue.AddMinutes(1) ? from.AddSeconds(-6) : from);
                var hint = bang
                    ? "Только что в игре громко бахнуло — это БОЕВОЙ выстрел. По кадрам до и после: кто стрелял и в кого (shot, result live), у кого убавились заряды (hp), не умер ли кто (dead)."
                    : blackout
                        ? "Только что экран гас целиком — скорее всего в друга попали или кто-то умер. По кадрам до и после: в кого попали (shot), табло жизней (hp), не умер ли кто (dead), не начался ли новый раунд."
                        : null;
                if (hot) Log.Write($"игра: {(bang ? "бах выстрела" : "экран гас")} — смотрю сразу");
                if (busy) urgent = Task.Run(() => LookAsync(sheet, ct, hint), ct);
                else vision = Task.Run(() => LookAsync(sheet, ct, hint), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.ExternalException or HttpRequestException)
            {
                Log.Write($"игра: {ex.Message}");
            }
        }
    }

    /// <summary>Повторить отложенную реплику, пока не устарела (друг говорил — подождали).</summary>
    private void TrySay()
    {
        (string Text, DateTime Until)? s;
        lock (_gate) s = _say;
        if (s is not { } say) return;
        if (DateTime.UtcNow > say.Until) { lock (_gate) _say = null; Log.Write($"игра: не успела сказать — {say.Text}"); return; }
        if (Say?.Invoke(say.Text) == true) lock (_gate) { _say = null; _lastSay = DateTime.UtcNow; }
    }

    /// <summary>Хочу сказать: не чаще gap секунд; срочное (код, патроны) перебивает неотправленное.</summary>
    private void Want(string prompt, double gapSec, double ttlSec)
    {
        lock (_gate)
        {
            if (DateTime.UtcNow - _lastSay < TimeSpan.FromSeconds(gapSec) && gapSec > 0) return;
            _say = (prompt, DateTime.UtcNow.AddSeconds(ttlSec));
        }
    }

    /// <summary>Распознанный текст кадра: надписи и субтитры в журнал (без повторов), в Бакшоте — сигналы смерти, партии, раунда.</summary>
    internal void NewText(string text)
    {
        var now = DateTime.UtcNow;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            // сигналы игры — до отсева повторов: «ПОДНИМАЙСЯ» второй раз за партию тоже значит смерть
            if (_game.Id == "buckshot" && line.Length >= 4) lock (_gate) _shot.Cue(line, this);
            var letters = line.Count(char.IsLetter);
            if (letters < 6 || letters < line.Length * 0.55) continue;   // HUD, цифры, мусор распознавания
            // кириллица вперемешку с латиницей внутри слов — мусор распознавания пиксельного шрифта
            var cyr = line.Count(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё');
            if (cyr > 0 && letters - cyr > 2 && cyr < letters * 0.8) continue;
            var key = new string(line.ToLowerInvariant().Replace('ё', 'е').Where(char.IsLetter).ToArray());
            lock (_gate)
            {
                if (_seenText.TryGetValue(key, out var t) && now - t < TimeSpan.FromMinutes(3)) { _seenText[key] = now; continue; }
                _seenText[key] = now;
                if (_seenText.Count > 300)
                    foreach (var k in _seenText.Where(kv => now - kv.Value > TimeSpan.FromMinutes(3)).Select(kv => kv.Key).ToList()) _seenText.Remove(k);
            }
            Add("текст", line.Length > 160 ? line[..160] + "…" : line);
        }
    }

    [GeneratedRegex(@"(?:раунд|round)\s*(\d)", RegexOptions.IgnoreCase)]
    private static partial Regex RoundRx();

    // надписи Бакшота (кадры 04.10, русская версия; распознавание путает буквы — ловим устойчивые куски)
    [GeneratedRegex(@"ОТКАЗ\s*ОТ\s*ПРЕТЕН|ПОДПИШИ\s*ОТКАЗ|RELEASE\s*OF\s*LIAB", RegexOptions.IgnoreCase)]
    private static partial Regex NewGameRx();
    [GeneratedRegex(@"ПОЗДРАВЛЯ|CONGRATULATIONS", RegexOptions.IgnoreCase)]
    private static partial Regex WonRx();
    [GeneratedRegex(@"ПОДНИМАЙСЯ|НОЧЬ\s*ТОЛЬКО\s*НАЧАЛ", RegexOptions.IgnoreCase)]
    private static partial Regex FriendDownRx();
    [GeneratedRegex(@"^(?:МУЛЬТИПЛЕЕР|СОЗДАТЕЛИ|MULTIPLAYER|CREDITS)$", RegexOptions.IgnoreCase)]
    private static partial Regex MenuRx();

    // ---------------- глаза ----------------

    private async Task LookAsync(byte[] jpeg, CancellationToken ct, string? hint = null)
    {
        string user;
        lock (_gate)
            user = $"{(_game.Id == "video" ? "Видео" : "Игра")}: {_title}. Уже учтено:\n{StateTextLocked()}\nПоследние события:\n{RecentLocked(_game.Id == "video" ? 12 : 6)}\n\n" +
                   $"{(hint is null ? "" : hint + "\n")}Кадры ниже (по порядку, время подписано). Что нового?";
        string? reply;
        try { reply = await _brain.OneShotAsync(_game.Vision, user, jpeg, 380, ct, fast: true); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidOperationException)
        {
            Log.Write($"игра: глаза не ответили — {ex.Message}");
            return;
        }
        if (string.IsNullOrWhiteSpace(reply) || ct.IsCancellationRequested) return;
        var json = JsonBlock(reply);
        if (json is null) { Log.Write($"игра: глаза ответили не JSON — {Short(reply, 120)}"); return; }
        Apply(json);
    }

    private static JsonObject? JsonBlock(string reply)
    {
        var a = reply.IndexOf('{');
        var b = reply.LastIndexOf('}');
        if (a < 0 || b <= a) return null;
        try { return JsonNode.Parse(reply[a..(b + 1)]) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) && s.Trim() is var t && t != "null" ? t : null;

    private static int? Int(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (int)Math.Round(d);
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out var j) ? j : null;
    }

    private static string[] List(JsonNode? n) =>
        n is JsonArray a ? a.Select(Str).Where(s => s is not null).Select(s => s!).Take(10).ToArray() : [];

    /// <summary>Ответ глаз → события и счёт.</summary>
    internal void Apply(JsonObject j)
    {
        var scene = Str(j["scene"]);
        switch (_game.Id)
        {
            case "buckshot":
            {
                lock (_gate) _shot.Apply(j, this);
                break;
            }
            case "detroit":
            {
                Fact("герой", Str(j["character"]));
                if (Str(j["chapter"]) is { } ch && Fact("глава", ch)) Add("глава", ch);
                if (Str(j["objective"]) is { } ob && Fact("цель", ob)) Add("цель", ob);
                if (Str(j["event"]) is { } ev) Add("событие", ev);
                var choice = List(j["choice"]);
                if (choice.Length >= 2 && Fact("выбор", string.Join(" | ", choice)))
                {
                    Add("выбор", string.Join(" | ", choice));
                    Want($"(В Detroit на экране выбор: {string.Join(" / ", choice)}. Одной короткой фразой скажи, что бы выбрала ты и почему — без спойлеров, друг сам решит.)", 45, 12);
                }
                break;
            }
            case "doom3":
            {
                if (Int(j["health"]) is { } hp) Fact("здоровье", hp.ToString(CultureInfo.InvariantCulture));
                if (Int(j["armor"]) is { } ar) Fact("броня", ar.ToString(CultureInfo.InvariantCulture));
                if (Str(j["location"]) is { } loc && Fact("место", loc)) Add("место", loc);
                var danger = Str(j["danger"]) ?? "none";
                var monster = Str(j["monster"]);
                if (danger == "high" && monster is not null)
                {
                    Add("монстр", monster);
                    Want($"(DOOM 3: на друга лезет {monster}. Он играет, ему страшно — одной короткой фразой подбодри или предупреди, по-свойски.)", 25, 6);
                }
                if (Str(j["code"]) is { } code && Fact("код:" + code, code))
                {
                    Add("код", code);
                    Want($"(DOOM 3: на экране код — {code}. Скажи другу код коротко и чётко, цифры по одной, и к чему он.)", 0, 20);
                }
                if (Str(j["locker"]) is { } locker && LockerCode(locker) is { } known && Fact("шкаф:" + locker, known))
                {
                    Add("шкафчик", $"{locker} → {known}");
                    Want($"(DOOM 3: друг у шкафчика/двери {locker}. По твоему блокноту код {known} — скажи коротко, цифры по одной.)", 0, 20);
                }
                break;
            }
            case "video":
            {
                if (Str(j["title"]) is { } vt && Fact("видео", vt)) Add("видео", vt);
                var people = List(j["people"]);
                if (people.Length > 0) Fact("в кадре", string.Join(", ", people));
                var events = List(j["events"]);
                foreach (var ev in events) Add("событие", ev);
                // реплика — изредка и к месту (друг: «пиздит без остановок»): не чаще раза в 75 с, устаревает за 10 с
                if (events.Length > 0)
                    Want($"(В видео только что: {string.Join("; ", events)}. Если это и правда смешно или важно — одной короткой живой " +
                         "фразой отреагируй как зритель рядом с друзьями; если ничего особенного — промолчи.)", 75, 10);
                break;
            }
            default:
                if (Str(j["event"]) is { } e2) Add("событие", e2);
                break;
        }
        if (scene is not null) lock (_gate) { _facts["сцена"] = Short(scene, 200); _factAt["сцена"] = DateTime.UtcNow; }
    }

    /// <summary>Запомнить факт игры; true — он новый (поменялся).</summary>
    private bool Fact(string key, string? value)
    {
        if (value is null) return false;
        lock (_gate)
        {
            _factAt[key] = DateTime.UtcNow;   // подтвердила — свежий, даже если тот же
            if (_facts.TryGetValue(key, out var old) && old == value) return false;
            _facts[key] = value;
            return true;
        }
    }

    /// <summary>Сколько назад видела: свежее 20 с — без пометки; здоровье, броня, сцена старше 2 минут — «могло измениться».</summary>
    private string AgeLocked(string key)
    {
        if (!_factAt.TryGetValue(key, out var at)) return "";
        var s = (DateTime.UtcNow - at).TotalSeconds;
        if (s < 20) return "";
        var ago = s < 120 ? $"{(int)s} с назад" : $"{(int)(s / 60)} мин назад";
        return key is "здоровье" or "броня" or "сцена" && s >= 120 ? $" (видела {ago} — могло измениться)" : $" (видела {ago})";
    }

    /// <summary>Код шкафчика/двери из блокнота игры: строка, где стоит этот номер.</summary>
    private string? LockerCode(string locker)
    {
        var num = Regex.Match(locker, @"\d{2,3}[A-Za-zА-Яа-я]?").Value;
        if (num.Length == 0) return null;
        var guide = GuideFile(_game.Id);
        if (!File.Exists(guide)) return null;
        foreach (var line in File.ReadLines(guide))
            if (Regex.IsMatch(line, $@"(?<![\d]){Regex.Escape(num)}(?![\d])") && Regex.Match(line, @"код\s*([\d\- ]{3,9})", RegexOptions.IgnoreCase) is { Success: true } m)
                return m.Groups[1].Value.Trim();
        return null;
    }

    private string GuideFile(string id) => Path.Combine(_root, "guide", id + ".md");

    // ---------------- журнал ----------------

    private static readonly JsonSerializerOptions JsonOpts = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal void Add(string kind, string text, DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        lock (_gate)
        {
            _log.Add((now, kind, text));
            if (_log.Count > 400) _log.RemoveRange(0, 100);
        }
        if (kind != "текст") Log.Write($"игра [{_game.Id}] {kind}: {text}");
        try
        {
            var dir = Path.Combine(_root, "watch", _game.Id);
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".jsonl"),
                JsonSerializer.Serialize(new { t = now.ToString("O"), kind, text }, JsonOpts) + "\n");
        }
        catch (IOException) { }
    }

    private string RecentLocked(int n)
    {
        // по времени: речь видео приходит с началом своего куска — раньше, чем её записали
        var take = _log.Where(e => e.Kind != "старт").OrderBy(e => e.T).TakeLast(n).ToList();
        return take.Count == 0 ? "(пока ничего)" : string.Join("\n", take.Select(e => $"{e.T.ToLocalTime():HH:mm:ss} {e.Kind}: {e.Text}"));
    }

    private string StateText() { lock (_gate) return StateTextLocked(); }

    private string StateTextLocked()
    {
        var facts = _facts.Where(kv => !kv.Key.Contains(':') && kv.Key != "выбор").Select(kv => $"{kv.Key}: {kv.Value}{AgeLocked(kv.Key)}").ToList();
        var codes = _facts.Where(kv => kv.Key.StartsWith("код:") || kv.Key.StartsWith("шкаф:")).Select(kv => kv.Key.StartsWith("шкаф:") ? $"шкафчик {kv.Key[5..]} — {kv.Value}" : kv.Value).ToList();
        var sb = new StringBuilder();
        if (_game.Id == "buckshot") sb.Append(_shot.Summary());
        if (facts.Count > 0) sb.Append(sb.Length > 0 ? "\n" : "").Append(string.Join("; ", facts));
        if (codes.Count > 0) sb.Append("\nКоды, что видела: ").Append(string.Join("; ", codes));
        return sb.Length > 0 ? sb.ToString() : "(пока ничего не учтено)";
    }

    /// <summary>
    /// Строка в контекст хода: что за игра, как себя вести, счёт и последние события; на слова про коды/правила — блокнот.
    /// </summary>
    public string? ContextNote(string userText)
    {
        if (!Active) return null;
        string state, recent;
        lock (_gate)
        {
            state = StateTextLocked();
            recent = RecentLocked(_game.Id switch { "detroit" => 24, "video" => 36, _ => 14 });
        }
        var sb = new StringBuilder(_game.Id == "video"
            ? $"(Видео «{_title}» — вы смотрите его вместе, ты видишь и слышишь его с {Since.ToLocalTime():HH:mm}, сейчас {DateTime.Now:HH:mm:ss}. {_game.Companion}\n"
            : $"(Игра «{_title}» — ты смотришь её своими глазами с {Since.ToLocalTime():HH:mm}. {_game.Companion}\n");
        sb.Append("Сейчас: ").Append(state).Append('\n');
        sb.Append("Что было на экране (по времени):\n").Append(recent);
        var guide = GuideFile(_game.Id);
        if (_game.GuideWords.Length > 0 && Regex.IsMatch(userText, _game.GuideWords, RegexOptions.IgnoreCase) && File.Exists(guide))
        {
            var text = File.ReadAllText(guide);
            sb.Append("\nТвой блокнот по игре:\n").Append(text.Length > 3500 ? text[..3500] + "…" : text);
        }
        return sb.Append(')').ToString();
    }

    /// <summary>
    /// Поправка от друга или от неё самой (инструмент game_watch fix/note): «осталось 1 боевой и 2 холостых», «раунд 3»,
    /// «мы выбрали спасти Хэнка».
    /// </summary>
    public string Fix(JsonObject args)
    {
        if (!Active) return "Сейчас игру не смотрю — сначала game_watch start.";
        var changed = new List<string>();
        lock (_gate)
        {
            if (_game.Id == "buckshot") changed.AddRange(_shot.Fix(args));
        }
        if (Str(args["note"]) is { } note) { Add("заметка", note); changed.Add($"записала: {note}"); }
        return changed.Count == 0 ? "Нечего поправлять: для Бакшота — live, blank, round, hp_you, hp_dealer; для любой игры — note." : $"Поправила: {string.Join("; ", changed)}. Сейчас: {StateText()}";
    }

    /// <summary>Журнал игры за сегодня или с даты (для «что было в прошлый раз»).</summary>
    public string History(string? day, int max = 60)
    {
        var id = _game.Id;
        var dir = Path.Combine(_root, "watch", id);
        if (!Directory.Exists(dir)) return "Журнала этой игры ещё нет.";
        var file = day is { Length: > 0 } ? Path.Combine(dir, day + ".jsonl") : Directory.GetFiles(dir, "*.jsonl").OrderBy(f => f).LastOrDefault();
        if (file is null || !File.Exists(file)) return "За этот день журнала нет.";
        var lines = new List<string>();
        foreach (var l in File.ReadLines(file))
        {
            try
            {
                var n = JsonNode.Parse(l);
                if (n?["kind"]?.GetValue<string>() is { } k && n["text"]?.GetValue<string>() is { } t && DateTime.TryParse(n["t"]?.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
                    lines.Add($"{at.ToLocalTime():HH:mm:ss} {k}: {t}");
            }
            catch (JsonException) { }
        }
        return $"Журнал «{Path.GetFileNameWithoutExtension(file)}» ({id}), последние {Math.Min(max, lines.Count)} из {lines.Count}:\n" + string.Join("\n", lines.TakeLast(max));
    }

    // ---------------- Бакшот: расчёт хода ----------------

    /// <summary>
    /// Точный расчёт выстрела до конца заряда: перебор всех исходов (патронов ≤ 8, жизней ≤ 6 — мгновенно), Дилер играет
    /// лучшим для себя образом. Цена — ожидаемый (урон Дилеру − урон себе), смерть ±10. Друг 05.10: «совет был
    /// бесполезным — сказала стреляй в себя, а там боевой»: правило «холостых больше — в себя» врёт — при 1 боевом из 3
    /// холостой в себя оставляет тебя под оставшимся боевым (ты теряешь заряд в 2/3 случаев), а выстрел в Дилера
    /// перекладывает это на него (он теряет в 2/3).
    /// </summary>
    internal static class BuckshotSolver
    {
        private const double Death = 10;
        private static readonly Dictionary<(int L, int B, int My, int D, bool Me), double> Memo = new();

        /// <summary>Цена позиции при лучшей игре обоих: me — ход друга.</summary>
        public static double Value(int l, int b, int my, int d, bool me)
        {
            if (my <= 0) return -Death;
            if (d <= 0) return Death;
            if (l + b == 0) return 0;
            var key = (l, b, my, d, me);
            lock (Memo) if (Memo.TryGetValue(key, out var v)) return v;
            var self = Shot(l, b, my, d, me, atSelf: true);
            var other = Shot(l, b, my, d, me, atSelf: false);
            var r = me ? Math.Max(self, other) : Math.Min(self, other);
            lock (Memo) Memo[key] = r;
            return r;
        }

        /// <summary>
        /// Цена выстрела: atSelf — стреляющий в себя; known — текущий патрон известен (лупа); dmg — урон боевого (ножовка 2).
        /// Боевой всегда передаёт ход; холостой в себя ход оставляет, в другого — передаёт.
        /// </summary>
        public static double Shot(int l, int b, int my, int d, bool me, bool atSelf, string? known = null, int dmg = 1)
        {
            var pl = known == "live" ? 1.0 : known == "blank" ? 0.0 : l + b == 0 ? 0 : (double)l / (l + b);
            double res = 0;
            if (pl > 0 && l > 0)
            {
                var hitMe = me == atSelf;
                res += pl * (hitMe ? -dmg + Value(l - 1, b, my - dmg, d, !me) : dmg + Value(l - 1, b, my, d - dmg, !me));
            }
            if (pl < 1 && b > 0) res += (1 - pl) * Value(l, b - 1, my, d, atSelf ? me : !me);
            return res;
        }
    }

    // ---------------- Бакшот: счёт патронов и жизней ----------------

    /// <summary>
    /// Жизни одного игрока: сколько, откуда знаю (табло — видела; по счёту — вычислила по боевым выстрелам), когда, и не
    /// пропустила ли что-то после этого. Друг 04.10: «шизит — думает, у нас 2 хп, а на самом деле одно»: жизни менялись
    /// только когда глаза читали табло, боевой выстрел их не трогал, а пропущенный выстрел не помечал счёт сомнительным.
    /// </summary>
    private sealed class Life
    {
        public int? N;
        public bool Seen, Unsure;
        public string Src = "";
        public DateTime At = DateTime.MinValue;

        public void See(int n, string src = "табло") { N = n; Seen = true; Unsure = false; Src = src; At = DateTime.UtcNow; }

        public void Hit(int dmg)
        {
            if (N is { } n) { N = Math.Max(0, n - dmg); Seen = false; Src = "по счёту выстрелов"; At = DateTime.UtcNow; }
            else Unsure = true;
        }

        public void Forget() { N = null; Seen = false; Unsure = false; Src = ""; At = DateTime.UtcNow; }

        public string Text()
        {
            if (N is not { } n) return "не знаю";
            var s = (int)(DateTime.UtcNow - At).TotalSeconds;
            var src = $"{Src}, {s} с назад";
            return Unsure ? $"{n}?? (не уверена — мимо глаз вылетел патрон; {src})" : $"{n} ({src})";
        }
    }

    private sealed class Buckshot
    {
        public int Round = 1, Load;
        public int Live, Blank, Unknown;
        public int ShownLive, ShownBlank;
        public DateTime ShownAt = DateTime.MinValue, LastShot = DateTime.MinValue, TurnAt = DateTime.MinValue, ItemsAt = DateTime.MinValue;
        public readonly List<string> Out = [];
        public string? Current;
        public readonly Life You = new(), Dealer = new();
        public string? Turn;
        public string[] ItemsYou = [], ItemsDealer = [];
        /// <summary>Счёт от прошлого раунда или жизни: новый показ патронов пропустила.</summary>
        public bool Stale;
        /// <summary>Что вылетело мимо глаз между зарядами (в этом заряде).</summary>
        public string? Gap;
        /// <summary>Где сейчас партия, если не обычный ход: меню, смерть, новая партия.</summary>
        public string? Phase;
        private DateTime _phaseAt = DateTime.MinValue;

        private static string Ru(string? r) => r == "live" ? "боевой" : r == "blank" ? "холостой" : "неизвестный";

        private int Left0 => Math.Max(0, Live + Blank - Unknown);

        /// <summary>Заряд, жизни и предметы — заново (смерть, новая партия, меню).</summary>
        private void Clear()
        {
            Live = Blank = Unknown = 0;
            Out.Clear();
            Current = null;
            Gap = null;
            Stale = Load > 0;
            You.Forget();
            Dealer.Forget();
            Turn = null;
            ItemsYou = [];
            ItemsDealer = [];
        }

        private bool SamePhase(string phase) => Phase == phase && DateTime.UtcNow - _phaseAt < TimeSpan.FromSeconds(40);

        private void SetPhase(string phase) { Phase = phase; _phaseAt = DateTime.UtcNow; }

        /// <summary>Подписали отказ — новая партия: всё с нуля.</summary>
        public void NewGame(GameWatch w)
        {
            if (SamePhase("новая партия")) return;
            Clear();
            Round = 1; Load = 0; Stale = false;
            SetPhase("новая партия");
            w.Add("партия", "новая партия (подписали отказ) — раунд 1, счёт с нуля");
        }

        /// <summary>Главное меню — партия кончилась (или не начиналась).</summary>
        public void Menu(GameWatch w)
        {
            if (SamePhase("главное меню")) return;
            Clear();
            Load = 0; Stale = false;
            SetPhase("главное меню");
            w.Add("партия", "главное меню — партия не идёт");
        }

        /// <summary>У кого-то кончились заряды: заряд и жизни от этой жизни больше не верны, ждём новый показ.</summary>
        public void Died(string who, GameWatch w, bool inferred = false)
        {
            var phase = who == "друг" ? "друга убили" : "Дилер повержен";
            // одну смерть видят глаза, слышно в словах друга и на табло — считается один раз (иначе раунд +2)
            if (SamePhase(phase) || _lastDeath == phase && DateTime.UtcNow - DeathAt < TimeSpan.FromSeconds(40)) return;
            _lastDeath = phase;
            Clear();
            if (who == "Дилер") Round++;
            SetPhase(phase);
            DeathAt = DateTime.UtcNow;
            var how = inferred ? " (сам момент не видела — по табло начался новый раунд)" : "";
            w.Add("смерть", who == "друг"
                ? $"у друга кончились заряды{how} — эта жизнь/раунд закончились; патроны и жизни — заново, жду новый показ"
                : $"Дилер без зарядов{how} — раунд за другом, дальше раунд {Round}; жду новый показ патронов");
            w.Want(who == "друг"
                ? "(Бакшот: друга подстрелили — заряды кончились. Одной короткой фразой поддержи; старый счёт патронов больше не действует.)"
                : "(Бакшот: Дилер повержен, раунд ваш! Одной короткой фразой порадуйся.)", 0, 8);
        }

        /// <summary>Когда последний раз замечена смерть (своя или Дилера).</summary>
        public DateTime DeathAt = DateTime.MinValue;
        private string? _lastDeath;

        /// <summary>Партия выиграна (экран «Поздравляем» или друг сказал «выиграли»): счёт закрыт, дальше — меню или новая партия.</summary>
        public void Won(GameWatch w, string why)
        {
            if (SamePhase("партия выиграна")) return;
            Clear();
            Load = 0; Stale = false;
            SetPhase("партия выиграна");
            w.Add("партия", $"партия выиграна ({why}) — счёт закрыт");
        }

        // ---------- слова друга: он играет и проговаривает вслух — это главнее глаз ----------

        private const string NumRx = @"(\d|ноль|один|одн[аоу]|одного|две|дв[ау]|двух|три|тр[её]х|четыре|четыр[её]х|пять|пяти|шесть|семь|восемь)";

        private static int? Num(string s) => s switch
        {
            "0" or "ноль" => 0,
            "1" or "один" or "одна" or "одно" or "одну" or "одного" => 1,
            "2" or "два" or "две" or "дву" or "двух" => 2,
            "3" or "три" or "трёх" or "трех" => 3,
            "4" or "четыре" or "четырёх" or "четырех" => 4,
            "5" or "пять" or "пяти" => 5,
            "6" or "шесть" => 6,
            "7" or "семь" => 7,
            "8" or "восемь" => 8,
            _ => int.TryParse(s, out var n) ? n : null,
        };

        private static readonly (string Name, string Rx)[] ItemWords =
        [
            ("лупа", @"луп\w*"), ("пиво", @"пив\w*"), ("сигареты", @"сигар\w*|сиш\w*|сижк\w*"), ("ножовка", @"ножов\w*"),
            ("наручники", @"наруч\w*"), ("адреналин", @"адренал\w*"), ("телефон", @"телефон\w*"), ("инвертор", @"инверт\w*"),
            ("лекарство", @"лекарст\w*|таблет\w*"),
        ];

        /// <summary>
        /// Друг 05.10: «я шмальнул в Дилера холостым, а он в себя боевым» — а она трижды твердила «в стволе 3 и 2, ни один
        /// не вылетел, я сама видела»: глаза выстрелы пропустили, а слова друга в счёт не попадали. Здесь его фразы
        /// разбираются и правят счёт сразу: выстрелы (прошедшее время — «хуярю наугад» не выстрел), заряд, жизни, лупа,
        /// предметы, ход, раунд, смерть, победа. Вопросы пропускаются. Возвращает, что поменялось.
        /// </summary>
        public List<string> Hear(string text, GameWatch w)
        {
            var done = new List<string>();
            var t = text.ToLowerInvariant().Replace('ё', 'е');
            var shots = 0;
            foreach (var raw in Regex.Split(t, @"[.!;\n]+|,\s*а\s+|\s+а\s+потом\s+"))
            {
                var c = raw.Trim(' ', ',', '—', '-');
                if (c.Length < 3 || c.Contains('?') || Regex.IsMatch(c, @"^(сколько|какой|какие|какая|где|что|чё|почему|ты\s+видишь)\b")) continue;
                // «я шмальнул в дилера холостым, а он в себя боевым» — во второй половине глагол опущен: он тот же
                var shot = HearShot(c, w, chained: shots > 0);
                if (shot is not null) { done.Add(shot); shots++; }
                if (HearDeath(c, w) is { } death) done.Add(death);
                if (HearLoad(c, w) is { } load) done.Add(load);
                if (shot is null && HearDamage(c, w) is { } dmg) done.Add(dmg);
                if (HearHp(c) is { } hp) done.Add(hp);
                if (Regex.Match(c, @"^(?:у\s+нас|там|в\s+стволе(?:\s+сейчас)?|текущий|лупа\s+показала)\s+(холост|боев)") is { Success: true } cur)
                {
                    Current = cur.Groups[1].Value == "боев" ? "live" : "blank";
                    done.Add($"лупа: текущий — {Ru(Current)}");
                }
                if (HearItems(c) is { } items) done.Add(items);
                if (Regex.IsMatch(c, @"\b(?:ход\s+(?:наш|мой)|(?:наш|мой)\s+ход)\b")) { Turn = "you"; TurnAt = DateTime.UtcNow; done.Add("ход друга"); }
                else if (Regex.IsMatch(c, @"\b(?:ход\s+(?:дилера|его)|его\s+ход)\b")) { Turn = "dealer"; TurnAt = DateTime.UtcNow; done.Add("ход Дилера"); }
                // «похоронили пидораса, всё, следующий раунд» — смерть Дилера раунд уже прибавила, второй раз не прибавлять
                if (Regex.IsMatch(c, @"\b(новый|следующий)\s+раунд") && DateTime.UtcNow - DeathAt > TimeSpan.FromSeconds(60))
                {
                    NewRound(Round + 1, w);
                    done.Add($"раунд {Round}");
                }
                if (Regex.IsMatch(c, @"\b(выиграли|выиграл|победили|победа)\b") && !c.Contains("не ")) { Won(w, "друг сказал"); done.Add("партия выиграна"); }
            }
            if (done.Count > 0)
            {
                // друг рассказывает ход — партия идёт (меню и старая смерть позади); смерть и победа из этих же слов остаются
                var freshDeath = Phase is "друга убили" or "Дилер повержен" && DateTime.UtcNow - _phaseAt < TimeSpan.FromSeconds(5);
                if (Phase != "партия выиграна" && !freshDeath) Phase = null;
                w.Add("друг", string.Join("; ", done));
            }
            return done;
        }

        /// <summary>
        /// «я шмальнул в дилера холостым», «он шмальнул в себя боевым», «первый боевой отстрелял в дилера», «выстрелил хвостым».
        /// chained — в этой же фразе выстрел уже был: глагол может быть опущен («…, а он в себя боевым»), а повтор того же
        /// цвета — второй выстрел, а не тот же, что засчитали глаза.
        /// </summary>
        private string? HearShot(string c, GameWatch w, bool chained = false)
        {
            var verb = Regex.IsMatch(c, @"\b(шмальнул\w*|стрельнул\w*|выстрелил\w*|пальнул\w*|отстрелял\w*|бахнул\w*|засадил\w*|ебнул\w*)\b");
            // «хвостым» — так whisper слышит «холостым»
            var res = Regex.IsMatch(c, @"\b(холост\w*|хвост\w*|пуст\w*|осечк\w*)") ? "blank" : Regex.IsMatch(c, @"\b(боев\w*|хэдшот\w*)") ? "live" : null;
            if (res is null) return null;
            var dealerSubj = Regex.IsMatch(c, @"(^|\s)(дилер|он)(\s|$)");
            var meSubj = Regex.IsMatch(c, @"(^|\s)(я|мы)(\s|$)");
            var atSelf = Regex.IsMatch(c, @"в\s+себя");
            var atDealer = Regex.IsMatch(c, @"в\s+(дилера|него|гада)");
            var atMe = Regex.IsMatch(c, @"в\s+(меня|нас)");
            // без глагола — только продолжение выстрела в той же фразе и с тем, кто/в кого: «а он в себя боевым»
            if (!verb && !(chained && (dealerSubj || meSubj) && (atSelf || atDealer || atMe))) return null;
            bool? dealerShoots = dealerSubj ? true : meSubj ? false : atMe ? true : atDealer ? false : null;
            bool? self = atSelf ? true : atDealer || atMe ? false : null;
            // глаза могли засчитать этот же выстрел только что — не списывать патрон дважды
            if (!chained && DateTime.UtcNow - LastShot < TimeSpan.FromSeconds(8) && Out.Count > 0 && Out[^1].StartsWith(Ru(res))) return null;
            LastShot = DateTime.UtcNow;
            var spent = Spend(res);
            var by = dealerShoots == true ? "Дилер" : dealerShoots == false ? "друг" : "кто-то";
            var at = self == true ? "в себя" : self == false ? (dealerShoots == true ? "в друга" : "в Дилера") : "";
            Out.Add($"{Ru(spent)} ({by} {at}, со слов друга)".Replace("  ", " "));
            Current = null;
            if (spent == "live" && dealerShoots is not null && self is not null)
            {
                var target = self.Value == dealerShoots.Value ? Dealer : You;
                var line = $"выстрел: {by} {at} — {Ru(spent)}; осталось {Left()}".Replace("  ", " ");
                if (HitToDeath(target, 1, w) is { } death) return $"{line}; {death}";
                return line;
            }
            return $"выстрел: {by} {at} — {Ru(spent)}; осталось {Left()}".Replace("  ", " ");
        }

        /// <summary>
        /// Урон со слов друга. Счёт жизней был уверенный (табло или его слова, без пропущенных патронов) и дошёл до нуля —
        /// это смерть (друг 09.10: «выстрелы пусть пропускает, а смерть Дилера или мою — никогда»). Вернёт, кто умер.
        /// </summary>
        private string? HitToDeath(Life target, int dmg, GameWatch w)
        {
            var sure = target.N is not null && !target.Unsure;
            target.Hit(dmg);
            if (!sure || target.N != 0) return null;
            var who = target == Dealer ? "Дилер" : "друг";
            Died(who, w);
            return who == "Дилер" ? "Дилер повержен" : "друга убили";
        }

        /// <summary>
        /// Смерть словами друга: «похоронили пидораса», «дилер сдох», «убил дилера» — Дилер; «меня убили», «я сдох», «мы
        /// проиграли» — друг. С «не» — не смерть («не убил», «не сдох»).
        /// </summary>
        private string? HearDeath(string c, GameWatch w)
        {
            if (Regex.IsMatch(c, @"\b(не|почти)\s+(\w+\s+)?(убил|убили|сдох|умер|подох|проиграл|похоронил|завалил|грохнул|пристрелил)")) return null;
            if (Regex.IsMatch(c, @"\b(дилер\w*\s+(сдох|умер|подох|откинулся|повержен)\b|(убил\w*|завалил\w*|грохнул\w*|похоронил\w*|положил\w*)\s+(дилера|его|гада|пидор\w*|суку|эту\s+суку)\b|похоронили\b)"))
            {
                Died("Дилер", w);
                return "Дилер повержен (со слов друга)";
            }
            if (Regex.IsMatch(c, @"\b((я|мы)\s+(сдох\w*|умер\w*|подох\w*|труп\w*)\b|(меня|нас)\s+(убил\w*|завалил\w*|грохнул\w*|пристрелил\w*)\b|(убил\w*|завалил\w*|грохнул\w*|пристрелил\w*)\s+(меня|нас)\b|(мы\s+)?проиграли\b)"))
            {
                Died("друг", w);
                return "друга убили (со слов друга)";
            }
            return null;
        }

        /// <summary>«пять боевых, три холостых было», «один боевой, два холостых» — заряд (больше, чем осталось по счёту, — новый показ).</summary>
        private string? HearLoad(string c, GameWatch w)
        {
            int? l = null, b = null;
            if (Regex.Match(c, $@"\b{NumRx}\s+боев\w*\s*(?:,|и)?\s*{NumRx}\s+холост\w*") is { Success: true } m1) { l = Num(m1.Groups[1].Value); b = Num(m1.Groups[2].Value); }
            else if (Regex.Match(c, $@"\b{NumRx}\s+холост\w*\s*(?:,|и)?\s*{NumRx}\s+боев\w*") is { Success: true } m2) { b = Num(m2.Groups[1].Value); l = Num(m2.Groups[2].Value); }
            if (l is not { } live || b is not { } blank || live + blank is < 1 or > 8) return null;
            if (Load == 0 || Stale || live + blank > Left0 || c.Contains("было") || c.Contains("показал"))
            {
                Load++;
                ShownLive = live; ShownBlank = blank; ShownAt = DateTime.UtcNow;
                Out.Clear(); Gap = null;
            }
            Live = live; Blank = blank; Unknown = 0; Stale = false; Current = null;
            return $"заряд со слов друга: {live} боевых, {blank} холостых";
        }

        /// <summary>«минус одна жизнь у дилера», «минус одно хп» — урон (без названного выстрела — значит, вылетел боевой).</summary>
        private string? HearDamage(string c, GameWatch w)
        {
            if (Regex.Match(c, $@"минус\s+{NumRx}?\s*(?:хп|жизн\w*|заряд\w*)(?:\s+у\s+(дилера|него|нас|меня))?") is not { Success: true } m) return null;
            var n = m.Groups[1].Success ? Num(m.Groups[1].Value) ?? 1 : 1;
            var dealer = m.Groups[2].Value is "дилера" or "него";
            if (Live > 0) { Spend("live"); Out.Add($"боевой (в {(dealer ? "Дилера" : "друга")}, со слов друга)"); LastShot = DateTime.UtcNow; }
            var line = $"{(dealer ? "Дилер" : "друг")} −{n}";
            return HitToDeath(dealer ? Dealer : You, n, w) is { } death ? $"{line}; {death}" : line;
        }

        /// <summary>«одно хп осталось», «у него одна жизнь осталась», «у дилера 2 хп».</summary>
        private string? HearHp(string c)
        {
            if (c.Contains("минус")) return null;
            var m = Regex.Match(c, $@"(?:у\s+(нас|меня|дилера|него)\s+)?(?:\w+\s+){{0,2}}?{NumRx}\s+(?:хп|хпш\w*|жизн\w*|заряд\w*)(?:\s+у\s+(нас|меня|дилера|него))?");
            if (!m.Success || Num(m.Groups[2].Value) is not { } n || n > 8) return null;
            var own = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[3].Success ? m.Groups[3].Value : "";
            if (own.Length == 0 && !c.Contains("остал")) return null;   // «два хп» без чьих и без «осталось» — не про текущие жизни
            var dealer = own is "дилера" or "него";
            (dealer ? Dealer : You).See(n, "друг сказал");
            return $"жизни {(dealer ? "Дилера" : "друга")}: {n}";
        }

        /// <summary>«у нас лупа, две сишки и пиво» — предметы друга; «лупы нету» — нет.</summary>
        private string? HearItems(string c)
        {
            var found = ItemWords.Where(i => Regex.IsMatch(c, $@"\b({i.Rx})")).Select(i => i.Name).ToList();
            if (found.Count == 0) return null;
            if (Regex.IsMatch(c, @"\bнет(у)?\b|\bкончил\w*|\bнету\b"))
            {
                ItemsYou = [.. ItemsYou.Where(x => !found.Any(f => x.Contains(f[..3], StringComparison.OrdinalIgnoreCase)))];
                ItemsAt = DateTime.UtcNow;
                return $"нет у друга: {string.Join(", ", found)}";
            }
            if (!Regex.IsMatch(c, @"^у\s+нас\b|у\s+нас\s+есть|\bесть\s+")) return null;
            ItemsYou = [.. found];
            ItemsAt = DateTime.UtcNow;
            return $"предметы друга: {string.Join(", ", found)}";
        }

        /// <summary>Надпись на экране (распознавание или глаза): смерть, новая партия, меню, номер раунда.</summary>
        public void Cue(string line, GameWatch w)
        {
            if (WonRx().IsMatch(line)) { Won(w, "на экране «поздравляем»"); return; }
            if (NewGameRx().IsMatch(line)) NewGame(w);
            else if (FriendDownRx().IsMatch(line)) Died("друг", w);
            else if (MenuRx().IsMatch(line.Trim())) Menu(w);
            else if (RoundRx().Match(line) is { Success: true } rm && int.TryParse(rm.Groups[1].Value, out var rn) && rn is > 0 and < 10) NewRound(rn, w);
        }

        /// <summary>
        /// Новый раунд — новые патроны покажут на стол, жизни станут другими: счёт не стираем (глаза могли ошибиться с
        /// номером), а помечаем старым, пока не увидим показ. Показ уже был в последние 20 с — это и есть патроны нового раунда.
        /// </summary>
        public void NewRound(int n, GameWatch w)
        {
            if (n == Round) return;
            Round = n;
            Stale = Load > 0 && DateTime.UtcNow - ShownAt > TimeSpan.FromSeconds(20);
            if (DateTime.UtcNow - You.At > TimeSpan.FromSeconds(10)) You.Forget();
            if (DateTime.UtcNow - Dealer.At > TimeSpan.FromSeconds(10)) Dealer.Forget();
            w.Add("раунд", $"раунд {n}");
        }

        public void Apply(JsonObject j, GameWatch w)
        {
            var now = DateTime.UtcNow;
            if (Str(j["text"]) is { } tx)
                foreach (var line in tx.Split('\n', '|', ';')) if (line.Trim().Length >= 4) Cue(line.Trim(), w);
            if (Int(j["round"]) is { } r && r is > 0 and < 10 && r != Round) NewRound(r, w);
            if (j["shells"] is JsonObject s && Int(s["live"]) is { } l && Int(s["blank"]) is { } b && l + b is >= 2 and <= 8 && l >= 0 && b >= 0)
            {
                // тот же расклад на соседних кадрах — один и тот же показ
                if (!(l == ShownLive && b == ShownBlank && now - ShownAt < TimeSpan.FromSeconds(25)))
                {
                    // по счёту в стволе ещё были патроны, а уже новый заряд — значит, они вылетели мимо глаз
                    string? gap = null;
                    if (Load > 0 && !Stale && Left0 > 0)
                    {
                        var left = Left0;
                        var kinds = Unknown > 0 ? $"до {Live} боевых и до {Blank} холостых" : $"{Live} боевых и {Blank} холостых";
                        gap = $"перед зарядом {Load + 1} мимо глаз вылетело {left} ({kinds}) — кто в кого, не видела";
                        if (Live > 0) { You.Unsure |= You.N is not null; Dealer.Unsure |= Dealer.N is not null; }
                        w.Add("пропуск", gap);
                    }
                    Load++;
                    Live = l; Blank = b; Unknown = 0; Out.Clear(); Current = null; Stale = false; Gap = gap; Phase = null;
                    ShownLive = l; ShownBlank = b; ShownAt = now;
                    w.Add("патроны", $"заряд {Load}: показали {l} боевых и {b} холостых (всего {l + b})");
                    w.Want($"(Бакшот: на стол выложили патроны — {l} боевых (красных) и {b} холостых (синих), всего {l + b}. Скажи другу одной короткой фразой, сколько каких, чтобы запомнил.)", 0, 15);
                }
            }
            if (j["shot"] is JsonObject sh && now - LastShot > TimeSpan.FromSeconds(3))
            {
                LastShot = now;
                var dealerShoots = Str(sh["by"]) == "dealer";
                var self = Str(sh["at"]) == "self";
                var by = dealerShoots ? "Дилер" : "друг";
                var at = self ? "в себя" : dealerShoots ? "в друга" : "в Дилера";
                // на кадрах не понять, какой патрон, а бах был — значит, боевой (холостой только щёлкает)
                var said = Str(sh["result"]);
                var res = Spend(said is "live" or "blank" ? said : w.BangRecent ? "live" : said);
                Out.Add($"{Ru(res)} ({by} {at})");
                Current = null;
                Phase = null;
                // в кого попало: в себя — стрелявший, иначе — другой
                var target = self == dealerShoots ? Dealer : You;
                var who = target == Dealer ? "Дилер" : "друг";
                var dmg = sh["double"] is JsonValue dv && dv.TryGetValue<bool>(out var dbl) && dbl ? 2 : 1;
                if (res == "live") target.Hit(dmg);
                else if (res == "unknown") target.Unsure |= target.N is not null;
                w.Add("выстрел", $"{by} {at} — {Ru(res)}{(res == "live" && dmg == 2 ? " (ножовка, −2)" : "")}; осталось {Left()}");
                if (res == "live")
                {
                    w.Add("жизни", $"друг {You.Text()}, Дилер {Dealer.Text()}");
                    if (target.N == 0)
                        w.Add("жизни", $"по счёту у {(who == "друг" ? "друга" : "Дилера")} 0 зарядов — если игра продолжается, я где-то обсчиталась");
                    else
                        w.Want($"(Бакшот: {by} {at} — боевой, бах! Осталось {Left()}. Одной короткой фразой отреагируй.)", 25, 6);
                }
            }
            if (Str(j["ejected"]) is { } ej && now - LastShot > TimeSpan.FromSeconds(2))
            {
                LastShot = now;
                var res = Spend(ej);
                Out.Add($"{Ru(res)} (выброшен)");
                Current = null;
                w.Add("выброс", $"выбросили {Ru(res)}; осталось {Left()}");
            }
            if (Str(j["peek"]) is { } pk && pk is "live" or "blank" && Current != pk)
            {
                Current = pk;
                w.Add("лупа", $"текущий патрон — {Ru(pk)}");
            }
            if (Str(j["dead"]) is { } dead && dead is "you" or "dealer") Died(dead == "you" ? "друг" : "Дилер", w);
            else if (j["hp"] is JsonObject hp)
            {
                var y = Int(hp["you"]); var d = Int(hp["dealer"]);
                if (y is < 0 or > 8) y = null;
                if (d is < 0 or > 8) d = null;
                // друг 09.10: «выстрелы пусть пропускает, а смерть Дилера или мою — никогда». Ноль на табло — смерть сразу.
                if (d == 0 && Dealer.N != 0) { Died("Дилер", w); return; }
                if (y == 0 && You.N != 0) { Died("друг", w); return; }
                // оба заряда выросли сразу (или на 2+) — это новый раунд, а не сигареты; смерти не видела — умер тот,
                // у кого зарядов было меньше (по табло новый раунд, значит, кто-то кончился)
                if (y is { } ny && d is { } nd && You.N is { } oy && Dealer.N is { } od && (ny > oy && nd > od || ny >= oy + 2 || nd >= od + 2) && Phase is null)
                {
                    if (DateTime.UtcNow - DeathAt > TimeSpan.FromSeconds(60)) Died(od <= oy ? "Дилер" : "друг", w, inferred: true);
                    else NewRound(Round + 1, w);
                }
                var changed = y is { } a && a != You.N || d is { } c && c != Dealer.N || y is not null && (!You.Seen || You.Unsure) || d is not null && (!Dealer.Seen || Dealer.Unsure);
                if (y is { } yy) You.See(yy);
                if (d is { } dd) Dealer.See(dd);
                if (changed) w.Add("жизни", $"друг {You.Text()}, Дилер {Dealer.Text()}");
            }
            if (Str(j["turn"]) is { } tn && tn is "you" or "dealer")
            {
                // ход перешёл к другу — лид сразу говорит, что делать (друг 04.10: «пусть ведёт игру как лид, а не кидает стрелки на меня»)
                var fresh = tn == "you" && (Turn != "you" || now - TurnAt > TimeSpan.FromSeconds(20));
                Turn = tn; TurnAt = now;
                if (fresh && Advice() is { } adv)
                    w.Want($"(Бакшот: ход друга. Ты лид — скажи одной уверенной фразой, что делать и почему: {adv}. Прямой совет, не «решай сам».)", 6, 8);
            }
            if (j["items"] is JsonObject it)
            {
                var iy = List(it["you"]); var id = List(it["dealer"]);
                if (iy.Length > 0 || id.Length > 0) { ItemsYou = iy; ItemsDealer = id; ItemsAt = now; }
            }
        }

        /// <summary>
        /// Совет лида на ход друга по счёту: точно известный патрон (лупа или остался один цвет) — куда стрелять и ножовка;
        /// иначе — лупа, если есть; дальше по шансам. null — счёта нет (показ пропустила), советовать нечем.
        /// </summary>
        public string? Advice()
        {
            if (Load == 0 || Stale) return null;
            var total = Left0;
            if (total <= 0) return null;
            bool Has(string item) => ItemsYou.Any(i => i.Contains(item, StringComparison.OrdinalIgnoreCase));
            var saw = Has("ножов") ? "отпили ствол ножовкой и " : "";
            if (Current == "live") return $"лупа показала боевой — {saw}стреляй в Дилера";
            if (Current == "blank") return "лупа показала холостой — стреляй в себя, ход останется у тебя";
            if (Unknown == 0 && Blank == 0) return $"остались только боевые ({Live}) — {saw}стреляй в Дилера";
            if (Unknown == 0 && Live == 0) return $"остались только холостые ({Blank}) — стреляй в себя, ход сохранится";
            if (Has("лупа")) return $"сначала лупа: сейчас {Live} боевых и {Blank} холостых, гадать незачем";
            // один патрон вылетел неопознанным — считаем оба случая и берём средний
            var cases = Unknown == 0
                ? new List<(int L, int B, double W)> { (Live, Blank, 1.0) }
                : new List<(int L, int B, double W)> { (Math.Max(0, Live - 1), Blank, (double)Live / (Live + Blank)), (Live, Math.Max(0, Blank - 1), (double)Blank / (Live + Blank)) };
            int my = You.N ?? 2, dl = Dealer.N ?? 2;
            double self = 0, other = 0;
            foreach (var (l, b, wt) in cases)
            {
                if (l + b == 0) continue;
                self += wt * BuckshotSolver.Shot(l, b, my, dl, me: true, atSelf: true);
                other += wt * BuckshotSolver.Shot(l, b, my, dl, me: true, atSelf: false);
            }
            var p = Math.Round(100.0 * Live / Math.Max(1, Live + Blank));
            var unsure = Unknown > 0 ? " (счёт неполный — один вылетел неопознанным)" : "";
            if (Math.Abs(self - other) < 0.02)
                return $"по расчёту без разницы{unsure} — стреляй в Дилера: боевой снимет заряд ему, холостой просто отдаст ход";
            if (other > self)
                return Live < Blank
                    ? $"стреляй в Дилера{unsure}: холостых больше, но холостой в себя оставит тебя под оставшимися боевыми — по расчёту до конца заряда в Дилера выгоднее (шанс боевого {p}%)"
                    : $"стреляй в Дилера{unsure}: боевых {Live} из {Live + Blank} ({p}%)";
            return $"стреляй в себя{unsure}: шанс боевого {p}%, а холостой оставит ход у тебя — по расчёту до конца заряда так выгоднее";
        }

        /// <summary>Списать патрон: цвет известен — его; «неизвестный», а одного цвета не осталось — значит другой.</summary>
        private string Spend(string? res)
        {
            if (res is not ("live" or "blank")) res = Live == 0 && Blank > 0 ? "blank" : Blank == 0 && Live > 0 ? "live" : null;
            if (res == "live" && Live > 0) Live--;
            else if (res == "blank" && Blank > 0) Blank--;
            else if (res is null && Live + Blank - Unknown > 0) Unknown++;
            return res ?? "unknown";
        }

        private string Left()
        {
            var total = Left0;
            return Load == 0 || Stale ? "сколько — не знаю (показ патронов пропустила)" : Unknown > 0 ? $"{total} (боевых до {Live}, холостых до {Blank} — один вылетел неопознанным)" : $"{total}: {Live} боевых, {Blank} холостых";
        }

        public string Summary()
        {
            var sb = new StringBuilder($"Раунд {Round}");
            if (Phase is not null) sb.Append($" — {Phase} ({(int)(DateTime.UtcNow - _phaseAt).TotalSeconds} с назад)");
            if (Load == 0 || Stale)
                sb.Append(Stale
                    ? ". Патроны этого заряда я НЕ видела (раунд или жизнь сменились, показ пропустила) — счёта нет, не выдумывай."
                    : ", патроны этого заряда я не видела (пропустила показ) — счёта нет.");
            else
            {
                sb.Append($", заряд №{Load}: показали {ShownLive} боевых и {ShownBlank} холостых в {ShownAt.ToLocalTime():HH:mm:ss}.");
                if (Gap is not null) sb.Append($" (Перед ним {Gap}.)");
                sb.Append(Out.Count > 0 ? $" Вылетели по порядку: {string.Join(", ", Out)}." : " Ещё ни один не вылетел.");
                var total = Left0;
                sb.Append($" В стволе осталось {Left()}");
                if (Unknown == 0 && total > 0) sb.Append($" — шанс, что следующий боевой, {Math.Round(100.0 * Live / total)}%");
                if (total <= 0) sb.Append(" — заряд кончился, ждём новый показ");
                sb.Append('.');
            }
            if (Current is not null) sb.Append($" Текущий патрон по лупе — {Ru(Current)}.");
            sb.Append($" Жизни: друг {You.Text()}, Дилер {Dealer.Text()}.");
            if (Turn is not null && DateTime.UtcNow - TurnAt < TimeSpan.FromSeconds(25)) sb.Append(Turn == "you" ? " Ход друга." : " Ход Дилера.");
            if (Advice() is { } adv) sb.Append($" Совет лида на ход друга (точный расчёт до конца заряда — говори его, не своё правило): {adv}.");
            var itemsAge = (int)(DateTime.UtcNow - ItemsAt).TotalSeconds;
            var itemsNote = itemsAge > 60 ? $" (видела {itemsAge / 60} мин назад)" : "";
            if (ItemsYou.Length > 0) sb.Append($" Предметы друга: {string.Join(", ", ItemsYou)}{itemsNote}.");
            if (ItemsDealer.Length > 0) sb.Append($" У Дилера: {string.Join(", ", ItemsDealer)}{itemsNote}.");
            return sb.ToString();
        }

        public List<string> Fix(JsonObject a)
        {
            var res = new List<string>();
            if (Int(a["round"]) is { } r and > 0) { Round = r; res.Add($"раунд {r}"); }
            var l = Int(a["live"]); var b = Int(a["blank"]);
            if (l is >= 0 || b is >= 0)
            {
                if (Load == 0 || Stale) { Load = Math.Max(1, Load); ShownLive = l ?? 0; ShownBlank = b ?? 0; ShownAt = DateTime.UtcNow; Out.Clear(); Gap = null; }
                Live = l ?? Live; Blank = b ?? Blank; Unknown = 0; Stale = false;
                res.Add($"в стволе {Live} боевых, {Blank} холостых");
            }
            if (Int(a["hp_you"]) is { } hy) { You.See(hy, "друг сказал"); res.Add($"жизни друга {hy}"); }
            if (Int(a["hp_dealer"]) is { } hd) { Dealer.See(hd, "друг сказал"); res.Add($"жизни Дилера {hd}"); }
            if (res.Count > 0) Phase = null;
            return res;
        }
    }

    // ---------------- картинки ----------------

    private static Bitmap Resize(Bitmap src, int width)
    {
        var k = Math.Min(1.0, width / (double)src.Width);
        var bmp = new Bitmap(Math.Max(1, (int)(src.Width * k)), Math.Max(1, (int)(src.Height * k)), PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
        g.DrawImage(src, 0, 0, bmp.Width, bmp.Height);
        return bmp;
    }

    private static byte[] Jpeg(Bitmap src, int width)
    {
        using var bmp = Resize(src, width);
        return Encode(bmp);
    }

    private static byte[] Encode(Bitmap bmp)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var prm = new EncoderParameters(1);
        prm.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        using var ms = new MemoryStream();
        bmp.Save(ms, codec, prm);
        return ms.ToArray();
    }

    /// <summary>
    /// Лист из кадров с прошлого взгляда (до 6, по порядку, с временем; 2×2 или 3×2): глаза видят всё, что было между
    /// взглядами, а не последние секунды — выстрел посреди долгого ответа глаз больше не пропадает.
    /// </summary>
    private byte[] Sheet(DateTime from)
    {
        List<(DateTime T, Bitmap F)> pick;
        lock (_gate)
        {
            if (_frames.Count == 0) throw new InvalidOperationException("нет кадров");
            // кадры после прошлого взгляда (и один до него — с чего началось); если их нет — последние 4
            var first = _frames.FindIndex(f => f.T > from);
            var lo = first < 0 ? Math.Max(0, _frames.Count - 4) : Math.Max(0, first - 1);
            var span = _frames.Count - lo;
            var want = Math.Min(6, span);
            // поровну по промежутку, последний — обязательно
            var idx = Enumerable.Range(0, want).Select(k => want == 1 ? _frames.Count - 1 : lo + (int)Math.Round(k * (span - 1) / (double)(want - 1)))
                .Distinct().ToList();
            pick = idx.Select(i => (_frames[i].T, (Bitmap)_frames[i].Frame.Clone())).ToList();
        }
        try
        {
            if (pick.Count == 1)
            {
                using var one = new Bitmap(pick[0].F);
                return Encode(one);
            }
            var w = pick[0].F.Width; var h = pick[0].F.Height;
            var cols = pick.Count > 4 ? 3 : 2;
            var rows = (pick.Count + cols - 1) / cols;
            using var sheet = new Bitmap(w * cols, h * rows, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(sheet);
            g.Clear(Color.Black);
            using var font = new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var bg = new SolidBrush(Color.FromArgb(190, 0, 0, 0));
            for (var i = 0; i < pick.Count; i++)
            {
                var x = i % cols * w; var y = i / cols * h;
                g.DrawImage(pick[i].F, x, y, w, h);
                var label = $"{i + 1}. {pick[i].T.ToLocalTime():HH:mm:ss}";
                var size = g.MeasureString(label, font);
                g.FillRectangle(bg, x + 4, y + 4, size.Width + 4, size.Height);
                g.DrawString(label, font, Brushes.Yellow, x + 6, y + 4);
            }
            return Encode(sheet);
        }
        finally
        {
            foreach (var (_, f) in pick) f.Dispose();
        }
    }

    /// <summary>Яркие красные и синие пиксели (гильзы Бакшота) на кадре 640 px: в его тёмной бурой палитре их почти нет.</summary>
    internal static (int Red, int Blue) Vivid(Bitmap bmp)
    {
        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            var stride = data.Stride;
            var buf = new byte[stride * bmp.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
            int red = 0, blue = 0;
            for (var y = 0; y < bmp.Height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < bmp.Width; x++)
                {
                    int b = buf[row + x * 3], g = buf[row + x * 3 + 1], r = buf[row + x * 3 + 2];
                    var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
                    if (max < 90 || max - min < max * 0.55) continue;
                    if (r == max && g < r * 0.45 && b < r * 0.5) red++;
                    else if (b == max && r < b * 0.55 && g < b * 0.8) blue++;
                }
            }
            return (red, blue);
        }
        finally { bmp.UnlockBits(data); }
    }

    private static byte[] Fingerprint(Bitmap src)
    {
        using var small = new Bitmap(32, 18, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(src, 0, 0, 32, 18);
        }
        var res = new byte[32 * 18];
        for (var y = 0; y < 18; y++)
            for (var x = 0; x < 32; x++)
            {
                var c = small.GetPixel(x, y);
                res[y * 32 + x] = (byte)((c.R * 30 + c.G * 59 + c.B * 11) / 100);
            }
        return res;
    }

    private static bool IsBlank(byte[] print) => print.Max() - print.Min() < 6;

    private static double Diff(byte[] a, byte[] b)
    {
        double s = 0;
        for (var i = 0; i < a.Length; i++) s += Math.Abs(a[i] - b[i]);
        return s / a.Length;
    }

    private static string Short(string s, int n) => s.Length > n ? s[..n] + "…" : s;
}
