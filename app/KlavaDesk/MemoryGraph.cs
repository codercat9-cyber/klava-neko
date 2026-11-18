using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Горячая ассоциативная память Клавы (друг 29.09, PipelineKlava.md: «граф памяти как у ИИ-стримерши, без ютубовского
/// говна, и без задержек»). Два контура поверх банка памяти (MemoryBank):
///  - горячий: факты-узлы (кто — что — кого/что, человеческой фразой, с сущностями и чувством). Каждую реплику:
///    сущности из слов друга (со склонениями: «Аксюте» = «Аксюта») зажигают плавающий подграф, он гаснет за ~6 реплик
///    (затухание e^(−0,35·Δреплик)); кандидаты — факты зажжённых сущностей плюс ближайшие по смыслу (вектор bge-m3);
///    счёт = сходство × затухание × 1/(1+ln(1+сколько раз вспоминали)) × штраф за недавнее (45 с — почти ноль, 10 мин —
///    вполовину) — так память не зацикливается на одном любимом факте («эффект шибари»). До 3 фактов в подмес, ~0,1 с.
///  - холодный: всё, включая архив, — инструмент memory recall (там и эпизоды банка).
/// Факты достаёт фоновый «гном» — та же раскладка журнала (MemoryBank), что делит его на эпизоды: мозг заодно
/// возвращает факты; ответы это не задерживает. «Сон» раз в сутки: одинаковые по смыслу факты сливаются, забытые
/// (ни разу не вспомнились за 60 дней) уходят в архив.
/// Векторы — bge-m3 (Q8, GGUF) в своём llama-server на процессоре (порт embedPort): питона нет, в процесс ничего не грузим.
/// Хранение (01.10, «буфер сбрасывается, каждый перезапуск на него влияет»): раньше граф лежал одним файлом в 1,7 МБ
/// вместе с векторами и на выходе писался прямо поверх себя — Клаву закрыли посреди записи, файл обрезало, и следующий
/// запуск начал бы граф с нуля. Теперь graph.json — только факты (маленький, пишется через SafeFile: целиком или никак,
/// с копиями), векторы — отдельно в graph.vec (это кэш: пропал — пересчитается). У каждого факта — когда он случился
/// (Made): в подмес он идёт с датой, а не голой строкой.
/// </summary>
internal sealed partial class MemoryGraph
{
    public static MemoryGraph? Current { get; set; }

    internal sealed class Fact
    {
        public string Id { get; set; } = "";
        public string Text { get; set; } = "";
        public string Subject { get; set; } = "";
        public string Predicate { get; set; } = "";
        public string Object { get; set; } = "";
        public List<string> Entities { get; set; } = [];
        /// <summary>Чувство вокруг факта — метка эмоции Клавы (радость, раздражение, нежность…) или пусто.</summary>
        public string Emotion { get; set; } = "";
        public string Source { get; set; } = "";
        public DateTime Made { get; set; }
        public int Uses { get; set; }
        public DateTime LastUsed { get; set; }
        /// <summary>Вектор внутри файла — только у графа старого вида (до 01.10); теперь векторы лежат в graph.vec.</summary>
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public string? Vec { get; set; }
    }

    internal sealed class Store
    {
        public List<Fact> Facts { get; set; } = [];
        public List<Fact> Archive { get; set; } = [];
        /// <summary>Канон сущности → её имена (со склонениями и прозвищами).</summary>
        public Dictionary<string, List<string>> Entities { get; set; } = new(StringComparer.Ordinal);
        public DateTime LastSleep { get; set; }
        public bool Seeded { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly KlavaConfig _cfg;
    private readonly string _file;
    private readonly string _vecFile;
    private readonly object _disk = new();
    private volatile bool _vecDirty;
    private readonly object _lock = new();
    private Store _store;
    private readonly HttpClient _http = NetProxy.Client(null, TimeSpan.FromSeconds(20));
    private Process? _server;
    private volatile bool _embedOk;
    private readonly Dictionary<string, float[]> _vec = new(StringComparer.Ordinal);   // id → вектор (кэш из Vec)
    private readonly Dictionary<string, string> _alias = new(StringComparer.Ordinal);  // основа имени → канон
    private readonly Dictionary<string, int> _active = new(StringComparer.Ordinal);    // сущность → номер реплики
    private int _turn;
    private int _saving;

    public MemoryGraph(KlavaConfig cfg)
    {
        _cfg = cfg;
        var dir = cfg.Abs(@"data\memory");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "graph.json");
        _vecFile = Path.Combine(dir, "graph.vec");
        var broken = File.Exists(_file) ? File.ReadAllText(_file) : null;
        _store = SafeFile.Load(_file, t => JsonSerializer.Deserialize<Store>(t, Json)) ?? Salvage(broken) ?? new Store();
        LoadVectors();
        // граф старого вида: векторы лежали в самих фактах — переносим в кэш и вон из файла
        var moved = 0;
        foreach (var f in _store.Facts.Concat(_store.Archive))
        {
            if (f.Vec is { Length: > 0 } b64)
            {
                try { _vec[f.Id] = FromB64(b64); moved++; } catch (FormatException) { }
            }
            f.Vec = null;
            f.Entities = f.Entities
                .Select(e => e == "Релиз Клавы" && !f.Text.Contains("релиз", StringComparison.OrdinalIgnoreCase) ? "Клава" : People.Canon(e) is { Length: > 0 } c ? c : e)
                .Distinct(StringComparer.Ordinal).ToList();
        }
        if (moved > 0) { _vecDirty = true; SaveNow(); }
        RebuildAliases();
    }

    /// <summary>
    /// Обрезанный graph.json старого вида (запись оборвали посреди файла): целые факты из начала списка спасаются,
    /// остальное досеется из банка памяти и вернётся при перекладке журнала. null — спасать нечего.
    /// </summary>
    internal static Store? Salvage(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var at = text.IndexOf("\"Facts\"", StringComparison.Ordinal);
        if (at < 0 || (at = text.IndexOf('[', at)) < 0) return null;
        var store = new Store();
        int depth = 0, start = -1;
        bool inString = false, escaped = false;
        for (var i = at + 1; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            if (c == '"') inString = true;
            else if (c == '{') { if (depth++ == 0) start = i; }
            else if (c == '}' && --depth == 0 && start >= 0)
            {
                try { if (JsonSerializer.Deserialize<Fact>(text[start..(i + 1)], Json) is { Text.Length: > 0 } f) store.Facts.Add(f); }
                catch (JsonException) { }
                start = -1;
            }
            else if (c == ']' && depth == 0) break;
        }
        if (store.Facts.Count == 0) return null;
        Log.Write($"граф памяти: graph.json обрезан — спасено целых фактов {store.Facts.Count}, остальное досеется из банка памяти");
        return store;
    }

    private void LoadVectors()
    {
        if (!File.Exists(_vecFile)) return;
        try
        {
            using var r = new BinaryReader(File.OpenRead(_vecFile), Encoding.UTF8);
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var id = r.ReadString();
                var v = new float[r.ReadInt32()];
                for (var j = 0; j < v.Length; j++) v[j] = r.ReadSingle();
                _vec[id] = v;
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or OverflowException or ArgumentException)
        {
            _vec.Clear();
            Log.Write($"граф памяти: кэш векторов не прочитался ({ex.Message}) — пересчитаю");
        }
    }

    private byte[] VectorBytes()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(_vec.Count);
            foreach (var (id, v) in _vec)
            {
                w.Write(id);
                w.Write(v.Length);
                foreach (var x in v) w.Write(x);
            }
        }
        return ms.ToArray();
    }

    public string Describe()
    {
        lock (_lock)
            return $"фактов {_store.Facts.Count} (в архиве {_store.Archive.Count}), сущностей {_store.Entities.Count}, " +
                   $"векторов {_vec.Count}, векторизатор {(_embedOk ? "есть" : "нет")}";
    }

    // ---------------- векторизатор ----------------

    /// <summary>Поднять свой llama-server с bge-m3 (или взять уже работающий на порту), засеять граф, досчитать векторы, «сон».</summary>
    public async Task StartAsync(MemoryBank bank, FriendMemory friends)
    {
        var model = _cfg.Abs(_cfg.EmbedModel);
        if (File.Exists(model) && !await EmbedAliveAsync().ConfigureAwait(false))
        {
            var exe = _cfg.Abs(_cfg.BrainExe);
            if (File.Exists(exe))
            {
                var psi = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                };
                foreach (var a in new[] { "-m", model, "--embedding", "--pooling", "cls", "--host", "127.0.0.1", "--port", _cfg.EmbedPort.ToString(),
                                          "-t", _cfg.EmbedThreads.ToString(), "-c", "2048", "-ub", "512", "-ngl", "0", "--parallel", "2" })
                    psi.ArgumentList.Add(a);
                try
                {
                    _server = Process.Start(psi);
                    if (_server is not null)
                    {
                        _server.OutputDataReceived += (_, _) => { };
                        _server.ErrorDataReceived += (_, _) => { };
                        _server.BeginOutputReadLine();
                        _server.BeginErrorReadLine();
                        try { Native.TieToLifetime(_server); } catch (InvalidOperationException) { }
                        Log.Write($"граф памяти: векторизатор bge-m3 запущен (PID {_server.Id}, порт {_cfg.EmbedPort})");
                    }
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    Log.Error("граф памяти: векторизатор не запустился", ex);
                }
            }
            for (var i = 0; i < 60 && !await EmbedAliveAsync().ConfigureAwait(false); i++) await Task.Delay(500).ConfigureAwait(false);
        }
        _embedOk = await EmbedAliveAsync().ConfigureAwait(false);
        if (!_embedOk) Log.Write($"граф памяти: векторизатора нет ({model}) — ищу только по словам");
        if (!_store.Seeded) Seed(bank, friends);
        await FillVectorsAsync().ConfigureAwait(false);
        if (DateTime.Now - _store.LastSleep > TimeSpan.FromHours(20)) Sleep();
        Log.Write($"граф памяти: {Describe()}");
    }

    public void Stop()
    {
        try { if (_server is { HasExited: false }) _server.Kill(); }
        catch (InvalidOperationException) { }
        // на выходе — сразу и в этом потоке: фоновую запись процесс уже не дождётся
        SaveNow();
    }

    /// <summary>Записать граф сейчас, в этом потоке: факты — целиком или никак (SafeFile), векторы — если менялись.</summary>
    public void SaveNow()
    {
        try
        {
            string text;
            byte[]? vectors = null;
            lock (_lock)
            {
                text = JsonSerializer.Serialize(_store, Json);
                if (_vecDirty) { vectors = VectorBytes(); _vecDirty = false; }
            }
            lock (_disk)
            {
                SafeFile.Write(_file, text);
                if (vectors is not null) SafeFile.WriteBytes(_vecFile, vectors);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Error("граф памяти: не записался", ex); }
    }

    /// <summary>Досчитать векторы новым фактам и записать граф — прежде чем гасить (проверки, перекладка из консоли).</summary>
    public Task FlushAsync() => FillVectorsAsync();

    /// <summary>Подключиться к векторизатору без засева графа — для проверки (--selftest).</summary>
    internal async Task<bool> ConnectAsync() => _embedOk = await EmbedAliveAsync().ConfigureAwait(false);

    private async Task<bool> EmbedAliveAsync()
    {
        try
        {
            using var r = await _http.GetAsync($"http://127.0.0.1:{_cfg.EmbedPort}/health").ConfigureAwait(false);
            return r.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    /// <summary>Векторы (нормированные) для текстов; null — векторизатор не ответил за timeout.</summary>
    public async Task<float[][]?> EmbedAsync(IReadOnlyList<string> texts, TimeSpan timeout)
    {
        if (!_embedOk || texts.Count == 0) return null;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            using var resp = await _http.PostAsJsonAsync($"http://127.0.0.1:{_cfg.EmbedPort}/v1/embeddings",
                new { input = texts.Select(t => t.Length > 1500 ? t[..1500] : t).ToArray() }, cts.Token).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var root = JsonNode.Parse(await resp.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false))!;
            var data = root["data"]!.AsArray();
            var outv = new float[texts.Count][];
            foreach (var d in data)
            {
                var idx = d!["index"]?.GetValue<int>() ?? 0;
                var arr = d["embedding"]!.AsArray().Select(x => (float)x!.GetValue<double>()).ToArray();
                var norm = MathF.Sqrt(arr.Sum(x => x * x));
                if (norm > 0) for (var i = 0; i < arr.Length; i++) arr[i] /= norm;
                if (idx >= 0 && idx < outv.Length) outv[idx] = arr;
            }
            return outv.All(v => v is not null) ? outv : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or NullReferenceException)
        {
            return null;
        }
    }

    private static float[] FromB64(string s)
    {
        var b = Convert.FromBase64String(s);
        var v = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, v, 0, b.Length);
        return v;
    }

    private static float Dot(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var s = 0f;
        for (var i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>Досчитать векторы фактам без них — пачками по 16, в фоне.</summary>
    private async Task FillVectorsAsync()
    {
        if (!_embedOk) return;
        while (true)
        {
            List<Fact> todo;
            lock (_lock) todo = _store.Facts.Concat(_store.Archive).Where(f => !_vec.ContainsKey(f.Id)).Take(16).ToList();
            if (todo.Count == 0) break;
            var vs = await EmbedAsync(todo.Select(f => f.Text).ToList(), TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (vs is null) { Log.Write("граф памяти: векторизатор не ответил — векторы досчитаю позже"); break; }
            lock (_lock)
            {
                for (var i = 0; i < todo.Count; i++) _vec[todo[i].Id] = vs[i];
                _vecDirty = true;
            }
        }
        Save(force: true);
    }

    // ---------------- сущности ----------------

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordRx();

    /// <summary>Основа слова: без регистра, ё → е, срезано частое окончание (падежи, числа, прилагательные).</summary>
    internal static string Stem(string word)
    {
        var w = word.ToLowerInvariant().Replace('ё', 'е');
        if (w.Length <= 3) return w;
        foreach (var end in Endings)
            if (w.Length - end.Length >= 3 && w.EndsWith(end, StringComparison.Ordinal)) return w[..^end.Length];
        return w;
    }

    private static readonly string[] Endings =
    [
        "ями", "ами", "ого", "его", "ому", "ему", "ыми", "ими", "иях", "ах", "ях", "ов", "ев", "ей", "ом", "ем", "ой", "ый", "ий",
        "ая", "яя", "ое", "ее", "ые", "ие", "ую", "юю", "ым", "им", "ых", "их", "ам", "ям", "ою", "ею", "а", "я", "о", "е", "ы", "и", "у", "ю", "ь", "й",
    ];

    private static string Key(string name) =>
        string.Join(' ', WordRx().Matches(name.Replace('_', ' ')).Select(m => Stem(m.Value)).Where(s => s.Length > 0));

    private void RebuildAliases()
    {
        lock (_lock)
        {
            _alias.Clear();
            foreach (var (canon, names) in _store.Entities)
                foreach (var n in names.Append(canon))
                {
                    var k = Key(n);
                    if (k.Length >= 3) _alias[k] = canon;
                }
        }
    }

    /// <summary>Канон сущности: знакомое имя (с учётом склонения) или новое.</summary>
    private string Resolve(string raw)
    {
        var name = raw.Replace('_', ' ').Trim();
        if (name.Length == 0) return "";
        // человек, которого она знает по голосу, — всегда под именем профиля («Волк», «Гена», «Серого Волка» → «Серый Волк»)
        if (People.Profile(name) is { } profile) name = profile.Replace('_', ' ');
        // она сама — всегда «Клава»: прежняя догадка «одно слово = часть длинного имени» приклеивала её к «Релиз Клавы»
        if (Stem(name) == "клав") return "Клава";
        var k = Key(name);
        lock (_lock)
        {
            if (_alias.TryGetValue(k, out var canon)) return canon;
            var canonName = char.ToUpper(name[0]) + name[1..];
            _store.Entities[canonName] = [name];
            if (k.Length >= 3) _alias[k] = canonName;
            return canonName;
        }
    }

    /// <summary>Сущности, названные в тексте (по основам слов; многословные — когда есть все слова).</summary>
    private HashSet<string> Mentioned(string text)
    {
        var stems = WordRx().Matches(text.Replace('_', ' ')).Select(m => Stem(m.Value)).Where(s => s.Length >= 3).ToHashSet(StringComparer.Ordinal);
        var found = new HashSet<string>(StringComparer.Ordinal);
        lock (_lock)
            foreach (var (k, canon) in _alias)
            {
                var parts = k.Split(' ');
                if (parts.All(stems.Contains)) found.Add(canon);
            }
        return found;
    }

    // ---------------- наполнение ----------------

    /// <summary>Факт от «гнома» (раскладка журнала) или засева.</summary>
    public sealed record FactIn(string Text, string Subject, string Predicate, string Object, IReadOnlyList<string> Entities, string Emotion, string Source, DateTime Made);

    /// <summary>Добавить факты (векторы — в фоне). Повтор того же текста не дублируется.</summary>
    public void AddFacts(IEnumerable<FactIn> facts)
    {
        var added = 0;
        foreach (var f in facts)
        {
            var text = f.Text.Trim();
            if (text.Length < 8) continue;
            var ents = f.Entities.Append(f.Subject).Append(f.Object).Where(e => e.Trim().Length >= 2 && e.Trim().Length <= 40)
                .Select(Resolve).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).Take(8).ToList();
            lock (_lock)
            {
                if (_store.Facts.Any(x => x.Text.Equals(text, StringComparison.OrdinalIgnoreCase))) continue;
                _store.Facts.Add(new Fact
                {
                    Id = $"f{f.Made:yyMMddHHmm}-{Guid.NewGuid().ToString("N")[..8]}", Text = text.Length > 300 ? text[..300] : text,
                    Subject = f.Subject.Trim(), Predicate = f.Predicate.Trim(), Object = f.Object.Trim(), Entities = ents,
                    Emotion = f.Emotion.Trim().ToLowerInvariant(), Source = f.Source, Made = f.Made,
                });
                added++;
            }
        }
        if (added == 0) return;
        Log.Write($"граф памяти: +{added} фактов");
        _ = FillVectorsAsync();
    }

    /// <summary>
    /// День переложен банком заново: прежние факты этого дня (и засеянные из его эпизодов) уходят, приходят новые.
    /// Сколько раз факт вспоминался — у новых с нуля: это другие формулировки.
    /// </summary>
    public void ReplaceDay(string day, IReadOnlyList<FactIn> facts)
    {
        int removed;
        lock (_lock)
        {
            var gone = _store.Facts.Where(f => f.Source.StartsWith($"журнал {day}", StringComparison.Ordinal) || f.Source.StartsWith($"эпизод {day}", StringComparison.Ordinal)).ToList();
            foreach (var f in gone) { _store.Facts.Remove(f); _vec.Remove(f.Id); }
            removed = gone.Count;
            if (removed > 0) _vecDirty = true;
        }
        Log.Write($"граф памяти: день {day} переложен — фактов убрано {removed}, пришло {facts.Count}");
        AddFacts(facts);
        if (facts.Count == 0) Save(force: true);
    }

    /// <summary>Первый запуск: граф из того, что уже есть, — эпизоды банка и факты о друзьях (без мозга, сразу).</summary>
    private void Seed(MemoryBank bank, FriendMemory friends)
    {
        var list = new List<FactIn>();
        foreach (var e in bank.AllEpisodes())
            list.Add(new FactIn($"{e.Title}: {e.Summary}", e.People.FirstOrDefault() ?? "", "эпизод", "", e.People.Concat(e.Keys).ToList(), "",
                $"эпизод {e.Id}", DateTime.TryParse($"{e.Day} {e.From}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : DateTime.Now));
        foreach (var (who, fact) in friends.AllFacts())
            list.Add(new FactIn($"{who.Replace('_', ' ')}: {fact}", who, "о человеке", "", [who], "", "друзья", DateTime.Now));
        // в графе, спасённом из обрезанного файла, часть этого уже есть — AddFacts повторы по тексту не берёт
        AddFacts(list);
        lock (_lock) _store.Seeded = true;
        Save(force: true);
        Log.Write($"граф памяти: засеян из банка и друзей — {list.Count} фактов");
    }

    // ---------------- горячий контур ----------------

    /// <summary>
    /// Подмес к реплике: до 3 фактов к слову (см. счёт в описании класса), каждый — с датой, когда это было. Бюджет
    /// ~0,3 с — векторизатор не успел, идём по одним сущностям. Пусто — нечего.
    /// </summary>
    public async Task<List<Recall>> ContextForAsync(string? speaker, string userText)
    {
        var sw = Stopwatch.StartNew();
        var ents = Mentioned(userText);
        if (!string.IsNullOrWhiteSpace(speaker)) ents.Add(Resolve(speaker));
        int turn;
        lock (_lock)
        {
            turn = ++_turn;
            foreach (var e in ents) _active[e] = turn;
            foreach (var k in _active.Where(kv => turn - kv.Value > 6).Select(kv => kv.Key).ToList()) _active.Remove(k);
        }
        var qv = (await EmbedAsync([userText], TimeSpan.FromMilliseconds(_cfg.EmbedBudgetMs)).ConfigureAwait(false))?[0];
        var now = DateTime.Now;
        var scored = new List<(Fact F, float Score, float Sim)>();
        lock (_lock)
        {
            foreach (var f in _store.Facts)
            {
                var linked = f.Entities.Where(_active.ContainsKey).ToList();
                var sim = qv is not null && _vec.TryGetValue(f.Id, out var fv) ? Dot(qv, fv) : 0f;
                // без вектора — только факты зажжённых сущностей, с условным сходством
                if (qv is null && linked.Count == 0) continue;
                if (qv is null) sim = 0.6f;
                if (sim < 0.5f && linked.Count == 0) continue;
                var decay = linked.Count > 0 ? linked.Max(e => MathF.Exp(-0.35f * (turn - _active[e]))) : 0.55f;
                var damper = 1f / (1f + MathF.Log(1f + f.Uses));
                var since = now - f.LastUsed;
                var cool = since < TimeSpan.FromSeconds(45) ? 0.05f : since < TimeSpan.FromMinutes(10) ? 0.5f : 1f;
                scored.Add((f, sim * decay * damper * cool, sim));
            }
        }
        var top = scored.Where(x => x.Sim >= 0.52f && x.Score >= 0.3f).OrderByDescending(x => x.Score).Take(3).ToList();
        if (top.Count == 0)
        {
            if (sw.ElapsedMilliseconds > 400) Log.Write($"граф памяти: пусто за {sw.ElapsedMilliseconds} мс");
            return [];
        }
        lock (_lock)
            foreach (var (f, _, _) in top) { f.Uses++; f.LastUsed = now; }
        Save();
        Log.Write($"граф памяти: {top.Count} к слову за {sw.ElapsedMilliseconds} мс — " +
                  string.Join(" | ", top.Select(t => $"{t.Score:F2} {Short(t.F.Text, 50)}")));
        // знание о человеке (из FriendMemory) события не имеет — без даты; остальное — когда случилось
        return top.Select(t => new Recall(t.F.Source == "друзья" ? null : t.F.Made, t.F.Source == "друзья" ? "знаешь" : "",
            t.F.Text + (t.F.Emotion.Length > 0 ? $" (тебе от этого: {t.F.Emotion})" : ""), t.F.Id)).ToList();
    }

    private static string Short(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ---------------- холодный контур ----------------

    /// <summary>Глубокий поиск (инструмент memory recall): по смыслу и словам среди всех фактов и архива.</summary>
    public async Task<List<string>> RecallAsync(string query, int top = 8)
    {
        var qv = (await EmbedAsync([query], TimeSpan.FromSeconds(5)).ConfigureAwait(false))?[0];
        var stems = WordRx().Matches(query).Select(m => Stem(m.Value)).Where(s => s.Length >= 3).ToHashSet(StringComparer.Ordinal);
        lock (_lock)
        {
            return _store.Facts.Concat(_store.Archive)
                .Select(f =>
                {
                    var sim = qv is not null && _vec.TryGetValue(f.Id, out var v) ? Dot(qv, v) : 0f;
                    var words = WordRx().Matches(f.Text).Select(m => Stem(m.Value)).Count(stems.Contains);
                    return (f, score: sim + 0.05f * Math.Min(words, 4));
                })
                .Where(x => x.score > 0.45f)
                .OrderByDescending(x => x.score).Take(top)
                .OrderBy(x => x.f.Made)
                .Select(x => $"{(x.f.Source == "друзья" ? "о человеке" : Chronicle.Stamp(x.f.Made, DateTime.Now))} — {x.f.Text}{(x.f.Emotion.Length > 0 ? $" ({x.f.Emotion})" : "")}")
                .ToList();
        }
    }

    // ---------------- сон ----------------

    /// <summary>
    /// Сон: одинаковые по смыслу факты (сходство ≥ 0,94 и общая сущность) сливаются — остаётся свежий, вспоминания
    /// складываются; ни разу не всплывшие за 60 дней — в архив (холодный поиск их по-прежнему находит).
    /// </summary>
    public void Sleep()
    {
        int merged = 0, archived = 0;
        lock (_lock)
        {
            var facts = _store.Facts.OrderByDescending(f => f.Made).ToList();
            var gone = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < facts.Count; i++)
            {
                if (gone.Contains(facts[i].Id) || !_vec.TryGetValue(facts[i].Id, out var a)) continue;
                for (var j = i + 1; j < facts.Count; j++)
                {
                    var g = facts[j];
                    if (gone.Contains(g.Id) || !_vec.TryGetValue(g.Id, out var b)) continue;
                    if (!g.Entities.Intersect(facts[i].Entities).Any() || Dot(a, b) < 0.94f) continue;
                    facts[i].Uses += g.Uses;
                    gone.Add(g.Id);
                    merged++;
                }
            }
            _store.Facts.RemoveAll(f => gone.Contains(f.Id));
            var old = _store.Facts.Where(f => f.Uses == 0 && DateTime.Now - f.Made > TimeSpan.FromDays(60)).ToList();
            foreach (var f in old) { _store.Facts.Remove(f); _store.Archive.Add(f); }
            archived = old.Count;
            _store.LastSleep = DateTime.Now;
        }
        Save(force: true);
        Log.Write($"граф памяти: сон — слито {merged}, в архив {archived}");
    }

    /// <summary>Записать граф в фоне: обычная запись — не чаще раза в 3 с (частые «вспомнила» копятся), force — сразу.</summary>
    private void Save(bool force = false)
    {
        if (!force && Interlocked.Exchange(ref _saving, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            if (!force) await Task.Delay(3000).ConfigureAwait(false);
            if (!force) Interlocked.Exchange(ref _saving, 0);
            SaveNow();
        });
    }
}

internal sealed partial class MemoryGraph
{
    /// <summary>
    /// KlavaDesk.exe --graph-test ["реплика" [говорящий]]… — поднять граф (засев, векторы, сон) и показать, что всплывёт
    /// на реплики подряд (подграф живёт между ними), плюс холодный поиск по первой. Мозг не нужен.
    /// </summary>
    public static int Test(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\graph_test.log"));
        People.Load(cfg);
        using var brain = new Brain(cfg, saveHistory: false);
        var journal = new FlightJournal(cfg.Abs("data"));
        var friends = new FriendMemory(cfg.Abs(@"data\memory"), brain, journal);
        var bank = new MemoryBank(cfg.Abs(@"data\memory"), brain, journal, friends);
        var graph = new MemoryGraph(cfg);
        var sw = Stopwatch.StartNew();
        graph.StartAsync(bank, friends).GetAwaiter().GetResult();
        Console.WriteLine($"граф поднят за {sw.Elapsed.TotalSeconds:F1} с: {graph.Describe()}");
        var speaker = args.Length > 1 && !args[^1].Contains(' ') && args[^1].Contains('_') ? args[^1] : "Кибер_Кот";
        foreach (var line in args.Where(a => a != speaker))
        {
            sw.Restart();
            var ctx = Chronicle.Compose(graph.ContextForAsync(speaker, line).GetAwaiter().GetResult(), DateTime.Now);
            Console.WriteLine($"\n«{line}» ({sw.ElapsedMilliseconds} мс):\n{ctx ?? "(ничего не всплыло)"}");
        }
        if (args.Length > 0)
        {
            Console.WriteLine($"\nхолодный поиск «{args[0]}»:");
            foreach (var r in graph.RecallAsync(args[0]).GetAwaiter().GetResult()) Console.WriteLine("  " + r);
        }
        graph.Stop();
        return 0;
    }
}
