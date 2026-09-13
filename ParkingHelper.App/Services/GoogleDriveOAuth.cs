using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Authentication;
using Microsoft.Maui.Storage;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Services;

public sealed class GoogleDriveTokenStorageException(Exception innerException)
    : InvalidOperationException("Google Drive credentials could not be saved securely on this device.", innerException);

public interface IGoogleDriveOAuthConfiguration
{
    string ClientId { get; }
    string RedirectUri { get; }
    string PackageName { get; }
    string SigningCertificateSha1 { get; }
}

public sealed class GoogleDriveOAuthAuthentication(
    IGoogleDriveOAuthConfiguration configuration,
    HttpClient http,
    TimeProvider clock,
    ISecureStorage? storage,
    IWebAuthenticator? browser) : IGoogleDriveSession
{
    private readonly ISecureStorage storage = storage ?? SecureStorage.Default;
    private readonly IWebAuthenticator browser = browser ?? WebAuthenticator.Default;
    private const string TokenKey = "parkinghelper.google-drive.oauth-token";
    private readonly SemaphoreSlim gate = new(1, 1);
    private TokenState? token;
    private bool loaded;

    public bool IsConnected => token is not null;
    public GoogleDriveAccount? Account => token?.Account;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var accessToken = await GetAccessTokenAsync(cancellationToken);
        if (accessToken is null || Account is not null) return;
        var account = await GoogleDriveIdentity.ReadAsync(http, accessToken, cancellationToken);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (token?.AccessToken == accessToken && account is not null)
                await SaveAsync(token with { Account = account });
        }
        finally { gate.Release(); }
    }

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            ValidateConfiguration();
            var state = CreateVerifier();
            var verifier = CreateVerifier();
            var challenge = CreateChallenge(verifier);
            var authorization = new Uri(
                "https://accounts.google.com/o/oauth2/v2/auth?" +
                FormUrlEncoded(new Dictionary<string, string>
                {
                    ["client_id"] = configuration.ClientId,
                    ["redirect_uri"] = configuration.RedirectUri,
                    ["response_type"] = "code",
                    ["scope"] = string.Join(" ", GoogleDriveIdentity.Scopes),
                    ["state"] = state,
                    ["access_type"] = "offline",
                    ["prompt"] = "select_account consent",
                    ["code_challenge"] = challenge,
                    ["code_challenge_method"] = "S256"
                }));

            var result = await browser.AuthenticateAsync(new WebAuthenticatorOptions
            {
                Url = authorization, CallbackUrl = new Uri(configuration.RedirectUri), PrefersEphemeralWebBrowserSession = true
            }, cancellationToken).ConfigureAwait(false);
            if (result.Properties.TryGetValue("error", out var authorizationError) && authorizationError == "access_denied")
                return false;
            if (!result.Properties.TryGetValue("state", out var returnedState) || returnedState != state)
                throw new InvalidOperationException("Google authorization state did not match.");
            if (!result.Properties.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("Google authorization did not return an authorization code.");

            var response = await ExchangeAsync(new Dictionary<string, string>
            {
                ["client_id"] = configuration.ClientId,
                ["code"] = code,
                ["code_verifier"] = verifier,
                ["grant_type"] = "authorization_code",
                ["redirect_uri"] = configuration.RedirectUri
            }, cancellationToken).ConfigureAwait(false);
            response = response with { Account = await GoogleDriveIdentity.ReadAsync(http, response.AccessToken, cancellationToken) };
            cancellationToken.ThrowIfCancellationRequested();
            await SaveAsync(response).ConfigureAwait(false);
            return true;
        }
        finally { gate.Release(); }
    }

    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (token is null) return null;
        if (token.ExpiresUtc <= clock.GetUtcNow().UtcDateTime.AddMinutes(1))
            await RefreshAccessTokenAsync(cancellationToken);
        return token?.AccessToken;
    }

    public async Task<bool> RefreshAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (token is null) return false;
            if (string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                ClearLocalSession();
                return false;
            }
            try
            {
                var refreshed = await ExchangeAsync(new Dictionary<string, string>
                {
                    ["client_id"] = configuration.ClientId,
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = token.RefreshToken
                }, cancellationToken).ConfigureAwait(false);
                refreshed = refreshed with { RefreshToken = refreshed.RefreshToken ?? token.RefreshToken, Account = token.Account };
                await SaveAsync(refreshed).ConfigureAwait(false);
                return true;
            }
            catch (GoogleDriveAuthenticationExpiredException)
            {
                ClearLocalSession();
                return false;
            }
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            var revokeToken = token?.RefreshToken ?? token?.AccessToken;
            // Clear locally even when the network is unavailable. No Drive files are deleted.
            try { ClearLocalSession(); }
            finally
            {
                if (revokeToken is not null && !await GoogleDriveIdentity.RevokeAsync(http, revokeToken, cancellationToken))
                    throw new GoogleDriveDisconnectException("Disconnected locally. Google authorization could not be revoked; remove Parking Helper access in your Google Account.");
            }
        }
        finally { gate.Release(); }
    }

    public async Task ClearSessionAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try { ClearLocalSession(); }
        finally { gate.Release(); }
    }

    private void ClearLocalSession()
    {
        token = null;
        loaded = true;
        try { storage.Remove(TokenKey); }
        catch (Exception)
        {
            throw new GoogleDriveDisconnectException("Could not clear saved Google credentials. Please try Disconnect again before restarting the app.");
        }
    }

    private async Task EnsureLoadedAsync()
    {
        if (loaded) return;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (loaded) return;
            var serialized = await storage.GetAsync(TokenKey).ConfigureAwait(false);
            token = string.IsNullOrWhiteSpace(serialized)
                ? null
                : JsonSerializer.Deserialize<TokenState>(serialized);
            loaded = true;
        }
        finally { gate.Release(); }
    }

    private async Task SaveAsync(TokenState value)
    {
        try
        {
            await storage.SetAsync(TokenKey, JsonSerializer.Serialize(value)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            throw new GoogleDriveTokenStorageException(error);
        }
        // Do not report a connection until the credentials have been persisted.
        token = value;
        loaded = true;
    }

    private async Task<TokenState> ExchangeAsync(
        IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(values), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var error = await response.Content.ReadFromJsonAsync<OAuthError>(cancellationToken).ConfigureAwait(false);
            if (error?.Error == "invalid_grant") throw new GoogleDriveAuthenticationExpiredException();
        }
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Google token response was empty.");
        if (payload.Scope is not null && !payload.Scope.Split(' ').Contains(GoogleDriveIdentity.DriveScope))
            throw new GoogleDriveAuthenticationExpiredException();
        if (string.IsNullOrWhiteSpace(payload.AccessToken) || payload.ExpiresIn <= 0)
            throw new InvalidOperationException("Google token response was invalid.");
        return new TokenState(payload.AccessToken, payload.RefreshToken,
            clock.GetUtcNow().UtcDateTime.AddSeconds(payload.ExpiresIn));
    }

    private void ValidateConfiguration()
    {
        if (string.IsNullOrWhiteSpace(configuration.ClientId)
            || string.IsNullOrWhiteSpace(configuration.RedirectUri))
            throw new NotSupportedException("Google Drive OAuth configuration is missing.");
    }

    private static string CreateVerifier() =>
        Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string CreateChallenge(string verifier) =>
        Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string FormUrlEncoded(IReadOnlyDictionary<string, string> values) =>
        string.Join("&", values.Select(pair =>
            $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"));

    private sealed record TokenState(string AccessToken, string? RefreshToken, DateTime ExpiresUtc, GoogleDriveAccount? Account = null);
    private sealed record OAuthError(string? Error);
    private sealed record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string? AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn,
        [property: System.Text.Json.Serialization.JsonPropertyName("scope")] string? Scope);
}
