using ParkingHelper.App.Services;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class GoogleDriveConnectionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "DriveConnectionTests", Guid.NewGuid().ToString());
    private readonly Auth auth = new();
    private readonly Transport transport = new();
    private SqliteParkingRepository Repository => new(Path.Combine(directory, "parking.db3"));
    private GoogleDriveConnection Create(Network? network = null) => new(auth, new GoogleDriveSynchronizationService(Repository, auth,
        transport, network ?? new Network(), new ConcurrencyRetryPolicy(delay: TimeSpan.Zero)));

    [Fact]
    public async Task ConnectDisconnectReconnectPreservesLocalAndCloudData()
    {
        var repository = Repository;
        var plate = new VehiclePlate(Guid.NewGuid(), "LOCAL", DateTime.UtcNow, DateTime.UtcNow);
        await repository.AddPlateAsync(plate);
        var connection = Create();
        Assert.False(connection.IsConnected);
        Assert.Null(connection.Account);
        await connection.SyncAsync();
        Assert.Equal(0, transport.Uploads);
        await connection.ConnectAsync();
        Assert.True(connection.IsConnected);
        Assert.Equal("User", connection.Account?.Name);
        Assert.NotNull(connection.LastSuccessfulSync);
        var cloud = transport.Cloud;
        await connection.DisconnectAsync();
        Assert.False(connection.IsConnected);
        Assert.Null(connection.Account);
        Assert.Null(connection.LastSuccessfulSync);
        Assert.Equal(1, auth.Disconnects);
        Assert.Equal(plate.Id, Assert.Single(await repository.GetPlatesAsync()).Id);
        Assert.Same(cloud, transport.Cloud);
        await connection.SyncAsync();
        Assert.Equal(1, transport.Uploads);
        await connection.ConnectAsync();
        Assert.Equal(2, auth.Connects);
        Assert.True(connection.IsConnected);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledAndFailedAuthorizationStayDisconnected(bool cancelled)
    {
        auth.Error = cancelled ? new OperationCanceledException() : new InvalidOperationException();
        var connection = Create();
        await connection.ConnectAsync();
        Assert.False(connection.IsConnected);
        Assert.False(connection.IsBusy);
        Assert.Null(connection.Account);
        Assert.Equal(0, transport.Uploads);
        if (cancelled) Assert.Empty(connection.Message);
    }

    [Fact]
    public async Task ConnectionIsNotShownBeforeAuthorizationCompletes()
    {
        auth.ConnectGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = Create();
        var pending = connection.ConnectAsync();
        Assert.True(connection.IsBusy);
        Assert.False(connection.IsConnected);
        Assert.Null(connection.Account);
        var joined = connection.ConnectAsync();
        Assert.Same(pending, joined);
        Assert.Equal(1, auth.Connects);
        auth.ConnectGate.SetResult();
        await pending;
        Assert.True(connection.IsConnected);
    }

    [Fact]
    public async Task SyncFailureKeepsAccountAndLastSuccess()
    {
        var connection = Create();
        await connection.ConnectAsync();
        var last = connection.LastSuccessfulSync;
        transport.Fail = true;
        await connection.SyncAsync();
        Assert.True(connection.IsConnected);
        Assert.Equal(last, connection.LastSuccessfulSync);
        Assert.Contains("Local changes were kept", connection.Message);
    }

    [Fact]
    public async Task DisconnectCancelsInFlightSyncAndPreventsFurtherUploads()
    {
        var connection = Create();
        await connection.ConnectAsync();
        transport.Block = true;
        var pending = connection.SyncAsync();
        await transport.Entered.Task;
        var joined = connection.SyncAsync();
        Assert.Same(pending, joined);
        await connection.DisconnectAsync();
        await pending;
        Assert.False(connection.IsConnected);
        Assert.Equal(1, transport.Uploads);
        Assert.Null(connection.Account);
        Assert.Equal(1, auth.Disconnects);
    }

    [Fact]
    public async Task RevocationFailureStillHidesLocallyDisconnectedAccount()
    {
        var connection = Create();
        await connection.ConnectAsync();
        auth.RevokeFails = true;
        await connection.DisconnectAsync();
        Assert.False(connection.IsConnected);
        Assert.Null(connection.Account);
        Assert.Contains("Disconnected locally", connection.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupAndResumeRestoreEvenOfflineWithoutLoggingOut(bool online)
    {
        var network = new Network { IsOnline = online };
        var connection = Create(network);
        auth.RestoreConnected = true;
        using var trigger = new SynchronizationTrigger(connection, network);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Changed += () =>
        {
            if (!connection.IsBusy && connection.IsConnected && (!online || connection.LastSuccessfulSync is not null))
                finished.TrySetResult();
        };
        trigger.RequestResumeSync();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(connection.IsConnected);
        Assert.Equal("User", connection.Account?.Name);
        Assert.Equal(online ? 1 : 0, transport.Uploads);
        Assert.Equal(0, auth.Connects);
        Assert.Equal(0, auth.Disconnects);
    }

    private sealed class Network : ISyncNetworkStatus { public bool IsOnline { get; set; } = true; }
    private sealed class Auth : IGoogleDriveSession
    {
        public bool IsConnected { get; private set; }
        public GoogleDriveAccount? Account => IsConnected ? new("User", "user@example.com") : null;
        public int Connects;
        public int Disconnects;
        public bool RevokeFails;
        public Exception? Error;
        public TaskCompletionSource? ConnectGate;
        public bool RestoreConnected;
        public Task InitializeAsync(CancellationToken cancellationToken = default)
        { if (RestoreConnected) IsConnected = true; return Task.CompletedTask; }
        public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Connects++;
            if (Error is not null) throw Error;
            if (ConnectGate is not null) await ConnectGate.Task.WaitAsync(cancellationToken);
            return IsConnected = true;
        }
        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            Disconnects++;
            IsConnected = false;
            if (RevokeFails) throw new GoogleDriveDisconnectException("Disconnected locally; remote revocation failed.");
            return Task.CompletedTask;
        }
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(IsConnected ? "token" : null);
        public Task<bool> RefreshAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsConnected);
        public Task ClearSessionAsync(CancellationToken cancellationToken = default) { IsConnected = false; return Task.CompletedTask; }
    }
    private sealed class Transport : IGoogleDriveTransport
    {
        public SyncEnvelope? Cloud;
        public int Uploads;
        public bool Fail;
        public bool Block;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<DriveSyncSnapshot?> DownloadAsync(CancellationToken cancellationToken = default)
        {
            if (Fail) throw new HttpRequestException();
            if (Block) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return Cloud is null ? null : new(Cloud, new("file", "1"), []);
        }
        public Task<DriveUploadResult> UploadAsync(SyncEnvelope envelope, DriveWriteCondition condition, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Uploads++; Cloud = envelope; return Task.FromResult(new DriveUploadResult(new("file", "2"))); }
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
