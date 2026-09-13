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
        SyncStatusLabel.Text = "Connecting to Google Drive…";
        try
        {
            if (!await authentication.ConnectAsync())
            {
                SyncStatusLabel.Text = "Google Drive connection cancelled.";
                return;
            }
            SyncStatusLabel.Text = "Google Drive connected. Synchronizing…";
            services.GetRequiredService<ILogger<SettingsPage>>().LogInformation(
                "Google Drive authentication completed; starting initial sync.");
            try
            {
                await synchronization.SynchronizeAsync();
            }
            catch (Exception error) when (error is HttpRequestException or IOException
                or TimeoutException or InvalidOperationException or System.Text.Json.JsonException
                or OperationCanceledException)
            {
                services.GetRequiredService<ILogger<SettingsPage>>().LogWarning(
                    "Initial Drive sync failed: {ErrorType}; HTTP status {Status}",
                    error.GetType().Name, (error as HttpRequestException)?.StatusCode);
                SyncStatusLabel.Text = "Google Drive connected. Sync could not complete; local data was kept.";
                await DisplayAlertAsync("Google Drive", "Connected, but sync could not complete. Please try Sync Now.", "OK");
                return;
            }
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
        if (connectingDrive) return;
        try { await synchronization.SynchronizeAsync(); }
        catch (Exception error) when (error is InvalidOperationException or IOException or TimeoutException
            or HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
        {
            services.GetRequiredService<ILogger<SettingsPage>>().LogWarning(
                "Manual Drive sync failed: {ErrorType}; HTTP status {Status}",
                error.GetType().Name, (error as HttpRequestException)?.StatusCode);
            SyncStatusLabel.Text = authentication.IsConnected
                ? "Google Drive connected. Sync could not complete; local data was kept."
                : "Google Drive disconnected.";
            await DisplayAlertAsync("Sync", "Sync could not complete. Please try again. Local data was kept.", "OK");
            return;
        }
        UpdateSyncStatus();
    }

    private async void OnDisconnectDrive(object? sender, EventArgs e)
    {
        if (connectingDrive) return;
        await authentication.DisconnectAsync();
        SyncStatusLabel.Text = "Google Drive disconnected.";
    }

    private void UpdateSyncStatus()
    {
        SyncStatusLabel.Text = authentication.IsConnected
            ? $"Google Drive connected. {synchronization.Status.Message}"
            : "Google Drive disconnected.";
    }
}
