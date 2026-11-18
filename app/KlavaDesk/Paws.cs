using System.Runtime.InteropServices;

namespace KlavaDesk;

/// <summary>
/// Игровые лапки Клавы — универсальная виртуальная клавиатура и мышь для любой игры (CS 1.6, Serious Sam, Minecraft…),
/// без консолей и скриптов игры: только то, что делает живой игрок. Клавиши — скан-кодом (GameKey): их видят и SDL,
/// и DirectInput, и Raw Input, и обычные окна; мышь — относительным движением, как настоящая.
/// Зажатое ДЕРЖИТСЯ между ходами мозга (бежать вперёд, пока она смотрит на снимок и думает), но только пока окно игры
/// впереди: ушло окно, вышел срок, друг нажал Backspace или Esc, она договорила — всё отпускается само.
/// В чужое окно не уходит ничего: перед каждым нажатием проверяется, что впереди именно игра.
/// </summary>
internal sealed partial class Hands
{
    /// <summary>
    /// Лапки автопилота (игрок по выученному): живут без «сеанса рук» разговора, у них своя защита (Raw Input: друг
    /// взялся за управление). Сторож всё равно отпускает всё, когда окно игры уходит или срок не продлили.
    /// </summary>
    public bool Standalone { get; init; }

    private readonly object _padGate = new();
    private readonly List<ushort> _padKeys = [];
    private readonly List<MouseButtons> _padButtons = [];
    private uint _padPid;
    private DateTime _padUntil;
    private System.Threading.Timer? _padGuard;

    public static uint PidOf(IntPtr window)
    {
        Native.GetWindowThreadProcessId(window, out var pid);
        return pid;
    }

    /// <summary>Впереди окно этого процесса (игры) — только тогда лапки жмут.</summary>
    public static bool InFront(uint pid) => pid != 0 && PidOf(Native.GetForegroundWindow()) == pid;

    /// <summary>Зажата клавиша ходьбы (W/A/S/D) — она сейчас бежит.</summary>
    public bool PadMoving
    {
        get { lock (_padGate) return _padKeys.Any(k => k is 'W' or 'A' or 'S' or 'D'); }
    }

    /// <summary>Какие клавиши зажаты сейчас (копия).</summary>
    public List<ushort> PadKeys
    {
        get { lock (_padGate) return [.. _padKeys]; }
    }

    /// <summary>Что зажато сейчас — в ответ мозгу, чтобы она помнила про свои лапки; пусто — ничего.</summary>
    public string PadHeld
    {
        get
        {
            lock (_padGate)
            {
                if (_padKeys.Count == 0 && _padButtons.Count == 0) return "";
                var names = _padKeys.Select(KeyName).Concat(_padButtons.Select(ButtonName));
                var left = Math.Max(0, (int)Math.Ceiling((_padUntil - DateTime.UtcNow).TotalSeconds));
                return $"Зажато: {string.Join(", ", names)} — держу, пока не отпустишь (up), но не дольше {left} с и только пока окно игры впереди.";
            }
        }
    }

    /// <summary>Зажать клавиши и держать до PadUp (не дольше maxMs). false — окно игры не впереди, ничего не нажато.</summary>
    public bool PadDown(uint pid, IReadOnlyList<ushort> keys, int maxMs)
    {
        lock (_padGate)
        {
            if (!InFront(pid)) return false;
            foreach (var k in keys)
                if (!_padKeys.Contains(k)) { GameKey(k, true); _padKeys.Add(k); }
            Arm(pid, maxMs);
            return true;
        }
    }

    /// <summary>Отпустить клавиши; null — все клавиши.</summary>
    public void PadUp(IReadOnlyList<ushort>? keys)
    {
        lock (_padGate)
        {
            for (var i = _padKeys.Count - 1; i >= 0; i--)
                if (keys is null || keys.Contains(_padKeys[i])) { GameKey(_padKeys[i], false); _padKeys.RemoveAt(i); }
            DropGuardIfIdle();
        }
    }

    /// <summary>Зажать кнопку мыши (стрелять очередью, целиться ПКМ). false — окно игры не впереди.</summary>
    public bool PadMouseDown(uint pid, MouseButtons button, int maxMs)
    {
        lock (_padGate)
        {
            if (!InFront(pid)) return false;
            if (!_padButtons.Contains(button)) { SendMouse(ButtonFlags(button).Down, 0); _padButtons.Add(button); }
            Arm(pid, maxMs);
            return true;
        }
    }

    /// <summary>Отпустить кнопку мыши; null — все.</summary>
    public void PadMouseUp(MouseButtons? button)
    {
        lock (_padGate)
        {
            for (var i = _padButtons.Count - 1; i >= 0; i--)
                if (button is null || _padButtons[i] == button) { SendMouse(ButtonFlags(_padButtons[i]).Up, 0); _padButtons.RemoveAt(i); }
            DropGuardIfIdle();
        }
    }

    /// <summary>Отпустить всё зажатое (клавиши и кнопки мыши).</summary>
    public void PadReleaseAll(string why)
    {
        lock (_padGate)
        {
            if (_padKeys.Count == 0 && _padButtons.Count == 0) return;
            for (var i = _padKeys.Count - 1; i >= 0; i--) GameKey(_padKeys[i], false);
            foreach (var b in _padButtons) SendMouse(ButtonFlags(b).Up, 0);
            _padKeys.Clear();
            _padButtons.Clear();
            DropGuardIfIdle();
        }
        Log.Write($"лапки: отпустила всё зажатое — {why}");
    }

    private void Arm(uint pid, int maxMs)
    {
        _padPid = pid;
        _padUntil = DateTime.UtcNow.AddMilliseconds(Math.Clamp(maxMs, 200, 180000));
        _padGuard ??= new System.Threading.Timer(_ => PadGuardTick(), null, 100, 100);
    }

    private void DropGuardIfIdle()
    {
        if (_padKeys.Count > 0 || _padButtons.Count > 0) return;
        _padGuard?.Dispose();
        _padGuard = null;
    }

    /// <summary>Сторож: каждые 100 мс — окно игры ещё впереди, срок не вышел, сеанс рук идёт? Иначе отпустить всё.</summary>
    private void PadGuardTick()
    {
        string? why;
        lock (_padGate)
        {
            if (_padKeys.Count == 0 && _padButtons.Count == 0) { DropGuardIfIdle(); return; }
            why = !InFront(_padPid) ? "окно игры ушло с экрана"
                : DateTime.UtcNow > _padUntil ? "вышел срок"
                : !Active && !Standalone ? "сеанс рук кончился"
                : null;
        }
        if (why is not null) PadReleaseAll(why);
    }

    private static (uint Down, uint Up) ButtonFlags(MouseButtons b) => b switch
    {
        MouseButtons.Right => (Native.MOUSEEVENTF_RIGHTDOWN, Native.MOUSEEVENTF_RIGHTUP),
        MouseButtons.Middle => (Native.MOUSEEVENTF_MIDDLEDOWN, Native.MOUSEEVENTF_MIDDLEUP),
        _ => (Native.MOUSEEVENTF_LEFTDOWN, Native.MOUSEEVENTF_LEFTUP),
    };

    private static string ButtonName(MouseButtons b) => b switch { MouseButtons.Right => "ПКМ", MouseButtons.Middle => "СКМ", _ => "ЛКМ" };

    /// <summary>Имя клавиши для ответа мозгу: буквы и цифры как есть, остальное — по таблице имён.</summary>
    private static string KeyName(ushort vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        foreach (var (name, code) in KeyNames)
            if (code == vk && name.All(c => c < 128)) return name;
        return $"0x{vk:X2}";
    }

    /// <summary>
    /// Игра запущена от администратора, а Клава — нет: Windows (UIPI) молча выбрасывает её нажатия — SendInput
    /// даже не говорит об ошибке. У Serious Sam 2 друга, например, стоит «запуск от имени администратора».
    /// </summary>
    public static bool BlockedByElevation(uint pid) =>
        pid != 0 && IsElevated((uint)Environment.ProcessId) == false && IsElevated(pid) == true;

    /// <summary>Процесс с повышенными правами? null — Windows не дала даже спросить.</summary>
    private static bool? IsElevated(uint pid)
    {
        var proc = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc == IntPtr.Zero) return null;
        try
        {
            if (!Native.OpenProcessToken(proc, Native.TOKEN_QUERY, out var token)) return null;
            try { return Native.GetTokenInformation(token, Native.TokenElevation, out var elevated, sizeof(int), out _) ? elevated != 0 : null; }
            finally { Native.CloseHandle(token); }
        }
        finally { Native.CloseHandle(proc); }
    }
}

internal static partial class Native
{
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
    public const int TokenElevation = 20;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returned);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(IntPtr handle);
}
