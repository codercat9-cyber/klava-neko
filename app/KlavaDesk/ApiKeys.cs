using System.Text.Json;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Ключи облачных мозгов. Клава их нигде у себя не хранит — берёт там, где они у друга уже лежат:
/// переменные окружения, ~/.dsh/.env (DeepSeek Harness), настройки Antigravity Tools. Ключ уходит только
/// своему провайдеру (по адресу из профиля мозга).
/// </summary>
internal static class ApiKeys
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Ключ по описанию из профиля; null — не нашёлся (или ключ мозгу не нужен).</summary>
    public static string? Resolve(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        if (spec == "antigravity-tools") return AntigravityToolsKey();
        if (spec.StartsWith("env:", StringComparison.Ordinal))
        {
            var name = spec[4..];
            return NonEmpty(Environment.GetEnvironmentVariable(name))
                   // заведённая после запуска Клавы пользовательская переменная видна только через реестр
                   ?? NonEmpty(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User))
                   ?? DshEnv(name);
        }
        return spec;   // ключ записан прямо в klava.json
    }

    /// <summary>Куда положить ключ, если его нет, — для подсказки в пузыре.</summary>
    public static string Hint(string? spec) => spec switch
    {
        "antigravity-tools" => "ключ API-прокси в Antigravity Tools (~/.antigravity_tools/gui_config.json)",
        not null when spec.StartsWith("env:", StringComparison.Ordinal) => $"переменная окружения {spec[4..]} или строка {spec[4..]}=… в ~/.dsh/.env",
        _ => "ключ в klava.json",
    };

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? DshEnv(string name)
    {
        var file = Path.Combine(Home, ".dsh", ".env");
        if (!File.Exists(file)) return null;
        foreach (var raw in File.ReadLines(file))
        {
            var line = raw.Trim();
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0 || line.StartsWith('#') || line[..eq].Trim() != name) continue;
            return NonEmpty(line[(eq + 1)..].Trim().Trim('"', '\''));
        }
        return null;
    }

    private static string? AntigravityToolsKey()
    {
        var file = Path.Combine(Home, ".antigravity_tools", "gui_config.json");
        if (!File.Exists(file)) return null;
        try { return NonEmpty(JsonNode.Parse(File.ReadAllText(file))?["proxy"]?["api_key"]?.GetValue<string>()); }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException) { return null; }
    }
}
