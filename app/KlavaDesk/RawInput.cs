using System.Runtime.InteropServices;

namespace KlavaDesk;

/// <summary>
/// Живой ввод друга через Raw Input — клавиатура и мышь так, как их видят шутеры (движение мыши в сырых единицах, даже
/// когда курсор захвачен игрой). Регистрация одна на процесс (Windows держит одну цель на устройство), события раздаются
/// подписчикам: запись игры для учёбы (GameRecorder) и игрок по выученному (GamePlayer — «друг взялся за управление»).
/// Нажатия самой Клавы (SendInput) приходят без устройства — их отсюда не видно: это ввод ТОЛЬКО живого человека.
/// </summary>
internal static class RawInput
{
    /// <summary>Событие живого устройства. Мышь: Dx/Dy (относительно), Buttons — флаги RI_MOUSE_*, Wheel. Клавиша: Vk, KeyUp.</summary>
    internal readonly record struct Event(bool Mouse, int Dx, int Dy, ushort Buttons, short Wheel, ushort Vk, bool KeyUp);

    private static readonly object Gate = new();
    private static readonly List<Action<Event>> Subs = [];
    private static Sink? _sink;
    private static Thread? _thread;

    /// <summary>Подписаться; Dispose — отписаться (последний отписавшийся снимает регистрацию).</summary>
    public static IDisposable Subscribe(Action<Event> handler)
    {
        lock (Gate)
        {
            Subs.Add(handler);
            if (_thread is null)
            {
                var ready = new ManualResetEventSlim();
                _thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "raw-input" };
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.Start();
                ready.Wait(3000);
            }
        }
        return new Unsub(handler);
    }

    private sealed class Unsub(Action<Event> handler) : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Thread? t = null;
            lock (Gate)
            {
                Subs.Remove(handler);
                if (Subs.Count == 0 && _sink is not null)
                {
                    _sink.Close();
                    t = _thread;
                    _sink = null;
                    _thread = null;
                }
            }
            t?.Join(2000);
        }
    }

    private static void Loop(ManualResetEventSlim ready)
    {
        var sink = new Sink();
        lock (Gate) _sink = sink;
        if (!sink.Register()) Log.Write($"Raw Input: не зарегистрировался (код {Marshal.GetLastPInvokeError()})");
        ready.Set();
        Application.Run();   // очередь сообщений потока: WM_INPUT приходят сюда, пока окно не закрыто
    }

    private static void Raise(Event e)
    {
        Action<Event>[] subs;
        lock (Gate) subs = [.. Subs];
        foreach (var s in subs)
        {
            try { s(e); }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException) { Log.Error("Raw Input: подписчик", ex); }
        }
    }

    /// <summary>Невидимое окно-приёмник (RIDEV_INPUTSINK — ввод идёт, даже когда впереди игра).</summary>
    private sealed class Sink : NativeWindow
    {
        private const int WM_INPUT = 0x00FF, WM_CLOSE = 0x0010;
        private const uint RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1;

        public Sink() => CreateHandle(new CreateParams { Caption = "klava-raw-input", Parent = new IntPtr(-3) /* HWND_MESSAGE */ });

        public bool Register() => RegisterDevices(RIDEV_INPUTSINK, Handle);

        private static bool RegisterDevices(uint flags, IntPtr target)
        {
            var devs = new[]
            {
                new RawDevice { UsagePage = 1, Usage = 2, Flags = flags, Target = target },   // мышь
                new RawDevice { UsagePage = 1, Usage = 6, Flags = flags, Target = target },   // клавиатура
            };
            return RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RawDevice>());
        }

        public void Close()
        {
            if (Handle != IntPtr.Zero) PostMessage(Handle, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) { Read(m.LParam); m.Result = IntPtr.Zero; }
            else if (m.Msg == WM_CLOSE)
            {
                RegisterDevices(RIDEV_REMOVE, IntPtr.Zero);
                DestroyHandle();
                Application.ExitThread();
            }
            else base.WndProc(ref m);
        }

        private static void Read(IntPtr raw)
        {
            uint size = 0;
            var header = (uint)(8 + 2 * IntPtr.Size);
            GetRawInputData(raw, RID_INPUT, IntPtr.Zero, ref size, header);
            if (size == 0 || size > 1024) return;
            var buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(raw, RID_INPUT, buf, ref size, header) != size) return;
                var type = (uint)Marshal.ReadInt32(buf, 0);
                if (Marshal.ReadIntPtr(buf, 8) == IntPtr.Zero) return;   // SendInput — без устройства: это не человек
                var d = buf + (int)header;
                if (type == RIM_TYPEMOUSE)
                {
                    var flags = (ushort)Marshal.ReadInt16(d, 0);
                    var relative = (flags & 1) == 0;   // MOUSE_MOVE_RELATIVE
                    Raise(new Event(true, relative ? Marshal.ReadInt32(d, 12) : 0, relative ? Marshal.ReadInt32(d, 16) : 0,
                        (ushort)Marshal.ReadInt16(d, 4), Marshal.ReadInt16(d, 6), 0, false));
                }
                else if (type == RIM_TYPEKEYBOARD)
                {
                    var vk = (ushort)Marshal.ReadInt16(d, 6);
                    if (vk is 0 or 0xFF) return;
                    Raise(new Event(false, 0, 0, 0, 0, vk, (Marshal.ReadInt16(d, 2) & 1) != 0));   // RI_KEY_BREAK — отпустил
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RawDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RawDevice[] devices, uint count, uint size);

        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr raw, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    }
}
