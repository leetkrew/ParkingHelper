namespace ParkingHelper.App.Services;

public interface IWalletLauncherService
{
    bool IsAvailable { get; }
    Task<bool> OpenAsync();
}

public sealed class WalletLauncherService : IWalletLauncherService
{
#if ANDROID
    private const string WalletPackage = "com.google.android.apps.walletnfcrel";
#endif
    public bool IsAvailable
    {
        get
        {
#if ANDROID
            try
            {
                using var intent = Android.App.Application.Context.PackageManager?.GetLaunchIntentForPackage(WalletPackage);
                return intent != null;
            }
            catch { return false; }
#else
            // Apple documents opening a specific PKPass, not a generic Wallet destination.
            // Parking Helper has no Wallet pass. Do not use private shoebox:// schemes.
            return false;
#endif
        }
    }

    public Task<bool> OpenAsync()
    {
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            using var intent = context.PackageManager?.GetLaunchIntentForPackage(WalletPackage);
            if (intent == null) return Task.FromResult(false);
            intent.AddFlags(Android.Content.ActivityFlags.NewTask);
            context.StartActivity(intent);
            return Task.FromResult(true);
        }
        catch { return Task.FromResult(false); }
#else
        return Task.FromResult(false);
#endif
    }
}
