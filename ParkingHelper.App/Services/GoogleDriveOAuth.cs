using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Maui.Authentication;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Services;

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
    TimeProvider clock) : IGoogleDriveAuthentication
{
    private const string TokenKey = "parkinghelper.google-drive.oauth-token";
    private const string Scope = "https://www.googleapis.com/auth/drive.appdata";
    private readonly SemaphoreSlim gate = new(1, 1);
    private TokenState? token;
    private bool loaded;

    public bool IsConnected => token is not null;

    public async Task<bool> ConnectAsync(CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();
        var verifier = CreateVerifier();
        var challenge = CreateChallenge(verifier);
        var authorization = new Uri(
            "https://accounts.google.com/o/oauth2/v2/auth?" +
            FormUrlEncoded(new Dictionary<string, string>
            {
                ["client_id"] = configuration.ClientId,
                ["redirect_uri"] = configuration.RedirectUri,
                ["response_type"] = "code",
                ["scope"] = Scope,
                ["access_type"] = "offline",
                ["prompt"] = "consent",
                ["code_challenge"] = challenge,
                ["code_challenge_method"] = "S256"
            }));

        var result = await WebAuthenticator.Default.AuthenticateAsync(authorization, new Uri(configuration.RedirectUri))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
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
        await SaveAsync(response).ConfigureAwait(false);
        return true;
    }

    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await EnsureLoadedAsync().ConfigureAwait(false);
        if (token is null) return null;
        if (token.ExpiresUtc > clock.GetUtcNow().UtcDateTime.AddMinutes(1))
            return token.AccessToken;

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (token is null) return null;
            if (token.ExpiresUtc > clock.GetUtcNow().UtcDateTime.AddMinutes(1))
                return token.AccessToken;
            if (string.IsNullOrWhiteSpace(token.RefreshToken))
            {
                token = null;
                SecureStorage.Default.Remove(TokenKey);
                return null;
            }
            var refreshed = await ExchangeAsync(new Dictionary<string, string>
            {
                ["client_id"] = configuration.ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = token.RefreshToken
            }, cancellationToken).ConfigureAwait(false);
            refreshed = refreshed with { RefreshToken = token.RefreshToken };
            await SaveAsync(refreshed).ConfigureAwait(false);
            return refreshed.AccessToken;
        }
        finally { gate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        token = null;
        loaded = true;
        SecureStorage.Default.Remove(TokenKey);
    }

    private async Task EnsureLoadedAsync()
    {
        if (loaded) return;
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (loaded) return;
            var serialized = await SecureStorage.Default.GetAsync(TokenKey).ConfigureAwait(false);
            token = string.IsNullOrWhiteSpace(serialized)
                ? null
                : JsonSerializer.Deserialize<TokenState>(serialized);
            loaded = true;
        }
        finally { gate.Release(); }
    }

    private async Task SaveAsync(TokenState value)
    {
        token = value;
        loaded = true;
        await SecureStorage.Default.SetAsync(TokenKey, JsonSerializer.Serialize(value)).ConfigureAwait(false);
    }

    private async Task<TokenState> ExchangeAsync(
        IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsync(
            "https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(values), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Google token response was empty.");
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

    private sealed record TokenState(string AccessToken, string? RefreshToken, DateTime ExpiresUtc);
    private sealed record TokenResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string? AccessToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string? RefreshToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("expires_in")] int ExpiresIn);
}
