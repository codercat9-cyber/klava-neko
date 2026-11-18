using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace KlavaDesk;

/// <summary>
/// Один из мозгов, между которыми Клаву можно переключать (меню «Мозг» или голосом «переключись на жминю»).
/// </summary>
internal sealed class BrainProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>
    /// own — свой llama-server (Qwen3.6-35B-A3B: общие слои на видеокарте, эксперты в ОЗУ); flashnext — старый FlashNext через START_FLASHNEXT.ps1;
    /// openai — любой OpenAI-совместимый API (Gemini, DeepSeek, прокси Antigravity Tools);
    /// claude-code — Claude Code CLI по подписке (без инструментов на компьютере);
    /// codex — ChatGPT через Codex CLI по подписке ChatGPT (вход «codex login»; тоже без инструментов).
    /// </summary>
    public string Kind { get; set; } = "openai";
    /// <summary>Адрес OpenAI-совместимого API без /chat/completions. У своих мозгов — llmUrl + /v1.</summary>
    public string Url { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>env:ИМЯ — ключ из переменной окружения или из ~/.dsh/.env; antigravity-tools — ключ прокси из его настроек.</summary>
    public string? Key { get; set; }
    /// <summary>Умеет смотреть на картинки (тогда по «глянь» уходит снимок экрана). У своего мозга — если есть проектор.</summary>
    public bool Vision { get; set; }
    /// <summary>Можно давать инструменты (папки, файлы, PowerShell — каждое действие с разрешения друга).</summary>
    public bool Tools { get; set; } = true;
    /// <summary>Потолок ответа в токенах; у думающих моделей сюда же входят размышления.</summary>
    public int MaxTokens { get; set; } = 400;
    /// <summary>null — своя температура провайдера (Gemini 3 просят не трогать 1.0), у своих мозгов — llmTemperature.</summary>
    public double? Temperature { get; set; }
    /// <summary>Добавки к телу запроса — прежде всего как выключить размышления у этого провайдера.</summary>
    public JsonObject? Extra { get; set; }
    /// <summary>Что запустить, если адрес не отвечает (Antigravity Tools); переменные окружения раскрываются.</summary>
    public string? Launch { get; set; }
    /// <summary>Как его назвать голосом: «переключись на …». Сравнивается по началу слова, без регистра.</summary>
    public string[] Aliases { get; set; } = [];
    /// <summary>
    /// Запасной мозг (id): если этот молчит дольше fallbackAfterMs или падает, тот же вопрос уходит запасному,
    /// и отвечает тот, кто первым заговорит. Gemini на бесплатном ключе со снимком экрана отвечал то за 2 с, то за 40.
    /// </summary>
    public string? Fallback { get; set; }
    /// <summary>Сколько ждать первую фразу, прежде чем спросить запасной; null — общий fallbackAfterMs.</summary>
    public int? FallbackAfterMs { get; set; }
    /// <summary>
    /// Список моделей брать у самого сервера (/models) — в меню у мозга подменю со всеми моделями.
    /// Так у Antigravity видно всё, что отдают аккаунты AG Tools, а не одну вписанную модель.
    /// </summary>
    public bool ModelsFromServer { get; set; }
    /// <summary>Какие модели из /models показывать (регулярка по id; пусто — все).</summary>
    public string? ModelInclude { get; set; }
    /// <summary>Какие модели из /models прятать (озвучка, картинки, эмбеддинги — не для болтовни).</summary>
    public string? ModelExclude { get; set; }
    /// <summary>
    /// Быстрая модель для простой болтовни (без инструментов и снимка экрана). Думающая «high» отвечает 10–15 с —
    /// в живом разговоре это молчание; она остаётся для задач. null — всегда Model.
    /// </summary>
    public string? ChatModel { get; set; }
    /// <summary>Как выходить в сеть: auto (свои адреса напрямую, облако — через найденный прокси), direct, proxy.</summary>
    public string? Proxy { get; set; }

    [JsonIgnore] public bool IsLocal => Kind is "own" or "flashnext";

    /// <summary>
    /// Свой сервер моделей на этой машине с OpenAI-совместимым API (STRATA, Ollama, LM Studio) — не облако и не прокси
    /// AG Tools. Такие тяжёлые (STRATA ~62 ГБ ОЗУ): вместе со своим мозгом не влезают, сами в запасные не поднимаются.
    /// </summary>
    [JsonIgnore] public bool IsLocalServer => Kind == "openai" && Key != "antigravity-tools"
                                               && Uri.TryCreate(Url, UriKind.Absolute, out var u) && u.IsLoopback;

    /// <summary>Имя для меню и пузыря: у мозгов со списком моделей — вместе с выбранной моделью.</summary>
    [JsonIgnore] public string DisplayName => ModelsFromServer && Model.Length > 0 ? $"{Name}: {Model}" : Name;

    private const string AntigravityTools = @"%LOCALAPPDATA%\Antigravity Tools\antigravity-tools.exe";

    /// <summary>Набор по умолчанию: что реально есть на этой машине (ключи в окружении и ~/.dsh/.env, AG Tools, Claude Code).</summary>
    public static List<BrainProfile> Defaults() =>
    [
        new()
        {
            Id = "antigravity", Name = "Antigravity — AG Tools (Claude / Gemini / GPT)", Kind = "openai",
            // модель — та, что есть почти на всех аккаунтах: одинокую модель (есть на одном акке) circuit breaker AG
            // запирает вместе с аккаунтом. На лимите Клава сама пересаживается на соседнюю живую модель (BrainGuard).
            Url = "http://127.0.0.1:8045/v1", Model = "claude-sonnet-4-6", Key = "antigravity-tools",
            Vision = true, MaxTokens = 4000, Launch = AntigravityTools, ModelsFromServer = true,
            // AG Tools отдаёт и картиночные модели, и служебные — в меню их не надо
            ModelExclude = @"image|internal|agent|tiered|x-flash|^gpt-(3|4)|^claude-3|^claude$|\d{8}|gemini-2\.0",
            Aliases = ["антигравит", "гравит", "antigravity", "аг тулз"],
            Fallback = "deepseek", FallbackAfterMs = 6000,
        },
        new()
        {
            Id = "deepseek", Name = "Дипсик — DeepSeek V4.1 Flash", Kind = "openai",
            Url = "https://api.deepseek.com", Model = "deepseek-flash", Key = "env:DEEPSEEK_API_KEY",
            Vision = true, MaxTokens = 800,
            Extra = new JsonObject { ["thinking"] = new JsonObject { ["type"] = "disabled" } },
            Aliases = ["дипсик", "дип сик", "deepseek", "дипсек"], Fallback = "antigravity",
        },
        new()
        {
            Id = "gemini", Name = "Жминя (ключ API)", Kind = "openai",
            Url = "https://generativelanguage.googleapis.com/v1beta/openai", Model = "gemini-3.5-flash",
            Key = "env:GEMINI_API_KEY", Vision = true, MaxTokens = 4000, ModelsFromServer = true,
            ModelInclude = "^gemini-", ModelExclude = "tts|image|embed|live|audio|transcribe|omni|robotics|computer|customtools",
            Extra = new JsonObject { ["reasoning_effort"] = "minimal" },
            Aliases = ["жмин", "гемин", "джемин", "gemini", "гугл"], Fallback = "deepseek",
        },
        new()
        {
            Id = "codex", Name = "ChatGPT — Codex (подписка)", Kind = "codex", Model = "", MaxTokens = 800,
            Vision = true, Tools = false, Extra = new JsonObject { ["reasoning_effort"] = "low" },
            ModelsFromServer = true,   // подменю моделей: список у самого Codex (CodexCli.Models)
            Aliases = ["кодекс", "codex", "чатгпт", "чат гпт", "джипити"], Fallback = "antigravity",
        },
        new()
        {
            Id = "claude", Name = "Claude — Claude Code (подписка)", Kind = "claude-code", Model = "sonnet",
            Vision = true, Tools = false, Aliases = ["клод", "claude", "клауд", "клоду"], Fallback = "antigravity",
        },
        new()
        {
            Id = "local", Name = "Локалка — Qwen3.6-35B-A3B uncensored", Kind = "own", MaxTokens = 1500, Vision = true,
            Extra = new JsonObject { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false } },
            Aliases = ["локал", "квен", "свой мозг", "свои мозги"], Fallback = "antigravity", FallbackAfterMs = null,
        },
    ];
}

/// <summary>Настройки Клавы из klava.json (лежит в корне Neko; KlavaDesk.exe — в Neko\bin).</summary>
internal sealed class KlavaConfig
{
    /// <summary>Адрес своего llama-server (мозги own и flashnext живут на нём по очереди).</summary>
    public string LlmUrl { get; set; } = "http://127.0.0.1:8090";
    public string LlmModel { get; set; } = "flashnext";
    /// <summary>Температура своих мозгов (облачным — их собственная, если в профиле не задана).</summary>
    public double LlmTemperature { get; set; } = 0.7;
    /// <summary>
    /// Какой мозг сейчас думает за Клаву — id из brains. По умолчанию DeepSeek: на замерах самый ровный
    /// (первая фраза 0,8 с текстом и 1,5 с со снимком экрана), Gemini на бесплатном ключе скачет до 40 с.
    /// </summary>
    public string Brain { get; set; } = "deepseek";
    public List<BrainProfile> Brains { get; set; } = BrainProfile.Defaults();
    /// <summary>Сколько ждать первую фразу от мозга, прежде чем спросить ещё и запасной (0 — не страховать).</summary>
    public int FallbackAfterMs { get; set; } = 4000;
    /// <summary>Сколько фраз она говорит за раз в болтовне — остальное обрывается. Слишком мало — «затыкается посередине мысли», слишком много — разливается.</summary>
    public int MaxPhrases { get; set; } = 3;
    /// <summary>Сколько фраз можно, если друг просит рассказать, объяснить, спеть.</summary>
    public int MaxPhrasesLong { get; set; } = 8;
    /// <summary>Поднимать мозг самой, если он не запущен (одна кнопка «запустить» — и всё едет).</summary>
    public bool StartBrain { get; set; } = true;
    /// <summary>llama-server: свой путь или runtime\llama (туда кладёт tools\setup.ps1 сборку llama.cpp под эту машину).</summary>
    public string BrainExe { get; set; } = @"runtime\llama\llama-server.exe";
    public string BrainModel { get; set; } = @"models\brain\qwen36\Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-Q6_K_P.gguf";
    /// <summary>Зрение (проектор картинок). Держим в ОЗУ: на видеокарте рядом с голосом и слухом ему нет места.</summary>
    public string BrainMmproj { get; set; } = @"models\brain\qwen36\mmproj-Qwen3.6-35B-A3B-Uncensored-HauhauCS-Aggressive-f16.gguf";
    /// <summary>У Qwen3.6 внимание полное лишь в каждом четвёртом слое — 32К контекста стоят меньше гигабайта.</summary>
    public int BrainCtx { get; set; } = 32768;
    /// <summary>
    /// Потоков процессора мозгу на генерацию; 0 — физические ядра минус два (системе, окну Клавы, звуку и whisper
    /// нужно хоть что-то: на всех 8 из 8 ядер система «задыхалась»). На промпт (-tb) — все физические ядра.
    /// </summary>
    public int BrainThreads { get; set; }
    /// <summary>
    /// Куски промпта от стольких токенов считает видеокарта: веса экспертов из закреплённой ОЗУ летят к ней по DMA;
    /// меньше — процессор (перекачка 23 ГБ ради пары слов дольше самого счёта). Замер 27.09 на промпте 6155 токенов:
    /// процессор 77 т/с (84 с), видеокарта из закреплённой ОЗУ 580 т/с (14,6 с). 0 — не трогать (llama.cpp: 32).
    /// </summary>
    public int BrainOffloadMinBatch { get; set; } = 128;
    /// <summary>
    /// Остальные аргументы llama-server. --fit сам решает, сколько слоёв и экспертов положить на видеокарту, оставляя
    /// --fit-target МБ запаса под голос и слух. --load-mode none — модель целиком в закреплённой ОЗУ (не отображение
    /// файла: Windows не выгружает экспертов на диск посреди ответа, видеокарта забирает их по DMA на полной скорости
    /// PCIe). --poll 0 — потоки спят, пока работает видеокарта (по умолчанию крутились вхолостую на 100 %),
    /// --prio -1 — ниже обычного: система и окна не тормозят. Сэмплинг — рецепт Qwen3.6 без размышлений.
    /// </summary>
    public List<string> BrainArgs { get; set; } = [.. DefaultBrainArgs];

    public static readonly string[] DefaultBrainArgs =
    [
        "--fit", "on", "--load-mode", "none", "-b", "2048", "-ub", "2048", "--poll", "0", "--prio", "-1",
        "-ctk", "q8_0", "-ctv", "q8_0", "-fa", "on",
        "--temp", "0.7", "--top-p", "0.8", "--top-k", "20", "--min-p", "0", "--presence-penalty", "1.5",
    ];

    /// <summary>
    /// Старый klava.json: процессор считал весь промпт (--no-op-offload), модель отображалась с диска, потоки крутились
    /// вхолостую. Переписать на раскладку из замера 27.09, сэмплинг и прочие свои флаги оставить.
    /// </summary>
    private bool MigrateBrainArgs()
    {
        if (!BrainArgs.Contains("--no-op-offload") || BrainArgs.Contains("--load-mode")) return false;
        var args = BrainArgs.Where(a => a != "--no-op-offload").ToList();
        void Ensure(string flag, string value)
        {
            if (args.Contains(flag)) return;
            var at = Math.Min(2, args.Count);
            args.InsertRange(at, [flag, value]);
        }
        Ensure("--prio", "-1");
        Ensure("--poll", "0");
        Ensure("-ub", "2048");
        Ensure("-b", "2048");
        Ensure("--load-mode", "none");
        BrainArgs = args;
        return true;
    }
    /// <summary>Сколько видеопамяти (МБ) --fit оставляет свободной, когда голос и слух уже работают.</summary>
    public int BrainFitTargetMiB { get; set; } = 1536;
    /// <summary>Сколько видеопамяти (МБ) займут голос и слух большого комплекта, если мозг поднимается раньше них
    /// (замер: Qwen3-TTS 1.7B с адаптером ударений ~4,5 ГБ пиком вместе с whisper; на старой 0.6B было ~2,2 ГБ).</summary>
    public int VoiceVramMiB { get; set; } = 4600;
    /// <summary>Сколько токенов максимум на снимок экрана (больше — точнее, но дольше считает).</summary>
    public int BrainImageTokens { get; set; } = 768;
    public string BrainScript { get; set; } = "";
    /// <summary>Режим START_FLASHNEXT.ps1 (для brainKind = flashnext): klava — 16К, освобождает видеопамять под голос.</summary>
    public string BrainMode { get; set; } = "klava";
    /// <summary>Отдавать мозгу снимок экрана картинкой (только если мозг умеет видеть — brainKind = own).</summary>
    public bool SendScreenImage { get; set; } = true;

    public string TtsUrl { get; set; } = "http://127.0.0.1:8095";

    /// <summary>
    /// Голос потоком (tts/server.py /tts_stream): звук идёт кусками по мере синтеза, первый — через ~0,5 с, а не после
    /// всей фразы (друг 29.09: «убрать задержку перед ответом»). false — как раньше, фраза целиком.
    /// </summary>
    public bool TtsStream { get; set; } = true;

    /// <summary>
    /// Векторизатор памяти (MemoryGraph): bge-m3 GGUF в своём llama-server на процессоре. Нет файла — граф ищет по словам.
    /// </summary>
    public string EmbedModel { get; set; } = @"models\embed\bge-m3-q8_0.gguf";
    public int EmbedPort { get; set; } = 8094;
    public int EmbedThreads { get; set; } = 4;
    /// <summary>Сколько ждать вектор реплики для подмеса памяти (мс): не успел — память по одним сущностям.</summary>
    public int EmbedBudgetMs { get; set; } = 350;
    public string TtsPython { get; set; } = @"tts\.venv\Scripts\python.exe";
    public string TtsScript { get; set; } = @"tts\server.py";
    /// <summary>Музыкальный слух и пение (music\klava_music.py на окружении голоса): поднимается сам, когда нужна музыка,
    /// и сам выходит после 30 минут простоя.</summary>
    public string MusicUrl { get; set; } = "http://127.0.0.1:8099";
    public string MusicScript { get; set; } = @"music\klava_music.py";
    /// <summary>SoundFont для «похожей основы» (MIDI → звук без сети): GeneralUser GS, tools\fetch_models.ps1.</summary>
    public string MusicSoundFont { get; set; } = @"models\music\GeneralUser-GS.sf2";
    /// <summary>Сколько секунд слушать звук компьютера на «послушай, что играет» (10–120).</summary>
    public int MusicListenSec { get; set; } = 30;
    /// <summary>Чем петь русский кавер: ace — живым вокалом ACE-Step 1.5 и перекраской в её тембр (toolsce-step,
    /// setup_full.ps1); world — прежний путь (её речь по нотам, звучит отрывисто). Нет ACE — сам откатится на world.</summary>
    public string MusicCoverEngine { get; set; } = "ace";
    /// <summary>Мягкий коврик под лапками (web/props.js, её просьба 30.09).</summary>
    public bool Rug { get; set; } = true;

    public string SttUrl { get; set; } = "http://127.0.0.1:8097";
    /// <summary>whisper-server большого комплекта: свой путь или runtime\whisper (tools\setup_full.ps1).</summary>
    public string SttExe { get; set; } = @"runtime\whisper\whisper-server.exe";
    /// <summary>Основная модель слуха — на видеокарте (large-v3-turbo: точный русский, фраза за доли секунды).</summary>
    public string SttModel { get; set; } = @"models\whisper\ggml-large-v3-turbo-q5_0.bin";
    public bool SttGpu { get; set; } = true;
    /// <summary>Запасная модель на процессоре, если на видеокарте не хватило памяти.</summary>
    public string SttFallbackModel { get; set; } = @"models\whisper\ggml-small-q5_1.bin";
    public int SttThreads { get; set; } = 8;
    /// <summary>Лучевой поиск whisper на процессоре: 1 — жадно и быстро, 3 — заметно меньше каши в русской речи.</summary>
    public int SttBeam { get; set; } = 3;
    /// <summary>Лучевой поиск на видеокарте — там он почти бесплатный.</summary>
    public int SttBeamGpu { get; set; } = 5;
    /// <summary>
    /// Подсказка распознаванию: стиль и слова, которые звучат в разговоре. Чем больше сленга и междометий тут перечислено,
    /// тем меньше шанс, что whisper подменит «пук» на «пок» или «ща» на «щас». Часто ошибающиеся слова стоит держать здесь.
    /// </summary>
    public string SttPrompt { get; set; } =
        "Живой разговор двух пацанов по-русски, неформально, с матом и сленгом. " +
        "Клава — кошкодевочка на рабочем столе. Мур, ня, ща, чё, нах, бля, блядь, ебать, ебал, ебало, охуеть, охренеть, " +
        "заебись, зашибись, пиздец, пиздато, пиздабол, залупа, хуйня, хуёво, нихуя, ниибёт, пофиг, забей, лол, кек, чёрт, " +
        "пивас, пивко, киберпиво, пацан, пацанский, братан, братуха, чувак, тян, вайб, вайбовый, реально, серьёзно, " +
        "пук, пук-пук, ага, угу, окей, го, погнали, ща, щас, чёт, чёто, нифига, зашквар, кринж, флэшнэкст, дипсик, " +
        "жминя, гемини, антигравити, клод, квэн, тэтээс, эстэтэ, куда, вирам, эйай.";

    /// <summary>Клава краем глаза видит активное окно и иногда комментирует, что ты делаешь.</summary>
    public bool WatchScreen { get; set; } = true;
    /// <summary>Не чаще, чем раз в столько минут, она сама заговорит про то, что увидела.</summary>
    public int WatchCommentMinutes { get; set; } = 6;
    /// <summary>Город друга для погоды (запоминается, когда он его называет).</summary>
    public string HomeCity { get; set; } = "";
    /// <summary>«Наблюдай» (журнал экрана): как часто глядеть на экран, с.</summary>
    public int WatchIntervalSec { get; set; } = 30;
    /// <summary>Насколько должен измениться кадр (среднее по отпечатку 32×18, 0..255), чтобы это стало записью.</summary>
    public double WatchChangeThreshold { get; set; } = 6;
    /// <summary>Даже если меняется по чуть-чуть — запись не реже, чем раз в столько минут.</summary>
    public int WatchHeartbeatMinutes { get; set; } = 10;
    /// <summary>Не чаще, чем раз в столько секунд, снимок уходит мозгу с глазами на описание (это облачный запрос).</summary>
    public int WatchDescribeSec { get; set; } = 90;
    /// <summary>Описывать снимки мозгом с глазами; false — только окно и распознанный текст (всё локально).</summary>
    public bool WatchVision { get; set; } = true;
    /// <summary>Знакомая игра впереди (Бакшот, Detroit, DOOM 3) — сама начинает смотреть её вместе с другом (GameWatch).</summary>
    public bool GameWatchAuto { get; set; } = true;
    /// <summary>Темп глаз в игре: 1 — как задано для игры, 2 — вдвое реже (меньше облачных запросов), 0.5 — вдвое чаще.</summary>
    public double GameWatchPace { get; set; } = 1.0;
    /// <summary>
    /// Живость: сама заводит разговор, если столько минут тихо, а друг за компом (реальный интервал случайный ±40 %;
    /// 0 — не заводить). Проигнорировал две её реплики подряд — молчит, пока он сам не заговорит.
    /// </summary>
    public int ChatterMinutes { get; set; } = 15;
    /// <summary>Замечать, что друг вернулся после отлучки, ворчать ночью «иди спать» и звать размяться после двух часов за компом.</summary>
    public bool LifeEvents { get; set; } = true;
    /// <summary>Сколько символов распознанного с экрана текста отдавать мозгу (контекст 16К).</summary>
    public int ScreenTextMaxChars { get; set; } = 1800;

    public string WebRoot { get; set; } = "web";
    public string PersonaFile { get; set; } = "persona.txt";
    /// <summary>Правила для инструментов — добавляются к характеру, только когда друг просит что-то сделать.</summary>
    public string PersonaToolsFile { get; set; } = "persona_tools.txt";
    public string HistoryFile { get; set; } = @"data\history.json";
    public int HistoryMaxChars { get; set; } = 8000;
    /// <summary>
    /// Режим мозгов (для всех): auto — инструменты по смыслу реплики («открой», «найди», продолжение задачи);
    /// chat — «💬 Общение»: только разговор, без инструментов, быстрая модель; work — «🛠 Работа»: инструменты всегда.
    /// </summary>
    public string WorkMode { get; set; } = "auto";

    /// <summary>Клавиша «зажми и говори», виртуальный код Windows. 0xA3 — правый Ctrl.</summary>
    public int PushToTalkVk { get; set; } = 0xA3;
    /// <summary>Слушать без кнопки: детектор голоса сам ловит начало и конец фразы.</summary>
    public bool HandsFree { get; set; } = true;
    /// <summary>Номер микрофона WinMM; -1 — системный по умолчанию через преобразователь форматов.</summary>
    public int MicDevice { get; set; } = -1;
    /// <summary>Программный gain для микрофона (умножает PCM). 1.0 — как есть, 2.0 — вдвое громче, 4.0 — очень далеко/тихий микрофон. Клипинг ограничен.</summary>
    public double MicGain { get; set; } = 1.0;
    /// <summary>Минимальная громкость речи (RMS 0..1) для детектора голоса. Меньше — слышит далеко/шёпот; больше — только явную речь.</summary>
    public double VadMinLevel { get; set; } = 0.008;
    /// <summary>Сколько тишины (мс) считается концом фразы. Меньше — Клава отвечает быстрее, но чаще режет паузы в середине; больше — меньше «двоения», но она чуть медленнее реагирует.</summary>
    public int VadSilenceMs { get; set; } = 750;
    /// <summary>Сколько миллисекунд голоса подряд — старт фразы (меньше — быстрее реагирует, но чувствительнее к кашлю).</summary>
    public int VadStartMs { get; set; } = 100;
    /// <summary>Столько миллисекунд громкого голоса подряд перебивают её речь. Меньше — легче встрять, больше — реже ложные срабатывания на её же эхо из колонок.</summary>
    public int VadInterruptStartMs { get; set; } = 120;
    /// <summary>Абсолютный порог перебивания: чтобы шёпот друга не мог, а нормальная речь — да. Слишком низкий — Клава сама себя прерывает от эха.</summary>
    public double VadInterruptLevel { get; set; } = 0.028;
    /// <summary>Горячая клавиша поля ввода текста: Ctrl+Alt+эта буква.</summary>
    public char TextHotkeyLetter { get; set; } = 'M';

    public int WindowWidth { get; set; } = 440;
    public int WindowHeight { get; set; } = 900;
    /// <summary>Размер Клавы на экране: множитель окна (0.4 … 1.6). Ctrl+колесо или «Настройки вида».</summary>
    public double WindowScale { get; set; } = 1.0;
    /// <summary>Поворот модели, градусы (−180 … 180; 0 — лицом к тебе). Shift+колесо.</summary>
    public double ModelYaw { get; set; }
    /// <summary>Приближение камеры: 0 — в полный рост, 1 — лицо крупно. Колесо.</summary>
    public double CameraZoom { get; set; }
    public bool Muted { get; set; }
    /// <summary>Суфлёр: текст её реплик облачком у аватара (друг 28.09 — можно спрятать). Без звука показывается всегда.</summary>
    public bool Subtitles { get; set; } = true;
    public bool DevTools { get; set; }

    /// <summary>
    /// Автопилот: безопасные действия Клавы (посмотреть папку, прочитать/записать файл в разрешённой зоне,
    /// открыть программу, шевельнуть мышью после первого разрешения, положить хлам в Корзину) выполняются
    /// без спроса. Опасные (системные пути, форматирование, реестр, службы, скачивание exe, sudo/UAC)
    /// ВСЕГДА всплывают окном подтверждения. Первый ввод в живой терминал и первый жест лапками — тоже
    /// с подтверждением, а дальше в этом разговоре без вопросов, как и было.
    /// </summary>
    public bool Autopilot { get; set; } = true;

    /// <summary>
    /// Режим общения Клавы (PersonaModes): default (как в характере) | mat (с матом) | clean (без мата) |
    /// moderate (умеренная) | thoughtful (рассудительная); старые polite/rage = clean/mat. К persona.txt
    /// подмешивается накладка persona_*.txt, в каждую реплику — напоминание, в чистых режимах речь фильтруется.
    /// </summary>
    public string PersonaMode { get; set; } = "default";

    /// <summary>
    /// Что на ней надето (web/wardrobe.js): kimono | gothic | cyber | classic | native. Переодевается сама — меткой
    /// [наряд …] в речи, или друг из меню «👗 Гардероб»; страница собирается заново с ?outfit=имя.
    /// </summary>
    public string Outfit { get; set; } = "kimono";

    /// <summary>
    /// Держаться поверх любых полноэкранных игр и видео (друг: «поверх всех давай, чё ей смотреть нельзя?»). Выключить —
    /// тогда только поверх тех, где она участвует: CS, Зума, тетрис.
    /// </summary>
    public bool OverFullscreen { get; set; } = true;

    /// <summary>
    /// Режим игростримера: Клава держится поверх игр (CS2, CS:GO, Point Blank и др.), Z-order держится непрерывно.
    /// </summary>
    public bool GameStreamerMode { get; set; } = true;

    /// <summary>
    /// Сквозной проклик мыши, пока впереди игра: клики пролетают насквозь через кошку прямо в игру (WS_EX_TRANSPARENT).
    /// На столе, в браузере и прочих окнах кошку можно трогать как обычно.
    /// </summary>
    public bool GameClickThrough { get; set; } = true;

    /// <summary>Папка Counter-Strike 1.6 (hl.exe, cstrike: журнал сервера в logs, команды — klava_cmd.cfg по F11).</summary>
    public string CsDir { get; set; } = @"F:\Games\Counter-Strike 1.6 RAZER";

    /// <summary>
    /// Фоновый агент (AgentRunner): профиль мозга и модель для настоящей работы на компьютере. По умолчанию — AG Tools
    /// и Claude Sonnet (инструменты вызывает чисто, ~2 с на шаг), запасная модель — там же, дальше запасной мозг профиля.
    /// </summary>
    public string AgentBrain { get; set; } = "antigravity";
    public string AgentModel { get; set; } = "claude-sonnet-4-6";
    public string AgentFallbackModel { get; set; } = "gemini-3.8-flash-high";
    /// <summary>
    /// Запасные модели агентов по порядку (после agentModel и agentFallbackModel): модель упёрлась в лимит или не
    /// ответила — агент сам пересаживается на следующую и продолжает. Имена — как у шлюза (AG Tools), можно неточно:
    /// сверяются со списком его моделей.
    /// </summary>
    public List<string> AgentModels { get; set; } = ["gemini-3.1-pro-high", "gemini-3.8-flash-high", "claude-opus-4-6-thinking", "claude-sonnet-4-6"];
    /// <summary>Сколько шагов (вызовов модели) и минут агенту на одну задачу.</summary>
    public int AgentMaxRounds { get; set; } = 120;
    public int AgentMaxMinutes { get; set; } = 45;

    /// <summary>
    /// На чём агенты (AgentHub, 30.09): auto/claude — Claude Code (подписка, если CLI залогинен, иначе Anthropic-вход
    /// AG Tools профиля AgentBrain), ag — всегда через AG Tools, own — прежний самодельный агент (AgentRunner).
    /// </summary>
    public string AgentEngine { get; set; } = "auto";
    /// <summary>Модель Claude Code по подписке (псевдоним CLI: sonnet, opus, fable…).</summary>
    public string AgentClaudeModel { get; set; } = "sonnet";
    /// <summary>Anthropic-адрес прокси для Claude Code; пусто — адрес профиля AgentBrain без /v1.</summary>
    public string AgentAnthropicUrl { get; set; } = "";
    /// <summary>Сколько агентов одновременно и через сколько минут простоя законченная сессия закрывается.</summary>
    public int AgentMaxParallel { get; set; } = 4;
    public int AgentIdleMinutes { get; set; } = 30;
    /// <summary>Какие из установленных плагинов Claude Code давать агентам (дев-набор; ralph-loop и hookify — нет: зацикливают и правят хуки).</summary>
    public List<string> AgentPlugins { get; set; } =
    [
        "code-review", "code-simplifier", "commit-commands", "feature-dev", "pr-review-toolkit", "superpowers", "security-guidance",
        "skill-creator", "claude-md-management", "csharp-lsp", "typescript-lsp", "pyright-lsp", "frontend-design",
        "desktop-commander", "modern-web-guidance", "engineering", "unity", "playground",
    ];

    /// <summary>
    /// Пока Клава говорит, микрофон глухой полностью (её эхо из колонок не долетает до whisper).
    /// Перебивать голосом в этом режиме нельзя — только PTT (правый Ctrl). Без наушников включай, с наушниками выключай.
    /// </summary>
    public bool EchoBlock { get; set; } = false;

    /// <summary>Сервер голосовых профилей (speaker_id.py, resemblyzer) — узнаёт кто из друзей говорит (Лис/Сова/Кот/…).</summary>
    public string SpeakerIdUrl { get; set; } = "http://127.0.0.1:8098";
    /// <summary>Автозапуск speaker_id при старте Клавы.</summary>
    public bool SpeakerIdEnabled { get; set; } = false;
    /// <summary>Скрипт speaker_id (относительно корня проекта).</summary>
    public string SpeakerIdScript { get; set; } = @"tts\speaker_id.py";

    /// <summary>Сколько человек в комнате (1 — соло, до <see cref="MaxRoomPeople"/>). Друг 28.09: пятый человек не влезал —
    /// во всех окнах выбор был до 4.</summary>
    public int RoomPeopleCount { get; set; } = 1;

    /// <summary>Больше скольких людей в компании не выбрать (меню, панель, настройки звука).</summary>
    public const int MaxRoomPeople = 8;

    /// <summary>Сколько реплик максимум ждать во временном буфере перед ответом Клавы (по умолчанию равно RoomPeopleCount).</summary>
    public int DialogBatchMaxPhrases { get; set; } = 1;

    /// <summary>Таймаут затишья (в секундах) после последней реплики: если никто больше не говорит, отвечать сразу.</summary>
    public double DialogSilenceTimeoutSec { get; set; } = 1.2;

    /// <summary>
    /// Кто в компании (имена голосовых профилей). Соло — один человек: Клава слышит и отвечает только ему,
    /// телевизор и чужие голоса пишутся в журнал фоном, но в разговор не идут. Двое, трое — только эти люди.
    /// Пусто — слушает всех, как раньше.
    /// </summary>
    public List<string> RoomMembers { get; set; } = [];

    /// <summary>
    /// Порог «это свой» для голосового профиля (косинусная близость). null — берётся рекомендованный сервером
    /// для его модели (WavLM ≈ 0,80, resemblyzer ≈ 0,75).
    /// </summary>
    public double? SpeakerAcceptSim { get; set; }
    /// <summary>Без компании имя назначается, только если лучший кандидат оторван от второго не меньше чем на столько (косинус).</summary>
    public double SpeakerFreeMargin { get; set; } = 0.07;
    /// <summary>Прибавка к порогу «свой» для свободного режима без компании (жёстче, чтоб левых не присваивать).</summary>
    public double SpeakerFreeExtra { get; set; } = 0.04;

    /// <summary>Имя микрофона (по нему находим номер устройства, даже если номера сдвинулись после переподключения USB).</summary>
    public string? MicDeviceName { get; set; }

    /// <summary>Прокси для облака: auto — найти самой (переменные, системный, порты Karing/Clash/v2ray), none — без прокси, либо адрес.</summary>
    public string NetProxy { get; set; } = "auto";

    /// <summary>Реплика с инструментами: если мозг не начал отвечать за столько мс — вопрос уходит запасному (последовательно, не наперегонки).</summary>
    public int ToolTurnFallbackMs { get; set; } = 60000;

    /// <summary>Задача с инструментами дольше стольких секунд — инструменты забираются, она честно отчитывается словами (не залипает по 4 минуты).</summary>
    public int ToolTurnBudgetSec { get; set; } = 150;

    /// <summary>Поток ответа молчит дольше — считаем соединение зависшим и обрываем (вместо вечного «думаю…»).</summary>
    public int StreamIdleMs { get; set; } = 60000;

    /// <summary>
    /// Слух и голос. auto — большой комплект (whisper на видеокарте, Qwen3-TTS, WavLM на Python), если он установлен,
    /// иначе лёгкий; full — только большой; lite — только лёгкий: sherpa-onnx на процессоре, без Python и видеокарты
    /// (GigaAM v3, Supertonic-3, ERes2Net), идёт на любом ноутбуке.
    /// </summary>
    public string SpeechBackend { get; set; } = "auto";
    /// <summary>Папка моделей лёгкого комплекта (качает tools\fetch_lite.ps1).</summary>
    public string LiteDir { get; set; } = @"models\lite";
    /// <summary>Потоков процессора на модель лёгкого комплекта; 0 — половина ядер, не больше 4.</summary>
    public int LiteThreads { get; set; }
    /// <summary>Голос Supertonic: 0–4 женские F1–F5, 5–9 мужские M1–M5. F2 на высоте 1,06 ближе всех к её большому голосу (--lite-test).</summary>
    public int LiteVoiceId { get; set; } = 1;
    public double LiteVoiceSpeed { get; set; } = 1.05;
    /// <summary>Шагов Supertonic: больше — чище звук, дольше синтез (5 — слабый ноутбук, 8 — норма, 12 — качество).</summary>
    public int LiteVoiceSteps { get; set; } = 8;
    /// <summary>Высота лёгкого голоса без смены темпа: 1 — как есть, 1,08 — моложе и «кошачьее».</summary>
    public double LiteVoicePitch { get; set; } = 1.06;
    /// <summary>Порог «это свой» для лёгких профилей ERes2Net (у неё близости ниже, чем у WavLM).</summary>
    public double LiteSpeakerThreshold { get; set; } = 0.45;
    /// <summary>
    /// Кто считает голосовые профили: auto — в процессе на модели sherpa-onnx (speakerModel), если она есть, иначе
    /// speaker_id.py с WavLM; wavlm — всегда питоновский сервер. Замер 26.09 на записях друзей: TitaNet разводит «своих»
    /// и «чужих» с запасом 0,36, WavLM — 0,10 (путал Кибер_Кота с Настей).
    /// </summary>
    public string SpeakerBackend { get; set; } = "auto";
    /// <summary>
    /// Модель отпечатков голоса для профилей в процессе. CAM++ — по замеру --speakers-bench 26.09 на записях друзей
    /// меньше всех ошибается (EER 9,9 % против 12,3 % у TitaNet) и быстрее всех (20–35 мс на фразу).
    /// </summary>
    public string SpeakerModel { get; set; } = @"models\speaker\sherpa\3dspeaker_speech_campplus_sv_zh_en_16k-common_advanced.onnx";
    /// <summary>Порог «тот же человек» руками; 0 — подобранный тренировкой слуха (до неё — замеренный для модели).</summary>
    public double SpeakerThreshold { get; set; }
    /// <summary>Большой голос или слух упал — на это время подменять лёгким (если он скачан), чтобы Клава не немела и не глохла.</summary>
    public bool LiteFallback { get; set; } = true;

    /// <summary>Итог speechBackend: работает лёгкий комплект.</summary>
    [JsonIgnore] public bool LiteSpeech { get; private set; }

    /// <summary>Большой комплект на месте: Python-окружение с Qwen3-TTS и whisper-server с моделью.</summary>
    public bool FullSpeechInstalled() =>
        File.Exists(Abs(TtsPython)) && File.Exists(Abs(TtsScript)) && (Directory.Exists(Abs(@"models\Qwen3-TTS-12Hz-1.7B-Base-ru-stress")) || Directory.Exists(Abs(@"models\Qwen3-TTS-12Hz-0.6B-Base"))) &&
        File.Exists(Abs(SttExe)) && (File.Exists(Abs(SttModel)) || File.Exists(Abs(SttFallbackModel)));

    [JsonIgnore] public string Root { get; private set; } = "";

    /// <summary>Настройки по умолчанию с корнем в своей папке — для проверок (--selftest), чтобы не трогать data\ друга.</summary>
    internal static KlavaConfig ForRoot(string root) => new() { Root = root };

    /// <summary>Мозг, который сейчас думает (если id в конфиге битый — первый из списка).</summary>
    [JsonIgnore] public BrainProfile ActiveBrain => Brains.FirstOrDefault(b => b.Id == Brain) ?? Brains[0];

    /// <summary>Адрес OpenAI-совместимого API мозга (у своих — llmUrl/v1).</summary>
    public string ApiUrl(BrainProfile p) => (p.IsLocal ? LlmUrl.TrimEnd('/') + "/v1" : p.Url).TrimEnd('/');

    public string Abs(string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(Root, path));

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        // кириллица в klava.json — буквами, а не ж: файл правят руками
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Корень — папка над bin с KlavaDesk.exe; можно переопределить переменной KLAVA_HOME.</summary>
    public static KlavaConfig Load()
    {
        var root = Environment.GetEnvironmentVariable("KLAVA_HOME");
        if (string.IsNullOrWhiteSpace(root))
        {
            var cur = new DirectoryInfo(AppContext.BaseDirectory);
            while (cur != null)
            {
                if (File.Exists(Path.Combine(cur.FullName, "klava.json")))
                {
                    root = cur.FullName;
                    break;
                }
                cur = cur.Parent;
            }
            if (string.IsNullOrWhiteSpace(root))
                root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        }
        var file = Path.Combine(root, "klava.json");
        var cfg = File.Exists(file)
            ? JsonSerializer.Deserialize<KlavaConfig>(File.ReadAllText(file), Json) ?? new KlavaConfig()
            : new KlavaConfig();
        cfg.Root = root;
        cfg.LiteSpeech = cfg.SpeechBackend.Trim().ToLowerInvariant() switch
        {
            "lite" => true,
            "full" => false,
            _ => !cfg.FullSpeechInstalled(),
        };
        if (cfg.Brains.Count == 0) cfg.Brains = BrainProfile.Defaults();
        global::KlavaDesk.NetProxy.Configured = cfg.NetProxy;
        // AG Tools: у старых конфигов нет быстрой модели для болтовни, а запасной ждал 15 с — дольше, чем думает high
        if (cfg.Brains.FirstOrDefault(b => b.Id == "antigravity") is { } ag && ag.ChatModel is null && ag.Model.EndsWith("-high", StringComparison.OrdinalIgnoreCase))
        {
            ag.ChatModel = ag.Model[..^5] + "-low";
            if (ag.FallbackAfterMs is null or > 8000) ag.FallbackAfterMs = 6000;
        }
        // старый конфиг без списка мозгов — записать список, чтобы его было видно и можно было править
        var migrated = cfg.MigrateBrainArgs();
        // whisper считает на видеокарте: 8 потоков процессора ему не нужны, а мозгу и системе мешали
        if (cfg.SttGpu && cfg.SttThreads > 4) { cfg.SttThreads = 4; migrated = true; }
        // ChatGPT через Codex: подменю моделей всегда (список локальный, у самого Codex) — профиль 18.11 был без него
        foreach (var cx in cfg.Brains.Where(b => b.Kind == "codex" && !b.ModelsFromServer)) { cx.ModelsFromServer = true; migrated = true; }
        // STRATA (свой движок Strata: Qwen3.8-Flash-Next, 512 экспертов, без цензуры) — мозгом, если её профиль на диске
        if (cfg.Brains.All(b => b.Id != "strata") && StrataBrain() is { } strata) { cfg.Brains.Add(strata); migrated = true; }
        if (migrated || !File.Exists(file) || !File.ReadAllText(file).Contains("\"brains\"")) cfg.Save();
        return cfg;
    }

    /// <summary>
    /// Профиль движка Strata (profile.json рядом с моделью: exe …\strata.exe, port, model_name) → мозг «STRATA».
    /// Где ищем: KLAVA_STRATA_PROFILE, папки моделей G:\Neiro\models\*, H:\Neiro\Strata-data\*. Запуск — START_STRATA_*.bat
    /// рядом с профилем (так его запускает и сам друг). Нет профиля — null (у друзей без STRATA ничего не появится).
    /// </summary>
    private static BrainProfile? StrataBrain()
    {
        var files = new List<string>();
        if (Environment.GetEnvironmentVariable("KLAVA_STRATA_PROFILE") is { Length: > 0 } env && File.Exists(env)) files.Add(env);
        foreach (var dir in new[] { @"G:\Neiro\models", @"H:\Neiro\Strata-data" })
        {
            try
            {
                if (Directory.Exists(dir))
                    files.AddRange(Directory.EnumerateDirectories(dir).Select(d => Path.Combine(d, "profile.json")).Where(File.Exists));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var f in files)
        {
            try
            {
                var n = JsonNode.Parse(File.ReadAllText(f));
                var exe = n?["exe"]?.GetValue<string>() ?? "";
                if (!exe.Contains("strata", StringComparison.OrdinalIgnoreCase)) continue;
                var port = n?["port"] is JsonValue pv && pv.TryGetValue<int>(out var pp) ? pp : 8093;
                var model = n?["model_name"]?.GetValue<string>() ?? "strata";
                var dir = Path.GetDirectoryName(f)!;
                var bat = Directory.EnumerateFiles(dir, "START_STRATA*.bat").FirstOrDefault();
                return new BrainProfile
                {
                    Id = "strata", Name = "STRATA — Qwen3.8-Flash-Next, 512 экспертов (свой, без цензуры)", Kind = "openai",
                    Url = $"http://127.0.0.1:{port}/v1", Model = model, Key = null, Vision = n?["vision"] is JsonObject, Tools = true,
                    MaxTokens = 1500, Launch = bat, Proxy = "direct",
                    // болтовня без размышлений (как ходит в неё MYTHOS): у Strata по умолчанию думает долго
                    Extra = new JsonObject { ["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false } },
                    Aliases = ["страт", "strata"], Fallback = "antigravity",
                };
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException) { }
        }
        return null;
    }

    private static readonly object SaveGate = new();

    /// <summary>
    /// Записать klava.json. Пишут и окно Клавы, и панель настроек (свой поток), и таймеры — под замком и через
    /// временный файл: полузаписанный конфиг при сбое питания или двух записях разом не остаётся.
    /// </summary>
    public void Save()
    {
        lock (SaveGate)
        {
            var file = Path.Combine(Root, "klava.json");
            string json;
            try { json = JsonSerializer.Serialize(this, Json); }
            catch (InvalidOperationException)   // список (RoomMembers, Brains) правили в этот миг другим потоком — ещё раз
            {
                Thread.Sleep(20);
                json = JsonSerializer.Serialize(this, Json);
            }
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, file, overwrite: true);
        }
    }
}
