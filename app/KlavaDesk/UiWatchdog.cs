namespace KlavaDesk;

/// <summary>
/// Сторож UI-потока (друг 30.09: «часто ловит зависон намертво»). Дамп живого зависания показал дедлок: инструмент
/// памяти в UI-потоке синхронно ждал задачу, чьё продолжение стояло в очереди того же UI-потока, — окно при этом ещё
/// отвечало на присланные сообщения, а таймеры, ответы и голос встали, и в логе не осталось ни слова. Сторож раз в 2 с
/// кладёт в очередь UI пустой вызов; не выполнился за 15 с — пишет в лог, сколько висит и чем UI был занят (Doing),
/// ожил — пишет и это. Зависание не лечит: его дело — чтобы следующий такой случай был виден сразу, без дампа.
/// </summary>
internal static class UiWatchdog
{
    private static long _pong = Environment.TickCount64;
    private static string _doing = "";
    private static long _doingSince;

    /// <summary>Чем сейчас занят UI-поток (инструмент мозга и т. п.) — попадёт в лог, если он зависнет.</summary>
    public static void Doing(string what)
    {
        Interlocked.Exchange(ref _doingSince, Environment.TickCount64);
        Volatile.Write(ref _doing, what);
    }

    public static void Done() => Volatile.Write(ref _doing, "");

    public static void Start(Control ui)
    {
        var t = new Thread(() =>
        {
            var reported = false;
            while (!ui.IsDisposed)
            {
                Thread.Sleep(2000);
                if (!ui.IsHandleCreated) continue;
                try { ui.BeginInvoke(() => Interlocked.Exchange(ref _pong, Environment.TickCount64)); }
                catch (InvalidOperationException) { return; }   // окно закрылось
                var late = Environment.TickCount64 - Interlocked.Read(ref _pong);
                if (late > 15000 && !reported)
                {
                    reported = true;
                    var d = Volatile.Read(ref _doing);
                    var since = (Environment.TickCount64 - Interlocked.Read(ref _doingSince)) / 1000;
                    Log.Write($"СТОРОЖ: UI-поток не отвечает {late / 1000} с" + (d.Length > 0 ? $" — занят: {d} (уже {since} с)" : " — занят неизвестно чем"));
                }
                else if (late < 5000 && reported)
                {
                    reported = false;
                    Log.Write("СТОРОЖ: UI-поток ожил");
                }
            }
        }) { IsBackground = true, Name = "UI-сторож" };
        t.Start();
    }
}
