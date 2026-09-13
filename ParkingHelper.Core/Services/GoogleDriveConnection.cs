namespace ParkingHelper.Core.Services;

public sealed record GoogleDriveAccount(string? Name, string? Email);

// Account/session operations are separate from the synchronization and merge contracts.
public interface IGoogleDriveSession : IGoogleDriveAuthentication
{
    GoogleDriveAccount? Account { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<bool> RefreshAccessTokenAsync(CancellationToken cancellationToken = default);
    Task ClearSessionAsync(CancellationToken cancellationToken = default);
}

public sealed class GoogleDriveAuthenticationExpiredException()
    : InvalidOperationException("Google Drive authorization expired. Reconnect to continue.");
public sealed class GoogleDriveDisconnectException(string message) : InvalidOperationException(message);

/// <summary>One shared flight for Settings, pull-to-refresh and automatic Drive work.</summary>
public sealed class GoogleDriveConnection(
    IGoogleDriveAuthentication authentication,
    GoogleDriveSynchronizationService synchronization)
{
    private readonly object stateGate = new();
    private CancellationTokenSource? active;
    private TaskCompletionSource? flight;
    private bool disconnecting;
    private long mutationVersion;
    private long completedMutationVersion;
    private long? attemptedMutationVersion;
    private bool fullSyncRequested;
    private bool retryNeeded = true;
    public event Action? Changed;
    public bool IsDisconnecting => disconnecting;
    public bool IsConnected => !disconnecting && authentication.IsConnected;
    public GoogleDriveAccount? Account => IsConnected ? (authentication as IGoogleDriveSession)?.Account : null;
    public bool IsBusy => flight is not null || disconnecting;
    public bool NeedsSynchronization
    {
        get { lock (stateGate) return retryNeeded || mutationVersion != completedMutationVersion; }
    }
    public string Message { get; private set; } = "";
    public DateTime? LastSuccessfulSync { get; private set; }

    public void MarkLocalChange()
    {
        lock (stateGate) mutationVersion++;
    }

    public Task InitializeAsync() => InitializeAsync(CancellationToken.None);
    public Task InitializeAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        if (authentication is IGoogleDriveSession session) await session.InitializeAsync(token);
        Message = IsConnected ? "Connected" : "";
    }, cancellationToken);

    public Task RestoreAndSyncAsync(CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
    {
        if (authentication is IGoogleDriveSession session) await session.InitializeAsync(token);
        if (IsConnected) await SyncCoreAsync(token);
    }, cancellationToken, requestFullSync: true);

    public Task ConnectAsync() => ExecuteAsync(async token =>
    {
        if (IsConnected) return;
        Message = "Connecting…";
        Notify();
        if (!await authentication.ConnectAsync(token)) { Message = ""; return; }
        if (IsConnected) await SyncCoreAsync(token);
    });

    public Task SyncAsync() => SyncAsync(CancellationToken.None);
    public Task SyncAsync(CancellationToken cancellationToken) => ExecuteAsync(async token =>
    {
        if (IsConnected) await SyncCoreAsync(token);
    }, cancellationToken, requestFullSync: true);

    public Task CheckCloudAsync(CancellationToken cancellationToken = default) => ExecuteAsync(async token =>
    {
        if (!IsConnected) return;
        if (NeedsSynchronization || await synchronization.HasCloudChangedAsync(token))
            await SyncCoreAsync(token);
    }, cancellationToken);

    private async Task SyncCoreAsync(CancellationToken token)
    {
        long version;
        lock (stateGate)
        {
            version = mutationVersion;
            attemptedMutationVersion = version;
            fullSyncRequested = false;
            retryNeeded = true;
        }
        Message = "Syncing…";
        Notify();
        await synchronization.SynchronizeAsync(token);
        if (!authentication.IsConnected)
        {
            Message = "Reconnect to Google Drive to synchronize.";
            LastSuccessfulSync = null;
        }
        else if (synchronization.Status.State == SyncRunStatus.Succeeded)
        {
            lock (stateGate)
            {
                completedMutationVersion = version;
                retryNeeded = false;
            }
            LastSuccessfulSync = synchronization.Status.CompletedUtc;
            Message = "Connected";
        }
        else Message = synchronization.Status.Message;
    }

    public async Task DisconnectAsync()
    {
        Task running;
        CancellationTokenSource? source;
        lock (stateGate)
        {
            if (disconnecting) return;
            disconnecting = true;
            source = active;
            running = flight?.Task ?? Task.CompletedTask;
        }
        // Cancellation callbacks can notify the coordinator; never invoke them under our lock.
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { /* The flight already finished. */ }
        Notify();
        await running;
        try
        {
            Message = "Disconnecting…";
            Notify();
            await authentication.DisconnectAsync();
            Message = "";
        }
        catch (GoogleDriveDisconnectException error) { Message = error.Message; }
        catch (Exception) { Message = "Could not finish disconnecting. Please try again."; }
        finally
        {
            LastSuccessfulSync = null;
            lock (stateGate) { disconnecting = false; retryNeeded = true; }
            Notify();
        }
    }

    private Task ExecuteAsync(Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default, bool requestFullSync = false)
    {
        TaskCompletionSource completion;
        CancellationTokenSource source;
        lock (stateGate)
        {
            if (disconnecting || cancellationToken.IsCancellationRequested) return Task.CompletedTask;
            if (flight is not null)
            {
                // Join the existing flight. A request during metadata/initialization needs
                // a full sync; repeated requests during a full sync need no extra queue.
                if (requestFullSync && attemptedMutationVersion is null) fullSyncRequested = true;
                return flight.Task;
            }
            completion = flight = new(TaskCreationOptions.RunContinuationsAsynchronously);
            source = active = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptedMutationVersion = null;
            fullSyncRequested = false;
        }
        _ = RunAsync(operation, source, completion);
        return completion.Task;
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation,
        CancellationTokenSource source, TaskCompletionSource completion)
    {
        Notify();
        try
        {
            await operation(source.Token);
            bool followUp;
            lock (stateGate)
                followUp = fullSyncRequested || attemptedMutationVersion is { } version && mutationVersion > version;
            // At most one follow-up per flight. Mutations during that follow-up remain
            // pending for the next debounce/lifecycle/check trigger, never an endless loop.
            if (followUp && IsConnected && !source.IsCancellationRequested)
                await SyncCoreAsync(source.Token);
        }
        catch (OperationCanceledException) { Message = IsConnected ? "Connected" : ""; }
        catch (GoogleDriveAuthenticationExpiredException)
        {
            LastSuccessfulSync = null;
            Message = "Reconnect to Google Drive to synchronize.";
        }
        catch (NotSupportedException) { Message = "Google Drive is not configured for this build."; }
        catch (Exception)
        {
            lock (stateGate) retryNeeded = true;
            Message = IsConnected
                ? "Sync could not complete. Local data was kept. Try Sync Now."
                : "Could not connect to Google Drive. Please try again.";
        }
        finally
        {
            lock (stateGate) { active = null; flight = null; }
            source.Dispose();
            completion.TrySetResult();
            Notify();
        }
    }

    private void Notify() => Changed?.Invoke();
}
