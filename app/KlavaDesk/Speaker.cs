using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>Профиль голоса: имя, время последнего обновления, сколько записей накоплено и сколько в них минут речи.</summary>
internal sealed record SpeakerProfile(string Name, DateTime Updated, int Samples, double Minutes = 0);

/// <summary>
/// Результат идентификации: имя говорящего или null (никого узнаваемого), близость лучшего, ближайший кандидат
/// и близость к КАЖДОМУ профилю — по ним решается «свой из компании или чужой голос».
/// </summary>
internal sealed record SpeakerMatch(string? Name, double Similarity, string? Candidate, Dictionary<string, double>? Scores = null);

/// <summary>
/// Что за модель голосов и какой у неё порог «тот же человек»: Threshold — для обычных фраз, ShortThreshold — для
/// коротких (до 1,2 с; NaN — сервер не знает, берётся Threshold − 0,06), Ear — итог последней тренировки слуха.
/// </summary>
internal sealed record SpeakerInfo(string Model, double Threshold, int Profiles, double ShortThreshold = double.NaN, string Ear = "");

/// <summary>
/// Голосовые профили: клиент speaker_id.py (большой комплект) или модель 3D-Speaker в процессе (лёгкий). enroll/identify/list/remove.
/// Всё асинхронно, ошибки логируются, наружу — null.
/// </summary>
internal sealed class Speaker
{
    private readonly KlavaConfig _cfg;
    private readonly HttpClient _http = NetProxy.Client(null, TimeSpan.FromSeconds(15));
    private readonly HttpClient _fast = NetProxy.Client(null, TimeSpan.FromSeconds(6));

    public Speaker(KlavaConfig cfg) => _cfg = cfg;

    private string Base => _cfg.SpeakerIdUrl.TrimEnd('/');

    /// <summary>Профили прямо в процессе (sherpa-onnx: TitaNet или 3D-Speaker), без speaker_id.py.</summary>
    private bool LiteMode => Lite.InProcessSpeakers(_cfg);

    /// <summary>Почему последняя запись в профиль не принята (пусто — принята или причина неизвестна).</summary>
    public string LastReason { get; private set; } = "";

    /// <summary>Вызов лёгких профилей в пуле потоков (модель считает на процессоре); ошибка — в журнал и fallback.</summary>
    private async Task<T> LiteAsync<T>(string what, Func<LiteSpeakers, T> f, T fallback)
    {
        try { return await Task.Run(() => f(Lite.Speakers(_cfg))); }
        // фраза короче полсекунды («ага», «да») — опознавать нечего, это не сбой: раньше 35 «ОШИБОК» за вечер в журнале
        catch (InvalidDataException ex) when (what == "identify" && ex.Message.StartsWith("слишком коротко", StringComparison.Ordinal)) { return fallback; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Log.Error($"speaker.{what} (лёгкий)", ex);
            return fallback;
        }
    }

    /// <summary>Сервер поднят и модель загружена.</summary>
    public async Task<bool> AliveAsync()
    {
        if (LiteMode) return await LiteAsync("alive", _ => true, false);
        try { using var r = await _http.GetAsync($"{Base}/health"); return r.IsSuccessStatusCode; }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }

    /// <summary>Модель и рекомендованный порог сервера (старый сервер без /info — resemblyzer с порогом 0,75).</summary>
    public async Task<SpeakerInfo?> InfoAsync()
    {
        if (LiteMode)
            return await LiteAsync<SpeakerInfo?>("info", l => new SpeakerInfo(l.Model, l.DecisionThreshold, l.Names().Count, l.ShortThreshold, l.EarReport), null);
        try
        {
            using var r = await _http.GetAsync($"{Base}/info");
            if (!r.IsSuccessStatusCode) return new SpeakerInfo("resemblyzer", 0.75, 0);
            var n = JsonNode.Parse(await r.Content.ReadAsStringAsync());
            return new SpeakerInfo(n?["model"]?.GetValue<string>() ?? "?", n?["threshold"]?.GetValue<double>() ?? 0.75, n?["profiles"]?.GetValue<int>() ?? 0);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            Log.Error("speaker.info", ex);
            return null;
        }
    }

    /// <summary>Список записанных профилей.</summary>
    public async Task<List<SpeakerProfile>> ListAsync()
    {
        if (LiteMode) return await LiteAsync("list", l => l.List(), new List<SpeakerProfile>());
        try
        {
            var s = await _http.GetStringAsync($"{Base}/list");
            var arr = JsonNode.Parse(s) as JsonArray;
            return (arr ?? []).Select(n => new SpeakerProfile(
                n?["name"]?.GetValue<string>() ?? "",
                DateTimeOffset.FromUnixTimeSeconds((long)(n?["updated"]?.GetValue<double>() ?? 0)).LocalDateTime,
                n?["samples"]?.GetValue<int>() ?? 1))
                .Where(p => p.Name.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Error("speaker.list", ex);
            return new List<SpeakerProfile>();
        }
    }

    /// <summary>Записать новый профиль (или обновить существующий) под именем. append=true — доучить: добавить новый сэмпл в накопленный массив.</summary>
    public async Task<bool> EnrollAsync(string name, byte[] wav, bool append = false)
    {
        if (LiteMode)
            return await LiteAsync("enroll", l =>
            {
                var ok = l.Enroll(name, wav, append);
                LastReason = ok ? "" : l.LastReason;
                return ok;
            }, false);
        try
        {
            using var body = new ByteArrayContent(wav);
            body.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            var suffix = append ? "&append=1" : "";
            using var r = await _http.PostAsync($"{Base}/enroll?name={Uri.EscapeDataString(name)}{suffix}", body);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error("speaker.enroll", ex);
            return false;
        }
    }

    /// <summary>Опознать кто говорит по короткому wav. null.Name — никого уверенного нет.</summary>
    public async Task<SpeakerMatch?> IdentifyAsync(byte[] wav)
    {
        if (LiteMode) return await LiteAsync<SpeakerMatch?>("identify", l => l.Identify(wav), null);
        try
        {
            using var body = new ByteArrayContent(wav);
            body.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            // опознание стоит на пути каждой фразы — дольше 6 с ждать нельзя: лучше «не узнан», чем тишина в ответ
            using var r = await _fast.PostAsync($"{Base}/identify", body);
            if (!r.IsSuccessStatusCode) return null;
            var n = JsonNode.Parse(await r.Content.ReadAsStringAsync());
            Dictionary<string, double>? scores = null;
            if (n?["scores"] is JsonObject so)
            {
                scores = new Dictionary<string, double>();
                foreach (var (k, v) in so) if (v is not null) scores[k] = v.GetValue<double>();
            }
            return new SpeakerMatch(
                n?["name"]?.GetValue<string>(),
                n?["similarity"]?.GetValue<double>() ?? 0,
                n?["candidate"]?.GetValue<string>(),
                scores);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Error("speaker.identify", ex);
            return null;
        }
    }

    /// <summary>Удалить профиль.</summary>
    public async Task<bool> RemoveAsync(string name)
    {
        if (LiteMode) return await LiteAsync("remove", l => l.Remove(name), false);
        try
        {
            using var r = await _http.DeleteAsync($"{Base}/remove?name={Uri.EscapeDataString(name)}");
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error("speaker.remove", ex);
            return false;
        }
    }

    /// <summary>Получить исходный wav профиля (для прослушивания в UI).</summary>
    public async Task<byte[]?> GetSampleAsync(string name)
    {
        if (LiteMode) return await LiteAsync<byte[]?>("sample", l => l.Sample(name), null);
        try
        {
            using var r = await _http.GetAsync($"{Base}/sample?name={Uri.EscapeDataString(name)}");
            return r.IsSuccessStatusCode ? await r.Content.ReadAsByteArrayAsync() : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error("speaker.sample", ex);
            return null;
        }
    }

    /// <summary>Переименовать профиль (без записи заново — embedding сохраняется).</summary>
    public async Task<bool> RenameAsync(string from, string to)
    {
        if (LiteMode) return await LiteAsync("rename", l => l.Rename(from, to), false);
        try
        {
            using var r = await _http.PostAsync($"{Base}/rename?from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}", new StringContent(""));
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error("speaker.rename", ex);
            return false;
        }
    }

    /// <summary>Штраф модели за ошибку: удалить ложный сэмпл из wrongName и доучить correctName.</summary>
    public async Task<bool> PenalizeAndCorrectAsync(string wrongName, string correctName, byte[] wav)
    {
        if (LiteMode) return await LiteAsync("correct", l => l.PenalizeAndCorrect(wrongName, correctName, wav), false);
        try
        {
            using var body = new ByteArrayContent(wav);
            body.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            using var r = await _http.PostAsync($"{Base}/correct?wrong={Uri.EscapeDataString(wrongName)}&correct={Uri.EscapeDataString(correctName)}", body);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error("speaker.correct", ex);
            return false;
        }
    }

    /// <summary>Параллельное самообучение: аккуратно доучить профиль на лету с проверкой контрастного марджина.</summary>
    public async Task<bool> AdaptAsync(string name, byte[] wav)
    {
        if (LiteMode) return await LiteAsync("adapt", l => l.Adapt(name, wav), false);
        try
        {
            using var body = new ByteArrayContent(wav);
            body.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            using var r = await _http.PostAsync($"{Base}/adapt?name={Uri.EscapeDataString(name)}", body);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Error("speaker.adapt", ex);
            return false;
        }
    }

    /// <summary>Проверка профилей: записи, которые похоже не его голос (только в процессе; у WavLM-сервера — пусто).</summary>
    public async Task<List<SuspectSample>> AuditAsync() =>
        LiteMode ? await LiteAsync("audit", l => l.Audit(), new List<SuspectSample>()) : [];

    /// <summary>Сомнительные записи — в карантин data\speakers\_карантин (не удаляются).</summary>
    public async Task<int> QuarantineAsync(IEnumerable<SuspectSample> items) =>
        LiteMode ? await LiteAsync("quarantine", l => { var n = 0; foreach (var i in items) { l.Quarantine(i); n++; } return n; }, 0) : 0;

    /// <summary>
    /// Обучить слух по записям всех профилей и подобрать пороги (самопроверка «как было / как стало»). Только для
    /// профилей в процессе; у питоновского WavLM-сервера обучения нет.
    /// </summary>
    public async Task<(bool Ok, string Report)> TrainEarAsync(IProgress<string>? progress)
    {
        if (!LiteMode) return (false, "обучение слуха работает с профилями в процессе (speakerBackend: auto)");
        return await LiteAsync("train", l => l.TrainEar(progress), (false, "не получилось обучить — глянь журнал"));
    }

    /// <summary>Долгая запись профиля: вся речь (до 15 минут) → отобранные куски своего голоса.</summary>
    public async Task<LongEnrollResult> LongEnrollAsync(string name, byte[] wav, bool replace, IProgress<string>? progress)
    {
        if (!LiteMode) return new LongEnrollResult(false, 0, 0, 0, "долгая запись работает с профилями в процессе (speakerBackend: auto)");
        return await LiteAsync("long-enroll", l => l.LongEnroll(name, Lite.ReadWav16k(wav), replace, progress),
            new LongEnrollResult(false, 0, 0, 0, "не получилось разобрать запись — глянь журнал"));
    }
}
