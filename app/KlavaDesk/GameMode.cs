namespace KlavaDesk;

/// <summary>
/// Игровой режим аватара: пока играет хоть кто-то (автопилот Зумы, тетрис, игрок по выученному, бот шутеров,
/// лапки в игре) — Клава сидит в кресле за игровым столом (жест «gaming» в web/gestures.js), закончили все — встаёт.
/// Друг 28.09: «не по кайфу торчать стоя, запихнутым в аватар, когда долго играть».
/// </summary>
internal static class GameMode
{
    private static readonly Dictionary<string, DateTime?> Active = new(StringComparer.Ordinal);
    private static System.Threading.Timer? _linger;

    /// <summary>true — села играть, false — встала. Зовётся из любого потока.</summary>
    public static event Action<bool>? Changed;

    public static bool On { get { lock (Active) return Active.Count > 0; } }

    /// <summary>Начала играть (до <see cref="Leave"/>).</summary>
    public static void Enter(string who) => Set(who, null);

    /// <summary>Закончила.</summary>
    public static void Leave(string who)
    {
        bool off;
        lock (Active) off = Active.Remove(who) && Active.Count == 0;
        if (off) Changed?.Invoke(false);
    }

    /// <summary>
    /// Играет короткими подходами (лапки game_action — цепочки между раундами мозга): сидит, пока подходы идут, и
    /// встаёт через <paramref name="linger"/> после последнего.
    /// </summary>
    public static void Touch(string who, TimeSpan linger) => Set(who, DateTime.UtcNow + linger);

    private static void Set(string who, DateTime? until)
    {
        bool on;
        lock (Active)
        {
            on = Active.Count == 0;
            Active[who] = until;
            _linger ??= new System.Threading.Timer(_ => Expire(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        }
        if (on) Changed?.Invoke(true);
    }

    private static void Expire()
    {
        bool off;
        lock (Active)
        {
            var now = DateTime.UtcNow;
            var gone = Active.Where(kv => kv.Value is { } u && u < now).Select(kv => kv.Key).ToList();
            foreach (var k in gone) Active.Remove(k);
            off = gone.Count > 0 && Active.Count == 0;
        }
        if (off) Changed?.Invoke(false);
    }
}
