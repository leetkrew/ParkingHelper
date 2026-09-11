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
        builder.Services.AddSingleton<ParkingService>();
        builder.Services.AddSingleton<IPlateService, PlateService>();
        builder.Services.AddSingleton<ITicketService, TicketService>();
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