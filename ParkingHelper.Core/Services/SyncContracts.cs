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

public interface IGoogleDriveVersionReader
{
    Task<DriveWriteCondition?> ReadCloudVersionAsync(CancellationToken cancellationToken = default);
}

public sealed record DriveFileCandidate(string FileId, string? VersionToken, string? RevisionId);
/// <summary>The transport's observed server version; not necessarily an HTTP ETag.</summary>
public sealed record DriveWriteCondition(string? FileId, string? VersionToken);
public sealed record DriveSyncSnapshot(
    SyncEnvelope Envelope,
    DriveWriteCondition Version,
    IReadOnlyList<DriveFileCandidate> DuplicateFiles);
public sealed record DriveUploadResult(DriveWriteCondition Version);
public sealed class DriveTransportException(string message) : InvalidOperationException(message);
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

    public GoogleDriveSyncDiagnostics? Diagnostics { get; init; }
    public SyncStatus Status => status;
    public DriveWriteCondition? ObservedCloudVersion { get; private set; }

    public async Task<bool> HasCloudChangedAsync(CancellationToken cancellationToken = default)
    {
        await singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!network.IsOnline || !authentication.IsConnected) return false;
            if (ObservedCloudVersion is null) return true;
            if (transport is not IGoogleDriveVersionReader reader) return false;
            return await reader.ReadCloudVersionAsync(cancellationToken).ConfigureAwait(false) != ObservedCloudVersion;
        }
        finally { singleFlight.Release(); }
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        if (!await singleFlight.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return;
        Diagnostics?.Begin(clock.GetUtcNow().UtcDateTime);
        try
        {
            if (!network.IsOnline)
            {
                Diagnostics?.Update(d => d with { Outcome = "Offline" });
                status = new(SyncRunStatus.Offline, null, "Offline; local changes were kept.");
                return;
            }
            if (!authentication.IsConnected)
            {
                Diagnostics?.Update(d => d with { Outcome = "Disconnected" });
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
                        Diagnostics?.Update(d => d with { Attempt = d.Attempt + 1, Stage = DriveDiagnosticStage.LocalRead,
                            CloudDownloaded = null, DownloadedFile = null, MatchingFiles = null, MergeOutput = null,
                            SQLiteAfter = null, SQLiteReadBackError = null, UploadedFile = null, UploadedCounts = null,
                            UploadOutcome = "Not attempted", Persistence = null });
                        var local = await repository.GetSyncRecordsAsync().ConfigureAwait(false);
                        Diagnostics?.Update(d => d with { LocalBefore = DriveDiagnosticCounts.From(local), Stage = DriveDiagnosticStage.Download });
                        var remote = await transport.DownloadAsync(token).ConfigureAwait(false);
                        Diagnostics?.Update(d => d with { CloudDownloaded = DriveDiagnosticCounts.From(remote?.Envelope.Records ?? []),
                            Stage = DriveDiagnosticStage.ValidateCloud });
                        if (remote is not null) SyncSchema.Validate(remote.Envelope);
                        duplicateCount = remote?.DuplicateFiles.Count ?? 0;
                        Diagnostics?.Update(d => d with { Stage = DriveDiagnosticStage.Merge });
                        var merged = SyncMergeEngine.Merge(local, remote?.Envelope.Records ?? []);
                        Diagnostics?.Update(d => d with { MergeOutput = DriveDiagnosticCounts.From(merged), Stage = DriveDiagnosticStage.ApplySQLite });
                        try { await repository.ApplySyncRecordsAsync(merged).ConfigureAwait(false); }
                        catch (SyncPersistenceException) { throw; }
                        catch (Exception) { throw new SyncPersistenceException("LocalApplyFailed"); }
                        await CaptureDiagnosticLocalAsync(afterApply: true).ConfigureAwait(false);
                        // Compare values rather than record equality (barcode payloads contain arrays).
                        // A cloud-only update must not cause a redundant upload/version bump.
                        var unchanged = remote is not null &&
                            System.Text.Json.JsonSerializer.Serialize(merged) ==
                            System.Text.Json.JsonSerializer.Serialize(remote.Envelope.Records
                                .OrderBy(record => record.RecordType).ThenBy(record => record.Id).ToArray());
                        Diagnostics?.Update(d => d with { Stage = DriveDiagnosticStage.Upload,
                            UploadOutcome = unchanged ? "Skipped: merged document equals cloud" : "Attempting" });
                        ObservedCloudVersion = unchanged ? remote!.Version :
                            (await transport.UploadAsync(new SyncEnvelope(SyncSchema.CurrentVersion, merged),
                                remote?.Version ?? new DriveWriteCondition(null, null), token).ConfigureAwait(false)).Version;
                        if (!unchanged) Diagnostics?.Update(d => d with { UploadOutcome = "Succeeded",
                            UploadedCounts = DriveDiagnosticCounts.From(merged) });
                    }, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (DriveConcurrencyException) when (conflictAttempt < 2) { }
            }
            Diagnostics?.Update(d => d with { Stage = DriveDiagnosticStage.Complete, Outcome = "Succeeded",
                LastSuccessfulUtc = clock.GetUtcNow().UtcDateTime });
            status = new(SyncRunStatus.Succeeded, clock.GetUtcNow().UtcDateTime,
                duplicateCount == 0
                    ? "Synced with Google Drive."
                    : $"Synced with Google Drive; {duplicateCount} duplicate file(s) need review.");
        }
        catch (SyncPersistenceException error)
        {
            Diagnostics?.Failed(error);
            status = new(SyncRunStatus.Failed, null, "Sync failed while saving merged data to SQLite. Existing local data was kept.");
        }
        catch (SyncSchemaException error)
        {
            Diagnostics?.Failed(error);
            status = new(SyncRunStatus.Failed, null, "Sync data is from a newer app version.");
            throw;
        }
        catch (NotSupportedException error)
        {
            Diagnostics?.Failed(error);
            status = new(SyncRunStatus.Unavailable, null, "Google Drive synchronization is not configured.");
        }
        catch (Exception error) when (error is DriveConcurrencyException or DriveTransportException or IOException or TimeoutException
            or HttpRequestException or System.Text.Json.JsonException)
        {
            Diagnostics?.Failed(error);
            status = new(SyncRunStatus.Failed, null, "Sync could not be completed. Local changes were kept.");
        }
        catch (Exception error)
        {
            Diagnostics?.Failed(error);
            throw;
        }
        finally { singleFlight.Release(); }
    }

    public async Task CaptureDiagnosticLocalAsync(bool afterApply = false)
    {
        if (Diagnostics is null) return;
        try
        {
            var counts = DriveDiagnosticCounts.From(await repository.GetSyncRecordsAsync().ConfigureAwait(false));
            Diagnostics.Update(d => afterApply
                ? d with { SQLiteAfter = counts, SQLiteReadBackError = null, CurrentLocal = counts, CurrentLocalUtc = clock.GetUtcNow().UtcDateTime }
                : d with { CurrentLocal = counts, CurrentLocalUtc = clock.GetUtcNow().UtcDateTime });
        }
        catch (Exception error) { Diagnostics.Update(d => d with { SQLiteReadBackError = error.GetType().Name }); }
    }

    // Explicit diagnostic read-back only: no merge, upload, or Last Sync update.
    public async Task VerifyCloudForDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        if (Diagnostics is null) return;
        await singleFlight.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Diagnostics.VerifyingCloud = true;
            Diagnostics.Update(d => d with { VerificationOutcome = "Reading", VerifiedCloud = null, VerifiedFile = null, VerifiedUtc = null });
            var remote = await transport.DownloadAsync(cancellationToken).ConfigureAwait(false);
            Diagnostics.Update(d => d with { VerificationOutcome = remote is null ? "No matching file" : "Downloaded",
                VerifiedCloud = DriveDiagnosticCounts.From(remote?.Envelope.Records ?? []), VerifiedUtc = clock.GetUtcNow().UtcDateTime });
        }
        catch (Exception error) { Diagnostics.Update(d => d with { VerificationOutcome = "Failed: " + error.GetType().Name }); }
        finally { Diagnostics.VerifyingCloud = false; singleFlight.Release(); }
    }
}
