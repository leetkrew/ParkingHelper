using System.Net;
using System.Text;
using ParkingHelper.App.Services;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class GoogleDriveTokenRecoveryTests
{
    [Fact]
    public async Task UnauthorizedUploadRefreshesOnceAndPreservesMultipartBody()
    {
        var auth = new Session();
        var requests = 0;
        string? firstBody = null;
        var handler = new Handler(async request =>
        {
            requests++;
            var body = await request.Content!.ReadAsStringAsync();
            if (requests == 1)
            {
                firstBody = body;
                Assert.Equal("old", request.Headers.Authorization?.Parameter);
                return Reply(HttpStatusCode.Unauthorized, "{}");
            }
            Assert.Equal(firstBody, body);
            Assert.Equal("new", request.Headers.Authorization?.Parameter);
            Assert.Equal("multipart/related", request.Content.Headers.ContentType?.MediaType);
            return Reply(HttpStatusCode.OK, "{\"id\":\"file\",\"version\":\"1\"}");
        });
        var transport = new GoogleDriveRestTransport(new HttpClient(handler), auth);
        var result = await transport.UploadAsync(new SyncEnvelope(1, []), new(null, null));
        Assert.Equal("1", result.Version.VersionToken);
        Assert.Equal(1, auth.Refreshes);
        Assert.True(auth.IsConnected);
        Assert.Equal(2, requests);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    public async Task UnrecoverableAuthorizationDisconnectsWithoutRetryLoop(bool refreshSucceeds, int expectedRequests)
    {
        var auth = new Session { CanRefresh = refreshSucceeds };
        var requests = 0;
        var handler = new Handler(request =>
        {
            requests++;
            return Task.FromResult(Reply(HttpStatusCode.Unauthorized, "{}"));
        });
        var transport = new GoogleDriveRestTransport(new HttpClient(handler), auth);
        await Assert.ThrowsAsync<GoogleDriveAuthenticationExpiredException>(() => transport.DownloadAsync());
        Assert.False(auth.IsConnected);
        Assert.Null(auth.Account);
        Assert.Equal(expectedRequests, requests);
        Assert.Equal(1, auth.Refreshes);
    }

    [Fact]
    public async Task ServerFailureDoesNotRevokeSession()
    {
        var auth = new Session();
        var handler = new Handler(_ => Task.FromResult(Reply(HttpStatusCode.ServiceUnavailable, "{}")));
        var transport = new GoogleDriveRestTransport(new HttpClient(handler), auth);
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.DownloadAsync());
        Assert.True(auth.IsConnected);
        Assert.Equal(0, auth.Refreshes);
    }

    private static HttpResponseMessage Reply(HttpStatusCode status, string json) => new(status)
        { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class Session : IGoogleDriveSession
    {
        public int Refreshes;
        public bool CanRefresh = true;
        public bool IsConnected { get; private set; } = true;
        public GoogleDriveAccount? Account => IsConnected ? new("User", "user@example.com") : null;
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> ConnectAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsConnected = true);
        public Task DisconnectAsync(CancellationToken cancellationToken = default) => ClearSessionAsync(cancellationToken);
        public Task ClearSessionAsync(CancellationToken cancellationToken = default) { IsConnected = false; return Task.CompletedTask; }
        public Task<bool> RefreshAccessTokenAsync(CancellationToken cancellationToken = default)
        { Refreshes++; IsConnected = CanRefresh; return Task.FromResult(CanRefresh); }
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(IsConnected ? Refreshes == 0 ? "old" : "new" : null);
    }
}
