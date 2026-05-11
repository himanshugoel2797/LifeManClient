using System.Text.Json;
using Android.Content;
using Android.Graphics;
using Android.Hardware.Display;
using Android.Media;
using Android.Media.Projection;
using Android.OS;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Lifeman.Client.Android.Services;
using Lifeman.Client.Collectors;

namespace Lifeman.Client.Android.Collectors;

/// `phone.screen_capture` — opt-in low-rate JPEG snapshots of the phone
/// screen via MediaProjection. Per CLIENT_DESIGN: not for OCR, just
/// "what's roughly on screen" cues for the LLM. Default cadence 30s,
/// JPEG q~50, max 1280px on the long edge, base64 in the payload.
///
/// The user has to grant MediaProjection consent each process via
/// MainActivity (system requirement — the FGS can't raise the dialog
/// itself). MainActivity stashes the resulting Intent in
/// MediaProjectionState; this collector reads it on each tick. Missing
/// grant → emit CollectorDisabled and yield-break; user must re-enable
/// from the activity.
///
/// We build and tear down VirtualDisplay + MediaProjection per capture.
/// Keeping the projection alive between samples saves a few millis but
/// (a) keeps an extra surface bound 24/7 for a 1/30s sample, and (b)
/// makes shutdown ordering harder. The persistent FGS notification stays
/// up regardless, so there's no UX difference to the user.
public sealed class PhoneScreenCaptureCollector : ICollector
{
    private readonly Context _ctx;
    private readonly TimeSpan _interval;
    private const int MaxLongEdgePx = 1280;
    private const int JpegQuality = 50;

    public string Surface => "phone.screen_capture";

    public PhoneScreenCaptureCollector(Context ctx, TimeSpan? interval = null)
    {
        _ctx = ctx;
        _interval = interval ?? TimeSpan.FromSeconds(30);
    }

    public async IAsyncEnumerable<CollectedEvent> StreamAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var mpm = (MediaProjectionManager?)_ctx.GetSystemService(Context.MediaProjectionService);
        if (mpm is null)
        {
            yield return ClientObservations.CollectorDisabled(Surface, "MediaProjectionManager unavailable");
            yield break;
        }

        // Wait once before first sample so the FGS has a chance to come
        // up and the user has a beat to grant consent if they just
        // started the agent.
        try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
        catch (System.OperationCanceledException) { yield break; }

        var warnedNoConsent = false;

        while (!ct.IsCancellationRequested)
        {
            var consent = MediaProjectionState.ConsentData;
            var resultCode = MediaProjectionState.ConsentResultCode;
            if (consent is null)
            {
                if (!warnedNoConsent)
                {
                    Log.Warn("lifeman", "phone.screen_capture: no consent token; collector disabled until user grants via MainActivity");
                    warnedNoConsent = true;
                    yield return ClientObservations.CollectorDisabled(Surface, "MediaProjection consent not granted");
                }
                yield break;
            }

            CollectedEvent? captured = null;
            string? failure = null;
            try
            {
                captured = CaptureOnce(mpm, resultCode, consent);
            }
            catch (Exception ex)
            {
                failure = ex.Message;
                Log.Warn("lifeman", $"phone.screen_capture: capture failed: {ex}");
            }

            if (failure is not null)
            {
                // Treat any capture exception as consent revocation —
                // MediaProjection grants are routinely killed by the
                // system (process moved to background pre-FGS, user
                // tapped "Stop sharing" in the system UI, etc).
                MediaProjectionState.Clear();
                yield return ClientObservations.CollectorDisabled(Surface,
                    $"MediaProjection capture failed: {failure}");
                yield break;
            }

            if (captured is not null) yield return captured;

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (System.OperationCanceledException) { yield break; }
        }
    }

    private CollectedEvent? CaptureOnce(MediaProjectionManager mpm, int resultCode, Intent consent)
    {
        // Resolve display size up-front: the VirtualDisplay must be
        // created at the real screen resolution or the captured frame
        // is letter-/pillar-boxed.
        var (srcW, srcH, dpi) = GetDisplayMetrics();
        if (srcW <= 0 || srcH <= 0)
        {
            throw new InvalidOperationException("display metrics unavailable");
        }

        MediaProjection? projection = null;
        ImageReader? reader = null;
        VirtualDisplay? virtualDisplay = null;
        Image? image = null;
        Bitmap? raw = null;
        Bitmap? scaled = null;
        try
        {
            projection = mpm.GetMediaProjection(resultCode, consent)
                ?? throw new InvalidOperationException("GetMediaProjection returned null");

            // Required from API 34+: register a callback before creating
            // a VirtualDisplay or the framework throws SecurityException.
            // Harmless on older APIs.
            projection.RegisterCallback(new ProjectionStopCallback(), null);

            // ImageReader.NewInstance is typed as ImageFormatType but the
            // concrete PixelFormat constants live on Android.Graphics.Format;
            // both enums share the same int values from the Android SDK.
            reader = ImageReader.NewInstance(srcW, srcH, (ImageFormatType)Format.Rgba8888, 2)
                ?? throw new InvalidOperationException("ImageReader.NewInstance returned null");

            virtualDisplay = projection.CreateVirtualDisplay(
                "lifeman-screencap",
                srcW, srcH, dpi,
                // The CreateVirtualDisplay overload is typed as DisplayFlags
                // (Android.Views) but the OwnContentOnly bit is only declared
                // on VirtualDisplayFlags (Android.Hardware.Display). Same int
                // values from the SDK; cast through to combine them.
                (DisplayFlags)((int)VirtualDisplayFlags.Presentation | (int)VirtualDisplayFlags.OwnContentOnly),
                reader.Surface,
                null,
                null);
            if (virtualDisplay is null)
                throw new InvalidOperationException("CreateVirtualDisplay returned null");

            // Poll for the first frame. ImageReader.AcquireLatestImage
            // returns null until the producer pushes; ~500ms is plenty
            // even on slow devices for one frame.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(800);
            while (image is null && DateTimeOffset.UtcNow < deadline)
            {
                image = reader.AcquireLatestImage();
                if (image is null) Thread.Sleep(40);
            }
            if (image is null)
                throw new TimeoutException("no frame from VirtualDisplay within 800ms");

            raw = ImageToBitmap(image, srcW, srcH);
            scaled = Downscale(raw, MaxLongEdgePx);

            byte[] jpegBytes;
            using (var ms = new System.IO.MemoryStream())
            {
                if (!scaled.Compress(Bitmap.CompressFormat.Jpeg!, JpegQuality, ms))
                    throw new InvalidOperationException("Bitmap.Compress returned false");
                jpegBytes = ms.ToArray();
            }

            var b64 = Convert.ToBase64String(jpegBytes);
            var payload = JsonSerializer.Serialize(new
            {
                image_b64 = b64,
                width = scaled.Width,
                height = scaled.Height,
                format = "jpeg",
                quality = JpegQuality,
                source_width = srcW,
                source_height = srcH,
                bytes = jpegBytes.Length,
                timestamp = DateTimeOffset.UtcNow.ToString("O"),
            });
            return new CollectedEvent(Surface, payload, DateTimeOffset.UtcNow);
        }
        finally
        {
            try { image?.Close(); } catch { }
            try { virtualDisplay?.Release(); } catch { }
            try { reader?.Close(); } catch { }
            try { projection?.Stop(); } catch { }
            if (scaled is not null && !ReferenceEquals(scaled, raw))
            {
                try { scaled.Recycle(); } catch { }
            }
            try { raw?.Recycle(); } catch { }
        }
    }

    private (int w, int h, int dpi) GetDisplayMetrics()
    {
        // WindowManager from the application context returns the default
        // display, which is what MediaProjection mirrors anyway.
        var wm = (IWindowManager?)_ctx.GetSystemService(Context.WindowService);
        var display = wm?.DefaultDisplay;
        if (display is null) return (0, 0, 0);

        var metrics = new DisplayMetrics();
        display.GetRealMetrics(metrics);
        return (metrics.WidthPixels, metrics.HeightPixels, (int)metrics.DensityDpi);
    }

    private static Bitmap ImageToBitmap(Image image, int width, int height)
    {
        var planes = image.GetPlanes() ?? throw new InvalidOperationException("Image planes null");
        if (planes.Length == 0) throw new InvalidOperationException("Image has no planes");
        var plane = planes[0];
        var buffer = plane.Buffer ?? throw new InvalidOperationException("Image plane buffer null");
        var pixelStride = plane.PixelStride;
        var rowStride = plane.RowStride;
        var rowPadding = rowStride - pixelStride * width;

        // ImageReader.RGBA_8888 may pad each row out to a stride wider
        // than width*4. Allocate a bitmap with the row-stride width
        // first, copy raw pixels in, then crop to the screen width so
        // the right edge isn't garbage.
        var paddedWidth = width + rowPadding / pixelStride;
        var padded = Bitmap.CreateBitmap(paddedWidth, height, Bitmap.Config.Argb8888!)
            ?? throw new InvalidOperationException("Bitmap.CreateBitmap returned null");
        padded.CopyPixelsFromBuffer(buffer);
        if (paddedWidth == width) return padded;

        var cropped = Bitmap.CreateBitmap(padded, 0, 0, width, height)
            ?? throw new InvalidOperationException("Bitmap.CreateBitmap (crop) returned null");
        if (!ReferenceEquals(cropped, padded))
        {
            try { padded.Recycle(); } catch { }
        }
        return cropped;
    }

    private static Bitmap Downscale(Bitmap src, int maxLongEdge)
    {
        var longEdge = Math.Max(src.Width, src.Height);
        if (longEdge <= maxLongEdge) return src;
        var scale = (float)maxLongEdge / longEdge;
        var newW = Math.Max(1, (int)Math.Round(src.Width * scale));
        var newH = Math.Max(1, (int)Math.Round(src.Height * scale));
        return Bitmap.CreateScaledBitmap(src, newW, newH, true)
            ?? throw new InvalidOperationException("Bitmap.CreateScaledBitmap returned null");
    }

    private sealed class ProjectionStopCallback : MediaProjection.Callback
    {
        public override void OnStop() { /* per-cycle teardown handles this */ }
    }
}
