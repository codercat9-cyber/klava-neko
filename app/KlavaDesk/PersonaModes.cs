using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Режимы общения Клавы: обычный (как в характере), с матом, без мата, умеренный, рассудительный.
/// Раньше режим был только накладкой в конце длинного характера — а вся история разговора с матом, и модель
/// продолжала свой же прошлый стиль; о смене режима ей никто не говорил, и она переспрашивала «что за режим».
/// Теперь режим держится тремя способами: накладка в характере (persona_*.txt), короткое напоминание в каждой
/// реплике (в историю не пишется) и событие при смене — она сама подтверждает уже в новой манере. В чистых
/// режимах её речь ещё проходит фильтр мата — страховка, если модель всё-таки сорвётся.
/// </summary>
internal static partial class PersonaModes
{
    /// <param name="Id">значение PersonaMode в klava.json</param>
    /// <param name="Title">пункт меню</param>
    /// <param name="File">накладка на persona.txt (пусто — без накладки)</param>
    /// <param name="Note">напоминание в каждой реплике</param>
    /// <param name="Meaning">что это значит — для события при смене режима</param>
    /// <param name="Clean">без мата: речь проходит фильтр</param>
    public sealed record Mode(string Id, string Title, string File, string Note, string Meaning, bool Clean);

    public static readonly Mode[] All =
    [
        new("default", "Обычная (как в характере)", "", "",
            "говоришь как всегда: живо, с юмором, мат к месту, как в твоём характере", false),
        new("mat", "С матом (по-пацански)", "persona_rage.txt",
            "Режим «с матом»: мат — только манера речи, смачно и по-пацански, но не в каждой фразе и не одними и теми же словами. " +
            "Ты та же добрая, честная, смешливая Клава: не вредничаешь, не врёшь, что сделала то, чего не делала, просьбы выполняешь.",
            "материшься смачно и по-пацански — это только манера речи: остаёшься доброй и честной, не вредничаешь и не врёшь", false),
        // друг 29.09: «в других режимах она как официант-дворецкий, нудит как робот; без мата — ок, но училку не делай»
        new("clean", "Без мата", "persona_polite.txt",
            "Режим «без мата»: ни одного матерного слова (эмоции — «блин», «жесть», «офигеть»). Остальное не меняется: ты та же дерзкая, смешливая, озорная Клава — не официантка, не училка, не робот; коротко и по-свойски.",
            "только без мата — эмоции «блин», «жесть», «офигеть»; всё остальное как было: дерзкая, смешливая, с подколами и «мур», не вежливая официантка и не училка", true),
        new("moderate", "Умеренная", "persona_moderate.txt",
            "Режим «умеренный»: крепкое слово — редко, не больше одного за ответ; в остальном ты та же живая, дерзкая, смешливая Клава — не официантка и не училка, коротко и по-свойски.",
            "крепкое слово редко — не больше одного за ответ, без обзывательств; в остальном та же живая, дерзкая и смешливая", false),
        new("thoughtful", "Рассудительная", "persona_thoughtful.txt",
            "Режим «рассудительный»: без мата; рассуждаешь живо, от себя, как умная подруга вслух — со своим мнением, эмоцией и юмором; не лекция, не училка, без «во-первых» и «важно отметить».",
            "без мата, рассуждаешь вслух как умная подруга — от себя, со своим мнением, эмоцией и юмором, не лекцией; ответ может быть на пару фраз длиннее", true),
    ];

    /// <summary>Режим по значению из настроек; старые polite/rage — это «без мата» и «с матом».</summary>
    public static Mode Get(string? id)
    {
        var key = (id ?? "").Trim().ToLowerInvariant() switch
        {
            "polite" => "clean",
            "rage" => "mat",
            var k => k,
        };
        return All.FirstOrDefault(m => m.Id == key) ?? All[0];
    }

    /// <summary>Режим по словам друга: «режим без мата», «переключись в рассудительный режим»… null — это не про режим.</summary>
    public static Mode? FromSpeech(string text)
    {
        var m = SwitchRx().Match(text);
        if (!m.Success) return null;
        var w = m.Groups["w"].Value.ToLowerInvariant();
        return w.StartsWith("без") ? Get("clean")
            : w.StartsWith("с мат") || w.StartsWith("мат") || w.StartsWith("пацан") || w.StartsWith("рейдж") ? Get("mat")
            : w.StartsWith("умерен") ? Get("moderate")
            : w.StartsWith("рассуд") || w.StartsWith("серьёз") || w.StartsWith("серьез") ? Get("thoughtful")
            : w.StartsWith("обычн") || w.StartsWith("нормальн") ? Get("default")
            : null;
    }

    // «режим без мата», «включи режим с матом», «переключись на рассудительный режим», «давай умеренный режим»
    [GeneratedRegex(@"(?:^|\b)(?:(?:включи|переключи(?:сь)?|давай|сделай|поставь|перейди)\s+(?:на\s+|в\s+)?)?(?:режим\s+(?<w>без\s+мата|с\s+матом|матерный|пацанский|рейдж|умеренный|рассудительный|серь[её]зный|обычный|нормальный)|(?<w>без\s+мата|с\s+матом|умеренный|рассудительный|серь[её]зный|обычный|нормальный)\s+режим)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SwitchRx();

    /// <summary>
    /// Фильтр мата для чистых режимов: корни мата меняются на безобидные слова («нахуй» → «нафиг», «охуеть» →
    /// «офигеть», «пиздец» → «жесть», «блять» → «блин»). Страховка: основное держит напоминание в реплике.
    /// </summary>
    public static string Censor(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return MatRx().Replace(text, m =>
        {
            var w = m.Value;
            var low = w.ToLowerInvariant();
            string rep;
            if (low.StartsWith("бля")) rep = "блин";
            else if (low.StartsWith("пиздец")) rep = "жесть";
            else if (low.Contains("пиздат")) rep = "круто";
            else if (low.Contains("пизд")) rep = "фигня";
            else if (low.StartsWith("долбо")) rep = "балбес";
            else if (low.StartsWith("мудак") || low.StartsWith("мудил") || low.StartsWith("пидор") || low.StartsWith("пидр")) rep = "дурак";
            else if (low.StartsWith("сук")) rep = "зараза";
            else if (low.StartsWith("шлюх")) rep = "зараза";
            else if (low.StartsWith("гандон")) rep = "гад";
            else if (low.Contains("залуп")) rep = "фигня";
            else if (low.StartsWith("ёпт") || low.StartsWith("епт")) rep = "ёлки";
            else if (HuiRx().IsMatch(low)) rep = HuiRx().Replace(low, h => h.Value[2] switch { 'й' => "фиг", 'я' => "фига", 'ю' => "фигу", 'и' => "фиги", _ => "фиге" });
            else if (low.StartsWith("заеб") || low.StartsWith("заёб")) rep = "достал";
            else if (low.Contains("наеб") || low.Contains("наёб")) rep = "навернулся";
            else rep = "блин";
            // первая буква — как была (в начале фразы — заглавная)
            return char.IsUpper(w[0]) ? char.ToUpper(rep[0]) + rep[1..] : rep;
        });
    }

    // корни: хуй/хуе/хуё/хуя/хую/хуи (только с начала слова или после мат-приставки: «плохую», «тихую» — не мат),
    // пизд, бля, ёб/еб с приставками, ёпт, сука, мудак, пидор, гандон, шлюха, залупа, долбоёб
    [GeneratedRegex(@"\b(?:(?:на|по|о|за|до|от|вы|ни|раз|рас|с|об|при|под)?ху[йяеёию]\w*|\w*пизд\w*|бля\w*|(?:за|по|на|вы|у|от|об|про|до|съ|разъ|под|пере|вз|из|при|о)?[её]б(?:а|ан|ал|ат|ну|у|ущ|уч|ись|ёт|ет|ли|ля|лан|ло|ош|ок)\w*|[её]пт\w*|сук(?:а|и|у|ой|ам)\b|муда(?:к|ч)\w*|мудил\w*|пид(?:о)?р\w*|гандон\w*|шлюх\w*|\w*залуп\w*|долбо[её]б\w*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MatRx();

    [GeneratedRegex(@"ху[йяеёию]", RegexOptions.CultureInvariant)]
    private static partial Regex HuiRx();
}
