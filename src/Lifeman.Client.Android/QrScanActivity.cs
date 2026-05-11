using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.Hardware.Camera2;
using Android.Hardware.Camera2.Params;
using Android.Media;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Core.App;
using AndroidX.Core.Content;
using ZXing;
using ZXing.Common;
using ZxResult = ZXing.Result;

namespace Lifeman.Client.Android;

/// In-app QR scan UI for the pairing flow. Acquires Camera2, pumps the
/// YUV_420_888 preview frames through ZXing.Net's pure-managed decoder
/// (no platform binding), and returns the decoded text via
/// `Intent.PutExtra(ResultKey, …)` on first valid `lifeman://` hit.
///
/// The fallback paste-URL flow in MainActivity remains the canonical
/// path: this activity is convenience over correctness, and any failure
/// (no camera, permission denied, decode hang) closes the activity so
/// the user lands back on the paste field.
[Activity(Label = "Scan pair QR", Exported = false,
    Theme = "@android:style/Theme.DeviceDefault.Light.NoActionBar")]
public sealed class QrScanActivity : Activity, TextureView.ISurfaceTextureListener
{
    public const string ResultKey = "scan_result";
    public const int RequestCode = 0x51; // 'Q'
    private const int CameraPermRequest = 0x52;

    private TextureView? _preview;
    private TextView? _hint;
    private CameraDevice? _camera;
    private CameraCaptureSession? _session;
    private ImageReader? _reader;
    private HandlerThread? _bgThread;
    private Handler? _bgHandler;
    private string? _cameraId;
    private Size? _previewSize;
    private readonly object _decodeGate = new();
    private bool _decoding;
    private bool _finished;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        var root = new FrameLayout(this);
        _preview = new TextureView(this) { SurfaceTextureListener = this };
        root.AddView(_preview, new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _hint = new TextView(this)
        {
            Text = "Point the camera at the lifeman pairing QR code.",
            TextSize = 14f,
        };
        _hint.SetTextColor(Color.White);
        _hint.SetBackgroundColor(Color.Argb(0xaa, 0, 0, 0));
        _hint.SetPadding(32, 32, 32, 32);
        var hintLp = new FrameLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent,
            ViewGroup.LayoutParams.WrapContent)
        { Gravity = GravityFlags.Bottom };
        root.AddView(_hint, hintLp);
        SetContentView(root);

        if (ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.Camera)
            != Permission.Granted)
        {
            ActivityCompat.RequestPermissions(this,
                new[] { global::Android.Manifest.Permission.Camera }, CameraPermRequest);
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, [global::Android.Runtime.GeneratedEnum] Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != CameraPermRequest) return;
        if (grantResults.Length == 0 || grantResults[0] != Permission.Granted)
        {
            FinishWithError("camera permission denied");
            return;
        }
        if (_preview?.IsAvailable == true && _preview.SurfaceTexture is not null)
            OpenCamera(_preview.SurfaceTexture, _preview.Width, _preview.Height);
    }

    protected override void OnPause()
    {
        TeardownCamera();
        base.OnPause();
    }

    public void OnSurfaceTextureAvailable(SurfaceTexture surface, int width, int height)
    {
        if (ContextCompat.CheckSelfPermission(this, global::Android.Manifest.Permission.Camera) == Permission.Granted)
            OpenCamera(surface, width, height);
    }
    public bool OnSurfaceTextureDestroyed(SurfaceTexture surface) { TeardownCamera(); return true; }
    public void OnSurfaceTextureSizeChanged(SurfaceTexture surface, int width, int height) { }
    public void OnSurfaceTextureUpdated(SurfaceTexture surface) { }

    private void OpenCamera(SurfaceTexture surface, int viewW, int viewH)
    {
        try
        {
            _bgThread = new HandlerThread("lifeman-qr");
            _bgThread.Start();
            _bgHandler = new Handler(_bgThread.Looper!);

            var manager = (CameraManager)GetSystemService(CameraService)!;
            _cameraId = ChooseBackCamera(manager) ?? throw new InvalidOperationException("no back camera");
            var chars = manager.GetCameraCharacteristics(_cameraId);
            var configs = (StreamConfigurationMap?)chars.Get(CameraCharacteristics.ScalerStreamConfigurationMap)
                ?? throw new InvalidOperationException("no stream config");
            // ImageReader format Yuv420888 — Camera2's standard preview surface.
            var sizes = configs.GetOutputSizes((int)ImageFormatType.Yuv420888) ?? Array.Empty<Size>();
            _previewSize = ChooseSize(sizes, 1280, 720);

            _reader = ImageReader.NewInstance(_previewSize.Width, _previewSize.Height, ImageFormatType.Yuv420888, 2);
            _reader.SetOnImageAvailableListener(new ImageAvailable(this), _bgHandler);

            surface.SetDefaultBufferSize(_previewSize.Width, _previewSize.Height);

            manager.OpenCamera(_cameraId, new CameraCallback(this, surface), _bgHandler);
        }
        catch (Exception ex)
        {
            FinishWithError($"camera open failed: {ex.Message}");
        }
    }

    private static string? ChooseBackCamera(CameraManager mgr)
    {
        foreach (var id in mgr.GetCameraIdList())
        {
            var c = mgr.GetCameraCharacteristics(id);
            var facing = (Java.Lang.Integer?)c.Get(CameraCharacteristics.LensFacing);
            if (facing is null || facing.IntValue() == (int)LensFacing.Back) return id;
        }
        return null;
    }

    internal static Size ChooseSize(Size[] sizes, int targetW, int targetH)
    {
        if (sizes.Length == 0) return new Size(targetW, targetH);
        // Pick the smallest size whose long edge is ≥ target. Smaller =
        // fewer bytes to scan per frame; ZXing decode time grows with area.
        var ordered = sizes.OrderBy(s => s.Width * s.Height).ToArray();
        foreach (var s in ordered)
            if (s.Width >= targetW && s.Height >= targetH) return s;
        return ordered[^1];
    }

    private void StartCaptureSession(SurfaceTexture surface)
    {
        if (_camera is null || _reader is null) return;
        var previewSurface = new Surface(surface);
        var outputs = new List<Surface> { previewSurface, _reader.Surface! };
        var builder = _camera.CreateCaptureRequest(CameraTemplate.Preview);
        builder.AddTarget(previewSurface);
        builder.AddTarget(_reader.Surface!);
        _camera.CreateCaptureSession(outputs, new SessionCallback(this, builder), _bgHandler);
    }

    private sealed class CameraCallback : CameraDevice.StateCallback
    {
        private readonly QrScanActivity _owner;
        private readonly SurfaceTexture _surface;
        public CameraCallback(QrScanActivity owner, SurfaceTexture surface) { _owner = owner; _surface = surface; }
        public override void OnOpened(CameraDevice camera) { _owner._camera = camera; _owner.StartCaptureSession(_surface); }
        public override void OnDisconnected(CameraDevice camera) { camera.Close(); _owner._camera = null; }
        public override void OnError(CameraDevice camera, CameraError error) { camera.Close(); _owner._camera = null; _owner.FinishWithError($"camera error: {error}"); }
    }

    private sealed class SessionCallback : CameraCaptureSession.StateCallback
    {
        private readonly QrScanActivity _owner;
        private readonly CaptureRequest.Builder _builder;
        public SessionCallback(QrScanActivity owner, CaptureRequest.Builder builder) { _owner = owner; _builder = builder; }
        public override void OnConfigured(CameraCaptureSession session)
        {
            _owner._session = session;
            try
            {
                _builder.Set(CaptureRequest.ControlAfMode, (int)ControlAFMode.ContinuousPicture);
                session.SetRepeatingRequest(_builder.Build()!, null, _owner._bgHandler);
            }
            catch (Exception ex) { _owner.FinishWithError($"capture session failed: {ex.Message}"); }
        }
        public override void OnConfigureFailed(CameraCaptureSession session) { _owner.FinishWithError("capture session config failed"); }
    }

    private sealed class ImageAvailable : Java.Lang.Object, ImageReader.IOnImageAvailableListener
    {
        private readonly QrScanActivity _owner;
        public ImageAvailable(QrScanActivity owner) => _owner = owner;
        public void OnImageAvailable(ImageReader reader)
        {
            using var img = reader.AcquireLatestImage();
            if (img is null) return;
            // Single-flight: drop frames while the prior decode is in flight.
            // ZXing on a 1280×720 luminance plane runs ~30–80ms on a midrange
            // phone; queueing every preview frame would just balloon RAM.
            lock (_owner._decodeGate)
            {
                if (_owner._decoding || _owner._finished) return;
                _owner._decoding = true;
            }
            try
            {
                var width = img.Width;
                var height = img.Height;
                var yPlane = img.GetPlanes()![0];
                var rowStride = yPlane.RowStride;
                var pixelStride = yPlane.PixelStride;
                var src = yPlane.Buffer!;
                var luma = new byte[width * height];
                CopyLuminance(src, luma, width, height, rowStride, pixelStride);

                var source = new PlanarYUVLuminanceSource(luma, width, height, 0, 0, width, height, false);
                var binarizer = new HybridBinarizer(source);
                var bitmap = new BinaryBitmap(binarizer);
                var hints = new Dictionary<DecodeHintType, object>
                {
                    { DecodeHintType.POSSIBLE_FORMATS, new List<BarcodeFormat> { BarcodeFormat.QR_CODE } },
                    { DecodeHintType.TRY_HARDER, true },
                };
                var reader2 = new MultiFormatReader();
                reader2.Hints = hints;
                ZxResult? result = null;
                try { result = reader2.decodeWithState(bitmap); }
                catch (ReaderException) { /* no QR in frame; keep scanning */ }

                if (result is not null && !string.IsNullOrEmpty(result.Text))
                    _owner.OnDecoded(result.Text);
            }
            catch (Exception)
            {
                // Per-frame decode errors are expected (motion blur, low light).
                // Don't tear down the session — wait for the next frame.
            }
            finally
            {
                lock (_owner._decodeGate) _owner._decoding = false;
            }
        }
    }

    /// Copy the Y plane to a tightly-packed luma byte[]. Camera2 hands
    /// us strided buffers that are usually 16-byte-aligned per row; ZXing
    /// expects width*height contiguous bytes, so we collapse the row
    /// stride here. Pixel stride for the Y plane is documented as 1 but
    /// some OEMs lie — handle ≠1 just in case.
    internal static void CopyLuminance(Java.Nio.ByteBuffer src, byte[] dst, int width, int height, int rowStride, int pixelStride)
    {
        if (rowStride == width && pixelStride == 1)
        {
            src.Get(dst, 0, dst.Length);
            return;
        }
        var rowBuf = new byte[rowStride];
        var srcPos = 0;
        var dstPos = 0;
        for (var row = 0; row < height; row++)
        {
            src.Position(srcPos);
            var toRead = Math.Min(rowBuf.Length, src.Remaining());
            src.Get(rowBuf, 0, toRead);
            if (pixelStride == 1)
            {
                Buffer.BlockCopy(rowBuf, 0, dst, dstPos, width);
            }
            else
            {
                for (var col = 0; col < width; col++)
                    dst[dstPos + col] = rowBuf[col * pixelStride];
            }
            dstPos += width;
            srcPos += rowStride;
        }
    }

    private void OnDecoded(string text)
    {
        // Only accept lifeman:// URLs — every other QR (wifi, vCard,
        // random URL) is rejected silently so the user can keep scanning
        // without us flapping back to the paste UI on noise.
        if (!text.StartsWith("lifeman://", StringComparison.OrdinalIgnoreCase)) return;
        lock (_decodeGate)
        {
            if (_finished) return;
            _finished = true;
        }
        RunOnUiThread(() =>
        {
            var result = new Intent();
            result.PutExtra(ResultKey, text);
            SetResult(global::Android.App.Result.Ok, result);
            Finish();
        });
    }

    private void FinishWithError(string msg)
    {
        global::Android.Util.Log.Warn("lifeman", $"qr: {msg}");
        lock (_decodeGate)
        {
            if (_finished) return;
            _finished = true;
        }
        RunOnUiThread(() =>
        {
            Toast.MakeText(this, msg, ToastLength.Short)?.Show();
            SetResult(global::Android.App.Result.Canceled);
            Finish();
        });
    }

    private void TeardownCamera()
    {
        try { _session?.Close(); } catch { } _session = null;
        try { _camera?.Close(); } catch { } _camera = null;
        try { _reader?.Close(); } catch { } _reader = null;
        try { _bgThread?.QuitSafely(); } catch { } _bgThread = null;
        _bgHandler = null;
    }
}
