using System.Net.Http.Headers;
using System.Net.Http.Json;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Services;

public static class GoogleDriveIdentity
{
    public const string DriveScope = "https://www.googleapis.com/auth/drive.appdata";
    public static readonly string[] Scopes = ["openid", "profile", "email", DriveScope];

    public static async Task<GoogleDriveAccount?> ReadAsync(HttpClient http, string accessToken,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadFromJsonAsync<GoogleDriveAccount>(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static async Task<bool> RevokeAsync(HttpClient http, string token, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await http.PostAsync("https://oauth2.googleapis.com/revoke",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }), timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException) { return false; }
        catch (OperationCanceledException) { return false; }
    }
}
