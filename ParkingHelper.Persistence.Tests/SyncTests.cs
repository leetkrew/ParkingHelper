using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using ParkingHelper.Persistence;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParkingHelper.Sync", Guid.NewGuid().ToString());
    private SqliteParkingRepository Repository => new(Path.Combine(directory, "parking.db3"));

    [Fact]
    public void MergeUsesTombstonesForEqualClockAndPreservesTicketSnapshots()
    {
        var id = Guid.NewGuid();
        var plateId = Guid.NewGuid();
        var now = new DateTime(2026, 9, 13, 4, 0, 0, DateTimeKind.Utc);
        var full = new ParkingTicket(id, plateId, "Pdf417", "barcode", ParkingTicketState.Active, now.AddMinutes(-1), now, null, null)
        {
            EntryUtc = now.AddMinutes(-1),
            PlateNumberSnapshot = "ABC-123",
            RawBarcodeData = [1, 2, 3]
        };
        var sparse = SyncRecord.ForTicket(full with { EntryUtc = default, PlateNumberSnapshot = null, RawBarcodeData = null, UpdatedUtc = now.AddMinutes(1) });
        var mergedTicket = Assert.Single(SyncMergeEngine.Merge([SyncRecord.ForTicket(full)], [sparse])).Ticket!;
        Assert.Equal(full.EntryUtc, mergedTicket.EntryUtc);
        Assert.Equal(full.PlateNumberSnapshot, mergedTicket.PlateNumberSnapshot);
        Assert.Equal(full.RawBarcodeData, mergedTicket.RawBarcodeData);

        var deleted = SyncRecord.ForPlateTombstone(plateId, now);
        var live = SyncRecord.ForPlate(new VehiclePlate(plateId, "ABC-123", now.AddMinutes(-1), now));
        Assert.True(Assert.Single(SyncMergeEngine.Merge([live], [deleted])).IsDeleted);
    }

    [Fact]
    public void NewerSchemaIsRejectedBeforeMerge()
    {
        var envelope = new SyncEnvelope(2, []);
        Assert.Throws<SyncSchemaException>(() => SyncSchema.Validate(envelope));
        var record = SyncRecord.ForPlate(new VehiclePlate(Guid.NewGuid(), "NEWER",
            DateTime.UtcNow, DateTime.UtcNow)) with { SchemaVersion = 2 };
        Assert.Throws<SyncSchemaException>(() => SyncSchema.Validate(new SyncEnvelope(1, [record])));
    }

    [Fact]
    public async Task DeletedPlateIsExportedAsTombstoneAndRemoteRecordCanBeApplied()
    {
        var repository = Repository;
        var created = new DateTime(2026, 9, 13, 5, 0, 0, DateTimeKind.Utc);
        var plate = new VehiclePlate(Guid.NewGuid(), "LOCAL", created, created);
        await repository.AddPlateAsync(plate);
        await repository.DeletePlateAsync(plate.Id, created.AddMinutes(1));
        var tombstone = Assert.Single(await repository.GetSyncRecordsAsync(), r => r.IsDeleted);
        Assert.Equal(plate.Id, tombstone.Id);

        var remote = new VehiclePlate(Guid.NewGuid(), "REMOTE", created, created);
        await repository.ApplySyncRecordsAsync([SyncRecord.ForPlate(remote)]);
        Assert.Equal("REMOTE", Assert.Single(await repository.GetPlatesAsync()).PlateNumber);
    }

    [Fact]
    public async Task OfflineAndDisconnectedRunsDoNotTouchDrive()
    {
        var transport = new StubTransport();
        var offline = new GoogleDriveSynchronizationService(Repository, new StubAuth(true),
            transport, new StubNetwork(false), new NoRetry());
        await offline.SynchronizeAsync();
        Assert.Equal(SyncRunStatus.Offline, offline.Status.State);
        Assert.Equal(0, transport.Downloads);

        var disconnected = new GoogleDriveSynchronizationService(Repository, new StubAuth(false),
            transport, new StubNetwork(true), new NoRetry());
        await disconnected.SynchronizeAsync();
        Assert.Equal(SyncRunStatus.Disconnected, disconnected.Status.State);
        Assert.Equal(0, transport.Downloads);
    }

    [Fact]
    public async Task ConcurrentRunsAreSingleFlight()
    {
        var transport = new StubTransport { DownloadGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var service = new GoogleDriveSynchronizationService(Repository, new StubAuth(true),
            transport, new StubNetwork(true), new NoRetry());
        var first = service.SynchronizeAsync();
        while (transport.Downloads == 0) await Task.Yield();
        var second = service.SynchronizeAsync();
        Assert.True(second.IsCompleted);
        transport.DownloadGate.SetResult();
        await first;
        Assert.Equal(1, transport.Downloads);
        Assert.Equal(0, transport.Uploads); // Identical empty cloud/local snapshots need no upload.
    }

    [Fact]
    public async Task DriveVersionConflictRedownloadsAndRetriesWithoutLosingLocalData()
    {
        var repository = Repository;
        var created = new DateTime(2026, 9, 13, 6, 0, 0, DateTimeKind.Utc);
        var plate = new VehiclePlate(Guid.NewGuid(), "LOCAL", created, created);
        await repository.AddPlateAsync(plate);
        var transport = new StubTransport { ConflictOnce = true };
        var service = new GoogleDriveSynchronizationService(repository, new StubAuth(true),
            transport, new StubNetwork(true), new NoRetry());
        await service.SynchronizeAsync();
        Assert.Equal(2, transport.Downloads);
        Assert.Equal(2, transport.Uploads);
        Assert.Equal("LOCAL", Assert.Single(await repository.GetPlatesAsync()).PlateNumber);
    }

    [Fact]
    public async Task DriveFailureLeavesLocalDataUntouched()
    {
        var repository = Repository;
        var created = new DateTime(2026, 9, 13, 7, 0, 0, DateTimeKind.Utc);
        var plate = new VehiclePlate(Guid.NewGuid(), "SAFE", created, created);
        await repository.AddPlateAsync(plate);
        var service = new GoogleDriveSynchronizationService(repository, new StubAuth(true),
            new StubTransport { FailDownload = true }, new StubNetwork(true), new NoRetry());
        await service.SynchronizeAsync();
        Assert.Equal(SyncRunStatus.Failed, service.Status.State);
        Assert.Equal("SAFE", Assert.Single(await repository.GetPlatesAsync()).PlateNumber);
    }

    private sealed class StubAuth(bool connected) : IGoogleDriveAuthentication
    {
        public bool IsConnected => connected;
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>("test");
        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(connected);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubNetwork(bool online) : ISyncNetworkStatus
    {
        public bool IsOnline => online;
    }

    private sealed class NoRetry : IConcurrencyRetryPolicy
    {
        public Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
    }

    private sealed class StubTransport : IGoogleDriveTransport
    {
        public int Downloads { get; private set; }
        public int Uploads { get; private set; }
        public bool ConflictOnce { get; set; }
        public bool FailDownload { get; set; }
        public TaskCompletionSource? DownloadGate { get; init; }

        public async Task<DriveSyncSnapshot?> DownloadAsync(CancellationToken cancellationToken = default)
        {
            Downloads++;
            if (FailDownload) throw new IOException("offline");
            if (Downloads == 1 && DownloadGate is not null)
                await DownloadGate.Task.WaitAsync(cancellationToken);
            return new DriveSyncSnapshot(new SyncEnvelope(1, []),
                new DriveWriteCondition("sync-file", "\"v1\""), []);
        }

        public Task<DriveUploadResult> UploadAsync(SyncEnvelope envelope, DriveWriteCondition condition,
            CancellationToken cancellationToken = default)
        {
            Uploads++;
            if (ConflictOnce)
            {
                ConflictOnce = false;
                throw new DriveConcurrencyException("changed");
            }
            return Task.FromResult(new DriveUploadResult(condition));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
