using ParkingHelper.App.Services;
using Microsoft.Extensions.Logging;
using ParkingHelper.App.Layout;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Pages;

public partial class TicketsPage : ContentPage
{
    private readonly TicketsViewModel model;
    private readonly AppNavigation routes;
    private IDispatcherTimer? timer;
    private Window? owningWindow;
    private bool opening;
    private readonly SingleOpenRow<SwipeView> openRow = new(row => row.Close(Views.BoundedSwipeView.MotionEnabled));

    public TicketsPage(TicketsViewModel model, AppNavigation routes)
    {
        InitializeComponent();
        BindingContext = this.model = model;
        this.routes = routes;
        model.Reloading += openRow.Close;
        model.Items.CollectionChanged += (_, _) => openRow.Close();
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
        model.Activate(action => Dispatcher.Dispatch(action));
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
        openRow.Close();
        model.Deactivate();
        if (timer != null) { timer.Stop(); timer.Tick -= OnTick; }
        if (owningWindow != null) { owningWindow.Stopped -= OnStopped; owningWindow.Resumed -= OnResumed; }
        owningWindow = null;
        base.OnDisappearing();
    }

    private void OnTick(object? sender, EventArgs e) => model.UpdateDurations();
    private void OnStopped(object? sender, EventArgs e) { timer?.Stop(); model.Deactivate(); openRow.Close(); }
    private async void OnResumed(object? sender, EventArgs e) { model.Activate(action => Dispatcher.Dispatch(action)); timer?.Start(); await model.LoadAsync(); }
    private async void OnActive(object? sender, EventArgs e) { openRow.Close(); await model.LoadAsync(false); }
    private async void OnArchived(object? sender, EventArgs e) { openRow.Close(); await model.LoadAsync(true); }
    public Task ShowActiveTicketsAsync() { openRow.Close(); return model.LoadAsync(false); }
    private async void OnExport(object? sender, EventArgs e)
    {
        if (opening || !model.IsArchived || model.IsBusy) return;
        opening = true;
        try { await routes.ShowArchiveExportAsync(Navigation); }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Handler?.MauiContext?.Services.GetService<ILogger<TicketsPage>>()?
                .LogError(exception, "Could not open archived ticket export");
            await DisplayAlertAsync("Export unavailable", "Couldn’t open archive export. Please try again.", "OK");
        }
        finally { opening = false; }
    }
    private async void OnRefreshing(object? sender, EventArgs e)
    {
        // Programmatic IsRefreshing=true can raise Refreshing again. Only the VM
        // owns the operation and its finally; a reentrant event must not clear it.
        if (model.IsRefreshing) return;
        openRow.Close();
        await model.RefreshAsync();
    }
    private async void OnRetry(object? sender, EventArgs e) { openRow.Close(); await model.LoadAsync(); }
    private async void OnTicketTapped(object? sender, TappedEventArgs e) { if (e.Parameter is Guid id) await OpenAsync(id); }
    private async Task OpenAsync(Guid id, bool edit = false)
    {
        if (opening) return;
        opening = true;
        openRow.Close();
        try
        {
            if (edit) await routes.ShowTicketEditorAsync(Navigation, id);
            else await routes.ShowTicketPreviewAsync(Navigation, id);
        }
        catch { await DisplayAlertAsync("Ticket unavailable", "Couldn’t open this ticket. Please try again.", "OK"); }
        finally { opening = false; }
    }
    private void OnSwipeStarted(object? sender, SwipeStartedEventArgs e)
    {
        if (sender is SwipeView row) openRow.Open(row);
    }
    private void OnSwipeEnded(object? sender, SwipeEndedEventArgs e)
    {
        if (!e.IsOpen && sender is SwipeView row) openRow.Closed(row);
    }
    private void OnRowUnloaded(object? sender, EventArgs e)
    {
        if (sender is SwipeView row) { openRow.Closed(row); row.Close(false); }
    }
    private void OnRowContextChanged(object? sender, EventArgs e)
    {
        if (sender is not Border { BindingContext: TicketRowViewModel row } border) return;
        if (border.Parent is SwipeView swipe) { openRow.Closed(swipe); swipe.Close(false); }
#if MACCATALYST
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, EventHandler action)
        {
            var item = new MenuFlyoutItem { Text = text, BindingContext = row };
            item.Command = new Command(() => action(item, EventArgs.Empty));
            return item;
        }
        menu.Add(Item("Edit", OnEditTicket));
        menu.Add(Item(row.StateAction, OnChangeTicketState));
        menu.Add(Item("Delete", OnDeleteTicket));
        FlyoutBase.SetContextFlyout(border, menu);
        ToolTipProperties.SetText(border, "Click to preview. Right-click or Control-click for ticket actions.");
#endif
    }
    private async void OnEditTicket(object? sender, EventArgs e)
    {
        if (sender is BindableObject { BindingContext: TicketRowViewModel row }) await OpenAsync(row.Id, edit: true);
    }
    private async void OnChangeTicketState(object? sender, EventArgs e)
    {
        if (sender is BindableObject { BindingContext: TicketRowViewModel row })
            await ChangeTicketStateAsync(row.Id, row.IsArchived ? ParkingTicketState.Active : ParkingTicketState.Archived);
    }
    private async void OnDeleteTicket(object? sender, EventArgs e)
    {
        if (sender is BindableObject { BindingContext: TicketRowViewModel row })
            await ChangeTicketStateAsync(row.Id, ParkingTicketState.Deleted);
    }
    private async Task ChangeTicketStateAsync(Guid id, ParkingTicketState state)
    {
        if (opening || model.IsLoading) return;
        opening = true;
        openRow.Close();
        try
        {
            if (state == ParkingTicketState.Deleted &&
                !await DisplayAlertAsync("Delete this ticket?", "This ticket will be removed from Parking Helper.", "Delete", "Cancel")) return;
            await model.ChangeStateAsync(id, state);
            await model.ReloadAsync();
        }
        catch (TicketOperationException error) { await DisplayAlertAsync("Ticket unavailable", error.Message, "OK"); }
        catch (Exception) { await DisplayAlertAsync("Ticket unavailable", "Couldn’t update this ticket. Please try again.", "OK"); }
        finally { opening = false; }
    }

}
