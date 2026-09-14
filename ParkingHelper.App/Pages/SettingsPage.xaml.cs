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
        _ = services.GetRequiredService<Services.GoogleDriveDiagnosticsReport>();
        InitializeComponent();
        VersionLabel.Text = BuildIdentity.DisplayVersion;
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

    private async void OnDriveDiagnostics(object? sender, EventArgs e) => await OpenAsync<GoogleDriveDiagnosticsPage>();

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

    private async void OnAbout(object? sender, EventArgs e) => await OpenInformationAsync(
        "About Parking Helper",
        """
        Parking Helper — Scan. Save. Show.

        Keep your vehicle plates and parking tickets together. Scan tickets, review active parking, and manage archived tickets.

        Connect Google Drive to synchronize your parking data across supported devices.

        Developed by RJ Regalado
        © 2026 RJ Regalado
        Parking Helper
        All rights reserved.
        parkinghelper@2radical.dev
        www.rjregalado.com

        Technologies
        • .NET 10
        • .NET MAUI
        • C#
        • SQLite
        • ZXing.Net.MAUI
        • Google Drive API
        • Google OAuth / Identity Services
        • Android
        • iOS / iPadOS
        • Mac Catalyst

        AI-assisted development
        • OpenAI ChatGPT
        • Anthropic Claude
        • GitHub Copilot
        • Google Gemini

        All other trademarks, product names, and copyrights are the property of their respective owners.
        """);

    private async void OnPrivacyPolicy(object? sender, EventArgs e)
    {
        const string url = "https://parkinghelper.2radical.dev/privacy-policy.html";

        try
        {
            await Launcher.Default.OpenAsync(new Uri(url));
        }
        catch (Exception)
        {
            await DisplayAlert(
                "Privacy Policy",
                "Unable to open the privacy policy in your browser.",
                "OK");
        }
    }

    private async Task OpenInformationAsync(string title, string text)
    {
        if (navigating) return;
        navigating = true;
        try
        {
            await Navigation.PushAsync(new ContentPage
            {
                Title = title,
                Content = new ScrollView
                {
                    Content = new Views.ReadableContentView
                    {
                        Content = new VerticalStackLayout
                        {
                            Padding = 24, Spacing = 16,
                            Children =
                            {
                                new Label { Text = "Parking Helper", Style = (Style)Application.Current!.Resources["PageHeading"] },
                                new Label { Text = "Scan. Save. Show.", FontSize = 20, TextColor = (Color)Application.Current!.Resources["Primary"] },
                                new Label { Text = text.Replace("Parking Helper — Scan. Save. Show.\n\n", ""), FontSize = 16, LineHeight = 1.3 }
                            }
                        }
                    }
                }
            });
        }
        finally { navigating = false; }
    }

    private async void OnConnectDrive(object? sender, EventArgs e) => await drive.ConnectAsync();
    private async void OnSyncNow(object? sender, EventArgs e) => await drive.SyncAsync();
    private async void OnDisconnectDrive(object? sender, EventArgs e) => await drive.DisconnectAsync();

    private void UpdateSyncStatus()
    {
        ConnectedDriveRows.IsVisible = DisconnectDriveRows.IsVisible = drive.IsConnected;
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
        AccountRow.IsVisible = AccountNameLabel.IsVisible || AccountEmailLabel.IsVisible;
        SyncStatusLabel.Text = drive.Message;
        SyncStatusLabel.IsVisible = !string.IsNullOrWhiteSpace(drive.Message) && drive.Message != "Connected";
        LastSyncLabel.IsVisible = drive.IsConnected;
        var local = drive.LastSuccessfulSync?.ToLocalTime();
        LastSyncLabel.Text = local is null ? "Never"
            : local.Value.Date == DateTime.Today ? $"Today, {local:t}"
            : $"{local:g}";
        DriveActivity.IsVisible = drive.IsBusy;
        DriveActivity.IsRunning = drive.IsBusy;
        DriveStatusRow.IsVisible = drive.IsBusy || SyncStatusLabel.IsVisible;
    }
}
