using System.Drawing.Imaging;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Проверка автопилота Зумы без живой мыши: безголовый Edge с портом отладки (--remote-debugging-port), кадры —
/// Page.captureScreenshot, мышь — Input.dispatchMouseEvent. Живой экран и курсор друга не трогаются.
///   KlavaDesk.exe --zuma-cdp [порт=9223] [секунд=60] [fresh] [wait] [out=папка]
/// fresh — открыть Зуму заново и пройти карточку, рекламу, меню и обучалку (координаты — под вид 1280×800, только
/// первый запуск на чистом профиле: иначе меню другое, и клик может попасть в рекламу);
/// wait — ждать, пока уровень появится на экране (меню проходят снаружи, глядя на снимки).
/// Одно подключение на всю партию: вид 1280×800 ставится один раз — переподключение с новым размером посреди
/// игры замораживало её намертво.
/// </summary>
internal static class ZumaCdp
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 9223;
        var seconds = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 60;
        var fresh = args.Contains("fresh");
        var waitLevel = args.Contains("wait");
        var outDir = args.FirstOrDefault(a => a.StartsWith("out=", StringComparison.Ordinal))?[4..] ?? Path.Combine(Path.GetTempPath(), "zuma_cdp");
        Directory.CreateDirectory(outDir);
        try { return Task.Run(() => RunAsync(port, seconds, fresh, waitLevel, outDir)).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or InvalidOperationException or IOException)
        {
            Console.WriteLine($"проверка сломалась: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    private static async Task<int> RunAsync(int port, int seconds, bool fresh, bool waitLevel, string outDir)
    {
        using var io = await CdpIo.ConnectAsync(port);
        await io.Call("Emulation.setDeviceMetricsOverride", new JsonObject { ["width"] = 1280, ["height"] = 800, ["deviceScaleFactor"] = 1, ["mobile"] = false });
        if (fresh)
        {
            Console.WriteLine("открываю Зуму заново…");
            await io.Call("Page.navigate", new JsonObject { ["url"] = ZumaBot.DefaultUrl });
            await Task.Delay(15000);
            await io.ClickAsync(new PointF(482, 471), false);   // «Играть» на карточке Яндекс Игр
            await Task.Delay(25000);
            await io.ClickAsync(new PointF(1152, 100), false);  // крестик рекламы
            await Task.Delay(8000);
            await io.ClickAsync(new PointF(730, 225), false);   // «Играть» в меню игры
            await Task.Delay(6000);
            await io.ClickAsync(new PointF(480, 590), false);   // «нажмите любую кнопку» после обучалки
            await Task.Delay(3000);
        }
        if (waitLevel)
        {
            // меню проходит человек или другой клиент отладки (снимок → клик), а мы ждём поле с шарами: жёсткие
            // клики по меню однажды попали в рекламный баннер и увели вкладку со страницы игры
            Console.WriteLine("жду уровень на экране (до 3 мин)…");
            // живой уровень два раза подряд через секунду: между ними бывает реклама и затемнённое окно «Упс»
            var ready = false;
            var streak = 0;
            for (var i = 0; i < 180 && !ready; i++)
            {
                var f = await io.GrabAsync();
                var b = ZumaBot.FindBoard(f);
                var live = b.Width >= 300 && ZumaBot.StoneShare(f, b) >= 0.3 && ZumaBot.See(f, b, null, null, 0) is { RSure: true, Balls.Count: >= 5 };
                streak = live ? streak + 1 : 0;
                ready = streak >= 2;
                if (!ready) await Task.Delay(1000);
            }
            if (!ready) { Console.WriteLine("уровня так и не появилось"); return 1; }
        }
        var n = 0;
        var result = await ZumaBot.PlayCoreAsync(io, TimeSpan.FromSeconds(seconds), CancellationToken.None, (f, sc, mv, frog) =>
        {
            n++;
            Console.WriteLine($"#{n,3} шаров {sc.Balls.Count,2}, сейчас {sc.Cur} ({sc.CurVotes}), след. {sc.Next} → " +
                (mv is null ? "жду цвет" : mv.Swap ? $"смена: {mv.Why}" : $"({mv.Aim.X:F0},{mv.Aim.Y:F0}) {mv.Why}"));
            if ((n <= 3 || n % 5 == 0) && io.Last is { } bmp)
            {
                using var ann = ZumaBot.Annotate(bmp, sc, mv, frog);
                ann.Save(Path.Combine(outDir, $"z{n:D3}.png"), ImageFormat.Png);
            }
        });
        Console.WriteLine($"итог: {result}");
        Console.WriteLine($"кадры: {outDir}");
        return result.Contains("Выстрелов: 0") || result.StartsWith("не нашла", StringComparison.Ordinal) || result.StartsWith("поле вижу", StringComparison.Ordinal) ? 1 : 0;
    }

    /// <summary>Глаза и руки через протокол отладки браузера.</summary>
    private sealed class CdpIo : ZumaBot.IIo, IDisposable
    {
        private readonly ClientWebSocket _ws = new();
        private int _id;
        private PointF _pos;
        /// <summary>Последний кадр — для картинок отладки.</summary>
        public Bitmap? Last { get; private set; }

        public static async Task<CdpIo> ConnectAsync(int port)
        {
            using var http = new HttpClient();
            var list = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json"))!.AsArray();
            var pages = list.Where(t => t?["type"]?.GetValue<string>() == "page").ToList();
            var page = pages.FirstOrDefault(t => t!["url"]!.GetValue<string>() != "about:blank") ?? pages.FirstOrDefault()
                ?? throw new InvalidOperationException("в браузере нет вкладок");
            var io = new CdpIo();
            await io._ws.ConnectAsync(new Uri(page!["webSocketDebuggerUrl"]!.GetValue<string>()), CancellationToken.None);
            return io;
        }

        public async Task<JsonNode?> Call(string method, JsonObject? prms = null)
        {
            var id = ++_id;
            var msg = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = prms ?? new JsonObject() };
            await _ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
            var buf = new byte[1 << 20];
            while (true)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await _ws.ReceiveAsync(buf, CancellationToken.None);
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                var o = JsonNode.Parse(ms.ToArray());
                if (o?["id"]?.GetValue<int>() != id) continue;   // события и чужие ответы
                if (o["error"] is { } err) throw new InvalidOperationException($"{method}: {err["message"]}");
                return o["result"];
            }
        }

        public async Task<ZumaBot.Frame> GrabAsync()
        {
            var r = await Call("Page.captureScreenshot", new JsonObject { ["format"] = "png" });
            var bytes = Convert.FromBase64String(r!["data"]!.GetValue<string>());
            using var ms = new MemoryStream(bytes);
            using var img = new Bitmap(ms);
            Last?.Dispose();
            Last = new Bitmap(img);
            return new ZumaBot.Frame(Last);
        }

        public async Task AimAsync(PointF p)
        {
            _pos = p;
            await Call("Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseMoved", ["x"] = p.X, ["y"] = p.Y });
        }

        public async Task ClickAsync(PointF p, bool right)
        {
            await AimAsync(p);
            var button = right ? "right" : "left";
            await Call("Input.dispatchMouseEvent", new JsonObject { ["type"] = "mousePressed", ["x"] = p.X, ["y"] = p.Y, ["button"] = button, ["clickCount"] = 1 });
            await Task.Delay(40);
            await Call("Input.dispatchMouseEvent", new JsonObject { ["type"] = "mouseReleased", ["x"] = p.X, ["y"] = p.Y, ["button"] = button, ["clickCount"] = 1 });
        }

        public string? Interrupted() => null;
        public bool Avoid(PointF p) => false;

        public void Dispose()
        {
            Last?.Dispose();
            _ws.Dispose();
        }
    }
}
