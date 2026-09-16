using ParkingHelper.App.Services;
using ParkingHelper.App.Layout;
using ParkingHelper.App.ViewModels;

namespace ParkingHelper.App.Pages;

public partial class FullScreenTicketPage : ContentPage
{
    private readonly TicketPreviewViewModel model;
    private readonly IWalletLauncherService wallet;
    private readonly IScreenAwakeService screen;
    private IDisposable? awakeLease;
    private IDispatcherTimer? timer;
    private Window? owningWindow;
    private Guid ticketId;
    private bool leaving;

    public FullScreenTicketPage(TicketPreviewViewModel model, IWalletLauncherService wallet, IScreenAwakeService screen)
    {
        InitializeComponent();
        BindingContext = this.model = model;
        this.wallet = wallet;
        this.screen = screen;
        PresentationViewport.SizeChanged += (_, _) => AdaptLayout();
    }
    private void AdaptLayout()
    {
        var width = PresentationViewport.Width;
        var height = PresentationViewport.Height;
        if (width <= 0 || height <= 0) return;
        var landscape = ResponsiveLayout.UseLandscapePresentation(width, height);
        PresentationContent.WidthRequest = Math.Min(width, 1120);
        if (PresentationContent.ColumnDefinitions.Count != (landscape ? 2 : 1))
        {
            PresentationContent.ColumnDefinitions.Clear();
            PresentationContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            if (landscape) PresentationContent.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }
        Grid.SetRow(PresentationIdentity, 0);
        Grid.SetColumn(PresentationIdentity, landscape ? 1 : 0);
        Grid.SetRow(PresentationBarcode, landscape ? 0 : 1);
        Grid.SetRowSpan(PresentationBarcode, landscape ? 2 : 1);
        Grid.SetRow(PresentationDetails, landscape ? 1 : 2);
        Grid.SetColumn(PresentationDetails, landscape ? 1 : 0);
        // Give square codes the full available column width; the renderer retains
        // each original symbology's proportions and quiet space.
        var barcodeWidth = landscape ? (Math.Min(width, 1120) - 76) / 2 : width - 48;
        PresentationBarcode.HeightRequest = Math.Max(
            ResponsiveLayout.PresentationBarcodeHeight(width, height), Math.Min(barcodeWidth, 640));
        PresentationPlate.FontSize = ResponsiveLayout.IsWide(width) ? 80 : width < 360 ? 52 : 64;
    }

    public void SetTicketId(Guid id) => ticketId = id;
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        awakeLease ??= screen.Acquire();
        WalletButton.IsVisible = wallet.IsAvailable;
        timer ??= Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += OnTick;
        timer.Start();
        owningWindow = Window;
        if (owningWindow != null) { owningWindow.Stopped += OnStopped; owningWindow.Resumed += OnResumed; }
        await model.LoadAsync(ticketId);
    }
    protected override void OnDisappearing()
    {
        if (timer != null) { timer.Stop(); timer.Tick -= OnTick; }
        if (owningWindow != null) { owningWindow.Stopped -= OnStopped; owningWindow.Resumed -= OnResumed; }
        owningWindow = null;
        ReleaseScreen();
        base.OnDisappearing();
    }
    private void OnTick(object? sender, EventArgs e) => model.UpdateDuration();
    private void OnStopped(object? sender, EventArgs e) { timer?.Stop(); ReleaseScreen(); }
    private void OnResumed(object? sender, EventArgs e)
    {
        awakeLease ??= screen.Acquire();
        WalletButton.IsVisible = wallet.IsAvailable;
        model.UpdateDuration();
        timer?.Start();
    }
    private void ReleaseScreen() { awakeLease?.Dispose(); awakeLease = null; }
    private async void OnRetry(object? sender, EventArgs e) => await model.LoadAsync(ticketId);
    private async void OnWallet(object? sender, EventArgs e)
    {
        if (!await wallet.OpenAsync())
        {
            WalletButton.IsVisible = wallet.IsAvailable;
            await DisplayAlertAsync("Wallet unavailable", "Wallet couldn’t be opened on this device.", "OK");
        }
    }
    private async void OnArchive(object? sender, EventArgs e) =>
        await model.ChangeStateAsync(ParkingHelper.Core.Models.ParkingTicketState.Archived);

    private async void OnDone(object? sender, EventArgs e)
    {
        if (leaving) return;
        leaving = true;
        try { await Navigation.PopAsync(); }
        catch { await DisplayAlertAsync("Navigation unavailable", "Couldn’t close the preview. Please try again.", "OK"); }
        finally { leaving = false; }
    }
}
