using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly IServiceProvider services;
    private readonly GoogleDriveSynchronizationService synchronization;
    private readonly IGoogleDriveAuthentication authentication;
    private bool navigating;

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
        try
        {
            await authentication.ConnectAsync();
            UpdateSyncStatus();
        }
        catch (NotSupportedException error) { await DisplayAlertAsync("Google Drive", error.Message, "OK"); }
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
