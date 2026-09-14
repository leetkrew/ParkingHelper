using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

public enum DriveDiagnosticStage { NotStarted, LocalRead, Download, ValidateCloud, Merge, ApplySQLite, Upload, Complete }
public sealed record DriveDiagnosticFile(string Id, string? Version, string? ModifiedUtc);
public sealed record DriveDiagnosticCounts(int Plates, int PlateTombstones, int Tickets, int Active, int Archived, int Deleted)
{
    public static DriveDiagnosticCounts From(IEnumerable<SyncRecord> records)
    {
        var values = records.ToArray();
        return new(values.Count(r => r.RecordType == SyncRecordType.VehiclePlate && !r.IsDeleted),
            values.Count(r => r.RecordType == SyncRecordType.VehiclePlate && r.IsDeleted),
            values.Count(r => r.RecordType == SyncRecordType.ParkingTicket && !r.IsDeleted),
            values.Count(r => r.RecordType == SyncRecordType.ParkingTicket && !r.IsDeleted && r.Ticket?.State == ParkingTicketState.Active),
            values.Count(r => r.RecordType == SyncRecordType.ParkingTicket && !r.IsDeleted && r.Ticket?.State == ParkingTicketState.Archived),
            values.Count(r => r.RecordType == SyncRecordType.ParkingTicket && r.IsDeleted));
    }
}

// In-memory support diagnostics allowlist. Never retain records, exception messages, URLs,
// authorization objects, or response bodies in this diagnostic state.
public sealed record DriveDiagnosticSnapshot
{
    public Guid RunId { get; init; }
    public int Attempt { get; init; }
    public DateTime? StartedUtc { get; init; }
    public DateTime? LastSuccessfulUtc { get; init; }
    public DriveDiagnosticStage Stage { get; init; }
    public string Outcome { get; init; } = "Not run";
    public string? ErrorType { get; init; }
    public int? HttpStatus { get; init; }
    public int? MatchingFiles { get; init; }
    public DriveDiagnosticFile? DownloadedFile { get; init; }
    public DriveDiagnosticFile? UploadedFile { get; init; }
    public DriveDiagnosticCounts? LocalBefore { get; init; }
    public DriveDiagnosticCounts? CloudDownloaded { get; init; }
    public DriveDiagnosticCounts? MergeOutput { get; init; }
    public DriveDiagnosticCounts? SQLiteAfter { get; init; }
    public SyncPersistenceReport? Persistence { get; init; }
    public string? SQLiteReadBackError { get; init; }
    public DriveDiagnosticCounts? UploadedCounts { get; init; }
    public string UploadOutcome { get; init; } = "Not attempted";
    public DriveDiagnosticCounts? CurrentLocal { get; init; }
    public DateTime? CurrentLocalUtc { get; init; }
    public int? VerifiedMatchingFiles { get; init; }
    public string? VerificationOutcome { get; init; }
    public DriveDiagnosticFile? VerifiedFile { get; init; }
    public DriveDiagnosticCounts? VerifiedCloud { get; init; }
    public DateTime? VerifiedUtc { get; init; }
    public int? ActiveListCount { get; init; }
    public DateTime? ActiveListLoadedUtc { get; init; }
    public int? ArchivedListCount { get; init; }
    public DateTime? ArchivedListLoadedUtc { get; init; }
    public string? ListError { get; init; }
}

public sealed class GoogleDriveSyncDiagnostics
{
    private readonly object gate = new();
    private DriveDiagnosticSnapshot snapshot = new();
    public DriveDiagnosticSnapshot Snapshot { get { lock (gate) return snapshot; } }
    public event Action? Changed;
    // Set only inside the service's single-flight diagnostic verification operation.
    public bool VerifyingCloud { get; set; }

    public void Update(Func<DriveDiagnosticSnapshot, DriveDiagnosticSnapshot> update)
    {
        lock (gate) snapshot = update(snapshot);
        if (Changed is not { } changed) return;
        foreach (Action listener in changed.GetInvocationList())
            try { listener(); } catch { /* Observability must not break a local write or sync. */ }
    }

    public void Begin(DateTime utc) => Update(previous => new()
    {
        RunId = Guid.NewGuid(), StartedUtc = utc, Outcome = "Running", Stage = DriveDiagnosticStage.LocalRead,
        LastSuccessfulUtc = previous.LastSuccessfulUtc,
        ActiveListCount = previous.ActiveListCount, ActiveListLoadedUtc = previous.ActiveListLoadedUtc,
        ArchivedListCount = previous.ArchivedListCount, ArchivedListLoadedUtc = previous.ArchivedListLoadedUtc
    });

    public void Failed(Exception error) => Update(value => value with
    {
        Outcome = error is OperationCanceledException ? "Canceled" : "Failed",
        ErrorType = error.GetType().Name,
        HttpStatus = error is HttpRequestException http ? (int?)http.StatusCode : null
    });

    public void ListLoaded(bool archived, int count, DateTime utc) => Update(value => archived
        ? value with { ArchivedListCount = count, ArchivedListLoadedUtc = utc, ListError = null }
        : value with { ActiveListCount = count, ActiveListLoadedUtc = utc, ListError = null });
}
