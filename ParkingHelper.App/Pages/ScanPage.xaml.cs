using System.ComponentModel;
using ParkingHelper.App.Layout;
using Microsoft.Extensions.Logging;
using ParkingHelper.App.Services;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;

namespace ParkingHelper.App.Pages;

public partial class ScanPage : ContentPage
{
    private readonly IBarcodeScannerService scanner;
    private readonly ScanViewModel model;
    private readonly IServiceProvider services;
    private IDispatcherTimer? clock;
    private Window? owningWindow;
    private CancellationTokenSource? activation;
    private Task? saveFlow;
    private Guid? pendingPreviewId;
    private bool openingPreview;
    private bool visible;
    private bool navigating;

    public ScanPage(IBarcodeScannerService scanner, ScanViewModel model, IServiceProvider services)
    {
        this.scanner = scanner;
        this.model = model;
        this.services = services;
        InitializeComponent();
        BindingContext = model;
        PreviewHost.Content = scanner.Preview;
        DesktopSource.IsVisible = DeviceInfo.Platform == DevicePlatform.MacCatalyst;
        ScanViewport.SizeChanged += (_, _) => AdaptLayout();
    }

    private void AdaptLayout()
    {
        var width = ScanViewport.Width;
        if (width <= 0) return;
        var wide = ResponsiveLayout.IsWide(width);
        ScanContent.WidthRequest = Math.Min(width, ResponsiveLayout.ContentMaximum);
        if (ScanPanels.ColumnDefinitions.Count != (wide ? 2 : 1))
        {
            ScanPanels.ColumnDefinitions.Clear();
            ScanPanels.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            if (wide) ScanPanels.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(280)));
        }
        Grid.SetRow(ScanDetails, wide ? 0 : 1);
        Grid.SetColumn(ScanDetails, wide ? 1 : 0);
        SelectedPlateSummary.IsVisible = wide;
        CameraPanel.HeightRequest = ResponsiveLayout.CameraHeight(width, ScanViewport.Height);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        visible = true;
        scanner.Changed += OnChanged;
        scanner.Captured += OnCaptured;
        model.PropertyChanged += OnModelChanged;
        owningWindow = Window;
        if (owningWindow != null) { owningWindow.Stopped += OnStopped; owningWindow.Resumed += OnResumed; }
        clock ??= Dispatcher.CreateTimer();
        clock.Interval = TimeSpan.FromSeconds(1);
        clock.Tick += OnClock;
        clock.Start();
        OnClock(this, EventArgs.Empty);
        await StartAsync();
    }

    protected override void OnDisappearing()
    {
        visible = false;
        Stop();
        scanner.Changed -= OnChanged;
        scanner.Captured -= OnCaptured;
        model.PropertyChanged -= OnModelChanged;
        if (owningWindow != null) { owningWindow.Stopped -= OnStopped; owningWindow.Resumed -= OnResumed; }
        owningWindow = null;
        if (clock != null) { clock.Stop(); clock.Tick -= OnClock; }
        base.OnDisappearing();
    }

    private async Task StartAsync()
    {
        Stop();
        var owner = activation = new CancellationTokenSource();
        var token = owner.Token;
        try
        {
            // An accepted save completes even if the page goes away. Do not start another camera
            // session until its transaction has completed; never rebind that scan to a new plate.
            if (saveFlow != null) await saveFlow;
            token.ThrowIfCancellationRequested();
            if (pendingPreviewId is Guid savedId)
            {
                await OpenPreviewAsync(savedId);
                return;
            }
            var loaded = await model.LoadAsync();
            token.ThrowIfCancellationRequested();
            model.ClearFeedback();
            if (loaded && !model.CanScan && Window is Window window)
            {
                services.GetRequiredService<AppNavigation>().ShowSetup(window);
                return;
            }
            scanner.Cancel();
            scanner.Rescan();
            // Preload silently while the camera initializes, for prompt post-commit feedback.
            _ = services.GetRequiredService<IScanFeedbackService>().PrepareAsync();
            // Plate loading/selection precedes enabling barcode processing.
            await scanner.StartAsync(detectBarcodes: model.CanScan);
            token.ThrowIfCancellationRequested();
            Refresh();
        }
        catch (OperationCanceledException) { }
    }

    private void Stop()
    {
        activation?.Cancel();
        activation?.Dispose();
        activation = null;
        scanner.Stop();
    }

    private void OnCaptured(object? sender, ScanResult scan)
    {
        if (!visible || activation == null || saveFlow != null) return;
        saveFlow = SaveAndResumeAsync(scan, activation.Token);
    }

    private async Task SaveAndResumeAsync(ScanResult scan, CancellationToken token)
    {
        try
        {
            var outcome = await model.SaveScanAsync(scan);
            if (outcome?.Created == true)
            {
                // Release the native camera as soon as the transaction commits. No cooldown/rescan
                // runs on success, even if navigation fails or the app backgrounds during the save.
                scanner.Stop();
                pendingPreviewId = outcome.Ticket.Id;
                token.ThrowIfCancellationRequested();
                await services.GetRequiredService<IScanFeedbackService>().NotifySavedAsync(outcome, token);
                token.ThrowIfCancellationRequested();
                await OpenPreviewAsync(outcome.Ticket.Id);
                return;
            }
            token.ThrowIfCancellationRequested();
            await Task.Delay(model.HasSaveError ? 3000 : 1800, token);
            model.ClearFeedback();
            scanner.Rescan();
        }
        catch (OperationCanceledException) { }
        finally
        {
            saveFlow = null;
            Refresh();
        }
    }

    private async Task OpenPreviewAsync(Guid ticketId)
    {
        if (openingPreview) return;
        openingPreview = true;
        try
        {
            await services.GetRequiredService<AppNavigation>().ShowTicketPreviewAsync(Navigation, ticketId);
            pendingPreviewId = null;
        }
        catch (Exception exception)
        {
            services.GetRequiredService<ILogger<ScanPage>>().LogWarning(exception, "Could not open saved ticket preview");
            model.ReportPreviewFailure();
            // Keep the saved ID for navigation retry; never save the scan a second time.
            Refresh();
        }
        finally { openingPreview = false; }
    }

    private void OnClock(object? sender, EventArgs e) => DeviceTime.Text = DateTime.Now.ToString("ddd, MMM d · h:mm:ss tt");
    private void OnChanged(object? sender, EventArgs e) => Refresh();
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Refresh();
    private void Refresh()
    {
        ScanStatus.Text = !string.IsNullOrEmpty(model.Feedback) ? model.Feedback : !model.CanScan ? model.PlateStatus : scanner.Status;
        Target.Stroke = model.SaveSucceeded ? Color.FromArgb("#22C55E")
            : model.HasSaveError ? Color.FromArgb("#EF4444") : Color.FromArgb("#60A5FA");
        var processing = model.IsSaving || saveFlow != null || pendingPreviewId != null;
        RetryButton.Text = pendingPreviewId != null ? "Open saved ticket" : "Retry camera";
        RescanButton.IsEnabled = !processing;
        CancelButton.IsEnabled = !processing;
        TorchButton.IsVisible = scanner.CanUseTorch;
        TorchButton.Text = scanner.IsTorchOn ? "Torch off" : "Torch on";
        SwitchButton.IsVisible = DeviceInfo.Platform != DevicePlatform.MacCatalyst && scanner.Cameras.Count > 2;
        PermissionButton.IsVisible = scanner.NeedsPermissionSettings;
        VideoSourceName.Text = scanner.VideoSource;
    }

    private void OnPlateSelected(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: Guid id }) model.SelectPlate(id);
    }
    private void OnStopped(object? sender, EventArgs e) => Stop();
    private async void OnResumed(object? sender, EventArgs e) { if (visible) await StartAsync(); }
    private void OnRescan(object? sender, EventArgs e)
    {
        if (saveFlow != null || pendingPreviewId != null) return;
        model.ClearFeedback();
        scanner.Rescan();
    }
    private void OnCancel(object? sender, EventArgs e)
    {
        if (saveFlow != null || pendingPreviewId != null) return;
        model.ClearFeedback();
        scanner.Cancel();
    }
    private void OnTorch(object? sender, EventArgs e) => scanner.ToggleTorch();
    private async void OnRetry(object? sender, EventArgs e) => await StartAsync();
    private void OnPermissionSettings(object? sender, EventArgs e) => AppInfo.ShowSettingsUI();
    private async void OnSwitch(object? sender, EventArgs e) => await scanner.SwitchCameraAsync();
    private async void OnCameraSettings(object? sender, EventArgs e)
    {
        if (navigating) return;
        navigating = true;
        try { await Navigation.PushAsync(services.GetRequiredService<CameraSettingsPage>()); }
        finally { navigating = false; }
    }
}
