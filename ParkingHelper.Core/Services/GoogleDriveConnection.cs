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

/// <summary>Serializes Settings and automatic sync operations without changing the merge engine.</summary>
public sealed class GoogleDriveConnection(
    IGoogleDriveAuthentication authentication,
    GoogleDriveSynchronizationService synchronization)
{
    private readonly object stateGate = new();
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? active;
    private bool disconnecting;
    public event Action? Changed;
    public bool IsDisconnecting => disconnecting;
    public bool IsConnected => !disconnecting && authentication.IsConnected;
    public GoogleDriveAccount? Account => IsConnected ? (authentication as IGoogleDriveSession)?.Account : null;
    private bool busy;
    public bool IsBusy => busy || disconnecting;
    public string Message { get; private set; } = "";
    public DateTime? LastSuccessfulSync { get; private set; }

    public Task InitializeAsync() => ExecuteAsync(async token =>
    {
        if (authentication is IGoogleDriveSession session) await session.InitializeAsync(token);
        Message = IsConnected ? "Connected" : "";
    });

    public Task ConnectAsync() => ExecuteAsync(async token =>
    {
        if (IsConnected) return;
        Message = "Connecting…";
        Notify();
        if (!await authentication.ConnectAsync(token)) { Message = ""; return; }
        if (IsConnected) await SyncCoreAsync(token);
    });

    public Task SyncAsync() => ExecuteAsync(async token =>
    {
        if (IsConnected) await SyncCoreAsync(token);
    });

    private async Task SyncCoreAsync(CancellationToken token)
    {
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
            LastSuccessfulSync = synchronization.Status.CompletedUtc;
            Message = "Connected";
        }
        else Message = synchronization.Status.Message;
    }

    public async Task DisconnectAsync()
    {
        lock (stateGate)
        {
            if (disconnecting) return;
            disconnecting = true;
            active?.Cancel();
        }
        Notify();
        await gate.WaitAsync();
        try
        {
            busy = true;
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
            lock (stateGate) { disconnecting = false; busy = false; }
            gate.Release();
            Notify();
        }
    }

    private async Task ExecuteAsync(Func<CancellationToken, Task> operation)
    {
        if (disconnecting || !await gate.WaitAsync(0)) return;
        using var source = new CancellationTokenSource();
        lock (stateGate)
        {
            if (disconnecting) { gate.Release(); return; }
            active = source;
            busy = true;
        }
        Notify();
        try { await operation(source.Token); }
        catch (OperationCanceledException) { Message = IsConnected ? "Connected" : ""; }
        catch (GoogleDriveAuthenticationExpiredException)
        {
            LastSuccessfulSync = null;
            Message = "Reconnect to Google Drive to synchronize.";
        }
        catch (NotSupportedException) { Message = "Google Drive is not configured for this build."; }
        catch (Exception)
        {
            Message = IsConnected
                ? "Sync could not complete. Local data was kept. Try Sync Now."
                : "Could not connect to Google Drive. Please try again.";
        }
        finally
        {
            lock (stateGate) { active = null; busy = false; }
            gate.Release();
            Notify();
        }
    }

    private void Notify() => Changed?.Invoke();
}
