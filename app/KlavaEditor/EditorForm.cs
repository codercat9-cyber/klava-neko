using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace KlavaEditor;

/// <summary>
/// Окно редактора. Страница шлёт {id, type, ...}, хост отвечает {id, ok, path | error}:
/// saveConfig — config.json Клавы (старый — в web\config.backups), saveTexture — web\textures\*.png,
/// saveFile — в «3D редактор аватара» на рабочем столе, openFolder, pickModel — взять VRM в web\assets,
/// autoDone — конец безлюдного экспорта.
/// </summary>
internal sealed class EditorForm : Form
{
    private readonly string _web;
    private readonly bool _auto;
    private readonly WebView2 _view = new() { Dock = DockStyle.Fill };

    public int ExitCode { get; private set; }

    public EditorForm(string web, bool auto)
    {
        _web = web;
        _auto = auto;
        Text = "Редактор Клавы";
        Width = 1500;
        Height = 920;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(18, 19, 26);
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch (ArgumentException) { }
        Controls.Add(_view);
        if (auto) { WindowState = FormWindowState.Minimized; ShowInTaskbar = false; }
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        try
        {
            // отдельная папка профиля: редактор и живая Клава могут работать одновременно
            var profile = Path.Combine(Path.GetDirectoryName(_web)!, "data", "webview-editor");
            var env = await CoreWebView2Environment.CreateAsync(null, profile);
            await _view.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or InvalidOperationException or COMException)
        {
            MessageBox.Show($"WebView2 не поднялся: {ex.Message}", "Редактор Клавы", MessageBoxButtons.OK, MessageBoxIcon.Error);
            ExitCode = 3;
            Close();
            return;
        }
        var core = _view.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.SetVirtualHostNameToFolderMapping("klava.local", _web, CoreWebView2HostResourceAccessKind.Allow);
        core.NavigationStarting += (_, a) => { if (!a.Uri.StartsWith("https://klava.local/", StringComparison.OrdinalIgnoreCase)) a.Cancel = true; };
        core.NewWindowRequested += (_, a) => a.Handled = true;
        core.WebMessageReceived += OnMessage;
        core.Navigate($"https://klava.local/editor.html{(_auto ? "?auto=export" : "")}");
        if (_auto)
        {
            // страховка: если страница зависла, не висим вечно
            var guard = new System.Windows.Forms.Timer { Interval = 180_000 };
            guard.Tick += (_, _) => { guard.Stop(); ExitCode = 4; Close(); };
            guard.Start();
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        JsonObject? m;
        try { m = JsonNode.Parse(e.WebMessageAsJson)?.AsObject(); }
        catch (JsonException) { return; }
        if (m is null) return;
        var id = m["id"]?.GetValue<int>() ?? 0;
        JsonObject reply;
        try { reply = Dispatch(m); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or JsonException or ArgumentException)
        {
            reply = new JsonObject { ["ok"] = false, ["error"] = ex.Message };
        }
        reply["id"] = id;
        _view.CoreWebView2.PostWebMessageAsJson(reply.ToJsonString());
    }

    private static JsonObject Ok(string path) => new() { ["ok"] = true, ["path"] = path };
    private static JsonObject Fail(string error) => new() { ["ok"] = false, ["error"] = error };
    private static string Str(JsonObject m, string k) => m[k]?.GetValue<string>() ?? "";

    private JsonObject Dispatch(JsonObject m)
    {
        switch (Str(m, "type"))
        {
            case "saveConfig":
            {
                var json = Str(m, "json");
                JsonNode.Parse(json);   // битый JSON в Клаву не пишем — она с ним не запустится
                var file = Path.Combine(_web, "config.json");
                if (File.Exists(file))
                {
                    var backups = Path.Combine(_web, "config.backups");
                    Directory.CreateDirectory(backups);
                    File.Copy(file, Path.Combine(backups, $"config_{DateTime.Now:yyyyMMdd_HHmmss}.json"), overwrite: true);
                }
                File.WriteAllText(file, json, new UTF8Encoding(false));
                // живая Клава опрашивает config.json и перерисовывается сама — страница скажет об этом по флагу live
                var reply = Ok(file);
                reply["live"] = Process.GetProcessesByName("KlavaDesk").Length > 0;
                return reply;
            }
            case "saveTexture":
            {
                var dir = Path.Combine(_web, "textures");
                var file = Paths.SafeJoin(dir, Str(m, "name"));
                if (file is null || !file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return Fail("плохое имя текстуры");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(file, Convert.FromBase64String(Str(m, "base64")));
                return Ok(file);
            }
            case "saveFile":
            {
                var dir = Paths.OutputFolder(Str(m, "folder"));
                if (dir is null) return Fail("неизвестная папка");
                var file = Paths.SafeJoin(dir, Str(m, "name"));
                if (file is null) return Fail("плохое имя файла");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                // .txt — с BOM, чтобы Блокнот не спутал кодировку; JSON и прочее — без (Python и парсеры на BOM спотыкаются)
                if (m["text"] is JsonValue t)
                    File.WriteAllText(file, t.GetValue<string>(), new UTF8Encoding(file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));
                else File.WriteAllBytes(file, Convert.FromBase64String(Str(m, "base64")));
                return Ok(file);
            }
            case "openFolder":
            {
                var dir = Paths.OutputFolder(Str(m, "folder")) ?? Paths.Output;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true })?.Dispose();
                return Ok(dir);
            }
            case "pickModel":
            {
                using var dlg = new OpenFileDialog
                {
                    Title = "Какую модель взять (VRM, например из VRoid Studio)", Filter = "VRM-модель (*.vrm)|*.vrm",
                    InitialDirectory = Directory.Exists(Paths.Output) ? Paths.Output : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                };
                if (dlg.ShowDialog(this) != DialogResult.OK) return Fail("");
                var assets = Path.Combine(_web, "assets");
                Directory.CreateDirectory(assets);
                var name = Path.GetFileName(dlg.FileName);
                var dst = Path.Combine(assets, name);
                if (!string.Equals(Path.GetFullPath(dlg.FileName), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                    File.Copy(dlg.FileName, dst, overwrite: true);
                return Ok($"./assets/{name}");
            }
            case "autoDone":
                ExitCode = m["ok"]?.GetValue<bool>() == true ? 0 : 1;
                BeginInvoke(Close);
                return Ok("");
            default:
                return Fail($"не знаю команду {Str(m, "type")}");
        }
    }
}
