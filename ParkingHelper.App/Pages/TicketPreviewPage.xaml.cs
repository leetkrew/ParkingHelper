using ParkingHelper.App.ViewModels;
using ParkingHelper.App.Layout;
using ParkingHelper.App.Services;
using ParkingHelper.Core.Models;

namespace ParkingHelper.App.Pages;

public partial class TicketPreviewPage : ContentPage
{
    private readonly TicketPreviewViewModel model;
    private readonly AppNavigation routes;
    private readonly IWalletLauncherService wallet;
    private Guid ticketId;
    private IDispatcherTimer? timer;
    private Window? owningWindow;
    private bool leaving;
    private bool editOnOpen;

    public TicketPreviewPage(TicketPreviewViewModel model, AppNavigation routes, IWalletLauncherService wallet)
    {
        this.model = model;
        this.routes = routes;
        this.wallet = wallet;
        InitializeComponent();
        BindingContext = model;
        TicketViewport.SizeChanged += (_, _) => TicketBarcode.HeightRequest = ResponsiveLayout.IsWide(TicketViewport.Width) ? 360 : 260;
    }

    // The navigation boundary carries only the permanent ID, never a database record.
    public void SetTicketId(Guid id) => ticketId = id;

    public void EditOnOpen() => editOnOpen = true;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        UpdateWalletAvailability();
        timer ??= Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += OnTick;
        timer.Start();
        owningWindow = Window;
        if (owningWindow != null) { owningWindow.Stopped += OnStopped; owningWindow.Resumed += OnResumed; }
        await model.LoadAsync(ticketId);
        if (editOnOpen)
        {
            editOnOpen = false;
            await model.BeginEditPlateAsync();
        }
    }

    protected override void OnDisappearing()
    {
        if (timer != null) { timer.Stop(); timer.Tick -= OnTick; }
        if (owningWindow != null) { owningWindow.Stopped -= OnStopped; owningWindow.Resumed -= OnResumed; }
        owningWindow = null;
        base.OnDisappearing();
    }

    private void UpdateWalletAvailability()
    {
        WalletButton.IsEnabled = wallet.IsAvailable;
        SemanticProperties.SetHint(WalletButton, WalletButton.IsEnabled ? "Open your wallet app" : "Wallet is unavailable on this device");
    }

    private void OnTick(object? sender, EventArgs e) => model.UpdateDuration();
    private void OnStopped(object? sender, EventArgs e) => timer?.Stop();
    private void OnResumed(object? sender, EventArgs e) { UpdateWalletAvailability(); model.UpdateDuration(); timer?.Start(); }
    private async void OnEditPlate(object? sender, EventArgs e) => await model.BeginEditPlateAsync();
    private void OnSelectPlate(object? sender, EventArgs e)
    {
        if (sender is Button { CommandParameter: Guid id }) model.SelectPlate(id);
    }
    private async void OnConfirmPlate(object? sender, EventArgs e) => await model.ConfirmPlateAsync();
    private void OnCancelPlate(object? sender, EventArgs e) => model.CancelEditPlate();
    private async void OnArchive(object? sender, EventArgs e) => await model.ChangeStateAsync(ParkingTicketState.Archived);
    private async void OnRestore(object? sender, EventArgs e) => await model.ChangeStateAsync(ParkingTicketState.Active);
    private async void OnDelete(object? sender, EventArgs e)
    {
        if (leaving || !model.CanAct) return;
        leaving = true;
        try
        {
            if (await DisplayAlertAsync("Delete this ticket?", "This ticket will be removed from Parking Helper.", "Delete", "Cancel")
                && await model.ChangeStateAsync(ParkingTicketState.Deleted)) await Navigation.PopAsync();
        }
        catch { await DisplayAlertAsync("Navigation unavailable", "The ticket was deleted. Use Back to return to your tickets.", "OK"); }
        finally { leaving = false; }
    }
    private async void OnFullScreen(object? sender, EventArgs e)
    {
        if (leaving || !model.CanAct) return;
        leaving = true;
        try { await routes.ShowFullScreenTicketAsync(Navigation, ticketId); }
        catch { await DisplayAlertAsync("Preview unavailable", "Couldn’t open full screen. Please try again.", "OK"); }
        finally { leaving = false; }
    }
    private async void OnWallet(object? sender, EventArgs e)
    {
        if (!await wallet.OpenAsync())
        {
            UpdateWalletAvailability();
            await DisplayAlertAsync("Wallet unavailable", "Wallet couldn’t be opened on this device.", "OK");
        }
    }
    private async void OnRetry(object? sender, EventArgs e) => await model.LoadAsync(ticketId);
    private async void OnDetails(object? sender, EventArgs e)
    {
        if (leaving || !model.CanAct) return;
        leaving = true;
        try
        {
            await Navigation.PushAsync(new ContentPage
            {
                Title = "Ticket Details",
                Content = new ScrollView
                {
                    Content = new Label { Text = model.Details, Padding = new Thickness(24), FontSize = 18 }
                }
            });
        }
        catch { await DisplayAlertAsync("Details unavailable", "Couldn’t open ticket details. Please try again.", "OK"); }
        finally { leaving = false; }
    }
}
