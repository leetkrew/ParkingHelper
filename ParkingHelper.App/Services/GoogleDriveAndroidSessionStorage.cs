using System.Text.Json;
using Microsoft.Maui.Storage;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Services;

// GIS retains the refresh authorization in Google Play services; no refresh token is
// exposed to this app. Persist the granted session securely for process restoration.
public sealed class GoogleDriveAndroidSessionStorage(ISecureStorage storage)
{
    private const string Key = "parkinghelper.google-drive.android-session";
    public sealed record Session(string AccessToken, GoogleDriveAccount? Account);

    public async Task<Session?> ReadAsync()
    {
        var value = await storage.GetAsync(Key);
        if (string.IsNullOrWhiteSpace(value)) return null;
        var session = JsonSerializer.Deserialize<Session>(value);
        return string.IsNullOrWhiteSpace(session?.AccessToken) ? null : session;
    }

    public async Task SaveAsync(string accessToken, GoogleDriveAccount? account)
    {
        try { await storage.SetAsync(Key, JsonSerializer.Serialize(new Session(accessToken, account))); }
        catch (Exception error) { throw new GoogleDriveTokenStorageException(error); }
    }

    public void Clear()
    {
        try { storage.Remove(Key); }
        catch (Exception)
        {
            throw new GoogleDriveDisconnectException("Could not clear saved Google credentials. Please try Disconnect again before restarting the app.");
        }
    }
}
