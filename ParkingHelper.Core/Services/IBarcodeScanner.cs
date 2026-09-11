namespace ParkingHelper.Core.Services;

// A future MAUI adapter maps ZXing results into this library-independent value.
public sealed record BarcodeScanResult(string Format, string Value);

public interface IBarcodeScanner
{
    // Null represents user cancellation. Camera UI/permissions belong in the MAUI adapter.
    Task<BarcodeScanResult?> ScanAsync(CancellationToken cancellationToken = default);
}
