using ParkingHelper.App.Services;

namespace ParkingHelper.App;

public sealed class MacCatalystGoogleDriveOAuthConfiguration : IGoogleDriveOAuthConfiguration
{
    public string ClientId => GoogleAuthGeneratedConfiguration.MacCatalystClientId;
    public string RedirectUri => $"{GoogleAuthGeneratedConfiguration.MacCatalystReversedClientId}:/oauthredirect/google";
    public string PackageName => "";
    public string SigningCertificateSha1 => "";
}
