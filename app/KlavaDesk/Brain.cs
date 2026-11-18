using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>Одна фраза для озвучки: текст без меток и эмоция лица.</summary>
/// <param name="Part">Кусок предложения, отрезанный по запятой (в лимит фраз не считается — там считаются предложения).</param>
/// <param name="Voice">Эмоция голоса (tts/emotions.json: calm, happy, laugh, angry, annoyed, sad, surprised, shy, tender, sly, whisper).</param>
/// <param name="Fx">Действие аватара по метке: burp/fart (рыг, пук — со звуком и зелёным облачком), chips/fish/garlic (ест).</param>
/// <param name="FxAfter">Рыг/пук после слов фразы (метка стояла посреди или в конце), иначе — перед ними.</param>
internal sealed record Phrase(string Text, string Emotion, bool Part = false, string Voice = "calm", string? Fx = null, bool FxAfter = false);

/// <summary>
/// Мозг Клавы: личность, общая история разговора и потоковый запрос к тому мозгу, что сейчас выбран
/// (свой llama-server, облако с OpenAI-совместимым API или Claude Code CLI). История одна на всех —
/// после переключения мозг продолжает тот же разговор.
/// Ответ режется на фразы по мере генерации, чтобы озвучка начиналась с первого предложения.
///
/// Надёжность (чтобы она не «думала и замолкала»):
///  - болтовня идёт на быстрой модели профиля (ChatModel), думающая — только для задач;
///  - у потока есть сторож тишины: нет ни байта StreamIdleMs — обрыв и ответ запасного;
///  - болтовня страхуется гонкой с запасным мозгом (кто первым заговорит); реплика с инструментами —
///    последовательно: если выбранный не начал отвечать за ToolTurnFallbackMs или упал до первого действия,
///    задачу берёт запасной (гонка тут опасна — два мозга выполнили бы одну команду дважды);
///  - мозг промолчал — отвечает запасной, а после инструментов без слов — короткое «готово».
/// </summary>
internal sealed partial class Brain : IDisposable
{
    private readonly KlavaConfig _cfg;
    private readonly string _persona;
    private readonly string _personaTools;
    private readonly List<(string Role, string Text)> _history = new();
    private readonly object _lock = new();
    private readonly ClaudeCode _claude;
    private readonly bool _saveHistory;
    /// <summary>Лапки, глаза, терминал — их ставит OverlayForm.</summary>
    public ToolBox? Toolbox { get; set; }
    private ToolBox? _toolbox => Toolbox;

    /// <summary>
    /// Что друг сказал, пока она работает инструментами: забирается между кругами и сразу идёт мозгу. Раньше поправки
    /// («прямоугольниками забей», «зачем ты бежевой по крыше») ждали конца хода — по четыре минуты — и она их не слышала.
    /// </summary>
    public Func<List<string>>? TakeInterjections { get; set; }
    /// <summary>После инструментов она ничего не сказала — промолчать и дальше (играет её песня), а не «Готово, глянь.» поверх.</summary>
    public Func<bool>? QuietAfterTools { get; set; }
    /// <summary>До этого времени инструменты даются на любую реплику: «давай», «ага, дальше» после задачи — продолжение задачи.</summary>
    private DateTime _toolsUntil = DateTime.MinValue;

    // Первые шесть — те, что просит характер; остальные модели (особенно облачные) придумывают сами.
    /// <summary>
    /// Метка → «лицо/голос»: выражение аватара и эмоция голоса (tts/emotions.json — свой эталон или манера). Раньше
    /// голос был один на все метки, и «[злость]» звучала так же ласково, как «[нежность]».
    /// </summary>
    private static readonly Dictionary<string, string> EmotionTags = new(StringComparer.OrdinalIgnoreCase)
    {
        ["радость"] = "happy/happy", ["смущение"] = "extra/shy", ["удивление"] = "surprised/surprised",
        ["грусть"] = "sad/sad", ["злость"] = "angry/angry", ["спокойствие"] = "relaxed/calm",
        ["смех"] = "happy/laugh", ["нежность"] = "relaxed/tender", ["хитрость"] = "happy/sly", ["раздражение"] = "angry/annoyed",
        ["шёпот"] = "relaxed/whisper", ["шепот"] = "relaxed/whisper",
        ["сочувствие"] = "relaxed/tender", ["забота"] = "relaxed/tender", ["задумчивость"] = "relaxed/calm",
        ["веселье"] = "happy/happy", ["восторг"] = "happy/excited", ["визг"] = "happy/excited", ["игривость"] = "happy/sly", ["сарказм"] = "relaxed/sly",
        ["ехидство"] = "relaxed/sly", ["интерес"] = "surprised/calm", ["любопытство"] = "surprised/calm",
        ["шок"] = "surprised/surprised", ["страх"] = "surprised/scared", ["испуг"] = "surprised/scared", ["обида"] = "sad/sad",
        // живые звуки её тембром (tts/nonverbal: настоящие смех, плач, вздохи, стоны, переведённые Seed-VC) — друг 28.09
        // лицо cry — грустное со слезами по щекам (web/tears.js), ahegao — глаза под лоб, язык, румянец (web/ahegao.js)
        ["плач"] = "cry/cry", ["слёзы"] = "cry/cry", ["слезы"] = "cry/cry", ["вздох"] = "relaxed/sigh", ["усталость"] = "relaxed/sigh",
        ["стон"] = "relaxed/moan", ["томно"] = "relaxed/sultry", ["томность"] = "relaxed/sultry", ["кокетство"] = "happy/sultry",
        ["ахегао"] = "ahegao/ahegao",   // голос: комичная анимешная манера + «Ах, ямэро-о!» (tts/emotions.json)
    };

    /// <summary>Выражение лица из «лицо/голос».</summary>
    private static string FaceOf(string emotion) => emotion.Split('/')[0];

    /// <summary>Эмоция голоса из «лицо/голос» (нет — спокойный голос).</summary>
    private static string VoiceOf(string emotion) => emotion.IndexOf('/') is var i and >= 0 ? emotion[(i + 1)..] : "calm";

    [GeneratedRegex(@"\[([^\[\]]{1,20})\]")]
    private static partial Regex TagRx();
    // метки действий: [рыг] [пук] — звук с зелёным облачком (web/snacks.js), [чипсы] [рыбка] [чеснок] — ест;
    // обстановка (web/props.js, её хотелки 30.09): [кресло] [кровать] [спать] [ванна] [танец] [карамельдансен] [встать]
    [GeneratedRegex(@"\[\s*(рыг\w*|отрыг\w*|отрыжк\w*|пук\w*|пердёж|пердеж|пёрд\w*|перд\w*|чипс\w*|рыб\w*|вобл\w*|чеснок\w*|чесноч\w*|кальмар\w*|темпур\w*|наряд[^\]]*|кресл\w*|массаж\w*|кроват\w*|спать|сплю|сон|ванн\w*|купел\w*|пен[аку]|танц\w*|танец|пляс\w*|карамел\w*|caramel\w*|встать|встаю|подъём|подъем)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex FxTagRx();

    /// <summary>Слово метки → действие аватара.</summary>
    private static string FxOf(string tag)
    {
        var t = tag.ToLowerInvariant();
        if (t.StartsWith("наряд")) return Wardrobe.ByWords(t[5..]) is { } dress ? "outfit:" + dress : "none";
        return t.StartsWith("пук") || t.StartsWith("пер") || t.StartsWith("пёр") ? "fart"
            : t.StartsWith("массаж") ? "chair"
            : t.StartsWith("кресл") ? (t.Contains("массаж") ? "chair" : "armchair")
            : t.StartsWith("кроват") ? "bed"
            : t is "спать" or "сплю" or "сон" ? "sleep"
            : t.StartsWith("ванн") || t.StartsWith("купел") || t.StartsWith("пен") ? "bath"
            : t.StartsWith("карамел") || t.StartsWith("caramel") ? "caramell"
            : t.StartsWith("танц") || t == "танец" || t.StartsWith("пляс") ? "danceBrief"
            : t.StartsWith("вста") || t.StartsWith("подъ") ? "stand"
            : t.StartsWith("чипс") ? "chips"
            : t.StartsWith("кальмар") || t.StartsWith("темпур") ? "squid"
            : t.StartsWith("рыб") || t.StartsWith("вобл") ? "fish"
            : t.StartsWith("чеснок") || t.StartsWith("чесноч") ? "garlic"
            : "burp";
    }

    /// <summary>Действие-обстановка (мебель, сон, ванна, танец, встать): метка может стоять где угодно во фразе.</summary>
    internal static bool IsPlace(string kind) => kind is "chair" or "armchair" or "bed" or "sleep" or "bath" or "danceBrief" or "caramell" or "stand";

    /// <summary>Проверка нарезки (--selftest): весь ответ целиком → фразы с действиями.</summary>
    internal static List<Phrase> CutForTest(string reply)
    {
        var emotion = "relaxed/calm";
        var buf = new StringBuilder(reply);
        var all = new List<Phrase>();
        all.AddRange(CutPhrases(buf, ref emotion, final: false));
        all.AddRange(CutPhrases(buf, ref emotion, final: true));
        return all;
    }

    /// <summary>Действие для готовой фразы: рыг/пук, стоящий до слов, или еда где угодно во фразе.</summary>
    private static string? FxLead(string raw, ref string emotion)
    {
        foreach (Match m in FxTagRx().Matches(raw))
        {
            var kind = FxOf(m.Groups[1].Value);
            if (kind is "chips" or "fish" or "garlic" or "squid" || IsPlace(kind) || kind.StartsWith("outfit:", StringComparison.Ordinal)) return kind;
            if (kind == "none") continue;
            if (Clean(raw[..m.Index], ref emotion, peek: true).Length < 2) return kind;
        }
        return null;
    }
    [GeneratedRegex(@"\*[^*]{1,60}\*")]
    private static partial Regex ActionRx();
    [GeneratedRegex(@"[\p{So}\p{Cs}️‍]")]
    private static partial Regex EmojiRx();
    [GeneratedRegex(@"[#`>_~|]")]
    private static partial Regex MarkupRx();
    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRx();

    // Просьба что-то сделать на компьютере: только тогда модели дают инструменты (файлы, PowerShell, корзина, лапки, кодинг, малявы в чужие чаты, майнкрафт, bash).
    [GeneratedRegex(@"(папк|файл|\bдиск|открой|откро|запусти|запуст|выполни|команд|powershell|пауэршел|терминал|консол|шелл|shell|bash|баш|\bcmd\b|систем|доступ|виндовс|windows|майн|minecraft|mineflayer|копай|скрафти|добудь|алмаз|создай|созда|запиши|сохрани|прочитай|прочти|удали|переимен|скопируй|перемести|установи|поставь|скачай|качни|найди|поищи|погугли|загугли|гугл|прохожден|гайд|как\s+пройти|как\s+победить|как\s+убить|где\s+найти|в\s+инете|в\s+сети|веб|браузер|вкладк|сайт|интернет|ютуб|youtube|ссылк|выкин|выброс|корзин|очист|прибер|разгреб|хлам|мышк|мышь|лапк|кликн|нажми|нажм|тыкни|ткни|напечата|набери|вбей|впиши|прокрут|перетащ|курсор|заверши|закрой|убей|кильни|процесс|код|закод|покоди|программ|скрипт|компил|билд|собер|пофикс|разработ|тест|дебаг|провер|скрин|снимок|сфоткай|нарисуй|рисуй|пейнт|paint|поиграй|сыграй|игр[ауы]|ярлык|напиши|напис|отпиш|отправь|отправить|отправл|скинь|скиньте|черкни|черкан|стукн|отстук|дай\s+знать|передай|попроси|спроси\s+у|уточни\s+у|перекинь|шепни|звякни|дёрни|переключи\s+окно|разверни|сверни|настрой|почини|исправь|сделай|включи|выключи|громкост|звук|наблюда|последи|следи|присмотри|отч[её]т|пока\s+я\s+сп|что\s+было|что\s+делали|переведи|перевод|погод|спой|спеть|спо[её]шь|кавер|послушай|послушать|заценишь|музык|песн|трек|мелоди|студи[юя])", RegexOptions.IgnoreCase)]
    private static partial Regex ToolIntentRx();

    // Просьба рассказать подробно — тогда ответ длиннее обычных двух фраз.
    [GeneratedRegex(@"(расскажи|подробн|объясни|почему|зачем|как\s+(это\s+)?работает|историю|сказку|анекдот|стих|спой|опиши)", RegexOptions.IgnoreCase)]
    private static partial Regex LongAnswerRx();

    /// <summary>Просит ли друг что-то сделать на компьютере (тогда модель получает инструменты).</summary>
    public static bool WantsTools(string userText) => ToolIntentRx().IsMatch(userText);

    /// <summary>В этом ответе мозг уже сходил в инструменты (тогда перебивать и переспрашивать нельзя — действие идёт).</summary>
    public bool TurnUsedTools { get; private set; }

    /// <summary>Ответ оборвался посреди мысли (фильтр провайдера, лимит токенов, обрыв потока): кто и что успел сказать.</summary>
    private (string BrainId, string Said)? _truncated;

    /// <param name="saveHistory">false — проверка мозгов (--probe): историю читаем, но не пишем.</param>
    public Brain(KlavaConfig cfg, bool saveHistory = true)
    {
        _cfg = cfg;
        _saveHistory = saveHistory;
        _persona = File.ReadAllText(cfg.Abs(cfg.PersonaFile), Encoding.UTF8);
        var tools = cfg.Abs(cfg.PersonaToolsFile);
        _personaTools = File.Exists(tools) ? File.ReadAllText(tools, Encoding.UTF8) : "";
        _claude = new ClaudeCode(cfg);
        LoadHistory();
    }

    public void Dispose() => _claude.Dispose();

    /// <summary>Накладка режима общения (см. PersonaModes) — читается из файла при каждом ответе, чтобы менялось на лету.</summary>
    private string PersonaOverlay()
    {
        var file = PersonaModes.Get(_cfg.PersonaMode).File;
        if (file.Length == 0) return "";
        var path = _cfg.Abs(file);
        if (!File.Exists(path)) return "";
        try { return "\n\n" + File.ReadAllText(path, Encoding.UTF8).Trim(); }
        catch (IOException) { return ""; }
    }

    /// <summary>
    /// Характер для мозга. Своим — persona.txt (llama-server держит его в кэше слота); накладку режима
    /// прикладываем только облачным, чтобы не пересчитывать кэш локального мозга на каждой смене режима.
    /// </summary>
    private string SystemFor(BrainProfile p) => p.IsLocal
        ? _persona + PersonaOverlay() + (StablePrefix(p) && _personaTools.Length > 0 ? $"\n\n{_personaTools.Trim()}" : "")
        : $"{_persona.TrimEnd()}{PersonaOverlay()}\n\nСейчас за тебя думает {p.DisplayName}. Спросит друг, на каком ты мозге, — скажи. " +
          "Отвечай так же коротко, как в примерах: одно-два предложения, это живой голосовой разговор.";

    /// <summary>
    /// Свой мозг: начало промпта всегда одно и то же — характер, правила инструментов и схема инструментов (~17 тыс.
    /// токенов). Раньше правила шли в каждую реплику, а схема — только в «задачах»: кэш llama-server не держал их,
    /// и каждый ход, а на каждой смене болтовня↔задача вообще вся история, считались заново (минуты на процессоре).
    /// Теперь в болтовне схема та же, просто tool_choice = none.
    /// </summary>
    private static bool StablePrefix(BrainProfile p) => p.IsLocal && p.Tools && p.Kind != "claude-code";

    /// <summary>Слоты своего llama-server: разговор всегда в 0, разовые вопросы (наблюдение, отчёты) — в 1, кэш разговора не вытесняют.</summary>
    private const int ChatSlot = 0, SideSlot = 1;

    /// <summary>Схема инструментов в запрос: своему мозгу — всегда (кэш), вызывать можно или нельзя — через tool_choice.</summary>
    private void PutTools(JsonObject body, BrainProfile p, bool allow)
    {
        if (StablePrefix(p))
        {
            body["tools"] = Tools.Schema(_toolbox is not null);
            body["tool_choice"] = allow ? "auto" : "none";
            body["id_slot"] = ChatSlot;
        }
        else if (allow)
        {
            body["tools"] = Tools.Schema(_toolbox is not null);
            body["tool_choice"] = "auto";
        }
    }

    /// <summary>
    /// Прогрев своего мозга: он один раз считает характер и историю (это и есть почти весь промпт), и они ложатся
    /// в кэш слота. Облачным прогрев не нужен.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken ct)
    {
        var p = _cfg.ActiveBrain;
        if (!p.IsLocal) return;
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = SystemFor(p) } };
        lock (_lock)
        {
            foreach (var (role, text) in _history)
                messages.Add(new JsonObject { ["role"] = role, ["content"] = text });
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = "(прогрев)" });
        var body = new JsonObject { ["model"] = _cfg.LlmModel, ["stream"] = false, ["max_tokens"] = 1, ["messages"] = messages, ["id_slot"] = ChatSlot };
        PutTools(body, p, allow: false);
        MergeExtra(body, p);
        using var resp = await NetProxy.Client(null, Timeout.InfiniteTimeSpan).PostAsync($"{_cfg.ApiUrl(p)}/chat/completions",
            new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Разовый вопрос мимо разговора (история не трогается): описать снимок экрана, собрать отчёт. Берёт активный мозг,
    /// если он годится (картинка — нужны глаза; Claude Code — нет, он живёт своим процессом), потом его запасной и
    /// остальные с глазами. null — никто не ответил.
    /// </summary>
    /// <param name="accept">Годится ли ответ (нужный вид: JSON, заголовки). Не годится — спрашиваем следующий мозг:
    /// 30.09 провайдер вместо раскладки журнала вернул текстом «The prompt could not be submitted…», и это легло в память.</param>
    /// <summary>
    /// Разовый вопрос (глаза игры, наблюдение, отчёты). fast — нужен быстрый ответ (игра: патроны видны 3 секунды):
    /// думающие модели (thinking, opus, pro) и мозги, что сейчас молчат дольше срока, — в конец очереди, на попытку 15 с
    /// вместо 90. Друг 05.10: глаза Бакшота шли через claude-opus-4-6-thinking — ответ позже, чем кончался ход.
    /// </summary>
    public async Task<string?> OneShotAsync(string system, string user, byte[]? image, int maxTokens, CancellationToken ct, Func<string, bool>? accept = null,
        bool fast = false)
    {
        bool Fits(BrainProfile b) => b.Kind is not ("claude-code" or "codex") && (image is null || b.Vision) && (b.Key is null || ApiKeys.Resolve(b.Key) is not null)
                                     && BrainGuard.CooledFor(b, null) is null;
        var order = new List<BrainProfile>();
        void Add(BrainProfile? b) { if (b is not null && Fits(b) && !order.Contains(b)) order.Add(b); }
        var act = _cfg.ActiveBrain;
        // свой мозг занят разговором, а картинку его зрение считает на процессоре ~25 с (видеопамяти под него нет):
        // наблюдение и отчёты сначала облачным глазам, свой — последним, в отдельном слоте
        if (!act.IsLocal) Add(act);
        Add(_cfg.Brains.FirstOrDefault(b => b.Id == act.Fallback));
        foreach (var b in _cfg.Brains.Where(b => !b.IsLocal)) Add(b);   // свой мозг, если не он активный, скорее всего не запущен
        if (act.IsLocal) Add(act);
        if (fast) order = [.. order.OrderBy(b => SlowGuess(b))];   // OrderBy устойчив: внутри «быстрых» прежний порядок
        foreach (var p in order.Take(3))
        {
            try
            {
                JsonNode content = image is null
                    ? JsonValue.Create(user)!
                    : new JsonArray
                    {
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(image) } },
                        new JsonObject { ["type"] = "text", ["text"] = user },
                    };
                var body = new JsonObject
                {
                    ["model"] = p.IsLocal ? _cfg.LlmModel : (p.ChatModel ?? p.Model),
                    ["stream"] = false,
                    ["max_tokens"] = maxTokens,
                    ["temperature"] = 0.3,
                    ["messages"] = new JsonArray
                    {
                        new JsonObject { ["role"] = "system", ["content"] = system },
                        new JsonObject { ["role"] = "user", ["content"] = content },
                    },
                };
                if (p.IsLocal) body["id_slot"] = SideSlot;
                MergeExtra(body, p);
                var url = _cfg.ApiUrl(p);
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{url}/chat/completions")
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                if (ApiKeys.Resolve(p.Key) is { } key) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(fast ? 15 : 90));
                using var resp = await NetProxy.ClientFor(url, p.Proxy).SendAsync(req, cts.Token);
                var raw = await resp.Content.ReadAsStringAsync(cts.Token);
                if (!resp.IsSuccessStatusCode) { Log.Write($"разовый вопрос {p.Id}: {(int)resp.StatusCode} {ApiError(raw)}"); continue; }
                var text = JsonNode.Parse(raw)?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim();
                if (!string.IsNullOrEmpty(text) && accept?.Invoke(text) == false)
                {
                    Log.Write($"разовый вопрос {p.Id}: ответ не по форме («{(text.Length > 80 ? text[..80] + "…" : text).Replace('\n', ' ')}») — спрашиваю следующий мозг");
                    continue;
                }
                if (!string.IsNullOrEmpty(text)) return text;
                Log.Write($"разовый вопрос {p.Id}: пустой ответ");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                Log.Write($"разовый вопрос {p.Id}: {ex.Message}");
            }
        }
        return null;
    }

    /// <summary>0 — быстрый, 1 — думающая модель или молчит дольше срока прямо сейчас (для fast-вопросов — в конец).</summary>
    private int SlowGuess(BrainProfile b)
    {
        var model = b.IsLocal ? "" : b.ChatModel ?? b.Model;
        if (System.Text.RegularExpressions.Regex.IsMatch(model, @"thinking|opus|-pro\b|reason", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return 1;
        lock (_slow) return _slow.TryGetValue(b.Id, out var n) && n > 0 ? 1 : 0;
    }

    /// <summary>
    /// Идёт игра (друг играет, она смотрит): ждать медленный мозг нельзя — болтовня сразу наперегонки с запасным, задача
    /// ждёт выбранного не дольше 2,5 с. Друг 05.10 в Бакшоте: совет пришёл через 30–60 с, когда ход уже прошёл.
    /// </summary>
    public Func<bool>? Hurry { get; set; }

    public void Forget()
    {
        lock (_lock) _history.Clear();
        SaveHistory();
        _claude.Reset();
    }

    /// <summary>Сменился мозг: процесс Claude Code, если был, больше не нужен.</summary>
    public void OnBrainSwitched() => _claude.Reset();

    private static void MergeExtra(JsonObject body, BrainProfile p)
    {
        if (p.Extra is null) return;
        foreach (var (k, v) in p.Extra) body[k] = v?.DeepClone();
    }

    /// <summary>Недавняя история текстом — новому процессу Claude Code, который её не видел.</summary>
    private string HistorySeed()
    {
        lock (_lock)
        {
            if (_history.Count == 0) return "";
            var sb = new StringBuilder("(Это продолжение вашего разговора. Недавно было:\n");
            foreach (var (role, text) in _history)
                sb.Append(role == "user" ? "Друг: " : "Клава: ").Append(text).Append('\n');
            return sb.Append(")\n\n").ToString();
        }
    }

    /// <summary>Сколько раз за одну реплику Клава может сходить в инструменты (снимок → клик → снимок → … — это много шагов).</summary>
    private const int MaxToolRounds = 16;

    /// <summary>
    /// Эта реплика — с инструментами? Облачным мозгам — всегда (кроме событий): иначе на «ну и чего? давай дальше»
    /// у неё не было рук, она обещала «щас сделаю» и физически не могла. Своему маленькому — только по просьбе
    /// сделать что-то на компе (схема инструментов для него — 3К токенов пересчёта на каждой реплике).
    /// </summary>
    // режим «💬 Общение» — инструментов нет ни у кого (быстро, просто разговор), «🛠 Работа» — есть всегда
    private bool ToolsTurn(BrainProfile p, string userText) =>
        p.Tools && p.Kind is not ("claude-code" or "codex") && !userText.StartsWith('(') && _cfg.WorkMode switch
        {
            "chat" => false,
            "work" => true,
            _ => !p.IsLocal || IsTaskContinuation(userText) || WantsTools(userText) || DateTime.UtcNow < _toolsUntil,
        };

    /// <summary>Реплика — явная просьба что-то сделать (или продолжение недавней задачи): нужна умная модель, а не быстрая.</summary>
    private bool TaskIntent(string userText) => _cfg.WorkMode switch
    {
        "chat" => false,
        "work" => !userText.StartsWith('('),
        _ => IsTaskContinuation(userText) || WantsTools(userText) || DateTime.UtcNow < _toolsUntil,
    };

    /// <summary>Метка реплики-возврата к долгой задаче (<see cref="TaskBoard"/>): это не событие, а работа с инструментами.</summary>
    public const string TaskPrefix = "[задача]";

    public static bool IsTaskContinuation(string userText) => userText.StartsWith(TaskPrefix, StringComparison.Ordinal);

    // ---- честность инструментов ----

    /// <summary>Недавние действия инструментами (факты для контекста следующих реплик, НЕ текст её ответов).</summary>
    private readonly List<(DateTime At, string Line)> _recentActions = new();

    /// <summary>
    /// Результат инструмента, который значит «не сделано»: ошибка, отказ, не нашла, нет окна… Такие помечаются
    /// крупно — раньше модель читала «Ошибка shell: …» и всё равно говорила «готово».
    /// </summary>
    [GeneratedRegex(@"^\s*(❌|Ошибка|Не смогла|Не получилось|Не удалось|Не нашла|Не найден|Нет такого|Пустая|Пустой|Не указан|Не сказано|Друг НЕ разрешил|Окно .{0,60} не|Окна .{0,60} не|Лапок сейчас нет|Мышь и клавиатуру|Сначала посмотри|Глаза ещё|Живого терминала нет|Впереди окно самой Клавы|Надписи .{0,60} не|Windows не дала|Файла нет|Папки нет|Не поняла|Не знаю|Таймаут|Команда не уложилась|Некорректный)", RegexOptions.IgnoreCase)]
    private static partial Regex ToolFailRx();

    [GeneratedRegex(@"код выхода (-?[1-9]\d*)")]
    private static partial Regex ExitCodeFailRx();

    /// <summary>След действий, который раньше дописывался в её реплики в истории.</summary>
    [GeneratedRegex(@"\n?\(сделала инструментами:.*$", RegexOptions.Singleline)]
    private static partial Regex OldTraceRx();

    /// <summary>Вызов инструмента, написанный текстом вместо настоящего вызова: «look_screen({…})», «(сделала инструмент: …)».</summary>
    [GeneratedRegex(@"\(\s*сделала\s+инструмент[^)]*\)+|\b(look_screen|look screen|mouse_click|mouse_move|mouse_drag|draw_stroke|draw_shape|click_element|click_text|type_text|press_keys|open_app|open_terminal|terminal_type|run_powershell|shell|web_search|web_fetch|browser_open|browser_read|create_shortcut|save_screenshot|focus_window|ui_elements|list_windows|send_chat|read_window|game_action|paint_draw|task_start|task_step|task_wait|task_note|task_done|task_cancel)\s*\(\s*\{[^}]*\}\s*\)\)?", RegexOptions.IgnoreCase)]
    private static partial Regex FakeToolRx();

    /// <summary>Обещание действия в её ответе: «щас сделаю», «секунду, открою», «погнали вбивать».</summary>
    [GeneratedRegex(@"\b(щас|сейчас|ща|секунду|сек|погоди|минутку|начну|начинаю|давай я|погнали)\b.{0,50}\b(сдела|откро|откры|глян|посмотр|нарису|рисую|малю|впиш|впеча|вбив|вобью|набер|напеча|запущ|запуска|включ|закро|созда|найд|поищ|скача|кликн|нажм|сфотк|щёлкн|щелкн|почин|добью|доделаю|перепиш)", RegexOptions.IgnoreCase)]
    private static partial Regex PromiseRx();

    /// <summary>
    /// «Делаю прямо сейчас»: «жму», «бегу», «стреляю», «погнала». Без вызова лапок в этом же ответе — враньё: было
    /// «да жму я!», а лапки не шевелились и боты убивали.
    /// </summary>
    [GeneratedRegex(@"\b(жму|жмакаю|нажимаю|нажала|тыкаю|бегу|побежала|стреляю|стрельнула|шмаляю|палю|целюсь|закупаюсь|закупилась|рулю|прыгаю|погнала)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ClaimRx();

    /// <summary>
    /// «Уже делаю / сделала» на просьбу друга: «запускаю», «открыла», «гуглю». Без вызова инструмента в этом же ответе —
    /// неправда (01.10: «Зуму — легко! Запускаю» — и ничего не запустилось).
    /// </summary>
    [GeneratedRegex(@"\b(запускаю|запустила|включаю|включила|врубаю|врубила|открываю|открыла|скачиваю|скачала|устанавливаю|установила|гуглю|загуглила|погуглила|ищу|нашла|отправляю|отправила|сохраняю|сохранила)\b", RegexOptions.IgnoreCase)]
    private static partial Regex DoingRx();

    /// <summary>Служебная разметка вызова инструмента, протёкшая в текст ответа (DeepSeek DSML, Qwen/Hermes tool_call).</summary>
    [GeneratedRegex(@"<\s*[｜|]{1,2}\s*(?:DSML|tool|function|begin)|<tool_call>|<function=", RegexOptions.IgnoreCase)]
    private static partial Regex LeakRx();

    /// <summary>Справка о недавних действиях — в контекст реплики (последние 5 минут), чтобы она помнила, что реально сделано.</summary>
    private string? RecentActionsNote()
    {
        lock (_recentActions)
        {
            _recentActions.RemoveAll(a => DateTime.UtcNow - a.At > TimeSpan.FromMinutes(5));
            if (_recentActions.Count == 0) return null;
            return "(Справка системы, это не твоя реплика и не повторяй её вслух. Что ты реально сделала инструментами за последние минуты:\n" +
                   string.Join("\n", _recentActions.TakeLast(8).Select(a => "- " + a.Line)) + ")";
        }
    }

    /// <summary>
    /// Спросить Клаву и получать фразы по мере генерации. <paramref name="userText"/> в скобках — событие.
    /// Если модель зовёт инструмент, <paramref name="approve"/> спрашивает друга; результат уходит
    /// обратно в модель, и она продолжает ответ.
    /// </summary>
    /// <param name="context">Что Клава видит на экране и кто в комнате — идёт в модель вместе с репликой, но не в историю.</param>
    /// <param name="image">Снимок экрана (JPEG) — мозгу с глазами; в историю не попадает, иначе контекст забьётся.</param>
    public async IAsyncEnumerable<Phrase> AskAsync(string userText, string? context, byte[]? image, Func<ToolRequest, Task<bool>> approve,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var p = _cfg.ActiveBrain;
        LastBrainId = p.Id;
        TurnUsedTools = false;
        _truncated = null;
        var chain = FallbackChain(p, image);
        var toolsTurn = ToolsTurn(p, userText);
        // чистый режим (без мата, рассудительный) — ещё и фильтр: сорвалась модель — вслух всё равно без мата
        var clean = PersonaModes.Get(_cfg.PersonaMode).Clean;
        await foreach (var ph in AskRoutedAsync(p, chain, toolsTurn, userText, context, image, approve, ct))
            yield return clean ? ph with { Text = PersonaModes.Censor(ph.Text) } : ph;
        // оборвалась на полуслове — запасной мозг договаривает мысль (не повторяя сказанного); не вышло — и ладно, уже сказано
        var fb = chain.FirstOrDefault(b => b.Id != _truncated?.BrainId && b.Kind != "claude-code");
        if (!ct.IsCancellationRequested && _truncated is { } cutOff && fb is not null)
        {
            Log.Write($"мозг {cutOff.BrainId}: оборвался на «…{Short(cutOff.Said, 60)}» — договаривает {fb.Id}");
            _truncated = null;
            var tail = $"{userText}\n\n(Ты уже начала отвечать и сказала вслух: «{cutOff.Said}» — но тебя оборвало на полуслове. " +
                       "Договори мысль одной короткой фразой, продолжая с того места, где оборвалась. Сказанное не повторяй.)";
            var rest = new StringBuilder();
            var e = AskOneAsync(fb, tail, context, null, approve, false, null, ct, remember: false).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    bool has;
                    try { has = await e.MoveNextAsync(); }
                    catch (Exception ex) when (!ct.IsCancellationRequested) { Log.Write($"мозг {fb.Id}: договорить не вышло ({Short(ex.Message, 120)})"); break; }
                    if (!has) break;
                    var ph = clean ? e.Current with { Text = PersonaModes.Censor(e.Current.Text) } : e.Current;
                    rest.Append(ph.Text).Append(' ');
                    yield return ph;
                }
            }
            finally { try { await e.DisposeAsync(); } catch (Exception) { } }
            AppendToLastAnswer(rest.ToString().Trim());
        }
    }

    /// <summary>
    /// Кто подхватит, если выбранный мозг отказал. Раньше запасной был ОДИН (fallback профиля), и когда он тоже лежал
    /// (01.11: AG на лимите → Жминя с нулевой квотой) — «мозг не ответил». Теперь цепочка: явные запасные по ссылкам
    /// fallback, потом остальные облачные (API раньше CLI: DeepSeek отвечает за секунду, Codex — за шесть), свой мозг —
    /// последним рубежом, если его модель на диске. Без ключа, без входа в Codex, без глаз при снимке — мимо.
    /// Claude Code сам в цепочку не попадает: без входа CLI висит молча.
    /// </summary>
    private List<BrainProfile> FallbackChain(BrainProfile p, byte[]? image)
    {
        var chain = new List<BrainProfile>();
        bool Usable(BrainProfile b) =>
            b != p && !chain.Contains(b)
            && (image is null || b.Vision)
            && (b.Key is null || ApiKeys.Resolve(b.Key) is not null)
            && b.Kind switch
            {
                "openai" => !b.IsLocalServer,   // STRATA и прочие свои серверы сами не поднимаем: ~62 ГБ ОЗУ без спроса

                "codex" => CodexCli.FindExe() is not null && CodexCli.LoggedIn(),
                _ => false,   // claude-code — см. выше; свои мозги — отдельно, последними
            };
        var seen = new HashSet<string>(StringComparer.Ordinal) { p.Id };
        for (var id = p.Fallback; !string.IsNullOrEmpty(id) && seen.Add(id);)
        {
            var b = _cfg.Brains.FirstOrDefault(x => x.Id == id);
            if (b is null) break;
            if (Usable(b)) chain.Add(b);
            id = b.Fallback;
        }
        foreach (var b in _cfg.Brains.Where(b => b.Kind == "openai").Concat(_cfg.Brains.Where(b => b.Kind == "codex")))
            if (Usable(b)) chain.Add(b);
        if (!p.IsLocal && _cfg.Brains.FirstOrDefault(b => b.Kind == "own") is { } own && (image is null || own.Vision)
            && File.Exists(_cfg.Abs(_cfg.BrainModel)))
            chain.Add(own);
        return chain;
    }

    /// <summary>
    /// Спросить запасных по очереди, пока кто-то не заговорит. Отказ до первого слова (лимит, нет денег, сеть, молчание,
    /// пустой ответ) — следующий; заговорил или сходил в инструменты — дальше не идём (два мозга не выполнят одно дважды).
    /// Отказали все — BrainsDownException с причинами по каждому: она скажет честно, почему, а не «мозг не ответил».
    /// </summary>
    private async IAsyncEnumerable<Phrase> AskChainAsync(List<BrainProfile> chain, string userText, string? context, byte[]? image,
        Func<ToolRequest, Task<bool>> approve, bool toolsTurn, List<(string Brain, string Why)> reasons, [EnumeratorCancellation] CancellationToken ct)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            var b = chain[i];
            LastBrainId = b.Id;
            var e = AskOneAsync(b, userText, context, image, approve, toolsTurn && ToolsTurn(b, userText), null, ct, hasBackup: i < chain.Count - 1)
                .GetAsyncEnumerator(ct);
            var spoke = false;
            string? fail = null;
            try
            {
                while (true)
                {
                    bool has;
                    try { has = await e.MoveNextAsync(); }
                    catch (Exception ex) when (!ct.IsCancellationRequested && !spoke && !TurnUsedTools) { fail = ex.Message; break; }
                    if (!has) break;
                    spoke = true;
                    yield return e.Current;
                }
            }
            finally { try { await e.DisposeAsync(); } catch (Exception) { } }
            if (spoke || TurnUsedTools) yield break;
            fail ??= "промолчал";
            reasons.Add((b.Name, Short(fail, 140)));
            Log.Write($"мозг {b.Id}: {Short(fail, 160)} — {(i + 1 < chain.Count ? "дальше " + chain[i + 1].Id : "запасных больше нет")}");
        }
        throw new BrainsDownException(reasons);
    }

    /// <summary>Досказанное запасным мозгом — в ту же реплику истории, чтобы мысль там была целой.</summary>
    private void AppendToLastAnswer(string text)
    {
        if (text.Length == 0) return;
        lock (_lock)
        {
            if (_history.Count == 0 || _history[^1].Role != "assistant") return;
            _history[^1] = ("assistant", _history[^1].Text + " " + text);
        }
        SaveHistory();
    }

    /// <summary>
    /// Сколько раз подряд выбранный мозг промолчал дольше своего срока (друг 29.09: «убрать задержку перед ответом»;
    /// Opus через AG почти каждый раз молчал 3 с, и отвечал запасной — уже после этих 3 с). Три раза подряд — на
    /// болтовню сразу спрашивается запасной (выбранный, если без инструментов, бежит наперегонки), а каждую пятую
    /// реплику выбранный снова получает честный шанс: ответил вовремя — счётчик в ноль.
    /// </summary>
    private readonly Dictionary<string, int> _slow = new(StringComparer.Ordinal);

    private bool SkipSlow(BrainProfile p, string userText)
    {
        lock (_slow)
        {
            var n = _slow.TryGetValue(p.Id, out var v) ? v : 0;
            if (n < 3 || TaskIntent(userText)) return false;
            _slow[p.Id] = n + 1;
            return (n - 3) % 5 != 4;   // каждый пятый раз — проверка, не ожил ли
        }
    }

    private void MarkSlow(BrainProfile p, bool slow)
    {
        lock (_slow) _slow[p.Id] = slow ? (_slow.TryGetValue(p.Id, out var v) ? Math.Max(v, 0) + 1 : 1) : 0;
    }

    /// <summary>Выбранный мозг со страховкой: задача — последовательно, болтовня — гонка с запасным.</summary>
    private async IAsyncEnumerable<Phrase> AskRoutedAsync(BrainProfile p, List<BrainProfile> chain, bool toolsTurn, string userText, string? context, byte[]? image,
        Func<ToolRequest, Task<bool>> approve, [EnumeratorCancellation] CancellationToken ct)
    {
        var spokeAny = false;
        var fb = chain.FirstOrDefault();
        var hasBackup = fb is not null;

        var skipSlow = fb is not null && SkipSlow(p, userText);
        var hurry = fb is not null && Hurry?.Invoke() == true;
        if (toolsTurn && skipSlow)
        {
            Log.Write($"мозг {p.Id}: подряд молчит дольше срока — сразу отвечает {fb!.Id}");
            await foreach (var ph in AskChainAsync(chain, userText, context, image, approve, true, [(p.Name, "подряд молчит дольше срока")], ct))
            { spokeAny = true; yield return ph; }
            if (!spokeAny && TurnUsedTools && QuietAfterTools?.Invoke() != true) yield return new Phrase("Готово, глянь.", "relaxed");
            if (TurnUsedTools) _toolsUntil = DateTime.UtcNow.AddMinutes(3);
            yield break;
        }
        if (toolsTurn)
        {
            // задача: последовательная страховка
            var firstToken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ctsA = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var a = AskOneAsync(p, userText, context, image, approve, true, () => firstToken.TrySetResult(), ctsA.Token, hasBackup: hasBackup)
                .GetAsyncEnumerator(ctsA.Token);
            var switchToFallback = false;
            string? why = null;
            try
            {
                var next = a.MoveNextAsync().AsTask();
                // болтовня (инструменты под рукой, но просьбы сделать нет) страхуется так же быстро, как раньше гонкой;
                // явная задача — дольше: умной модели нужно подумать перед первым действием
                var waitMs0 = TaskIntent(userText) ? _cfg.ToolTurnFallbackMs : (p.FallbackAfterMs ?? _cfg.FallbackAfterMs);
                if (hurry && fb is not null) waitMs0 = waitMs0 > 0 ? Math.Min(waitMs0, 2500) : 2500;
                var wait = waitMs0 > 0 && fb is not null ? waitMs0 : Timeout.Infinite;
                var first = await Task.WhenAny(next, firstToken.Task, Task.Delay(wait, ct));
                ct.ThrowIfCancellationRequested();
                if (first != next && first != firstToken.Task)
                {
                    switchToFallback = true;
                    why = $"молчит {wait / 1000} с";
                    MarkSlow(p, true);
                    // эта модель в сторону на полторы минуты: следующая реплика сразу пойдёт на соседнюю модель того же
                    // сервера, а не будет снова ждать срок (18.11: запертый Argon каждый раз съедал 6 с тишины)
                    if (!p.IsLocal && p.ModelsFromServer) BrainGuard.Cool(p, ModelFor(p, userText, image), TimeSpan.FromSeconds(90), why);
                    ctsA.Cancel();
                    try { await next; } catch (Exception) { }
                }
                else
                {
                    MarkSlow(p, false);
                    while (true)
                    {
                        bool has;
                        // !ct.IsCancellationRequested, а не «не OperationCanceledException»: таймаут HttpClient — тоже
                        // TaskCanceledException, и раньше он проскакивал мимо запасного прямо в «мозг не ответил»
                        try { has = await next; }
                        catch (Exception ex) when (!ct.IsCancellationRequested && fb is not null && !spokeAny && !TurnUsedTools)
                        {
                            switchToFallback = true;
                            why = ex.Message;
                            break;
                        }
                        if (!has) break;
                        spokeAny = true;
                        yield return a.Current;
                        next = a.MoveNextAsync().AsTask();
                    }
                    // ответила пустотой (ни слова, ни действия) — это тоже отказ, а не «Чё-то я зависла»
                    if (!switchToFallback && !spokeAny && !TurnUsedTools && fb is not null && !ct.IsCancellationRequested)
                    {
                        switchToFallback = true;
                        why = "промолчал";
                    }
                }
            }
            finally
            {
                try { await a.DisposeAsync(); } catch (Exception) { }
            }
            if (switchToFallback && fb is not null)
            {
                Log.Write($"мозг {p.Id}: {why} — задачу берёт {fb.Id}");
                await foreach (var ph in AskChainAsync(chain, userText, context, image, approve, true, [(p.Name, Short(why ?? "отказ", 140))], ct))
                {
                    spokeAny = true;
                    yield return ph;
                }
            }
            if (!spokeAny && TurnUsedTools && QuietAfterTools?.Invoke() != true) yield return new Phrase("Готово, глянь.", "relaxed");
            if (TurnUsedTools) _toolsUntil = DateTime.UtcNow.AddMinutes(3);
            yield break;
        }

        // Болтовня: сначала ждём выбранный мозг; если он после всех ретраев упал до первого слова — отвечает запасной
        IAsyncEnumerator<Phrase>? ea = null;
        bool hasA = false;
        var chatWhy = "промолчал";
        try
        {
            ea = AskOneAsync(p, userText, context, image, approve, false, null, ct, hasBackup: hasBackup).GetAsyncEnumerator(ct);
            hasA = await ea.MoveNextAsync();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested && fb is not null)
        {
            Log.Write($"мозг {p.Id}: сбой первого ответа ({ex.Message}) — болтовню берёт {fb.Id}");
            chatWhy = Short(ex.Message, 140);
            if (ea is not null) try { await ea.DisposeAsync(); } catch (Exception) { }
            ea = null;
        }

        if (ea is not null && hasA)
        {
            try
            {
                do
                {
                    spokeAny = true;
                    yield return ea.Current;
                } while (await ea.MoveNextAsync());
            }
            finally
            {
                await ea.DisposeAsync();
            }
        }
        else
        {
            if (ea is not null) try { await ea.DisposeAsync(); } catch (Exception) { }
            if (fb is not null && !spokeAny)
            {
                Log.Write($"мозг {p.Id}: промолчал или упал — болтовню подхватывает {fb.Id}");
                await foreach (var ph in AskChainAsync(chain, userText, context, image, approve, false, [(p.Name, chatWhy)], ct))
                {
                    spokeAny = true;
                    yield return ph;
                }
            }
        }
    }

    /// <summary>Модели, которым пришлось поднять уровень размышлений (minimal не поддерживают).</summary>
    private static readonly Dictionary<string, string> EffortOverride = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Какой мозг ответил на последнюю реплику (с учётом страховки).</summary>
    public string LastBrainId { get; private set; } = "";

    /// <summary>Задача «следующая фраза» завершилась фразой (не ошибкой, не молчанием).</summary>
    private static bool Spoke(Task<bool> next) => next.IsCompletedSuccessfully && next.Result;

    private static string Failure(Task<bool> next) =>
        next.IsFaulted ? next.Exception!.GetBaseException().Message : next.IsCanceled ? "прерван" : "промолчал";

    /// <summary>
    /// Закрыть поток мозга: оборвать, дождаться начатого шага (DisposeAsync на идущем шаге бросает) и закрыть.
    /// Ошибки здесь глотаются все: это уборка за брошенным или уже упавшим потоком, его ошибку сообщать некому.
    /// </summary>
    private static async Task DisposeQuietlyAsync(IAsyncEnumerator<Phrase> e, Task<bool> pending, CancellationTokenSource cts)
    {
        if (!pending.IsCompleted) cts.Cancel();
        try { await pending; }
        catch (Exception) { }
        try { await e.DisposeAsync(); }
        catch (Exception) { }
        cts.Dispose();
    }

    /// <summary>Модель для этой реплики: болтовня — быстрая ChatModel профиля, задача или снимок — основная.</summary>
    private string ModelFor(BrainProfile p, string userText, byte[]? image) =>
        !TaskIntent(userText) && image is null && !string.IsNullOrWhiteSpace(p.ChatModel) ? p.ChatModel! : p.Model;

    /// <summary>Одна реплика одному мозгу.</summary>
    /// <param name="onFirstToken">Мозг начал отвечать (первый кусок текста или вызов инструмента).</param>
    /// <param name="hasBackup">За ним в цепочке есть запасные: тогда на лимит, перегруз и долгое молчание не долбим ту же
    /// модель по три раза (01.11 это стоило по 6–12 с тишины на каждой реплике), а сразу отдаём ход следующему.</param>
    private async IAsyncEnumerable<Phrase> AskOneAsync(BrainProfile p, string userText, string? context, byte[]? image,
        Func<ToolRequest, Task<bool>> approve, bool withTools, Action? onFirstToken, [EnumeratorCancellation] CancellationToken ct, bool remember = true,
        bool hasBackup = false)
    {
        // system у мозга всегда один и тот же: llama-server держит его и историю в кэше слота, и ответ начинается
        // через секунду. Поэтому экран, комната и правила инструментов идут в саму реплику.
        var system = SystemFor(p);
        var turnWatch = Stopwatch.StartNew();
        if (withTools && RecentActionsNote() is { } note) context = string.IsNullOrWhiteSpace(context) ? note : $"{note}\n{context}";
        if (RepeatNote() is { } rep) context = string.IsNullOrWhiteSpace(context) ? rep : $"{rep}\n{context}";
        var userContent = string.IsNullOrWhiteSpace(context) ? userText : $"{context}\n\n{userText}";
        if (withTools && _personaTools.Length > 0 && !StablePrefix(p)) userContent = $"({_personaTools.Trim()})\n\n{userContent}";
        if (_cfg.WorkMode == "chat" && !userText.StartsWith('(') && WantsTools(userText))
            userContent += "\n\n(Сейчас режим «Общение»: рук и инструментов нет. Просит что-то сделать на компьютере — скажи, " +
                           "что для этого нужен режим «Работа»: кнопка в панели настроек или скажи «режим работы».)";
        // режим общения — прямо в реплике, последним: накладка в конце длинного характера проигрывала истории, где
        // она материлась, и модель продолжала по-старому. В историю напоминание не пишется (там только userText)
        if (PersonaModes.Get(_cfg.PersonaMode).Note is { Length: > 0 } modeNote) userContent = $"{userContent}\n\n({modeNote})";
        var spokenAll = new StringBuilder();
        var actions = new List<string>();
        var emotion = "relaxed/calm";   // «лицо/голос» (см. EmotionTags)
        // Модели плохо держат «одно-два предложения» и льют по пять фраз: лишнее не дослушиваем — обрываем поток.
        // В задаче (явная просьба или уже пошли инструменты) не обрываем: там после слов идут действия.
        var task = TaskIntent(userText);
        var limit = task ? int.MaxValue : LongAnswerRx().IsMatch(userText) ? _cfg.MaxPhrasesLong : _cfg.MaxPhrases + (withTools ? 1 : 0);
        var spoken = new List<Phrase>();
        var cut = false;
        var toolCallsThisTurn = 0;
        var nudged = false;
        var outOfTime = false;
        var heardMidTurn = new List<string>();

        if (p.Kind == "claude-code")
        {
            var pendingCc = new StringBuilder();
            await foreach (var chunk in _claude.AskAsync(p.Model, system, HistorySeed, userContent, image, ct))
            {
                onFirstToken?.Invoke();
                spokenAll.Append(chunk);
                pendingCc.Append(chunk);
                foreach (var ph in CutPhrases(pendingCc, ref emotion, final: false, splitFirst: spoken.Count == 0))
                {
                    yield return ph;
                    spoken.Add(ph);
                    if (spoken.Count(x => !x.Part) >= limit) { cut = true; break; }
                }
                if (cut) break;
            }
            if (!cut)
                foreach (var ph in CutPhrases(pendingCc, ref emotion, final: true, splitFirst: spoken.Count == 0)) { yield return ph; spoken.Add(ph); }
            if (remember) Remember(userText, cut, spoken, spokenAll, actions);
            yield break;
        }

        if (p.Kind == "codex")
        {
            // Codex не помнит беседу между вызовами — недавняя история едет каждый раз, характер — в начале
            var effort = p.Extra?["reasoning_effort"] is JsonValue ev && ev.TryGetValue<string>(out var es) ? es : "low";
            var prompt = $"{system}\n\n{HistorySeed()}Сейчас тебе говорят:\n{userContent}\n\n" +
                         "(Ответь только репликой Клавы, как в живом голосовом разговоре: без пояснений, без кода, без команд и без списков.)";
            var pendingCx = new StringBuilder();
            await foreach (var chunk in CodexCli.AskAsync(ModelFor(p, userText, image), effort, prompt, image, TimeSpan.FromSeconds(task ? 150 : 75), ct))
            {
                onFirstToken?.Invoke();
                spokenAll.Append(chunk);
                pendingCx.Append(chunk);
                foreach (var ph in CutPhrases(pendingCx, ref emotion, final: false, splitFirst: spoken.Count == 0))
                {
                    yield return ph;
                    spoken.Add(ph);
                    if (spoken.Count(x => !x.Part) >= limit) { cut = true; break; }
                }
                if (cut) break;
            }
            if (!cut)
                foreach (var ph in CutPhrases(pendingCx, ref emotion, final: true, splitFirst: spoken.Count == 0)) { yield return ph; spoken.Add(ph); }
            if (remember) Remember(userText, cut, spoken, spokenAll, actions);
            yield break;
        }

        JsonNode turnContent = image is null
            ? JsonValue.Create(userContent)!
            : new JsonArray
            {
                new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(image) } },
                new JsonObject { ["type"] = "text", ["text"] = userContent },
            };
        var turn = new List<JsonNode> { new JsonObject { ["role"] = "user", ["content"] = turnContent } };
        var key = ApiKeys.Resolve(p.Key);
        if (p.Key is not null && key is null) throw new InvalidOperationException($"нет ключа для «{p.DisplayName}»: {ApiKeys.Hint(p.Key)}");
        var url = _cfg.ApiUrl(p);
        var http = NetProxy.ClientFor(url, p.Proxy);
        var model = p.IsLocal ? _cfg.LlmModel : ModelFor(p, userText, image);
        var tried = new List<string> { model };
        var hops = 0;            // пересадки на соседнюю модель того же сервера за реплику
        var relaunched = false;  // упавший прокси поднимаем раз за реплику
        var keyBased = p.Key is not null && p.Key != "antigravity-tools";
        if (!p.IsLocal)
        {
            // профиль или модель отдыхает (лимит, нет денег) — к серверу даже не ходим: соседняя модель или следующий мозг
            if (BrainGuard.CooledFor(p, null) is { } whole)
                throw new BrainRestingException($"{p.Name}: отдыхает ещё {BrainGuard.Human(whole)}");
            if (BrainGuard.CooledFor(p, model) is { } left)
            {
                var alt = await BrainGuard.SiblingAsync(_cfg, p, model, task || image is not null, tried, ct);
                if (alt is null) throw new BrainRestingException($"{p.Name}: {model} на лимите ещё {BrainGuard.Human(left)}, пересесть не на что");
                Log.Write($"мозг {p.Id}: {model} отдыхает ещё {BrainGuard.Human(left)} — беру {alt}");
                model = alt;
                tried.Add(alt);
            }
        }

        for (var round = 0; round <= MaxToolRounds; round++)
        {
            if (round > 0 && toolCallsThisTurn > 0)
            {
                // друг что-то сказал, пока она работала, — сразу в разговор, а не после хода
                if (TakeInterjections?.Invoke() is { Count: > 0 } heard)
                {
                    heardMidTurn.AddRange(heard);
                    Log.Write($"мозг {p.Id}: посреди задачи услышала: {string.Join(" | ", heard.Select(h => Short(h, 80)))}");
                    AddUserNote(turn, "(Пока ты работала, тебе сказали — учти прямо сейчас и поправь план. Просят остановиться — остановись и коротко ответь:\n" +
                                      string.Join("\n", heard.Select(h => "— " + h)) + ")");
                }
                // бюджет хода: дольше ToolTurnBudgetSec с инструментами — стоп, честный отчёт словами
                if (!outOfTime && turnWatch.Elapsed.TotalSeconds > Math.Max(30, _cfg.ToolTurnBudgetSec))
                {
                    outOfTime = true;
                    Log.Write($"мозг {p.Id}: задача идёт {turnWatch.Elapsed.TotalSeconds:F0} с — инструменты забираю, пусть отчитается");
                    AddUserNote(turn, $"(Система: на задачу ушло уже {turnWatch.Elapsed.TotalSeconds:F0} с. Инструменты больше не вызывай. " +
                                      "Одной-двумя фразами честно скажи другу, что получилось, что нет и что предлагаешь дальше.)");
                }
            }
            var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
            lock (_lock)
            {
                foreach (var (role, text) in _history)
                    messages.Add(new JsonObject { ["role"] = role, ["content"] = text });
            }
            foreach (var m in turn) messages.Add(m.DeepClone());

            var body = new JsonObject
            {
                ["model"] = model,
                ["stream"] = true,
                ["max_tokens"] = p.MaxTokens,
                ["messages"] = messages,
            };
            var temperature = p.Temperature ?? (p.IsLocal ? _cfg.LlmTemperature : null);
            if (temperature is { } t) body["temperature"] = t;
            if (p.IsLocal && !withTools)
            {
                // Свой мозг в болтовне залипает на выкройке (01.10: 12 ответов из 14 — «[смех] …блять, да я ж блять… нахуй!
                // [радость] … — ёпта, … сука! [хитрость] … — охуеть!», целые фразы слово в слово): история, полная его же
                // ответов, тянет повторять их. DRY штрафует продолжение любой цепочки, уже встречавшейся в контексте, —
                // чем длиннее повтор, тем сильнее; метки эмоций и скобки — разрывы цепочки, их повторять можно.
                // С инструментами не включается: JSON вызова — законный повтор. Перекрывается через extra в klava.json.
                body["dry_multiplier"] = 0.8;
                body["dry_base"] = 1.75;
                body["dry_allowed_length"] = 3;
                body["dry_penalty_last_n"] = 4096;
                body["dry_sequence_breakers"] = new JsonArray("\n", ":", "\"", "*", "[", "]", "(", ")", "«", "»");
                body["presence_penalty"] = 0.3;
            }
            MergeExtra(body, p);
            lock (EffortOverride)
                if (body.ContainsKey("reasoning_effort") && EffortOverride.TryGetValue(model, out var eff)) body["reasoning_effort"] = eff;
            // На последнем круге инструменты не даём — пусть договорит словами.
            PutTools(body, p, withTools && round < MaxToolRounds && !outOfTime);

            // сторож тишины и повторные попытки (долбим запросом, если модель думает, сеть моргнула или 503)
            HttpResponseMessage? resp = null;
            var idleMs = Math.Max(30_000, _cfg.StreamIdleMs);
            // до первых байт ответа: есть запасные — не ждём минуту (AG при занятых аккаунтах держит запрос в очереди до 60 с).
            // Срок общий на мозг вместе со всеми пересадками и только до того, как он заговорил: 18.11 Жминя по ключу
            // висела 45 с без заголовков, и DeepSeek ответил через 47 с — болтовня ждёт не больше 15 с, задача — 30 с.
            var guarded = hasBackup && round == 0;
            var budgetMs = guarded ? Math.Min(idleMs, task ? 30_000 : 15_000) : idleMs;
            var headMs = budgetMs;   // сколько ждём шапку в этой попытке
            const int maxRetries = 3;
            CancellationTokenSource? streamIdle = null;
            for (var attempt = 1; attempt <= maxRetries; attempt++)
            {
                if (guarded)
                {
                    headMs = budgetMs - (int)turnWatch.ElapsedMilliseconds;
                    if (headMs < 2500) throw new TimeoutException($"{p.Name}: {model} — за {budgetMs / 1000} с так и не заговорил (с пересадками)");
                }
                var attemptIdle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attemptIdle.CancelAfter(headMs);
                body["model"] = model;
                using var req = new HttpRequestMessage(HttpMethod.Post, $"{url}/chat/completions")
                {
                    Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (key is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                try
                {
                    resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, attemptIdle.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    attemptIdle.Dispose();
                    if (hasBackup)
                    {
                        // молчит — следующий мозг ответит быстрее, чем эта модель додумает; модель в сторону на полторы минуты
                        if (!p.IsLocal) BrainGuard.Cool(p, model, TimeSpan.FromSeconds(90), $"молчит {headMs / 1000} с");
                        throw new TimeoutException($"{p.Name}: {model} молчит {headMs / 1000} с");
                    }
                    Log.Write($"мозг {p.Id}: таймаут {headMs / 1000} с (попытка {attempt}/{maxRetries}) — долблю ещё раз");
                    if (attempt == maxRetries) throw new TimeoutException($"{p.Name}: {model} не ответил за {headMs / 1000} с после {maxRetries} попыток");
                    await Task.Delay(1500, ct);
                    continue;
                }
                catch (HttpRequestException ex) when (!ct.IsCancellationRequested)
                {
                    attemptIdle.Dispose();
                    if (BrainGuard.Refused(ex))
                    {
                        // на адресе никто не слушает: прокси AG Tools упал посреди разговора (01.11 — полчаса «подключение
                        // отвергнуто») или свой мозг не запущен. Долбить мёртвый порт незачем — поднять или идти дальше.
                        if (!relaunched && !string.IsNullOrWhiteSpace(p.Launch))
                        {
                            relaunched = true;
                            if (hasBackup)
                            {
                                _ = BrainGuard.RelaunchAsync(_cfg, p, CancellationToken.None);   // поднимется к следующей реплике
                                throw new BrainRestingException($"{p.Name}: прокси не слушает {url} — поднимаю, пока отвечает запасной");
                            }
                            if (await BrainGuard.RelaunchAsync(_cfg, p, ct)) { attempt--; continue; }
                        }
                        throw new BrainRestingException($"{p.Name}: не слушает {url} ({(p.IsLocal ? "свой мозг не запущен" : "сервер не запущен")})");
                    }
                    if (attempt >= maxRetries) throw;
                    Log.Write($"мозг {p.Id}: сбой сети ({ex.Message}, попытка {attempt}/{maxRetries}) — повторяю запрос");
                    await Task.Delay(1500, ct);
                    continue;
                }

                if (!resp.IsSuccessStatusCode)
                {
                    attemptIdle.Dispose();
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    var status = resp.StatusCode;
                    var retryAfter = resp.Headers.RetryAfter?.Delta;
                    resp.Dispose();
                    resp = null;
                    // У Gemini 3.7/3.8/Argon нет уровня minimal: повторяем с low и запоминаем для этой модели.
                    if ((int)status == 400 && err.Contains("Thinking level", StringComparison.OrdinalIgnoreCase)
                        && body["reasoning_effort"]?.GetValue<string>() is { } effort && effort != "low")
                    {
                        lock (EffortOverride) EffortOverride[model] = "low";
                        Log.Write($"мозг {p.Id}: у {model} нет уровня размышлений «{effort}» — дальше low");
                        round--;
                        break;
                    }
                    var verdict = BrainGuard.Judge((int)status, err, retryAfter, keyBased);
                    if (verdict.Kind != BrainGuard.FailKind.Other && !p.IsLocal)
                    {
                        BrainGuard.Cool(p, verdict.WholeProfile ? null : model, verdict.Span, $"{(int)status} {verdict.Text}");
                        // лимит/перегруз/пропала модель — у AG на соседней модели другие аккаунты: пересесть, а не прыгать с сервера
                        if (!verdict.WholeProfile && hops < (keyBased ? 2 : 4) && (!guarded || budgetMs - turnWatch.ElapsedMilliseconds > 4000)
                            && await BrainGuard.SiblingAsync(_cfg, p, model, task || image is not null, tried, ct) is { } alt)
                        {
                            hops++;
                            Log.Write($"мозг {p.Id}: {model} — {verdict.Text} ({(int)status}) — пересаживаюсь на {alt}");
                            model = alt;
                            tried.Add(alt);
                            attempt--;   // пересадка — не попытка
                            continue;
                        }
                        // перегруз на десяток секунд и запасных нет — одна повторная попытка; иначе сразу следующему мозгу
                        if (verdict.Kind == BrainGuard.FailKind.Busy && !hasBackup && attempt < maxRetries)
                        {
                            Log.Write($"мозг {p.Id}: {model} — {verdict.Text} ({(int)status}), попытка {attempt}/{maxRetries} — повторяю");
                            await Task.Delay(2000, ct);
                            continue;
                        }
                        throw new BrainRestingException($"{p.Name}: {model} — {verdict.Text} ({(int)status} {Short(ApiError(err), 100)})");
                    }
                    if (((int)status is 503 or 429 or 500 or 502 or 504) && attempt < maxRetries && !hasBackup)
                    {
                        Log.Write($"мозг {p.Id}: статус {(int)status} ({ApiError(err)}), попытка {attempt}/{maxRetries} — долблю снова");
                        await Task.Delay(2000, ct);
                        continue;
                    }
                    throw new HttpRequestException($"{p.Name}: {model}: {(int)status} {ApiError(err)}", null, status);
                }
                attemptIdle.CancelAfter(idleMs);   // шапка пришла — дальше сторож тишины потока
                if (!p.IsLocal) BrainGuard.Clear(p, model);
                streamIdle = attemptIdle;
                break;
            }
            if (resp is null || streamIdle is null) continue;
            using var idle = streamIdle;
            using var respScope = resp;
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var roundText = new StringBuilder();
            var leaked = false;   // в текст протекла служебная разметка вызова инструмента
            string? finish = null;   // finish_reason последнего куска: stop / length / content_filter / tool_calls
            var pending = new StringBuilder();
            var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
            string? line;
            while (true)
            {
                try { line = await reader.ReadLineAsync(idle.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"{p.DisplayName}: поток ответа замолчал на {idleMs / 1000} с");
                }
                if (line is null) break;
                idle.CancelAfter(idleMs);
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line.AsSpan(5).Trim();
                if (data.SequenceEqual("[DONE]")) break;
                JsonNode? chunk;
                try { chunk = JsonNode.Parse(data.ToString()); }
                catch (JsonException) { continue; }   // обрывок служебной строки — не повод ронять ответ
                if (chunk?["error"] is { } streamError) throw new HttpRequestException($"{p.DisplayName}: {ApiError(streamError.ToJsonString())}");
                // у Gemini и DeepSeek бывают куски без вариантов (одна статистика) — индексатор на пустом массиве бросает
                var choice0 = chunk?["choices"] is JsonArray { Count: > 0 } choices ? choices[0] : null;
                if (choice0?["finish_reason"] is JsonValue fr && fr.TryGetValue<string>(out var frs) && frs.Length > 0) finish = frs;
                var delta = choice0?["delta"];
                if (delta is null) continue;

                if (delta["tool_calls"] is JsonArray tcs)
                {
                    onFirstToken?.Invoke();
                    foreach (var tc in tcs)
                    {
                        var idx = tc?["index"]?.GetValue<int>() ?? 0;
                        if (!calls.TryGetValue(idx, out var c)) c = ("", "", new StringBuilder());
                        var id = tc?["id"]?.GetValue<string>();
                        var name = tc?["function"]?["name"]?.GetValue<string>();
                        var args = tc?["function"]?["arguments"]?.GetValue<string>();
                        c = (string.IsNullOrEmpty(id) ? c.Id : id, string.IsNullOrEmpty(name) ? c.Name : name, c.Args);
                        if (args is not null) c.Args.Append(args);
                        calls[idx] = c;
                    }
                }

                var content = delta["content"]?.GetValue<string>();
                if (string.IsNullOrEmpty(content)) continue;
                onFirstToken?.Invoke();
                roundText.Append(content);
                // служебная разметка вызова инструмента протекла в текст (у DeepSeek — «<｜｜DSML｜｜ invoke …») —
                // с этого места ничего не озвучиваем, после круга пинаем вызвать по-настоящему
                if (leaked) continue;
                if (LeakRx().Match(roundText.ToString()) is { Success: true } lm)
                {
                    leaked = true;
                    var keep = content.Length - (roundText.Length - lm.Index);
                    if (keep > 0) pending.Append(content[..keep]);
                    Log.Write($"мозг {p.Id}: служебная разметка инструмента в тексте — не озвучиваю");
                }
                else pending.Append(content);
                foreach (var ph in CutPhrases(pending, ref emotion, final: false, splitFirst: spoken.Count == 0))
                {
                    yield return ph;
                    spoken.Add(ph);
                    if (spoken.Count(x => !x.Part) >= limit) { cut = true; break; }
                }
                if (cut) break;
            }
            if (!cut)
                foreach (var ph in CutPhrases(pending, ref emotion, final: true, splitFirst: spoken.Count == 0)) { yield return ph; spoken.Add(ph); }
            var roundSaid = roundText.ToString();
            if (leaked) roundSaid = roundSaid[..LeakRx().Match(roundSaid).Index];
            spokenAll.Append(roundSaid).Append(' ');

            // Оборвалась на полуслове: фильтр провайдера/лимит токенов или последняя фраза без точки при нестандартном конце
            // Честность: написала вызов инструмента текстом или пообещала «щас сделаю», а инструмент не вызвала —
            // один раз пинаем: «вызови по-настоящему или честно скажи, что не можешь». Раньше тут и был весь обман.
            // Без инструментов в этой реплике (болтовня своего мозга, режим «Общение»), а она сказала «щас запущу» или на
            // просьбу ответила «запускаю»: руки даются на добор (кроме режима «Общение») — сделать или честно отказаться.
            // Друг 01.10: «не хочет играть — пусть скажет, что не в настроении, а не соврёт, что запускает, и дропнет».
            var armed = false;
            if (!withTools && !cut && calls.Count == 0 && !nudged && round < MaxToolRounds && !userText.StartsWith('(') && p.Kind != "claude-code"
                && (PromiseRx().IsMatch(roundText.ToString()) || DoingRx().IsMatch(roundText.ToString()) && WantsTools(userText)))
            {
                armed = true;
                withTools = p.Tools && _cfg.WorkMode != "chat";
            }
            if ((withTools || armed) && !cut && calls.Count == 0 && !nudged && round < MaxToolRounds)
            {
                var said = roundText.ToString();
                var fake = leaked || FakeToolRx().IsMatch(said);
                if (leaked) said = said[..LeakRx().Match(said).Index];
                // заявки «жму/бегу» — кроме комментариев к игре её бота в CS: там играет бот, а она болеет
                var promise = armed || toolCallsThisTurn == 0 && (PromiseRx().IsMatch(said) || (ClaimRx().IsMatch(said) && CsBridge.KlavaName is null)
                                                               || DoingRx().IsMatch(said) && WantsTools(userText));
                if (fake || promise)
                {
                    nudged = true;
                    Log.Write($"мозг {p.Id}: {(fake ? "написала вызов инструмента текстом" : "пообещала действие без инструмента")} — «{Short(said, 90)}» — пинаю вызвать по-настоящему");
                    turn.Add(new JsonObject { ["role"] = "assistant", ["content"] = FakeToolRx().Replace(said, "").Trim() });
                    turn.Add(new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = fake
                            ? "(Система: ты написала вызов инструмента обычным текстом — так он НЕ выполняется. Вызови инструмент по-настоящему через функцию. Вслух больше ничего не повторяй.)"
                            : withTools
                                ? "(Система: ты сказала, что делаешь это, но не вызвала ни одного инструмента — значит, ничего не сделано. Сделай это сейчас через инструменты. Не хочешь или не можешь — честно скажи одной фразой, как есть: не в настроении, не умею, не вышло. Не делай вид, что сделала, и уже сказанное вслух не повторяй.)"
                                : "(Система: ты сказала, что делаешь это, но в режиме «Общение» рук у тебя нет — ничего не сделано. Честно скажи одной фразой, что не сделала и почему; попросят — пусть включат режим «Работа».)",
                    });
                    continue;
                }
            }

            if (!cut && calls.Count == 0 && spoken.Count > 0 && !withTools)
            {
                var last = spoken[^1].Text;
                var unterminated = last.Length > 0 && !".!?…»\"')".Contains(last[^1]);
                if (finish is "content_filter" or "safety" or "SAFETY" or "length" or "recitation" or "RECITATION" or "PROHIBITED_CONTENT"
                    || (unterminated && finish is not "stop"))
                {
                    Log.Write($"мозг {p.Id}: ответ оборван (finish_reason={finish ?? "нет"}), последняя фраза «{Short(last, 60)}»");
                    _truncated = (p.Id, string.Join(" ", spoken.Select(x => x.Text)));
                }
            }

            if (cut || calls.Count == 0) break;

            TurnUsedTools = true;
            var toolCalls = new JsonArray();
            var parsed = new List<ToolCall>();
            foreach (var (idx, c) in calls)
            {
                var id = string.IsNullOrEmpty(c.Id) ? $"call_{round}_{idx}" : c.Id;
                parsed.Add(new ToolCall(id, c.Name, c.Args.ToString()));
                toolCalls.Add(new JsonObject
                {
                    ["id"] = id,
                    ["type"] = "function",
                    ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Args.ToString() },
                });
            }
            turn.Add(new JsonObject { ["role"] = "assistant", ["content"] = roundText.ToString(), ["tool_calls"] = toolCalls });
            var shots = new List<byte[]>();
            foreach (var call in parsed)
            {
                toolCallsThisTurn++;
                Log.Write($"инструмент → {call.Name} {Short(call.ArgumentsJson, 160)}");
                ToolResult result;
                UiWatchdog.Doing($"инструмент {call.Name} {Short(call.ArgumentsJson, 80)}");
                try { result = await Tools.RunAsync(call, approve, _cfg, _toolbox, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // упавший инструмент — это ответ «не сработало», а не повод ронять весь разговор
                    Log.Error($"инструмент {call.Name}", ex);
                    result = new ToolResult($"Ошибка: {ex.Message}");
                }
                finally { UiWatchdog.Done(); }
                var failed = ToolFailRx().IsMatch(result.Text);
                // ненулевой код выхода — не «не сработало»: команда выполнилась, но с ошибкой. Раньше и он шёл с пометкой
                // «действие НЕ выполнено» — и после первого же кода 1 (git вне репо, where без находки) она бросала дело
                // (друг 30.09: «после первого экзит кода падает»). Теперь — разобрать вывод, починить и продолжать.
                var exitFail = !failed && call.Name is "shell" or "terminal_type" or "run_powershell" or "bash" or "linux"
                               && ExitCodeFailRx().Match(result.Text[..Math.Min(80, result.Text.Length)]) is { Success: true };
                var text = failed
                    ? $"[НЕ СРАБОТАЛО — действие НЕ выполнено, не говори, что сделала] {result.Text}"
                    : exitFail
                        ? $"[команда завершилась с ошибкой — прочитай вывод, почини и продолжай; «готово» — только после проверки] {result.Text}"
                        : result.Text;
                Log.Write($"инструмент ← {call.Name}{(failed ? " [НЕ СРАБОТАЛО]" : "")}: {Short(result.Text, 200)}");
                var actLine = $"{call.Name} {Short(call.ArgumentsJson, 70)} → {(failed ? "НЕ СРАБОТАЛО: " : "ок: ")}{Short(result.Text, 90)}";
                actions.Add(actLine);
                lock (_recentActions) _recentActions.Add((DateTime.UtcNow, actLine));
                turn.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = call.Id, ["content"] = text });
                if (result.Image is not null) shots.Add(result.Image);
            }
            limit = int.MaxValue;   // пошли действия — отчёт о них не обрываем
            // снимки — отдельной репликой-картинкой: результат инструмента текстом картинку не несёт
            if (shots.Count > 0)
            {
                var content = new JsonArray();
                foreach (var jpeg in shots.TakeLast(2))
                    content.Add(new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = "data:image/jpeg;base64," + Convert.ToBase64String(jpeg) } });
                content.Add(new JsonObject { ["type"] = "text", ["text"] = "(вот что сейчас на экране — работай по этому снимку)" });
                turn.Add(new JsonObject { ["role"] = "user", ["content"] = content });
            }
            // старые снимки из прошлых кругов уже не нужны — только раздувают запрос (каждый ~150 КБ)
            var imageTurns = turn.Where(n => n is JsonObject o && o["role"]?.GetValue<string>() == "user" && o["content"] is JsonArray).ToList();
            foreach (var old in imageTurns.SkipLast(1).Skip(image is null ? 0 : 1))
                if (old is JsonObject o) o["content"] = "(здесь был снимок экрана — он устарел, смотри последний)";
        }

        // поправки посреди задачи — в историю вместе с исходной просьбой, иначе в следующей реплике их как не было
        if (remember) Remember(heardMidTurn.Count == 0 ? userText : $"{userText}\n(по ходу дела: {string.Join(" / ", heardMidTurn)})", cut, spoken, spokenAll, actions);
    }

    /// <summary>
    /// Системная заметка в ход: к последней реплике «друга» (текст или картинки со снимком), чтобы не шли две
    /// user-реплики подряд — часть провайдеров их не любит.
    /// </summary>
    private static void AddUserNote(List<JsonNode> turn, string note)
    {
        if (turn.Count > 0 && turn[^1] is JsonObject last && last["role"]?.GetValue<string>() == "user")
        {
            if (last["content"] is JsonArray parts) { parts.Add(new JsonObject { ["type"] = "text", ["text"] = note }); return; }
            if (last["content"] is JsonValue v && v.TryGetValue<string>(out var s)) { last["content"] = $"{s}\n\n{note}"; return; }
        }
        turn.Add(new JsonObject { ["role"] = "user", ["content"] = note });
    }

    [GeneratedRegex(@"[\p{L}\d_]+")]
    private static partial Regex WordRx();

    /// <summary>Первые два слова реплики без меток эмоций — «кибер_кот блять».</summary>
    private static string Opener(string text) =>
        string.Join(' ', WordRx().Matches(TagRx().Replace(text, " ")).Take(2).Select(m => m.Value.ToLowerInvariant()));

    /// <summary>
    /// Залипание в долгой сессии: модель копирует свои прошлые реплики, и зачин («Кибер_Кот, блять…») крепнет с каждым
    /// ответом. Три из последних четырёх начинаются одинаково — просим начать иначе.
    /// </summary>
    private string? RepeatNote()
    {
        List<string> openers;
        lock (_lock) openers = _history.Where(h => h.Role == "assistant").TakeLast(4).Select(h => Opener(h.Text)).Where(o => o.Length > 0).ToList();
        // та же выкройка три ответа подряд: одинаковые метки эмоций в том же порядке (смех → радость → хитрость)
        List<string> shapes;
        lock (_lock) shapes = _history.Where(h => h.Role == "assistant").TakeLast(3).Select(h => string.Join(' ', TagRx().Matches(h.Text).Select(m => m.Groups[1].Value))).ToList();
        var shape = shapes.Count == 3 && shapes[0].Contains(' ') && shapes.All(s => s == shapes[0])
            ? $"(Система: три ответа подряд у тебя одной выкройки — те же эмоции по порядку ({shapes[0]}) и те же словечки на тех же местах. " +
              "Сломай её: другая эмоция, одна-две фразы другого строя, без «да я ж», мат — не в каждой фразе и не тем же словом.)"
            : null;
        if (openers.Count < 3) return shape;
        var top = openers.GroupBy(o => o).MaxBy(g => g.Count())!;
        return top.Count() < 3 ? shape
            : $"(Система: последние ответы ты начинала одинаково — «{top.Key}…». Сейчас начни по-другому, без этого зачина и без своих прошлых оборотов.)" +
              (shape is null ? "" : "\n" + shape);
    }

    private static string Short(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>
    /// В историю — только то, что она реально сказала (иначе модель «помнит» недослушанный хвост),
    /// плюс короткий след сделанных действий: без него в следующей реплике она не помнит, что уже открыла,
    /// где терминал и что вернула команда — и начинает заново или отказывается.
    /// </summary>
    private void Remember(string userText, bool cut, List<Phrase> spoken, StringBuilder spokenAll, List<string> actions)
    {
        string said;
        if (cut)
        {
            // куски, отрезанные по запятой, склеиваем обратно в одно предложение с одной меткой
            var sb = new StringBuilder();
            var afterPart = false;
            foreach (var ph in spoken)
            {
                if (sb.Length > 0) sb.Append(' ');
                if (!afterPart) sb.Append('[').Append(RuTag(ph.Emotion, ph.Voice)).Append("] ");
                sb.Append(ph.Text);
                afterPart = ph.Part;
            }
            said = sb.ToString();
        }
        else said = spokenAll.ToString().Trim();
        // След действий в её реплику НЕ пишем: модель начинала подражать ему текстом («(сделала инструмент: look
        // screen(…))») вместо настоящего вызова — и говорила «сделала», хотя ничего не запускалось. Действия
        // живут отдельно (_recentActions) и приходят справкой системы в следующие реплики.
        said = FakeToolRx().Replace(said, "").Trim();
        // в чистом режиме и в истории без мата: иначе модель снова заражается своим же прошлым стилем
        if (PersonaModes.Get(_cfg.PersonaMode).Clean) said = PersonaModes.Censor(said);
        if (said.Length == 0 && actions.Count > 0) said = "[спокойствие] (молча поработала инструментами)";
        if (said.Length == 0) return;   // мозг промолчал — пустую реплику не храним
        lock (_lock)
        {
            // в истории у реплики стоит время: иначе вчерашний разговор после перезапуска выглядит как «минуту назад»
            _history.Add(("user", $"({DateTime.Now:dd.MM HH:mm}) {userText}"));
            _history.Add(("assistant", said));
            TrimHistory();
        }
        SaveHistory();
    }

    /// <summary>Из ответа API с ошибкой — только суть (message), без простыни JSON.</summary>
    private static string ApiError(string raw)
    {
        try
        {
            var n = JsonNode.Parse(raw);
            var e = n is JsonArray { Count: > 0 } arr ? arr[0] : n;   // Gemini заворачивает ошибку в массив
            var msg = e?["error"]?["message"]?.GetValue<string>() ?? e?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(msg)) raw = msg;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }
        raw = SpacesRx().Replace(raw.Replace('\n', ' '), " ").Trim();
        return raw.Length > 200 ? raw[..200] + "…" : raw;
    }

    /// <summary>Метка для истории: точная по лицу и голосу, иначе первая с тем же лицом.</summary>
    private static string RuTag(string face, string voice) =>
        EmotionTags.FirstOrDefault(kv => kv.Value == $"{face}/{voice}").Key
        ?? EmotionTags.FirstOrDefault(kv => FaceOf(kv.Value) == face).Key ?? "спокойствие";

    /// <summary>
    /// Отрезает из буфера готовые предложения. Метка эмоции действует до следующей метки.
    /// </summary>
    /// <param name="splitFirst">
    /// Это начало ответа: длинное первое предложение режем по запятой — короткий кусок звучит через секунду,
    /// а остальное синтезируется, пока он играет.
    /// </param>
    private static IEnumerable<Phrase> CutPhrases(StringBuilder buf, ref string emotion, bool final, bool splitFirst = false)
    {
        var result = new List<Phrase>();
        while (true)
        {
            var s = buf.ToString();
            var end = -1;
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                var isEnd = c is '.' or '!' or '?' or '…' or '\n';
                if (!isEnd) continue;
                // многоточие и «?!» — держим вместе
                var j = i;
                while (j + 1 < s.Length && s[j + 1] is '.' or '!' or '?' or '…') j++;
                if (j + 1 < s.Length && !char.IsWhiteSpace(s[j + 1]) && s[j + 1] != '[') continue;
                if (j + 1 >= s.Length && !final) break;       // знак в самом конце — может прийти ещё «..»
                // «т. е. это», «и т. д. и» — после точки строчная буква: это сокращение, а не конец предложения
                if (c == '.' && j == i && j + 1 < s.Length && s[j + 1] == ' ')
                {
                    if (j + 2 >= s.Length) { if (!final) break; }
                    else if (char.IsLower(s[j + 2])) { i = j; continue; }
                }
                if (Clean(s[..(j + 1)], ref emotion, peek: true).Length < 6 && c != '\n') { i = j; continue; }
                end = j + 1;
                // метка-действие сразу за точкой («Отличное пиво. [рыг]», «Ща переоденусь. [наряд готика]») — к этой фразе
                if (FxTagRx().Match(s, end) is { Success: true } after && string.IsNullOrWhiteSpace(s[end..after.Index])) end = after.Index + after.Length;
                break;
            }
            // рыг/пук посреди фразы — режем прямо по метке: слова до неё, звук с облачком, дальше следующая фраза (как у Рика)
            if (FxTagRx().Match(s) is { Success: true } fm && (end < 0 || fm.Index < end) && FxOf(fm.Groups[1].Value) is "burp" or "fart"
                && Clean(s[..fm.Index], ref emotion, peek: true).Length >= 2)
            {
                var kind = FxOf(fm.Groups[1].Value);
                var before = Clean(s[..fm.Index], ref emotion, peek: false);
                buf.Remove(0, fm.Index + fm.Length);
                result.Add(new Phrase(before, FaceOf(emotion), false, VoiceOf(emotion), kind, true));
                continue;
            }
            var byComma = false;
            if (splitFirst && result.Count == 0 && CommaCut(s, end, final, emotion) is > 0 and var comma) { end = comma; byComma = true; }
            if (end < 0)
            {
                if (final && s.Trim().Length > 0)
                {
                    var fxTail = FxLead(s, ref emotion);
                    var tail = Clean(s, ref emotion, peek: false);
                    // одна метка без слов в самом конце — всё равно действие (пустая фраза: только жест/переодевание)
                    if (tail.Length > 0 || fxTail is not null) result.Add(new Phrase(tail, FaceOf(emotion), false, VoiceOf(emotion), fxTail));
                    buf.Clear();
                }
                return result;
            }
            var fx = FxLead(s[..end], ref emotion);
            var text = Clean(s[..end], ref emotion, peek: false);
            buf.Remove(0, end);
            if (text.Length > 0) result.Add(new Phrase(text, FaceOf(emotion), byComma, VoiceOf(emotion), fx));
        }
    }

    /// <summary>
    /// Где резать первое предложение, чтобы голос начал раньше (друг 29.09: «убрать задержку перед ответом»; голос теперь
    /// потоком, и ждать остаётся только, пока мозг допишет первый кусок). Режем после запятой, когда до неё ≥ 18 знаков
    /// речи: у готового предложения (точка пришла) — если оно длиннее 45 знаков и после запятой остаётся ≥ 14; пока
    /// точки нет — на первой такой запятой сразу, дальнейшее придёт следующей фразой. 0 — не резать.
    /// </summary>
    private static int CommaCut(string s, int sentenceEnd, bool final, string emotion)
    {
        var limit = sentenceEnd >= 0 ? sentenceEnd : s.Length;
        var spoken = Clean(s[..limit], ref emotion, peek: true).Length;
        var open = sentenceEnd < 0 && !final;       // предложение ещё пишется
        if (!open && spoken <= 45) return 0;
        for (var i = 0; i + 1 < limit; i++)
        {
            if (s[i] != ',' || s[i + 1] != ' ') continue;
            if (Clean(s[..(i + 1)], ref emotion, peek: true).Length < 18) continue;
            if (!open && Clean(s[(i + 1)..limit], ref emotion, peek: true).Length < 14) return 0;
            return i + 1;
        }
        return 0;
    }

    /// <summary>Убирает метки, действия в звёздочках, эмодзи и разметку; запоминает последнюю эмоцию.</summary>
    private static string Clean(string raw, ref string emotion, bool peek)
    {
        var emo = emotion;
        var text = TagRx().Replace(raw, m =>
        {
            if (EmotionTags.TryGetValue(m.Groups[1].Value.Trim(), out var e)) emo = e;
            return " ";
        });
        if (!peek) emotion = emo;
        text = FakeToolRx().Replace(text, " ");   // «вызов инструмента» текстом — не для озвучки
        if (LeakRx().Match(text) is { Success: true } leak) text = text[..leak.Index];
        text = ActionRx().Replace(text, " ");
        text = EmojiRx().Replace(text, "");
        text = MarkupRx().Replace(text, " ");
        text = text.Replace("**", "").Replace("*", "");
        return SpacesRx().Replace(text, " ").Trim();
    }

    /// <summary>
    /// Переполнилась — режем сразу до 60 % лимита, а не по одной паре: каждая обрезка сдвигает начало разговора, и
    /// кэш промпта у мозга (у своего llama-server — минута пересчёта на процессоре) пропадает. Пачкой — раз в много ходов.
    /// </summary>
    private void TrimHistory()
    {
        var total = _history.Sum(h => h.Text.Length);
        if (total <= _cfg.HistoryMaxChars) return;
        var goal = _cfg.HistoryMaxChars * 6 / 10;
        while (total > goal && _history.Count > 2)
        {
            total -= _history[0].Text.Length + _history[1].Text.Length;
            _history.RemoveRange(0, 2);
        }
    }

    private void LoadHistory()
    {
        var file = _cfg.Abs(_cfg.HistoryFile);
        if (!File.Exists(file) && !File.Exists(file + ".bak")) return;
        try
        {
            // основной файл, не читается — прошлая запись (.bak) и копии дня: разговор не пропадает из-за обрыва записи
            var arr = SafeFile.Load(file, t => JsonNode.Parse(t) as JsonArray);
            if (arr is null) return;
            foreach (var n in arr)
            {
                var role = n?["role"]?.GetValue<string>();
                var text = n?["content"]?.GetValue<string>();
                if (role is "user" or "assistant" && text is not null)
                {
                    // старые записи со следом «(сделала инструментами: …)» — вычищаем, чтобы модель им не подражала
                    var clean = role == "assistant" ? FakeToolRx().Replace(OldTraceRx().Replace(text, ""), "").Trim() : text;
                    if (clean.Length > 0) _history.Add((role, clean));
                }
            }
            TrimHistory();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            Log.Error("история разговора не прочиталась — начинаю с чистой (битый файл отложен рядом)", ex);   // запуск не роняем
        }
    }

    private void SaveHistory()
    {
        if (!_saveHistory) return;
        var file = _cfg.Abs(_cfg.HistoryFile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        JsonArray arr;
        lock (_lock)
            arr = new JsonArray(_history.Select(h => (JsonNode)new JsonObject { ["role"] = h.Role, ["content"] = h.Text }).ToArray());
        try
        {
            lock (_historyDisk)
                SafeFile.Write(file, arr.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("история разговора не записалась", ex); }
    }

    private readonly object _historyDisk = new();
}
