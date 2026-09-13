using System.ComponentModel;
using ParkingHelper.App.Layout;
using Microsoft.Extensions.Logging;
using ParkingHelper.App.Services;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Pages;

public partial class ScanPage : ContentPage
{
    private readonly IBarcodeScannerService scanner;
    private readonly ScanPreviewInteraction previewInteraction = new(TimeProvider.System);
    private readonly ScanViewModel model;
    private readonly IServiceProvider services;
    private IDispatcherTimer? clock;
    private Window? owningWindow;
    private CancellationTokenSource? activation;
    private Task? saveFlow;
    private Guid? pendingPreviewId;
    private bool openingPreview;
    private bool restartingCamera;
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
            await previewInteraction.InitializeAsync(async () =>
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
            }, token);
            Refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            services.GetRequiredService<ILogger<ScanPage>>().LogWarning(exception, "Could not start scanner");
            ScanStatus.Text = "Couldn’t start the camera. Tap the preview to retry.";
        }
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
                // The accepted result already pauses detection. Keep recovery taps blocked through
                // feedback, stop the camera, then navigate using the committed ticket ID.
                pendingPreviewId = outcome.Ticket.Id;
                try
                {
                    token.ThrowIfCancellationRequested();
                    await services.GetRequiredService<IScanFeedbackService>().NotifySavedAsync(outcome, token);
                }
                finally { scanner.Stop(); }
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
        ScanStatus.Text = !string.IsNullOrEmpty(model.Feedback) ? model.Feedback : restartingCamera ? "Restarting camera…" : !model.CanScan ? model.PlateStatus : scanner.Status;
        Target.Stroke = model.SaveSucceeded ? Color.FromArgb("#22C55E")
            : model.HasSaveError ? Color.FromArgb("#EF4444") : Color.FromArgb("#60A5FA");
        var processing = model.IsSaving || saveFlow != null || pendingPreviewId != null;
        ReloadPlatesButton.IsVisible = !model.CanScan && !processing;
        OpenSavedButton.IsVisible = pendingPreviewId != null;
        PreviewHint.Text = scanner.HasCameraError || !scanner.HasActiveVideoStream
            ? "Camera unavailable — tap to retry" : "Tap camera to rescan";
        CancelButton.IsEnabled = !processing && !previewInteraction.IsBusy;
        SwitchButton.IsEnabled = !processing && !previewInteraction.IsBusy;
        TorchButton.IsVisible = scanner.CanUseTorch;
        TorchButton.Text = scanner.IsTorchOn ? "Torch off" : "Torch on";
        SwitchButton.IsVisible = DeviceInfo.Platform != DevicePlatform.MacCatalyst && scanner.Cameras.Count > 2;
        PermissionButton.IsVisible = scanner.NeedsPermissionSettings;
        VideoSourceName.Text = scanner.VideoSource;
    }

    private async void OnPreviewTapped(object? sender, TappedEventArgs e)
    {
        if (activation == null) return;
        var token = activation.Token;
        string? failure = null;
        try
        {
            await previewInteraction.TapAsync(
                () => visible && !navigating && model.CanScan && !model.IsSaving
                    && saveFlow == null && pendingPreviewId == null,
                () => !scanner.HasCameraError && scanner.HasActiveVideoStream,
                () => { model.ClearFeedback(); scanner.Rescan(); },
                async () =>
                {
                    // Do not reload plates or reset the preferred source during a camera retry.
                    restartingCamera = true;
                    try
                    {
                        model.ClearFeedback();
                        scanner.Stop();
                        scanner.Rescan();
                        ScanStatus.Text = "Restarting camera…";
                        await scanner.StartAsync(detectBarcodes: model.CanScan);
                        token.ThrowIfCancellationRequested();
                    }
                    finally { restartingCamera = false; }
                }, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            services.GetRequiredService<ILogger<ScanPage>>().LogWarning(exception, "Camera preview recovery failed");
            failure = "Couldn’t restart the camera. Tap the preview to retry.";
        }
        finally
        {
            Refresh();
            if (failure != null) ScanStatus.Text = failure;
        }
    }

    private void OnPlateSelected(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: Guid id }) model.SelectPlate(id);
    }
    private void OnStopped(object? sender, EventArgs e) => Stop();
    private async void OnResumed(object? sender, EventArgs e) { if (visible) await StartAsync(); }
    private void OnCancel(object? sender, EventArgs e)
    {
        if (previewInteraction.IsBusy || saveFlow != null || pendingPreviewId != null) return;
        model.ClearFeedback();
        scanner.Cancel();
    }
    private void OnTorch(object? sender, EventArgs e) => scanner.ToggleTorch();
    private async void OnRetry(object? sender, EventArgs e) => await StartAsync();
    private void OnPermissionSettings(object? sender, EventArgs e) => AppInfo.ShowSettingsUI();
    private async void OnSwitch(object? sender, EventArgs e)
    {
        if (previewInteraction.IsBusy || saveFlow != null || pendingPreviewId != null) return;
        await scanner.SwitchCameraAsync();
    }
    private async void OnCameraSettings(object? sender, EventArgs e)
    {
        if (navigating) return;
        navigating = true;
        try { await Navigation.PushAsync(services.GetRequiredService<CameraSettingsPage>()); }
        finally { navigating = false; }
    }
}
