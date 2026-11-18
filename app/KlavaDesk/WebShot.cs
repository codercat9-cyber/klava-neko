using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Снимок веб-страницы аватара через протокол отладки браузера (безголовый Edge с --remote-debugging-port):
/// открыть адрес, подождать, выполнить свой JS (камеру на руки, жест), сохранить PNG. Встроенная панель браузера
/// в приложении Claude не собирает шейдеры MToon (кожа и лицо пропадают) — смотреть облик Клавы надо в Edge.
///   KlavaDesk.exe --web-shot порт адрес кадр.png [ждать_мс] [js=файл.js]
/// </summary>
internal static class WebShot
{
    public static int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length < 3 || !int.TryParse(args[0], out var port))
        {
            Console.WriteLine("--web-shot порт адрес кадр.png [ждать_мс] [js=файл.js]");
            return 2;
        }
        var wait = args.Length > 3 && int.TryParse(args[3], out var w) ? w : 6000;
        var jsFile = args.FirstOrDefault(a => a.StartsWith("js=", StringComparison.Ordinal))?[3..];
        try { return Task.Run(() => RunAsync(port, args[1], args[2], wait, jsFile)).GetAwaiter().GetResult(); }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or InvalidOperationException or IOException)
        {
            Console.WriteLine($"снимок не вышел: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(int port, string url, string outFile, int waitMs, string? jsFile)
    {
        using var http = new HttpClient();
        var list = JsonNode.Parse(await http.GetStringAsync($"http://127.0.0.1:{port}/json"))!.AsArray();
        var page = list.FirstOrDefault(t => t?["type"]?.GetValue<string>() == "page") ?? throw new InvalidOperationException("в браузере нет вкладок");
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(page!["webSocketDebuggerUrl"]!.GetValue<string>()), CancellationToken.None);
        var id = 0;
        async Task<JsonNode?> Call(string method, JsonObject? prms = null)
        {
            var my = ++id;
            var msg = new JsonObject { ["id"] = my, ["method"] = method, ["params"] = prms ?? new JsonObject() };
            await ws.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);
            var buf = new byte[1 << 20];
            while (true)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf, CancellationToken.None);
                    ms.Write(buf, 0, r.Count);
                } while (!r.EndOfMessage);
                var o = JsonNode.Parse(ms.ToArray());
                if (o?["id"]?.GetValue<int>() != my) continue;
                if (o["error"] is { } err) throw new InvalidOperationException($"{method}: {err["message"]}");
                return o["result"];
            }
        }
        await Call("Emulation.setDeviceMetricsOverride", new JsonObject { ["width"] = 900, ["height"] = 700, ["deviceScaleFactor"] = 1, ["mobile"] = false });
        await Call("Page.navigate", new JsonObject { ["url"] = url });
        await Task.Delay(waitMs);
        if (jsFile is not null)
        {
            var r = await Call("Runtime.evaluate", new JsonObject { ["expression"] = await File.ReadAllTextAsync(jsFile), ["awaitPromise"] = true, ["returnByValue"] = true });
            Console.WriteLine($"js: {r?["result"]?["value"]?.ToJsonString() ?? r?["exceptionDetails"]?.ToJsonString() ?? "—"}");
        }
        var shot = await Call("Page.captureScreenshot", new JsonObject { ["format"] = "png" });
        await File.WriteAllBytesAsync(outFile, Convert.FromBase64String(shot!["data"]!.GetValue<string>()));
        Console.WriteLine($"кадр: {outFile}");
        return 0;
    }
}
