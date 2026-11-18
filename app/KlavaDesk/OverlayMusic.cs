using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Музыка в Клаве: «послушай, что играет», разбор трека, кавер её голосом (по-русски слог в слог или в оригинале),
/// песня на сцене (губы по её голосу, кивок на долю, строки в облачке). Друг 30.09 — «Клава Певица.md».
/// Задачи идут в фоне: разговор не занят, итог приходит ей самой репликой-событием.
/// </summary>
internal sealed partial class OverlayForm
{
    private MusicClient? _musicClient;
    private CancellationTokenSource? _musicCts;
    private string _musicDoing = "";
    private JsonNode? _pendingSong;
    private DateTime _songUntil = DateTime.MinValue;
    private DateTime _musicBubbleAt = DateTime.MinValue;
    private MusicStudioForm? _studio;
    private string[] _songLyrics = [];
    private readonly List<string> _calledDuringSong = [];

    private MusicClient MusicSvc => _musicClient ??= new MusicClient(_cfg, _services);
    private bool SongPlaying => DateTime.UtcNow < _songUntil;
    internal string MusicRoot => _cfg.Abs(@"data\music");

    /// <summary>Её собственная фонотека: data\music\library (песни, скачанные для неё, — Карамельдансен с Internet Archive).</summary>
    internal static string? SongLibrary { get; set; }

    /// <summary>
    /// Карамельдансен (друг 02.10: «я же указывал, где достать песню — скачай и установи»): трек лежит в фонотеке.
    /// Разобран (доли) — играет песня, и она танцует всю песню в её долю (поза caramellSong); не разобран — танцует под
    /// свой счёт 30 с и сама запускает разбор, со второго раза — с музыкой. Без файла — просто танец.
    /// </summary>
    private void Caramell()
    {
        MarkActivity("танцуешь Карамельдансен — ладошки ушками над головой, бёдрами влево-вправо", 40);
        var file = FindSong("caramelldansen");
        if (file is not null && !SongPlaying && (TrackFor(file) ?? TrackCard(file)) is JsonObject info)
        {
            info["pose"] = "caramellSong";
            // говорит («ща станцую!») — песня после фразы, иначе оборвала бы её; пока говорит — танцует под свой счёт
            if (_speaking) { _pendingSong = info; Post(new { type = "gesture", name = "caramell" }); }
            else PlaySong(info);
            MarkActivity("танцуешь Карамельдансен под песню Caramell — ладошки ушками, бёдрами влево-вправо", info["duration"]?.GetValue<double>() ?? 180);
            return;
        }
        Post(new { type = "gesture", name = "caramell" });
        if (file is not null && _musicCts is null)
        {
            Log.Write("музыка: Карамельдансен ещё не разобран — разбираю, со следующего раза будет с песней");
            StartMusicJob("разбираю Caramelldansen для танца", ct => AnalyzeJobAsync(file, ct));
        }
    }

    private IProgress<MusicClient.Step> MusicProgress(string what) => new Progress<MusicClient.Step>(s =>
    {
        Log.Write($"музыка ({what}): {(int)(s.Progress * 100)}% {s.Message}");
        _studio?.SetStatus(s.Progress, s.Message);
        // облачко — не чаще раза в 8 с, чтобы не заслонять разговор
        if (DateTime.UtcNow - _musicBubbleAt < TimeSpan.FromSeconds(8) || _speaking) return;
        _musicBubbleAt = DateTime.UtcNow;
        Ui(() => Bubble($"🎵 {s.Message}"));
    });

    /// <summary>Одна музыкальная задача за раз (видеокарта одна); ошибка — ей же репликой, честно.</summary>
    private string StartMusicJob(string what, Func<CancellationToken, Task> body)
    {
        if (_musicCts is { IsCancellationRequested: false }) return $"Уже занята музыкой: {_musicDoing}. Дождись итога или отмени (action cancel).";
        var cts = new CancellationTokenSource();
        _musicCts = cts;
        _musicDoing = what;
        _ = Task.Run(async () =>
        {
            try { await body(cts.Token); }
            catch (OperationCanceledException) { Log.Write($"музыка: «{what}» отменено"); }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or UnauthorizedAccessException
                                           or System.Text.Json.JsonException or TaskCanceledException)
            {
                Log.Error($"музыка: «{what}»", ex);
                Ui(() => QueueEvent($"(Музыкальная задача «{what}» сорвалась: {ex.Message}. Скажи другу честно одной-двумя фразами, что не вышло.)"));
            }
            finally
            {
                if (ReferenceEquals(_musicCts, cts)) { _musicCts = null; _musicDoing = ""; }
                cts.Dispose();
                _studio?.SetStatus(1, "");
            }
        });
        return "";
    }

    // ---------- инструмент music ----------

    internal async Task<string> MusicToolAsync(string action, JsonObject args)
    {
        string Arg(string n) => args[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s : args[n]?.ToJsonString() ?? "";
        int Num(string n) => args[n] is JsonValue v && (v.TryGetValue<int>(out var i) || (v.TryGetValue<double>(out var d) && (i = (int)d) == i)) ? i
            : int.TryParse(Arg(n), out var k) ? k : 0;
        switch (action.Trim().ToLowerInvariant())
        {
            case "listen" or "hear":
            {
                var sec = Math.Clamp(Num("seconds") > 0 ? Num("seconds") : _cfg.MusicListenSec, 10, 120);
                var busy = StartMusicJob($"слушаю звук компьютера {sec} с", ct => ListenJobAsync(sec, ct));
                return busy.Length > 0 ? busy
                    : $"Слушаю звук компьютера {sec} с (свой голос в запись не идёт), потом разбор — итог придёт тебе сам через минуту-две. " +
                      "Скажи другу одной фразой, что слушаешь; не выдумывай, что слышишь, пока не пришёл разбор.";
            }
            case "analyze" or "analyse":
            {
                var file = FindSong(Arg("path"));
                if (file is null) return $"Не нашла трек «{Arg("path")}». Нужен путь к файлу (mp3, flac, ogg, wav, m4a) или его имя с рабочего стола/музыки/загрузок.";
                var busy = StartMusicJob($"разбираю «{Path.GetFileNameWithoutExtension(file)}»", ct => AnalyzeJobAsync(file, ct));
                return busy.Length > 0 ? busy : $"Разбираю «{Path.GetFileName(file)}» (1–3 минуты: дорожки, доли, ноты, аккорды, текст) — итог придёт сам. Скажи другу коротко.";
            }
            case "sing" or "cover":
            {
                var file = FindSong(Arg("path"));
                if (file is null) return $"Не нашла песню «{Arg("path")}». Нужен путь к файлу или его имя.";
                var lang = Arg("lang").Trim().ToLowerInvariant();
                var orig = lang is "orig" or "original" or "en" or "оригинал";
                var lyrics = Arg("lyrics");
                var busy = StartMusicJob($"кавер «{Path.GetFileNameWithoutExtension(file)}»", ct => CoverJobAsync(file, orig, lyrics, ct));
                return busy.Length > 0 ? busy
                    : $"Готовлю кавер «{Path.GetFileName(file)}» {(orig ? "в оригинале" : "по-русски")}: разбор, {(orig || lyrics.Length > 0 ? "" : "перевод слог в слог, ")}пение " +
                      "своим голосом (минут 3–6). Когда готово — сама споёшь. Скажи другу коротко, что взялась.";
            }
            case "compose" or "own" when Arg("path").Trim().Length == 0:
            case "violin" or "paganini":
            {
                if (!File.Exists(_cfg.Abs(_cfg.MusicSoundFont))) return $"Нет звукового банка {_cfg.MusicSoundFont} — его качает tools\\fetch_models.ps1.";
                var violin = action.Trim().ToLowerInvariant() is "violin" or "paganini";
                var mood = MoodOf(Arg("mood"));
                var busy = StartMusicJob(violin ? "скрипка: каприс Паганини" : $"своя мелодия ({mood})", ct => OwnMusicJobAsync(violin, mood, ct));
                return busy.Length > 0 ? busy
                    : violin ? "Берёшь скрипку: тема 24-го каприса Паганини — заиграет сама через пару секунд, ты со скрипкой на сцене. Скажи другу коротко."
                    : "Сочиняешь свою мелодию по настроению (темп, лад, гармония, грув — свои) — заиграет сама, ты за синтезатором. Скажи другу коротко.";
            }
            case "similar" or "compose":
            {
                var file = FindSong(Arg("path"));
                if (file is null) return $"Не нашла трек «{Arg("path")}». Нужен путь к файлу или его имя.";
                if (!File.Exists(_cfg.Abs(_cfg.MusicSoundFont))) return $"Нет звукового банка {_cfg.MusicSoundFont} — его качает tools\\fetch_models.ps1.";
                var busy = StartMusicJob($"похожая основа по «{Path.GetFileNameWithoutExtension(file)}»", ct => SimilarJobAsync(file, ct));
                return busy.Length > 0 ? busy : "Собираю похожую основу по разбору трека (тот же темп, грув и гармония, своя мелодия) — сыграет сама. Скажи другу коротко.";
            }
            case "play":
            {
                var what = Arg("path").Trim();
                var info = what is "" or "last" or "cover" or "кавер" ? LastCover() : CoverFor(what) ?? TrackFor(what);
                if (info is null) return "Нечего включить: каверов ещё нет. Сначала sing.";
                Ui(() => PlaySong(info, Num("from"), Num("to")));
                return $"Играет «{info["title"]}»{(info["mode"]?.GetValue<string>() == "track" ? "" : " — твоим голосом")}. Пока играет — молчи.";
            }
            case "stop":
                Ui(() => StopSong());
                return "Музыку остановила.";
            case "cancel":
                if (_musicCts is null) return "Музыкальных задач нет.";
                _musicCts.Cancel();
                return $"Отменила: {_musicDoing}.";
            case "studio":
                Ui(() => ShowStudio(FindSong(Arg("path"))));
                return "Открыла другу музыкальную студию.";
            case "status":
                return _musicCts is null ? $"Задач нет. Последний кавер: {LastCover()?["title"] ?? "нет"}." : $"Идёт: {_musicDoing}.";
            default:
                return "action: listen | analyze | sing | similar | compose | violin | play | stop | cancel | studio | status";
        }
    }

    // ---------- задачи ----------

    private async Task ListenJobAsync(int seconds, CancellationToken ct)
    {
        // старая Windows не умеет «всё, кроме себя» — тогда пусть она сначала договорит, иначе её голос попадёт в запись
        for (var i = 0; i < 40 && (_speaking || _voiceTurn); i++) await Task.Delay(250, ct);
        var meta = await MusicClient.NowPlayingAsync();
        Ui(() => Bubble($"🎧 Слушаю {seconds} с…"));
        var file = Path.Combine(MusicRoot, "listen", $"{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        // пока слушает — кивает в такт живому биту
        var live = new MusicClient.LiveBeat();
        live.Beat += (period, next) => Ui(() => Post(new { type = "groove", period, next }));
        var (path, sec, peak) = await MusicClient.RecordAsync(file, seconds, ct, live.Feed);
        Log.Write($"музыка: записала {sec:F1} с звука компьютера, пик {peak:F3}{(meta is null ? "" : $"; плеер: {meta}")}");
        if (sec < 3 || peak < 0.004)
        {
            Ui(() => QueueEvent($"(Ты хотела послушать, что играет, но за {sec:F0} с из колонок почти ничего не прозвучало" +
                               (meta is null ? "" : $" (плеер Windows пишет: {meta})") + ". Скажи другу одной фразой — пусть включит погромче или запустит трек.)"));
            return;
        }
        var res = await MusicSvc.RunAsync("analyze", new JsonObject { ["path"] = path, ["quick"] = true }, MusicProgress("слушаю"), ct);
        var passport = res?["passport"]?.GetValue<string>() ?? "";
        Ui(() => QueueEvent($"(Ты послушала {sec:F0} с звука компьютера" + (meta is null ? "" : $"; плеер Windows пишет, что играет: {meta}") +
                            ". Вот что измерил твой музыкальный слух — это замеры нейросетей-анализаторов, а не догадки:\n" + passport +
                            "\n\nСкажи другу живыми словами, как тебе эта музыка: грув и темп, чем сделано (инструменты), гармония и лад, " +
                            "настроение, звук и сведение. Узнаёшь вещь по названию из плеера, по тексту или по нотам темы — назови, но честно " +
                            "отличай «узнала» от «похоже на». Это кусок в полминуты — о форме всей вещи не суди. 3–6 фраз, без списков и цифр подряд.)"));
    }

    private async Task AnalyzeJobAsync(string file, CancellationToken ct)
    {
        var res = await MusicSvc.RunAsync("analyze", new JsonObject { ["path"] = file }, MusicProgress("разбор"), ct);
        var passport = res?["passport"]?.GetValue<string>() ?? "";
        Ui(() =>
        {
            _studio?.LoadTrack(file);
            QueueEvent($"(Ты разобрала трек «{Path.GetFileName(file)}» своим музыкальным слухом (замеры нейросетей, не догадки):\n{passport}\n\n" +
                       "Скажи другу живыми словами, что это за вещь и как тебе: грув, инструменты, гармония, форма, звук; узнаёшь по " +
                       "тексту или теме — назови, честно отличая «узнала» от «похоже». 4–7 фраз. Предложи спеть кавер или открыть студию, если к месту.)");
        });
    }

    private async Task CoverJobAsync(string file, bool orig, string lyricsText, CancellationToken ct)
    {
        var prog = MusicProgress("кавер");
        var plan = await MusicSvc.RunAsync("plan", new JsonObject { ["path"] = file }, prog, ct)
                   ?? throw new InvalidOperationException("разбор не вернул план песни");
        var n = plan["lines"]!.AsArray().Count;
        List<string?>? lyrics = null;
        if (!orig)
        {
            if (!string.IsNullOrWhiteSpace(lyricsText))
                lyrics = SplitLyrics(lyricsText, n);
            else if (plan["language"]?.GetValue<string>() is "ru" or "uk")
                lyrics = plan["lines"]!.AsArray().Select(l => l!["vocalise"]!.GetValue<bool>() ? "=" : l["en"]!.GetValue<string>()).ToList()!;
            else
                lyrics = await MusicClient.TranslateAsync(_brain, plan, prog, ct);
            var bad = lyrics.Select((t, i) => (t, i)).Count(x => x.t is not null and not "=" &&
                                                                  MusicClient.Syllables(x.t) != plan["lines"]![x.i]!["k"]!.GetValue<int>());
            if (bad > 0) Log.Write($"музыка: перевод — {bad} строк не в размер, ноты подгонятся под слоги");
        }
        var args = new JsonObject { ["path"] = file, ["orig"] = orig, ["engine"] = _cfg.MusicCoverEngine };
        if (!orig && _cfg.MusicCoverEngine == "ace" && await MusicClient.StyleAsync(_brain, plan, ct) is { Length: > 0 } style)
        {
            args["style"] = style;
            Log.Write($"музыка: стиль для ACE — {style}");
        }
        if (lyrics is not null) args["lyrics"] = new JsonArray([.. lyrics.Select(x => (JsonNode?)JsonValue.Create(x))]);
        var cover = await MusicSvc.RunAsync("sing", args, prog, ct) ?? throw new InvalidOperationException("пение не вернуло итог");
        var copy = CopyCoverToDesktop(cover);
        _pendingSong = cover;
        var text = lyrics is null ? "" : string.Join("\n", lyrics.Where(x => x is not null and not "=").Distinct());
        Ui(() =>
        {
            _studio?.LoadTrack(file);
            QueueEvent($"(Твой кавер готов: «{cover["artist"]} — {cover["title"]}», {(orig ? "в оригинале твоим голосом" : "по-русски, текст — твой перевод слог в слог")}." +
                       (text.Length > 0 ? $"\nТекст:\n{text}" : "") + (copy is null ? "" : $"\nФайл для друга: {copy}") +
                       "\nКак договоришь — песня заиграет сама. Скажи другу одну-две живые фразы перед песней, текст не пересказывай.)");
            // мозг молчит (нет сети) — песня всё равно заиграет
            _ = Task.Delay(TimeSpan.FromSeconds(40)).ContinueWith(_ => Ui(() =>
            {
                if (_pendingSong is { } s && !IsBusy) { _pendingSong = null; PlaySong(s); }
            }), TaskScheduler.Default);
        });
    }

    /// <summary>Настроение своей музыки из слов мозга или друга: happy | sad | chill | energetic | rock ("" — сама выберет).</summary>
    internal static string MoodOf(string s)
    {
        s = s.Trim().ToLowerInvariant();
        return s switch
        {
            _ when Regex.IsMatch(s, @"happy|весел|весёл|радост|солн|позитив") => "happy",
            _ when Regex.IsMatch(s, @"sad|груст|печал|тоск|меланх") => "sad",
            _ when Regex.IsMatch(s, @"chill|чил|спокой|нежн|джаз|lo-?fi|лоуфай|мягк") => "chill",
            _ when Regex.IsMatch(s, @"energ|завод|танц|бодр|электро|клуб") => "energetic",
            _ when Regex.IsMatch(s, @"rock|рок|злая|злой|тяжел|тяжёл|метал") => "rock",
            _ => "",
        };
    }

    /// <summary>Своя музыка без исходного трека: скрипка (каприс Паганини) или мелодия по настроению — MIDI и звук из банка,
    /// играет сама (поза со скрипкой / за синтезатором, пальцы по нотам).</summary>
    private async Task OwnMusicJobAsync(bool violin, string mood, CancellationToken ct)
    {
        var seed = Environment.TickCount & 0xFFFF;
        var outDir = Path.Combine(MusicRoot, violin ? "violin" : "own", $"{DateTime.Now:yyyyMMdd_HHmmss}");
        var sf = _cfg.Abs(_cfg.MusicSoundFont);
        var r = await Task.Run(() => violin ? MusicCompose.Violin(outDir, sf, seed) : MusicCompose.Original(outDir, sf, seed, mood), ct);
        Log.Write($"музыка: {(violin ? "скрипка" : "своя мелодия")} — {r.Description}");
        _pendingSong = r.Song;
        Ui(() =>
        {
            QueueEvent(violin
                ? $"(Ты взяла скрипку: {r.Description} Как договоришь — заиграешь. Скажи другу одну живую фразу перед игрой.)"
                : $"(Ты сочинила свою мелодию: {r.Description}\nMIDI для друга: {r.Midi}. Как договоришь — заиграешь за синтезатором. " +
                  "Скажи другу 1–2 фразы о ней: настроение и что задумала.)");
            _ = Task.Delay(TimeSpan.FromSeconds(40)).ContinueWith(_ => Ui(() =>
            {
                if (_pendingSong is { } s && !IsBusy) { _pendingSong = null; PlaySong(s); }
            }), TaskScheduler.Default);
        });
    }

    private async Task SimilarJobAsync(string file, CancellationToken ct)
    {
        var res = await MusicSvc.RunAsync("analyze", new JsonObject { ["path"] = file }, MusicProgress("похожая основа"), ct)
                  ?? throw new InvalidOperationException("разбор не вернул итог");
        var dir = res["dir"]!.GetValue<string>();
        var seed = Environment.TickCount & 0xFFFF;
        var outDir = Path.Combine(MusicRoot, "similar", $"{Path.GetFileName(dir)}-{seed}");
        var r = await Task.Run(() => MusicCompose.Compose(dir, outDir, _cfg.Abs(_cfg.MusicSoundFont), seed), ct);
        Log.Write($"музыка: похожая основа — {r.Description}");
        _pendingSong = r.Song;
        Ui(() =>
        {
            _studio?.LoadTrack(file);
            QueueEvent($"(Ты собрала похожую основу по разбору «{Path.GetFileName(file)}» — своими нотами и инструментами из звукового банка: {r.Description}\n" +
                       $"MIDI для друга: {r.Midi}. Как договоришь — заиграет. Скажи другу 1–2 фразы: что взяла из оригинала и что своё.)");
            _ = Task.Delay(TimeSpan.FromSeconds(40)).ContinueWith(_ => Ui(() =>
            {
                if (_pendingSong is { } s && !IsBusy) { _pendingSong = null; PlaySong(s); }
            }), TaskScheduler.Default);
        });
    }

    private static List<string?> SplitLyrics(string text, int n)
    {
        var rows = text.Replace("\r", "").Split('\n').Select(r => r.Trim()).Where(r => r.Length > 0).ToList();
        var numbered = rows.All(r => Regex.IsMatch(r, @"^\d{1,3}\s*[|:.)]"));
        var res = new string?[n];
        for (var i = 0; i < rows.Count; i++)
        {
            var m = Regex.Match(rows[i], @"^(\d{1,3})\s*[|:.)]\s*(.*)$");
            var (idx, t) = numbered && m.Success ? (int.Parse(m.Groups[1].Value), m.Groups[2].Value.Trim()) : (i, rows[i]);
            if (idx >= 0 && idx < n) res[idx] = t;
        }
        return [.. res];
    }

    private string? CopyCoverToDesktop(JsonNode cover)
    {
        try
        {
            var src = Path.Combine(cover["dir"]!.GetValue<string>(), cover["mp3"]!.GetValue<string>());
            if (!File.Exists(src)) return null;
            var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var home = Directory.Exists(Path.Combine(desk, "ИИ кошкодевочка")) ? Path.Combine(desk, "ИИ кошкодевочка") : desk;
            var dir = Path.Combine(home, "Каверы Клавы");
            Directory.CreateDirectory(dir);
            var how = cover["mode"]?.GetValue<string>() == "orig" ? ", оригинал" : cover["engine"]?.GetValue<string>() == "ace" ? ", по-русски, живой вокал" : ", по-русски";
            var name = $"{cover["artist"]} - {cover["title"]} (Клава{how}).mp3";
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            var dst = Path.Combine(dir, name.TrimStart(' ', '-'));
            File.Copy(src, dst, overwrite: true);
            return dst;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"музыка: кавер не скопировался на рабочий стол — {ex.Message}");
            return null;
        }
    }

    // ---------- песня на сцене ----------

    private string MusicUrl(string file)
    {
        var rel = Path.GetRelativePath(MusicRoot, file).Replace('\\', '/');
        return "https://klava-music.local/" + string.Join("/", rel.Split('/').Select(Uri.EscapeDataString));
    }

    /// <summary>Спеть кавер (cover.json) или включить разобранный трек (analysis.json: микс, без её голоса).</summary>
    private void PlaySong(JsonNode info, int from = 0, int to = 0)
    {
        var dir = info["dir"]!.GetValue<string>();
        var track = info["mode"]?.GetValue<string>() == "track";
        var mix = Path.Combine(dir, info[track ? "mix" : "cover"]!.GetValue<string>());
        if (!File.Exists(mix)) { Bubble("Файла песни нет — перепою."); return; }
        string? vocal = track ? null : Path.Combine(dir, info["vocal"]!.GetValue<string>());
        var dur = info["duration"]?.GetValue<double>() ?? 240;
        StopSpeechForSong();
        Post(new
        {
            type = "song",
            mix = MusicUrl(mix),
            vocal = vocal is not null && File.Exists(vocal) ? MusicUrl(vocal) : null,
            beats = info["beats"],
            downbeats = info["downbeats"],
            sections = info["sections"],   // части песни — танец меняет фазы по ним (Карамельдансен)
            lines = info["lines"],
            mood = info["mood"],
            title = info["title"]?.GetValue<string>() ?? "",
            pose = info["pose"]?.GetValue<string>(),   // violin / synth — сыграть самой (props.js), иначе микрофон или танец
            keys = info["keys"],
            offset = from > 0 ? from : 0,
            until = to > 0 ? to : 0,
            volume = _cfg.Muted ? 0 : 1,   // «Без звука» — поёт губами, без звука
        });
        var len = (to > 0 ? Math.Min(to, dur) : dur) - Math.Max(0, from);
        _songUntil = DateTime.UtcNow.AddSeconds(len + 3);
        _songLyrics = info["lines"] is JsonArray lines ? [.. lines.Select(l => l?["text"]?.GetValue<string>() ?? "")] : [];
        lock (_calledDuringSong) _calledDuringSong.Clear();
        UpdateDeaf();
        Log.Write($"музыка: играет «{info["title"]}» ({len:F0} с){(vocal is null ? "" : ", поёт")}");
    }

    private void StopSpeechForSong()
    {
        if (_speaking) Post(new { type = "stop" });
    }

    private void StopSong()
    {
        if (!SongPlaying) return;
        lock (_calledDuringSong) _calledDuringSong.Clear();   // оборвали командой — та же фраза и ответит
        Post(new { type = "song_stop" });
        _songUntil = DateTime.MinValue;
        UpdateDeaf();
    }

    private void OnSongEnd(bool natural)
    {
        _songUntil = DateTime.MinValue;
        UpdateDeaf();
        Log.Write($"музыка: песня {(natural ? "доиграла" : "остановлена")}");
        string[] calls;
        lock (_calledDuringSong) { calls = [.. _calledDuringSong]; _calledDuringSong.Clear(); }
        if (natural && calls.Length > 0)
            QueueEvent($"(Пока ты пела, к тебе обращались: {string.Join(" / ", calls.Select(c => $"«{c}»"))}. " +
                       "Микрофон слышал это поверх песни, слова могли смешаться с её текстом. Песня кончилась — ответь теперь коротко, по-свойски; " +
                       "не разобрать, чего хотели, — переспроси и спроси, как им песня.)");
    }

    [GeneratedRegex(@"\b(стоп|хватит|выключ\w*|останов\w*|пауз\w*|замолч\w*|stop)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SongStopRx();

    [GeneratedRegex(@"\b(песн\w*|песен\w*|музык\w*|петь|по[её]шь|пой|трек\w*|кавер\w*|выступ\w*)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SongWordRx();

    /// <summary>
    /// Песня играет из колонок, микрофон её слышит: это не реплика друга. Друг 30.09: «пока поёт — пусть не отвлекается».
    /// Песню обрывает только явная команда (IsSongStop). Обращение по имени без стопа её не сбивает: запоминается,
    /// и она ответит, когда допоёт. Остальное (её же песня, болтовня компании) — мимо.
    /// </summary>
    internal enum SongHeard { None, Drop, Stop, Called }

    /// <summary>
    /// Что микрофон поймал, пока она поёт: строчку её песни или шум (Drop — пропустить сразу), «стоп / хватит» (Stop) или
    /// обращение по имени (Called). Stop и Called решаются уже после узнавания голоса (SongInterrupt): раньше «стоп» и
    /// «Клава» от любого голоса — телевизора, звука игры, фантома — обрывали кавер на полуслове.
    /// </summary>
    private SongHeard ClassifyHeardDuringSong(string text, bool ptt)
    {
        if (ptt || !SongPlaying) return SongHeard.None;
        if (IsSongStop(text, _songLyrics)) return SongHeard.Stop;
        if (KlavaNameRx().IsMatch(text)) return SongHeard.Called;
        Log.Write($"музыка: в микрофоне песня — «{text}» — пропускаю");
        return SongHeard.Drop;
    }

    /// <summary>
    /// Стоп или обращение во время песни — от уверенно узнанного своего из тех, кто здесь. true — фраза дальше не идёт
    /// (позвали — ответит, когда допоёт; чужой голос — поёт дальше), false — стоп принят, фраза обрабатывается как обычно.
    /// </summary>
    private bool SongInterrupt(SongHeard kind, string text, bool sure, string? who)
    {
        if (!sure)
        {
            Log.Write($"музыка: «{text}» — голос не узнан как свой из комнаты ({who ?? "?"}) — пою дальше");
            return true;
        }
        if (kind == SongHeard.Stop)
        {
            Ui(() => StopSong());
            return false;
        }
        lock (_calledDuringSong) if (_calledDuringSong.Count < 3) _calledDuringSong.Add(text);
        Log.Write($"музыка: {(who is null ? "" : HumanName(who) + " ")}позвал во время песни — «{text}» — отвечу, когда допою");
        return true;
    }

    /// <summary>
    /// «Стоп», «хватит», «выключи»… — короткой фразой (до трёх слов), с её именем или со словом про песню
    /// («хватит петь», «выключи музыку»). «Да хватит тебе прикалываться» между друзьями и строчка её же песни — не стоп.
    /// </summary>
    internal static bool IsSongStop(string text, IReadOnlyList<string> lyrics)
    {
        if (!SongStopRx().IsMatch(text) || FromLyrics(text, lyrics)) return false;
        var words = NonWordRx().Split(text.Trim()).Count(w => w.Length > 0);
        return words <= 3 || KlavaNameRx().IsMatch(text) || SongWordRx().IsMatch(text);
    }

    /// <summary>Микрофон поймал строчку её же песни: больше половины слов фразы есть в тексте песни (имя «Клава» — никогда).</summary>
    internal static bool FromLyrics(string text, IReadOnlyList<string> lyrics)
    {
        if (lyrics.Count == 0 || KlavaNameRx().IsMatch(text)) return false;
        static IEnumerable<string> Words(string s) =>
            NonWordRx().Split(s.ToLowerInvariant().Replace('ё', 'е').Replace("-", "")).Where(w => w.Length > 0);
        var vocab = lyrics.SelectMany(Words).ToHashSet();
        var said = Words(text).ToList();
        return said.Count > 0 && said.Count(vocab.Contains) * 2 > said.Count;
    }

    // ---------- что где лежит ----------

    private JsonNode? LastCover()
    {
        var dir = Path.Combine(MusicRoot, "covers");
        if (!Directory.Exists(dir)) return null;
        var f = new DirectoryInfo(dir).GetFiles("cover.json", SearchOption.AllDirectories).OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
        return f is null ? null : ReadCover(f.FullName);
    }

    private JsonNode? CoverFor(string query)
    {
        var dir = Path.Combine(MusicRoot, "covers");
        if (!Directory.Exists(dir) || query.Length == 0) return null;
        var words = query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var f = new DirectoryInfo(dir).GetFiles("cover.json", SearchOption.AllDirectories)
            .Where(x => words.All(w => x.Directory!.Name.ToLowerInvariant().Contains(w) || (ReadCover(x.FullName)?["title"]?.GetValue<string>() ?? "").ToLowerInvariant().Contains(w)))
            .OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
        return f is null ? null : ReadCover(f.FullName);
    }

    private static JsonNode? ReadCover(string file)
    {
        try
        {
            var j = JsonNode.Parse(File.ReadAllText(file));
            if (j is not null) j["dir"] = Path.GetDirectoryName(file);
            return j;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>
    /// Карточка трека рядом с файлом («песня.track.json»: доли, части, длительность — из разбора на машине автора,
    /// ставится setup.ps1 из seed\music). У друзей без видеокарты своего разбора нет, а танцу нужны доли и части песни —
    /// играет сам файл из фонотеки (он под data\music, как и разборы).
    /// </summary>
    private JsonNode? TrackCard(string file)
    {
        var card = Path.Combine(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".track.json");
        if (!File.Exists(card) || !Path.GetFullPath(file).StartsWith(Path.GetFullPath(MusicRoot), StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(card)) is not JsonObject c) return null;
            return new JsonObject
            {
                ["mode"] = "track", ["dir"] = Path.GetDirectoryName(file), ["mix"] = Path.GetFileName(file),
                ["title"] = c["title"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file),
                ["duration"] = c["duration"]?.GetValue<double>() ?? 0, ["beats"] = c["beats"]?.DeepClone(), ["downbeats"] = c["downbeats"]?.DeepClone(),
                ["sections"] = c["sections"]?.DeepClone(), ["lines"] = new JsonArray(), ["mood"] = new JsonArray(),
            };
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Разобранный трек как «песня» без её голоса: микс, доли, строки оригинала.</summary>
    private JsonNode? TrackFor(string query)
    {
        var file = FindSong(query);
        if (file is null) return null;
        var tracks = Path.Combine(MusicRoot, "tracks");
        if (!Directory.Exists(tracks)) return null;
        foreach (var aj in new DirectoryInfo(tracks).GetFiles("analysis.json", SearchOption.AllDirectories).OrderByDescending(x => x.LastWriteTimeUtc))
        {
            JsonNode? an;
            try { an = JsonNode.Parse(File.ReadAllText(aj.FullName)); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { continue; }
            if (!string.Equals(an?["source"]?.GetValue<string>(), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) continue;
            var lines = new JsonArray();
            foreach (var l in an!["lyrics"]?["lines"]?.AsArray() ?? [])
                lines.Add(new JsonObject { ["t0"] = l![0]!.GetValue<double>(), ["t1"] = l[1]!.GetValue<double>(), ["text"] = l[2]!.GetValue<string>() });
            return new JsonObject
            {
                ["mode"] = "track", ["dir"] = aj.DirectoryName, ["mix"] = "mix.flac", ["title"] = an["meta"]?["title"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(file),
                ["duration"] = an["duration"]?.GetValue<double>() ?? 0, ["beats"] = an["beats"]?.DeepClone(), ["downbeats"] = an["downbeats"]?.DeepClone(), ["sections"] = an["sections"]?.DeepClone(),
                ["lines"] = lines, ["mood"] = new JsonArray(),
            };
        }
        return null;
    }

    private static readonly string[] AudioExt = [".mp3", ".flac", ".ogg", ".wav", ".m4a", ".opus", ".aac", ".wma", ".webm", ".mp4"];

    /// <summary>Файл песни по пути или по словам из имени: рабочий стол (и «ИИ кошкодевочка»), Музыка, Загрузки.</summary>
    internal static string? FindSong(string query)
    {
        query = Environment.ExpandEnvironmentVariables(query.Trim().Trim('"', '«', '»'));
        if (query.Length == 0) return null;
        if (File.Exists(query)) return Path.GetFullPath(query);
        var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var roots = new[]
        {
            desk, Path.Combine(desk, "ИИ кошкодевочка"), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"), SongLibrary ?? "",
        }.Where(r => r.Length > 0 && Directory.Exists(r)).Distinct().ToList();
        foreach (var r in roots)
        {
            var p = Path.Combine(r, query);
            if (File.Exists(p)) return p;
        }
        var words = Regex.Split(query.ToLowerInvariant(), @"[\s_\-—–.,]+").Where(w => w.Length > 1).ToArray();
        if (words.Length == 0) return null;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        return roots.SelectMany(r =>
            {
                try { return Directory.EnumerateFiles(r, "*.*", opts); }
                catch (IOException) { return []; }
            })
            .Where(f => AudioExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
            // её собственные каверы — не исходники (иначе «спой Lately» взяло бы свежий кавер и перепело саму себя)
            .Where(f => !f.Contains(@"\Каверы Клавы\", StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(f).Contains("(Клава"))
            .Select(f => (f, score: words.Count(w => Path.GetFileNameWithoutExtension(f).ToLowerInvariant().Replace('_', ' ').Contains(w))))
            .Where(x => x.score == words.Length || (words.Length > 2 && x.score >= words.Length - 1))
            .OrderByDescending(x => x.score).ThenByDescending(x => File.GetLastWriteTimeUtc(x.f))
            .Select(x => x.f).FirstOrDefault();
    }

    // ---------- меню и студия ----------

    private ToolStripMenuItem BuildMusicMenu()
    {
        var m = new ToolStripMenuItem("🎵 Музыка: слух и пение");
        m.DropDownItems.Add("🎧 Послушать, что играет", null, (_, _) => Bubble(RunFromMenu(() => MusicToolAsync("listen", new JsonObject()))));
        m.DropDownItems.Add("📂 Разобрать трек…", null, (_, _) => { if (PickSong() is { } f) Bubble(RunFromMenu(() => MusicToolAsync("analyze", new JsonObject { ["path"] = f }))); });
        m.DropDownItems.Add("🎤 Спеть кавер по-русски…", null, (_, _) => { if (PickSong() is { } f) Bubble(RunFromMenu(() => MusicToolAsync("sing", new JsonObject { ["path"] = f }))); });
        m.DropDownItems.Add("🎤 Спеть в оригинале своим голосом…", null, (_, _) => { if (PickSong() is { } f) Bubble(RunFromMenu(() => MusicToolAsync("sing", new JsonObject { ["path"] = f, ["lang"] = "orig" }))); });
        m.DropDownItems.Add("🎹 Похожая основа по треку…", null, (_, _) => { if (PickSong() is { } f) Bubble(RunFromMenu(() => MusicToolAsync("similar", new JsonObject { ["path"] = f }))); });
        m.DropDownItems.Add("🎹 Сочинить свою мелодию", null, (_, _) => Bubble(RunFromMenu(() => MusicToolAsync("compose", new JsonObject()))));
        m.DropDownItems.Add("🎻 Сыграть на скрипке (Паганини)", null, (_, _) => Bubble(RunFromMenu(() => MusicToolAsync("violin", new JsonObject()))));
        var last = new ToolStripMenuItem("▶ Спеть последний кавер", null, (_, _) => { if (LastCover() is { } c) PlaySong(c); });
        m.DropDownItems.Add(last);
        m.DropDownItems.Add("⏹ Остановить песню", null, (_, _) => StopSong());
        var cancel = new ToolStripMenuItem("✖ Отменить музыкальную задачу", null, (_, _) => _musicCts?.Cancel());
        m.DropDownItems.Add(cancel);
        m.DropDownItems.Add(new ToolStripSeparator());
        m.DropDownItems.Add("🎛 Музыкальная студия…", null, (_, _) => ShowStudio(null));
        m.DropDownItems.Add("📁 Папка каверов", null, (_, _) =>
        {
            var d = Path.Combine(MusicRoot, "covers");
            Directory.CreateDirectory(d);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{d}\"") { UseShellExecute = true })?.Dispose();
        });
        m.DropDownOpening += (_, _) =>
        {
            last.Enabled = LastCover() is not null;
            cancel.Enabled = _musicCts is not null;
            cancel.Text = _musicCts is null ? "✖ Отменить музыкальную задачу" : $"✖ Отменить: {_musicDoing}";
        };
        return m;
    }

    private static string RunFromMenu(Func<Task<string>> f)
    {
        var t = f();
        return t.IsCompleted ? t.Result : "🎵 Взялась…";
    }

    private static string? PickSong()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Песня для Клавы",
            Filter = "Музыка|*.mp3;*.flac;*.ogg;*.wav;*.m4a;*.opus;*.aac;*.wma;*.webm;*.mp4|Все файлы|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
        return dlg.ShowDialog() == DialogResult.OK ? dlg.FileName : null;
    }

    private void ShowStudio(string? file)
    {
        if (_studio is null || _studio.IsDisposed)
        {
            _studio = new MusicStudioForm(MusicRoot, new MusicStudioForm.Actions
            {
                Analyze = f => Bubble(RunFromMenu(() => MusicToolAsync("analyze", new JsonObject { ["path"] = f }))),
                Sing = (f, orig, lyrics) => Bubble(RunFromMenu(() => MusicToolAsync("sing", new JsonObject { ["path"] = f, ["lang"] = orig ? "orig" : "ru", ["lyrics"] = lyrics ?? "" }))),
                PlayCover = c => PlaySong(c),
                StopSong = StopSong,
                Cancel = () => _musicCts?.Cancel(),
                AskOpinion = f => Bubble(RunFromMenu(() => MusicToolAsync("analyze", new JsonObject { ["path"] = f }))),
                Similar = f => Bubble(RunFromMenu(() => MusicToolAsync("similar", new JsonObject { ["path"] = f }))),
            });
        }
        if (file is not null) _studio.LoadTrack(file);
        _studio.Show();
        _studio.Activate();
    }
}
