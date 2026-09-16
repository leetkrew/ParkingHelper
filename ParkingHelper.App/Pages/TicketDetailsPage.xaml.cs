using ParkingHelper.App.Views;

namespace ParkingHelper.App.Pages;

public partial class TicketDetailsPage : ContentPage
{
    private bool leaving;
    private CopyableDetailRow? selectedRow;

    public TicketDetailsPage(IReadOnlyList<KeyValuePair<string, string>> fields)
    {
        InitializeComponent();
        foreach (var field in fields)
        {
            if (DetailRows.Children.Count > 0)
            {
                var separator = new BoxView { HeightRequest = 0.5, Margin = new Thickness(20, 0, 0, 0) };
                separator.SetAppThemeColor(BoxView.ColorProperty, Color.FromArgb("DFE8F4"), Color.FromArgb("334963"));
                DetailRows.Add(separator);
            }
            var row = new CopyableDetailRow(field.Key, field.Value);
            row.CopyRequested += (_, _) =>
            {
                if (selectedRow != row) selectedRow?.HideCopy();
                selectedRow = row;
            };
            DetailRows.Add(row);
        }
    }

    private async void OnBack(object? sender, EventArgs e)
    {
        if (leaving) return;
        leaving = true;
        try { await Navigation.PopAsync(); }
        catch { await DisplayAlertAsync("Navigation unavailable", "Couldn’t go back. Please try again.", "OK"); }
        finally { leaving = false; }
    }
}
