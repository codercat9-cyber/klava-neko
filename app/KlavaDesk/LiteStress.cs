using System.Text;
using System.Text.Json;

namespace KlavaDesk;

/// <summary>
/// Нативная расстановка ударений для легковесного голоса KlavaDesk (Sherpa-ONNX / Supertonic-3).
/// Работает без Python и без задержек:
/// 1. Приоритетный словарь custom_stress.json (сленг, мат, имена, кошачьи слова).
/// 2. Буква «ё» / «Ё» -> всегда ударная «е\u0301» / «Е\u0301» (в Supertonic токен ё равен -1).
/// 3. Большой бинарный словарь data\accents.bin (3.19 млн словоформ, бинарный поиск O(log N) за 0.05 мкс).
/// </summary>
internal static class LiteStress
{
    private const ulong FnvPrime = 1099511628211UL;
    private const ulong FnvOffset = 14695981039346656037UL;

    private static readonly Dictionary<string, string> CustomDict = new(StringComparer.OrdinalIgnoreCase);
    private static byte[]? _binData;
    private static int _entryCount;
    private static bool _loaded;
    private static readonly object _initLock = new();

    /// <summary>FNV-1a 64-bit хэш для UTF-8 строки в нижнем регистре.</summary>
    public static ulong Hash(string s)
    {
        ulong h = FnvOffset;
        Span<byte> buf = stackalloc byte[Encoding.UTF8.GetMaxByteCount(s.Length)];
        var written = Encoding.UTF8.GetBytes(s.ToLowerInvariant(), buf);
        for (int i = 0; i < written; i++)
        {
            h ^= buf[i];
            h *= FnvPrime;
        }
        return h;
    }

    /// <summary>Инициализация словарей (один раз при первом обращении).</summary>
    public static void Init(KlavaConfig cfg)
    {
        if (_loaded) return;
        lock (_initLock)
        {
            if (_loaded) return;

            // 1. Читаем custom_stress.json
            var customPath = cfg.Abs(@"tts\custom_stress.json");
            if (!File.Exists(customPath)) customPath = cfg.Abs(@"data\custom_stress.json");
            if (File.Exists(customPath))
            {
                try
                {
                    using var stream = File.OpenRead(customPath);
                    using var doc = JsonDocument.Parse(stream);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        if (prop.Name.StartsWith("_")) continue;
                        CustomDict[prop.Name] = prop.Value.GetString() ?? prop.Name;
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"[LiteStress] Ошибка чтения {customPath}: {ex.Message}");
                }
            }

            // 2. Читаем бинарный индекс data\accents.bin
            var binPath = cfg.Abs(@"data\accents.bin");
            if (File.Exists(binPath))
            {
                try
                {
                    var data = File.ReadAllBytes(binPath);
                    if (data.Length >= 8 && data[0] == (byte)'A' && data[1] == (byte)'C' && data[2] == (byte)'N' && data[3] == (byte)'T')
                    {
                        var count = BitConverter.ToInt32(data, 4);
                        if (data.Length >= 8 + count * 9)
                        {
                            _binData = data;
                            _entryCount = count;
                            Log.Write($"[LiteStress] accents.bin загружен: {count} словоформ ({(data.Length / 1024.0 / 1024.0):F2} МБ)");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write($"[LiteStress] Ошибка чтения {binPath}: {ex.Message}");
                }
            }

            _loaded = true;
        }
    }

    /// <summary>Бинарный поиск позиции ударения в accents.bin. Возвращает -1 если не найдено.</summary>
    private static int FindBinStressPos(ulong hash)
    {
        if (_binData == null || _entryCount == 0) return -1;
        int low = 0;
        int high = _entryCount - 1;
        var data = _binData.AsSpan(8); // пропускаем заголовок 8 байт

        while (low <= high)
        {
            int mid = (low + high) >>> 1;
            int offset = mid * 9;
            ulong midHash = BitConverter.ToUInt64(data.Slice(offset, 8));

            if (midHash < hash)
            {
                low = mid + 1;
            }
            else if (midHash > hash)
            {
                high = mid - 1;
            }
            else
            {
                return data[offset + 8]; // uint8 pos
            }
        }
        return -1;
    }

    /// <summary>
    /// Расставляет ударения в одиночном русском слове с сохранением исходного регистра.
    /// </summary>
    public static string StressWord(string word)
    {
        if (string.IsNullOrEmpty(word)) return word;

        // Если уже есть знак ударения или плюс — оставляем как есть
        if (word.Contains('+') || word.Contains('\u0301'))
        {
            return FixYo(word);
        }

        // Проверяем наличие гласных. Если одна гласная или нет — ударение однозначно или не нужно
        int vowelCount = 0;
        int singleVowelPos = -1;
        for (int i = 0; i < word.Length; i++)
        {
            char c = char.ToLowerInvariant(word[i]);
            if (IsRussianVowel(c))
            {
                vowelCount++;
                singleVowelPos = i;
            }
        }

        if (vowelCount <= 1)
        {
            return FixYo(word);
        }

        var lower = word.ToLowerInvariant();

        // 1. Проверяем custom_stress.json
        if (CustomDict.TryGetValue(lower, out var custom))
        {
            return ApplyPattern(word, custom);
        }

        // 2. Ищем в accents.bin
        var hash = Hash(lower);
        int pos = FindBinStressPos(hash);
        if (pos >= 0 && pos < word.Length)
        {
            // pos указывает на ударную гласную. Добавляем \u0301 сразу после неё
            var sb = new StringBuilder(word.Length + 2);
            for (int i = 0; i < word.Length; i++)
            {
                char ch = word[i];
                if (ch is 'ё' or 'Ё')
                {
                    sb.Append(ch == 'Ё' ? 'Е' : 'е');
                    sb.Append('\u0301');
                }
                else
                {
                    sb.Append(ch);
                    if (i == pos) sb.Append('\u0301');
                }
            }
            return sb.ToString();
        }

        // 3. Если в словаре нет, но есть буква «ё», превращаем её в «е\u0301»
        return FixYo(word);
    }

    /// <summary>Замена 'ё' -> 'е\u0301' и 'Ё' -> 'Е\u0301' для Supertonic-3 (в модели нет токена ё).</summary>
    public static string FixYo(string s)
    {
        if (!s.Contains('ё') && !s.Contains('Ё')) return s;
        var sb = new StringBuilder(s.Length + 4);
        foreach (var c in s)
        {
            if (c == 'ё')
            {
                sb.Append('е');
                sb.Append('\u0301');
            }
            else if (c == 'Ё')
            {
                sb.Append('Е');
                sb.Append('\u0301');
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>Переносит регистр исходного слова на паттерн с ударением.</summary>
    private static string ApplyPattern(string orig, string pattern)
    {
        // В custom_stress pattern вида "кл+ава"
        // Преобразуем '+' перед гласной в '\u0301' после гласной для Supertonic
        var sb = new StringBuilder(orig.Length + 4);
        int origIdx = 0;
        bool addAccentNext = false;

        for (int i = 0; i < pattern.Length; i++)
        {
            char pc = pattern[i];
            if (pc == '+')
            {
                addAccentNext = true;
                continue;
            }

            char ch = origIdx < orig.Length ? orig[origIdx++] : pc;
            if (ch is 'ё' or 'Ё')
            {
                sb.Append(ch == 'Ё' ? 'Е' : 'е');
                sb.Append('\u0301');
                addAccentNext = false;
            }
            else
            {
                sb.Append(ch);
                if (addAccentNext)
                {
                    sb.Append('\u0301');
                    addAccentNext = false;
                }
            }
        }
        return sb.ToString();
    }

    /// <summary>Словарь accents.bin загружен (иначе проверки «слово известно» не работают).</summary>
    public static bool HasDictionary => _binData is not null;

    /// <summary>
    /// Слово есть в словаре (любой регистр). Книги часто без «ё» («еще», «черный»): тогда пробуется каждая «е» как «ё».
    /// Для тренажёра чтения — отличить нормальный текст от OCR-мусора и устаревшей орфографии.
    /// </summary>
    public static bool Known(string word)
    {
        if (_binData is null || word.Length == 0) return false;
        var low = word.ToLowerInvariant();
        if (CustomDict.ContainsKey(low) || FindBinStressPos(Hash(low)) >= 0) return true;
        if (low.Contains('ё') && FindBinStressPos(Hash(low.Replace('ё', 'е'))) >= 0) return true;
        for (var i = low.IndexOf('е'); i >= 0; i = low.IndexOf('е', i + 1))
            if (FindBinStressPos(Hash(string.Concat(low.AsSpan(0, i), "ё", low.AsSpan(i + 1)))) >= 0) return true;
        return false;
    }

    /// <summary>Индекс ударной гласной в слове по словарю (свой словарь, потом accents.bin), −1 — не знаю.</summary>
    public static int StressIndex(string word)
    {
        var low = word.ToLowerInvariant();
        if (CustomDict.TryGetValue(low, out var custom))
        {
            var plus = custom.IndexOf('+');
            return plus >= 0 && plus < low.Length ? plus : -1;
        }
        if (_binData is null) return -1;
        var pos = FindBinStressPos(Hash(low));
        return pos >= 0 && pos < low.Length && IsRussianVowel(low[pos]) ? pos : -1;
    }

    private static bool IsRussianVowel(char c) =>
        c is 'а' or 'е' or 'ё' or 'и' or 'о' or 'у' or 'ы' or 'э' or 'ю' or 'я';

    /// <summary>
    /// Расстановка ударений во всем предложении.
    /// Обрабатывает слова, разделенные пробелами и пунктуацией.
    /// </summary>
    public static string StressSentence(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var sb = new StringBuilder(text.Length + 16);
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsLetter(c))
            {
                int start = i;
                while (i < text.Length && (char.IsLetter(text[i]) || text[i] == '-' || text[i] == '+' || text[i] == '\u0301'))
                {
                    i++;
                }
                var token = text[start..i];
                sb.Append(StressWord(token));
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        return sb.ToString();
    }
}
