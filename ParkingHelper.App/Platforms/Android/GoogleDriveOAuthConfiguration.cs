using ParkingHelper.App.Services;

namespace ParkingHelper.App;

public sealed class AndroidGoogleDriveOAuthConfiguration : IGoogleDriveOAuthConfiguration
{
    public string ClientId => GoogleAuthGeneratedConfiguration.AndroidClientId;
    public string RedirectUri => "";
    public string PackageName => "dev.2radical.parkinghelper";
    public string SigningCertificateSha1 => "";
}
