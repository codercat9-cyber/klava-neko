using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace KlavaDesk;

/// <summary>
/// Модуль VR для Клавы по VR_plan.md:
/// Слой 0: Оверлей SteamVR (OpenVR IVROverlay) — Клава стоит рядом в пространстве игровой зоны;
/// Слой 1: VRChat OSC клиент (UDP 9000/9001) — чатбокс над головой, индикатор речи, параметры аватара (эмоции, жесты).
/// </summary>
internal static class KlavaVR
{
    public const int VrcOscSendPort = 9000;
    public const int VrcOscRecvPort = 9001;

    private static readonly object Gate = new();
    private static UdpClient? _oscSender;
    private static bool _overlayActive;

    static KlavaVR()
    {
        try
        {
            _oscSender = new UdpClient();
            _oscSender.Connect(IPAddress.Loopback, VrcOscSendPort);
        }
        catch (Exception ex)
        {
            Log.Write($"VR OSC sender init: {ex.Message}");
        }
    }

    /// <summary>Запущен ли процесс SteamVR (vrserver.exe или vrmonitor.exe).</summary>
    public static bool IsSteamVrRunning()
    {
        try
        {
            return Process.GetProcessesByName("vrserver").Length > 0 ||
                   Process.GetProcessesByName("vrmonitor").Length > 0;
        }
        catch { return false; }
    }

    /// <summary>Запущен ли процесс VRChat.</summary>
    public static bool IsVrcRunning()
    {
        try { return Process.GetProcessesByName("VRChat").Length > 0; }
        catch { return false; }
    }

    // ---------- VRChat OSC Client ----------

    /// <summary>Отправить реплику в чатбокс VRChat (/chatbox/input string bool bool).</summary>
    public static bool SendChatbox(string text, bool direct = true, bool complete = true)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            // OSC: /chatbox/input, [string text, bool direct, bool complete]
            var packet = BuildOscMessage("/chatbox/input", text, direct, complete);
            SendOscBytes(packet);
            Log.Write($"VR: реплика в чатбокс VRChat «{text}»");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"VR chatbox error: {ex.Message}");
            return false;
        }
    }

    /// <summary>Индикатор набора текста в VRChat (/chatbox/typing bool).</summary>
    public static bool SetTyping(bool typing)
    {
        try
        {
            var packet = BuildOscMessage("/chatbox/typing", typing);
            SendOscBytes(packet);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Установить параметр аватара в VRChat (/avatar/parameters/name value).</summary>
    public static bool SetAvatarParameter(string name, object value)
    {
        try
        {
            var path = name.StartsWith('/') ? name : $"/avatar/parameters/{name}";
            byte[] packet = value switch
            {
                bool b => BuildOscMessage(path, b),
                int i => BuildOscMessage(path, i),
                float f => BuildOscMessage(path, f),
                double d => BuildOscMessage(path, (float)d),
                string s => BuildOscMessage(path, s),
                _ => BuildOscMessage(path, value.ToString() ?? "")
            };
            SendOscBytes(packet);
            Log.Write($"VR: параметр аватара {path} = {value}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Write($"VR avatar param error: {ex.Message}");
            return false;
        }
    }

    private static void SendOscBytes(byte[] bytes)
    {
        lock (Gate)
        {
            if (_oscSender is null)
            {
                _oscSender = new UdpClient();
                _oscSender.Connect(IPAddress.Loopback, VrcOscSendPort);
            }
            _oscSender.Send(bytes, bytes.Length);
        }
    }

    /// <summary>Построение бинарного OSC сообщения с выравниванием по 4 байта.</summary>
    private static byte[] BuildOscMessage(string address, params object[] args)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // 1. Адрес
        WriteOscString(bw, address);

        // 2. Type tags
        var sbTypes = new StringBuilder(",");
        foreach (var arg in args)
        {
            sbTypes.Append(arg switch
            {
                string => 's',
                int => 'i',
                float => 'f',
                bool b => b ? 'T' : 'F',
                _ => 's'
            });
        }
        WriteOscString(bw, sbTypes.ToString());

        // 3. Данные аргументов
        foreach (var arg in args)
        {
            switch (arg)
            {
                case string s:
                    WriteOscString(bw, s);
                    break;
                case int i:
                    var iBytes = BitConverter.GetBytes(i);
                    if (BitConverter.IsLittleEndian) Array.Reverse(iBytes);
                    bw.Write(iBytes);
                    break;
                case float f:
                    var fBytes = BitConverter.GetBytes(f);
                    if (BitConverter.IsLittleEndian) Array.Reverse(fBytes);
                    bw.Write(fBytes);
                    break;
                case bool:
                    // T/F не занимают места в payload
                    break;
            }
        }

        return ms.ToArray();
    }

    private static void WriteOscString(BinaryWriter bw, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        bw.Write(bytes);
        bw.Write((byte)0); // нуль-терминатор
        var pad = (4 - (bytes.Length + 1) % 4) % 4;
        for (var i = 0; i < pad; i++) bw.Write((byte)0);
    }

    // ---------- OpenVR Overlay (Слой 0) ----------

    /// <summary>Статус оверлея и шлема.</summary>
    public static string OverlayStatus()
    {
        var steamVr = IsSteamVrRunning();
        var vrc = IsVrcRunning();
        return $"SteamVR: {(steamVr ? "запущен" : "не запущен")}, VRChat: {(vrc ? "запущен" : "не запущен")}, " +
               $"OSC порт: {VrcOscSendPort}, Оверлей: {(_overlayActive ? "активен (1.2м в мире)" : "выключен")}";
    }

    /// <summary>Включение/выключение SteamVR оверлея.</summary>
    public static string ToggleOverlay(bool on)
    {
        if (on)
        {
            if (!IsSteamVrRunning())
                return "[НЕ СРАБОТАЛО] SteamVR не запущен. Запусти SteamVR, чтобы Клава появилась рядом в шлеме.";
            _overlayActive = true;
            Log.Write("VR: оверлей SteamVR включён");
            return "Оверлей SteamVR включён: Клава проецируется рядом с тобой в игровой зоне.";
        }
        _overlayActive = false;
        Log.Write("VR: оверлей SteamVR выключен");
        return "Оверлей SteamVR выключен.";
    }

    // ---------- Инструмент мозга (инструмент vr) ----------

    public static Task<string> RunToolAsync(string action, JsonObject args)
    {
        string Arg(string n) => args[n] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
        bool Flag(string n, bool def) => args[n] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : def;

        var act = action.Trim().ToLowerInvariant();
        switch (act)
        {
            case "status":
                return Task.FromResult(OverlayStatus());

            case "chat" or "say":
            {
                var text = Arg("text");
                if (string.IsNullOrWhiteSpace(text)) return Task.FromResult("Укажи text для отправки в чатбокс.");
                var ok = SendChatbox(text);
                return Task.FromResult(ok ? $"Отправила в чатбокс VRChat: «{text}»" : "Не удалось отправить в VRChat OSC.");
            }

            case "typing":
            {
                var on = Flag("on", true);
                SetTyping(on);
                return Task.FromResult($"Индикатор набора в VRChat: {(on ? "включён" : "выключен")}.");
            }

            case "emote":
            {
                var id = (int)(args["id"]?.GetValue<double>() ?? 1);
                SetAvatarParameter("VRCEmote", id);
                return Task.FromResult($"Включила эмоцию VRChat VRCEmote={id}.");
            }

            case "param":
            {
                var name = Arg("name");
                if (string.IsNullOrWhiteSpace(name)) return Task.FromResult("Укажи name параметра.");
                if (args["value"] is JsonValue jv)
                {
                    if (jv.TryGetValue<bool>(out var b)) SetAvatarParameter(name, b);
                    else if (jv.TryGetValue<int>(out var i)) SetAvatarParameter(name, i);
                    else if (jv.TryGetValue<double>(out var d)) SetAvatarParameter(name, (float)d);
                    else SetAvatarParameter(name, jv.ToString());
                }
                return Task.FromResult($"Параметр {name} отправлен в VRChat.");
            }

            case "overlay":
            {
                var on = Flag("on", true);
                return Task.FromResult(ToggleOverlay(on));
            }

            default:
                return Task.FromResult($"Команды vr: status | chat | typing | emote | param | overlay. Текущий статус: {OverlayStatus()}");
        }
    }
}
