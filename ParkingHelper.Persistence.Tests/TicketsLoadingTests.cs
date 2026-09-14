using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class TicketsLoadingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "TicketsLoading", Guid.NewGuid().ToString());
    private readonly Tickets tickets = new();
    private static readonly DateTime Now = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
    private static ParkingTicket Ticket(int id = 1) => new(Guid.Parse($"00000000-0000-0000-0000-{id:000000000000}"),
        Guid.NewGuid(), "QR_CODE", "PRIVATE", ParkingTicketState.Active, Now, Now, null, null);
    private static TaskCompletionSource<IReadOnlyList<ParkingTicket>> Read() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TicketsViewModel Model(GoogleDriveConnection? drive = null, TimeSpan? timeout = null)
    {
        var model = new TicketsViewModel(tickets, TimeProvider.System, NullLogger<TicketsViewModel>.Instance, drive)
            { RefreshTimeout = timeout ?? TimeSpan.FromSeconds(30) };
        model.PropertyChanged += (_, _) => Assert.False(model.ShowEmpty && model.IsLoading);
        return model;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LocalLoadOwnsSpinnerAndEmptyStateIsMutuallyExclusive(bool populated)
    {
        var pending = Read(); tickets.Read = _ => pending.Task;
        var model = Model();
        var load = model.LoadAsync();
        Assert.True(model.IsLoading); Assert.False(model.ShowEmpty); Assert.False(model.ShowTickets);
        pending.SetResult(populated ? [Ticket()] : []);
        await load;
        Assert.False(model.IsLoading); Assert.False(model.IsRefreshing);
        Assert.Equal(!populated, model.ShowEmpty); Assert.Equal(populated, model.ShowTickets);
    }

    [Fact]
    public async Task SQLiteExceptionClearsSpinnerAndShowsLocalErrorInsteadOfEmpty()
    {
        tickets.Read = _ => throw new SqliteException("Local read failed", 11);
        var model = Model(); await model.LoadAsync();
        Assert.False(model.IsLoading); Assert.True(model.HasError); Assert.False(model.ShowEmpty);
        tickets.Read = _ => Task.FromResult<IReadOnlyList<ParkingTicket>>([]);
        await model.LoadAsync();
        Assert.False(model.HasError); Assert.True(model.ShowEmpty);
    }

    [Fact]
    public async Task CancellationClearsSpinnerWithoutShowingAnError()
    {
        tickets.Read = _ => Task.FromCanceled<IReadOnlyList<ParkingTicket>>(new CancellationToken(true));
        var model = Model(); await model.LoadAsync();
        Assert.False(model.IsLoading); Assert.False(model.HasError); Assert.False(model.ShowEmpty);
    }

    [Fact]
    public async Task RapidFilterSwitchesOnlyAllowLatestLoadToRenderAndClearItsSpinner()
    {
        var first = Read(); var second = Read(); var third = Read();
        var reads = new Queue<TaskCompletionSource<IReadOnlyList<ParkingTicket>>>([first, second, third]);
        var filters = new List<bool>();
        tickets.Read = archived => { filters.Add(archived); return reads.Dequeue().Task; };
        var model = Model();
        var a = model.LoadAsync(false); var b = model.LoadAsync(true); var c = model.LoadAsync(false);
        first.SetResult([Ticket(1)]); second.SetResult([Ticket(2)]);
        await Task.WhenAll(a, b);
        Assert.True(model.IsLoading); Assert.Empty(model.Items); Assert.False(model.ShowEmpty);
        var latest = Ticket(3); third.SetResult([latest]); await c;
        Assert.Equal(latest.Id, Assert.Single(model.Items).Id);
        Assert.True(model.IsActive); Assert.False(model.IsLoading);
        Assert.Equal(new[] { false, true, false }, filters);
    }

    [Fact]
    public async Task NavigationAwayCancelsUiWaitAndOldReadCannotOverwriteReturnToTickets()
    {
        var old = Read(); tickets.Read = _ => old.Task;
        var model = Model(); model.Activate();
        var leaving = model.LoadAsync(); model.Deactivate();
        await leaving.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(model.IsLoading); Assert.False(model.ShowEmpty);
        tickets.Rows = [Ticket(2)]; tickets.Read = null;
        model.Activate(); await model.LoadAsync(false);
        old.SetResult([Ticket(1)]);
        Assert.Equal(tickets.Rows[0].Id, Assert.Single(model.Items).Id);
        Assert.False(model.IsLoading);
        model.Deactivate();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HangingOrFailedBackgroundDriveDoesNotOwnPageSpinner(bool fail)
    {
        var (drive, transport) = Drive();
        tickets.Rows = [Ticket()];
        var model = Model(drive); model.Activate();
        var sync = drive.SyncAsync(); await transport.Entered.Task;
        await model.LoadAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(drive.IsBusy); Assert.False(model.IsLoading); Assert.True(model.ShowTickets);
        transport.Fail = fail; transport.Release.SetResult(); await sync;
        Assert.False(model.IsLoading); Assert.True(model.ShowTickets); Assert.False(model.HasError);
        model.Deactivate();
    }

    [Fact]
    public async Task SuccessfulSyncReloadsQuietlyAndKeepsLocalTicketsUsableDuringRead()
    {
        var (drive, transport) = Drive(); tickets.Rows = [Ticket(1)];
        var model = Model(drive); model.Activate(); await model.LoadAsync();
        var reload = Read(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tickets.Read = _ => { entered.TrySetResult(); return reload.Task; };
        var sync = drive.SyncAsync(); await transport.Entered.Task;
        transport.Release.SetResult(); await sync; await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(model.IsLoading); Assert.True(model.ShowTickets); Assert.Equal(tickets.Rows[0].Id, model.Items[0].Id);
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = Ticket(2);
        model.PropertyChanged += (_, _) => { if (model.Items.FirstOrDefault()?.Id == latest.Id) rendered.TrySetResult(); };
        reload.SetResult([latest]); await rendered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(model.IsLoading); Assert.Equal(latest.Id, model.Items[0].Id);
        model.Deactivate();
    }

    [Fact]
    public async Task HungPullRefreshTimesOutItsIndicatorWithoutCancelingSharedDriveOrClearingTickets()
    {
        var (drive, transport) = Drive(); tickets.Rows = [Ticket()];
        var model = Model(drive, TimeSpan.FromMilliseconds(20)); await model.LoadAsync();
        // Observe the state synchronously: the 20 ms timeout may expire before the
        // transport continuation is scheduled on a busy build machine.
        var sawRefreshing = false;
        model.PropertyChanged += (_, _) =>
        {
            if (!model.IsRefreshing) return;
            sawRefreshing = true;
            Assert.False(model.IsLoading); Assert.True(model.ShowTickets);
        };
        var refresh = model.RefreshAsync(); await transport.Entered.Task;
        Assert.True(sawRefreshing); Assert.False(model.IsLoading); Assert.True(model.ShowTickets);
        await refresh.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(model.IsRefreshing); Assert.False(model.IsLoading); Assert.True(model.ShowTickets);
        Assert.True(drive.IsBusy);
        transport.Release.SetResult();
        await drive.SyncAsync();
    }

    [Fact]
    public async Task PullRefreshFailureAndLocalReloadFailureAlwaysReleaseNativeIndicator()
    {
        var (drive, transport) = Drive(); tickets.Rows = [Ticket()];
        var model = Model(drive); await model.LoadAsync();
        var refresh = model.RefreshAsync(); await transport.Entered.Task;
        transport.Fail = true;
        tickets.Read = _ => throw new SqliteException("Read failure", 11);
        transport.Release.SetResult(); await refresh;
        Assert.False(model.IsRefreshing); Assert.False(model.IsLoading); Assert.True(model.ShowTickets);
        Assert.True(model.HasError); Assert.False(model.ShowEmpty);
    }

    [Fact]
    public async Task LeavingDuringRefreshReleasesIndicatorsAndCannotCancelNewLocalLoad()
    {
        var (drive, transport) = Drive(); var model = Model(drive); await model.LoadAsync();
        var refresh = model.RefreshAsync(); await transport.Entered.Task;
        model.Deactivate(); await refresh.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(model.IsRefreshing); Assert.False(model.IsLoading); Assert.True(drive.IsBusy);
        var local = Read(); tickets.Read = _ => local.Task;
        model.Activate(); var current = model.LoadAsync();
        // A failed old flight must not interfere with the current local load.
        transport.Fail = true; transport.Release.SetResult(); await drive.SyncAsync();
        Assert.True(model.IsLoading);
        local.SetResult([]); await current;
        Assert.False(model.IsLoading); Assert.True(model.ShowEmpty);
        model.Deactivate();
    }

    private (GoogleDriveConnection, Transport) Drive()
    {
        var repository = new SqliteParkingRepository(Path.Combine(directory, Guid.NewGuid() + ".db3"));
        var auth = new Auth(); var transport = new Transport();
        return (new(auth, new GoogleDriveSynchronizationService(repository, auth, transport, new Network(), new ConcurrencyRetryPolicy())), transport);
    }
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
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Fail;
        public async Task<DriveSyncSnapshot?> DownloadAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            if (Fail) throw new HttpRequestException("Offline");
            return null;
        }
        public Task<DriveUploadResult> UploadAsync(SyncEnvelope envelope, DriveWriteCondition condition, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DriveUploadResult(new("file", "1")));
    }
    private sealed class Tickets : ITicketService
    {
        public IReadOnlyList<ParkingTicket> Rows = [];
        public Func<bool, Task<IReadOnlyList<ParkingTicket>>>? Read;
        public Task<IReadOnlyList<ParkingTicket>> GetActiveTicketsAsync() => Read?.Invoke(false) ?? Task.FromResult(Rows);
        public Task<IReadOnlyList<ParkingTicket>> GetArchivedTicketsAsync() => Read?.Invoke(true) ?? Task.FromResult(Rows);
        public Task<ParkingTicket> ArchiveTicketAsync(Guid id) => throw new NotSupportedException();
        public Task<ParkingTicket> RestoreTicketAsync(Guid id) => throw new NotSupportedException();
        public Task<ParkingTicket> DeleteTicketAsync(Guid id) => throw new NotSupportedException();
        public Task<ParkingTicket?> GetTicketAsync(Guid id) => throw new NotSupportedException();
        public Task<ParkingTicket> EditTicketAsync(Guid id, Guid plate, DateTime time) => throw new NotSupportedException();
        public Task<ParkingTicket> ChangeTicketPlateAsync(Guid id, Guid plate) => throw new NotSupportedException();
        public Task<TicketCreationResult> CreateActiveTicketAsync(ScanResult scan, VehiclePlate plate) => throw new NotSupportedException();
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
