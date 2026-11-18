using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace KlavaDesk;

/// <summary>
/// Сеть Клавы: как выходить наружу. Облачные мозги (Gemini, OpenAI, Anthropic…) из России без прокси отвечают
/// «not available in your location», локальные (AG Tools, llama-server) через прокси ходить не должны.
///
/// Автонастройка ищет рабочий прокси сама, по порядку:
///  1. явный адрес в klava.json (netProxy: "http://127.0.0.1:3067" или "socks5://…"; "none" — без прокси);
///  2. переменные окружения HTTPS_PROXY / ALL_PROXY (процесс, пользователь, машина);
///  3. системный прокси Windows (Internet Settings → ProxyServer);
///  4. скан известных локальных портов (Karing, Clash, v2rayN, Nekoray, Hiddify, sing-box, Tor…).
/// Кандидат считается рабочим, только если реально пропускает CONNECT на google.com:443.
/// </summary>
internal static class NetProxy
{
    /// <summary>Порты популярных клиентов: Karing 3067, Clash 7890/7897, v2rayN 10808/10809, Nekoray 2080, Hiddify 12334, Tor 9050…</summary>
    private static readonly int[] KnownPorts = [3067, 7890, 7897, 10809, 10808, 2080, 2081, 1080, 8889, 20171, 20170, 12334, 7891, 9050, 9150, 8118, 1087, 6152, 6153];

    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();
    private static string? _detected;          // рабочий прокси, найденный автонастройкой ("" — не нашли)
    private static DateTime _detectedAt;
    private static readonly SemaphoreSlim DetectGate = new(1, 1);

    /// <summary>Прокси, найденный последней автонастройкой (null — ещё не искали, "" — нет рабочего).</summary>
    public static string? Detected => _detected;

    /// <summary>Адрес из настроек: пусто/auto — искать самим, none — никогда.</summary>
    public static string Configured { get; set; } = "auto";

    /// <summary>Адрес — это эта машина (тогда только напрямую).</summary>
    public static bool IsLoopback(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        return u.IsLoopback || u.Host is "localhost" or "127.0.0.1" or "[::1]" or "0.0.0.0";
    }

    /// <summary>
    /// Каким путём ходить на адрес профиля: "direct" | "proxy" | "auto". auto — loopback напрямую, остальное через
    /// найденный прокси, если он есть (иначе напрямую).
    /// </summary>
    public static string? ProxyFor(string url, string? mode)
    {
        if (IsLoopback(url)) return null;
        var m = (mode ?? "auto").Trim().ToLowerInvariant();
        if (m == "direct") return null;
        var p = Current();
        if (m == "proxy") return string.IsNullOrEmpty(p) ? null : p;
        return string.IsNullOrEmpty(p) ? null : p;
    }

    /// <summary>Клиент для адреса и режима. Один на комбинацию (прокси, таймаут) — сокеты переиспользуются.</summary>
    public static HttpClient ClientFor(string url, string? mode, TimeSpan? timeout = null)
    {
        var proxy = ProxyFor(url, mode);
        return Client(proxy, timeout ?? Timeout.InfiniteTimeSpan);
    }

    /// <summary>Клиент через конкретный прокси (null — строго напрямую, без системного прокси).</summary>
    public static HttpClient Client(string? proxy, TimeSpan timeout)
    {
        var key = $"{proxy ?? "direct"}|{timeout.Ticks}";
        return Clients.GetOrAdd(key, _ =>
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                ConnectTimeout = TimeSpan.FromSeconds(12),
                UseProxy = proxy is not null,
                Proxy = proxy is null ? null : new WebProxy(proxy) { BypassProxyOnLocal = true },
            };
            return new HttpClient(handler) { Timeout = timeout };
        });
    }

    /// <summary>Текущий рабочий прокси: из настроек или найденный автонастройкой (кэш 10 минут).</summary>
    public static string? Current()
    {
        var c = (Configured ?? "auto").Trim();
        if (c.Equals("none", StringComparison.OrdinalIgnoreCase) || c.Equals("direct", StringComparison.OrdinalIgnoreCase)) return null;
        if (c.Length > 0 && !c.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Normalize(c);
        if (_detected is not null && DateTime.UtcNow - _detectedAt < TimeSpan.FromMinutes(10)) return _detected.Length == 0 ? null : _detected;
        // не искали ещё — быстрый ответ по окружению/системе без проверки, полная проверка идёт в фоне
        var quick = FromEnvironment() ?? FromWinInet();
        _ = DetectAsync();
        return quick;
    }

    /// <summary>Найти рабочий прокси (с проверкой CONNECT). Возвращает адрес или null. Отчёт — в log.</summary>
    public static async Task<string?> DetectAsync(IProgress<string>? log = null, bool force = false)
    {
        if (!force && _detected is not null && DateTime.UtcNow - _detectedAt < TimeSpan.FromMinutes(10)) return _detected.Length == 0 ? null : _detected;
        await DetectGate.WaitAsync();
        try
        {
            if (!force && _detected is not null && DateTime.UtcNow - _detectedAt < TimeSpan.FromMinutes(10)) return _detected.Length == 0 ? null : _detected;
            var candidates = new List<(string Url, string Where)>();
            var c = (Configured ?? "auto").Trim();
            if (c.Length > 0 && !c.Equals("auto", StringComparison.OrdinalIgnoreCase) && !c.Equals("none", StringComparison.OrdinalIgnoreCase))
                candidates.Add((Normalize(c), "klava.json"));
            if (FromEnvironment() is { } env) candidates.Add((env, "переменная окружения HTTPS_PROXY/ALL_PROXY"));
            if (FromWinInet() is { } sys) candidates.Add((sys, "системный прокси Windows"));
            foreach (var port in KnownPorts)
            {
                var http = $"http://127.0.0.1:{port}";
                if (candidates.All(x => !x.Url.EndsWith($":{port}", StringComparison.Ordinal))) candidates.Add((http, $"локальный порт {port}"));
            }
            foreach (var (url, where) in candidates)
            {
                if (await WorksAsync(url))
                {
                    log?.Report($"прокси: {url} — работает ({where})");
                    Log.Write($"прокси: найден рабочий {url} ({where})");
                    _detected = url;
                    _detectedAt = DateTime.UtcNow;
                    return url;
                }
            }
            log?.Report("прокси: рабочего не нашла — хожу напрямую");
            Log.Write("прокси: рабочего не нашла — напрямую");
            _detected = "";
            _detectedAt = DateTime.UtcNow;
            return null;
        }
        finally { DetectGate.Release(); }
    }

    /// <summary>Прокси из переменных окружения (процесс, пользователь, машина).</summary>
    public static string? FromEnvironment()
    {
        foreach (var name in new[] { "HTTPS_PROXY", "https_proxy", "ALL_PROXY", "all_proxy", "HTTP_PROXY", "http_proxy" })
            foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            {
                var v = Environment.GetEnvironmentVariable(name, target);
                if (!string.IsNullOrWhiteSpace(v)) return Normalize(v);
            }
        return null;
    }

    /// <summary>Системный прокси Windows (Internet Settings), если включён.</summary>
    public static string? FromWinInet()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
            if (k?.GetValue("ProxyEnable") is not int en || en == 0) return null;
            var server = k.GetValue("ProxyServer") as string;
            if (string.IsNullOrWhiteSpace(server)) return null;
            // «http=host:port;https=host:port» или просто «host:port»
            var parts = server.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var https = parts.FirstOrDefault(p => p.StartsWith("https=", StringComparison.OrdinalIgnoreCase))
                        ?? parts.FirstOrDefault(p => p.StartsWith("http=", StringComparison.OrdinalIgnoreCase))
                        ?? parts.FirstOrDefault(p => !p.Contains('='));
            if (https is null) return null;
            var eq = https.IndexOf('=');
            return Normalize(eq >= 0 ? https[(eq + 1)..] : https);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException) { return null; }
    }

    private static string Normalize(string p)
    {
        p = p.Trim();
        if (!p.Contains("://", StringComparison.Ordinal)) p = "http://" + p;
        return p.TrimEnd('/');
    }

    /// <summary>
    /// Пропускает ли прокси наружу. HTTP-прокси — «CONNECT www.google.com:443» и ждём 200; SOCKS5 — рукопожатие
    /// и CONNECT по имени. Всё сырыми сокетами с таймаутом 2,5 с, чтобы проверка десятка портов шла быстро.
    /// </summary>
    public static async Task<bool> WorksAsync(string proxyUrl)
    {
        if (!Uri.TryCreate(proxyUrl, UriKind.Absolute, out var u)) return false;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(u.Host, u.Port, cts.Token);
            await using var s = tcp.GetStream();
            if (u.Scheme.StartsWith("socks", StringComparison.OrdinalIgnoreCase))
            {
                await s.WriteAsync(new byte[] { 5, 1, 0 }, cts.Token);
                var hello = new byte[2];
                if (await ReadExactAsync(s, hello, cts.Token) < 2 || hello[0] != 5 || hello[1] != 0) return false;
                var host = Encoding.ASCII.GetBytes("www.google.com");
                var req = new List<byte> { 5, 1, 0, 3, (byte)host.Length };
                req.AddRange(host);
                req.Add(443 >> 8); req.Add(443 & 0xFF);
                await s.WriteAsync(req.ToArray(), cts.Token);
                var rep = new byte[2];
                return await ReadExactAsync(s, rep, cts.Token) == 2 && rep[0] == 5 && rep[1] == 0;
            }
            var connect = Encoding.ASCII.GetBytes("CONNECT www.google.com:443 HTTP/1.1\r\nHost: www.google.com:443\r\n\r\n");
            await s.WriteAsync(connect, cts.Token);
            var buf = new byte[64];
            var n = await s.ReadAsync(buf, cts.Token);
            var head = Encoding.ASCII.GetString(buf, 0, n);
            return head.StartsWith("HTTP/1.", StringComparison.Ordinal) && head.Length > 12 && head[9] == '2';
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException) { return false; }
    }

    /// <summary>
    /// Открыт ли порт (быстро, без исключений в лог). Незавершённое подключение по таймауту гасим и его ошибку
    /// «наблюдаем» — иначе через минуту GC сыпал в лог «фоновая задача: SocketException».
    /// </summary>
    public static bool PortOpen(string host, int port, int timeoutMs = 400)
    {
        var c = new TcpClient();
        var t = c.ConnectAsync(host, port);
        try
        {
            return t.Wait(timeoutMs) && c.Connected;
        }
        catch (AggregateException) { return false; }
        finally
        {
            _ = t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);
            c.Dispose();
        }
    }

    private static async Task<int> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        var got = 0;
        while (got < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(got), ct);
            if (n <= 0) break;
            got += n;
        }
        return got;
    }
}
