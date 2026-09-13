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

// Registered once at app scope. The repeating work is metadata-only, never a full-sync timer.
public sealed class SynchronizationTrigger : ISynchronizationTrigger, IDisposable
{
    private readonly GoogleDriveConnection connection;
    private readonly ISyncNetworkStatus network;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private ITimer? debounce;
    private ITimer? check;
    private CancellationTokenSource? automatic;
    private bool foreground;
    private bool disposed;
    private long debounceRevision;
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(60);

    public SynchronizationTrigger(GoogleDriveConnection connection, ISyncNetworkStatus network, TimeProvider? clock = null)
    {
        this.connection = connection;
        this.network = network;
        this.clock = clock ?? TimeProvider.System;
        connection.Changed += OnConnectionChanged;
    }

    public void RequestSync()
    {
        // Called only after a successful SQLite write; never await Drive on this path.
        connection.MarkLocalChange();
        lock (gate)
        {
            if (disposed || !foreground || !connection.IsConnected) return;
            debounce?.Dispose();
            var source = automatic ??= new();
            var revision = ++debounceRevision;
            debounce = clock.CreateTimer(state => _ = DebounceAsync(source, revision), null, DebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void RequestResumeSync() => Resume(enterForeground: true);
    public void RequestNetworkSync() => Resume(enterForeground: false);

    private void Resume(bool enterForeground)
    {
        CancellationToken token;
        lock (gate)
        {
            if (disposed || !enterForeground && !foreground) return;
            foreground = true;
            automatic ??= new();
            token = automatic.Token;
            debounce?.Dispose();
            debounce = null;
            debounceRevision++;
            EnsureCheckTimer();
        }
        _ = connection.RestoreAndSyncAsync(token);
    }

    public void EnterBackground()
    {
        CancellationTokenSource? previous;
        lock (gate)
        {
            foreground = false;
            previous = StopAutomatic();
        }
        CancelAndDispose(previous);
    }

    private async Task DebounceAsync(CancellationTokenSource source, long revision)
    {
        CancellationToken token;
        lock (gate)
        {
            if (!CanRun(source) || revision != debounceRevision) return;
            token = source.Token;
        }
        if (network.IsOnline && connection.NeedsSynchronization)
            await connection.SyncAsync(token);
    }

    private async Task CheckAsync(CancellationTokenSource source)
    {
        CancellationToken token;
        lock (gate)
        {
            if (!CanRun(source)) return;
            token = source.Token;
        }
        try
        {
            if (network.IsOnline) await connection.CheckCloudAsync(token);
        }
        finally
        {
            lock (gate)
                if (CanRun(source)) check?.Change(CheckInterval, Timeout.InfiniteTimeSpan);
        }
    }

    private bool CanRun(CancellationTokenSource source) => !disposed && foreground &&
        ReferenceEquals(automatic, source) && !source.IsCancellationRequested && connection.IsConnected;

    private void OnConnectionChanged()
    {
        CancellationTokenSource? previous = null;
        lock (gate)
        {
            if (disposed) return;
            if (connection.IsDisconnecting) previous = StopAutomatic();
            else if (!connection.IsConnected)
            {
                debounce?.Dispose(); debounce = null; debounceRevision++;
                check?.Dispose(); check = null;
            }
            else
            {
                if (!connection.NeedsSynchronization) { debounce?.Dispose(); debounce = null; debounceRevision++; }
                EnsureCheckTimer();
            }
        }
        CancelAndDispose(previous);
    }

    private void EnsureCheckTimer()
    {
        if (!foreground || !connection.IsConnected || check is not null) return;
        var source = automatic ??= new();
        check = clock.CreateTimer(state => _ = CheckAsync(source), null, CheckInterval, Timeout.InfiniteTimeSpan);
    }

    private CancellationTokenSource? StopAutomatic()
    {
        debounce?.Dispose(); debounce = null; debounceRevision++;
        check?.Dispose(); check = null;
        var previous = automatic;
        automatic = null;
        return previous;
    }

    private static void CancelAndDispose(CancellationTokenSource? source)
    {
        if (source is null) return;
        source.Cancel();
        source.Dispose();
    }

    public void Dispose()
    {
        connection.Changed -= OnConnectionChanged;
        CancellationTokenSource? previous;
        lock (gate) { disposed = true; foreground = false; previous = StopAutomatic(); }
        CancelAndDispose(previous);
        // Runtime disposal must never revoke Google authorization or clear secure storage.
    }
}
