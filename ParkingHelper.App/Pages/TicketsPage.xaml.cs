using ParkingHelper.App.Services;
using ParkingHelper.App.Layout;
using ParkingHelper.App.ViewModels;

namespace ParkingHelper.App.Pages;

public partial class TicketsPage : ContentPage
{
    private readonly TicketsViewModel model;
    private readonly AppNavigation routes;
    private IDispatcherTimer? timer;
    private Window? owningWindow;
    private bool opening;

    public TicketsPage(TicketsViewModel model, AppNavigation routes)
    {
        InitializeComponent();
        BindingContext = this.model = model;
        this.routes = routes;
        TicketsViewport.SizeChanged += (_, _) =>
        {
            if (TicketsViewport.Width <= 0) return;
            TicketsContent.WidthRequest = Math.Min(TicketsViewport.Width, ResponsiveLayout.ContentMaximum);
            TicketItemsLayout.Span = ResponsiveLayout.TicketColumns(TicketsViewport.Width);
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        timer ??= Dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(1);
        timer.Tick += OnTick;
        timer.Start();
        owningWindow = Window;
        if (owningWindow != null) { owningWindow.Stopped += OnStopped; owningWindow.Resumed += OnResumed; }
        await model.LoadAsync();
    }

    protected override void OnDisappearing()
    {
        if (timer != null) { timer.Stop(); timer.Tick -= OnTick; }
        if (owningWindow != null) { owningWindow.Stopped -= OnStopped; owningWindow.Resumed -= OnResumed; }
        owningWindow = null;
        base.OnDisappearing();
    }

    private void OnTick(object? sender, EventArgs e) => model.UpdateDurations();
    private void OnStopped(object? sender, EventArgs e) => timer?.Stop();
    private async void OnResumed(object? sender, EventArgs e) { timer?.Start(); await model.LoadAsync(); }
    private async void OnActive(object? sender, EventArgs e) => await model.LoadAsync(false);
    private async void OnArchived(object? sender, EventArgs e) => await model.LoadAsync(true);
    private async void OnExport(object? sender, EventArgs e) => await routes.ShowArchiveExportAsync(Navigation);
    private async void OnRetry(object? sender, EventArgs e) => await model.LoadAsync();
    private async void OnTicketTapped(object? sender, TappedEventArgs e) { if (e.Parameter is Guid id) await OpenAsync(id); }
    private async void OnViewTicket(object? sender, EventArgs e) { if (sender is Button { CommandParameter: Guid id }) await OpenAsync(id); }
    private async Task OpenAsync(Guid id)
    {
        if (opening) return;
        opening = true;
        try { await routes.ShowTicketPreviewAsync(Navigation, id); }
        catch { await DisplayAlertAsync("Ticket unavailable", "Couldn’t open this ticket. Please try again.", "OK"); }
        finally { opening = false; }
    }
}
