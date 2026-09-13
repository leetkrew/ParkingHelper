using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ParkingHelper.App.Services;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class SynchronizationPolicyTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "SyncPolicy", Guid.NewGuid().ToString());
    private readonly ManualClock clock = new();
    private readonly Auth auth = new();
    private readonly Network network = new();
    private readonly Transport transport = new();
    private readonly SqliteParkingRepository repository;
    private readonly GoogleDriveConnection connection;
    private readonly SynchronizationTrigger coordinator;

    public SynchronizationPolicyTests()
    {
        repository = new(Path.Combine(directory, "parking.db3"));
        connection = new(auth, new GoogleDriveSynchronizationService(repository, auth, transport, network,
            new ConcurrencyRetryPolicy(delay: TimeSpan.Zero), clock));
        coordinator = new(connection, network, clock);
    }

    private async Task Start()
    {
        coordinator.RequestResumeSync();
        await Idle();
        await connection.ConnectAsync();
        Assert.Equal(1, transport.Downloads);
        Assert.Equal(1, transport.Uploads);
    }

    [Fact]
    public async Task ConnectAndResumeSyncImmediatelyWithoutUnnecessaryUpload()
    {
        await Start();
        coordinator.EnterBackground();
        coordinator.RequestResumeSync();
        await Idle();
        Assert.Equal(2, transport.Downloads);
        Assert.Equal(1, transport.Uploads);
        Assert.NotNull(connection.LastSuccessfulSync);
        Assert.Equal(1, clock.ActiveTimers);
    }

    [Fact]
    public async Task ThreeSecondDebounceCollapsesRapidSuccessfulSqliteWrites()
    {
        await Start();
        var plates = new PlateService(repository, clock, coordinator);
        await plates.AddAsync("ONE");
        clock.Advance(2);
        Assert.Equal(1, transport.Downloads);
        await plates.AddAsync("TWO");
        await plates.AddAsync("THREE");
        Assert.Equal(3, (await repository.GetPlatesAsync()).Count);
        clock.Advance(2.99);
        Assert.Equal(1, transport.Downloads);
        clock.Advance(.01);
        await Idle();
        Assert.Equal(2, transport.Downloads);
        Assert.Equal(3, transport.Cloud!.Records.Count);
        Assert.False(connection.NeedsSynchronization);
    }

    [Fact]
    public async Task MutationsDuringFlightHaveOneBoundedFollowUpAndNeverOverlap()
    {
        await Start();
        var first = transport.BlockNextDownload();
        var second = transport.BlockNextDownload();
        var running = connection.SyncAsync();
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var plates = new PlateService(repository, clock, coordinator);
        for (var i = 0; i < 8; i++) await plates.AddAsync($"A{i}");
        clock.Advance(3);
        var joiners = Enumerable.Range(0, 30).Select(_ => connection.SyncAsync()).ToArray();
        Assert.All(joiners, task => Assert.Same(running, task));
        first.Release.TrySetResult();
        await second.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await plates.AddAsync("DURINGFOLLOWUP");
        clock.Advance(3);
        second.Release.TrySetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, transport.Downloads); // Initial connect + current + one follow-up.
        Assert.True(connection.NeedsSynchronization);
        Assert.Equal(9, (await repository.GetPlatesAsync()).Count);
        Assert.Equal(1, transport.MaximumConcurrentRequests);
        clock.Advance(60);
        await Idle();
        Assert.False(connection.NeedsSynchronization);
        Assert.Equal(9, transport.Cloud!.Records.Count);
    }

    [Fact]
    public async Task UnchangedMetadataDoesNotDownloadUploadOrUpdateLastSynced()
    {
        await Start();
        var last = connection.LastSuccessfulSync;
        clock.Advance(59.99);
        Assert.Equal(0, transport.Checks);
        clock.Advance(.01);
        await Idle();
        Assert.Equal(1, transport.Checks);
        Assert.Equal(1, transport.Downloads);
        Assert.Equal(1, transport.Uploads);
        Assert.Equal(last, connection.LastSuccessfulSync);
    }

    [Fact]
    public async Task ChangedVersionDownloadsAndMergesWithoutUploadingCloudOnlyChange()
    {
        await Start();
        var now = clock.GetUtcNow().UtcDateTime;
        var remote = new VehiclePlate(Guid.NewGuid(), "REMOTE", now, now);
        transport.Cloud = new(1, [SyncRecord.ForPlate(remote)]);
        transport.Version++;
        clock.Advance(60);
        await Idle();
        Assert.Equal(1, transport.Checks);
        Assert.Equal(2, transport.Downloads);
        Assert.Equal(1, transport.Uploads);
        Assert.Equal(remote.Id, Assert.Single(await repository.GetPlatesAsync()).Id);
        Assert.Equal(clock.GetUtcNow().UtcDateTime, connection.LastSuccessfulSync);
        await Eventually(() => clock.ActiveTimers == 1);
        clock.Advance(60);
        await Idle();
        Assert.Equal(2, transport.Checks);
        Assert.Equal(2, transport.Downloads);
    }

    [Fact]
    public async Task PendingLocalChangesUseFullSyncInsteadOfMetadata()
    {
        await Start();
        network.IsOnline = false;
        await new PlateService(repository, clock, coordinator).AddAsync("OFFLINE");
        clock.Advance(3);
        Assert.True(connection.NeedsSynchronization);
        network.IsOnline = true;
        clock.Advance(57);
        await Idle();
        Assert.Equal(0, transport.Checks);
        Assert.Equal(2, transport.Downloads);
        Assert.Equal("OFFLINE", Assert.Single(transport.Cloud!.Records).Plate?.PlateNumber);
    }

    [Fact]
    public async Task BackgroundAndRepeatedResumesKeepOnlyOneForegroundTimer()
    {
        await Start();
        var block = transport.BlockNextDownload();
        for (var i = 0; i < 20; i++) coordinator.RequestResumeSync();
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, clock.ActiveTimers);
        block.Release.TrySetResult();
        await Idle();
        Assert.Equal(2, transport.Downloads);
        coordinator.EnterBackground();
        Assert.Equal(0, clock.ActiveTimers);
        clock.Advance(180);
        coordinator.RequestNetworkSync();
        Assert.Equal(0, transport.Checks);
        Assert.Equal(2, transport.Downloads);
        coordinator.RequestResumeSync();
        await Idle();
        Assert.Equal(3, transport.Downloads);
        Assert.Equal(1, clock.ActiveTimers);
        clock.Advance(60);
        await Idle();
        Assert.Equal(1, transport.Checks);
    }

    [Fact]
    public async Task DisconnectCancelsDebounceAndChecksAndReconnectRestartsThem()
    {
        await Start();
        await new PlateService(repository, clock, coordinator).AddAsync("LOCAL");
        await connection.DisconnectAsync();
        clock.Advance(180);
        Assert.Equal(1, transport.Downloads);
        Assert.Equal(0, transport.Checks);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Single(await repository.GetPlatesAsync());
        await connection.ConnectAsync();
        Assert.Equal(2, transport.Downloads);
        Assert.Equal(1, clock.ActiveTimers);
        coordinator.Dispose();
        clock.Advance(180);
        Assert.True(auth.IsConnected);
        Assert.Equal(1, auth.Disconnects); // Disposal/backgrounding are not logout.
        Assert.Equal(0, clock.ActiveTimers);
    }

    [Fact]
    public async Task AutomaticFailureKeepsLocalDataPendingAndLaterTriggerRetries()
    {
        await Start();
        var last = connection.LastSuccessfulSync;
        transport.Fail = true;
        await new PlateService(repository, clock, coordinator).AddAsync("SAFE");
        clock.Advance(3);
        await Idle();
        Assert.Equal("SAFE", Assert.Single(await repository.GetPlatesAsync()).PlateNumber);
        Assert.True(connection.NeedsSynchronization);
        Assert.True(auth.IsConnected);
        Assert.Equal(last, connection.LastSuccessfulSync);
        transport.Fail = false;
        coordinator.RequestNetworkSync();
        await Idle();
        Assert.False(connection.NeedsSynchronization);
        Assert.Equal("SAFE", Assert.Single(transport.Cloud!.Records).Plate?.PlateNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IntentionalPullRefreshMergesThenReloadsCurrentSqliteList(bool archived)
    {
        await Start();
        var model = new TicketsViewModel(new TicketService(repository, clock), clock,
            NullLogger<TicketsViewModel>.Instance, connection);
        await model.LoadAsync(archived);
        var now = clock.GetUtcNow().UtcDateTime;
        var plate = new VehiclePlate(Guid.NewGuid(), "CLOUD", now, now);
        var ticket = new ParkingTicket(Guid.NewGuid(), plate.Id, "QR_CODE", "TICKET",
            archived ? ParkingTicketState.Archived : ParkingTicketState.Active, now, now,
            archived ? now : null, null) { PlateNumberSnapshot = plate.PlateNumber };
        transport.Cloud = new(1, [SyncRecord.ForPlate(plate), SyncRecord.ForTicket(ticket)]);
        transport.Version++;
        var block = transport.BlockNextDownload();
        var refreshing = model.RefreshAsync();
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(model.IsRefreshing);
        Assert.Empty(model.Items);
        block.Release.TrySetResult();
        await refreshing;
        Assert.Equal(ticket.Id, Assert.Single(model.Items).Id);
        Assert.Equal(archived, model.IsArchived);
        Assert.False(model.IsRefreshing);
        Assert.Equal(2, transport.Downloads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisconnectedPullReloadsSqliteOnlyAndOrdinaryListActivityNeverSyncs(bool archived)
    {
        var plate = await new PlateService(repository, clock).AddAsync("LOCAL");
        var tickets = new TicketService(repository, clock);
        var ticket = (await tickets.CreateActiveTicketAsync(new("CODE", "QR_CODE", null, clock.GetUtcNow()), plate)).Ticket;
        if (archived) await tickets.ArchiveTicketAsync(ticket.Id);
        var model = new TicketsViewModel(tickets, clock, NullLogger<TicketsViewModel>.Instance, connection);
        await model.LoadAsync(archived);
        model.UpdateDurations();
        Assert.Equal(0, transport.Downloads);
        await model.RefreshAsync();
        Assert.Equal(ticket.Id, Assert.Single(model.Items).Id);
        Assert.False(model.IsRefreshing);
        Assert.Equal(0, transport.Downloads);
    }

    [Fact]
    public async Task PullRefreshFailureAlwaysEndsIndicatorAndShowsLocalTickets()
    {
        await Start();
        var plate = await new PlateService(repository, clock).AddAsync("SAFE");
        var tickets = new TicketService(repository, clock);
        var ticket = (await tickets.CreateActiveTicketAsync(new("CODE", "QR_CODE", null, clock.GetUtcNow()), plate)).Ticket;
        transport.Fail = true;
        var model = new TicketsViewModel(tickets, clock, NullLogger<TicketsViewModel>.Instance, connection);
        await model.RefreshAsync();
        Assert.False(model.IsRefreshing);
        Assert.Equal(ticket.Id, Assert.Single(model.Items).Id);
        Assert.True(auth.IsConnected);
        Assert.True(connection.NeedsSynchronization);
    }

    [Fact]
    public async Task OrdinaryConnectedListLoadsAndDurationUpdatesDoNotSynchronize()
    {
        await Start();
        var model = new TicketsViewModel(new TicketService(repository, clock), clock,
            NullLogger<TicketsViewModel>.Instance, connection);
        await model.LoadAsync(false);
        model.UpdateDurations();
        await model.LoadAsync(true);
        Assert.Equal(1, transport.Downloads);
        Assert.False(model.IsRefreshing);
    }

    [Fact]
    public void NativeRefreshGestureIsWiredWithoutScrollOrPanSyncHandlers()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "ParkingHelper.App"))) root = root.Parent;
        Assert.NotNull(root);
        var page = System.Xml.Linq.XDocument.Load(Path.Combine(root.FullName, "ParkingHelper.App", "Pages", "TicketsPage.xaml"));
        var refresh = Assert.Single(page.Descendants(), element => element.Name.LocalName == "RefreshView");
        Assert.Equal("OnRefreshing", refresh.Attribute("Refreshing")?.Value);
        Assert.Equal("CollectionView", Assert.Single(refresh.Elements()).Name.LocalName);
        Assert.DoesNotContain(page.Descendants().Attributes(), attribute => attribute.Name.LocalName is "Scrolled" or "PanUpdated");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BackgroundOrDisconnectCancelsRunningMetadataRequest(bool disconnect)
    {
        await Start();
        var block = transport.BlockNextCheck();
        clock.Advance(60);
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (disconnect) await connection.DisconnectAsync();
        else coordinator.EnterBackground();
        await Idle();
        clock.Advance(180);
        Assert.Equal(1, transport.Checks);
        Assert.Equal(1, transport.Downloads);
        Assert.Equal(0, clock.ActiveTimers);
        Assert.Equal(!disconnect, auth.IsConnected);
        Assert.Equal(1, transport.MaximumConcurrentRequests);
    }

    [Fact]
    public async Task ExplicitSyncDuringMetadataCheckJoinsThenRunsOneFullSync()
    {
        await Start();
        var block = transport.BlockNextCheck();
        clock.Advance(60);
        await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var requested = connection.SyncAsync();
        for (var i = 0; i < 10; i++) Assert.Same(requested, connection.SyncAsync());
        Assert.Equal(1, transport.Downloads);
        block.Release.TrySetResult();
        await requested.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, transport.Downloads);
        Assert.Equal(1, transport.MaximumConcurrentRequests);
    }

    [Fact]
    public async Task FailedSqliteWriteDoesNotMarkCloudSyncPending()
    {
        await Start();
        var plates = new PlateService(repository, clock, coordinator);
        using var database = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(directory, "parking.db3")}");
        database.Open();
        using var command = database.CreateCommand();
        command.CommandText = "CREATE TRIGGER RejectPlateInsert BEFORE INSERT ON VehiclePlates BEGIN SELECT RAISE(ABORT, 'disk failure'); END;";
        command.ExecuteNonQuery();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => plates.AddAsync("VALID"));
        Assert.False(connection.NeedsSynchronization);
        clock.Advance(3);
        Assert.Equal(1, transport.Downloads);
        Assert.Empty(await repository.GetPlatesAsync());
    }

    [Fact]
    public async Task TicketAndPlateMutationHooksCommitLocallyThenShareOneDebounce()
    {
        await Start();
        var plates = new PlateService(repository, clock, coordinator);
        var tickets = new TicketService(repository, clock, coordinator);
        var first = await plates.AddAsync("FIRST");
        var second = await plates.AddAsync("SECOND");
        clock.Advance(.1);
        await plates.UpdateAsync(first.Id, "EDITED");
        clock.Advance(.1);
        await plates.MoveAsync(second.Id, -1);
        var ticket = (await tickets.CreateActiveTicketAsync(new("SCAN", "QR_CODE", [1, 2, 3], clock.GetUtcNow()), first)).Ticket;
        clock.Advance(.1);
        await tickets.ChangeTicketPlateAsync(ticket.Id, second.Id);
        clock.Advance(.1);
        await tickets.EditTicketAsync(ticket.Id, first.Id, clock.GetUtcNow().UtcDateTime.AddMinutes(-5));
        clock.Advance(.1);
        await tickets.ArchiveTicketAsync(ticket.Id);
        clock.Advance(.1);
        await tickets.RestoreTicketAsync(ticket.Id);
        clock.Advance(.1);
        await tickets.DeleteTicketAsync(ticket.Id);
        clock.Advance(.1);
        await plates.DeleteAsync(second.Id);
        Assert.True(connection.NeedsSynchronization);
        Assert.Equal(1, transport.Downloads);
        Assert.Empty(await tickets.GetActiveTicketsAsync());
        Assert.Empty(await tickets.GetArchivedTicketsAsync());
        Assert.Equal("EDITED", Assert.Single(await plates.GetPlatesAsync()).PlateNumber);
        clock.Advance(3);
        await Idle();
        Assert.Equal(2, transport.Downloads);
        Assert.Contains(transport.Cloud!.Records, record => record.Id == ticket.Id && record.IsDeleted);
        Assert.Contains(transport.Cloud.Records, record => record.Id == second.Id && record.IsDeleted);
        Assert.False(connection.NeedsSynchronization);
    }

    private Task Idle() => Eventually(() => !connection.IsBusy);
    private static async Task Eventually(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }

    public void Dispose()
    {
        coordinator.Dispose();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    private sealed class Auth : IGoogleDriveSession
    {
        public bool IsConnected { get; private set; }
        public int Disconnects;
        public GoogleDriveAccount? Account => IsConnected ? new("User", "user@example.com") : null;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsConnected = true);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) { Disconnects++; IsConnected = false; return Task.CompletedTask; }
        public Task ClearSessionAsync(CancellationToken cancellationToken = default) { IsConnected = false; return Task.CompletedTask; }
        public Task<bool> RefreshAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsConnected);
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>("token");
    }
    private sealed class Network : ISyncNetworkStatus { public bool IsOnline { get; set; } = true; }
    private sealed class Block
    {
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Transport : IGoogleDriveTransport, IGoogleDriveVersionReader
    {
        public SyncEnvelope? Cloud;
        public int Version;
        public int Downloads, Uploads, Checks, MaximumConcurrentRequests;
        public bool Fail;
        private int concurrentRequests;
        private readonly Queue<Block> blocks = new();
        private Block? checkBlock;
        public Block BlockNextCheck() => checkBlock = new();
        public Block BlockNextDownload() { var block = new Block(); blocks.Enqueue(block); return block; }
        private void Enter() { var current = Interlocked.Increment(ref concurrentRequests); MaximumConcurrentRequests = Math.Max(MaximumConcurrentRequests, current); }
        private void Leave() => Interlocked.Decrement(ref concurrentRequests);
        public async Task<DriveSyncSnapshot?> DownloadAsync(CancellationToken cancellationToken = default)
        {
            Enter();
            try
            {
                Downloads++;
                if (Fail) throw new HttpRequestException("Offline");
                var snapshot = Cloud is null ? null : new DriveSyncSnapshot(Cloud, new("file", Version.ToString()), []);
                if (blocks.TryDequeue(out var block))
                {
                    block.Entered.TrySetResult();
                    await block.Release.Task.WaitAsync(cancellationToken);
                }
                return snapshot;
            }
            finally { Leave(); }
        }
        public Task<DriveUploadResult> UploadAsync(SyncEnvelope envelope, DriveWriteCondition condition, CancellationToken cancellationToken = default)
        {
            Enter();
            try { Uploads++; Cloud = envelope; Version++; return Task.FromResult(new DriveUploadResult(new("file", Version.ToString()))); }
            finally { Leave(); }
        }
        public async Task<DriveWriteCondition?> ReadCloudVersionAsync(CancellationToken cancellationToken = default)
        {
            Enter();
            try
            {
                Checks++;
                if (Fail) throw new HttpRequestException("Offline");
                if (checkBlock is { } block)
                {
                    checkBlock = null;
                    block.Entered.TrySetResult();
                    await block.Release.Task.WaitAsync(cancellationToken);
                }
                return Cloud is null ? null : new DriveWriteCondition("file", Version.ToString());
            }
            finally { Leave(); }
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);
        private readonly List<Timer> timers = [];
        public override DateTimeOffset GetUtcNow() => now;
        public int ActiveTimers { get { lock (timers) return timers.Count(timer => !timer.Disposed && timer.Due != DateTimeOffset.MaxValue); } }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (timers)
            {
                var timer = new Timer(this, callback, state);
                timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }
        public void Advance(double seconds)
        {
            Timer[] due;
            lock (timers)
            {
                now = now.AddSeconds(seconds);
                due = timers.Where(timer => !timer.Disposed && timer.Due <= now).ToArray();
                foreach (var timer in due) timer.Due = DateTimeOffset.MaxValue;
            }
            foreach (var timer in due) if (!timer.Disposed) timer.Callback(timer.State);
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public readonly TimerCallback Callback = callback;
            public readonly object? State = state;
            public DateTimeOffset Due;
            public bool Disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Assert.Equal(Timeout.InfiniteTimeSpan, period); // No recurring full-sync timer.
                lock (clock.timers)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.now + dueTime;
                    return true;
                }
            }
            public void Dispose() { lock (clock.timers) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
