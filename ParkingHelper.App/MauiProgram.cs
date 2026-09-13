using ParkingHelper.Core.Services;
using ParkingHelper.App.Pages;
using ParkingHelper.App.Services;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Persistence;
using Microsoft.Extensions.Logging;
using ZXing.Net.Maui.Controls;

namespace ParkingHelper.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .UseBarcodeReader()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<IParkingRepository>(_ => new SqliteParkingRepository(
            Path.Combine(FileSystem.Current.AppDataDirectory, "parking-helper.db3")));
#if ANDROID
        builder.Services.AddSingleton<IGoogleDriveOAuthConfiguration, AndroidGoogleDriveOAuthConfiguration>();
        builder.Services.AddSingleton<AndroidGoogleDriveOAuthAuthentication>();
        builder.Services.AddSingleton<IGoogleDriveAuthentication>(sp =>
            sp.GetRequiredService<AndroidGoogleDriveOAuthAuthentication>());
        builder.Services.AddSingleton<IGoogleDriveAccessTokenProvider>(sp =>
            sp.GetRequiredService<AndroidGoogleDriveOAuthAuthentication>());
#elif IOS
        builder.Services.AddSingleton<IGoogleDriveOAuthConfiguration, IosGoogleDriveOAuthConfiguration>();
#elif MACCATALYST
        builder.Services.AddSingleton<IGoogleDriveOAuthConfiguration, MacCatalystGoogleDriveOAuthConfiguration>();
#else
        builder.Services.AddSingleton<IGoogleDriveAuthentication, UnconfiguredGoogleDriveAuthentication>();
        builder.Services.AddSingleton<IGoogleDriveAccessTokenProvider>(sp =>
            sp.GetRequiredService<IGoogleDriveAuthentication>());
#endif
        builder.Services.AddSingleton<HttpClient>();
#if IOS || MACCATALYST
        builder.Services.AddSingleton<GoogleDriveOAuthAuthentication>();
        builder.Services.AddSingleton<IGoogleDriveAuthentication>(sp =>
            sp.GetRequiredService<GoogleDriveOAuthAuthentication>());
        builder.Services.AddSingleton<IGoogleDriveAccessTokenProvider>(sp =>
            sp.GetRequiredService<GoogleDriveOAuthAuthentication>());
#endif
        builder.Services.AddSingleton<IGoogleDriveTransport>(sp =>
            new GoogleDriveRestTransport(
                sp.GetRequiredService<HttpClient>(),
                sp.GetRequiredService<IGoogleDriveAccessTokenProvider>()));
        builder.Services.AddSingleton<ISyncNetworkStatus, MauiSyncNetworkStatus>();
        builder.Services.AddSingleton<IConcurrencyRetryPolicy, ConcurrencyRetryPolicy>();
        builder.Services.AddSingleton<GoogleDriveSynchronizationService>();
        builder.Services.AddSingleton<ISynchronizationService>(sp => sp.GetRequiredService<GoogleDriveSynchronizationService>());
        builder.Services.AddSingleton<ISynchronizationTrigger, SynchronizationTrigger>();
        builder.Services.AddSingleton<ParkingService>();
        builder.Services.AddSingleton<IPlateService, PlateService>();
        builder.Services.AddSingleton<ITicketService, TicketService>();
        builder.Services.AddSingleton<IArchiveExportService, ArchiveExportService>();
        builder.Services.AddSingleton<IArchiveFileShareService, ArchiveFileShareService>();
        builder.Services.AddSingleton<ISelectedPlatePreference, SelectedPlatePreference>();
        builder.Services.AddSingleton<AppNavigation>();
        builder.Services.AddSingleton<IPreferences>(Preferences.Default);
        builder.Services.AddSingleton<IScannerSettingsService, ScannerSettingsService>();
        builder.Services.AddSingleton<IScanFeedbackSettings, ScanFeedbackSettings>();
        builder.Services.AddSingleton<IScanFeedbackPlayer, NativeScanFeedbackPlayer>();
        builder.Services.AddSingleton<IScanFeedbackService, ScanFeedbackService>();
        builder.Services.AddSingleton<ScanSession>();
        builder.Services.AddTransient<IBarcodeScannerService, BarcodeScannerService>();
        builder.Services.AddTransient<ScanViewModel>();
        builder.Services.AddTransient<ScanPage>();
        builder.Services.AddTransient<TicketPreviewViewModel>();
        builder.Services.AddTransient<TicketPreviewPage>();
        builder.Services.AddTransient<TicketsViewModel>();
        builder.Services.AddTransient<TicketsPage>();
        builder.Services.AddTransient<ArchiveExportViewModel>();
        builder.Services.AddTransient<ArchiveExportPage>();
        builder.Services.AddTransient<FullScreenTicketPage>();
        builder.Services.AddSingleton<IBarcodeRenderingService, BarcodeRenderingService>();
        builder.Services.AddSingleton<IWalletLauncherService, WalletLauncherService>();
        builder.Services.AddSingleton<IScreenAwakeService, ScreenAwakeService>();
        builder.Services.AddTransient<ScannerSettingsPage>();
        builder.Services.AddTransient<CameraSettingsPage>();
        builder.Services.AddTransient<PlateEditorViewModel>();
        builder.Services.AddTransient<StartupPage>();
        builder.Services.AddTransient<InitialSetupPage>();
        builder.Services.AddTransient<ManagePlatesPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<AppShell>();

        return builder.Build();
    }
}