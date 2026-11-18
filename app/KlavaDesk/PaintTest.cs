using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// KlavaDesk.exe --paint-test — проверка инструмента paint без мозга: своё новое окно Paint (рисунок друга не трогается),
/// точные фигуры, пипетка, лупа, затем сверка пикселей на экране в известных точках. Снимки — data\logs\paint_test\.
/// Окно теста закрывается без сохранения.
/// </summary>
internal static class PaintTest
{
    /// <param name="args">art [описание] [procedural] [hand src=образец.png] — вместо фигур написать картину paint_art (своё окно Paint);
    /// hand — карандашом рукой.</param>
    public static int Run(string[]? args = null)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var cfg = KlavaConfig.Load();
        Log.Open(cfg.Abs(@"data\logs\paint_test.log"));
        var outDir = cfg.Abs(@"data\logs\paint_test");
        Directory.CreateDirectory(outDir);
        var code = 1;
        var art = args is { Length: > 0 } && args[0] == "art";
        using var form = new Form
        {
            ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual,
            Location = new Point(-3000, -3000), Size = new Size(1, 1), Opacity = 0,
        };
        form.Shown += async (_, _) =>
        {
            try { code = art ? await RunArtAsync(form, outDir, cfg, args!.Skip(1).ToArray()) : await RunAsync(form, outDir); }
            catch (Exception ex) { Console.WriteLine($"ОШИБКА: {ex.GetType().Name}: {ex.Message}"); }
            finally { form.Close(); }
        };
        Application.Run(form);
        return code;
    }

    /// <summary>Картина paint_art в своём окне Paint: слоями легла на холст (не белый, есть мазки), без «НЕ СРАБОТАЛО».</summary>
    private static async Task<int> RunArtAsync(Form ui, string outDir, KlavaConfig cfg, string[] args)
    {
        var (win, proc) = await OpenOwnPaintAsync();
        if (win == IntPtr.Zero) { Console.WriteLine("не открылось своё окно Paint"); return 1; }
        PaintTool.TargetWindow = win;
        var hands = new Hands(ui);
        using var shell = new Shell();
        var box = new ToolBox { Hands = hands, Shell = shell, Autopilot = true, Capture = () => throw new InvalidOperationException("снимок экрана в тесте не нужен") };
        hands.Begin();
        try
        {
            // hand src=образец.png — карандашом рукой с образца (PencilPlan): штрихи мышью инструментом «Карандаш»
            var hand = args.Contains("hand");
            var src = args.FirstOrDefault(a => a.StartsWith("src=", StringComparison.Ordinal))?[4..];
            var prompt = args.FirstOrDefault(a => a is not ("procedural" or "hand") && !a.StartsWith("src=", StringComparison.Ordinal)) ?? (hand ? "академический рисунок яблока" : "закат над морем с маяком");
            var req = new JsonObject { ["prompt"] = prompt, ["style"] = hand ? "карандаш" : "масло", ["canvas"] = "current" };
            if (args.Contains("procedural")) req["source"] = "procedural";
            if (src is not null) req["reference_file"] = src;
            var sw = Stopwatch.StartNew();
            var r = await PaintTool.ArtAsync(req, box, cfg, CancellationToken.None);
            Console.WriteLine($"картина за {sw.Elapsed.TotalSeconds:F0} с:\n{Indent(r.Text)}");
            Save(outDir, "art.jpg", r.Image);
            var ok = (r.Text.Contains("Картина готова") || r.Text.Contains("Рисунок карандашом готов")) && !r.Text.Contains("НЕ СРАБОТАЛО");
            // на холсте должна быть живопись: пипеткой несколько точек — не все белые
            var picks = await PaintTool.RunAsync(new JsonObject { ["ops"] = JsonNode.Parse("""[{"op":"pick","at":[200,100]},{"op":"pick","at":[500,200]},{"op":"pick","at":[800,300]}]""")!.AsArray() }, box, CancellationToken.None);
            var got = System.Text.RegularExpressions.Regex.Matches(picks.Text, @": (#[0-9A-F]{6}) ").Select(m => m.Groups[1].Value).ToList();
            var painted = got.Count(c => c != "#FFFFFF");
            Console.WriteLine($"  пипетка: {string.Join(", ", got)} — закрашено {painted} из {got.Count}");
            ok &= painted >= 2;
            Console.WriteLine(ok ? "paint art: OK" : "paint art: ПЛОХО");
            return ok ? 0 : 1;
        }
        finally
        {
            hands.End();
            PaintTool.TargetWindow = IntPtr.Zero;
            if (proc is { HasExited: false }) { try { proc.Kill(); } catch (InvalidOperationException) { } }
        }
    }

    private static async Task<int> RunAsync(Form ui, string outDir)
    {
        var (win, proc) = await OpenOwnPaintAsync();
        if (win == IntPtr.Zero) { Console.WriteLine("не открылось своё окно Paint"); return 1; }
        Console.WriteLine($"своё окно Paint: PID {proc?.Id}, окно 0x{win:X}");
        PaintTool.TargetWindow = win;
        var hands = new Hands(ui);
        using var shell = new Shell();
        var box = new ToolBox
        {
            Hands = hands, Shell = shell, Autopilot = true,
            Capture = () => throw new InvalidOperationException("снимок экрана в тесте не нужен"),
        };
        var fails = 0;
        void Check(string what, bool ok) { Console.WriteLine($"  [{(ok ? "ок" : "ПЛОХО")}] {what}"); if (!ok) fails++; }
        hands.Begin();
        try
        {
            var r0 = await PaintTool.RunAsync(new JsonObject(), box, CancellationToken.None);
            Console.WriteLine(r0.Text.Split('\n')[0]);
            var H = int.Parse(System.Text.RegularExpressions.Regex.Match(r0.Text, @"y 0\.\.(\d+)").Groups[1].Value);
            Save(outDir, "0_empty.jpg", r0.Image);

            // домик точными фигурами: небо, трава, стена, крыша-треугольник, солнце, дверь и линия-антенна
            var ops = JsonNode.Parse($$"""
            [
              {"op":"rect","from":[0,0],"to":[1000,{{H * 7 / 10}}],"color":"#99D9EA"},
              {"op":"rect","from":[0,{{H * 7 / 10}}],"to":[1000,{{H}}],"color":"#22B14C"},
              {"op":"rect","from":[350,{{H * 42 / 100}}],"to":[650,{{H * 7 / 10}}],"color":"#EFE4B0","outline":"#7F7F7F","width":2},
              {"op":"polygon","points":[[320,{{H * 42 / 100}}],[500,{{H * 18 / 100}}],[680,{{H * 42 / 100}}]],"color":"#ED1C24"},
              {"op":"ellipse","from":[80,40],"to":[200,160],"color":"#FFC90E"},
              {"op":"rect","from":[470,{{H * 55 / 100}}],"to":[530,{{H * 7 / 10}}],"color":"#880015"},
              {"op":"line","points":[[600,{{H * 30 / 100}}],[600,{{H * 12 / 100}}]],"color":"#000000","width":4}
            ]
            """)!.AsArray();
            var sw = Stopwatch.StartNew();
            var r1 = await PaintTool.RunAsync(new JsonObject { ["ops"] = ops }, box, CancellationToken.None);
            Console.WriteLine($"точные фигуры за {sw.ElapsedMilliseconds} мс:\n{Indent(r1.Text)}");
            Save(outDir, "1_house.jpg", r1.Image);
            Check("без «НЕ СРАБОТАЛО»", !r1.Text.Contains("НЕ СРАБОТАЛО"));

            // пипеткой — цвета там, где они должны быть
            var picks = JsonNode.Parse($$"""
            [
              {"op":"pick","at":[100,{{H * 50 / 100}}]},
              {"op":"pick","at":[500,{{H * 85 / 100}}]},
              {"op":"pick","at":[400,{{H * 60 / 100}}]},
              {"op":"pick","at":[500,{{H * 32 / 100}}]},
              {"op":"pick","at":[140,100]},
              {"op":"pick","at":[500,{{H * 65 / 100}}]}
            ]
            """)!.AsArray();
            var r2 = await PaintTool.RunAsync(new JsonObject { ["ops"] = picks }, box, CancellationToken.None);
            Console.WriteLine(Indent(r2.Text));
            string[] want = ["#99D9EA", "#22B14C", "#EFE4B0", "#ED1C24", "#FFC90E", "#880015"];
            string[] what = ["небо", "трава", "стена", "крыша", "солнце", "дверь"];
            var got = System.Text.RegularExpressions.Regex.Matches(r2.Text, @": (#[0-9A-F]{6}) ").Select(m => m.Groups[1].Value).ToList();
            for (var i = 0; i < want.Length; i++)
                Check($"{what[i]}: ждала {want[i]}, на холсте {(i < got.Count ? got[i] : "—")}", i < got.Count && Close(got[i], want[i]));

            // починка: «испортить» стену кистью и закрыть заплаткой цветом, взятым с холста
            var spoil = JsonNode.Parse($$"""[{"op":"brush","color":"#00A2E8","size":14,"points":[[380,{{H * 50 / 100}}],[450,{{H * 62 / 100}}],[620,{{H * 48 / 100}}]]}]""")!.AsArray();
            var rs = await PaintTool.RunAsync(new JsonObject { ["ops"] = spoil, ["look"] = new JsonArray(330, H * 40 / 100, 340, H * 32 / 100) }, box, CancellationToken.None);
            Save(outDir, "1b_spoiled_zoom.jpg", rs.Image);
            var fix = JsonNode.Parse($$"""
            [
              {"op":"rect","from":[352,{{H * 43 / 100}}],"to":[648,{{H * 69 / 100}}],"color":[400,{{H * 66 / 100}}]},
              {"op":"rect","from":[470,{{H * 55 / 100}}],"to":[530,{{H * 7 / 10}}],"color":"#880015"}
            ]
            """)!.AsArray();
            var r3 = await PaintTool.RunAsync(new JsonObject { ["ops"] = fix, ["look"] = new JsonArray(330, H * 40 / 100, 340, H * 32 / 100) }, box, CancellationToken.None);
            Save(outDir, "2_fixed_zoom.jpg", r3.Image);
            var r4 = await PaintTool.RunAsync(new JsonObject { ["ops"] = JsonNode.Parse($$"""[{"op":"pick","at":[450,{{H * 62 / 100}}]},{"op":"pick","at":[380,{{H * 50 / 100}}]}]""")!.AsArray() }, box, CancellationToken.None);
            var got2 = System.Text.RegularExpressions.Regex.Matches(r4.Text, @": (#[0-9A-F]{6}) ").Select(m => m.Groups[1].Value).ToList();
            Check($"заплатка закрыла мазок: {string.Join(", ", got2)} (ждала #EFE4B0)", got2.Count == 2 && got2.All(g => Close(g, "#EFE4B0")));
            Save(outDir, "3_final.jpg", r4.Image);

            // слои: новый слой поверх домика, на нём квадрат точной фигурой — вокруг квадрата должен остаться домик
            // (раньше копия слоя шла плоской картинкой, и вставка заливала белым всё, что ниже)
            List<string> Picks(ToolResult r) => System.Text.RegularExpressions.Regex.Matches(r.Text, @": (#[0-9A-F]{6}) ").Select(m => m.Groups[1].Value).ToList();
            async Task<ToolResult> Run(string json) => await PaintTool.RunAsync(new JsonObject { ["ops"] = JsonNode.Parse(json)!.AsArray() }, box, CancellationToken.None);
            var l1 = await Run("""[{"op":"layer","action":"add"}]""");
            Console.WriteLine(Indent(l1.Text));
            var layerName = System.Text.RegularExpressions.Regex.Match(l1.Text, "новый слой «([^»]+)»").Groups[1].Value;
            Check($"новый слой создан ({layerName})", layerName.Length > 0 && l1.Text.Contains("◀ текущий"));
            var sq = await Run($$"""
                [{"op":"rect","from":[700,{{H * 10 / 100}}],"to":[900,{{H * 30 / 100}}],"color":"#FF00FF"},
                 {"op":"pick","at":[800,{{H * 20 / 100}}]},{"op":"pick","at":[100,{{H * 50 / 100}}]},{"op":"pick","at":[500,{{H * 85 / 100}}]}]
                """);
            Console.WriteLine(Indent(sq.Text));
            Save(outDir, "4_layer_square.jpg", sq.Image);
            var g3 = Picks(sq);
            Check($"квадрат на новом слое: {(g3.Count > 0 ? g3[0] : "—")} (ждала #FF00FF)", g3.Count > 0 && Close(g3[0], "#FF00FF"));
            Check($"небо и трава под слоем целы: {string.Join(", ", g3.Skip(1))} (ждала #99D9EA, #22B14C)",
                g3.Count == 3 && Close(g3[1], "#99D9EA") && Close(g3[2], "#22B14C"));

            var hide = await Run($$"""[{"op":"layer","action":"hide","layer":"{{layerName}}"},{"op":"pick","at":[800,{{H * 20 / 100}}]}]""");
            Console.WriteLine(Indent(hide.Text));
            var g4 = Picks(hide);
            Check($"слой скрыт — на месте квадрата небо: {(g4.Count > 0 ? g4[0] : "—")}", g4.Count > 0 && Close(g4[0], "#99D9EA") && hide.Text.Contains("(скрыт)"));
            var show = await Run($$"""[{"op":"layer","action":"show","layer":"{{layerName}}"},{"op":"pick","at":[800,{{H * 20 / 100}}]}]""");
            Console.WriteLine(Indent(show.Text));
            var g5 = Picks(show);
            Check($"слой снова виден: {(g5.Count > 0 ? g5[0] : "—")}", g5.Count > 0 && Close(g5[0], "#FF00FF"));
            var down = await Run($$"""[{"op":"layer","action":"down","layer":"{{layerName}}"},{"op":"pick","at":[800,{{H * 20 / 100}}]}]""");
            Console.WriteLine(Indent(down.Text));
            var g6 = Picks(down);
            Check($"слой опущен под домик — квадрат закрыт небом: {(g6.Count > 0 ? g6[0] : "—")}", g6.Count > 0 && Close(g6[0], "#99D9EA"));
            var up = await Run($$"""[{"op":"layer","action":"up","layer":"{{layerName}}"},{"op":"pick","at":[800,{{H * 20 / 100}}]}]""");
            var g7 = Picks(up);
            Check($"слой поднят обратно: {(g7.Count > 0 ? g7[0] : "—")}", g7.Count > 0 && Close(g7[0], "#FF00FF"));

            // живая кисть точным цветом (не из палитры) и выбор кисти по названию
            var br = await Run($$"""
                [{"op":"brush","kind":"кисть","color":"#3A7BD5","size":40,"opacity":100,"points":[[100,{{H * 85 / 100}}],[300,{{H * 85 / 100}}]]},
                 {"op":"pick","at":[200,{{H * 85 / 100}}]}]
                """);
            Console.WriteLine(Indent(br.Text));
            Save(outDir, "5_exact_brush.jpg", br.Image);
            var g8 = Picks(br);
            Check($"мазок точным цветом: {(g8.Count > 0 ? g8[0] : "—")} (ждала #3A7BD5)", g8.Count > 0 && Close(g8[0], "#3A7BD5"));
            var wc = await Run($$"""[{"op":"brush","kind":"акварель","color":"#FFFFFF","size":30,"points":[[400,{{H * 88 / 100}}],[600,{{H * 88 / 100}}]]}]""");
            Console.WriteLine(Indent(wc.Text));
            Save(outDir, "6_watercolor.jpg", wc.Image);
            Check("акварельная кисть выбрана", wc.Text.Contains("кисть для акварели") && !wc.Text.Contains("НЕ СРАБОТАЛО"));
        }
        finally
        {
            hands.End();
            PaintTool.TargetWindow = IntPtr.Zero;
            // своё окно — закрыть без сохранения; чужой Paint (рисунок друга) не трогаем
            if (proc is { HasExited: false }) { try { proc.Kill(); } catch (InvalidOperationException) { } }
        }
        Console.WriteLine(fails == 0 ? "paint: OK" : $"paint: провалов {fails}");
        return fails == 0 ? 0 : 1;
    }

    private static bool Close(string a, string b)
    {
        int C(string h, int i) => Convert.ToInt32(h.Substring(1 + i * 2, 2), 16);
        return Enumerable.Range(0, 3).All(i => Math.Abs(C(a, i) - C(b, i)) <= 12);
    }

    private static string Indent(string s) => "    " + s.Replace("\n", "\n    ");

    private static void Save(string dir, string name, byte[]? jpeg)
    {
        if (jpeg is not null) File.WriteAllBytes(Path.Combine(dir, name), jpeg);
    }

    /// <summary>Новое окно Paint в своём процессе (Paint из Store на каждый запуск — отдельный процесс).</summary>
    private static async Task<(IntPtr Win, Process? Proc)> OpenOwnPaintAsync()
    {
        var before = Process.GetProcessesByName("mspaint").Select(p => p.Id).ToHashSet();
        Process.Start(new ProcessStartInfo("mspaint.exe") { UseShellExecute = true });
        for (var i = 0; i < 80; i++)
        {
            await Task.Delay(250);
            foreach (var p in Process.GetProcessesByName("mspaint"))
            {
                if (before.Contains(p.Id)) { p.Dispose(); continue; }
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    await Task.Delay(1500);   // холсту нужно дорисоваться
                    return (p.MainWindowHandle, p);
                }
            }
        }
        return (IntPtr.Zero, null);
    }
}
