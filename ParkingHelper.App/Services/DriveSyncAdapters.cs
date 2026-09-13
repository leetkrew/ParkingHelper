using Microsoft.Maui.Networking;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Services;

// The Google SDK/client credentials are intentionally supplied by a future platform
// adapter. This keeps the sync engine testable and prevents shipping fake credentials.
public sealed class UnconfiguredGoogleDriveAuthentication : IGoogleDriveAuthentication
{
    public bool IsConnected => false;
    public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
    public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(new NotSupportedException("Google Drive authentication is not configured."));
    public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class UnconfiguredGoogleDriveTransport : IGoogleDriveTransport
{
    public Task<DriveSyncSnapshot?> DownloadAsync(CancellationToken cancellationToken = default) =>
        Task.FromException<DriveSyncSnapshot?>(new NotSupportedException("Google Drive transport is not configured."));
    public Task<DriveUploadResult> UploadAsync(SyncEnvelope envelope, DriveWriteCondition condition,
        CancellationToken cancellationToken = default) =>
        Task.FromException<DriveUploadResult>(new NotSupportedException("Google Drive transport is not configured."));
}

public sealed class MauiSyncNetworkStatus : ISyncNetworkStatus
{
    public bool IsOnline => Connectivity.Current.NetworkAccess is NetworkAccess.Internet or NetworkAccess.ConstrainedInternet;
}

public sealed class SynchronizationTrigger(
    GoogleDriveSynchronizationService synchronization,
    IGoogleDriveAuthentication authentication,
    ISyncNetworkStatus network) : ISynchronizationTrigger
{
    private readonly object gate = new();
    private CancellationTokenSource? pending;

    public void RequestSync() => Schedule(TimeSpan.FromSeconds(2));
    public void RequestResumeSync() => Schedule(TimeSpan.Zero);

    private void Schedule(TimeSpan delay)
    {
        if (!network.IsOnline || !authentication.IsConnected) return;
        lock (gate)
        {
            pending?.Cancel();
            pending = new CancellationTokenSource();
            _ = RunAsync(pending, delay);
        }
    }

    private async Task RunAsync(CancellationTokenSource source, TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, source.Token).ConfigureAwait(false);
            await synchronization.SynchronizeAsync(source.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception) when (source.IsCancellationRequested) { }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(pending, source)) pending = null;
            }
            source.Dispose();
        }
    }
}
