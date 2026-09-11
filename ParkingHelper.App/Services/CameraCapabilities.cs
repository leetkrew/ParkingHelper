using ZXing.Net.Maui.Controls;

namespace ParkingHelper.App.Services;

// ZXing 0.10.4 exposes IsTorchOn but no torch capability property.
// Query native capabilities only; every torch operation still goes through ZXing.
internal static class CameraCapabilities
{
    public static ZXing.Net.Maui.CameraLocation InitialLocation
    {
        get
        {
#if ANDROID
            // Avoid CameraX binding a nonexistent rear camera before enumeration is ready.
            var manager = Android.App.Application.Context.PackageManager;
            if (manager?.HasSystemFeature(Android.Content.PM.PackageManager.FeatureCamera) == false
                && manager.HasSystemFeature(Android.Content.PM.PackageManager.FeatureCameraFront))
                return ZXing.Net.Maui.CameraLocation.Front;
#endif
            return ZXing.Net.Maui.CameraLocation.Rear;
        }
    }

    public static bool HasTorch(CameraBarcodeReaderView view, string? selectedId)
    {
        try
        {
#if ANDROID
            var context = Android.App.Application.Context;
            var future = AndroidX.Camera.Lifecycle.ProcessCameraProvider.GetInstance(context);
            if (!future.IsDone) return false;
            var provider = (AndroidX.Camera.Lifecycle.ProcessCameraProvider?)future.Get();
            if (provider == null) return false;
            var facing = view.CameraLocation == ZXing.Net.Maui.CameraLocation.Front
                ? AndroidX.Camera.Core.CameraSelector.LensFacingFront : AndroidX.Camera.Core.CameraSelector.LensFacingBack;
            var index = 0;
            if (selectedId != null)
            {
                var parts = selectedId.Split('-');
                if (parts.Length != 2 || !int.TryParse(parts[1], out index)) return false;
                facing = parts[0] == "front" ? AndroidX.Camera.Core.CameraSelector.LensFacingFront : AndroidX.Camera.Core.CameraSelector.LensFacingBack;
            }
            return provider.AvailableCameraInfos.Where(c => c.CameraSelector != null && c.LensFacing == facing)
                .ElementAtOrDefault(index)?.HasFlashUnit == true;
#elif IOS || MACCATALYST
            // Inspect the actual capture input, including the device chosen by Automatic.
            if (view.Handler?.PlatformView is UIKit.UIView native)
                return FindDevice(native.Layer) is { HasTorch: true, TorchAvailable: true };
#endif
        }
        catch { /* A disappearing device has no usable torch. */ }
        return false;
    }

#if IOS || MACCATALYST
    private static AVFoundation.AVCaptureDevice? FindDevice(CoreAnimation.CALayer layer)
    {
        if (layer is AVFoundation.AVCaptureVideoPreviewLayer preview)
            return preview.Session?.Inputs.OfType<AVFoundation.AVCaptureDeviceInput>().FirstOrDefault()?.Device;
        foreach (var child in layer.Sublayers ?? [])
            if (FindDevice(child) is { } device) return device;
        return null;
    }
#endif
}
