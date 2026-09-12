namespace ParkingHelper.Core.Services;

/// <summary>Requires matching reads on adjacent camera frames before accepting a candidate.
/// Call serially; timestamps are monotonic milliseconds captured at detection, not UI dispatch.</summary>
public sealed class ScanStabilityGate
{
    public const long WindowMilliseconds = 500;
    private string? value;
    private string? format;
    private long generation;
    private long frame;
    private long timestamp;

    public bool Observe(long generation, long frame, long timestamp, string? value, string? format)
    {
        if (value == null || string.IsNullOrEmpty(format))
        {
            Reset();
            return false;
        }

        // ZXing emits FrameReady before decoding, but no detection event for blank frames.
        // A sequence gap therefore invalidates the previous read, even inside the time window.
        var confirmed = this.value == value && this.format == format && this.generation == generation
            && frame == this.frame + 1 && timestamp >= this.timestamp
            && timestamp - this.timestamp <= WindowMilliseconds;
        this.value = value;
        this.format = format;
        this.generation = generation;
        this.frame = frame;
        this.timestamp = timestamp;
        return confirmed;
    }

    public void Reset()
    {
        value = null;
        format = null;
    }
}
