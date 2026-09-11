using Microsoft.Extensions.Logging;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace ParkingHelper.App.Services;

public sealed class BarcodeScannerService(
    IScannerSettingsService settings, ScanSession session, IPreferences preferences,
    ILogger<BarcodeScannerService> logger) : IBarcodeScannerService
{
    private readonly ContentView host = new() { BackgroundColor = Colors.Black };
    private CameraBarcodeReaderView? camera;
    private IReadOnlyList<CameraInfo> devices = [];
    private CancellationTokenSource? lifetime;
    private long lastFrame;
    private long scanStarted;
    private bool configured;
    private bool detectBarcodes;
    private bool cancelled;
    private bool selecting;
    public View Preview => host;
    public ScanResult? Result => session.Result;
    public string Status { get; private set; } = "Allow camera access to scan your parking ticket.";
    public IReadOnlyList<ScannerCamera> Cameras { get; private set; } = [new(null, "Automatic")];
    public string? SelectedCameraId { get; private set; }
    public string VideoSource { get; private set; } = "Automatic";
    public bool CanUseTorch { get; private set; }
    public bool IsTorchOn => camera?.IsTorchOn == true;
    public bool NeedsPermissionSettings { get; private set; }
    public event EventHandler? Changed;
    public event EventHandler<ScanResult>? Captured;

    public async Task StartAsync(bool detectBarcodes = true)
    {
        Stop();
        this.detectBarcodes = detectBarcodes;
        var owner = lifetime = new CancellationTokenSource();
        var token = owner.Token;
        NeedsPermissionSettings = false;
        Status = Result != null ? SuccessText() : "Requesting camera access…";
        Notify();
        try
        {
            var permission = await Permissions.CheckStatusAsync<Permissions.Camera>();
            if (permission != PermissionStatus.Granted)
            {
                var requested = preferences.Get("scanner.cameraPermissionRequested", false);
                if (requested && permission == PermissionStatus.Denied && !Permissions.ShouldShowRationale<Permissions.Camera>())
                {
                    NeedsPermissionSettings = true;
                    Status = "Camera access is denied. Enable it in system settings, then tap Retry camera.";
                    Notify();
                    return;
                }
                preferences.Set("scanner.cameraPermissionRequested", true);
                permission = await Permissions.RequestAsync<Permissions.Camera>();
            }
            token.ThrowIfCancellationRequested();
            if (permission != PermissionStatus.Granted)
            {
                NeedsPermissionSettings = true;
                Status = "Camera permission was denied or restricted. Allow access in system settings or retry.";
                Notify();
                return;
            }
            if (!BarcodeScanning.IsSupported)
            {
                Status = "No camera found. Connect a camera and tap Retry camera.";
                Notify();
                return;
            }
            camera = new CameraBarcodeReaderView
            {
                IsDetecting = false,
                CameraLocation = CameraCapabilities.InitialLocation,
                Options = new BarcodeReaderOptions { Formats = ScannerFormatCatalog.Resolve(settings.Format), AutoRotate = true, Multiple = false, TryHarder = true }
            };
            camera.BarcodesDetected += Detected;
            camera.FrameReady += FrameReady;
            host.Content = camera;
            Status = Result != null ? SuccessText() : "Starting camera…";
            Notify();
            // A handler alone is insufficient on Android: CameraX initializes its provider asynchronously.
            for (var attempt = 0; attempt < 30; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (camera.Handler != null && camera.IsLoaded)
                {
                    devices = await camera.GetAvailableCameras();
                    token.ThrowIfCancellationRequested();
                    if (devices.Count > 0) break;
                }
                await Task.Delay(200, token);
            }
            token.ThrowIfCancellationRequested();
            UpdateCameraList();
            if (devices.Count == 0)
            {
                Status = "No camera found. Connect a camera and tap Retry camera.";
                ReleaseCamera();
                Notify();
                return;
            }
            var preferred = settings.PreferredCameraId;
            var missing = preferred != null && devices.All(c => c.DeviceId != preferred);
            if (missing) settings.PreferredCameraId = null;
            ApplyCamera(missing ? null : preferred);
            configured = true;
            camera.IsDetecting = detectBarcodes && Result == null && !cancelled;
            Status = !detectBarcodes ? "Select a video source below." : Result != null ? SuccessText() : missing
                ? "Preferred camera unavailable. Using Automatic. Point at a ticket barcode."
                : cancelled ? "Scan cancelled. Tap Rescan to start again." : "Point at a ticket barcode.";
            Notify();
            _ = MonitorAsync(owner);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (!token.IsCancellationRequested) Fail(exception);
        }
    }

    private void FrameReady(object? sender, CameraFrameBufferEventArgs e)
    {
        if (ReferenceEquals(sender, camera)) Interlocked.Exchange(ref lastFrame, Environment.TickCount64);
    }

    private void Detected(object? sender, BarcodeDetectionEventArgs e)
    {
        var generation = session.Generation;
        var result = e.Results?.FirstOrDefault(r => r.Value != null && Enum.IsDefined(r.Format));
        if (result == null) return;
        // Copy immediately: the decoder owns its result buffers.
        var raw = result.Raw?.ToArray();
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!ReferenceEquals(sender, camera) || !configured || cancelled || camera?.IsDetecting != true) return;
            // Some underlying readers fall back when a requested format has no decoder.
            // Enforce the user's restriction at the result boundary as well.
            if (!ScannerFormatCatalog.Resolve(settings.Format).HasFlag(result.Format)) return;
            if (!session.TryCapture(generation, result.Value, result.Format.ToString(), raw)) return;
            camera.IsDetecting = false;
            camera.IsTorchOn = false;
            Status = SuccessText();
            Captured?.Invoke(this, session.Result!);
            Notify();
        });
    }

    private string SuccessText() => "Ticket captured.";

    private void UpdateCameraList() => Cameras = new[] { new ScannerCamera(null, "Automatic") }
        .Concat(devices.Select(c => new ScannerCamera(c.DeviceId, c.Name))).ToArray();

    private void ApplyCamera(string? id)
    {
        if (camera == null) return;
        session.InvalidateFrames();
        camera.IsDetecting = false;
        camera.IsTorchOn = false;
        CanUseTorch = false;
        var selected = devices.FirstOrDefault(c => c.DeviceId == id);
        // Keep the library's rear/main default on mobile, with a front-only-device fallback.
        camera.CameraLocation = devices.Any(c => c.Location == CameraLocation.Rear) ? CameraLocation.Rear : CameraLocation.Front;
        camera.SelectedCamera = selected!;
        SelectedCameraId = selected?.DeviceId;
        VideoSource = selected?.Name ?? "Automatic (system camera)";
        scanStarted = Environment.TickCount64;
        Interlocked.Exchange(ref lastFrame, scanStarted);
    }

    public Task SelectCameraAsync(string? id)
    {
        if (selecting) return Task.CompletedTask;
        selecting = true;
        try
        {
            if (id != null && devices.All(c => c.DeviceId != id)) id = null;
            settings.PreferredCameraId = id;
            ApplyCamera(id);
            if (camera != null) camera.IsDetecting = detectBarcodes && configured && Result == null && !cancelled;
            Status = !detectBarcodes ? "Video source selected." : Result != null ? SuccessText() : "Camera selected. Point at a ticket barcode.";
            Notify();
        }
        catch (Exception exception) { Fail(exception); }
        finally { selecting = false; }
        return Task.CompletedTask;
    }

    public Task SwitchCameraAsync()
    {
        if (devices.Count < 2) return Task.CompletedTask;
        var current = devices.FirstOrDefault(c => c.DeviceId == SelectedCameraId)
            ?? devices.FirstOrDefault(c => c.Location == CameraLocation.Rear) ?? devices[0];
        var index = devices.ToList().IndexOf(current);
        return SelectCameraAsync(devices[(index + 1) % devices.Count].DeviceId);
    }

    private async Task MonitorAsync(CancellationTokenSource owner)
    {
        var token = owner.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(2000, token);
                if (camera == null) return;
                devices = await camera.GetAvailableCameras();
                token.ThrowIfCancellationRequested();
                UpdateCameraList();
                if (devices.Count == 0)
                {
                    settings.PreferredCameraId = null;
                    SelectedCameraId = null;
                    VideoSource = "Automatic";
                    Status = "Camera disconnected. Connect a camera and tap Retry camera.";
                    ReleaseCamera();
                    Notify();
                    return;
                }
                if (SelectedCameraId != null && devices.All(c => c.DeviceId != SelectedCameraId))
                {
                    await SelectCameraAsync(null);
                    Status = "Selected camera disconnected. Switched to Automatic.";
                }
                CanUseTorch = CameraCapabilities.HasTorch(camera, SelectedCameraId);
                if (!CanUseTorch) camera.IsTorchOn = false;
                var now = Environment.TickCount64;
                if (now - Interlocked.Read(ref lastFrame) > 10000)
                {
                    Status = "Camera unavailable or in use. Close other camera apps, check the connection, then Retry camera.";
                    ReleaseCamera();
                    Notify();
                    return;
                }
                if (detectBarcodes && Result == null && !cancelled && now - scanStarted > 15000)
                    Status = "No ticket read yet. Hold steady, improve the light, or check scanner settings.";
                Notify();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!token.IsCancellationRequested) Fail(exception); }
    }

    public void ToggleTorch()
    {
        try { if (CanUseTorch && camera != null) camera.IsTorchOn = !camera.IsTorchOn; Notify(); }
        catch (Exception exception) { Fail(exception); }
    }

    public void Rescan()
    {
        session.Clear();
        cancelled = false;
        scanStarted = Environment.TickCount64;
        if (camera != null && configured) camera.IsDetecting = detectBarcodes;
        Status = camera == null ? "Tap Retry camera to scan." : "Point at a ticket barcode.";
        Notify();
    }

    public void Cancel()
    {
        session.Clear();
        cancelled = true;
        if (camera != null) { camera.IsDetecting = false; camera.IsTorchOn = false; }
        Status = "Scan cancelled. Tap Rescan to start again.";
        Notify();
    }

    public void Stop()
    {
        lifetime?.Cancel();
        lifetime?.Dispose();
        lifetime = null;
        session.InvalidateFrames();
        ReleaseCamera();
    }

    private void ReleaseCamera()
    {
        configured = false;
        CanUseTorch = false;
        var old = camera;
        camera = null;
        if (old == null) return;
        old.BarcodesDetected -= Detected;
        old.FrameReady -= FrameReady;
        try
        {
            old.IsDetecting = false;
            old.IsTorchOn = false;
        }
        catch (Exception exception) { logger.LogWarning(exception, "Could not reset camera controls"); }
        finally
        {
            // A disappearing camera can reject torch/property updates. Still disconnect its native
            // handler after detaching events, so frame processing cannot survive page navigation.
            try { old.Handler?.DisconnectHandler(); }
            catch (Exception exception) { logger.LogWarning(exception, "Could not disconnect camera"); }
            finally { host.Content = null; }
        }
    }

    private void Fail(Exception exception)
    {
        logger.LogWarning(exception, "Scanner camera operation failed");
        Status = "Camera unavailable. Check permission and video source, then tap Retry camera.";
        ReleaseCamera();
        Notify();
    }
    private void Notify() => Changed?.Invoke(this, EventArgs.Empty);
}
