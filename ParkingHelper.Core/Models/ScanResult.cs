namespace ParkingHelper.Core.Models;

/// <summary>A decoded ticket held in memory. Format is the exact decoder enum name, never a conversion target.</summary>
public sealed class ScanResult
{
    private readonly byte[]? rawBytes;

    public ScanResult(string value, string format, byte[]? rawBytes, DateTimeOffset detectedUtc)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        Value = value;
        Format = format;
        this.rawBytes = rawBytes?.ToArray();
        DetectedUtc = detectedUtc.ToUniversalTime();
    }

    public string Value { get; }
    public string Format { get; }
    // Some symbologies have no raw bytes. Do not synthesize them by re-encoding Value.
    public byte[]? RawBytes => rawBytes?.ToArray();
    public DateTimeOffset DetectedUtc { get; }
}
