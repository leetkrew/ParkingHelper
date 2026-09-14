using ParkingHelper.App.Services;
using ParkingHelper.App.Views;

namespace ParkingHelper.App.Pages;

public sealed class ScannerSettingsPage : ContentPage
{
    public ScannerSettingsPage(IScannerSettingsService settings)
    {
        Title = "Scanner Settings";
        var picker = new Picker { Title = "Scanner Format", MinimumHeightRequest = 52, ItemsSource = settings.Formats.ToList() };
        picker.SelectedItem = settings.Formats.First(f => f.Id == settings.Format);
        picker.SelectedIndexChanged += (_, _) =>
        {
            if (picker.SelectedItem is ScannerFormatOption selected) settings.Format = selected.Id;
        };
        Content = new ScrollView { Content = new ReadableContentView { Content = new VerticalStackLayout
        {
            Padding = 20, Spacing = 20, MaximumWidthRequest = 720,
            Children =
            {
                new Label { Text = "Scanner Format", Style = (Style)Application.Current!.Resources["PageHeading"] },
                new Border { Style = (Style)Application.Current!.Resources["SurfaceCard"], Content = picker },
                new Label { Text = "Auto scans common ticket and consumer barcodes. Select any individual format, including specialist formats, when needed." },
                new Label { Text = "Scanning never converts a barcode to another format." },
                new Label { Text = "UPC/EAN extension is a supplement, not a standalone barcode. Some specialist formats have decoder limitations or are more prone to false detections." }
            }
        }}};
    }
}
