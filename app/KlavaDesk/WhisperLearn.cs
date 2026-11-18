using System.Text.Json;

namespace KlavaDesk;

/// <summary>
/// Фоновое «доучивание» whisper без реального fine-tune (у нас нет ни GPU-ферм, ни данных):
///
/// <para>1. Adaptive prompt — держим ring-buffer из 60 последних распознанных фраз. При новом Transcribe
/// подсовываем последние 30 в поле <c>prompt</c> whisper.cpp — это его условие, он подстраивает
/// распознавание под контекст (то что уже слышал → лучше узнаёт снова).</para>
///
/// <para>2. Correction learning — фоновый Task каждые 30 сек сканирует recent-buffer, ищет пары
/// подряд идущих фраз с интервалом 1–6 сек и одинаковым числом слов, где 1–3 слова отличаются
/// на 1–3 буквы (типичная переформулировка «дели → дела»). Такие пословные различия сохраняются
/// в data/whisper_corrections.json и автоматически применяются к следующим распознанным фразам.
/// Так Клава постепенно чинит систематические ошибки whisper на конкретных словах.</para>
///
/// Всё асинхронно, ни одна фаза диалога не блокируется. Обучение идёт в фоновом Task с таймером.
/// </summary>
internal sealed class WhisperLearn : IDisposable
{
    private const int RecentCap = 60;
    // В подсказку whisper — только несколько последних фраз своих: длинный хвост чужой каши (телевизор)
    // whisper воспринимал как «так говорят» и начинал выдумывать похожее.
    private const int PromptTake = 4;
    private const int PromptMaxChars = 220;
    private const double MinPairGapSec = 1.0;
    private const double MaxPairGapSec = 6.0;
    private const int MinWordLen = 4;
    private const int MaxWordEditDistance = 3;

    private readonly string _corrPath;
    private readonly string _hallucinationsPath;
    private readonly Queue<(DateTime T, string Text)> _recent = new();
    private readonly Dictionary<string, string> _corrections = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _bannedHallucinations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public WhisperLearn(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _corrPath = Path.Combine(dataDir, "whisper_corrections.json");
        _hallucinationsPath = Path.Combine(dataDir, "whisper_hallucinations.json");
        Load();
    }

    /// <summary>Запустить фоновый learn-loop (30 сек тик).</summary>
    public void Start()
    {
        if (_worker is not null) return;
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => LearnLoop(_cts.Token));
    }

    /// <summary>Добавить новую распознанную фразу в буфер. Не блокирует — только запись.</summary>
    public void Feed(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 400) return;
        lock (_lock)
        {
            _recent.Enqueue((DateTime.UtcNow, text.Trim()));
            while (_recent.Count > RecentCap) _recent.Dequeue();
        }
    }

    /// <summary>Число недавних фраз в адаптивном буфере контекста.</summary>
    public int RecentCount { get { lock (_lock) return _recent.Count; } }

    /// <summary>Число выученных правил автозамены в словаре.</summary>
    public int CorrectionsCount { get { lock (_corrections) return _corrections.Count; } }

    /// <summary>Путь к файлу словаря автозамен.</summary>
    public string CorrectionsPath => _corrPath;

    /// <summary>Число забаненных фраз-галлюцинаций в чёрном списке.</summary>
    public int BannedCount { get { lock (_bannedHallucinations) return _bannedHallucinations.Count; } }

    /// <summary>Путь к файлу чёрного списка галлюцинаций.</summary>
    public string HallucinationsPath => _hallucinationsPath;

    /// <summary>Забанить фразу как ложно услышанную галлюцинацию (штраф whisper).</summary>
    public bool BanHallucination(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) return false;
        var clean = phrase.Trim().TrimEnd('.', '!', '?', ',').ToLowerInvariant();
        if (clean.Length == 0) return false;
        lock (_bannedHallucinations)
        {
            if (!_bannedHallucinations.Add(clean)) return true;
        }
        SaveHallucinations();
        Log.Write($"whisper-learn: забанена галлюцинация «{clean}» (всего {_bannedHallucinations.Count})");
        return true;
    }

    /// <summary>Проверить, является ли распознанная фраза известной галлюцинацией (на тишине/выдохе).</summary>
    public bool IsBannedHallucination(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = text.Trim().TrimEnd('.', '!', '?', ',').ToLowerInvariant();
        lock (_bannedHallucinations)
        {
            return _bannedHallucinations.Contains(clean);
        }
    }

    /// <summary>Применить накопленные коррекции к свежераспознанной фразе (regex по границам слов).</summary>
    public string Correct(string text)
    {
        if (_corrections.Count == 0 || string.IsNullOrEmpty(text)) return text;
        // Пословная подстановка: разбиваем на токены, каждый ищем в словаре (case-insensitive), сохраняем регистр первой буквы.
        return System.Text.RegularExpressions.Regex.Replace(text, @"[а-яёА-ЯЁa-zA-Z]{2,}", m =>
        {
            var w = m.Value;
            if (_corrections.TryGetValue(w.ToLowerInvariant(), out var fixd))
                return w.Length > 0 && char.IsUpper(w[0]) ? (fixd.Length > 0 ? char.ToUpper(fixd[0]) + fixd[1..] : fixd) : fixd;
            return w;
        });
    }

    /// <summary>Suffix для whisper.cpp prompt — последние 30 уникальных фраз через пробел, лимит 800 символов.</summary>
    public string PromptSuffix()
    {
        string[] recent;
        lock (_lock) recent = _recent.TakeLast(PromptTake).Select(x => x.Text).ToArray();
        var uniq = recent.Reverse().Distinct(StringComparer.OrdinalIgnoreCase).Reverse().ToArray();
        var s = string.Join(" ", uniq);
        return s.Length > PromptMaxChars ? s[^PromptMaxChars..] : s;
    }

    private async Task LearnLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(30_000, ct);
                var added = ScanPairs();
                if (added > 0)
                {
                    Save();
                    Log.Write($"whisper-learn: +{added} исправлений (всего {_corrections.Count})");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("whisper-learn", ex); }
    }

    /// <summary>Пройти по недавнему буферу, найти переформулировки, извлечь пословные исправления.</summary>
    private int ScanPairs()
    {
        (DateTime T, string Text)[] snap;
        lock (_lock) snap = _recent.ToArray();
        var added = 0;
        for (var i = 0; i + 1 < snap.Length; i++)
        {
            var (t1, s1) = snap[i];
            var (t2, s2) = snap[i + 1];
            var gap = (t2 - t1).TotalSeconds;
            if (gap < MinPairGapSec || gap > MaxPairGapSec) continue;
            added += DiffLearn(s1, s2);
        }
        return added;
    }

    private int DiffLearn(string bad, string good)
    {
        var wb = bad.Split(new[] { ' ', ',', '.', '!', '?', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var wg = good.Split(new[] { ' ', ',', '.', '!', '?', ';' }, StringSplitOptions.RemoveEmptyEntries);
        if (wb.Length != wg.Length || wb.Length < 2 || wb.Length > 15) return 0;

        // Считаем сколько слов отличаются — если больше половины, это не переформулировка, а разные фразы.
        var different = 0;
        var pairs = new List<(string bad, string good)>();
        for (var i = 0; i < wb.Length; i++)
        {
            if (string.Equals(wb[i], wg[i], StringComparison.OrdinalIgnoreCase)) continue;
            different++;
            if (wb[i].Length < MinWordLen || wg[i].Length < MinWordLen) continue;
            var d = Levenshtein(wb[i].ToLowerInvariant(), wg[i].ToLowerInvariant());
            if (d == 0 || d > MaxWordEditDistance) continue;
            pairs.Add((wb[i], wg[i]));
        }
        if (different == 0 || different > wb.Length / 2) return 0;

        var added = 0;
        lock (_corrections)
        {
            foreach (var (b, g) in pairs)
            {
                var key = b.ToLowerInvariant();
                if (_corrections.ContainsKey(key)) continue;
                // Не учим замену на само себя и на слишком короткое
                if (string.Equals(key, g, StringComparison.OrdinalIgnoreCase)) continue;
                _corrections[key] = g.ToLowerInvariant();
                added++;
            }
        }
        return added;
    }

    /// <summary>Levenshtein distance (для фонетической близости слов). Итеративная реализация O(n*m).</summary>
    private static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    private void Load()
    {
        if (File.Exists(_corrPath))
        {
            try
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_corrPath));
                if (dict is not null)
                {
                    foreach (var (k, v) in dict) _corrections[k] = v;
                    Log.Write($"whisper-learn: загружено {_corrections.Count} исправлений из {Path.GetFileName(_corrPath)}");
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException) { Log.Error("whisper-learn load", ex); }
        }

        if (File.Exists(_hallucinationsPath))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_hallucinationsPath));
                if (list is not null)
                {
                    foreach (var item in list)
                    {
                        var clean = item.Trim().TrimEnd('.', '!', '?', ',').ToLowerInvariant();
                        if (clean.Length > 0) _bannedHallucinations.Add(clean);
                    }
                    Log.Write($"whisper-learn: загружено {_bannedHallucinations.Count} забаненных галлюцинаций из {Path.GetFileName(_hallucinationsPath)}");
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException) { Log.Error("whisper hallucinations load", ex); }
        }
        else
        {
            // Базовый набор классических галлюцинаций Whisper на русском при тишине/выдохе
            string[] baseH = ["спасибо", "спасибо большое", "пожалуйста", "благодарю", "субтитры", "продолжение следует", "не за что", "до свидания", "всем пока"];
            foreach (var h in baseH) _bannedHallucinations.Add(h);
            SaveHallucinations();
        }
    }

    private void Save()
    {
        try
        {
            Dictionary<string, string> snap;
            lock (_corrections) snap = new Dictionary<string, string>(_corrections);
            File.WriteAllText(_corrPath, JsonSerializer.Serialize(snap, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch (IOException ex) { Log.Error("whisper-learn save", ex); }
    }

    private void SaveHallucinations()
    {
        try
        {
            List<string> snap;
            lock (_bannedHallucinations) snap = new List<string>(_bannedHallucinations);
            File.WriteAllText(_hallucinationsPath, JsonSerializer.Serialize(snap, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch (IOException ex) { Log.Error("whisper hallucinations save", ex); }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try { _worker?.Wait(1000); } catch (AggregateException) { }
        _cts?.Dispose();
    }
}
