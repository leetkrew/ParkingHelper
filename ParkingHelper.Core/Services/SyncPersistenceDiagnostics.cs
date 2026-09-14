using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

public sealed class SyncPersistenceException(string reason)
    : InvalidOperationException("Synchronized records could not be persisted. Existing local data was kept.")
{
    // A controlled reason code, never a SQLite message or record payload.
    public string Reason { get; } = reason;
}

public sealed record SyncRecordIdentity(SyncRecordType Type, Guid Id);
public enum SyncApplyResult { Inserted, Updated, Skipped, Rejected }
public sealed record SyncApplyRecordResult(SyncRecordType Type, Guid Id, Guid? ReferencedPlateId,
    SyncApplyResult Result, string Reason, int? SQLiteCode = null);
public sealed record SyncPersistenceReport(
    string Transaction,
    IReadOnlyList<SyncApplyRecordResult> Records,
    IReadOnlyList<SyncRecordIdentity> ExpectedIds,
    IReadOnlyList<SyncRecordIdentity> ReadBackIds,
    IReadOnlyList<SyncRecordIdentity> MissingIds,
    IReadOnlyList<SyncRecordIdentity> MismatchedIds,
    IReadOnlyList<SyncRecordIdentity> UnexpectedIds,
    string? FailureReason = null);
