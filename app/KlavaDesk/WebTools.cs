using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace KlavaDesk;

/// <summary>
/// Переводчик и погода «как человек в браузере»: другу открывается вкладка (Google Переводчик, Яндекс.Погода) — видно,
/// что Клава туда сходила, — а точный ответ она берёт из тех же сервисов напрямую (перевод — из того же движка
/// Google, погода — Open-Meteo, без ключей): со страницы, которую дорисовывают скрипты, текст ненадёжен.
/// </summary>
internal static partial class Tools
{
    private static readonly Dictionary<string, string> LangCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ru"] = "ru", ["русский"] = "ru", ["рус"] = "ru", ["en"] = "en", ["английский"] = "en", ["англ"] = "en", ["english"] = "en",
        ["de"] = "de", ["немецкий"] = "de", ["fr"] = "fr", ["французский"] = "fr", ["es"] = "es", ["испанский"] = "es",
        ["it"] = "it", ["итальянский"] = "it", ["ja"] = "ja", ["японский"] = "ja", ["zh"] = "zh-CN", ["китайский"] = "zh-CN",
        ["ko"] = "ko", ["корейский"] = "ko", ["uk"] = "uk", ["украинский"] = "uk", ["pl"] = "pl", ["польский"] = "pl",
        ["tr"] = "tr", ["турецкий"] = "tr", ["pt"] = "pt", ["португальский"] = "pt", ["ar"] = "ar", ["арабский"] = "ar",
        ["kk"] = "kk", ["казахский"] = "kk", ["be"] = "be", ["белорусский"] = "be", ["auto"] = "auto",
    };

    private static async Task<ToolResult> TranslateAsync(string text, string to, string from, bool show, Func<ToolRequest, Task<bool>> approve,
        ToolBox? box, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return new ToolResult("Нечего переводить.");
        var src = LangCodes.TryGetValue(from.Trim(), out var f) ? f : "auto";
        // куда: сказано — туда; не сказано — русское на английский, остальное на русский
        var dst = LangCodes.TryGetValue(to.Trim(), out var t) && t != "auto" ? t : Regex.IsMatch(text, "[А-Яа-яЁё]") ? "en" : "ru";
        var page = $"https://translate.google.com/?sl={src}&tl={dst}&text={Uri.EscapeDataString(text)}&op=translate";
        if (!await ApproveOrAutoAsync(new ToolRequest("Перевести в Google Переводчике", text.Length > 200 ? text[..200] + "…" : text, false, null, "web"), approve, box))
            return new ToolResult(Denied);
        if (show) Process.Start(new ProcessStartInfo(page) { UseShellExecute = true });
        var api = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={src}&tl={dst}&dt=t&q={Uri.EscapeDataString(text)}";
        try
        {
            var json = await GetPageAsync(api, ct);
            var root = JsonNode.Parse(json) as JsonArray;
            var sb = new StringBuilder();
            if (root?[0] is JsonArray parts)
                foreach (var p in parts)
                    if (p is JsonArray seg && seg.Count > 0 && seg[0] is JsonValue v && v.TryGetValue<string>(out var s)) sb.Append(s);
            var detected = root is { Count: > 2 } && root[2] is JsonValue dv && dv.TryGetValue<string>(out var ds) ? ds : src;
            if (sb.Length == 0) return new ToolResult($"[НЕ СРАБОТАЛО] Google не вернул перевод.{(show ? " Вкладка открыта — глянь browser_read." : "")}");
            return new ToolResult($"Перевод ({detected} → {dst}): {sb}{(show ? "\n(Google Переводчик открыт у друга во вкладке.)" : "")}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return new ToolResult($"[НЕ СРАБОТАЛО] переводчик не ответил ({ex.Message}).{(show ? " Вкладка открыта — посмотри перевод через browser_read." : "")}");
        }
    }

    /// <summary>Коды погоды WMO → по-русски.</summary>
    private static string WeatherText(int code) => code switch
    {
        0 => "ясно", 1 => "почти ясно", 2 => "переменная облачность", 3 => "пасмурно",
        45 or 48 => "туман", 51 or 53 or 55 => "морось", 56 or 57 => "ледяная морось",
        61 => "небольшой дождь", 63 => "дождь", 65 => "сильный дождь", 66 or 67 => "ледяной дождь",
        71 => "небольшой снег", 73 => "снег", 75 => "сильный снег", 77 => "снежная крупа",
        80 => "ливни", 81 => "сильные ливни", 82 => "очень сильные ливни", 85 or 86 => "снегопад",
        95 => "гроза", 96 or 99 => "гроза с градом", _ => $"погода с кодом {code}",
    };

    private static async Task<ToolResult> WeatherAsync(string place, int days, bool show, bool remember, KlavaConfig cfg,
        Func<ToolRequest, Task<bool>> approve, ToolBox? box, CancellationToken ct)
    {
        var name = string.IsNullOrWhiteSpace(place) ? cfg.HomeCity : place.Trim();
        string note = "";
        double lat, lon;
        string label;
        if (string.IsNullOrWhiteSpace(name))
        {
            // город не сказан и не запомнен — по адресу в сети (без прокси: через прокси это был бы город прокси)
            try
            {
                var geo = JsonNode.Parse(await NetProxy.Client(null, TimeSpan.FromSeconds(10)).GetStringAsync("http://ip-api.com/json/?lang=ru&fields=status,city,lat,lon", ct));
                if (geo?["status"]?.GetValue<string>() != "success") return new ToolResult("Не знаю, где ты: скажи город (и я его запомню).");
                lat = geo["lat"]!.GetValue<double>();
                lon = geo["lon"]!.GetValue<double>();
                label = geo["city"]?.GetValue<string>() ?? "здесь";
                note = $"\n(Город определила по сети: {label}. Если не тот — скажи свой, запомню.)";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NullReferenceException)
            {
                return new ToolResult($"Не знаю, где ты, и по сети не определилось ({ex.Message}): скажи город.");
            }
        }
        else
        {
            try
            {
                var g = JsonNode.Parse(await GetPageAsync($"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name)}&count=1&language=ru&format=json", ct));
                if (g?["results"] is not JsonArray { Count: > 0 } res) return new ToolResult($"Не нашла город «{name}». Скажи иначе (например, «Казань» или «Kazan»).");
                lat = res[0]!["latitude"]!.GetValue<double>();
                lon = res[0]!["longitude"]!.GetValue<double>();
                label = res[0]!["name"]?.GetValue<string>() ?? name;
                if (res[0]!["admin1"]?.GetValue<string>() is { } region && region != label) label += $", {region}";
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NullReferenceException)
            {
                return new ToolResult($"[НЕ СРАБОТАЛО] поиск города не ответил ({ex.Message}).");
            }
            if (remember && !string.IsNullOrWhiteSpace(place))
            {
                cfg.HomeCity = place.Trim();
                cfg.Save();
                note = $"\n(Запомнила: ты в городе {cfg.HomeCity}.)";
            }
        }
        if (!await ApproveOrAutoAsync(new ToolRequest("Посмотреть погоду", label, false, null, "web"), approve, box)) return new ToolResult(Denied);
        var inv = CultureInfo.InvariantCulture;
        if (show) Process.Start(new ProcessStartInfo($"https://yandex.ru/pogoda/?lat={lat.ToString("F4", inv)}&lon={lon.ToString("F4", inv)}") { UseShellExecute = true });
        days = Math.Clamp(days <= 0 ? 3 : days, 1, 7);
        try
        {
            var w = JsonNode.Parse(await GetPageAsync(
                $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(inv)}&longitude={lon.ToString(inv)}" +
                "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,precipitation" +
                "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max,wind_speed_10m_max" +
                $"&wind_speed_unit=ms&timezone=auto&forecast_days={days}", ct));
            var cur = w?["current"];
            var sb = new StringBuilder($"Погода — {label}:\n");
            if (cur is not null)
                sb.AppendLine($"Сейчас {cur["temperature_2m"]!.GetValue<double>():+0;-0;0}°, ощущается как {cur["apparent_temperature"]!.GetValue<double>():+0;-0;0}°, " +
                              $"{WeatherText(cur["weather_code"]!.GetValue<int>())}, ветер {cur["wind_speed_10m"]!.GetValue<double>():0} м/с, " +
                              $"влажность {cur["relative_humidity_2m"]!.GetValue<double>():0}%.");
            if (w?["daily"] is { } d && d["time"] is JsonArray time)
                for (var i = 0; i < time.Count; i++)
                {
                    var date = DateTime.Parse(time[i]!.GetValue<string>(), inv);
                    var dayName = i == 0 ? "сегодня" : i == 1 ? "завтра" : date.ToString("dddd d MMMM", new CultureInfo("ru-RU"));
                    sb.AppendLine($"{dayName}: {d["temperature_2m_min"]![i]!.GetValue<double>():+0;-0;0}…{d["temperature_2m_max"]![i]!.GetValue<double>():+0;-0;0}°, " +
                                  $"{WeatherText(d["weather_code"]![i]!.GetValue<int>())}, осадки {d["precipitation_probability_max"]![i]?.GetValue<double>() ?? 0:0}%, " +
                                  $"ветер до {d["wind_speed_10m_max"]![i]!.GetValue<double>():0} м/с");
                }
            if (show) sb.AppendLine("(Яндекс.Погода открыта у друга во вкладке.)");
            return new ToolResult(sb.ToString().TrimEnd() + note);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or NullReferenceException or FormatException)
        {
            return new ToolResult($"[НЕ СРАБОТАЛО] прогноз не пришёл ({ex.Message}).{(show ? " Вкладка открыта — посмотри через browser_read." : "")}");
        }
    }
}
