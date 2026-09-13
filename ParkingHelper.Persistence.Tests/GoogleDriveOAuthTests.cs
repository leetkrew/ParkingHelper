using System.Net;
using System.Text;
using Microsoft.Maui.Authentication;
using Microsoft.Maui.Storage;
using ParkingHelper.App.Services;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class GoogleDriveOAuthTests
{
    [Fact]
    public async Task ConnectUsesChooserMinimumScopesAndSecureStorageThenDisconnectRevokesAndClears()
    {
        var storage = new Storage();
        var browser = new Browser();
        var server = new Server();
        var authentication = Create(storage, browser, server);
        await authentication.ConnectAsync();
        Assert.True(authentication.IsConnected);
        Assert.Equal(new GoogleDriveAccount("Juan", "juan@example.com"), authentication.Account);
        Assert.Single(storage.Values);
        Assert.Contains("refresh-token", storage.Values.Values.Single());
        await authentication.DisconnectAsync();
        Assert.False(authentication.IsConnected);
        Assert.Null(authentication.Account);
        Assert.Empty(storage.Values);
        Assert.Equal(1, server.Revocations);
        await authentication.ConnectAsync();
        Assert.Equal(2, browser.Calls);
    }

    [Fact]
    public async Task FailedSecureSaveDoesNotPublishConnectionOrIdentity()
    {
        var auth = Create(new Storage { FailSave = true }, new Browser(), new Server());
        await Assert.ThrowsAsync<GoogleDriveTokenStorageException>(() => auth.ConnectAsync());
        Assert.False(auth.IsConnected);
        Assert.Null(auth.Account);
    }

    [Theory]
    [InlineData(503)]
    [InlineData(403)]
    public async Task IdentityUnavailableDoesNotBlockDriveAuthorization(int status)
    {
        var auth = Create(new Storage(), new Browser(), new Server { IdentityStatus = (HttpStatusCode)status });
        Assert.True(await auth.ConnectAsync());
        Assert.True(auth.IsConnected);
        Assert.Null(auth.Account);
        Assert.Equal("access-token", await auth.GetAccessTokenAsync());
    }

    [Fact]
    public async Task EmailOnlyIdentityIsRetained()
    {
        var auth = Create(new Storage(), new Browser(), new Server { Identity = "{\"email\":\"juan@example.com\"}" });
        await auth.ConnectAsync();
        Assert.Null(auth.Account!.Name);
        Assert.Equal("juan@example.com", auth.Account.Email);
    }

    [Fact]
    public async Task CancellationDoesNotSaveAnythingOrExchangeTokens()
    {
        var storage = new Storage();
        var server = new Server();
        var auth = Create(storage, new Browser { Cancel = true }, server);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.ConnectAsync());
        Assert.False(auth.IsConnected);
        Assert.Empty(storage.Values);
        Assert.Equal(0, server.Exchanges);
    }

    [Fact]
    public async Task InvalidStateDoesNotExchangeTokens()
    {
        var server = new Server();
        var auth = Create(new Storage(), new Browser { BadState = true }, server);
        await Assert.ThrowsAsync<InvalidOperationException>(() => auth.ConnectAsync());
        Assert.False(auth.IsConnected);
        Assert.Equal(0, server.Exchanges);
    }

    [Fact]
    public async Task RevokedRefreshTokenClearsSavedSession()
    {
        var storage = new Storage();
        var server = new Server();
        var auth = Create(storage, new Browser(), server);
        await auth.ConnectAsync();
        server.RefreshRevoked = true;
        Assert.False(await auth.RefreshAccessTokenAsync());
        Assert.False(auth.IsConnected);
        Assert.Null(auth.Account);
        Assert.Empty(storage.Values);
    }

    [Fact]
    public async Task TransientRefreshFailureDoesNotLogOut()
    {
        var storage = new Storage();
        var server = new Server();
        var auth = Create(storage, new Browser(), server);
        await auth.ConnectAsync();
        server.TokenStatus = HttpStatusCode.ServiceUnavailable;
        await Assert.ThrowsAsync<HttpRequestException>(() => auth.RefreshAccessTokenAsync());
        Assert.True(auth.IsConnected);
        Assert.Single(storage.Values);
    }

    [Fact]
    public async Task RevocationNetworkFailureStillClearsLocalTokensAndIdentity()
    {
        var storage = new Storage();
        var server = new Server();
        var auth = Create(storage, new Browser(), server);
        await auth.ConnectAsync();
        server.RevokeFails = true;
        await Assert.ThrowsAsync<GoogleDriveDisconnectException>(() => auth.DisconnectAsync());
        Assert.False(auth.IsConnected);
        Assert.Null(auth.Account);
        Assert.Empty(storage.Values);
    }

    [Fact]
    public async Task RestartRestoresAccountOnlyFromSecureSession()
    {
        var storage = new Storage();
        var server = new Server();
        await Create(storage, new Browser(), server).ConnectAsync();
        var restored = Create(storage, new Browser(), server);
        Assert.False(restored.IsConnected);
        await restored.InitializeAsync();
        Assert.True(restored.IsConnected);
        Assert.Equal("juan@example.com", restored.Account?.Email);
        await restored.DisconnectAsync();
        var restarted = Create(storage, new Browser(), server);
        await restarted.InitializeAsync();
        Assert.False(restarted.IsConnected);
        Assert.Null(restarted.Account);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessRecreationRestoresWithoutChooserOrShutdownCleanup(bool expired)
    {
        var storage = new Storage();
        var server = new Server();
        var browser = new Browser();
        var clock = new Clock();
        var first = new GoogleDriveOAuthAuthentication(new Configuration(), new HttpClient(server), clock, storage, browser);
        await first.ConnectAsync();
        if (expired) clock.Now = clock.Now.AddHours(2);
        // No shutdown callback: a killed process cannot perform a final save.
        var restored = new GoogleDriveOAuthAuthentication(new Configuration(), new HttpClient(server), clock, storage, browser);
        await restored.InitializeAsync();
        Assert.True(restored.IsConnected);
        Assert.Equal(first.Account, restored.Account);
        Assert.Equal(1, browser.Calls);
        Assert.Equal(expired ? 2 : 1, server.Exchanges);
        Assert.Equal(0, server.Revocations);
        Assert.Single(storage.Values);
    }

    [Fact]
    public async Task ExpiredSessionOfflineAtStartupRetainsCredentialsAndRetries()
    {
        var storage = new Storage();
        var server = new Server();
        var browser = new Browser();
        var clock = new Clock();
        await new GoogleDriveOAuthAuthentication(new Configuration(), new HttpClient(server), clock, storage, browser).ConnectAsync();
        var saved = storage.Values.Values.Single();
        clock.Now = clock.Now.AddHours(2);
        server.TokenStatus = HttpStatusCode.ServiceUnavailable;
        var restored = new GoogleDriveOAuthAuthentication(new Configuration(), new HttpClient(server), clock, storage, browser);
        await Assert.ThrowsAsync<HttpRequestException>(() => restored.InitializeAsync());
        Assert.True(restored.IsConnected);
        Assert.Equal("juan@example.com", restored.Account?.Email);
        Assert.Equal(saved, storage.Values.Values.Single());
        server.TokenStatus = HttpStatusCode.OK;
        await restored.InitializeAsync();
        Assert.True(restored.IsConnected);
        Assert.Equal(1, browser.Calls);
        Assert.Equal(0, server.Revocations);
    }

    [Fact]
    public async Task AndroidGrantIsSavedBeforeProcessDestructionAndExplicitlyCleared()
    {
        var storage = new Storage();
        var identity = new GoogleDriveAccount("Juan", "juan@example.com");
        await new GoogleDriveAndroidSessionStorage(storage).SaveAsync("access-token", identity);
        var restarted = new GoogleDriveAndroidSessionStorage(storage);
        var restored = await restarted.ReadAsync();
        Assert.Equal(identity, restored?.Account);
        Assert.Equal("access-token", restored?.AccessToken);
        restarted.Clear();
        Assert.Null(await new GoogleDriveAndroidSessionStorage(storage).ReadAsync());
        Assert.Empty(storage.Values);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static GoogleDriveOAuthAuthentication Create(Storage storage, Browser browser, Server server) =>
        new(new Configuration(), new HttpClient(server), TimeProvider.System, storage, browser);
    private sealed class Configuration : IGoogleDriveOAuthConfiguration
    {
        public string ClientId => "test-client";
        public string RedirectUri => "test.app:/callback";
        public string PackageName => "test.app";
        public string SigningCertificateSha1 => "";
    }
    private sealed class Storage : ISecureStorage
    {
        public Dictionary<string, string> Values = new();
        public bool FailSave;
        public Task<string?> GetAsync(string key) => Task.FromResult(Values.GetValueOrDefault(key));
        public Task SetAsync(string key, string value)
        { if (FailSave) throw new Exception("MissingEntitlement"); Values[key] = value; return Task.CompletedTask; }
        public bool Remove(string key) => Values.Remove(key);
        public void RemoveAll() => throw new Exception("Must not clear unrelated secure storage.");
    }
    private sealed class Browser : IWebAuthenticator
    {
        public int Calls;
        public bool Cancel;
        public bool BadState;
        public Task<WebAuthenticatorResult> AuthenticateAsync(WebAuthenticatorOptions options) => AuthenticateAsync(options, default);
        public Task<WebAuthenticatorResult> AuthenticateAsync(WebAuthenticatorOptions options, CancellationToken cancellationToken)
        {
            Calls++;
            var query = options.Url!.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2))
                .ToDictionary(x => Uri.UnescapeDataString(x[0]), x => Uri.UnescapeDataString(x[1]));
            Assert.Equal("select_account consent", query["prompt"]);
            Assert.Equal(new[] { "email", "https://www.googleapis.com/auth/drive.appdata", "openid", "profile" }, query["scope"].Split(' ').Order().ToArray());
            Assert.True(options.PrefersEphemeralWebBrowserSession);
            if (Cancel) throw new OperationCanceledException();
            return Task.FromResult(new WebAuthenticatorResult(new Dictionary<string, string>
                { ["code"] = "auth-code", ["state"] = BadState ? "wrong" : query["state"] }));
        }
    }
    private sealed class Server : HttpMessageHandler
    {
        public int Exchanges;
        public int Revocations;
        public bool RefreshRevoked;
        public bool RevokeFails;
        public HttpStatusCode TokenStatus = HttpStatusCode.OK;
        public HttpStatusCode IdentityStatus = HttpStatusCode.OK;
        public string Identity = "{\"name\":\"Juan\",\"email\":\"juan@example.com\"}";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.NotEqual(HttpMethod.Delete, request.Method);
            if (request.RequestUri!.AbsolutePath == "/revoke")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("token=refresh-token", await request.Content!.ReadAsStringAsync(cancellationToken));
                Revocations++;
                if (RevokeFails) throw new HttpRequestException();
                return Reply(HttpStatusCode.OK, "{}");
            }
            if (request.RequestUri.AbsolutePath == "/v1/userinfo")
            {
                Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
                return Reply(IdentityStatus, Identity);
            }
            Assert.Equal("/token", request.RequestUri.AbsolutePath);
            Exchanges++;
            if (RefreshRevoked) return Reply(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}");
            return Reply(TokenStatus, "{\"access_token\":\"access-token\",\"refresh_token\":\"refresh-token\",\"expires_in\":3600}");
        }
        private static HttpResponseMessage Reply(HttpStatusCode status, string json) => new(status)
            { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
