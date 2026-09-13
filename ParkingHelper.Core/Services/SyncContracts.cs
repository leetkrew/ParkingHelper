using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

public enum SyncRunStatus
{
    NeverRun,
    Succeeded,
    Offline,
    Disconnected,
    Unavailable,
    Failed
}

public sealed record SyncStatus(SyncRunStatus State, DateTime? CompletedUtc, string Message);

public interface IGoogleDriveAccessTokenProvider
{
    ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public interface IGoogleDriveAuthentication : IGoogleDriveAccessTokenProvider
{
    bool IsConnected { get; }
    Task<bool> ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IGoogleDriveTransport
{
    Task<DriveSyncSnapshot?> DownloadAsync(CancellationToken cancellationToken = default);
    Task<DriveUploadResult> UploadAsync(SyncEnvelope envelope, DriveWriteCondition condition,
        CancellationToken cancellationToken = default);
}

public sealed record DriveFileCandidate(string FileId, string? ETag, string? RevisionId);
public sealed record DriveWriteCondition(string? FileId, string? ETag);
public sealed record DriveSyncSnapshot(
    SyncEnvelope Envelope,
    DriveWriteCondition Version,
    IReadOnlyList<DriveFileCandidate> DuplicateFiles);
public sealed record DriveUploadResult(DriveWriteCondition Version);
public sealed class DriveConcurrencyException(string message) : InvalidOperationException(message);
public interface IDriveDuplicateReconciler
{
    DriveFileCandidate SelectCanonical(IReadOnlyList<DriveFileCandidate> candidates);
}

public interface ISyncNetworkStatus
{
    bool IsOnline { get; }
}

public interface ISynchronizationTrigger
{
    void RequestSync();
    void RequestResumeSync();
}

public interface IConcurrencyRetryPolicy
{
    Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
}

public sealed class ConcurrencyRetryPolicy(int maxAttempts = 3, TimeSpan? delay = null) : IConcurrencyRetryPolicy
{
    private readonly TimeSpan delay = delay ?? TimeSpan.FromMilliseconds(50);

    public Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) =>
        ExecuteAsync<object?>(async token => { await operation(token).ConfigureAwait(false); return null; }, cancellationToken);

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        Exception? last = null;
        for (var attempt = 1; attempt <= Math.Max(1, maxAttempts); attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return await operation(cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (attempt < maxAttempts && error is IOException or TimeoutException)
            {
                last = error;
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
        throw last ?? new InvalidOperationException("The operation did not complete.");
    }
}

public sealed class GoogleDriveSynchronizationService(
    IParkingRepository repository,
    IGoogleDriveAuthentication authentication,
    IGoogleDriveTransport transport,
    ISyncNetworkStatus network,
    IConcurrencyRetryPolicy retry,
    TimeProvider? clock = null) : ISynchronizationService
{
    private readonly SemaphoreSlim singleFlight = new(1, 1);
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private SyncStatus status = new(SyncRunStatus.NeverRun, null, "Not synchronized");

    public SyncStatus Status => status;

    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (!await singleFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;
        try
        {
            if (!network.IsOnline)
            {
                status = new(SyncRunStatus.Offline, null, "Offline; local changes were kept.");
                return;
            }
            if (!authentication.IsConnected)
            {
                status = new(SyncRunStatus.Disconnected, null, "Connect Google Drive to synchronize.");
                return;
            }

            var duplicateCount = 0;
            for (var conflictAttempt = 0; ; conflictAttempt++)
            {
                try
                {
                    await retry.ExecuteAsync(async token =>
                    {
                        var local = await repository.GetSyncRecordsAsync().ConfigureAwait(false);
                        var remote = await transport.DownloadAsync(token).ConfigureAwait(false);
                        if (remote is not null) SyncSchema.Validate(remote.Envelope);
                        duplicateCount = remote?.DuplicateFiles.Count ?? 0;
                        var merged = SyncMergeEngine.Merge(local, remote?.Envelope.Records ?? []);
                        await repository.ApplySyncRecordsAsync(merged).ConfigureAwait(false);
                        await transport.UploadAsync(new SyncEnvelope(SyncSchema.CurrentVersion, merged),
                            remote?.Version ?? new DriveWriteCondition(null, null), token).ConfigureAwait(false);
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (DriveConcurrencyException) when (conflictAttempt < 2) { }
            }
            status = new(SyncRunStatus.Succeeded, clock.GetUtcNow().UtcDateTime,
                duplicateCount == 0
                    ? "Synced with Google Drive."
                    : $"Synced with Google Drive; {duplicateCount} duplicate file(s) need review.");
        }
        catch (SyncSchemaException)
        {
            status = new(SyncRunStatus.Failed, null, "Sync data is from a newer app version.");
            throw;
        }
        catch (NotSupportedException)
        {
            status = new(SyncRunStatus.Unavailable, null, "Google Drive synchronization is not configured.");
        }
        catch (Exception error) when (error is DriveConcurrencyException or IOException or TimeoutException)
        {
            status = new(SyncRunStatus.Failed, null, "Sync could not be completed. Local changes were kept.");
        }
        finally { singleFlight.Release(); }
    }
}
