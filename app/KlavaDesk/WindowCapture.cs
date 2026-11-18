using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace KlavaDesk;

/// <summary>
/// Съёмка одного окна через Windows.Graphics.Capture (как «Захват окна» в OBS). Друг 05.10: «Клава и Бакшот вместе —
/// игра лагает вместе со звуком». Раньше глаза игры раз в секунду прятали Клаву от снимков, ждали 70 мс и снимали весь
/// экран через GDI — Windows вытаскивала готовый кадр из видеокарты, пока игра ждала, а прятки дважды в секунду
/// перерисовывали её прозрачное окно поверх игры. Здесь сессия держится, пока смотрим игру: кадры кладёт в пул сама
/// система (свежий — только когда окно изменилось), а на запрос отдаётся последний; поверх лежащие окна (сама Клава)
/// в снимок не попадают — прятаться не нужно. Рамку захвата (Windows 11) убираем, где можно.
/// </summary>
internal sealed class WindowCapture : IDisposable
{
    [ComImport, System.Runtime.InteropServices.Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] ref Guid iid);
    }

    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid DxgiDeviceIid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    private const int DriverTypeHardware = 1;
    private const uint CreateDeviceBgraSupport = 0x20;
    private const uint SdkVersion = 7;
    private const DirectXPixelFormat Format = DirectXPixelFormat.B8G8R8A8UIntNormalized;

    private readonly IDirect3DDevice _device;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly object _gate = new();
    private Windows.Graphics.SizeInt32 _size;
    private Bitmap? _last;
    private bool _disposed;

    public IntPtr Hwnd { get; }

    /// <summary>Система умеет снимать окна так (Windows 10 1903+)?</summary>
    public static bool Supported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidCastException) { return false; }
        }
    }

    private WindowCapture(IntPtr hwnd)
    {
        Hwnd = hwnd;
        _device = CreateDevice();
        var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
        var iid = GraphicsCaptureItemIid;
        var ptr = interop.CreateForWindow(hwnd, ref iid);
        try { _item = GraphicsCaptureItem.FromAbi(ptr); }
        finally { Marshal.Release(ptr); }
        _size = _item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_device, Format, 2, _size);
        _session = _pool.CreateCaptureSession(_item);
        if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
            _session.IsCursorCaptureEnabled = false;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) &&
            ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
        {
            try { _session.IsBorderRequired = false; }   // жёлтая рамка вокруг окна игры
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { }
        }
        _session.StartCapture();
    }

    /// <summary>Начать снимать окно; null — система не умеет или окно не даётся (тогда глаза снимут по-старому).</summary>
    public static async Task<WindowCapture?> StartAsync(IntPtr hwnd)
    {
        if (!Supported) return null;
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && ApiInformation.IsTypePresent("Windows.Graphics.Capture.GraphicsCaptureAccess"))
        {
            // без рамки — с разрешения системы (Windows 11); не дали — снимем с рамкой
            try { await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless); }
            catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { }
        }
        try { return new WindowCapture(hwnd); }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or InvalidOperationException)
        {
            Log.Write($"глаза: захват окна не дался ({ex.Message}) — снимаю по-старому");
            return null;
        }
    }

    /// <summary>
    /// Последний кадр окна (новый Bitmap — освобождает вызывающий). Окно не менялось с прошлого раза — копия прошлого
    /// кадра; первого кадра ждём до 0,5 с.
    /// </summary>
    public async Task<Bitmap?> GrabAsync()
    {
        Direct3D11CaptureFrame? latest = null;
        lock (_gate)
        {
            if (_disposed) return null;
            while (_pool.TryGetNextFrame() is { } f) { latest?.Dispose(); latest = f; }
        }
        for (var i = 0; latest is null && _last is null && i < 25; i++)
        {
            await Task.Delay(20).ConfigureAwait(false);
            lock (_gate) { if (_disposed) return null; latest = _pool.TryGetNextFrame(); }
        }
        if (latest is null)
            lock (_gate) return _last is null ? null : (Bitmap)_last.Clone();
        using (latest)
        {
            var content = latest.ContentSize;
            Bitmap bmp;
            using (var sb = await SoftwareBitmap.CreateCopyFromSurfaceAsync(latest.Surface, BitmapAlphaMode.Ignore))
                bmp = ToBitmap(sb, Math.Min(content.Width, sb.PixelWidth), Math.Min(content.Height, sb.PixelHeight));
            lock (_gate)
            {
                if (_disposed) { bmp.Dispose(); return null; }
                // окно сменило размер — пул под новый размер (кадры до этого шли в старый буфер, с обрезкой/полями)
                if (content.Width > 0 && content.Height > 0 && (content.Width != _size.Width || content.Height != _size.Height))
                {
                    _size = content;
                    _pool.Recreate(_device, Format, 2, _size);
                }
                _last?.Dispose();
                _last = (Bitmap)bmp.Clone();
            }
            return bmp;
        }
    }

    /// <summary>BGRA из видеопамяти → Bitmap; обрезка до содержимого окна (буфер пула бывает больше окна).</summary>
    private static Bitmap ToBitmap(SoftwareBitmap sb, int width, int height)
    {
        var srcW = sb.PixelWidth;
        var buf = new Windows.Storage.Streams.Buffer((uint)(srcW * sb.PixelHeight * 4));
        sb.CopyToBuffer(buf);
        var data = new byte[buf.Length];
        using (var reader = DataReader.FromBuffer(buf)) reader.ReadBytes(data);
        var bmp = new Bitmap(Math.Max(1, width), Math.Max(1, height), PixelFormat.Format32bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
        try
        {
            for (var y = 0; y < bmp.Height; y++)
                Marshal.Copy(data, y * srcW * 4, bd.Scan0 + y * bd.Stride, bmp.Width * 4);
        }
        finally { bmp.UnlockBits(bd); }
        return bmp;
    }

    private static IDirect3DDevice CreateDevice()
    {
        var hr = D3D11CreateDevice(IntPtr.Zero, DriverTypeHardware, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0, SdkVersion,
            out var d3d, out _, out var context);
        if (hr < 0) throw new COMException("D3D11CreateDevice", hr);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in DxgiDeviceIid, out var dxgi));
            try
            {
                Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                finally { Marshal.Release(inspectable); }
            }
            finally { Marshal.Release(dxgi); }
        }
        finally
        {
            if (context != IntPtr.Zero) Marshal.Release(context);
            Marshal.Release(d3d);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _session.Dispose();
            _pool.Dispose();
            _device.Dispose();
            _last?.Dispose();
            _last = null;
        }
    }
}
