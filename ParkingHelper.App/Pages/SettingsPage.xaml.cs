using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Pages;

public partial class SettingsPage : ContentPage
{
    private readonly IServiceProvider services;
    private readonly GoogleDriveConnection drive;
    private bool navigating;

    public SettingsPage(IServiceProvider services, GoogleDriveConnection drive)
    {
        this.services = services;
        this.drive = drive;
        InitializeComponent();
        UpdateSyncStatus();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        drive.Changed += OnDriveChanged;
        UpdateSyncStatus();
        await drive.InitializeAsync();
    }

    protected override void OnDisappearing()
    {
        drive.Changed -= OnDriveChanged;
        base.OnDisappearing();
    }

    private void OnDriveChanged() => MainThread.BeginInvokeOnMainThread(UpdateSyncStatus);

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

    private async void OnConnectDrive(object? sender, EventArgs e) => await drive.ConnectAsync();
    private async void OnSyncNow(object? sender, EventArgs e) => await drive.SyncAsync();
    private async void OnDisconnectDrive(object? sender, EventArgs e) => await drive.DisconnectAsync();

    private void UpdateSyncStatus()
    {
        ConnectDriveButton.IsVisible = !drive.IsConnected;
        ConnectDriveButton.IsEnabled = !drive.IsBusy;
        SyncNowButton.IsVisible = drive.IsConnected;
        SyncNowButton.IsEnabled = drive.IsConnected && !drive.IsBusy;
        DisconnectDriveButton.IsVisible = drive.IsConnected;
        DisconnectDriveButton.IsEnabled = drive.IsConnected;
        AccountNameLabel.Text = drive.Account?.Name;
        AccountNameLabel.IsVisible = !string.IsNullOrWhiteSpace(AccountNameLabel.Text);
        AccountEmailLabel.Text = drive.Account?.Email;
        AccountEmailLabel.IsVisible = !string.IsNullOrWhiteSpace(AccountEmailLabel.Text);
        SyncStatusLabel.Text = drive.Message;
        SyncStatusLabel.IsVisible = !string.IsNullOrWhiteSpace(drive.Message);
        LastSyncLabel.IsVisible = drive.IsConnected;
        var local = drive.LastSuccessfulSync?.ToLocalTime();
        LastSyncLabel.Text = local is null ? "Last successful sync: Never"
            : local.Value.Date == DateTime.Today ? $"Last successful sync: Today, {local:t}"
            : $"Last successful sync: {local:g}";
        DriveActivity.IsVisible = drive.IsBusy;
        DriveActivity.IsRunning = drive.IsBusy;
    }
}
