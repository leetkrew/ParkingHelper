using ParkingHelper.App.Services;

namespace ParkingHelper.App;

public sealed class IosGoogleDriveOAuthConfiguration : IGoogleDriveOAuthConfiguration
{
    public string ClientId => GoogleAuthGeneratedConfiguration.IosClientId;
    public string RedirectUri => $"{GoogleAuthGeneratedConfiguration.IosReversedClientId}:/oauthredirect/google";
    public string PackageName => "";
    public string SigningCertificateSha1 => "";
}
