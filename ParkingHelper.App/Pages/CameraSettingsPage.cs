using ParkingHelper.App.Services;
using ParkingHelper.App.Views;

namespace ParkingHelper.App.Pages;

public sealed class CameraSettingsPage : ContentPage
{
    private readonly IBarcodeScannerService scanner;
    private readonly Picker picker = new() { Title = "Camera", MinimumHeightRequest = 52 };
    private readonly Label status = new();
    private readonly Button permission = new() { Text = "Open system settings", IsVisible = false };
    private Window? owningWindow;
    private bool updating;
    private bool visible;

    public CameraSettingsPage(IBarcodeScannerService scanner)
    {
        this.scanner = scanner;
        Title = "Camera Settings";
        var retry = new Button { Text = "Refresh cameras", MinimumHeightRequest = 52 };
        retry.Clicked += async (_, _) => await scanner.StartAsync(detectBarcodes: false);
        permission.Clicked += (_, _) => AppInfo.ShowSettingsUI();
        picker.SelectedIndexChanged += async (_, _) =>
        {
            if (!updating && picker.SelectedItem is ScannerCamera selected) await scanner.SelectCameraAsync(selected.Id);
        };
        Content = new ScrollView { Content = new ReadableContentView { Content = new VerticalStackLayout
        {
            Padding = 24, Spacing = 16, MaximumWidthRequest = 720,
            Children =
            {
                new Label { Text = DeviceInfo.Platform == DevicePlatform.MacCatalyst ? "Video Source" : "Camera", FontSize = 28, FontAttributes = FontAttributes.Bold },
                new Label { Text = "Choose an available camera or let Automatic select the system camera. Camera access is needed to discover video sources." },
                new ContentView { Content = scanner.Preview, HeightRequest = 220, BackgroundColor = Colors.Black },
                picker, status, retry, permission,
                new Label { Text = "Only devices reported by the scanner library appear here. An unavailable preference falls back to Automatic." }
            }
        }}};
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        visible = true;
        scanner.Changed += Refresh;
        owningWindow = Window;
        if (owningWindow != null) { owningWindow.Stopped += OnStopped; owningWindow.Resumed += OnResumed; }
        await scanner.StartAsync(detectBarcodes: false);
    }

    protected override void OnDisappearing()
    {
        visible = false;
        scanner.Changed -= Refresh;
        scanner.Stop();
        if (owningWindow != null) { owningWindow.Stopped -= OnStopped; owningWindow.Resumed -= OnResumed; }
        owningWindow = null;
        base.OnDisappearing();
    }
    private void OnStopped(object? sender, EventArgs e) => scanner.Stop();
    private async void OnResumed(object? sender, EventArgs e) { if (visible) await scanner.StartAsync(detectBarcodes: false); }
    private void Refresh(object? sender, EventArgs e)
    {
        updating = true;
        // Avoid closing an open native picker on every watchdog tick.
        if (picker.ItemsSource is not List<ScannerCamera> old || !old.SequenceEqual(scanner.Cameras))
            picker.ItemsSource = scanner.Cameras.ToList();
        picker.SelectedItem = scanner.Cameras.FirstOrDefault(c => c.Id == scanner.SelectedCameraId);
        status.Text = scanner.Status;
        permission.IsVisible = scanner.NeedsPermissionSettings;
        updating = false;
    }
}
