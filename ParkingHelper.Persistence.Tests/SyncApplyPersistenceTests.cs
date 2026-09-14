using Microsoft.Data.Sqlite;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class SyncApplyPersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SyncApplyPersistence", Guid.NewGuid().ToString());
    private string DatabasePath => Path.Combine(directory, "parking.db3");
    private SqliteParkingRepository Repository => new(DatabasePath);
    private static readonly DateTime Now = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}");
    private static VehiclePlate Plate(int id, string number, int order = 0) => new(Id(id), number, Now, Now) { SortOrder = order };
    private static ParkingTicket Ticket(int id, Guid plateId, ParkingTicketState state = ParkingTicketState.Active) =>
        new(Id(id), plateId, "QR_CODE", "PRIVATE-BARCODE", state, Now, Now.AddHours(1),
            state == ParkingTicketState.Archived ? Now.AddMinutes(30) : null,
            state == ParkingTicketState.Deleted ? Now.AddMinutes(40) : null)
        { PlateNumberSnapshot = "PRIVATE-SNAPSHOT", RawBarcodeData = [1, 2, 3] };

    [Fact]
    public async Task CloudPlatesConvergeAndTicketsPersistOrBecomeCompactTombstones()
    {
        var repository = Repository;
        await repository.AddPlateAsync(Plate(1, "PRIVATE-A"));
        await repository.AddPlateAsync(Plate(2, "PRIVATE-B", 1));
        var cloudPlates = new[] { Plate(1, "PRIVATE-A"), Plate(3, "PRIVATE-A"), Plate(4, "PRIVATE-A"), Plate(5, "PRIVATE-B") };
        var cloudTickets = new[] { Ticket(11, Id(3)), Ticket(12, Id(4), ParkingTicketState.Archived), Ticket(13, Id(5), ParkingTicketState.Deleted) };
        var cloud = new SyncEnvelope(1, [.. cloudPlates.Select(SyncRecord.ForPlate), .. cloudTickets.Select(SyncRecord.ForTicket)]);
        var transport = new Transport { Cloud = cloud };
        var service = new GoogleDriveSynchronizationService(repository, new Auth(), transport, new Network(), new ConcurrencyRetryPolicy());
        Assert.Equal(4, cloud.Records.Count(r => r.RecordType == SyncRecordType.VehiclePlate));
        var merged = SyncMergeEngine.Merge(await repository.GetSyncRecordsAsync(), cloud.Records);
        Assert.Equal(5, merged.Count(r => r.RecordType == SyncRecordType.VehiclePlate));
        await service.SynchronizeAsync();
        var stored = await new SqliteParkingRepository(DatabasePath).GetSyncRecordsAsync();
        Assert.Equal(5, stored.Count(r => r.RecordType == SyncRecordType.VehiclePlate));
        Assert.Equal(3, stored.Count(r => r.RecordType == SyncRecordType.ParkingTicket));
        Assert.Equal(merged.Select(Key).Order(), stored.Select(Key).Order());
        foreach (var incoming in cloudTickets)
        {
            var saved = await repository.GetTicketAsync(incoming.Id);
            if (incoming.State == ParkingTicketState.Deleted)
            {
                Assert.Null(saved);
                Assert.True(stored.Single(r => r.Id == incoming.Id).IsDeleted);
                continue;
            }
            Assert.NotNull(saved);
            Assert.Equal(Id(1), saved.VehiclePlateId);
            Assert.Equal(incoming.State, saved.State);
            Assert.Equal(incoming.RawBarcodeData, saved.RawBarcodeData);
        }
        Assert.Single(await repository.GetTicketsAsync(ParkingTicketState.Active));
        Assert.Single(await repository.GetTicketsAsync(ParkingTicketState.Archived));
        Assert.Empty(await repository.GetTicketsAsync(ParkingTicketState.Deleted));
        Assert.Equal(2, (await repository.GetPlatesAsync()).Count);
        Assert.Equal(SyncRunStatus.Succeeded, service.Status.State);
    }

    [Fact]
    public async Task SyncedDuplicatesPreserveIdsAndLocalDuplicateProtectionRemainsEnabled()
    {
        var diagnostics = new GoogleDriveSyncDiagnostics();
        var repository = new SqliteParkingRepository(DatabasePath) { Diagnostics = diagnostics };
        SyncRecord[] records = [SyncRecord.ForPlate(Plate(1, "PRIVATE-A")), SyncRecord.ForPlate(Plate(2, "PRIVATE-A")),
            SyncRecord.ForTicket(Ticket(11, Id(1))), SyncRecord.ForTicket(Ticket(12, Id(2)))];
        await repository.ApplySyncRecordsAsync(records);
        Assert.Contains(diagnostics.Snapshot.Persistence!.Records, r => r.Reason == "PlateTombstonePreserved");
        Assert.Equal(4, diagnostics.Snapshot.Persistence.ReadBackIds.Count);
        Assert.Empty(diagnostics.Snapshot.Persistence.MissingIds);
        await repository.ApplySyncRecordsAsync(records);
        Assert.All(diagnostics.Snapshot.Persistence!.Records, r => Assert.Equal(SyncApplyResult.Skipped, r.Result));
        await Assert.ThrowsAsync<PlateOperationException>(() => repository.AddPlateAsync(Plate(3, "PRIVATE-A")));
        Assert.Equal(2, (await repository.GetTicketsAsync(ParkingTicketState.Active)).Count);
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM SyncApplyContext;"));
        Assert.DoesNotContain("PRIVATE", System.Text.Json.JsonSerializer.Serialize(diagnostics.Snapshot));
    }

    [Fact]
    public async Task MissingParentRejectsTicketAndRollsBackEarlierPlateWrite()
    {
        var diagnostics = new GoogleDriveSyncDiagnostics();
        var repository = new SqliteParkingRepository(DatabasePath) { Diagnostics = diagnostics };
        await repository.AddPlateAsync(Plate(1, "PRIVATE-A"));
        var error = await Assert.ThrowsAsync<SyncPersistenceException>(() => repository.ApplySyncRecordsAsync(
            [SyncRecord.ForPlate(Plate(2, "PRIVATE-B")), SyncRecord.ForTicket(Ticket(11, Id(99)))]));
        Assert.Equal("MissingReferencedPlateGuid", error.Reason);
        Assert.Equal(Id(1), Assert.Single(await Repository.GetSyncRecordsAsync()).Id);
        var report = diagnostics.Snapshot.Persistence!;
        Assert.Equal("RolledBack", report.Transaction);
        Assert.Contains(report.Records, r => r.Id == Id(11) && r.Result == SyncApplyResult.Rejected && r.ReferencedPlateId == Id(99));
        Assert.Contains(report.MissingIds, r => r.Id == Id(2));
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM SyncApplyContext;"));
    }

    [Theory]
    [InlineData("SELECT RAISE(ABORT, 'PRIVATE-PAYLOAD');", "SQLiteConstraintRejected")]
    [InlineData("SELECT RAISE(IGNORE);", "PlateWriteIgnored")]
    [InlineData("UPDATE VehiclePlates SET SortOrder = SortOrder + 1 WHERE Id = NEW.Id;", "ReadBackIdentityOrStateMismatch")]
    public async Task RejectedIgnoredOrCorruptedWriteRollsBackWholeApply(string triggerBody, string reason)
    {
        var diagnostics = new GoogleDriveSyncDiagnostics();
        var repository = new SqliteParkingRepository(DatabasePath) { Diagnostics = diagnostics };
        await repository.AddPlateAsync(Plate(1, "PRIVATE-A"));
        var timing = reason == "ReadBackIdentityOrStateMismatch" ? "AFTER" : "BEFORE";
        await Sql($"CREATE TRIGGER InjectFailure {timing} INSERT ON VehiclePlates WHEN NEW.Id = '{Id(3)}' BEGIN {triggerBody} END;");
        var error = await Assert.ThrowsAsync<SyncPersistenceException>(() => repository.ApplySyncRecordsAsync(
            [SyncRecord.ForPlate(Plate(2, "PRIVATE-B")), SyncRecord.ForPlate(Plate(3, "PRIVATE-C"))]));
        Assert.Equal(reason, error.Reason);
        Assert.Equal(Id(1), Assert.Single(await Repository.GetSyncRecordsAsync()).Id);
        Assert.Equal("RolledBack", diagnostics.Snapshot.Persistence!.Transaction);
        Assert.Contains(diagnostics.Snapshot.Persistence.Records, r => r.Id == Id(3) && r.Result == SyncApplyResult.Rejected);
        Assert.DoesNotContain("PRIVATE", error.ToString());
        Assert.DoesNotContain("PRIVATE", System.Text.Json.JsonSerializer.Serialize(diagnostics.Snapshot));
    }

    [Theory]
    [InlineData(false, ParkingTicketState.Active)]
    [InlineData(true, ParkingTicketState.Active)]
    [InlineData(false, ParkingTicketState.Deleted)]
    [InlineData(true, ParkingTicketState.Deleted)]
    public async Task TombstonedPlateRetainsExactTicketRelationshipWithoutExportingLivePlate(bool existing, ParkingTicketState state)
    {
        var repository = Repository;
        var ticket = Ticket(11, Id(1), state);
        if (existing) await repository.ApplySyncRecordsAsync([SyncRecord.ForPlate(Plate(1, "PRIVATE-A")), SyncRecord.ForTicket(ticket)]);
        SyncRecord[] records = [SyncRecord.ForPlateTombstone(Id(1), Now.AddHours(2)), SyncRecord.ForTicket(ticket)];
        await repository.ApplySyncRecordsAsync(records);
        await repository.ApplySyncRecordsAsync(records);
        var stored = await Repository.GetSyncRecordsAsync();
        Assert.Equal(2, stored.Count);
        Assert.True(Assert.Single(stored, r => r.RecordType == SyncRecordType.VehiclePlate).IsDeleted);
        if (state == ParkingTicketState.Deleted)
        {
            Assert.Null(await Repository.GetTicketAsync(Id(11)));
            Assert.Equal(Guid.Empty, stored.Single(r => r.RecordType == SyncRecordType.ParkingTicket).Ticket!.VehiclePlateId);
        }
        else Assert.Equal(Id(1), (await Repository.GetTicketAsync(Id(11)))!.VehiclePlateId);
        Assert.Empty(await Repository.GetPlatesAsync());
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;"));
    }

    [Fact]
    public async Task UpdatesPersistOrderAndKeepNewerLocalState()
    {
        var repository = Repository;
        var original = Plate(1, "PRIVATE-A");
        await repository.ApplySyncRecordsAsync([SyncRecord.ForPlate(original)]);
        var updated = original with { SortOrder = 7, UpdatedUtc = Now.AddHours(2) };
        await repository.ApplySyncRecordsAsync([SyncRecord.ForPlate(updated)]);
        await repository.ApplySyncRecordsAsync([SyncRecord.ForPlate(original)]);
        Assert.Equal(updated, Assert.Single(await Repository.GetPlatesAsync()));
    }

    [Fact]
    public async Task FailedPersistencePreventsUploadAndDoesNotAdvanceSuccessfulTimestamp()
    {
        var diagnostics = new GoogleDriveSyncDiagnostics();
        var repository = new SqliteParkingRepository(DatabasePath) { Diagnostics = diagnostics };
        await repository.AddPlateAsync(Plate(1, "PRIVATE-A"));
        var transport = new Transport();
        var service = new GoogleDriveSynchronizationService(repository, new Auth(), transport, new Network(), new ConcurrencyRetryPolicy())
            { Diagnostics = diagnostics };
        await service.SynchronizeAsync();
        Assert.Equal(SyncRunStatus.Succeeded, service.Status.State);
        var lastSuccess = diagnostics.Snapshot.LastSuccessfulUtc;
        Assert.NotNull(lastSuccess);
        var uploads = transport.Uploads;
        transport.Cloud = new(1, [SyncRecord.ForPlate(Plate(2, "PRIVATE-B")), SyncRecord.ForTicket(Ticket(11, Id(99)))]);
        await service.SynchronizeAsync();
        Assert.Equal(SyncRunStatus.Failed, service.Status.State);
        Assert.Contains("SQLite", service.Status.Message);
        Assert.Equal(lastSuccess, diagnostics.Snapshot.LastSuccessfulUtc);
        Assert.Equal(DriveDiagnosticStage.ApplySQLite, diagnostics.Snapshot.Stage);
        Assert.Equal(uploads, transport.Uploads);
        Assert.Equal(Id(1), Assert.Single(await Repository.GetSyncRecordsAsync()).Id);
    }

    [Fact]
    public async Task VersionFourMigrationKeepsExistingGuidsAndAllowsSyncedDuplicatePlate()
    {
        var repository = Repository;
        await repository.ApplySyncRecordsAsync([SyncRecord.ForPlate(Plate(1, "PRIVATE-A")), SyncRecord.ForTicket(Ticket(11, Id(1)))]);
        await Sql("""
            DROP TRIGGER TR_VehiclePlates_Duplicate_Insert;
            DROP TRIGGER TR_VehiclePlates_Duplicate_Update;
            DROP TRIGGER TR_ParkingTickets_ActiveDuplicate_Insert;
            DROP TRIGGER TR_ParkingTickets_ActiveDuplicate_Update;
            DROP TABLE SyncApplyContext;
            DROP INDEX IX_VehiclePlates_PlateNumber;
            CREATE UNIQUE INDEX IX_VehiclePlates_PlateNumber ON VehiclePlates(PlateNumber);
            PRAGMA user_version = 4;
            """);
        var upgraded = Repository;
        Assert.Equal(Id(1), (await upgraded.GetTicketAsync(Id(11)))!.VehiclePlateId);
        Assert.Equal(6L, await Scalar("PRAGMA user_version;"));
        await upgraded.ApplySyncRecordsAsync([SyncRecord.ForPlate(Plate(2, "PRIVATE-A")), SyncRecord.ForTicket(Ticket(12, Id(2)))]);
        Assert.Equal(4, (await Repository.GetSyncRecordsAsync()).Count);
        Assert.Equal(0L, await Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        await Assert.ThrowsAsync<PlateOperationException>(() => upgraded.AddPlateAsync(Plate(3, "PRIVATE-A")));
    }

    [Fact]
    public async Task ExistingActiveTicketUpdatesInPlaceIncludingPlateGuidAndEntryTime()
    {
        var repository = Repository;
        var ticket = Ticket(11, Id(1));
        await repository.ApplySyncRecordsAsync([SyncRecord.ForPlate(Plate(1, "PRIVATE-A")), SyncRecord.ForPlate(Plate(2, "PRIVATE-B")), SyncRecord.ForTicket(ticket)]);
        var updated = ticket with { VehiclePlateId = Id(2), UpdatedUtc = Now.AddHours(3), EntryUtc = Now.AddMinutes(-10) };
        await repository.ApplySyncRecordsAsync([SyncRecord.ForTicket(updated)]);
        var saved = (await Repository.GetTicketAsync(Id(11)))!;
        Assert.Equal(updated.VehiclePlateId, saved.VehiclePlateId);
        Assert.Equal(updated.EntryUtc, saved.EntryUtc);
        Assert.Equal(updated.UpdatedUtc, saved.UpdatedUtc);
        Assert.Single(await Repository.GetTicketsAsync(ParkingTicketState.Active));
    }

    [Fact]
    public async Task TicketConstraintFailureRollsBackEarlierPlateUpdate()
    {
        var diagnostics = new GoogleDriveSyncDiagnostics();
        var repository = new SqliteParkingRepository(DatabasePath) { Diagnostics = diagnostics };
        var original = Plate(1, "PRIVATE-A");
        await repository.AddPlateAsync(original);
        var invalid = Ticket(11, Id(1)) with { ArchivedUtc = Now.AddMinutes(1) };
        var error = await Assert.ThrowsAsync<SyncPersistenceException>(() => repository.ApplySyncRecordsAsync(
            [SyncRecord.ForPlate(original with { SortOrder = 8, UpdatedUtc = Now.AddHours(3) }), SyncRecord.ForTicket(invalid)]));
        Assert.Equal("SQLiteConstraintRejected", error.Reason);
        Assert.Equal(original, Assert.Single(await Repository.GetPlatesAsync()));
        Assert.Null(await Repository.GetTicketAsync(Id(11)));
        Assert.Contains(diagnostics.Snapshot.Persistence!.Records, r => r.Id == Id(11) && r.Result == SyncApplyResult.Rejected && r.SQLiteCode == 275);
    }

    private async Task Sql(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private async Task<long> Scalar(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static string Key(SyncRecord record) => $"{record.RecordType}:{record.Id}";
    private sealed class Auth : IGoogleDriveAuthentication
    {
        public bool IsConnected => true;
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>("unused");
        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Network : ISyncNetworkStatus { public bool IsOnline => true; }
    private sealed class Transport : IGoogleDriveTransport
    {
        public SyncEnvelope? Cloud;
        public int Uploads;
        public Task<DriveSyncSnapshot?> DownloadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Cloud is null ? null : new DriveSyncSnapshot(Cloud, new("file", "1"), []));
        public Task<DriveUploadResult> UploadAsync(SyncEnvelope envelope, DriveWriteCondition condition, CancellationToken cancellationToken = default)
        { Uploads++; Cloud = envelope; return Task.FromResult(new DriveUploadResult(new("file", "2"))); }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
