using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>Вызов инструмента, как его прислала модель.</summary>
internal sealed record ToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Что показать другу в окне подтверждения. Category нужна автопилоту, чтобы решить, показывать окно или молча пропустить.</summary>
/// <param name="Category">read|write|hands|shell|open|recycle|web|shortcut|danger — безопасные пропускает автопилот, danger всегда спрашивает.</param>
internal sealed record ToolRequest(string Title, string Details, bool Dangerous, string? DangerReason, string Category = "danger");

/// <summary>Свежий снимок экрана для мозга с глазами: картинка, её область на экране и масштаб.</summary>
internal sealed record ScreenShot(byte[] Jpeg, System.Drawing.Rectangle Bounds, double Scale, string Ocr);

/// <summary>Результат инструмента: текст мозгу и, если инструмент посмотрел на экран, свежая картинка.</summary>
internal sealed record ToolResult(string Text, byte[]? Image = null);

/// <summary>Живые части, которые нужны инструментам: лапки (мышь/клава), глаза, живая оболочка и кибер-терминал.</summary>
internal sealed class ToolBox
{
    public required Hands Hands { get; init; }
    /// <summary>Сделать снимок экрана (для глаз мозга и для координат лапок).</summary>
    public required Func<Task<ScreenShot>> Capture { get; init; }
    /// <summary>Снимок области экрана (окно, приближение) в полном разрешении; null — только весь экран.</summary>
    public Func<Rectangle, Task<ScreenShot>>? CaptureRegion { get; init; }
    /// <summary>Живой pwsh на весь разговор: `cd`, переменные, венвы сохраняются между командами.</summary>
    public required Shell Shell { get; init; }
    /// <summary>Разрешение «на весь разговор кодить» уже дано — не спрашивать перед каждой командой оболочки.</summary>
    public bool ShellApproved { get; set; }
    /// <summary>Автопилот: если true, безопасные категории выполняются без окна подтверждения.</summary>
    public bool Autopilot { get; set; }
    /// <summary>Открыть встроенный кибер-терминал Клавы на экране.</summary>
    public Action? ShowTerminal { get; init; }
    /// <summary>Напечатать команду лапками в строку кибер-терминала и выполнить её там.</summary>
    public Func<string, TimeSpan, CancellationToken, Task<ShellResult>>? TypeInTerminal { get; init; }
    /// <summary>Найти текст на экране распознаванием (координаты экрана).</summary>
    public Func<string, Task<List<ScreenText>>>? FindText { get; init; }
    /// <summary>Сохранить скриншот в файл: (путь или null, все мониторы) → путь.</summary>
    public Func<string?, bool, Task<string>>? SaveScreenshot { get; init; }
    /// <summary>Рабочая папка Клавы (workspace) — туда по умолчанию относительные пути и команды.</summary>
    public string WorkDir { get; init; } = "";
    /// <summary>Можно двигать настоящую мышь и жать клавиши (в --probe нельзя: там нет цикла сообщений для хуков).</summary>
    public bool HandsAllowed { get; init; } = true;
    /// <summary>Чем она сейчас работает: keyboard (печатает, кодит — аватар достаёт ноутбук), mouse (лапка к курсору).</summary>
    public Action<string>? Activity { get; init; }
    /// <summary>Спрятать её окно из захвата экрана (true) и вернуть (false): на снимках и в пипетке — то, что под ней.</summary>
    public Action<bool>? HideSelfFromCapture { get; init; }
    /// <summary>«Наблюдай»: журнал того, что происходит на экране, и отчёт по нему.</summary>
    public Watcher? Watcher { get; init; }
    /// <summary>Долгая задача (план из шагов), к которой Клава сама возвращается, пока не закроет.</summary>
    public TaskBoard? Tasks { get; init; }
    /// <summary>Музыкальный слух и пение: (action, аргументы) → ответ мозгу (тяжёлое идёт в фоне, итог — событием).</summary>
    public Func<string, JsonObject, Task<string>>? Music { get; init; }
    /// <summary>Её гардероб: наряды, обувь, шляпы, свои вещи, правка облика (OverlayWardrobe).</summary>
    public Func<string, JsonObject, Task<string>>? Wardrobe { get; init; }
    /// <summary>Мастерская 3D-вещей (craft): действие, аргументы, спросить разрешение на скрипт Blender → ответ со снимком.</summary>
    public Func<string, JsonObject, Func<ToolRequest, Task<bool>>, Task<ToolResult>>? Craft { get; init; }
    /// <summary>Кто в комнате: отметить ушедшего / вернувшегося, список (room).</summary>
    public Func<string, JsonObject, Task<string>>? Room { get; init; }
    /// <summary>Смотреть игру вместе с другом: счёт, события, поправки (game_watch).</summary>
    public Func<string, JsonObject, Task<string>>? Game { get; init; }

    /// <summary>
    /// Категории, для которых пользователь выбрал «Разрешать всегда» в этой сессии.
    /// Хранится в памяти (не в конфиге) — сбрасывается при перезапуске.
    /// Dangerous-запросы (req.Dangerous == true) сюда НЕ попадают и всегда спрашиваются.
    /// </summary>
    public HashSet<string> AlwaysAllowed { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Инструменты Клавы. Каждый вызов проходит через <c>approve</c> — окно подтверждения другу;
/// на автопилоте безопасное выполняется молча, опасное — всегда с окном или блокируется.
/// </summary>
internal static partial class Tools
{
    /// <summary>Набор инструментов. Лапки (мышь/клава) есть, когда у Клавы есть ToolBox.</summary>
    public static JsonArray Schema(bool withHands)
    {
        var a = new JsonArray
        {
            Fn("list_dir", "Показать содержимое папки. Пути: полные (C:\\…), %USERPROFILE%, ~, «desktop\\…», «загрузки\\…», «документы\\…».",
                ("path", "string", "Путь к папке", true)),
            Fn("read_file", "Прочитать текстовый файл (до 8000 символов). С offset/limit — нужные строки с номерами (для кода: смотри, потом правь edit_file).",
                ("path", "string", "Путь к файлу", true),
                ("offset", "number", "С какой строки (1 — первая)", false),
                ("limit", "number", "Сколько строк (по умолчанию 400)", false)),
            Fn("edit_file", "Точечная правка файла: заменить кусок old_string на new_string (кусок должен встречаться ровно один раз, иначе replace_all). Код правь так, а не переписывая файл целиком.",
                ("path", "string", "Путь к файлу", true),
                ("old_string", "string", "Точный кусок из файла, с отступами", true),
                ("new_string", "string", "На что заменить", true),
                ("replace_all", "boolean", "Заменить все вхождения (по умолчанию нет)", false)),
            Fn("grep", "Поиск по содержимому файлов регуляркой → «файл:строка: текст». Сборки и зависимости (bin, obj, node_modules, .git) пропускаются.",
                ("pattern", "string", "Регулярное выражение", true),
                ("path", "string", "Папка или файл (по умолчанию текущая папка терминала)", false),
                ("glob", "string", "Маска имён: *.cs, *.{js,ts}", false),
                ("ignore_case", "boolean", "Без учёта регистра", false)),
            Fn("find_files", "Найти файлы по маске имени (*.cs, **/*.json, config*) — список, свежие сверху.",
                ("pattern", "string", "Маска", true),
                ("path", "string", "Папка (по умолчанию текущая папка терминала)", false)),
            Fn("linux", "Команда в настоящем Linux (WSL): Ubuntu по умолчанию или kali-linux — apt, gcc, python3, сетевые утилиты.",
                ("command", "string", "Команда bash", true),
                ("distro", "string", "Ubuntu или kali-linux (по умолчанию — какой поднимется)", false),
                ("timeout_sec", "number", "Сколько ждать (по умолчанию 120, до 1800)", false)),
            Fn("job", "Фоновые процессы — серверы, обучение моделей, долгие сборки и загрузки, чтобы не ждать их в shell: start — запустить (вывод в лог), output — хвост вывода, status — жив ли, stop — остановить, list — все.",
                ("action", "string", "start | output | status | stop | list", true),
                ("command", "string", "Для start: команда", false),
                ("shell", "string", "Для start: powershell (по умолчанию) | bash | linux", false),
                ("name", "string", "Для start — короткое имя; для остальных — имя или номер", false),
                ("lines", "number", "Для output: сколько последних строк (по умолчанию 60)", false)),
            Fn("agent", "Фоновые агенты — твои руки для настоящей работы на компьютере: код, сборка, установка, разбор проекта, исследование, настройка, скрипты — всё, что дольше пары команд. Каждый агент — Claude Code со своей вкладкой в кибер-терминале: терминалы, правка файлов, поиск, веб, скиллы и MCP друга, свои субагенты для параллельных кусков; разрешения даёшь ты (автопилот — сам). Можно несколько сразу. Ты свободна: слышишь друга и болтаешь, итог придёт сам. start — дать задачу (цель подробно: что, где, как проверить), list — все агенты, status — подробно про одного (шаги, план, субагенты), tell — указание по ходу или новая задача законченному (помнит сделанное), stop — остановить (id или all).",
                ("action", "string", "start | list | status | tell | stop | model | models", true),
                ("model", "string", "Для model: на какую модель пересадить — словами («джемини 3.8 флеш хай», «3.1 про», «опус 4.6 синкинг») или id. " +
                    "Агент продолжит с того же места. Без id — все живые агенты и модель по умолчанию для новых. models — что есть у шлюза. " +
                    "Упал на лимите («API Error», «accounts limited») — он сам пересядет на запасную; просят переключить — делай ЭТИМ, в свои конфиги и исходники не лезь.", false),
                ("goal", "string", "Для start: задача целиком", false),
                ("title", "string", "Для start: короткое имя вкладки (2–4 слова)", false),
                ("path", "string", "Для start: рабочая папка (по умолчанию — текущая папка твоего терминала)", false),
                ("id", "string", "Для status/tell/stop: номер агента (#2) или слово из имени; пусто — последний работающий", false),
                ("note", "string", "Для tell: что добавить или поправить", false)),
            Fn("write_file", "Создать или перезаписать текстовый файл (папки создаются сами).",
                ("path", "string", "Путь к файлу", true),
                ("content", "string", "Полное содержимое файла", true)),
            Fn("shell", "ГЛАВНЫЙ терминал: живой PowerShell на весь разговор, вывод виден в кибер-терминале Клавы. `cd`, переменные, venv сохраняются. Многострочные скрипты можно. Сборки, тесты, git, npm, dotnet, python, pip, winget, проверка процессов и сети. Ждёт до 5 минут (timeout_sec до 1800). Возвращает вывод и код выхода.",
                ("command", "string", "Команда или многострочный скрипт PowerShell", true),
                ("timeout_sec", "number", "Сколько секунд ждать (30..1800). По умолчанию 300.", false)),
            Fn("terminal_type", "Набрать команду лапками прямо в строке кибер-терминала Клавы (друг видит, как буквы печатаются) и выполнить её в том же живом PowerShell. Возвращает вывод. Бери, когда друг просит «введи в терминал», «напечатай команду», «покажи, как кодишь».",
                ("command", "string", "Команда PowerShell", true),
                ("timeout_sec", "number", "Сколько секунд ждать (по умолчанию 300)", false)),
            Fn("open_terminal", "Открыть встроенный кибер-терминал Клавы на экране. Вызывай СРАЗУ, когда друг просит открыть терминал/консоль.",
                ("path", "string", "Папка, в которой открыть терминал (по умолчанию рабочая папка).", false)),
            Fn("run_powershell", "Одноразовая команда PowerShell без общего состояния (таймаут 60 секунд). Для кодинга и всего с `cd` — бери shell.",
                ("command", "string", "Команда PowerShell целиком", true)),
            Fn("bash", "Команда в bash (Git Bash или WSL): Linux-утилиты, sed, awk, curl, gcc.",
                ("command", "string", "Команда bash", true),
                ("timeout_sec", "number", "Сколько секунд ждать (по умолчанию 60)", false)),
            Fn("open_app", "Запустить программу или открыть файл/папку/ссылку, как двойной клик. Понимает простые имена: paint, блокнот, калькулятор, проводник, терминал, vscode, chrome, steam, telegram, discord.",
                ("target", "string", "Что открыть: имя программы, путь к файлу/папке или URL.", true),
                ("args", "string", "Аргументы командной строки, если нужны.", false)),
            Fn("create_shortcut", "Создать ярлык на Рабочем столе (или в папке): на программу (можно просто имя: paint, блокнот, steam), файл, папку или сайт (для сайта создаётся .url).",
                ("target", "string", "Путь к программе/файлу/папке, имя программы или URL сайта", true),
                ("name", "string", "Имя ярлыка", true),
                ("location", "string", "«desktop» (по умолчанию) или путь к папке", false),
                ("args", "string", "Параметры запуска", false),
                ("icon", "string", "Путь к иконке (.ico / .exe)", false)),
            Fn("recycle", "Убрать файлы или папки в Корзину (обратимо). Так выкидывают хлам — не удаляй безвозвратно.",
                ("paths", "array", "Пути к тому, что убрать в корзину", true)),
            Fn("web_search", "Поиск в интернете в фоне (DuckDuckGo, запасной — Bing): друг ничего не видит, окна не открываются, спрашивать разрешения не надо. " +
                "Свежая инфа, доки, прохождения игр, ссылки. Не знаешь или не уверена — не выдумывай, а загугли и расскажи своими словами; " +
                "подробности со страницы — web_fetch.",
                ("query", "string", "Поисковый запрос", true),
                ("count", "number", "Сколько результатов (по умолчанию 5)", false)),
            Fn("web_fetch", "Прочитать веб-страницу по URL чистым текстом (без рекламы и скриптов).",
                ("url", "string", "Полный URL (https://...)", true)),
            Fn("browser_open", "Открыть сайт или поиск в браузере друга новой вкладкой (браузер по умолчанию). Если это не адрес — откроется поиск по этим словам.",
                ("url", "string", "Адрес (https://…) или что поискать", true)),
            Fn("browser_read", "Посмотреть, что открыто в браузере друга: адрес и заголовок активной вкладки и текст страницы. Бери, когда друг говорит «глянь вкладку», «что у меня в браузере», «прочитай страницу».",
                ("with_screenshot", "boolean", "Добавить снимок экрана (если страница требует входа или текста мало)", false)),
            Fn("save_screenshot", "Сделать скриншот и сохранить в файл PNG (по умолчанию «Изображения\\Screenshots»). Возвращает путь.",
                ("path", "string", "Куда сохранить (необязательно)", false),
                ("all_monitors", "boolean", "Все мониторы сразу (по умолчанию — монитор под курсором)", false)),
            Fn("list_windows", "Список открытых окон (заголовок и программа) — чтобы понять, куда переключаться."),
            Fn("focus_window", "Переключиться на окно (поднять на передний план, развернуть) по куску заголовка или имени программы.",
                ("window", "string", "Часть заголовка или имя программы: «chrome», «блокнот», «Paint», «Telegram»", true)),
            Fn("ui_elements", "Карта окна: кнопки, пункты меню, вкладки, поля, ссылки с именами и координатами центра (экранные). По ней жми click_element по имени — это точнее мыши по картинке.",
                ("window", "string", "Часть заголовка окна (по умолчанию активное)", false)),
            Fn("send_chat", "Отстукнуть маляву в чужое окно чата (Claude, ChatGPT, DeepSeek, Antigravity, Cursor, Telegram, Discord): найдёт окно, поднимет, напечатает текст и нажмёт Enter. ТОЛЬКО в окно, которое назвал друг: нет такого окна — не подставляй другое, скажи другу.",
                ("app", "string", "Часть заголовка ИМЕННО того окна, что назвал друг: «claude», «chatgpt», «telegram»…", true),
                ("text", "string", "Что написать", true),
                ("send", "boolean", "Нажать Enter (по умолчанию true)", false)),
            Fn("open_game", "Быстрый запуск Paint, Steam или игры с Рабочего стола / из меню Пуск.",
                ("name", "string", "paint, steam или имя игры", true)),
            Fn("mc_connect", "Зайти в Minecraft отдельным игроком Klava (бот). Без port — сама найдёт мир, открытый друзьями по локальной сети (в игре Esc → «Открыть для сети»). Сервер должен пускать без лицензии; модовые сборки (Forge с модами) бот не пройдёт.",
                ("host", "string", "Адрес сервера (без — этот компьютер или мир из сети)", false),
                ("port", "number", "Порт (без — мир, открытый по LAN)", false),
                ("username", "string", "Ник (по умолчанию Klava)", false)),
            Fn("mc_status", "Как дела у Клавы в Minecraft: здоровье, голод, где стоит, день/ночь, что в руке и в инвентаре, игроки и мобы рядом."),
            Fn("mc_action", "Действие в Minecraft (ответ приходит, когда сделано): come — подойти к игроку; follow — ходить за ним; goto — к x,y,z; collect — добыть count блоков target (oak_log, stone, iron_ore…) и подобрать; fight — драться с target или с ближайшим враждебным мобом; look — посмотреть на игрока; eat; equip — взять предмет в руку; give — кинуть предмет target (count) игроку player; jump; stop.",
                ("action", "string", "come | follow | goto | collect | fight | look | eat | equip | give | jump | stop", true),
                ("target", "string", "Игрок (come/follow/look), блок (collect: oak_log, stone, dirt), моб (fight: zombie) или предмет (equip/give)", false),
                ("count", "number", "Сколько (collect, give)", false),
                ("player", "string", "Кому кинуть (give)", false),
                ("x", "number", "X (для goto)", false),
                ("y", "number", "Y (для goto)", false),
                ("z", "number", "Z (для goto)", false)),
            Fn("mc_chat", "Написать в чат Minecraft от лица Клавы.",
                ("message", "string", "Сообщение", true)),
            Fn("translate", "Перевести текст через Google Переводчик: у друга откроется вкладка переводчика, а тебе вернётся точный перевод. Язык куда — to (по умолчанию русское → английский, остальное → русский).",
                ("text", "string", "Что перевести", true),
                ("to", "string", "На какой язык: en, ru, de, fr, es, ja, zh… или словом «английский»", false),
                ("from", "string", "С какого (по умолчанию определится сам)", false),
                ("show", "boolean", "Открыть переводчик во вкладке у друга (по умолчанию да)", false)),
            Fn("weather", "Погода и прогноз на несколько дней: у друга откроется Яндекс.Погода, тебе вернутся цифры (температура, осадки, ветер). Город не сказан — его город (запомненный или по сети).",
                ("place", "string", "Город (можно пусто — город друга)", false),
                ("days", "number", "Сколько дней прогноза, 1–7 (по умолчанию 3)", false),
                ("remember", "boolean", "Друг сказал, что это ЕГО город, — запомнить", false),
                ("show", "boolean", "Открыть погоду во вкладке у друга (по умолчанию да)", false)),
            Fn("tetris", "Поиграть в тетрис самой (автопилот, быстрый — не ты жмёшь клавиши, а он): play — открывает Jstris (спринт на 40 линий) или играет в уже открытом, разговор не занят, в конце получишь итог; stop — хватит; status.",
                ("action", "string", "play | stop | status", true),
                ("seconds", "number", "Сколько играть, 20–900 с (по умолчанию 150)", false),
                ("speed", "number", "Скорость 1–8 (по умолчанию 3)", false),
                ("url", "string", "Другая страница с тетрисом (по умолчанию Jstris)", false)),
            Fn("cs", "Counter-Strike 1.6 с ботами: заходишь отдельным игроком «Klava» (встроенный бот CS с живой реакцией и прицелом — ты им не водишь, а комментируешь и командуешь), друг играет рядом; твои фраги, смерти, раунды приходят тебе сами. start — открыть CS и создать игру (map), join — зайти (team: auto — к другу, t, ct), leave — выйти, bots — добавить ботов (count, team: enemy — против друга), status — счёт и твои фраги. Играть самой в шутер (быстро, с прицелом-нейронкой, меню, закуп) — это не cs, а arena (play/start).",
                ("action", "string", "start | join | leave | bots | status", true),
                ("map", "string", "Для start: карта (de_dust2, de_inferno, de_nuke, de_train…)", false),
                ("team", "string", "Для join/bots: auto | t | ct | enemy", false),
                ("count", "number", "Для bots: сколько (1–10)", false),
                ("skill", "number", "Скилл бота 1–100", false)),
            Fn("zuma","Поиграть в Зуму (лягушка стреляет шарами, собирай три одного цвета) самой — автопилот знает трассу каждого уровня из файлов игры, видит шары и цвет в пасти, целится с упреждением и стреляет прямо в окно игры (мышь друга не трогает), сам проходит меню и уровень за уровнем: play — играет в окне Zuma Deluxe у друга (локальная игра, не браузер; окна нет — сама запустит установленную), без разрешений, разговор не занят, в конце получишь итог; stop — хватит; status. Сама через game_action в Зуму не играй: пока думаешь, цепочка уезжает. И никогда не ставь игру на паузу, чтобы «закончить», — проигрываешь, так и скажи.",
                ("action", "string", "play | stop | status", true),
                ("seconds", "number", "Сколько играть, 30–7200 с (по умолчанию 3600); начатый уровень доигрывается всегда, новый после этого времени не начинается", false),
                ("mode", "string", "demon (по умолчанию: перед выстрелом перекрашивает шар в пасти под лучшую тройку — чит, друг разрешил) | honest (честно: только прицел, когда просят «играй честно»)", false)),
            Fn("gamer", "Учёба играть по игре друга (любая игра на клаве и мыши: CS, Serious Sam…): record_start — записывать, как друг играет (кадры игры + его клава и мышь; window — окно игры, пусто — любая известная игра), record_stop — хватит записывать; train — обучить нейронку на записях (game — какая игра, в фоне на видеокарте, минуты–час); play_start — играть самой по выученному (window — окно игры, game — чья нейронка): нейронка смотрит на кадры, ты жмёшь лапками, быстро и без раздумий, разговор свободен; play_stop; status — как там запись/учёба/игра; list — что записано и выучено; arena_start | arena_stop | arena_status — боевой режим шутеров.",
                ("action", "string", "record_start | record_stop | train | play_start | play_stop | status | list | arena_start | arena_stop | arena_status", true),
                ("window", "string", "Окно игры (часть заголовка), для record_start, play_start, arena_start", false),
                ("game", "string", "Какая игра (имя папки записей: «Counter-Strike», «sam2»…), для train, play_start, arena_start", false),
                ("seconds", "number", "Для play_start / arena_start: сколько играть, 30–7200 с (по умолчанию 600)", false)),
            Fn("arena", "Играть в шутер САМОЙ (CS 1.6, Serious Sam…) — быстро, без раздумий мозга: нейронка-глаза YOLO находит врагов и головы, прицел плавный по-человечески, стрельба очередями, ходьба; всё честными лапками (клава и мышь, без консоли). В CS сама проходит меню честными кнопками: play — открыть CS (если закрыта), «Новая игра» с картой (map) и ботами (bots, difficulty), зайти за команду (team: t | ct | auto) персонажем (model 1–4, 5 — случайный) и играть; start — играть там, где друг уже в игре (MOTD, выбор команды и класса пройдёт сама); закупается в начале раунда (B → автопокупка), убили — ждёт раунда, по своим (форма и надпись «Товарищ») не стреляет. Друг взялся за мышь — отдаёт управление, Backspace/Esc — стоп. stop — хватит; status. Итог игры придёт тебе сам.",
                ("action", "string", "play | start | school | stop | status (school — школа у ботов: наблюдателем смотришь, как играют боты, и учишь на этом свои ноги; seconds — сколько смотреть)", true),
                ("window", "string", "Окно игры (часть заголовка, по умолчанию Counter-Strike)", false),
                ("game", "string", "Имя игры («Counter-Strike»), по умолчанию Counter-Strike", false),
                ("seconds", "number", "Сколько секунд играть (по умолчанию 600)", false),
                ("map", "string", "Для play: карта (de_dust2, de_inferno, de_nuke, de_train, cs_office…)", false),
                ("team", "string", "За кого: t | ct | auto (по умолчанию auto)", false),
                ("model", "number", "Персонаж 1–4, 5 — случайный (по умолчанию 5)", false),
                ("bots", "number", "Для play: сколько ботов (по умолчанию 9)", false),
                ("difficulty", "string", "Для play: сложность ботов — легкая | средняя | тяжелая | эксперт", false)),
            Fn("wardrobe", "Твой гардероб — делаешь себе вещи сама. list — что есть (наряды, обувь, шляпы, что можно перекрасить и " +
                "поправить); wear — надеть наряд (name: gothic, cyber, classic, kimono, native, rope — готика с верёвочками, или твой " +
                "собственный); shoes — сменить обувь наряду (style: combat — берцы, knee — лаковые сапоги до колена, ankle — ботильоны, " +
                "native — родные; color, accent — цвет и отделка словами или #rrggbb; outfit — какому наряду, по умолчанию текущему); " +
                "hat — шляпа (kind: beret — берет художника, witch — ведьминская, none — снять; color, accent); make — сшить свой наряд " +
                "(name — имя; pieces — СОБРАТЬ из вещей разных нарядов, по одной на место: верх offShoulder (топ готики) | cropTop " +
                "(кроп-топ кибера) | pajamaTop (кардиган), куртка jacket (косуха), низ pleatSkirt (юбка) | pajamaPants (штанишки), " +
                "ноги fishnet (сетка) | leggings (легинсы кибера), обвязка harness — например [\"offShoulder\",\"jacket\",\"leggings\"]; " +
                "base — основа из встроенных, если без pieces; title — название, look — как он выглядит твоими словами, shoes/shoes_color/" +
                "shoes_accent, hat/hat_color, перекраска частей: top, skirt, jacket, legs, harness — цвет; wear=false — не надевать сразу); " +
                "sew — СШИТЬ НОВУЮ ВЕЩЬ своего кроя прямо по своему телу (гнётся вместе с тобой): name; что закрыть — torso_from/torso_to " +
                "(neck | high — горло | chest | underbust | waist | hips | crotch | thigh) и sleeves (none | cap | elbow | forearm | wrist) — " +
                "кофта, худи, свитер, топ, водолазка; legs_from/legs_to (waist | hips | crotch | thigh | knee | calf → crotch | thigh | knee | " +
                "calf | ankle | foot) — штаны, шорты, леггинсы, чулки, гольфы, носки; skirt_to (crotch | thigh | knee | calf | ankle, pleats — " +
                "складок 0–48, skirt_flare — клёш в см) — юбка; fit — свобода в см (0.25 облегает как чулки, 1 — по фигуре, 3 — оверсайз), " +
                "flare — расклёш к краю в см; color, color2 — цвета словом или #rrggbb; pattern — solid | stripes | vstripes | plaid | dots | " +
                "rib | fur | mesh | camo | hearts | checker, pattern_size — шаг узора в см; trim — цвет окантовки, trim_width, glow=true — " +
                "окантовка светится неоном; shine 0–1 — блеск (кожа, латекс); opacity 0.15–1 — прозрачность; neckline — round | v | high | off | " +
                "square; look — как выглядит твоими словами; outfit — в какой свой наряд (по умолчанию нынешний; встроенный — сделаю новый " +
                "«наряд + вещь»); wear=false — не надевать. Вещь встаёт на своё место вместо прежней. Тот же name — перешить. " +
                "Сложная форма (капюшон, оборки, тапки-лапки) — craft blender с skin=true по выгрузке твоего тела. " +
                "remove — выкинуть свой наряд или сшитую вещь; tweak — поправить облик как в редакторе (path: hairTint, hairAccent, kimono.top, " +
                "kimono.obi, kimono.petal…, value — цвет; bellVolume — 0–1); editor — открыть другу редактор облика. Всё меняется, " +
                "когда договоришь.",
                ("action", "string", "list | wear | shoes | hat | make | sew | remove | tweak | editor", true),
                ("torso_from", "string", "sew: верх откуда — neck | high | chest | underbust | waist", false),
                ("torso_to", "string", "sew: верх докуда — underbust | waist | hips | crotch | thigh", false),
                ("sleeves", "string", "sew: рукава — none | cap | elbow | forearm | wrist", false),
                ("legs_from", "string", "sew: штанины откуда — waist | hips | crotch | thigh | knee | calf", false),
                ("legs_to", "string", "sew: штанины докуда — crotch | thigh | knee | calf | ankle | foot", false),
                ("skirt_to", "string", "sew: юбка до — crotch | thigh | knee | calf | ankle", false),
                ("pleats", "number", "sew: складок у юбки (0 — гладкая)", false),
                ("skirt_flare", "number", "sew: клёш юбки к подолу, см", false),
                ("fit", "number", "sew: свобода кроя, см (0.25 — облегает, 1 — по фигуре, 3 — оверсайз)", false),
                ("flare", "number", "sew: расклёш к краю рукава/штанины/низа, см", false),
                ("color2", "string", "sew: второй цвет узора", false),
                ("pattern", "string", "sew: solid | stripes | vstripes | plaid | dots | rib | fur | mesh | camo | hearts | checker", false),
                ("pattern_size", "number", "sew: шаг узора, см", false),
                ("trim", "string", "sew: цвет окантовки краёв", false),
                ("trim_width", "number", "sew: ширина окантовки, см", false),
                ("glow", "string", "sew: true — окантовка светится неоном", false),
                ("shine", "number", "sew: блеск 0–1", false),
                ("opacity", "number", "sew: непрозрачность 0.15–1", false),
                ("neckline", "string", "sew: вырез — round | v | high | off | square", false),
                ("name", "string", "wear/make/remove: имя наряда", false),
                ("base", "string", "make: основа — gothic | cyber | classic | kimono | native | rope", false),
                ("pieces", "array", "make: вещи из разных нарядов — offShoulder, cropTop, pajamaTop, jacket, pleatSkirt, pajamaPants, fishnet, leggings, harness (или словами: «топ от готики», «косуха», «легинсы»)", false),
                ("title", "string", "make: название наряда", false),
                ("look", "string", "make: как выглядит, твоими словами", false),
                ("style", "string", "shoes: combat | knee | ankle | native", false),
                ("kind", "string", "hat: beret | witch | none", false),
                ("color", "string", "shoes/hat: цвет словом или #rrggbb", false),
                ("accent", "string", "shoes/hat: цвет отделки", false),
                ("outfit", "string", "shoes/hat: какому наряду (по умолчанию — надетому)", false),
                ("shoes", "string", "make: обувь", false), ("shoes_color", "string", "make: цвет обуви", false),
                ("shoes_accent", "string", "make: отделка обуви", false), ("hat", "string", "make: шляпа", false),
                ("hat_color", "string", "make: цвет шляпы", false), ("top", "string", "make: цвет верха", false),
                ("skirt", "string", "make: цвет юбки", false), ("jacket", "string", "make: цвет куртки", false),
                ("legs", "string", "make: цвет легинсов/сетки", false), ("harness", "string", "make: цвет верёвочек", false),
                ("wear", "string", "make: false — не надевать сразу", false),
                ("path", "string", "tweak: что поправить", false), ("value", "string", "tweak: новое значение", false)),
            Fn("music", "Твой музыкальный слух и голос певицы. listen — послушать, что сейчас играет на компьютере (seconds, по умолчанию 30): " +
                "в фоне запишешь звук (свой голос не пишется) и разберёшь — придёт паспорт: темп и грув, размер, тональность, аккорды, " +
                "инструменты с вероятностями, голос, форма, текст, звук; analyze — разобрать файл целиком (path: путь или слова из имени файла); " +
                "sing — спеть кавер своим голосом (path; lang: ru — русский текст слог в слог под ноты оригинала, его сама переведёшь, поёшь живым вокалом; " +
                "orig — в оригинале; lyrics — свой русский текст строками, если друг дал); play — спеть готовый кавер (path: «last» или " +
                "название) или включить разобранный трек, from/to — секунды куска; stop — остановить песню; cancel — отменить задачу; " +
                "similar — сочинить и сыграть похожую основу по разбору трека (тот же темп, грув, гармония с заменами, своя мелодия; MIDI " +
                "и звук из банка инструментов); compose — сочинить и сыграть СВОЮ мелодию без трека (mood: happy | sad | chill | energetic | rock, " +
                "сыграешь за синтезатором); violin — сыграть на скрипке тему 24-го каприса Паганини; studio — открыть другу музыкальную студию; " +
                "status. О музыке говори только по пришедшему " +
                "паспорту, не выдумывай.",
                ("action", "string", "listen | analyze | sing | similar | compose | violin | play | stop | cancel | studio | status", true),
                ("mood", "string", "compose: настроение — happy | sad | chill | energetic | rock (пусто — выберешь сама)", false),
                ("path", "string", "Файл песни (путь или слова из имени: «Lately», «Judith»), для play — «last» или название кавера", false),
                ("seconds", "number", "listen: сколько слушать, 10–120 (по умолчанию 30)", false),
                ("lang", "string", "sing: ru (по умолчанию) | orig", false),
                ("lyrics", "string", "sing: готовый русский текст, строка на строку песни", false),
                ("from", "number", "play: с какой секунды", false),
                ("to", "number", "play: до какой секунды", false)),
            Fn("room", "Кто в комнате. Сказали, что человек ушёл («Лис ушёл на работу», «Паши сегодня нет») — away; что вернулся " +
                "(«Лис пришёл», «я вернулся, это Лис») — here; list — кто ушёл. Пока человек отмечен ушедшим, распознавалка не " +
                "подписывает его именем чужие фразы — ты говоришь с теми, кто здесь.",
                ("action", "string", "away | here | list", true),
                ("name", "string", "away/here: кто (имя, как зовёте)", false),
                ("why", "string", "away: куда/почему (на работе, уехал домой)", false)),
            Fn("game_watch", "Смотреть игру вместе с другом своими глазами: раз в секунду кадр, важное — глазами; счёт и события " +
                "(в Бакшоте — патроны, выстрелы, жизни, раунд; в Detroit — сцены, субтитры, выборы; в DOOM 3 — монстры, коды) " +
                "приходят тебе в каждом ходе. Знакомые игры включаются сами. start — начать (game — название, по умолчанию " +
                "активное окно), stop, status, history — журнал игры (day: ГГГГ-ММ-ДД), fix — поправить счёт, если друг " +
                "сказал иначе (live, blank, round, hp_you, hp_dealer) или записать факт игры (note — «выбрали спасти Хэнка»).",
                ("action", "string", "start | stop | status | history | fix", true),
                ("game", "string", "start: игра (buckshot, detroit, doom3 или заголовок окна)", false),
                ("day", "string", "history: день ГГГГ-ММ-ДД (по умолчанию последний)", false),
                ("live", "number", "fix: боевых в стволе осталось", false),
                ("blank", "number", "fix: холостых в стволе осталось", false),
                ("round", "number", "fix: номер раунда", false),
                ("hp_you", "number", "fix: жизни друга", false),
                ("hp_dealer", "number", "fix: жизни Дилера", false),
                ("note", "string", "fix: факт игры словами", false)),
            Fn("watch_screen", "Наблюдение за экраном с журналом: start — «наблюдай / присмотри / следи, пока я сплю»: раз в полминуты смотришь на экран и записываешь, что происходит (что рисуют, кодят, смотрят); stop — хватит; status; report — отчёт, что было (с since или с начала наблюдения).",
                ("action", "string", "start | stop | status | report", true),
                ("since", "string", "Для report: с какого времени — «23:00», «8ч», «30 мин», «ночь» (по умолчанию — с начала наблюдения или последние 12 ч)", false)),
            Fn("read_window", "Прочитать ТЕКСТ окна целиком через доступность Windows — без снимка и без ошибок распознавания: ответ другой нейронки в её чате (Claude, ChatGPT, DeepSeek) после send_chat, переписка в мессенджере, документ, поля программы. По умолчанию — конец текста (там свежий ответ).",
                ("window", "string", "Часть заголовка окна (пусто — активное окно)", false),
                ("max_chars", "number", "Сколько символов вернуть (500–20000, по умолчанию 6000)", false),
                ("from_start", "boolean", "Читать с начала, а не с конца", false)),
            Fn("task_start", "Завести ДОЛГУЮ задачу с планом — когда дело больше 2–3 действий, в несколько этапов или с ожиданием (спросить другую нейронку и дождаться ответа, собрать, скачать, настроить). Задача живёт между ходами: система сама вернёт тебя к ней, пока не закроешь task_done. Заводи в самом начале, до первых команд.",
                ("goal", "string", "Цель целиком, как её понял: что должно получиться в итоге", true),
                ("steps", "array", "План: 3–8 конкретных шагов по порядку", true)),
            Fn("task_step", "Отметить шаг плана задачи: done / failed / skip / doing, с заметкой, что узнала (пути, версии, ответы, ошибки) — заметки видны тебе на следующих подходах. Номер вне плана — новый шаг (план уточнился).",
                ("step", "number", "Номер шага с 1", true),
                ("status", "string", "done | failed | skip | doing", true),
                ("note", "string", "Что получилось или что нашла — коротко, но с фактами", false),
                ("text", "string", "Новая формулировка шага (если уточнилась)", false)),
            Fn("task_wait", "Подождать и вернуться к задаче позже: ответ другой нейронки в чате, сборка, загрузка, перезапуск программы. Система разбудит тебя через seconds — тогда посмотришь результат и продолжишь.",
                ("seconds", "number", "Сколько ждать: 10–1800 (ответ нейронки — 60–120, сборка — 60–300)", true),
                ("reason", "string", "Чего ждёшь и что проверишь, когда вернёшься", true)),
            Fn("task_note", "Записать в задачу важный факт (ответ Клода, путь к файлу, найденную настройку), чтобы не потерять между подходами.",
                ("text", "string", "Факт", true)),
            Fn("task_done", "Закрыть задачу: цель достигнута и ПРОВЕРЕНА (командой, снимком, чтением файла). Не закрывай, пока не проверила.",
                ("summary", "string", "Итог: что сделано и где результат", true)),
            Fn("task_cancel", "Снять задачу: друг передумал, или сделать нельзя (объясни почему) — не бросай молча.",
                ("reason", "string", "Почему снимаешь", true)),
        };
        if (withHands)
        {
            // Лапки: мышь и клавиатура. Точнее всего — click_element (по имени) и click_text (по надписи на экране);
            // мышь по координатам — по сетке на снимке look_screen.
            a.Add(Fn("click_element", "Нажать кнопку/пункт меню/вкладку/ссылку/поле по ИМЕНИ (UI Automation) — без угадывания координат. Если элемент не жмётся напрямую — лапки кликнут точно в его центр. text — вписать в найденное поле.",
                ("name", "string", "Надпись или имя элемента: «Сохранить», «Файл», «OK», «Поиск»", true),
                ("window", "string", "Часть заголовка окна (по умолчанию активное)", false),
                ("button", "string", "left | right | double (по умолчанию left)", false),
                ("text", "string", "Вписать этот текст в найденное поле", false)));
            a.Add(Fn("click_text", "Кликнуть по надписи, которую видно на экране (распознавание текста находит, где она). Для веб-страниц, игр и программ без UI Automation.",
                ("text", "string", "Видимая надпись: «Войти», «Скачать», «Далее»", true),
                ("button", "string", "left | right | double (по умолчанию left)", false),
                ("index", "number", "Какое по счёту совпадение, если их несколько (по умолчанию 1)", false)));
            var look = Fn("look_screen", "Посмотреть на экран: свежий снимок с сеткой координат (подписи — пиксели снимка, левый верх 0,0), текст с экрана и список кнопок активного окна. Координаты для mouse_* бери по сетке ЭТОГО снимка. Плохо видно мелочь — area:'window' (только активное окно, резче) или zoom:[x,y,w,h] (приблизить кусок прошлого снимка).",
                ("note", "string", "Что ищешь на экране (для себя)", false),
                ("area", "string", "screen (весь монитор, по умолчанию) или window (только активное окно)", false));
            look["function"]!["parameters"]!["properties"]!["zoom"] = new JsonObject
            {
                ["type"] = "array", ["items"] = new JsonObject { ["type"] = "number" },
                ["description"] = "[x, y, ширина, высота] — прямоугольник на прошлом снимке, который приблизить",
            };
            a.Add(look);
            a.Add(Fn("mouse_move", "Подвести курсор к точке снимка.",
                ("x", "number", "X на снимке", true), ("y", "number", "Y на снимке", true)));
            a.Add(Fn("mouse_click", "Кликнуть в точке снимка (координаты по сетке look_screen).",
                ("x", "number", "X на снимке", true), ("y", "number", "Y на снимке", true),
                ("button", "string", "left, right или middle (по умолчанию left)", false),
                ("double", "boolean", "Двойной клик", false)));
            a.Add(Fn("mouse_drag", "Перетащить с одной точки снимка на другую (зажать, провести, отпустить).",
                ("from_x", "number", "Откуда X", true), ("from_y", "number", "Откуда Y", true),
                ("to_x", "number", "Куда X", true), ("to_y", "number", "Куда Y", true)));
            a.Add(Fn("scroll", "Прокрутить колесо: плюс — вверх, минус — вниз.",
                ("clicks", "number", "Сколько щелчков колеса (например 3 или -3)", true)));
            a.Add(Fn("type_text", "Напечатать текст в активное окно (любые буквы, раскладка не важна). \\n — Enter. Печатает туда, что сейчас впереди — укажи window, чтобы сначала переключиться на нужное окно.",
                ("text", "string", "Что напечатать", true),
                ("window", "string", "Часть заголовка окна, куда печатать (например «Блокнот», «Без имени»)", false)));
            a.Add(Fn("press_keys", "Нажать сочетание клавиш: ctrl+s, enter, alt+tab, ctrl+shift+esc, win+d.",
                ("combo", "string", "Клавиши через плюс", true)));
            var paint = Fn("paint", PaintTool.Describe);
            paint["function"]!["parameters"]!["properties"]!["ops"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Операции рисования по порядку (см. описание). Без ops — только узнать размер холста и посмотреть на него.",
                ["items"] = new JsonObject { ["type"] = "object" },
            };
            paint["function"]!["parameters"]!["properties"]!["look"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Лупа: [x, y, w, h] в единицах холста — вернёт увеличенный кусок холста с мелкой сеткой вместо всего холста (проверить стыки, тонкие линии, где именно косяк).",
                ["items"] = new JsonObject { ["type"] = "number" },
            };
            a.Add(paint);
            var craftTool = Fn("craft", CraftStore.Describe,
                ("action", "string", "list | body | make | move | look | wear | unwear | remove | export | import | blender", true),
                ("name", "string", "Имя вещи (коротко: наушники, очки, бантик)", false),
                ("title", "string", "make/import/blender: название вещи", false),
                ("attach", "string", "make/move/import/blender: куда крепится — head | neck | chest | waist | hips | shoulderL/R | elbowL/R | handL/R | thighL/R | kneeL/R | footL/R | room", false),
                ("scale", "number", "make/move: общий размер вещи (1 — как есть)", false),
                ("wear", "string", "make: false — не надевать сразу", false),
                ("file", "string", "import: путь к файлу .glb", false),
                ("size", "number", "import/blender: наибольший размер модели в метрах (подогнать под себя)", false),
                ("script", "string", "blender: скрипт Blender на Python (bpy); сцена пустая, OUT — путь для .glb, klava_body() — импорт твоего тела для кроя по фигуре", false),
                ("skin", "string", "make/import/blender/move: true — пришить к коже (гнётся вместе с телом: одежда, перчатки, тапки)", false));
            var craftProps = (JsonObject)craftTool["function"]!["parameters"]!["properties"]!;
            craftProps["at"] = new JsonObject { ["type"] = "array", ["description"] = "make/move: сдвиг вещи от точки крепления [x, y, z] в метрах (x — к левой руке, y — вверх, z — вперёд)", ["items"] = new JsonObject { ["type"] = "number" } };
            craftProps["rot"] = new JsonObject { ["type"] = "array", ["description"] = "make/move: поворот вещи [x, y, z] в градусах", ["items"] = new JsonObject { ["type"] = "number" } };
            craftProps["parts"] = new JsonObject { ["type"] = "array", ["description"] = "make: части вещи — объекты {shape, размеры формы, pos:[x,y,z], rot:[x,y,z], scale, color, material, opacity, mirror} (см. описание)", ["items"] = new JsonObject { ["type"] = "object" } };
            a.Add(craftTool);
            a.Add(Fn("memory", "Твоя память по времени: журнал разговоров (каждая реплика с датой и часом) разложен по полочкам — эпизоды " +
                "(день, время с–по, кто, о чём, чем кончилось), планы и обещания людей, заметки и шутки компании, факты. " +
                "recall — вспомнить по теме/человеку (query) и/или времени (day, time): ответ — записи с датой и часом; " +
                "timeline — эпизоды дня или отрезка по порядку (на длинный день — оглавление по часам, тогда уточни time); " +
                "hours — день по часам: когда сколько говорили и о чём; journal — ДОСЛОВНО реплики за отрезок до 6 часов (кто что " +
                "сказал и что ты ответила — когда нужна точная цитата или «что было в 15:30»); note — запомнить то, что друг просит " +
                "запомнить (text, who); promise — ТВОЁ обещание сделать или передать что-то позже («попроси их плиту помыть», «напомни " +
                "мне про врача»): text — что, who — кому, day — к какому дню; записывай сразу, как пообещала, — тебе напомнят, пока не " +
                "выполнишь; alias — «Гена — это Волк»: прозвище text человека who; plans — открытые планы и твои обещания; done — " +
                "выполнено (id); forget — забыть запись (id). Бери, когда спрашивают про прошлое глубже, чем тебе подсказано, спрашивают " +
                "«когда» и «во сколько», или просят «запомни». Нет в памяти — честно «не помню», не выдумывай и не путай дни.",
                ("action", "string", "recall (по умолчанию) | timeline | hours | journal | note | promise | alias | plans | done | forget", false),
                ("query", "string", "О чём вспомнить: тема, человек, игра", false),
                ("day", "string", "Какой день: «сегодня», «вчера», «позавчера», «в пятницу», «27.09»", false),
                ("time", "string", "Какое время дня: «утром», «вечером», «ночью», «15:00-16:30», «около трёх», «час назад»", false),
                ("text", "string", "note: что запомнить", false),
                ("who", "string", "note: про кого / кто просил", false),
                ("id", "string", "done / forget: id записи", false)));
            a.Add(Fn("later", "Отложенное дело «когда … — сделай …»: сама сделаешь потом, в фоне, даже если никто ничего не скажет. " +
                "Когда: at — во столько-то («02:00»), after_minutes — через N минут, text_gone — когда в окне ПРОПАДЁТ надпись (лимит сессии «limit», «лимит», «подождите»), " +
                "text_appears — когда появится. Что: в окне window (часть заголовка: «Claude», «Telegram») нажать кнопку click (по имени), напечатать type, нажать keys (enter, ctrl+enter). " +
                "Пример: друг набил текст в чат Claude, а там лимит сессии — глянь (read_window «Claude»), во сколько сброс («resets 2am»), и поставь later window:«Claude», at:«02:01», keys:«enter»; " +
                "времени не видно — text_gone с ТОЧНОЙ фразой плашки («Session limit reached»), не одним словом «limit»: слово может быть и в самой переписке. Клавиши уйдут только в это окно и не посреди его печати. " +
                "action: add (по умолчанию) | list | cancel (id или all). НЕ обещай «потом сделаю», не поставив later.",
                ("action", "string", "add | list | cancel", false),
                ("window", "string", "Окно, где ждать и действовать (часть заголовка)", false),
                ("at", "string", "Во сколько: «02:00», «2:30»", false),
                ("after_minutes", "number", "Через сколько минут", false),
                ("text_gone", "string", "Сделать, когда в окне пропадёт эта надпись", false),
                ("text_appears", "string", "Сделать, когда в окне появится эта надпись", false),
                ("click", "string", "Нажать кнопку/элемент по имени", false),
                ("type", "string", "Напечатать текст", false),
                ("keys", "string", "Нажать клавиши: enter, ctrl+enter, f5…", false),
                ("say", "string", "Что сказать другу, когда сделаешь", false),
                ("max_hours", "number", "Сколько ждать условия, часов (по умолчанию 12)", false),
                ("id", "string", "Для cancel: какое дело", false)));
            var art = Fn("paint_art", PaintTool.ArtDescribe,
                ("prompt", "string", "Что на картине, подробно и живо: «закат над морем с маяком на скале», «академический рисунок яблока на столе» (с образцом с экрана можно коротко)", false),
                ("style", "string", "масло (по умолчанию) | акварель | карандаш | тушь (комикс, аниме) | пиксель-арт | реализм (детально) | импрессионизм | ван гог | пуантилизм — у каждого своё письмо, не только мазки", false),
                ("format", "string", "Формат холста новой картины: вертикальный | квадрат | горизонтальный | широкий; пусто — по образцу (обычно квадрат). Просил друг «вертикально», «во весь рост» — ставь вертикальный", false),
                ("canvas", "string", "new (по умолчанию) — новая картина в своём окне Paint на чистом холсте; current — писать в открытый холст (дорисовать рисунок друга)", false),
                ("sign_place", "string", "Где подпись от руки: bottom-right (по умолчанию) | bottom-left | top-right | top-left", false),
                ("detail", "string", "fast — быстрее, крупнее мазок; fine — тоньше и дольше (по умолчанию обычно)", false),
                ("source", "string", "auto (нейросеть-художник; без неё — свой пейзаж, но только если просили пейзаж) | procedural — только свой процедурный пейзаж", false),
                ("reference_file", "string", "Образец из файла (путь к картинке) — рисовать с него", false),
                ("hand", "boolean", "Карандаш рисуется рукой инструментом «Карандаш» Paint, штрих за штрихом (по умолчанию да); false — старая вставка готовых слоёв", false),
                ("paper", "boolean", "Карандашом — на чистом листе: новый слой заливается белым, прежний рисунок остаётся в слоях под ним (по умолчанию да)", false),
                ("new_layer", "boolean", "Писать на новом слое поверх (по умолчанию да — рисунок друга под ним не трогается)", false),
                ("sketch", "boolean", "Сначала карандашный набросок (по умолчанию да)", false),
                ("sign", "boolean", "Подписать «Клава» с лапкой (по умолчанию да)", false));
            art["function"]!["parameters"]!["properties"]!["reference_box"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject { ["type"] = "number" },
                ["description"] = "Образец, который тебе показали на экране: рамка [x, y, w, h] вокруг картинки по снимку look_screen (как zoom). Сначала look_screen, потом paint_art с этой рамкой — нарисуешь именно его, а не выдумку нейросети",
            };
            a.Add(art);
            a.Add(Fn("draw_stroke", "Нарисовать линию лапками в ЛЮБОМ окне по координатам снимка look_screen. Для Paint бери инструмент paint — он точнее.",
                ("points", "string", "JSON массив точек снимка: [{\"x\":100,\"y\":150},{\"x\":120,\"y\":170}]", true),
                ("button", "string", "left или right (по умолчанию left)", false),
                ("delay_ms", "number", "Задержка между точками в мс (по умолчанию 12)", false)));
            var game = Fn("game_action", "Твои игровые лапки — клавиатура и мышь для ЛЮБОЙ игры (CS, Serious Sam, Minecraft…), как у живого игрока. down — зажать keys и ДЕРЖАТЬ, пока не отпустишь (бежишь вперёд, пока смотришь и думаешь; duration_ms — предел, по умолчанию 30 с); up — отпустить (без keys — все); tap — нажать; hold — нажать и держать duration_ms, потом отпустить; look — повернуть камеру мышью на dx,dy (вправо/вниз — плюс; ~1000 = пол-оборота в большинстве игр); click — клик кнопкой (button left|right); mouse_down / mouse_up — зажать / отпустить кнопку мыши (очередь, прицел); release_all — отпустить всё; wait. steps — длинная цепочка нажатий за один вызов (до 200 шагов, repeat — прогнать её до 100 раз, до 3 минут на ход): так играешь непрерывно, а не по кнопке за раз. Меню игры (закуп на B, выбор команды, пауза) — тоже лапками: жми клавиши и цифры, глядя на снимок. game_window — окно игры: поднимется, а после хода придёт снимок окна — сверяйся с ним. Зажатое отпускается само, если окно игры уйдёт или ты договоришь.",
                ("action", "string", "down | up | tap | hold | look | click | mouse_down | mouse_up | release_all | wait (если нет steps)", false),
                ("keys", "string", "Клавиши через плюс: w, a, s, d, space, shift, ctrl, e, q, r, 1..9, tilde, esc, enter, up, left…", false),
                ("duration_ms", "number", "Сколько держать / длительность поворота / ожидания", false),
                ("dx", "number", "Поворот камеры по горизонтали (look)", false),
                ("dy", "number", "Поворот камеры по вертикали (look)", false),
                ("button", "string", "left | right (click, hold_mouse)", false),
                ("game_window", "string", "Часть заголовка окна игры (Minecraft, Counter-Strike…)", false),
                ("observe", "boolean", "Прислать снимок окна игры после хода (по умолчанию да)", false));
            game["function"]!["parameters"]!["properties"]!["steps"] = new JsonObject
            {
                ["type"] = "array",
                ["description"] = "Серия шагов по порядку: [{action:'down',keys:'w'},{action:'look',dx:300},{action:'mouse_down'},{action:'wait',duration_ms:600},{action:'mouse_up'}]",
                ["items"] = new JsonObject { ["type"] = "object" },
            };
            game["function"]!["parameters"]!["properties"]!["repeat"] = new JsonObject
            {
                ["type"] = "number",
                ["description"] = "Сколько раз прогнать steps подряд (1–100): патруль, стрейф туда-сюда, серия выстрелов",
            };
            game["function"]!["parameters"]!["properties"]!["lesson"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Урок из этой катки одной строкой (что сработало, где закуп, какие клавиши, куда бежать, что убивает) — запишется в твою тетрадку по этой игре и напомнится в следующий раз",
            };
            a.Add(game);
        }
        return a;
    }

    private static JsonObject Fn(string name, string description, params (string Name, string Type, string Desc, bool Required)[] args)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var a in args)
        {
            var p = new JsonObject { ["type"] = a.Type, ["description"] = a.Desc };
            if (a.Type == "array") p["items"] = new JsonObject { ["type"] = "string" };
            props[a.Name] = p;
            if (a.Required) required.Add(a.Name);
        }
        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["parameters"] = new JsonObject { ["type"] = "object", ["properties"] = props, ["required"] = required },
            },
        };
    }

    // Команды, за которые спрашиваем даже на автопилоте: удаление, реестр, службы и задачи, пользователи и права,
    // установка/удаление программ, скачивание в файл и исполнение скачанного/строки как кода, убийство процессов,
    // выключение компа, защитник, драйверы, теневые копии, ссылки ФС. Чтение (Get-*, Format-Table, irm без -OutFile,
    // winget search) — не опасно: раньше «\bformat\b» ловил Format-Table, а irm — любой запрос к API, и простой
    // поиск в интернете собирал три окна подтверждения.
    [GeneratedRegex(@"(?<![\w-])(?:" +
                    @"Remove-Item\b|rm\b|rmdir\b|del\b|erase\b|rd\b|ri\b|Clear-Content\b|Clear-RecycleBin\b|Format-Volume\b|format(?:\.com)?\s+[a-z]:|diskpart\b|Clear-Disk\b|Initialize-Disk\b|" +
                    @"Stop-Computer\b|Restart-Computer\b|shutdown\b|logoff\b|" +
                    @"reg(?:\.exe)?\s+(?:add|delete|import|restore|load|unload)\b|(?:Set|New|Remove)-ItemProperty\b.*\b(?:HK(?:LM|CU|CR|U|CC)|Registry::)|" +
                    @"Set-ExecutionPolicy\b|bcdedit\b|takeown\b|icacls\b|cipher\s+/w|Set-Acl\b|" +
                    @"(?:Invoke-WebRequest|iwr|curl(?:\.exe)?|wget|Invoke-RestMethod|irm)\b[^|\n]*(?:-OutFile\b|\s-o\s|--output\b|\.exe\b|\.msi\b|\.ps1\b|\.bat\b)|Invoke-Expression\b|iex\b|" +
                    @"Start-Process\b[^\n]*-Verb\s+RunAs\b|net(?:\.exe)?\s+(?:user|localgroup|group|share)\b|schtasks(?:\.exe)?\s+/(?:create|delete|change)\b|Register-ScheduledTask\b|Unregister-ScheduledTask\b|" +
                    @"Stop-Process\b|kill\b|taskkill\b|Stop-Service\b|Set-Service\b|New-Service\b|Remove-Service\b|sc(?:\.exe)?\s+(?:delete|create|config|stop|failure)\b|" +
                    @"New-LocalUser\b|Set-LocalUser\b|Remove-LocalUser\b|Add-LocalGroupMember\b|sfc\s+/|DISM\b|Repair-WindowsImage\b|Invoke-WmiMethod\b|Invoke-CimMethod\b|Remove-CimInstance\b|-EncodedCommand\b|-enc\s|" +
                    @"vssadmin\b|fsutil\b|manage-bde\b|rundll32\b|regsvr32\b|msiexec\b|Install-Package\b|Install-Module\b|Uninstall-\w+|Disable-\w+|" +
                    @"choco(?:\.exe)?\s+(?:install|uninstall|upgrade)\b|winget(?:\.exe)?\s+(?:install|uninstall|upgrade|import)\b|scoop\s+(?:install|uninstall)\b|pip\s+uninstall\b|npm\s+(?:un)?install\s+-g\b|" +
                    @"pnputil\b|mklink\b|New-Item\b.*-ItemType\s+(?:SymbolicLink|Junction|HardLink)\b|Get-Credential\b|ConvertTo-SecureString\b|Export-Certificate\b|New-SelfSignedCertificate\b|" +
                    @"Enable-PSRemoting\b|Enter-PSSession\b|Invoke-Command\b|Set-MpPreference\b|Add-MpPreference\b|Remove-MpPreference\b|Set-NetFirewall\w*|New-NetFirewallRule\b|netsh\s+(?:advfirewall|firewall|winsock|int)\b)",
                    RegexOptions.IgnoreCase)]
    private static partial Regex DangerCmdRx();

    /// <summary>Опасная ли команда (для самотеста и окна подтверждения): что именно насторожило, или null.</summary>
    internal static string? DangerOf(string command) => DangerCmdRx().Match(command) is { Success: true } m ? m.Value : null;

    /// <summary>Катастрофа (формат, разметка дисков, загрузчик, снос Windows…): что именно, или null.</summary>
    internal static string? IsCatastrophe(string command) => CatastropheRx().Match(command) is { Success: true } m ? m.Value : null;

    /// <summary>Системная или приватная папка Windows (запись туда — только с согласия друга).</summary>
    internal static bool IsSystemPath(string path) => SystemPathRx().IsMatch(path);

    /// <summary>
    /// Безвозвратное удаление вне временного и пересобираемого (bin, obj, node_modules…): на автопилоте не выполняется —
    /// только в Корзину (Клава — инструментом recycle, агенты — через SendToRecycleBin).
    /// </summary>
    internal static bool IsHardDelete(string command)
    {
        if (command.Contains("SendToRecycleBin", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (Match m in HardDeleteRx().Matches(command))
        {
            // что удаляет эта удалялка: слова до конца оператора, без ключей (-Recurse, /s, -rf)
            var rest = command[(m.Index + m.Length)..];
            var stop = StatementEndRx().Match(rest);
            var targets = ArgSplitRx().Split(stop.Success ? rest[..stop.Index] : rest)
                .Select(t => t.Trim().Trim('\'', '"'))
                .Where(t => t.Length > 0 && t[0] != '-' && !CmdSwitchRx().IsMatch(t) &&
                            !t.Equals("Path", StringComparison.OrdinalIgnoreCase) && !t.Equals("LiteralPath", StringComparison.OrdinalIgnoreCase))
                .ToList();
            // из конвейера (Get-ChildItem … | Remove-Item) — судим по всей команде
            var safe = targets.Count == 0 ? SafeTargetRx().IsMatch(command) : targets.All(t => SafeTargetRx().IsMatch(t));
            if (!safe) return true;
        }
        return false;
    }

    // удалялки PowerShell / cmd / bash (git rm — не в счёт: файл остаётся в истории git)
    [GeneratedRegex(@"(?<![\w-])(?<!git\s+)(Remove-Item|rm|rmdir|rd|del|erase|ri)(?![\w-])", RegexOptions.IgnoreCase)]
    private static partial Regex HardDeleteRx();

    [GeneratedRegex(@"[;|\n]|&&")]
    private static partial Regex StatementEndRx();

    [GeneratedRegex(@"[\s,]+")]
    private static partial Regex ArgSplitRx();

    // ключи cmd (/s /q /f) — не цели удаления
    [GeneratedRegex(@"^/[a-zA-Z]$")]
    private static partial Regex CmdSwitchRx();

    // что удалять можно: временное и то, что сборка сделает заново
    [GeneratedRegex(@"(^|[\\/])(bin|obj|node_modules|__pycache__|dist|build|out|target|\.pytest_cache|\.mypy_cache|\.ruff_cache|\.venv|venv|\.next|\.cache|coverage)([\\/]\*?)?$|\$env:TEMP|%TEMP%|[\\/]Temp[\\/]|^/tmp/|AppData[\\/]Local[\\/]Temp|klava_shell|klava_agent|\.(log|tmp|bak|pyc|o|obj)$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeTargetRx();

    // Настоящая катастрофа — это на автопилоте блокируется всегда: форматирование, разметка дисков, загрузчик,
    // снос корня диска или папки Windows, отключение защитника, теневые копии.
    [GeneratedRegex(@"\b(Format-Volume|format(\.com)?\s+[a-z]:|diskpart|Clear-Disk|Initialize-Disk|bcdedit|vssadmin\s+delete|manage-bde|cipher\s+/w|Set-MpPreference\s+.*-Disable|takeown\s+/f\s+[a-z]:\\(windows|$)|Remove-Item\s+.*[a-z]:\\(windows|program files|users)?\\?[""']?\s*(-|$)|rm\s+-rf?\s+/|del\s+/[sq].*[a-z]:\\(windows)?\\?\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex CatastropheRx();

    // «Супер-системные» и приватные пути: сюда Клава не лезет ни автопилотом, ни без ярко-красного окна.
    [GeneratedRegex(@"^([A-Za-z]:[\\/]?$|\\\\(\?|\.|[^\\]+\\[^\\$]+\$)|\\\\[^\\]+\\[a-zA-Z]\$)|" +
                    @"[\\/](Windows|Program Files( \(x86\))?|ProgramData|PerfLogs|Recovery|Boot|EFI|System Volume Information|\$Recycle\.Bin|\$WinREAgent)([\\/]|$)|" +
                    @"[\\/](System32|SysWOW64|WinSxS|Fonts|Drivers|DriverStore|assembly|Registration|servicing|CSC|Speech_OneCore|Media|Boot|Logs\\CBS|inf|schemas)([\\/]|$)|" +
                    @"[\\/]Users[\\/](Default|Public|All Users)([\\/]|$)|" +
                    @"[\\/]AppData[\\/](Roaming|Local)[\\/]Microsoft[\\/](Windows[\\/](Start Menu|Cookies|Credentials|Vault|Protect|SchCache|SystemCertificates|WebCache)|Vault|Credentials|Protect|Crypto|SystemCertificates|Ngc|Password)",
                    RegexOptions.IgnoreCase)]
    private static partial Regex SystemPathRx();

    /// <summary>Путь от модели → настоящий: переменные, ~, «desktop\…», «загрузки\…»; относительный — от рабочей папки.</summary>
    private static string ResolveUserPath(string path, string? baseDir = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        var p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"', '\''));
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (p == "~") return home;
        if (p.StartsWith("~\\", StringComparison.Ordinal) || p.StartsWith("~/", StringComparison.Ordinal)) p = Path.Combine(home, p[2..]);
        (string[] Names, Environment.SpecialFolder? Folder, string? Sub)[] known =
        [
            (["desktop", "рабочий стол", "рабочийстол"], Environment.SpecialFolder.Desktop, null),
            (["documents", "документы", "мои документы"], Environment.SpecialFolder.MyDocuments, null),
            (["pictures", "изображения", "картинки"], Environment.SpecialFolder.MyPictures, null),
            (["music", "музыка"], Environment.SpecialFolder.MyMusic, null),
            (["videos", "видео"], Environment.SpecialFolder.MyVideos, null),
            (["downloads", "загрузки"], null, "Downloads"),
        ];
        foreach (var (names, folder, sub) in known)
            foreach (var n in names)
            {
                var root = folder is { } f ? Environment.GetFolderPath(f) : Path.Combine(home, sub!);
                if (p.Equals(n, StringComparison.OrdinalIgnoreCase)) return root;
                if (p.Length > n.Length && p.StartsWith(n, StringComparison.OrdinalIgnoreCase) && p[n.Length] is '\\' or '/')
                    return Path.Combine(root, p[(n.Length + 1)..]);
            }
        if (!Path.IsPathRooted(p)) p = Path.Combine(string.IsNullOrEmpty(baseDir) ? home : baseDir, p);
        return Path.GetFullPath(p);
    }

    /// <summary>
    /// Спросить или не спросить: категория безопасна и автопилот включён и это НЕ Dangerous — выполняем молча;
    /// пользователь выбрал «Разрешать всегда» для этой категории — тоже молча (не для Dangerous);
    /// иначе — окно подтверждения. Реализация одна на все инструменты, чтобы правило было общее и предсказуемое.
    /// </summary>
    private static Task<bool> ApproveOrAutoAsync(ToolRequest req, Func<ToolRequest, Task<bool>> approve, ToolBox? box)
    {
        if (box is { Autopilot: true } && !req.Dangerous && req.Category is "read" or "write" or "open" or "recycle" or "web" or "shortcut" or "shell" or "hands")
        {
            Log.Write($"автопилот: {req.Title} — {req.Details.Split('\n')[0]}");
            return Task.FromResult(true);
        }
        // «Разрешать всегда» — пользователь запомнил категорию в этой сессии (только не для Dangerous)
        if (box is not null && !req.Dangerous && box.AlwaysAllowed.Contains(req.Category))
        {
            Log.Write($"всегда разрешено [{req.Category}]: {req.Title} — {req.Details.Split('\n')[0]}");
            return Task.FromResult(true);
        }
        return approve(req);
    }

    private static readonly HashSet<string> HandsTools =
        ["look_screen", "mouse_move", "mouse_click", "mouse_drag", "scroll", "type_text", "press_keys", "draw_stroke", "game_action", "click_text", "click_element", "paint", "paint_art"];

    /// <summary>Инструменты «печатает и кодит» — на аватаре ноутбук; «водит мышью» — лапка тянется к курсору.</summary>
    private static readonly HashSet<string> KeyboardTools = ["type_text", "press_keys", "terminal_type", "shell", "write_file", "open_terminal"];
    private static readonly HashSet<string> MouseTools =
        ["mouse_move", "mouse_click", "mouse_drag", "scroll", "draw_stroke", "game_action", "click_text", "click_element", "paint", "paint_art", "focus_window"];

    /// <summary>Разобрать, спросить разрешения, выполнить. Возвращает текст (и, для снимков, картинку) для модели.</summary>
    public static async Task<ToolResult> RunAsync(ToolCall call, Func<ToolRequest, Task<bool>> approve, KlavaConfig cfg, ToolBox? box, CancellationToken ct)
    {
        _gameNotesDir = cfg.Abs(Path.Combine("data", "games"));
        JsonObject? args;
        try { args = JsonNode.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson)?.AsObject(); }
        catch (JsonException) { return new ToolResult("Ошибка: аргументы инструмента — не JSON. Вызови инструмент ещё раз с правильными аргументами."); }
        catch (InvalidOperationException) { return new ToolResult("Ошибка: аргументы инструмента должны быть JSON-объектом."); }
        args ??= new JsonObject();
        string Arg(string n) => args[n] switch
        {
            null => "",
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            var other => other.ToJsonString(),
        };
        double Num(string n) => args[n] is { } v && double.TryParse(v.ToString().Trim('"'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
        bool Flag(string n, bool def) => args[n] switch
        {
            null => def,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            JsonValue v when v.TryGetValue<string>(out var s) => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1",
            _ => def,
        };
        var baseDir = box?.Shell.Cwd is { Length: > 0 } cwd ? cwd : box?.WorkDir is { Length: > 0 } wd ? wd : cfg.Abs("workspace");
        if (KeyboardTools.Contains(call.Name)) box?.Activity?.Invoke("keyboard");
        else if (MouseTools.Contains(call.Name)) box?.Activity?.Invoke("mouse");

        try
        {
            if (HandsTools.Contains(call.Name))
                return box is null
                    ? new ToolResult("Лапок сейчас нет. Сделай через shell или объясни словами.")
                    : await RunHandsAsync(call.Name, args, Arg, Num, Flag, approve, box, cfg, ct);

            switch (call.Name)
            {
                case "list_dir":
                {
                    var path = ResolveUserPath(Arg("path"), baseDir);
                    var sys = SystemPathRx().IsMatch(path);
                    if (!await ApproveOrAutoAsync(new ToolRequest("Посмотреть папку", path, sys, sys ? "Системная папка — обычно туда не лезем." : null, "read"), approve, box)) return new ToolResult(Denied);
                    if (!Directory.Exists(path)) return new ToolResult($"Папки нет: {path}");
                    var sb = new StringBuilder($"{path}:\n");
                    var n = 0;
                    foreach (var d in Directory.EnumerateDirectories(path).Take(120)) { sb.AppendLine($"[папка] {Path.GetFileName(d)}"); n++; }
                    foreach (var f in Directory.EnumerateFiles(path).Take(Math.Max(0, 250 - n)))
                    {
                        var fi = new FileInfo(f);
                        sb.AppendLine($"{fi.Name}  ({fi.Length / 1024.0:F1} КБ, {fi.LastWriteTime:dd.MM HH:mm})");
                    }
                    return new ToolResult(n == 0 && sb.ToString().Split('\n').Length <= 2 ? $"Папка {path} пустая." : sb.ToString());
                }
                case "read_file":
                {
                    var path = ResolveUserPath(Arg("path"), baseDir);
                    var sys = SystemPathRx().IsMatch(path);
                    if (!await ApproveOrAutoAsync(new ToolRequest("Прочитать файл", path, sys, sys ? "Файл в системной папке." : null, "read"), approve, box)) return new ToolResult(Denied);
                    if (!File.Exists(path)) return new ToolResult($"Файла нет: {path}");
                    var fi = new FileInfo(path);
                    if (Num("offset") > 0 || Num("limit") > 0) return await ReadLinesAsync(path, (int)Num("offset"), (int)Num("limit"), ct);
                    if (fi.Length > 4_000_000) return new ToolResult($"Файл слишком большой ({fi.Length / 1024 / 1024} МБ) — читаю только до 4 МБ. Возьми shell: Get-Content -TotalCount 200.");
                    var text = await File.ReadAllTextAsync(path, Encoding.UTF8, ct);
                    return new ToolResult(text.Length > 8000 ? text[..8000] + $"\n…(обрезано, всего {text.Length} символов)" : text);
                }
                case "write_file":
                {
                    var path = ResolveUserPath(Arg("path"), baseDir);
                    var content = Arg("content");
                    var exists = File.Exists(path);
                    var sys = SystemPathRx().IsMatch(path);
                    var details = $"{(exists ? "ПЕРЕЗАПИСАТЬ" : "Создать")}: {path}\r\n\r\n{content}";
                    var reason = sys ? "Системная папка." : null;
                    if (!await ApproveOrAutoAsync(new ToolRequest("Записать файл", details, sys, reason, "write"), approve, box)) return new ToolResult(Denied);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // старое содержимое не теряется: копия рядом в .bak (перезапись — самое частое «ой»)
                    if (exists) { try { File.Copy(path, path + ".bak", overwrite: true); } catch (IOException) { } }
                    await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), ct);
                    return new ToolResult($"Записано {content.Length} символов в {path}{(exists ? " (старая версия — рядом .bak)" : "")}");
                }
                case "run_powershell":
                {
                    var command = Arg("command");
                    var m = DangerCmdRx().Match(command);
                    var danger = m.Success || SystemPathRx().IsMatch(command);
                    if (box is { Autopilot: true } && CatastropheRx().IsMatch(command))
                        return new ToolResult($"❌ Заблокировано защитой: это может угробить систему ({CatastropheRx().Match(command).Value}).");
                    if (box is { Autopilot: true } && IsHardDelete(command))
                        return new ToolResult("❌ [Защита Клавы]: безвозвратное удаление — только в Корзину. Удаляй инструментом recycle и продолжай.");
                    var reason = m.Success ? $"Опасная команда: {m.Value}" : danger ? "Затрагивает системные папки." : null;
                    // на автопилоте опасное выполняется без окна (катастрофы и удаление отрезаны выше)
                    if (!await ApproveOrAutoAsync(new ToolRequest("Выполнить команду PowerShell", command, danger && box is not { Autopilot: true }, reason, "shell"), approve, box))
                        return new ToolResult(Denied);
                    return new ToolResult(await RunPowerShellAsync(command, baseDir, ct));
                }
                case "shell":
                    return await ShellRunAsync(Arg("command"), Num("timeout_sec"), approve, cfg, box, typed: false, ct);
                case "terminal_type":
                    return await ShellRunAsync(Arg("command"), Num("timeout_sec"), approve, cfg, box, typed: true, ct);
                case "open_terminal":
                    return await OpenTerminalAsync(Arg("path"), cfg, baseDir, approve, box);
                case "open_app":
                    return await OpenAppAsync(Arg("target"), Arg("args"), baseDir, approve, box);
                case "recycle":
                    return await RecycleAsync(args, baseDir, approve, box);
                case "send_chat":
                    return await SendChatAsync(Arg("app"), Arg("text"), Flag("send", true), approve, box);
                case "web_search":
                    return await WebSearchAsync(Arg("query"), (int)Num("count"), approve, box, ct);
                case "web_fetch":
                    return await WebFetchAsync(Arg("url"), approve, box, ct);
                case "browser_open":
                    return await BrowserOpenAsync(Arg("url"), approve, box);
                case "browser_read":
                    return await BrowserReadAsync(Flag("with_screenshot", false), approve, box, ct);
                case "save_screenshot":
                {
                    if (box?.SaveScreenshot is null) return new ToolResult("Глаза ещё не открылись — попробуй через пару секунд.");
                    var p = Arg("path");
                    var path = await box.SaveScreenshot(string.IsNullOrWhiteSpace(p) ? null : ResolveUserPath(p, baseDir), Flag("all_monitors", false));
                    Log.Write($"скриншот: {path}");
                    return new ToolResult($"Скриншот сохранён: {path}");
                }
                case "list_windows":
                {
                    var wins = Uia.ListWindows();
                    return new ToolResult(wins.Count == 0 ? "Открытых окон не видно." :
                        "Открытые окна (сверху — ближние):\n" + string.Join("\n", wins.Select(w => $"«{w.Title}» — {w.Process}")));
                }
                case "focus_window":
                {
                    var h = Uia.FindWindow(Arg("window"));
                    if (h == IntPtr.Zero) return new ToolResult($"Окна «{Arg("window")}» не нашла. Список: list_windows.");
                    Uia.Focus(h);
                    await Task.Delay(200, ct);
                    return new ToolResult($"Переключилась на «{Uia.WindowTitle(h)}».");
                }
                case "ui_elements":
                {
                    var h = string.IsNullOrWhiteSpace(Arg("window")) ? ForegroundNotMine() : Uia.FindWindow(Arg("window"));
                    if (h == IntPtr.Zero) return new ToolResult("Окно не нашла.");
                    var map = await Uia.DescribeAsync(h, 80, TimeSpan.FromSeconds(5));
                    return new ToolResult($"Окно «{Uia.WindowTitle(h)}» ({Uia.ProcessName(h)}). Элементы (центр — экранные координаты, жми click_element по имени):\n{map}");
                }
                case "create_shortcut":
                    return await CreateShortcutAsync(Arg("target"), Arg("name"), Arg("location"), Arg("args"), Arg("icon"), baseDir, approve, box);
                case "open_game":
                    return await OpenGameAsync(Arg("name"), approve, box);
                case "bash":
                    return await BashRunAsync(Arg("command"), Num("timeout_sec"), baseDir, approve, box, ct);
                case "linux":
                    return await LinuxAsync(Arg("command"), Num("timeout_sec") > 0 ? Num("timeout_sec") : 120, Arg("distro").Trim(), baseDir, approve, box, ct);
                case "edit_file":
                    return await EditFileAsync(ResolveUserPath(Arg("path"), baseDir), Arg("old_string"), Arg("new_string"), Flag("replace_all", false), approve, box, ct);
                case "grep":
                {
                    var gp = ResolveUserPath(Arg("path") is { Length: > 0 } p0 ? p0 : baseDir, baseDir);
                    if (!await ApproveOrAutoAsync(new ToolRequest("Поиск по файлам", $"{Arg("pattern")} в {gp}", false, null, "read"), approve, box)) return new ToolResult(Denied);
                    var (pat, glob, ic) = (Arg("pattern"), Arg("glob"), Flag("ignore_case", false));
                    return await Task.Run(() => Grep(pat, gp, glob, ic, ct), ct);
                }
                case "find_files":
                {
                    var fp = ResolveUserPath(Arg("path") is { Length: > 0 } p1 ? p1 : baseDir, baseDir);
                    if (!await ApproveOrAutoAsync(new ToolRequest("Поиск файлов", $"{Arg("pattern")} в {fp}", false, null, "read"), approve, box)) return new ToolResult(Denied);
                    var pat = Arg("pattern");
                    return await Task.Run(() => FindFiles(pat, fp, ct), ct);
                }
                case "cs":
                    return await CsAsync(Arg("action"), Arg("map"), Arg("team"), (int)Num("count"), (int)Num("skill"), cfg, approve, box);
                case "job":
                    return await JobAsync(Arg("action"), Arg("command"), Arg("shell"), Arg("name"), (int)Num("lines"), baseDir, cfg, approve, box);
                case "agent":
                {
                    var act = Arg("action").Trim().ToLowerInvariant();
                    if (!AgentHub.Available(cfg))   // Claude Code нет или выбран прежний агент — самодельный цикл
                        return new ToolResult(act switch
                        {
                            "start" => box is null ? "Фонового агента в этом режиме нет." : AgentRunner.Start(Arg("goal"), cfg, box, approve),
                            "tell" => AgentRunner.Tell(Arg("note")),
                            "stop" => AgentRunner.Stop(),
                            _ => AgentRunner.Status(),
                        });
                    return new ToolResult(act switch
                    {
                        "start" => box is null ? "Фоновых агентов в этом режиме нет." : await AgentHub.StartAsync(Arg("goal"), Arg("title"), Arg("path"), cfg, box, approve),
                        "tell" => AgentHub.Tell(Arg("id"), Arg("note")),
                        "stop" => AgentHub.Stop(Arg("id")),
                        "model" or "switch" => await AgentHub.ModelAsync(Arg("id"), Arg("model"), cfg),
                        "models" => await AgentHub.ModelAsync("", "", cfg),
                        "list" => AgentHub.Overview() is { Length: > 0 } all ? all : "Агентов нет — дай задачу через agent start.",
                        _ => AgentHub.Status(Arg("id")),
                    });
                }
                case "mc_connect":
                    return await McConnectAsync(Arg("host"), (int)Num("port"), Arg("username"), approve, cfg, box);
                case "mc_status":
                    return await McStatusAsync(cfg);
                case "mc_action":
                    GameMode.Touch("minecraft", TimeSpan.FromMinutes(3));   // бегает ботом в Майнкрафте — сидит за столом
                    return await McActionAsync(Arg("action"), Arg("target"), Num("count"), Arg("player"), Num("x"), Num("y"), Num("z"), approve, cfg, box);
                case "mc_chat":
                    return await McChatAsync(Arg("message"), approve, cfg, box);
                case "translate":
                    return await TranslateAsync(Arg("text"), Arg("to"), Arg("from"), Flag("show", true), approve, box, ct);
                case "weather":
                    return await WeatherAsync(Arg("place"), (int)Num("days"), Flag("show", true), Flag("remember", false), cfg, approve, box, ct);
                case "tetris":
                    return new ToolResult(Arg("action").Trim().ToLowerInvariant() switch
                    {
                        "play" or "start" => await TetrisBot.StartAsync(Arg("url"), (int)Num("seconds"), (int)Num("speed")),
                        "stop" => TetrisBot.Stop(),
                        _ => TetrisBot.Status(),
                    });
                case "memory":
                    return new ToolResult(MemoryBank.Current is { } bank
                        ? await bank.ToolAsync(Arg("action"), Arg("query"), Arg("day"), Arg("text"), Arg("who"), Arg("id"), Arg("time"))
                        : "Банк памяти ещё не загрузился.");
                case "later":
                {
                    var act = Arg("action").Trim().ToLowerInvariant();
                    if (act == "list") return new ToolResult(Later.List());
                    if (act is "cancel" or "remove" or "отмена") return new ToolResult(Later.Cancel(Arg("id")));
                    var window = Arg("window").Trim();
                    var at = Later.ParseAt(Arg("at"), Num("after_minutes"));
                    var gone = Arg("text_gone").Trim();
                    var appears = Arg("text_appears").Trim();
                    if (at is null && gone.Length == 0 && appears.Length == 0)
                        return new ToolResult("Когда делать? Нужно at («02:00»), after_minutes или text_gone/text_appears с window.");
                    if ((gone.Length > 0 || appears.Length > 0 || Arg("click").Length + Arg("type").Length + Arg("keys").Length > 0) && window.Length == 0)
                        return new ToolResult("В каком окне? Укажи window — часть заголовка (например «Claude»).");
                    if (Arg("keys").Length > 0 && Hands.ParseCombo(Arg("keys")) is null)
                        return new ToolResult($"Не поняла клавиши «{Arg("keys")}». Пример: enter, ctrl+enter.");
                    var acts = Arg("click").Length + Arg("type").Length + Arg("keys").Length > 0;
                    if (acts && !await ApproveOrAutoAsync(new ToolRequest("Отложенное дело лапками",
                            $"Потом, когда придёт условие, Клава сама поднимет окно «{window}» и " +
                            string.Join(", ", new[] { Arg("click").Length > 0 ? $"нажмёт «{Arg("click")}»" : null, Arg("type").Length > 0 ? "напечатает текст" : null, Arg("keys").Length > 0 ? $"нажмёт {Arg("keys")}" : null }.Where(s => s is not null)) +
                            ".\r\nКлавиши уходят только в это окно и не посреди твоей печати. Отменить: скажи «отмени отложенное».", false, null, "hands"), approve, box))
                        return new ToolResult(Denied);
                    var hours = Num("max_hours") > 0 ? Num("max_hours") : 12;
                    var id = "L" + DateTime.Now.ToString("HHmmss");
                    return new ToolResult(Later.Add(new Later.Job(id, window, at, gone.Length > 0 ? gone : null, appears.Length > 0 ? appears : null,
                        Arg("click").Length > 0 ? Arg("click") : null, Arg("type").Length > 0 ? Arg("type") : null, Arg("keys").Length > 0 ? Arg("keys") : null,
                        Arg("say").Length > 0 ? Arg("say") : null, DateTime.Now, DateTime.Now.AddHours(Math.Clamp(hours, 0.05, 72)))));
                }
                case "zuma":
                {
                    var act = Arg("action").Trim().ToLowerInvariant();
                    if (act == "stop") return new ToolResult(ZumaBot.Stop());
                    if (act is not ("play" or "start")) return new ToolResult(ZumaBot.Status());
                    if (box is not { HandsAllowed: true }) return new ToolResult("Мышь в этом режиме не трогаю (проверка без рук).");
                    // без окна разрешения (друг: «на захватить игру разрешение не надо»): игра — только в окне Зумы,
                    // сообщениями окну, мышь друга не трогается; забрать — Esc/Backspace, другое окно, «стоп», пункт меню
                    Log.Write("инструмент «Играть в Зуму»: без запроса — только окно Zuma Deluxe");
                    var honest = Arg("mode").Contains("honest", StringComparison.OrdinalIgnoreCase) || Arg("mode").Contains("чест", StringComparison.OrdinalIgnoreCase);
                    return new ToolResult(await ZumaBot.StartAsync((int)Num("seconds"), !honest));
                }
                case "gamer":
                    return await GamerAsync(Arg("action"), Arg("window"), Arg("game"), (int)Num("seconds"), cfg, approve, box);
                case "arena":
                    return await ArenaAsync(Arg("action"), Arg("window"), Arg("game"), (int)Num("seconds"), cfg, approve, box,
                        Arg("map"), Arg("team"), (int)Num("model"), (int)Num("bots"), Arg("difficulty"));
                case "music":
                    return new ToolResult(box?.Music is { } music ? await music(Arg("action"), args) : "Музыкального слуха в этом режиме нет.");
                case "wardrobe":
                    return new ToolResult(box?.Wardrobe is { } wardrobe ? await wardrobe(Arg("action"), args) : "Гардероба в этом режиме нет.");
                case "craft":
                    return box?.Craft is { } craft ? await craft(Arg("action"), args, r => ApproveOrAutoAsync(r, approve, box)) : new ToolResult("Мастерской в этом режиме нет.");
                case "room":
                    return new ToolResult(box?.Room is { } room ? await room(Arg("action"), args) : "Списка комнаты в этом режиме нет.");
                case "game_watch":
                    return new ToolResult(box?.Game is { } game ? await game(Arg("action"), args) : "Смотреть игру в этом режиме не на чем.");
                case "vr":
                    return new ToolResult(await KlavaVR.RunToolAsync(Arg("action"), args));
                case "watch_screen":
                {
                    if (box?.Watcher is not { } w) return new ToolResult("Наблюдать не на чем: глаза не подключены в этом режиме.");
                    return Arg("action").Trim().ToLowerInvariant() switch
                    {
                        "start" or "on" => new ToolResult(w.Start()),
                        "stop" or "off" => new ToolResult(w.Stop()),
                        "report" => new ToolResult(await w.ReportAsync(Arg("since"), ct)),
                        _ => new ToolResult(w.Status()),
                    };
                }
                case "read_window":
                {
                    var h = string.IsNullOrWhiteSpace(Arg("window")) ? ForegroundNotMine() : Uia.FindWindow(Arg("window"));
                    if (h == IntPtr.Zero) return new ToolResult($"Окна «{Arg("window")}» не нашла. Что открыто — list_windows.");
                    var max = (int)Math.Clamp(Num("max_chars") > 0 ? Num("max_chars") : 6000, 500, 20000);
                    var text = (await Uia.WindowTextAsync(h, max, TimeSpan.FromSeconds(8)))?.Trim();
                    if (string.IsNullOrEmpty(text)) return new ToolResult($"Не смогла прочитать текст окна «{Uia.WindowTitle(h)}» — посмотри глазами (look_screen).");
                    var fromStart = Flag("from_start", false);
                    var part = text.Length <= max ? text : fromStart ? text[..max] + "…" : "…" + text[^max..];
                    return new ToolResult($"Текст окна «{Uia.WindowTitle(h)}» ({text.Length} символов" +
                                          (text.Length > max ? $", показаны {(fromStart ? "первые" : "последние")} {max}" : "") + $"):\n{part}");
                }
                case "task_start" or "task_step" or "task_wait" or "task_note" or "task_done" or "task_cancel":
                {
                    if (box?.Tasks is not { } tb) return new ToolResult("❌ Долгие задачи работают только в живой Клаве (не в проверке --probe).");
                    return new ToolResult(call.Name switch
                    {
                        "task_start" => tb.Start(Arg("goal"),
                            args["steps"] is JsonArray st ? st.Select(x => x?.ToString() ?? "") : Arg("steps").Split('\n', ';'), ""),
                        "task_step" => tb.UpdateStep((int)Num("step"), Arg("status"), Arg("note"), Arg("text")),
                        "task_wait" => tb.Wait((int)Num("seconds"), Arg("reason")),
                        "task_note" => tb.Note(Arg("text")),
                        "task_done" => tb.Finish(Arg("summary"), cancelled: false),
                        _ => tb.Finish(Arg("reason"), cancelled: true),
                    });
                }
                default:
                    return new ToolResult($"Нет такого инструмента: {call.Name}.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or InvalidOperationException or System.ComponentModel.Win32Exception or HttpRequestException
                                       or JsonException or FormatException or System.Runtime.InteropServices.COMException or TimeoutException
                                       or TaskCanceledException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            // ошибка инструмента — мозгу текстом: он поймёт и попробует иначе, а разговор не рухнет
            Log.Error($"инструмент {call.Name}", ex);
            return new ToolResult($"Ошибка {call.Name}: {ex.Message}");
        }
    }

    /// <summary>Активное окно, но не окно самой Клавы (терминал, настройки): тогда ближайшее чужое под ним.</summary>
    private static IntPtr ForegroundNotMine()
    {
        var fg = Native.GetForegroundWindow();
        Native.GetWindowThreadProcessId(fg, out var pid);
        if (fg != IntPtr.Zero && pid != Environment.ProcessId) return fg;
        var w = Uia.ListWindows(5).FirstOrDefault();
        return w.Handle;
    }

    /// <summary>Убрать в Корзину (обратимо). Системные пути не даём трогать вовсе.</summary>
    private static async Task<ToolResult> RecycleAsync(JsonObject args, string baseDir, Func<ToolRequest, Task<bool>> approve, ToolBox? box)
    {
        var paths = (args["paths"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList()
            ?? (args["path"]?.GetValue<string>() is { Length: > 0 } one ? [one] : []);
        if (paths.Count == 0) return new ToolResult("Не указано, что убрать в корзину.");
        var full = paths.Select(p => ResolveUserPath(p, baseDir)).ToList();
        var bad = full.FirstOrDefault(p => SystemPathRx().IsMatch(p) || Path.GetPathRoot(p)?.TrimEnd('\\') == p.TrimEnd('\\'));
        if (bad is not null) return new ToolResult($"Не буду трогать системное или корень диска: {bad}. Это защита от беды.");
        var missing = full.Where(p => !File.Exists(p) && !Directory.Exists(p)).ToList();
        var exist = full.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (exist.Count == 0) return new ToolResult($"Ничего из этого не нашла на диске: {string.Join("; ", missing)}");
        var details = "В Корзину (можно вернуть):\r\n" + string.Join("\r\n", exist);
        if (!await ApproveOrAutoAsync(new ToolRequest("Убрать в Корзину", details, false, "Файлы уедут в корзину — вернуть можно, но проверь список.", "recycle"), approve, box)) return new ToolResult(Denied);
        // \0-разделённый список с двойным \0 на конце — так требует SHFileOperation
        var from = string.Join('\0', exist) + "\0\0";
        var op = new Native.SHFILEOPSTRUCT
        {
            wFunc = Native.FO_DELETE, pFrom = from,
            fFlags = (ushort)(Native.FOF_ALLOWUNDO | Native.FOF_NOCONFIRMATION | Native.FOF_NOERRORUI | Native.FOF_SILENT),
        };
        var rc = Native.SHFileOperationW(ref op);
        if (rc != 0) return new ToolResult($"Не получилось убрать в корзину (код {rc}). Может, файл занят программой.");
        if (op.fAnyOperationsAborted != 0) return new ToolResult("Часть не убралась — операцию прервали.");
        var msg = $"Убрала в корзину: {string.Join("; ", exist.Select(Path.GetFileName))}.";
        return new ToolResult(missing.Count > 0 ? msg + $" Не нашла: {string.Join("; ", missing)}" : msg);
    }

    /// <summary>Лапки на этот ответ: одно разрешение (на автопилоте — сразу) и дальше без вопросов, пока друг не забрал мышь.</summary>
    private static async Task<bool> EnsureHandsAsync(Func<ToolRequest, Task<bool>> approve, ToolBox box)
    {
        if (!box.HandsAllowed) return false;
        if (box.Hands.Active) return true;
        if (!await ApproveOrAutoAsync(new ToolRequest("Взять управление мышью и клавиатурой",
                "Клава сможет двигать курсор, кликать и печатать, пока не закончит.\r\n" +
                "Ты в любой момент забираешь управление: нажми Backspace (или Esc).", false,
                "Клава получит лапки до конца этого ответа.", "hands"), approve, box))
            return false;
        box.Hands.Begin();
        return true;
    }

    private static MouseButtons ButtonOf(string s) => s.ToLowerInvariant() switch
    {
        "right" or "правая" or "пкм" => MouseButtons.Right,
        "middle" or "средняя" => MouseButtons.Middle,
        _ => MouseButtons.Left,
    };

    /// <summary>Лапки: снимок экрана, поиск по имени/тексту и управление мышью/клавиатурой.</summary>
    private static async Task<ToolResult> RunHandsAsync(string name, JsonObject args, Func<string, string> Arg, Func<string, double> Num,
        Func<string, bool, bool> Flag, Func<ToolRequest, Task<bool>> approve, ToolBox box, KlavaConfig cfg, CancellationToken ct)
    {
        var h = box.Hands;
        if (name == "look_screen")
        {
            // area=window — только активное окно (меньше ужимается — резче); zoom=[x,y,w,h] по прошлому снимку —
            // приблизить кусок в полном разрешении (мелкий текст, иконки, клеточки игры)
            Rectangle? region = null;
            if (args["zoom"] is JsonArray z && z.Count >= 4 && h.Shot is not null)
            {
                var tl = h.ToScreen(z[0]!.GetValue<double>(), z[1]!.GetValue<double>());
                var br = h.ToScreen(z[0]!.GetValue<double>() + z[2]!.GetValue<double>(), z[1]!.GetValue<double>() + z[3]!.GetValue<double>());
                region = Rectangle.FromLTRB(tl.X, tl.Y, br.X, br.Y);
            }
            else if (Arg("area").Equals("window", StringComparison.OrdinalIgnoreCase) && ForegroundNotMine() is var fw && fw != IntPtr.Zero
                     && Native.GetWindowRect(fw, out var wr))
                region = Rectangle.FromLTRB(wr.Left, wr.Top, wr.Right, wr.Bottom);
            var shot = region is { } rg && box.CaptureRegion is { } cr ? await cr(rg) : await box.Capture();
            h.Shot = (shot.Bounds, shot.Scale);
            var w = (int)(shot.Bounds.Width * shot.Scale);
            var hgt = (int)(shot.Bounds.Height * shot.Scale);
            var fg = ForegroundNotMine();
            var ui = "";
            if (fg != IntPtr.Zero)
            {
                var b = shot.Bounds;
                var k = shot.Scale;
                var map = await Uia.DescribeAsync(fg, 45, TimeSpan.FromSeconds(2.5),
                    p => new Point((int)Math.Round((p.X - b.Left) * k), (int)Math.Round((p.Y - b.Top) * k)));
                ui = $"\nАктивное окно «{Uia.WindowTitle(fg)}». Его элементы (координаты — уже по снимку, можно сразу mouse_click; а надёжнее click_element по имени):\n{map}";
            }
            var ocr = shot.Ocr.Length > 0 ? "\nТекст с экрана: " + (shot.Ocr.Length > 1200 ? shot.Ocr[..1200] + "…" : shot.Ocr) : "";
            return new ToolResult($"Снимок {w}×{hgt} с сеткой: жёлтые подписи — координаты снимка (0,0 — левый верх). Координаты для mouse_* бери по сетке.{ui}{ocr}", shot.Jpeg);
        }

        // click_element: сначала без мыши (UI Automation Invoke / SetValue), лапки — только если элемент так не умеет
        Uia.Found? found = null;
        IntPtr win = IntPtr.Zero;
        if (name == "click_element")
        {
            win = string.IsNullOrWhiteSpace(Arg("window")) ? ForegroundNotMine() : Uia.FindWindow(Arg("window"));
            if (win == IntPtr.Zero) return new ToolResult($"Окно «{Arg("window")}» не нашла. Список окон: list_windows.");
            if (!string.IsNullOrWhiteSpace(Arg("window"))) { Uia.Focus(win); await Task.Delay(150, ct); }
            found = await Uia.FindElementAsync(win, Arg("name"), TimeSpan.FromSeconds(6));
            var bn = Arg("button").ToLowerInvariant();
            if (found is not null && Arg("text").Length > 0 && await Uia.SetValueAsync(found, Arg("text"), TimeSpan.FromSeconds(3)))
                return new ToolResult($"Вписала текст в «{found.Name}» ({found.Type}).");
            if (found is not null && bn is "" or "left" && !Flag("double", false) && Arg("text").Length == 0 && found.CanInvoke
                && await Uia.InvokeAsync(found, TimeSpan.FromSeconds(3)))
                return new ToolResult($"Нажала «{found.Name}» ({found.Type}) напрямую, без мыши.");
        }

        if (!await EnsureHandsAsync(approve, box))
            return new ToolResult(box.HandsAllowed ? Denied : "Мышь и клавиатуру в этом режиме не трогаю (проверка без рук).");
        if (h.Token.IsCancellationRequested) return new ToolResult("Друг забрал у меня управление — дальше не лезу.");
        try
        {
            switch (name)
            {
                case "click_element":
                {
                    var target = Arg("name");
                    var btnName = Arg("button").ToLowerInvariant();
                    var dbl = btnName is "double" or "двойной" || Flag("double", false);
                    var btn = ButtonOf(btnName);
                    var text = Arg("text");
                    if (found is null)
                    {
                        // программа без UI Automation — пробуем по надписи на экране
                        var byText = box.FindText is null ? [] : await box.FindText(target);
                        if (byText.Count == 0) return new ToolResult($"Не нашла «{target}» ни в элементах окна «{Uia.WindowTitle(win)}», ни в тексте на экране. Глянь look_screen или ui_elements.");
                        var c0 = Center(byText[0].Bounds);
                        await h.ClickAtAsync(c0, btn, dbl ? 2 : 1, h.Token);
                        if (text.Length > 0) { await Task.Delay(120, h.Token); await h.TypeAsync(text, h.Token); }
                        return new ToolResult($"Кликнула по надписи «{byText[0].Text}» (нашла распознаванием текста){(text.Length > 0 ? " и вписала текст" : "")}.");
                    }
                    if (text.Length > 0)
                    {
                        // поле не принимает текст через UI Automation — кликаем в него и печатаем лапками
                        await h.ClickAtAsync(Center(found.Bounds), MouseButtons.Left, 1, h.Token);
                        await Task.Delay(120, h.Token);
                        await h.TypeAsync(text, h.Token);
                        return new ToolResult($"Кликнула в «{found.Name}» и напечатала текст.");
                    }
                    await h.ClickAtAsync(Center(found.Bounds), btn, dbl ? 2 : 1, h.Token);
                    return new ToolResult($"Кликнула {(dbl ? "дважды " : "")}в центр «{found.Name}» ({found.Type}).");
                }
                case "click_text":
                {
                    if (box.FindText is null) return new ToolResult("Распознавание текста недоступно — используй look_screen и mouse_click.");
                    var target = Arg("text");
                    var matches = await box.FindText(target);
                    if (matches.Count == 0) return new ToolResult($"Надписи «{target}» на экране не видно (монитор под курсором). Проверь look_screen.");
                    var idx = Math.Clamp((int)Num("index") is > 0 and var i ? i - 1 : 0, 0, matches.Count - 1);
                    var hit = matches[idx];
                    var btnName = Arg("button").ToLowerInvariant();
                    var dbl = btnName is "double" or "двойной" || Flag("double", false);
                    await h.ClickAtAsync(Center(hit.Bounds), ButtonOf(btnName), dbl ? 2 : 1, h.Token);
                    var more = matches.Count > 1 ? $" (совпадений {matches.Count}, кликнула №{idx + 1})" : "";
                    return new ToolResult($"Кликнула по надписи «{hit.Text}»{more}.");
                }
                case "type_text":
                {
                    if (!string.IsNullOrWhiteSpace(Arg("window")))
                    {
                        var tw = Uia.FindWindow(Arg("window"));
                        if (tw == IntPtr.Zero) return new ToolResult($"Окна «{Arg("window")}» не нашла — ничего не печатала.");
                        Uia.Focus(tw);
                        await Task.Delay(200, h.Token);
                    }
                    var into = Native.GetForegroundWindow();
                    Native.GetWindowThreadProcessId(into, out var ipid);
                    if (ipid == Environment.ProcessId) return new ToolResult("Впереди окно самой Клавы — печатать некуда. Переключись на нужное окно (focus_window).");
                    await h.TypeAsync(Arg("text"), h.Token);
                    return new ToolResult($"Напечатала {Arg("text").Length} символов в окно «{Uia.WindowTitle(into)}».");
                }
                case "press_keys":
                    if (Hands.ParseCombo(Arg("combo")) is not { } keys) return new ToolResult($"Не поняла клавиши «{Arg("combo")}». Пример: ctrl+s, alt+tab, enter.");
                    await h.PressAsync(keys, h.Token);
                    return new ToolResult($"Нажала {Arg("combo")}.");
                case "scroll":
                    await h.ScrollAsync((int)Num("clicks"), h.Token);
                    return new ToolResult("Прокрутила.");
                case "paint":
                {
                    var r = await PaintTool.RunAsync(args, box, ct);
                    // снимок холста — не снимок экрана: координаты mouse_* по нему считать нельзя
                    if (r.Image is not null) box.Hands.Shot = null;
                    return r;
                }
                case "paint_art":
                {
                    var r = await PaintTool.ArtAsync(args, box, cfg, ct);
                    if (r.Image is not null) box.Hands.Shot = null;
                    return r;
                }
                case "game_action":
                    return await GameActionAsync(args, box, ct);
            }

            // дальше — по координатам снимка: без снимка нельзя
            if (h.Shot is null) return new ToolResult("Сначала посмотри на экран через look_screen — без снимка я не знаю координат. Или жми по имени: click_element / click_text.");
            switch (name)
            {
                case "mouse_move":
                    await h.MoveAsync(h.ToScreen(Num("x"), Num("y")), h.Token);
                    return new ToolResult("Подвела курсор.");
                case "mouse_click":
                {
                    var btn = ButtonOf(Arg("button"));
                    var dbl = Flag("double", false);
                    await h.ClickAtAsync(h.ToScreen(Num("x"), Num("y")), btn, dbl ? 2 : 1, h.Token);
                    return new ToolResult($"Кликнула {(dbl ? "дважды " : "")}в ({Num("x"):F0},{Num("y"):F0}). Проверь результат look_screen, если важно.");
                }
                case "mouse_drag":
                    await h.DragAsync(h.ToScreen(Num("from_x"), Num("from_y")), h.ToScreen(Num("to_x"), Num("to_y")), h.Token);
                    return new ToolResult("Перетащила.");
                case "draw_stroke":
                {
                    var btn = ButtonOf(Arg("button"));
                    var delay = (int)Math.Clamp(Num("delay_ms") is > 0 and var d ? d : 12, 1, 100);
                    var pts = new List<Point>();
                    var raw = args["points"] is JsonArray direct ? direct : JsonNode.Parse(Arg("points")) as JsonArray;
                    foreach (var it in raw ?? [])
                    {
                        if (it is JsonObject obj && obj["x"] is { } xs && obj["y"] is { } ys)
                            pts.Add(h.ToScreen(xs.GetValue<double>(), ys.GetValue<double>()));
                        else if (it is JsonArray pair && pair.Count >= 2)
                            pts.Add(h.ToScreen(pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>()));
                    }
                    if (pts.Count == 0) return new ToolResult("Нет точек для рисования: points = [{\"x\":..,\"y\":..}, …].");
                    // между далёкими точками добавляем промежуточные — Paint иначе рисует пунктир
                    var dense = new List<Point> { pts[0] };
                    for (var i = 1; i < pts.Count; i++)
                    {
                        var (a, b) = (pts[i - 1], pts[i]);
                        var steps = Math.Max(1, (int)(Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2)) / 6));
                        for (var s = 1; s <= steps; s++) dense.Add(new Point(a.X + (b.X - a.X) * s / steps, a.Y + (b.Y - a.Y) * s / steps));
                    }
                    await h.DrawStrokeAsync(dense, btn, Math.Max(1, delay / 3), h.Token);
                    return new ToolResult($"Нарисовала линию по {pts.Count} точкам.");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new ToolResult("Друг забрал у меня управление — остановилась."); }
        return new ToolResult("Не знаю такого действия лапками.");
    }

    private static Point Center(Rectangle r) => new(r.Left + r.Width / 2, r.Top + r.Height / 2);

    /// <summary>
    /// Игровые лапки — универсальные клавиатура и мышь для любой игры, как у живого игрока (без консолей, скриптов и
    /// инъекций): клавиши скан-кодами (их видят SDL/DirectInput/Raw Input), несколько сразу (W+Shift), зажать и держать
    /// между ходами (down/up — бежать, пока думает), поворот камеры относительной мышью, кнопки мыши с удержанием,
    /// серия шагов за один вызов и снимок окна игры в конце. Перед каждым шагом — проверка, что впереди именно игра:
    /// в чужое окно (чат друга) не уходит ни одной буквы.
    /// </summary>
    private static async Task<ToolResult> GameActionAsync(JsonObject args, ToolBox box, CancellationToken ct)
    {
        var h = box.Hands;
        var win = args["game_window"]?.ToString() ?? "";
        var target = string.IsNullOrWhiteSpace(win) ? Native.GetForegroundWindow() : Uia.FindWindow(win);
        if (target == IntPtr.Zero) return new ToolResult($"Окна игры «{win}» не нашла — list_windows покажет, как оно называется.");
        var pid = Hands.PidOf(target);
        if (pid == Environment.ProcessId) return new ToolResult("Впереди окно самой Клавы — укажи game_window (окно игры из list_windows).");
        var title = Uia.WindowTitle(target);
        if (Hands.BlockedByElevation(pid))
            return new ToolResult($"Не удалось: игра «{title}» запущена от имени администратора, а я — нет, и Windows не пропускает в неё ни клавиши, ни мышь. " +
                "Скажи другу: снять у игры галку «Запускать от имени администратора» (Свойства ярлыка → Совместимость) или запустить меня от администратора.");
        if (!Hands.InFront(pid))
        {
            Uia.Focus(target);
            await Task.Delay(200, h.Token);
            if (!Hands.InFront(pid))
                return new ToolResult($"Не удалось вывести окно «{title}» вперёд — Windows не отдал фокус (друг сейчас в другом окне). Ничего не нажала. Попроси друга щёлкнуть по игре.");
        }
        var game = GameNotesKey(pid, title);
        GameMode.Touch("paws", TimeSpan.FromSeconds(90));   // играет лапками — сидит за игровым столом, пока идут подходы
        if (args["lesson"]?.ToString() is { Length: > 0 } lesson) AddLesson(game, lesson);
        var isCs = ProcessOf(pid) == "hl";
        if (isCs) LastPawsInCs = DateTime.UtcNow;
        var deadBefore = CsBridge.HostDiedAt;
        var clipScope = Hands.ClipToWindow(target);
        var log = new List<string>();
        var lost = false;
        try
        {
            // одна команда или длинная цепочка: steps:[{action, keys, duration_ms, dx, dy, button}, …], repeat — прогнать
            // цепочку N раз. Предел — 3 минуты на ход; друг обрывает в любой момент (Esc, мышь, «стоп»).
            var steps = args["steps"] is JsonArray arr ? arr.OfType<JsonObject>().Take(200).ToList() : [args];
            var repeat = (int)Math.Clamp(double.TryParse(args["repeat"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var rp) ? rp : 1, 1, 100);
            var plan = Enumerable.Repeat(steps, repeat).SelectMany(s => s).ToList();
            var chainClock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var st in plan)
            {
                h.Token.ThrowIfCancellationRequested();
                if (chainClock.Elapsed > TimeSpan.FromMinutes(3)) { log.Add("3 минуты хода вышли — остановилась, посмотри и продолжай следующим ходом"); break; }
                if (isCs && CsBridge.HostDiedAt != deadBefore && CsBridge.HostDiedAt != DateTime.MinValue)
                {
                    h.PadReleaseAll("твоего игрока убили");
                    log.Add("тебя убили посреди хода — отпустила всё и остановилась");
                    break;
                }
                if (!Hands.InFront(pid))
                {
                    lost = true;
                    h.PadReleaseAll("окно игры ушло с экрана");
                    log.Add("окно игры ушло с экрана — остановилась, дальше не нажимала");
                    break;
                }
                var action = (st["action"]?.ToString() ?? "tap").Trim().ToLowerInvariant();
                var keysText = st["keys"]?.ToString() ?? st["key"]?.ToString() ?? "";
                var ms = (int)(double.TryParse(st["duration_ms"]?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0);
                var buttonText = (st["button"]?.ToString() ?? "").Trim().ToLowerInvariant();
                var button = buttonText switch { "right" or "правая" or "пкм" => MouseButtons.Right, "middle" => MouseButtons.Middle, _ => MouseButtons.Left };
                if (keysText is "left_click" or "click") (action, button) = (action is "hold" or "hold_key" ? "hold_mouse" : "click", MouseButtons.Left);
                if (keysText is "right_click") (action, button) = (action is "hold" or "hold_key" ? "hold_mouse" : "click", MouseButtons.Right);
                switch (action)
                {
                    case "hold" or "hold_key" or "tap" or "tap_key" or "press":
                    {
                        var keys = Hands.ParseCombo(keysText) ?? (Hands.ResolveKey(keysText) is { } one ? [one] : null);
                        if (keys is null) { log.Add($"не знаю клавиши «{keysText}»"); continue; }
                        var hold = action.StartsWith("hold") ? (ms > 0 ? ms : 1000) : 60;
                        await h.GameHoldAsync(keys, hold, h.Token);
                        log.Add(action.StartsWith("hold") ? $"держала {keysText} {hold} мс" : $"нажала {keysText}");
                        break;
                    }
                    case "down" or "key_down" or "press_down" or "hold_on":
                    {
                        var keys = Hands.ParseCombo(keysText) ?? (Hands.ResolveKey(keysText) is { } one ? [one] : null);
                        if (keys is null) { log.Add($"не знаю клавиши «{keysText}»"); continue; }
                        if (!h.PadDown(pid, keys, ms > 0 ? ms : 30000)) { lost = true; log.Add("окно игры не впереди — не зажала"); break; }
                        log.Add($"зажала {keysText} и держу");
                        break;
                    }
                    case "up" or "key_up" or "release":
                    {
                        var keys = string.IsNullOrWhiteSpace(keysText) ? null : Hands.ParseCombo(keysText) ?? (Hands.ResolveKey(keysText) is { } one ? [one] : null);
                        if (keys is null && !string.IsNullOrWhiteSpace(keysText)) { log.Add($"не знаю клавиши «{keysText}»"); continue; }
                        h.PadUp(keys);
                        log.Add(keys is null ? "отпустила все клавиши" : $"отпустила {keysText}");
                        break;
                    }
                    case "mouse_down" or "fire_on":
                        if (!h.PadMouseDown(pid, button, ms > 0 ? ms : 30000)) { lost = true; log.Add("окно игры не впереди — кнопку не зажала"); break; }
                        log.Add($"зажала {(button == MouseButtons.Right ? "ПКМ" : "ЛКМ")} и держу");
                        break;
                    case "mouse_up" or "fire_off":
                        h.PadMouseUp(buttonText.Length == 0 ? null : button);
                        log.Add(buttonText.Length == 0 ? "отпустила кнопки мыши" : $"отпустила {(button == MouseButtons.Right ? "ПКМ" : "ЛКМ")}");
                        break;
                    case "release_all" or "stop":
                        h.PadReleaseAll("сама отпустила");
                        log.Add("отпустила всё");
                        break;
                    case "look" or "turn" or "camera":
                    {
                        var dx = (int)(st["dx"]?.GetValue<double>() ?? 0);
                        var dy = (int)(st["dy"]?.GetValue<double>() ?? 0);
                        await h.LookAsync(dx, dy, ms > 0 ? ms : 200, h.Token);
                        log.Add($"повернула камеру на {dx},{dy}");
                        break;
                    }
                    case "click":
                        await h.MouseHoldAsync(button, 50, h.Token);
                        log.Add(button == MouseButtons.Right ? "клик ПКМ" : "клик ЛКМ");
                        break;
                    case "hold_mouse" or "mouse_hold":
                        await h.MouseHoldAsync(button, ms > 0 ? ms : 1000, h.Token);
                        log.Add($"держала {(button == MouseButtons.Right ? "ПКМ" : "ЛКМ")} {(ms > 0 ? ms : 1000)} мс");
                        break;
                    case "wait":
                    {
                        var unstuck = await WaitWatchingAsync(h, pid, target, Math.Clamp(ms > 0 ? ms : 500, 20, 10000));
                        log.Add(unstuck > 0 ? $"ждала {ms} мс — упиралась в стену {unstuck} раз, отходила и поворачивала" : $"ждала {ms} мс");
                        break;
                    }
                    default:
                        log.Add($"не знаю действия «{action}»");
                        break;
                }
                if (lost) break;
            }
        }
        finally
        {
            clipScope?.Dispose();
        }
        var held = h.PadHeld;
        // длинная цепочка — в ответ мозгу не сотни строк, а начало, конец и сколько всего
        var shown = log.Count <= 14 ? log : [.. log.Take(8), $"… ещё {log.Count - 12} шагов …", .. log.TakeLast(4)];
        var state = isCs && CsBridge.HostState() is { } cs ? " " + cs : "";
        var text = (lost ? "Не удалось доиграть ход: " : "🎮 ") + string.Join("; ", shown) + "." + (held.Length > 0 ? " " + held : "") + state + NotesOnce(game);
        // снимок окна игры сразу после хода — мозг видит результат без отдельного look_screen
        var observe = args["observe"]?.GetValue<bool>() ?? true;
        if (observe && target != IntPtr.Zero && box.CaptureRegion is not null && Native.GetWindowRect(target, out var r))
        {
            var shot = await box.CaptureRegion(new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            h.Shot = (shot.Bounds, shot.Scale);
            return new ToolResult(text + " Снимок окна игры после хода приложен.", shot.Jpeg);
        }
        return new ToolResult(text);
    }

    /// <summary>
    /// Живая оболочка (и набор лапками в кибер-терминал). На автопилоте кодит молча и опасное выполняет само; катастрофы
    /// отрезаются всегда, безвозвратное удаление вне временного — в Корзину. Без автопилота — одно подтверждение на весь
    /// разговор и отдельное на каждую опасную команду.
    /// </summary>
    private static async Task<ToolResult> ShellRunAsync(string command, double timeoutSec, Func<ToolRequest, Task<bool>> approve,
        KlavaConfig cfg, ToolBox? box, bool typed, CancellationToken ct)
    {
        if (box is null) return new ToolResult("Живого терминала нет в этом режиме. Попробуй run_powershell для одной команды.");
        if (string.IsNullOrWhiteSpace(command)) return new ToolResult("Пустая команда.");
        var m = DangerCmdRx().Match(command);
        var danger = m.Success || SystemPathRx().IsMatch(command);
        if (CatastropheRx().IsMatch(command))
        {
            var why = $"Катастрофическая команда: {CatastropheRx().Match(command).Value}";
            box.Shell.NotifyBlocked(command, why);
            Log.Write($"оболочка [БЛОКИРОВКА]: {why} — «{command}»");
            return new ToolResult($"❌ [Защита Клавы]: {why}. Такое не выполняю ни на каком режиме.");
        }
        if (!box.ShellApproved)
        {
            if (!box.Autopilot)
            {
                var start = $"Клава хочет открыть PowerShell на весь разговор и начать с:\r\n\r\n{command}\r\n\r\n" +
                            "После «Разрешить» её последующие команды в терминале пойдут БЕЗ подтверждения. " +
                            "Опасные команды (удаление, реестр, службы) всё равно будут спрашивать отдельно.";
                if (!await approve(new ToolRequest("Взять терминал на весь разговор", start, true, danger ? $"Начинает с опасной команды: {m.Value}" : null, "shell")))
                    return new ToolResult(Denied);
            }
            box.ShellApproved = true;
        }
        // автопилот (друг 30.09: «терминал почти каждую считает опасной, а мне автопилот нужен»): выполняем сами —
        // катастрофы отрезаны выше, безвозвратное удаление вне временного уходит в Корзину (recycle), не в окно
        if (box.Autopilot && IsHardDelete(command))
        {
            const string why = "безвозвратное удаление — только в Корзину";
            box.Shell.NotifyBlocked(command, why);
            return new ToolResult($"❌ [Защита Клавы]: {why}. Удаляй инструментом recycle (файлы и папки уходят в Корзину) и продолжай.");
        }
        if (danger && !box.Autopilot)
        {
            var reason = m.Success ? $"Опасная команда: {m.Value}" : "Затрагивает системные папки.";
            if (!await approve(new ToolRequest("Опасная команда в терминале", command, true, reason, "danger")))
            {
                box.Shell.NotifyBlocked(command, reason + " — друг не разрешил");
                return new ToolResult(Denied);
            }
        }
        else if (danger) Log.Write($"автопилот: опасная команда без окна — {(m.Success ? m.Value : "системная папка")}: {command.Split('\n')[0]}");
        if (!box.Shell.IsOpen) await box.Shell.StartAsync(box.WorkDir.Length > 0 ? box.WorkDir : cfg.Abs("workspace"));
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec > 0 ? timeoutSec : 300, 30, 1800));
        ShellResult r;
        if (typed && box.TypeInTerminal is { } type) r = await type(command, timeout, ct);
        else
        {
            box.ShowTerminal?.Invoke();   // друг видит, что она делает в терминале
            r = await box.Shell.RunAsync(command, timeout, ct);
        }
        var head = $"[папка {r.Cwd}] код выхода {r.ExitCode}{(r.Aborted ? " (прервано)" : "")}\n";
        var body = r.Output.Length > 8000 ? r.Output[..4000] + $"\n…(середина обрезана, всего {r.Output.Length} символов)…\n" + r.Output[^3500..] : r.Output;
        return new ToolResult(head + (body.Trim().Length > 0 ? body : "(пустой вывод)"));
    }

    /// <summary>
    /// Отстукнуть маляву соседу: найти видимое окно по подстроке заголовка, поднять его на передний план,
    /// напечатать текст посимвольно (без буфера обмена) и (по желанию) нажать Enter.
    /// </summary>
    private static async Task<ToolResult> SendChatAsync(string app, string text, bool send, Func<ToolRequest, Task<bool>> approve, ToolBox? box)
    {
        if (string.IsNullOrWhiteSpace(app)) return new ToolResult("Не сказано, куда стучаться (например: клод, chatgpt, deepseek).");
        if (string.IsNullOrEmpty(text)) return new ToolResult("Пустая малява.");
        var hWnd = Uia.FindWindow(app);
        if (hWnd == IntPtr.Zero) return new ToolResult($"Окно с «{app}» в заголовке не открыто. Скажи другу открыть его — и я отстукну.");
        var title = Uia.WindowTitle(hWnd);
        var details = $"→ «{title}»\r\n\r\n{text}";
        if (!await ApproveOrAutoAsync(new ToolRequest($"Написать в {app}", details, false, null, "open"), approve, box)) return new ToolResult(Denied);
        Uia.Focus(hWnd);
        await Task.Delay(220);   // окну надо время принять фокус и открыть строку ввода
        if (Native.GetForegroundWindow() != hWnd)
        {
            await Task.Delay(300);
            Uia.Focus(hWnd);
            await Task.Delay(200);
            if (Native.GetForegroundWindow() != hWnd) return new ToolResult($"Windows не дала поднять окно «{title}» — кликни в него сам, и я отстукну ещё раз.");
        }
        TypeUnicode(text);
        if (send)
        {
            await Task.Delay(120);
            SendCombo(Keys.Enter);
        }
        return new ToolResult($"Отстукнула в «{title}»{(send ? "" : " (без Enter, друг сам отправит)")}.");
    }

    /// <summary>Набрать строку в активное окно посимвольно через SendInput/UNICODE — работает с любой раскладкой.</summary>
    private static void TypeUnicode(string text)
    {
        var inputs = new List<Native.INPUT>(text.Length * 2);
        foreach (var ch in text)
        {
            if (ch == '\r') continue;
            if (ch == '\n')
            {
                // Shift+Enter — перенос строки в чатах, а не отправка
                inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = 0x10 } } });
                inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = (ushort)Keys.Enter } } });
                inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = (ushort)Keys.Enter, dwFlags = Native.KEYEVENTF_KEYUP } } });
                inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = 0x10, dwFlags = Native.KEYEVENTF_KEYUP } } });
                continue;
            }
            inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wScan = ch, dwFlags = Native.KEYEVENTF_UNICODE } } });
            inputs.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wScan = ch, dwFlags = Native.KEYEVENTF_UNICODE | Native.KEYEVENTF_KEYUP } } });
        }
        // некоторые чаты жуют мгновенный поток и теряют символы: шлём порциями по 40 символов
        var size = System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>();
        for (var off = 0; off < inputs.Count; off += 80)
        {
            var take = Math.Min(80, inputs.Count - off);
            var chunk = new Native.INPUT[take];
            inputs.CopyTo(off, chunk, 0, take);
            Native.SendInput((uint)take, chunk, size);
            Thread.Sleep(8);
        }
    }

    /// <summary>Нажать сочетание клавиш через SendInput (Enter и т. п.).</summary>
    private static void SendCombo(params Keys[] keys)
    {
        var down = new List<Native.INPUT>();
        var up = new List<Native.INPUT>();
        foreach (var k in keys)
        {
            down.Add(new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = (ushort)k } } });
            up.Insert(0, new Native.INPUT { type = Native.INPUT_KEYBOARD, u = new Native.INPUTUNION { ki = new Native.KEYBDINPUT { wVk = (ushort)k, dwFlags = Native.KEYEVENTF_KEYUP } } });
        }
        var arr = down.Concat(up).ToArray();
        Native.SendInput((uint)arr.Length, arr, System.Runtime.InteropServices.Marshal.SizeOf<Native.INPUT>());
    }

    /// <summary>Открыть встроенный кибер-терминал Клавы — видимо на экране, в нужной папке.</summary>
    private static async Task<ToolResult> OpenTerminalAsync(string path, KlavaConfig cfg, string baseDir, Func<ToolRequest, Task<bool>> approve, ToolBox? box)
    {
        var dir = string.IsNullOrWhiteSpace(path) ? (box?.Shell.Cwd is { Length: > 0 } c ? c : cfg.Abs("workspace")) : ResolveUserPath(path, baseDir);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        if (!await ApproveOrAutoAsync(new ToolRequest("Открыть кибер-терминал", $"Встроенный терминал Клавы в: {dir}", false, null, "open"), approve, box)) return new ToolResult(Denied);
        if (box?.ShowTerminal is { } show)
        {
            show();
            if (!box.Shell.IsOpen) await box.Shell.StartAsync(dir);
            else if (!string.Equals(box.Shell.Cwd, dir, StringComparison.OrdinalIgnoreCase))
                await box.Shell.RunAsync($"Set-Location -LiteralPath '{dir.Replace("'", "''")}'", TimeSpan.FromSeconds(30));
            box.ShellApproved = box.ShellApproved || box.Autopilot;
            return new ToolResult($"Открыла свой кибер-терминал на экране (папка: {dir}). Команды выполняй через shell или terminal_type — они пойдут в него.");
        }
        try { Process.Start(new ProcessStartInfo("wt.exe") { UseShellExecute = true, ArgumentList = { "-d", dir } }); }
        catch (System.ComponentModel.Win32Exception) { Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = dir, UseShellExecute = true }); }
        return new ToolResult($"Открыла терминал в {dir}.");
    }

    // простые имена программ → что запускать (App Execution Alias или протокол)
    private static readonly Dictionary<string, string> AppAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["paint"] = "mspaint.exe", ["пейнт"] = "mspaint.exe", ["паинт"] = "mspaint.exe", ["mspaint"] = "mspaint.exe", ["рисовалка"] = "mspaint.exe",
        ["notepad"] = "notepad.exe", ["блокнот"] = "notepad.exe", ["нотепад"] = "notepad.exe",
        ["calc"] = "calc.exe", ["calculator"] = "calc.exe", ["калькулятор"] = "calc.exe",
        ["explorer"] = "explorer.exe", ["проводник"] = "explorer.exe",
        ["terminal"] = "wt.exe", ["терминал"] = "wt.exe", ["windows terminal"] = "wt.exe",
        ["cmd"] = "cmd.exe", ["powershell"] = "powershell.exe", ["pwsh"] = "pwsh.exe",
        ["диспетчер задач"] = "taskmgr.exe", ["taskmgr"] = "taskmgr.exe",
        ["параметры"] = "ms-settings:", ["настройки"] = "ms-settings:", ["settings"] = "ms-settings:",
        ["steam"] = "steam://open/main", ["стим"] = "steam://open/main",
        ["vscode"] = "code", ["vs code"] = "code", ["код"] = "code",
        ["браузер"] = "https://www.google.com", ["browser"] = "https://www.google.com",
        ["ножницы"] = "ms-screenclip:", ["snipping tool"] = "ms-screenclip:",
    };

    /// <summary>
    /// Что на самом деле запускать: алиас, существующий путь (с учётом «desktop\…»), exe из PATH / App Paths,
    /// ярлык из меню Пуск или с Рабочего стола по имени. null — ничего не нашла.
    /// </summary>
    internal static string? ResolveLaunchTarget(string target, string baseDir)
    {
        var t = target.Trim().Trim('"');
        if (AppAliases.TryGetValue(t, out var alias)) t = alias;
        if (IsUrlLike(t)) return t;   // URL или протокол (steam://, ms-settings:)
        if (!t.Contains('\\') && !t.Contains('/'))
        {
            var exe = t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? t : t + ".exe";
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
                         .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps")))
            {
                try { var p = Path.Combine(dir.Trim(), exe); if (File.Exists(p)) return p; } catch (ArgumentException) { }
            }
            foreach (var hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
            {
                using var k = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}");
                if (k?.GetValue(null) is string ap && File.Exists(ap.Trim('"'))) return ap.Trim('"');
            }
        }
        string full;
        try { full = ResolveUserPath(t, baseDir); } catch (ArgumentException) { full = t; }
        if (File.Exists(full) || Directory.Exists(full)) return full;
        if (FindShortcutByName(Path.GetFileNameWithoutExtension(t)) is { } lnk) return lnk;
        // похоже на домен (youtube.com, ya.ru) и это не имя файла программы — сайт
        if (Regex.IsMatch(t, @"^(www\.)?[\w\-]+(\.[\w\-]+)*\.[a-zа-я]{2,24}(/\S*)?$", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(t, @"\.(exe|bat|cmd|lnk|msi|ps1|txt|png|jpe?g|dll|json|md|py|cs|js)$", RegexOptions.IgnoreCase))
            return "https://" + t;
        return null;
    }

    /// <summary>Адрес с протоколом (https://, steam://, ms-settings:), но не путь с буквой диска.</summary>
    private static bool IsUrlLike(string s) =>
        Regex.IsMatch(s, @"^[a-z][a-z0-9+.\-]+:", RegexOptions.IgnoreCase) && !Regex.IsMatch(s, @"^[a-z]:[\\/]", RegexOptions.IgnoreCase);

    /// <summary>Ярлык/exe по имени на Рабочем столе, в «Игры» и в меню Пуск (своём и общем).</summary>
    private static string? FindShortcutByName(string name)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var dirs = new[]
        {
            desktop, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.Combine(desktop, "Игры"), Path.Combine(desktop, "Games"),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
        };
        string? best = null;
        var bestScore = int.MaxValue;
        foreach (var d in dirs.Where(Directory.Exists))
        {
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(d, "*.*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, MaxRecursionDepth = 4 }); }
            catch (UnauthorizedAccessException) { continue; }
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is not (".lnk" or ".exe" or ".url")) continue;
                var fn = Path.GetFileNameWithoutExtension(f);
                if (fn.Contains("uninstall", StringComparison.OrdinalIgnoreCase) || fn.Contains("удал", StringComparison.OrdinalIgnoreCase)) continue;
                var i = fn.IndexOf(name, StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;
                var score = (fn.Length - name.Length) + (i == 0 ? 0 : 5);
                if (score < bestScore) { bestScore = score; best = f; }
            }
        }
        return best;
    }

    /// <summary>Запустить программу или открыть файл через оболочку Windows (как двойной клик).</summary>
    private static async Task<ToolResult> OpenAppAsync(string target, string args, string baseDir, Func<ToolRequest, Task<bool>> approve, ToolBox? box)
    {
        if (string.IsNullOrWhiteSpace(target)) return new ToolResult("Не сказано, что открыть.");
        var resolved = ResolveLaunchTarget(target, baseDir) ?? target;
        var details = args.Length > 0 ? $"{resolved} {args}" : resolved;
        var sys = SystemPathRx().IsMatch(resolved) && !resolved.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || SystemPathRx().IsMatch(args);
        if (!await ApproveOrAutoAsync(new ToolRequest("Открыть", details, sys, sys ? "Целевой файл или аргумент в системной папке." : null, "open"), approve, box)) return new ToolResult(Denied);
        var before = Uia.ListWindows(200).Select(w => w.Handle).ToHashSet();
        try
        {
            var psi = new ProcessStartInfo(resolved) { UseShellExecute = true };
            if (args.Length > 0) psi.Arguments = args;
            if (File.Exists(resolved)) psi.WorkingDirectory = Path.GetDirectoryName(resolved)!;
            Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        { return new ToolResult($"Не смогла открыть «{target}» ({resolved}): {ex.Message}. Уточни путь или имя программы."); }
        return new ToolResult($"Открыла: {details}. {await BringNewWindowAsync(before)}");
    }

    /// <summary>
    /// Дождаться окна, которое открылось после запуска, и вывести его вперёд. Без этого печать уходила в то окно,
    /// что было активным раньше (блокнот Windows 11 открылся вкладкой — текст напечатался в чужой файл).
    /// Возвращает, какое окно теперь впереди — мозг обязан это учесть перед type_text.
    /// </summary>
    private static async Task<string> BringNewWindowAsync(HashSet<IntPtr> before)
    {
        for (var i = 0; i < 25; i++)
        {
            await Task.Delay(160);
            var fresh = Uia.ListWindows(200).FirstOrDefault(w => !before.Contains(w.Handle));
            if (fresh.Handle != IntPtr.Zero)
            {
                await Task.Delay(250);   // окну нужно дорисоваться и принять ввод
                Uia.Focus(fresh.Handle);
                return $"Появилось окно «{Uia.WindowTitle(fresh.Handle)}» ({fresh.Process}) — оно впереди.";
            }
        }
        var fg = Native.GetForegroundWindow();
        return fg == IntPtr.Zero ? "Нового окна не видно." :
            $"Нового окна не появилось (могло открыться вкладкой). Сейчас впереди «{Uia.WindowTitle(fg)}» — прежде чем печатать, проверь, что это нужное окно/вкладка (look_screen).";
    }

    private const string Denied = "Друг НЕ разрешил это действие. Не повторяй его и не пытайся обойти другим способом — спроси, как лучше.";

    private static async Task<string> RunPowerShellAsync(string command, string workDir, CancellationToken ct)
    {
        if (!Directory.Exists(workDir)) workDir = Path.GetTempPath();
        var psi = new ProcessStartInfo(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe")) ? "pwsh" : "powershell")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
                     "[Console]::OutputEncoding=[Text.Encoding]::UTF8; " + command })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("pwsh не запустился");
        var outTask = p.StandardOutput.ReadToEndAsync(ct);
        var errTask = p.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return ct.IsCancellationRequested ? "Отменено." : "Команда не уложилась в 60 секунд и была остановлена. Для долгого — shell.";
        }
        var output = (await outTask) + (await errTask is { Length: > 0 } err ? "\n[ошибки]\n" + err : "");
        if (output.Length > 6000) output = output[..6000] + $"\n…(обрезано, всего {output.Length} символов)";
        return $"Код выхода {p.ExitCode}.\n{(output.Trim().Length > 0 ? output : "(пустой вывод)")}";
    }

    private const string BrowserUa = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";

    /// <summary>GET страницы как браузер: через прокси, если он нужен (автонастройка NetProxy).</summary>
    private static async Task<string> GetPageAsync(string url, CancellationToken ct)
    {
        var client = NetProxy.ClientFor(url, "auto", TimeSpan.FromSeconds(25));
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.ParseAdd(BrowserUa);
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        req.Headers.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
        using var resp = await client.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        var charset = resp.Content.Headers.ContentType?.CharSet?.Trim('"');
        Encoding enc;
        try { enc = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch (ArgumentException) { enc = Encoding.UTF8; }
        return enc.GetString(bytes);
    }

    /// <summary>Поиск: DuckDuckGo (HTML), если пусто/капча — Bing.</summary>
    private static async Task<ToolResult> WebSearchAsync(string query, int count, Func<ToolRequest, Task<bool>> approve, ToolBox? box, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return new ToolResult("Пустой поисковый запрос.");
        count = Math.Clamp(count <= 0 ? 5 : count, 1, 10);
        // поиск — в фоне и без спроса (друг 01.10: «пусть гуглит в фоне, без табличек и разрешений — рид онли»):
        // ни одного окна не открывается, на компьютере ничего не меняется
        Log.Write($"поиск в фоне: {query}");

        var results = new List<(string Title, string Url, string Snippet)>();
        var errors = new List<string>();
        try
        {
            var html = await GetPageAsync($"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}&kl=ru-ru", ct);
            var titleRx = new Regex(@"<a[^>]*class=""result__a""[^>]*href=""(?<url>[^""]+)""[^>]*>(?<title>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var snipRx = new Regex(@"<a[^>]*class=""result__snippet""[^>]*>(?<snip>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            var titles = titleRx.Matches(html);
            var snips = snipRx.Matches(html);
            for (var i = 0; i < titles.Count && results.Count < count; i++)
            {
                var url = ExtractDuckUrl(titles[i].Groups["url"].Value);
                var title = StripHtml(titles[i].Groups["title"].Value).Trim();
                if (url.Contains("duckduckgo.com/y.js", StringComparison.Ordinal) || url.Length == 0 || title.Length == 0) continue;   // реклама
                results.Add((title, url, i < snips.Count ? StripHtml(snips[i].Groups["snip"].Value).Trim() : ""));
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { errors.Add($"DuckDuckGo: {ex.Message}"); }

        if (results.Count == 0)
        {
            try
            {
                var html = await GetPageAsync($"https://www.bing.com/search?q={Uri.EscapeDataString(query)}&setlang=ru&cc=RU", ct);
                var blockRx = new Regex(@"<li class=""b_algo""[^>]*>(.*?)</li>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var aRx = new Regex(@"<h2[^>]*>\s*<a[^>]*href=""(?<url>https?://[^""]+)""[^>]*>(?<title>.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                var pRx = new Regex(@"<p[^>]*>(?<snip>.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                foreach (Match b in blockRx.Matches(html))
                {
                    if (results.Count >= count) break;
                    var a = aRx.Match(b.Value);
                    if (!a.Success) continue;
                    var p = pRx.Match(b.Value);
                    results.Add((StripHtml(a.Groups["title"].Value).Trim(), WebUtility.HtmlDecode(a.Groups["url"].Value), p.Success ? StripHtml(p.Groups["snip"].Value).Trim() : ""));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { errors.Add($"Bing: {ex.Message}"); }
        }

        if (results.Count == 0)
            return new ToolResult($"По запросу «{query}» ничего не нашла{(errors.Count > 0 ? " (" + string.Join("; ", errors) + ")" : "")}. Можно открыть поиск в браузере: browser_open.");
        var sb = new StringBuilder($"Нашла по запросу «{query}»:\n");
        for (var i = 0; i < results.Count; i++)
        {
            var (t, u, s) = results[i];
            sb.AppendLine($"{i + 1}. {t}\n   {u}");
            if (!string.IsNullOrWhiteSpace(s)) sb.AppendLine($"   {s}");
        }
        return new ToolResult(sb.ToString().TrimEnd());
    }

    private static string ExtractDuckUrl(string rawUrl)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return "";
        rawUrl = WebUtility.HtmlDecode(rawUrl);
        var idx = rawUrl.IndexOf("uddg=", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var sub = rawUrl[(idx + 5)..];
            var amp = sub.IndexOf('&');
            if (amp >= 0) sub = sub[..amp];
            return WebUtility.UrlDecode(sub);
        }
        return rawUrl.StartsWith("//", StringComparison.Ordinal) ? "https:" + rawUrl : rawUrl;
    }

    /// <summary>Чтение веб-страницы по URL и очистка от HTML/скриптов.</summary>
    /// <summary>Адрес в интернете (а не этот компьютер и не домашняя сеть).</summary>
    internal static bool IsPublicHost(Uri uri)
    {
        var host = uri.Host;
        if (uri.IsLoopback || host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase) || !host.Contains('.'))
            return false;
        if (!System.Net.IPAddress.TryParse(host, out var ip)) return true;
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6) return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal);
        var b = ip.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 169 && b[1] == 254);
    }

    private static async Task<ToolResult> WebFetchAsync(string url, Func<ToolRequest, Task<bool>> approve, ToolBox? box, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url)) return new ToolResult("URL не указан.");
        if (!url.Contains("://", StringComparison.Ordinal)) url = "https://" + url.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return new ToolResult("Некорректный URL (нужен http:// или https://).");
        // чтение страницы из интернета — тоже в фоне и без спроса; адреса своей сети и самого компьютера — как раньше,
        // с разрешением (там бывают не страницы, а управление: роутер, локальные сервисы)
        if (!IsPublicHost(uri))
        {
            if (!await ApproveOrAutoAsync(new ToolRequest("Загрузка страницы из локальной сети", url, false, null, "web"), approve, box))
                return new ToolResult(Denied);
        }
        else Log.Write($"чтение страницы в фоне: {url}");
        try
        {
            var text = StripHtml(await GetPageAsync(uri.ToString(), ct));
            if (text.Length > 8000) text = text[..8000] + $"\n…(обрезано, всего {text.Length} символов)";
            return new ToolResult(string.IsNullOrWhiteSpace(text) ? "(Страница без читаемого текста — возможно, всё рисует JavaScript. Посмотри глазами: browser_read with_screenshot.)" : text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ToolResult($"Не удалось загрузить {url}: {ex.Message}");
        }
    }

    /// <summary>Открыть адрес (или поиск) в браузере друга новой вкладкой.</summary>
    private static async Task<ToolResult> BrowserOpenAsync(string url, Func<ToolRequest, Task<bool>> approve, ToolBox? box)
    {
        if (string.IsNullOrWhiteSpace(url)) return new ToolResult("Не сказано, что открыть.");
        var u = url.Trim();
        if (!Regex.IsMatch(u, @"^https?://", RegexOptions.IgnoreCase))
            u = Regex.IsMatch(u, @"^(www\.)?[\w\-]+(\.[\w\-]+)+(/\S*)?$") ? "https://" + u : "https://www.google.com/search?q=" + Uri.EscapeDataString(u);
        if (!await ApproveOrAutoAsync(new ToolRequest("Открыть в браузере", u, false, null, "web"), approve, box)) return new ToolResult(Denied);
        Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
        await Task.Delay(400);
        return new ToolResult($"Открыла в браузере: {u}. Что там — посмотрю через browser_read.");
    }

    /// <summary>Что открыто в браузере: адрес/заголовок активной вкладки (UI Automation) + текст страницы, по желанию снимок.</summary>
    private static async Task<ToolResult> BrowserReadAsync(bool withShot, Func<ToolRequest, Task<bool>> approve, ToolBox? box, CancellationToken ct)
    {
        var (url, title, browser) = await Uia.BrowserTabAsync(TimeSpan.FromSeconds(4));
        if (browser.Length == 0) return new ToolResult("Не вижу открытого браузера. Открыть что-нибудь? (browser_open)");
        if (!await ApproveOrAutoAsync(new ToolRequest("Посмотреть вкладку браузера", url ?? title, false, null, "web"), approve, box)) return new ToolResult(Denied);
        var sb = new StringBuilder($"Браузер {browser}. Вкладка: «{title}»{(url is null ? "" : $"\nАдрес: {url}")}\n");
        // что нарисовано на странице сейчас (вместе с тем, что дорисовали скрипты), — прямо из браузера; не вышло — скачать адрес
        var fetched = Regex.Replace(await Uia.BrowserDocumentTextAsync(12000, TimeSpan.FromSeconds(6)) ?? "", @"\n{3,}", "\n\n").Trim();
        if (fetched.Length < 200 && url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            try { fetched = StripHtml(await GetPageAsync(uri.ToString(), ct)); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { sb.AppendLine($"(Скачать страницу не вышло: {ex.Message})"); }
        }
        if (fetched.Length > 0) sb.AppendLine("Текст страницы:\n" + (fetched.Length > 6000 ? fetched[..6000] + "…" : fetched));
        byte[]? image = null;
        if ((withShot || fetched.Length < 300) && box is not null)
        {
            var bw = Uia.BrowserWindow();
            if (bw != IntPtr.Zero && Native.GetForegroundWindow() != bw) { Uia.Focus(bw); await Task.Delay(250, ct); }
            var shot = await box.Capture();
            box.Hands.Shot = (shot.Bounds, shot.Scale);
            image = shot.Jpeg;
            sb.AppendLine("Снимок экрана с вкладкой приложен (сетка — координаты для mouse_click).");
            if (fetched.Length == 0 && shot.Ocr.Length > 0) sb.AppendLine("Текст на экране: " + (shot.Ocr.Length > 2500 ? shot.Ocr[..2500] + "…" : shot.Ocr));
        }
        return new ToolResult(sb.ToString().TrimEnd(), image);
    }

    /// <summary>Очистка HTML-разметки: вырезает скрипты, стили, нормализует пробелы и абзацы.</summary>
    private static string StripHtml(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var cleaned = Regex.Replace(html, @"<(script|style|noscript|svg|head|nav|footer)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<!--.*?-->", "", RegexOptions.Singleline);
        cleaned = Regex.Replace(cleaned, @"<(br|p|div|h[1-6]|li|tr|section|article)[^>]*>", "\n", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"<[^>]+>", " ");
        cleaned = WebUtility.HtmlDecode(cleaned);
        cleaned = Regex.Replace(cleaned, @"[ \t\u00A0]+", " ");
        cleaned = Regex.Replace(cleaned, @" *\n *", "\n");
        cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
        return cleaned.Trim();
    }

    /// <summary>
    /// Ярлык на Рабочем столе (или в папке): программа по имени (paint, блокнот, steam…), путь к файлу/папке или сайт.
    /// Сайт — .url (InternetShortcut), остальное — .lnk через WScript.Shell. Любая ошибка — текстом мозгу, не падением.
    /// </summary>
    private static async Task<ToolResult> CreateShortcutAsync(string target, string name, string? location, string? args, string? icon,
        string baseDir, Func<ToolRequest, Task<bool>> approve, ToolBox? box)
    {
        if (string.IsNullOrWhiteSpace(target)) return new ToolResult("Не указана цель ярлыка (target).");
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(target.TrimEnd('\\', '/'));
        foreach (var bad in Path.GetInvalidFileNameChars()) name = name.Replace(bad, '_');
        var dir = string.IsNullOrWhiteSpace(location) || location.Trim().ToLowerInvariant() is "desktop" or "рабочий стол" or "рабочийстол"
            ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
            : ResolveUserPath(location, baseDir);
        var resolved = ResolveLaunchTarget(target, baseDir);
        if (resolved is null) return new ToolResult($"Не нашла «{target}»: ни такого файла, ни программы в PATH, ни ярлыка в меню Пуск. Скажи полный путь.");
        var isUrl = IsUrlLike(resolved);
        if (isUrl && Uri.TryCreate(resolved, UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https") resolved = parsed.AbsoluteUri;   // кириллица в адресе — в %-кодировке
        var shortcutPath = Path.Combine(dir, name.EndsWith(isUrl ? ".url" : ".lnk", StringComparison.OrdinalIgnoreCase) ? name : name + (isUrl ? ".url" : ".lnk"));
        if (!await ApproveOrAutoAsync(new ToolRequest("Создать ярлык", $"{shortcutPath} → {resolved}", false, null, "shortcut"), approve, box))
            return new ToolResult(Denied);
        Directory.CreateDirectory(dir);
        if (isUrl)
        {
            var sb = new StringBuilder("[InternetShortcut]\r\n").Append("URL=").Append(resolved).Append("\r\n");
            if (!string.IsNullOrWhiteSpace(icon)) sb.Append("IconFile=").Append(ResolveUserPath(icon, baseDir)).Append("\r\nIconIndex=0\r\n");
            // .url — это INI: ASCII, а с кириллицей (путь к иконке) — UTF-16 с BOM, так его понимает оболочка
            var ini = sb.ToString();
            await File.WriteAllTextAsync(shortcutPath, ini, ini.All(c => c < 128) ? Encoding.ASCII : Encoding.Unicode);
            return new ToolResult($"✨ Создала ярлык «{Path.GetFileName(shortcutPath)}» на {resolved} в {dir}.");
        }
        if (resolved.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            // нашли готовый ярлык в меню Пуск — просто копируем его под нужным именем
            File.Copy(resolved, shortcutPath, overwrite: true);
            return new ToolResult($"✨ Скопировала ярлык «{Path.GetFileNameWithoutExtension(resolved)}» на Рабочий стол как «{Path.GetFileName(shortcutPath)}».");
        }
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell недоступен");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = resolved;
            if (!string.IsNullOrWhiteSpace(args)) shortcut.Arguments = args;
            if (!string.IsNullOrWhiteSpace(icon)) shortcut.IconLocation = ResolveUserPath(icon, baseDir);
            shortcut.WorkingDirectory = Directory.Exists(resolved) ? resolved : (Path.GetDirectoryName(resolved) ?? "");
            shortcut.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shortcut);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
        return File.Exists(shortcutPath)
            ? new ToolResult($"✨ Создала ярлык «{Path.GetFileName(shortcutPath)}» → {resolved}.")
            : new ToolResult($"Ярлык не появился ({shortcutPath}) — возможно, папка защищена.");
    }

    /// <summary>Быстрый запуск игры или программы (Paint, Steam, игры с Рабочего стола и из меню Пуск).</summary>
    private static async Task<ToolResult> OpenGameAsync(string name, Func<ToolRequest, Task<bool>> approve, ToolBox? box)
    {
        if (string.IsNullOrWhiteSpace(name)) return new ToolResult("Не указано имя игры или программы.");
        var target = ResolveLaunchTarget(name, Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        if (target is null) return new ToolResult($"Не нашла «{name}» ни на Рабочем столе, ни в меню Пуск.");
        if (!await ApproveOrAutoAsync(new ToolRequest("Запустить", target, false, null, "open"), approve, box)) return new ToolResult(Denied);
        var before = Uia.ListWindows(200).Select(w => w.Handle).ToHashSet();
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return new ToolResult($"🎮 Запустила «{Path.GetFileNameWithoutExtension(target)}». {await BringNewWindowAsync(before)}");
    }

    /// <summary>Команда в bash (Git Bash, иначе WSL). Аргументы — списком, без самодельного экранирования кавычек.</summary>
    /// <param name="wslDistro">null — Git Bash (или WSL, если его нет); имя — настоящий Linux в этом дистрибутиве WSL.</param>
    private static async Task<ToolResult> BashRunAsync(string cmd, double timeoutSec, string workDir, Func<ToolRequest, Task<bool>> approve, ToolBox? box, CancellationToken ct,
        string? wslDistro = null)
    {
        if (string.IsNullOrWhiteSpace(cmd)) return new ToolResult("Пустая команда bash.");
        var timeout = (int)Math.Clamp(timeoutSec > 0 ? timeoutSec : 60, 1, wslDistro is null ? 600 : 1800);
        if (CatastropheRx().IsMatch(cmd) || Regex.IsMatch(cmd, @"\brm\s+-[a-z]*r[a-z]*f?\s+(/|~|/c/?|/mnt/c/?)(\s|$)", RegexOptions.IgnoreCase))
            return new ToolResult("❌ Заблокировано защитой: команда может снести систему или домашнюю папку.");
        var m = DangerCmdRx().Match(cmd);
        if (box is { Autopilot: true } && IsHardDelete(cmd))
            return new ToolResult("❌ [Защита Клавы]: безвозвратное удаление — только в Корзину. Удаляй инструментом recycle и продолжай.");
        if (!await ApproveOrAutoAsync(new ToolRequest("Команда bash", cmd, m.Success && box is not { Autopilot: true }, m.Success ? $"Опасная команда: {m.Value}" : null, "shell"), approve, box))
            return new ToolResult(Denied);
        var gitBash = wslDistro is null ? new[] { @"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files (x86)\Git\bin\bash.exe" }.FirstOrDefault(File.Exists) : null;
        var psi = new ProcessStartInfo(gitBash ?? "wsl.exe")
        {
            WorkingDirectory = Directory.Exists(workDir) ? workDir : Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (gitBash is null)
        {
            if (wslDistro is not null) { psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(wslDistro); }
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("bash");
        }
        psi.ArgumentList.Add("-lc");
        psi.ArgumentList.Add(cmd);
        using var proc = Process.Start(psi);
        if (proc is null) return new ToolResult("Не удалось запустить bash.");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));
        var outTask = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            return new ToolResult($"Таймаут команды bash ({timeout} с).");
        }
        // wsl.exe пишет свои ошибки в UTF-16: читанные как UTF-8, они идут с нулями между буквами — убираем
        var combined = ((await outTask) + "\n" + (await errTask)).Replace("\0", "").Trim();
        if (combined.Length > 8000) combined = combined[..8000] + "\n…(обрезано)";
        return new ToolResult($"код выхода {proc.ExitCode}\n{(combined.Length > 0 ? combined : "(пустой вывод)")}");
    }

    // ---------- Minecraft: мост mineflayer (mineflayer_bridge\bridge.js, 127.0.0.1:8099) ----------

    private static readonly HttpClient McHttp = NetProxy.Client(null, TimeSpan.FromSeconds(100));
    internal const string McBridgeUrl = "http://127.0.0.1:8099";

    /// <summary>Мост запущен или поднят сейчас (Node.js и папка mineflayer_bridge на месте). null — всё ок, иначе причина.</summary>
    internal static async Task<string?> EnsureMcBridgeAsync(KlavaConfig cfg)
    {
        try
        {
            using var res = await McHttp.GetAsync($"{McBridgeUrl}/status");
            if (res.IsSuccessStatusCode) return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        var dir = cfg.Abs("mineflayer_bridge");
        if (!File.Exists(Path.Combine(dir, "bridge.js"))) return "нет папки mineflayer_bridge — мост в Minecraft не установлен";
        if (!Directory.Exists(Path.Combine(dir, "node_modules", "mineflayer"))) return "у моста нет библиотек: в папке mineflayer_bridge выполни npm install";
        Process? p;
        try { p = Process.Start(new ProcessStartInfo("node", "bridge.js") { WorkingDirectory = dir, UseShellExecute = false, CreateNoWindow = true }); }
        catch (System.ComponentModel.Win32Exception) { return "нет Node.js (node) — поставь с nodejs.org"; }
        if (p is not null) { try { Native.TieToLifetime(p); } catch (InvalidOperationException) { } }
        for (var i = 0; i < 30; i++)
        {
            await Task.Delay(300);
            try
            {
                using var res = await McHttp.GetAsync($"{McBridgeUrl}/status");
                if (res.IsSuccessStatusCode) return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
        }
        return "мост в Minecraft не поднялся за 9 с";
    }

    /// <summary>Ответ моста {ok, message|error, status?} → текст мозгу; провал — с пометкой «НЕ СРАБОТАЛО».</summary>
    private static async Task<ToolResult> McResultAsync(HttpResponseMessage resp)
    {
        var raw = await resp.Content.ReadAsStringAsync();
        JsonNode? n;
        try { n = JsonNode.Parse(raw); }
        catch (JsonException) { return new ToolResult($"[НЕ СРАБОТАЛО] мост ответил непонятно: {raw[..Math.Min(200, raw.Length)]}"); }
        if (n?["ok"]?.GetValue<bool>() != true) return new ToolResult($"[НЕ СРАБОТАЛО] {n?["error"]?.GetValue<string>() ?? raw}");
        var text = n["message"]?.GetValue<string>() ?? "Готово.";
        if (n["status"] is JsonObject st) text += "\n" + McDescribe(st);
        return new ToolResult(text);
    }

    private static string McDescribe(JsonNode st)
    {
        if (st["connected"]?.GetValue<bool>() != true)
        {
            var lan = st["lan"] is JsonArray { Count: > 0 } l ? $" В сети виден мир «{l[0]?["motd"]}» (порт {l[0]?["port"]})." : "";
            return $"Клава не в игре.{(st["error"] is { } e ? $" Последняя ошибка: {e}." : "")}{lan}";
        }
        var pos = st["position"];
        var sb = new StringBuilder($"В игре: {st["server"]} ({st["version"]}), здоровье {st["health"]?.ToString() ?? "?"}/20, голод {st["food"]?.ToString() ?? "?"}/20, " +
                                   $"стою {pos?["x"]} {pos?["y"]} {pos?["z"]}, {st["time"]}");
        if (st["holding"] is { } h) sb.Append($", в руке {h}");
        if (st["inventory"] is JsonArray { Count: > 0 } inv) sb.Append($". Инвентарь: {string.Join(", ", inv.Select(x => x?.ToString()))}");
        if (st["players"] is JsonArray { Count: > 0 } pl) sb.Append($". Игроки: {string.Join(", ", pl.Select(x => x?.ToString()))}");
        if (st["nearby"] is JsonArray { Count: > 0 } nb) sb.Append($". Рядом: {string.Join(", ", nb.Take(6).Select(x => $"{x?["name"]} ({x?["distance"]} м)"))}");
        return sb.Append('.').ToString();
    }

    private static async Task<ToolResult> McConnectAsync(string host, int port, string username, Func<ToolRequest, Task<bool>> approve, KlavaConfig cfg, ToolBox? box)
    {
        if (await EnsureMcBridgeAsync(cfg) is { } why) return new ToolResult($"[НЕ СРАБОТАЛО] {why}");
        var where = port > 0 ? $"{(string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim())}:{port}" : "мир, открытый по локальной сети";
        if (!await ApproveOrAutoAsync(new ToolRequest("Зайти в Minecraft", where, false, null, "open"), approve, box)) return new ToolResult(Denied);
        var body = new JsonObject
        {
            ["host"] = string.IsNullOrWhiteSpace(host) ? null : host.Trim(),
            ["port"] = port > 0 ? port : null,
            ["username"] = string.IsNullOrWhiteSpace(username) ? "Klava" : username.Trim(),
        };
        using var resp = await McHttp.PostAsync($"{McBridgeUrl}/connect", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        return await McResultAsync(resp);
    }

    private static async Task<ToolResult> McStatusAsync(KlavaConfig cfg)
    {
        if (await EnsureMcBridgeAsync(cfg) is { } why) return new ToolResult($"[НЕ СРАБОТАЛО] {why}");
        using var resp = await McHttp.GetAsync($"{McBridgeUrl}/status");
        var st = JsonNode.Parse(await resp.Content.ReadAsStringAsync());
        return new ToolResult(st is null ? "Мост молчит." : McDescribe(st));
    }

    private static async Task<ToolResult> McActionAsync(string action, string target, double count, string player, double x, double y, double z,
        Func<ToolRequest, Task<bool>> approve, KlavaConfig cfg, ToolBox? box)
    {
        if (await EnsureMcBridgeAsync(cfg) is { } why) return new ToolResult($"[НЕ СРАБОТАЛО] {why}");
        var act = action.ToLowerInvariant().Trim();
        if (!await ApproveOrAutoAsync(new ToolRequest("Minecraft", $"{act} {target}".Trim(), false, null, "write"), approve, box)) return new ToolResult(Denied);
        var payload = new JsonObject
        {
            ["action"] = act, ["target"] = target, ["count"] = count > 0 ? count : null, ["player"] = player, ["x"] = x, ["y"] = y, ["z"] = z,
        };
        using var resp = await McHttp.PostAsync($"{McBridgeUrl}/action", new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"));
        return await McResultAsync(resp);
    }

    private static async Task<ToolResult> McChatAsync(string message, Func<ToolRequest, Task<bool>> approve, KlavaConfig cfg, ToolBox? box)
    {
        if (string.IsNullOrWhiteSpace(message)) return new ToolResult("Пустое сообщение.");
        if (await EnsureMcBridgeAsync(cfg) is { } why) return new ToolResult($"[НЕ СРАБОТАЛО] {why}");
        if (!await ApproveOrAutoAsync(new ToolRequest("Minecraft чат", message, false, null, "write"), approve, box)) return new ToolResult(Denied);
        var body = new JsonObject { ["message"] = message };
        using var resp = await McHttp.PostAsync($"{McBridgeUrl}/chat", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        return await McResultAsync(resp);
    }
}
