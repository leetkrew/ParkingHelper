namespace ParkingHelper.Core.Services;

public enum PreviewTapAction { Ignored, Rescan, RetryCamera }

/// <summary>Serializes preview recovery with lifecycle initialization. Taps never queue.</summary>
public sealed class ScanPreviewInteraction(TimeProvider clock)
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(1);
    private readonly SemaphoreSlim operations = new(1, 1);
    private bool rescanAttempted;
    private long? completedAt;
    public bool IsBusy => operations.CurrentCount == 0;

    public async Task<PreviewTapAction> TapAsync(Func<bool> canInteract, Func<bool> cameraHealthy,
        Action rescan, Func<Task> retry, CancellationToken cancellationToken = default)
    {
        if (!await operations.WaitAsync(0, cancellationToken)) return PreviewTapAction.Ignored;
        var accepted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!canInteract() || completedAt is { } previous && clock.GetElapsedTime(previous) < Cooldown)
                return PreviewTapAction.Ignored;
            accepted = true;
            if (!cameraHealthy() || rescanAttempted)
            {
                // A completed restart begins a fresh two-tap cycle.
                rescanAttempted = false;
                await retry();
                return PreviewTapAction.RetryCamera;
            }
            rescanAttempted = true;
            rescan();
            return PreviewTapAction.Rescan;
        }
        finally
        {
            if (accepted) completedAt = clock.GetTimestamp();
            operations.Release();
        }
    }

    public async Task InitializeAsync(Func<Task> initialize, CancellationToken cancellationToken)
    {
        // Lifecycle transitions may wait for the cancelled prior operation; preview taps cannot.
        await operations.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            rescanAttempted = false;
            completedAt = null;
            await initialize();
        }
        finally { operations.Release(); }
    }
}
