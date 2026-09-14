using ParkingHelper.App.Services;

namespace ParkingHelper.App;

public partial class AppShell : Shell
{
    private readonly Pages.TicketsPage tickets;
    private readonly SectionRootNavigation rootNavigation = new();

    public AppShell(Pages.SettingsPage settings, Pages.ScanPage scan, Pages.TicketsPage tickets)
    {
        InitializeComponent();
        SettingsContent.Content = settings;
        ScanContent.Content = scan;
        TicketsContent.Content = this.tickets = tickets;
        Navigated += OnNavigated;
    }

    private void OnNavigated(object? sender, ShellNavigatedEventArgs e)
    {
        if (e.Source is ShellNavigationSource.ShellItemChanged or ShellNavigationSource.ShellSectionChanged
            && CurrentItem?.CurrentItem is { } section)
            ActivateSection(section);
    }

    // Native handlers also call this on a tap of the already-selected tab, which
    // does not raise Shell.Navigated. Dispatch after the native selection completes.
    public void ActivateSection(ShellSection section) => Dispatcher.Dispatch(async () =>
    {
        try
        {
            await rootNavigation.OpenAsync(section.Route,
                () => section.Navigation.PopToRootAsync(animated: false), tickets.ShowActiveTicketsAsync);
        }
        catch (Exception)
        {
            // The window can disappear while a native tap is being dispatched.
            // No account, ticket, or navigation payload is logged.
            System.Diagnostics.Debug.WriteLine("Section root navigation could not complete.");
        }
    });
}
