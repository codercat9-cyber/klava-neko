namespace KlavaDesk;

/// <summary>
/// Сама заводит разговор (друг 28.09: «сделать так, чтобы она сама триггерила разговор»). Прежняя болтовня «давно тихо»
/// ждала 9+ минут полной тишины — в голосовом чате с компанией её не бывает (за три часа — 4 события), а затравки были
/// безличные («поделись фактом») — ботом отдаёт. Здесь — в естественной паузе разговора (70–150 с тишины), раз в
/// 5–10 минут, пока люди рядом (кто-то говорил в последние полчаса), она обращается к кому-то ПО ИМЕНИ, и повод
/// берётся из памяти: созревший план человека («ну как, сходил к врачу?»), тема из прошлых разговоров (банк памяти),
/// факт о человеке (FriendMemory) или своя хотелка. Проигнорили два раза — молчит, пока ей не ответят.
/// </summary>
internal sealed partial class OverlayForm
{
    private DateTime _nextInitiative = DateTime.UtcNow + TimeSpan.FromMinutes(4);
    private TimeSpan _lull = TimeSpan.FromSeconds(100);

    /// <summary>
    /// О чём уже заводила разговор сама (эпизод, факт): data\memory\initiative_used.json, три недели. Раньше это жило в
    /// памяти процесса и обнулялось с каждым перезапуском — и она снова и снова спрашивала Кота, сдал ли он кровь
    /// (друг 10.10: «я ей уже тысячу раз сказал, что сдал»).
    /// </summary>
    private Dictionary<string, DateTime>? _usedInitiative;
    private static readonly TimeSpan InitiativeMemory = TimeSpan.FromDays(21);

    private Dictionary<string, DateTime> UsedInitiative() =>
        _usedInitiative ??= SafeFile.Load(_cfg.Abs(@"data\memory\initiative_used.json"),
            t => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTime>>(t)) ?? new(StringComparer.Ordinal);

    private bool AskedBefore(string key) => UsedInitiative().TryGetValue(key, out var at) && DateTime.UtcNow - at < InitiativeMemory;

    private void MarkAsked(string key)
    {
        var used = UsedInitiative();
        used[key] = DateTime.UtcNow;
        foreach (var old in used.Where(kv => DateTime.UtcNow - kv.Value > InitiativeMemory).Select(kv => kv.Key).ToList()) used.Remove(old);
        try { SafeFile.Write(_cfg.Abs(@"data\memory\initiative_used.json"), System.Text.Json.JsonSerializer.Serialize(used)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("инициатива: initiative_used.json", ex); }
    }

    private static readonly string[] OwnSeeds =
    [
        "Признайся, чего тебе сейчас хочется (поиграть, порисовать, поболтать о чём-то) и позови кого-то с собой.",
        "Спроси мнение компании о чём-нибудь, что тебя саму сейчас занимает, — про игры, кино, еду или людей.",
        "Расскажи, о чём ты думала, пока все болтали, — маленькую кошачью мысль, и спроси, что думают они.",
        "Предложи сыграть во что-то вместе: в Зуму, тетрис, Майнкрафт или словесную игру — и сразу начни.",
        "Подколи кого-нибудь по-доброму за что-то из сегодняшнего разговора.",
        "Спроси кого-нибудь, как прошёл его день и что было самое прикольное.",
    ];

    /// <summary>
    /// Кто рядом — для инициативы строже, чем для ответа: не меньше двух реплик за последние полчаса (одна случайно
    /// подписанная фраза — не человек в комнате; 10.10 так «появлялся» Паша, который на работе), и не отмечен ушедшим.
    /// </summary>
    private List<string> PresentPeople() =>
        _flightJournal.GetTodayEntries()
            .Where(e => DateTime.Now - e.Timestamp < TimeSpan.FromMinutes(30) && FlightJournal.KindOf(e) == FlightJournal.Said)
            .Select(e => e.Speaker).Where(s => s.Length > 0 && s != SelfVoiceName && s != "Друг")
            .Where(s => !_presence.IsAway(s) && !_presence.IsAway(s.Replace(' ', '_')))   // ушёл на работу — не зовём
            .GroupBy(s => s).Where(g => g.Count() >= 2).Select(g => g.Key).ToList();

    /// <summary>Повод завести разговор: план → тема из памяти / факт о человеке / своя хотелка. null — некому.</summary>
    private (string Why, string Prompt)? InitiativePrompt()
    {
        var people = PresentPeople();
        if (people.Count == 0) return null;
        var names = string.Join(", ", people.Select(HumanName));
        // своё обещание («передай им…», «напомни…») — первым делом: обещала — выполни, не дожидаясь, пока спросят
        if (_memoryBank.DuePromise() is { } promise)
            return ("сама: своё обещание", $"(Сама заведи разговор. Рядом сейчас: {names}. {MemoryBank.PromiseNote(promise, DateTime.Now)} " +
                                            "Одна-две живые фразы, обратись по имени или ко всем.)");
        foreach (var p in people)
            if (_memoryBank.DuePlan(p) is { } plan)
                return ($"сама: план {HumanName(p)}",
                    $"(Сама заведи разговор: {HumanName(p)} собирался — {plan.What}{(plan.When.Length > 0 ? $" ({plan.When})" : "")}. " +
                    "Спроси у него по имени, как вышло, одной-двумя живыми фразами — по-дружески, с интересом.)");
        var roll = _rng.NextDouble();
        if (roll < 0.35)
        {
            // эпизод — только про тех, кто сейчас рядом: про ушедших («Паша, как там с работой?») не вспоминает вслух
            var canon = people.Select(p => People.Canon(p).ToLowerInvariant()).ToList();
            bool AboutHere(MemoryBank.Episode e) =>
                e.People.Count == 0 || e.People.Any(x => canon.Any(c => c.Length > 0 && x.ToLowerInvariant().Contains(c.Split(' ')[0])));
            var ep = _memoryBank.Recent(3, 12)
                .Where(e => !AskedBefore("ep:" + e.Id) && AboutHere(e) && !(e.Day == DateTime.Today.ToString("yyyy-MM-dd")
                            && DateTime.Now - DateTime.Today.Add(TimeSpan.Parse(e.To)) < TimeSpan.FromMinutes(60)))
                .OrderBy(_ => _rng.Next()).FirstOrDefault();
            if (ep is not null)
            {
                MarkAsked("ep:" + ep.Id);
                var when = ep.Day == DateTime.Today.ToString("yyyy-MM-dd") ? "сегодня раньше" : ep.Day == DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd") ? "вчера" : "на днях";
                return ($"сама: вспомнила «{ep.Title}»",
                    $"(Сама заведи разговор — тебе вспомнилось, что {when} было «{ep.Title}»: {ep.Summary} Сейчас рядом: {names}. " +
                    "Вспомни это вслух и продолжи тему вопросом или подколкой, обратись по имени. Одна-две фразы, не пересказывай целиком.)");
            }
        }
        if (roll < 0.7)
        {
            var who = people[_rng.Next(people.Count)];
            var fact = _friendMemory.FactsOf(who).Where(f => !AskedBefore("fact:" + f)).OrderBy(_ => _rng.Next()).FirstOrDefault();
            if (fact is not null)
            {
                MarkAsked("fact:" + fact);
                return ($"сама: к {HumanName(who)}",
                    $"(Сама заведи разговор с {HumanName(who)}: ты про него помнишь — «{fact}». Спроси или подколи по этому, по имени, " +
                    "одной-двумя фразами, чтобы захотелось ответить. Не говори «я помню, что…» — просто спроси как подруга.)");
            }
        }
        var seed = OwnSeeds[_rng.Next(OwnSeeds.Length)];
        return ("сама: своя хотелка",
            $"(Сама заведи разговор, рядом {names}. {seed} Обратись к кому-то по имени, одна-две живые фразы.)");
    }

    /// <summary>Из LifeTick: пауза в разговоре с людьми рядом — повод влезть самой.</summary>
    private bool InitiativeTick(DateTime now)
    {
        // смотрит игру с другом — болтает по игре (GameWatch), а не «Лис, оторвись от Бакшота, как прошёл день»
        if (now < _nextInitiative || !CanChime(now) || _gameWatch.Active) return false;
        var quiet = now - _lastTalk;
        if (quiet < _lull || quiet > TimeSpan.FromMinutes(30)) return false;
        // два раза проигнорили — ждёт, пока ей кто-нибудь ответит
        if (_ignoredChimes >= 2 && _lastTalk < _lastChime) return false;
        var baseMin = Math.Max(3, _cfg.ChatterMinutes / 2.0);
        _nextInitiative = now + TimeSpan.FromMinutes(baseMin * (0.7 + _rng.NextDouble() * 0.7));
        _lull = TimeSpan.FromSeconds(70 + _rng.Next(80));
        if (InitiativePrompt() is not { } ip) return false;
        Chime(ip.Why, ip.Prompt);
        return true;
    }
}
