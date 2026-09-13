using ParkingHelper.App.Services;

namespace ParkingHelper.App;

public sealed class AndroidGoogleDriveOAuthConfiguration : IGoogleDriveOAuthConfiguration
{
    public string ClientId => GoogleAuthGeneratedConfiguration.AndroidClientId;
    public string RedirectUri => "";
    public string PackageName => Microsoft.Maui.ApplicationModel.AppInfo.Current.PackageName;
    public string SigningCertificateSha1 => "";
}
