namespace ParkingHelper.App;

public partial class AppShell : Shell
{
    private readonly Pages.TicketsPage tickets;
    private bool resettingTickets;

    public AppShell(Pages.SettingsPage settings, Pages.ScanPage scan, Pages.TicketsPage tickets)
    {
        InitializeComponent();
        SettingsContent.Content = settings;
        ScanContent.Content = scan;
        TicketsContent.Content = this.tickets = tickets;
        Navigated += OnNavigated;
    }

    private async void OnNavigated(object? sender, ShellNavigatedEventArgs e)
    {
        if (resettingTickets
            || e.Source is not (ShellNavigationSource.ShellItemChanged or ShellNavigationSource.ShellSectionChanged)
            || !e.Current.Location.OriginalString.Contains("/tickets", StringComparison.OrdinalIgnoreCase))
            return;

        resettingTickets = true;
        try
        {
            await tickets.Navigation.PopToRootAsync(animated: false);
            await tickets.ShowActiveTicketsAsync();
        }
        finally
        {
            resettingTickets = false;
        }
    }
}