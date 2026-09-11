using ParkingHelper.App.Pages;

namespace ParkingHelper.App.Services;

// Window-local root replacement keeps setup outside Shell's tabs and back stack.
// Page resolution is confined to this UI composition/navigation boundary.
public sealed class AppNavigation(IServiceProvider services)
{
    public Task ShowTicketPreviewAsync(INavigation navigation, Guid ticketId)
    {
        var page = services.GetRequiredService<TicketPreviewPage>();
        page.SetTicketId(ticketId);
        return navigation.PushAsync(page);
    }

    public Task ShowFullScreenTicketAsync(INavigation navigation, Guid ticketId)
    {
        var page = services.GetRequiredService<FullScreenTicketPage>();
        page.SetTicketId(ticketId);
        return navigation.PushAsync(page);
    }

    public void ShowSetup(Window window) => window.Page = services.GetRequiredService<InitialSetupPage>();
    public void ShowHome(Window window) => window.Page = services.GetRequiredService<AppShell>();
}
