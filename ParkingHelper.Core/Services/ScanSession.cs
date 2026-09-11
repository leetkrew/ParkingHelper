using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

/// <summary>One pending result, no ticket persistence. All frame races pass through this gate.</summary>
public sealed class ScanSession(TimeProvider clock)
{
    private readonly object sync = new();
    private ScanResult? result;
    private long generation;
    public ScanResult? Result { get { lock (sync) return result; } }
    public long Generation { get { lock (sync) return generation; } }

    public bool TryCapture(long expectedGeneration, string? value, string format, byte[]? raw)
    {
        lock (sync)
        {
            if (generation != expectedGeneration || result != null || value == null || string.IsNullOrEmpty(format))
                return false;
            result = new ScanResult(value, format, raw, clock.GetUtcNow());
            return true;
        }
    }

    public void InvalidateFrames() { lock (sync) generation++; }
    public void Clear() { lock (sync) { result = null; generation++; } }
}
