using ParkingHelper.App.ViewModels;
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

    public TicketPreviewPage(TicketPreviewViewModel model, AppNavigation routes, IWalletLauncherService wallet)
    {
        this.model = model;
        this.routes = routes;
        this.wallet = wallet;
        InitializeComponent();
        BindingContext = model;
    }

    // The navigation boundary carries only the permanent ID, never a database record.
    public void SetTicketId(Guid id) => ticketId = id;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
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
        base.OnDisappearing();
    }

    private void OnTick(object? sender, EventArgs e) => model.UpdateDuration();
    private void OnStopped(object? sender, EventArgs e) => timer?.Stop();
    private void OnResumed(object? sender, EventArgs e) { model.UpdateDuration(); timer?.Start(); }
    private async void OnEditPlate(object? sender, EventArgs e) => await model.BeginEditPlateAsync();
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
            WalletButton.IsVisible = wallet.IsAvailable;
            await DisplayAlertAsync("Wallet unavailable", "Wallet couldn’t be opened on this device.", "OK");
        }
    }
    private async void OnRetry(object? sender, EventArgs e) => await model.LoadAsync(ticketId);
    private async void OnDone(object? sender, EventArgs e)
    {
        if (leaving) return;
        leaving = true;
        try { await Navigation.PopAsync(); }
        catch { await DisplayAlertAsync("Navigation unavailable", "Couldn’t close the preview. Please try again.", "OK"); }
        finally { leaving = false; }
    }
}
