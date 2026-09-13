using System.Net;
using System.Text;
using System.Text.Json;
using ParkingHelper.App.Services;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class GoogleDriveRestTransportTests
{
    private static readonly SyncEnvelope Envelope = new(1, []);
    private static string Metadata(string version) => JsonSerializer.Serialize(new
        { id = "file", name = "parkinghelper-sync-v1.json", version, modifiedTime = "2026-09-13T00:00:00Z" });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateAndUpdateReturnServerVersionsWithMetadataFallback(bool fallback)
    {
        var handler = new ScriptedHandler();
        handler.Add("POST", "uploadType=multipart", fallback ? "{\"id\":\"file\"}" : Metadata("1"), async request =>
        {
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("parkinghelper-sync-v1.json", body);
            Assert.Contains("appDataFolder", body);
        });
        if (fallback) handler.Add("GET", "fields=id,name,version,modifiedTime", Metadata("1"));
        handler.Add("GET", "fields=id,name,version,modifiedTime", Metadata("1"));
        handler.Add("PATCH", "uploadType=media", fallback ? "" : Metadata("2"));
        if (fallback) handler.Add("GET", "fields=id,name,version,modifiedTime", Metadata("2"));
        var transport = new GoogleDriveRestTransport(new HttpClient(handler), new Auth());
        var created = await transport.UploadAsync(Envelope, new(null, null));
        Assert.Equal("1", created.Version.VersionToken);
        var updated = await transport.UploadAsync(Envelope, created.Version);
        Assert.Equal("2", updated.Version.VersionToken);
        Assert.Empty(handler.Steps);
    }

    [Fact]
    public async Task StaleVersionPreventsPatch()
    {
        var handler = new ScriptedHandler();
        handler.Add("GET", "fields=id,name,version,modifiedTime", Metadata("2"));
        var transport = new GoogleDriveRestTransport(new HttpClient(handler), new Auth());
        await Assert.ThrowsAsync<DriveConcurrencyException>(() => transport.UploadAsync(Envelope, new("file", "1")));
        Assert.Empty(handler.Steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DownloadUsesMetadataVersionAndDetectsChangesDuringRead(bool changed)
    {
        var handler = new ScriptedHandler();
        AddDownload(handler, "1", changed ? "2" : "1");
        var transport = new GoogleDriveRestTransport(new HttpClient(handler), new Auth());
        if (changed) await Assert.ThrowsAsync<DriveConcurrencyException>(() => transport.DownloadAsync());
        else Assert.Equal("1", (await transport.DownloadAsync())!.Version.VersionToken);
        Assert.Empty(handler.Steps);
    }

    [Fact]
    public async Task MissingVersionIsSyncFailureAndDoesNotOverwrite()
    {
        var handler = new ScriptedHandler();
        handler.Add("GET", "spaces=appDataFolder", "{\"files\":[{\"id\":\"file\"}]}");
        handler.Add("GET", "fields=id,name,version,modifiedTime", "{\"id\":\"file\"}");
        await WithService(handler, async service =>
        {
            await service.SynchronizeAsync();
            Assert.Equal(SyncRunStatus.Failed, service.Status.State);
            Assert.DoesNotContain("configured", service.Status.Message);
        });
        Assert.Empty(handler.Steps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealTransportConflictUsesBoundedServiceRetry(bool exhausted)
    {
        var handler = new ScriptedHandler();
        var attempts = exhausted ? 3 : 2;
        for (var i = 0; i < attempts; i++)
        {
            AddDownload(handler, "1", "1");
            handler.Add("GET", "fields=id,name,version,modifiedTime", Metadata(!exhausted && i == 1 ? "1" : "2"));
        }
        if (!exhausted) handler.Add("PATCH", "uploadType=media", Metadata("3"));
        await WithService(handler, async service =>
        {
            await service.SynchronizeAsync();
            Assert.Equal(exhausted ? SyncRunStatus.Failed : SyncRunStatus.Succeeded, service.Status.State);
        });
        Assert.Empty(handler.Steps);
    }

    [Fact]
    public async Task MissingVersionAfterSuccessfulUploadIsTransportError()
    {
        var handler = new ScriptedHandler();
        handler.Add("POST", "uploadType=multipart", "{\"id\":\"file\"}");
        handler.Add("GET", "fields=id,name,version,modifiedTime", "{\"id\":\"file\"}");
        var transport = new GoogleDriveRestTransport(new HttpClient(handler), new Auth());
        await Assert.ThrowsAsync<DriveTransportException>(() => transport.UploadAsync(Envelope, new(null, null)));
        Assert.Empty(handler.Steps);
    }

    [Fact]
    public async Task MissingObservedVersionNeverSendsUpdate()
    {
        var handler = new ScriptedHandler();
        var transport = new GoogleDriveRestTransport(new HttpClient(handler), new Auth());
        await Assert.ThrowsAsync<DriveTransportException>(() => transport.UploadAsync(Envelope, new("file", null)));
        Assert.Empty(handler.Steps);
    }

    private static void AddDownload(ScriptedHandler handler, string before, string after)
    {
        handler.Add("GET", "fields=files(id,name,version,modifiedTime,headRevisionId)", "{\"files\":[" + Metadata(before) + "]}");
        handler.Add("GET", "fields=id,name,version,modifiedTime", Metadata(before));
        handler.Add("GET", "alt=media", JsonSerializer.Serialize(Envelope));
        handler.Add("GET", "fields=id,name,version,modifiedTime", Metadata(after));
    }

    private static async Task WithService(ScriptedHandler handler, Func<GoogleDriveSynchronizationService, Task> action)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DriveTransportTests", Guid.NewGuid().ToString());
        try
        {
            var repository = new SqliteParkingRepository(Path.Combine(directory, "parking.db3"));
            await action(new GoogleDriveSynchronizationService(repository, new Auth(),
                new GoogleDriveRestTransport(new HttpClient(handler), new Auth()), new Network(),
                new ConcurrencyRetryPolicy(delay: TimeSpan.Zero)));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Auth : IGoogleDriveAuthentication
    {
        public bool IsConnected => true;
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>("test-token");
        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Network : ISyncNetworkStatus { public bool IsOnline => true; }
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        public Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> Steps { get; } = new();
        public void Add(string method, string query, string body, Func<HttpRequestMessage, Task>? inspect = null) =>
            Steps.Enqueue(async request =>
            {
                Assert.Equal(method, request.Method.Method);
                Assert.Contains(query, Uri.UnescapeDataString(request.RequestUri!.Query));
                Assert.Empty(request.Headers.IfMatch);
                if (method is "POST" or "PATCH") Assert.Contains("fields=id,name,version,modifiedTime", request.RequestUri.Query);
                if (inspect is not null) await inspect(request);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Steps.Dequeue()(request);
    }
}
