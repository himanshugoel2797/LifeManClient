using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Lifeman.Client.Collectors;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinRT;

namespace Lifeman.Client.Windows.Collectors;

/// `desktop.screen_capture` — samples one frame of the primary monitor
/// every N seconds via the Windows Graphics Capture API, downscales to
/// ~1280px wide, JPEG-compresses at quality ~50, and base64-encodes the
/// bytes into the payload.
///
/// Per CLIENT_DESIGN: "Not for OCR — for 'what's roughly on screen' cues
/// the LLM can use as context." Cap quality and dimensions so the outbox
/// doesn't fill with multi-MB blobs.
///
/// If WGC isn't available (older SKU, secure desktop, no GPU) the
/// collector self-disables via a single `client.observation` event.
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class DesktopScreenCaptureCollector : ICollector
{
    public string Surface => "desktop.screen_capture";

    private readonly TimeSpan _interval;
    private readonly uint _maxWidth;
    private readonly float _jpegQuality;

    public DesktopScreenCaptureCollector(
        TimeSpan? interval = null,
        uint maxWidth = 1280,
        float jpegQuality = 0.50f)
    {
        _interval = interval ?? TimeSpan.FromSeconds(30);
        _maxWidth = maxWidth == 0 ? 1280 : maxWidth;
        _jpegQuality = JpegEncoder.NormalizeQuality(jpegQuality);
    }

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (!GraphicsCaptureSession.IsSupported())
        {
            yield return ClientObservations.CollectorDisabled(Surface,
                "GraphicsCaptureSession.IsSupported() returned false");
            yield break;
        }

        CaptureContext? context;
        string? initError;
        (context, initError) = TryInit();
        if (context is null)
        {
            yield return ClientObservations.CollectorDisabled(Surface,
                initError ?? "WGC init failed");
            yield break;
        }

        using (context)
        {
            while (!ct.IsCancellationRequested)
            {
                CollectedEvent? ev = null;
                try
                {
                    ev = await CaptureOnceAsync(context, _maxWidth, _jpegQuality, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { yield break; }
                catch (Exception ex)
                {
                    var payload = JsonSerializer.Serialize(new
                    {
                        error = ex.Message,
                        timestamp = DateTimeOffset.UtcNow.ToString("O"),
                    });
                    ev = new CollectedEvent(ClientObservations.Surface, payload, DateTimeOffset.UtcNow);
                }
                if (ev is not null) yield return ev;

                try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
            }
        }
    }

    private static async Task<CollectedEvent?> CaptureOnceAsync(
        CaptureContext ctx, uint maxWidth, float quality, CancellationToken ct)
    {
        // Best-effort: poll a few times for a fresh frame. WGC delivers
        // frames roughly at refresh rate once a session is started, but
        // the very first frame after StartCapture can take a beat.
        Direct3D11CaptureFrame? frame = null;
        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            frame = ctx.FramePool.TryGetNextFrame();
            if (frame is not null) break;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        if (frame is null) return null;

        using (frame)
        {
            var size = frame.ContentSize;
            if (size.Width <= 0 || size.Height <= 0) return null;

            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(
                frame.Surface, BitmapAlphaMode.Ignore).AsTask(ct).ConfigureAwait(false);

            var (targetW, targetH) = JpegEncoder.FitWidth(
                (uint)size.Width, (uint)size.Height, maxWidth);

            using var stream = new InMemoryRandomAccessStream();
            var props = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(quality, global::Windows.Foundation.PropertyType.Single) }
            };
            var encoder = await BitmapEncoder.CreateAsync(
                BitmapEncoder.JpegEncoderId, stream, props).AsTask(ct).ConfigureAwait(false);
            encoder.SetSoftwareBitmap(bitmap);
            if (targetW != (uint)size.Width || targetH != (uint)size.Height)
            {
                encoder.BitmapTransform.ScaledWidth = targetW;
                encoder.BitmapTransform.ScaledHeight = targetH;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
            }
            await encoder.FlushAsync().AsTask(ct).ConfigureAwait(false);

            stream.Seek(0);
            var len = (int)stream.Size;
            var buffer = new byte[len];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)len).AsTask(ct).ConfigureAwait(false);
            reader.ReadBytes(buffer);

            var payload = JsonSerializer.Serialize(new
            {
                width = (int)targetW,
                height = (int)targetH,
                source_width = size.Width,
                source_height = size.Height,
                jpeg_quality = quality,
                jpeg_base64 = Convert.ToBase64String(buffer),
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
            });
            return new CollectedEvent("desktop.screen_capture", payload, DateTimeOffset.UtcNow);
        }
    }

    private sealed class CaptureContext : IDisposable
    {
        public required IDirect3DDevice Device { get; init; }
        public required GraphicsCaptureItem Item { get; init; }
        public required Direct3D11CaptureFramePool FramePool { get; init; }
        public required GraphicsCaptureSession Session { get; init; }

        public void Dispose()
        {
            try { Session.Dispose(); } catch { }
            try { FramePool.Dispose(); } catch { }
            // Item / Device dispose paths through their COM refcounts when
            // the runtime collects them; nothing extra to do.
        }
    }

    private static (CaptureContext? Ctx, string? Error) TryInit()
    {
        try
        {
            // 1. Create a D3D11 device (BGRA support is required by WGC).
            var hr = NativeMethods.D3D11CreateDevice(
                IntPtr.Zero,
                NativeMethods.D3D_DRIVER_TYPE_HARDWARE,
                IntPtr.Zero,
                NativeMethods.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                null, 0,
                NativeMethods.D3D11_SDK_VERSION,
                out var d3dDevice, out _, out _);
            if (hr < 0 || d3dDevice == IntPtr.Zero)
            {
                // Fall back to WARP if the hardware path is unavailable.
                hr = NativeMethods.D3D11CreateDevice(
                    IntPtr.Zero,
                    NativeMethods.D3D_DRIVER_TYPE_WARP,
                    IntPtr.Zero,
                    NativeMethods.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    null, 0,
                    NativeMethods.D3D11_SDK_VERSION,
                    out d3dDevice, out _, out _);
                if (hr < 0 || d3dDevice == IntPtr.Zero)
                    return (null, $"D3D11CreateDevice failed: 0x{hr:X8}");
            }

            IDirect3DDevice winrtDevice;
            try
            {
                // Query IDXGIDevice off the D3D11 device, then bridge to WinRT.
                var dxgiGuid = NativeMethods.IID_IDXGIDevice;
                var qhr = Marshal.QueryInterface(d3dDevice, in dxgiGuid, out var dxgiDevice);
                if (qhr < 0 || dxgiDevice == IntPtr.Zero)
                    return (null, $"QueryInterface(IDXGIDevice) failed: 0x{qhr:X8}");
                try
                {
                    var chr = NativeMethods.CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectable);
                    if (chr < 0 || inspectable == IntPtr.Zero)
                        return (null, $"CreateDirect3D11DeviceFromDXGIDevice failed: 0x{chr:X8}");
                    try
                    {
                        winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                    }
                    finally { Marshal.Release(inspectable); }
                }
                finally { Marshal.Release(dxgiDevice); }
            }
            finally { Marshal.Release(d3dDevice); }

            // 2. Get a GraphicsCaptureItem for the primary monitor.
            var hmon = NativeMethods.MonitorFromPoint(new NativeMethods.POINT(0, 0),
                NativeMethods.MONITOR_DEFAULTTOPRIMARY);
            if (hmon == IntPtr.Zero) return (null, "MonitorFromPoint returned null");

            var itemPtr = IntPtr.Zero;
            var interopGuid = typeof(IGraphicsCaptureItemInterop).GUID;
            var activationFactory = WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
            var interop = activationFactory.AsInterface<IGraphicsCaptureItemInterop>();
            var captureItemGuid = NativeMethods.IID_IGraphicsCaptureItem;
            var ihr = interop.CreateForMonitor(hmon, in captureItemGuid, out itemPtr);
            if (ihr < 0 || itemPtr == IntPtr.Zero)
                return (null, $"IGraphicsCaptureItemInterop.CreateForMonitor failed: 0x{ihr:X8}");

            GraphicsCaptureItem item;
            try
            {
                item = MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally { Marshal.Release(itemPtr); }

            // 3. Frame pool + session. 2 buffers is plenty for one-shot polls.
            var pool = Direct3D11CaptureFramePool.Create(
                winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            var session = pool.CreateCaptureSession(item);
            try { session.IsCursorCaptureEnabled = false; } catch { }
            session.StartCapture();

            // Re-create framepool if monitor resolution changes mid-stream.
            item.Closed += (_, __) => { /* item closed; next CaptureOnce will return null */ };

            return (new CaptureContext
            {
                Device = winrtDevice,
                Item = item,
                FramePool = pool,
                Session = session,
            }, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, in Guid iid, out IntPtr result);
        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, in Guid iid, out IntPtr result);
    }

    private static class NativeMethods
    {
        public const uint D3D_DRIVER_TYPE_HARDWARE = 1;
        public const uint D3D_DRIVER_TYPE_WARP = 5;
        public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;
        public const uint D3D11_SDK_VERSION = 7;
        public const uint MONITOR_DEFAULTTOPRIMARY = 1;

        public static Guid IID_IDXGIDevice = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
        public static Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

        [StructLayout(LayoutKind.Sequential)]
        public readonly struct POINT
        {
            public readonly int X;
            public readonly int Y;
            public POINT(int x, int y) { X = x; Y = y; }
        }

        [DllImport("d3d11.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int D3D11CreateDevice(
            IntPtr pAdapter,
            uint DriverType,
            IntPtr Software,
            uint Flags,
            uint[]? pFeatureLevels,
            uint FeatureLevels,
            uint SDKVersion,
            out IntPtr ppDevice,
            out uint pFeatureLevel,
            out IntPtr ppImmediateContext);

        [DllImport("d3d11.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("user32.dll", ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);
    }
}
