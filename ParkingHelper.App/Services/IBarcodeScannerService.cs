using ParkingHelper.Core.Models;

namespace ParkingHelper.App.Services;

public sealed record ScannerCamera(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>MAUI boundary. No ZXing controls or types escape this adapter.</summary>
public interface IBarcodeScannerService
{
    View Preview { get; }
    ScanResult? Result { get; }
    string Status { get; }
    IReadOnlyList<ScannerCamera> Cameras { get; }
    string? SelectedCameraId { get; }
    string VideoSource { get; }
    bool CanUseTorch { get; }
    bool IsTorchOn { get; }
    bool HasCameraError { get; }
    bool NeedsPermissionSettings { get; }
    event EventHandler? Changed;
    // Raised once, synchronously on the UI thread, for each accepted scan.
    event EventHandler<ScanResult>? Captured;
    Task StartAsync(bool detectBarcodes = true);
    void Stop();
    Task SelectCameraAsync(string? id);
    Task SwitchCameraAsync();
    void ToggleTorch();
    void Rescan();
    void Cancel();
}
