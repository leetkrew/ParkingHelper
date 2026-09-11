namespace ParkingHelper.App;

public partial class AppShell : Shell
{
    public AppShell(Pages.SettingsPage settings, Pages.ScanPage scan, Pages.TicketsPage tickets)
    {
        InitializeComponent();
        SettingsContent.Content = settings;
        ScanContent.Content = scan;
        TicketsContent.Content = tickets;
    }
}