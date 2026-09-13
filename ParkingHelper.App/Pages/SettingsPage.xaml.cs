using ParkingHelper.Core.Services;
using Microsoft.Extensions.Logging;

namespace ParkingHelper.App.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly IServiceProvider services;
    private readonly GoogleDriveSynchronizationService synchronization;
    private readonly IGoogleDriveAuthentication authentication;
    private bool navigating;
    private bool connectingDrive;

    public SettingsPage(IServiceProvider services, GoogleDriveSynchronizationService synchronization,
        IGoogleDriveAuthentication authentication)
    {
        this.services = services;
        this.synchronization = synchronization;
        this.authentication = authentication;
        InitializeComponent();
        UpdateSyncStatus();
    }

    private async void OnScannerSettings(object? sender, EventArgs e) => await OpenAsync<ScannerSettingsPage>();
    private async void OnCameraSettings(object? sender, EventArgs e) => await OpenAsync<CameraSettingsPage>();
    private async Task OpenAsync<T>() where T : Page
    {
        if (navigating) return;
        navigating = true;
        try { await Navigation.PushAsync(services.GetRequiredService<T>()); }
        finally { navigating = false; }
    }

    private async void OnManagePlates(object? sender, EventArgs e)
    {
        if (navigating) return;
        navigating = true;
        try { await Navigation.PushAsync(services.GetRequiredService<ManagePlatesPage>()); }
        finally { navigating = false; }
    }

    private async void OnConnectDrive(object? sender, EventArgs e)
    {
        if (connectingDrive) return;
        connectingDrive = true;
        try
        {
            await authentication.ConnectAsync();
            UpdateSyncStatus();
        }
        catch (OperationCanceledException)
        {
            SyncStatusLabel.Text = "Google Drive connection cancelled.";
        }
#if ANDROID
        catch (Android.Gms.Common.Apis.ApiException error) when (error.StatusCode == 16)
        {
            SyncStatusLabel.Text = "Google Drive connection cancelled.";
        }
        catch (Android.Gms.Common.Apis.ApiException error)
        {
            // Log only the numeric status and a recognized error marker. Native
            // exception messages may contain account or authorization details.
            var unregistered = error.Message?.Contains("UNREGISTERED_ON_API_CONSOLE", StringComparison.Ordinal) == true;
            services.GetRequiredService<ILogger<SettingsPage>>().LogWarning(
                "Google authorization result failed: status {Status}; unregistered {Unregistered}",
                error.StatusCode, unregistered);
            SyncStatusLabel.Text = "Google Drive disconnected.";
            await DisplayAlertAsync("Google Drive", unregistered
                ? "This Android build is not registered for Google sign-in. Check its package name and signing certificate in Google Cloud Console."
                : "Google authorization could not complete. Please try again.", "OK");
        }
#endif
        catch (NotSupportedException error) { await DisplayAlertAsync("Google Drive", error.Message, "OK"); }
        finally { connectingDrive = false; }
    }

    private async void OnSyncNow(object? sender, EventArgs e)
    {
        try { await synchronization.SynchronizeAsync(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException)
        {
            await DisplayAlertAsync("Sync", error.Message, "OK");
        }
        UpdateSyncStatus();
    }

    private async void OnDisconnectDrive(object? sender, EventArgs e)
    {
        await authentication.DisconnectAsync();
        SyncStatusLabel.Text = "Google Drive disconnected.";
    }

    private void UpdateSyncStatus()
    {
        SyncStatusLabel.Text = synchronization.Status.Message;
    }
}
