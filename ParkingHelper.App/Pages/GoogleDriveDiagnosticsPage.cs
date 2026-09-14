using ParkingHelper.App.Services;
using ParkingHelper.App.Views;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Pages;

public sealed class GoogleDriveDiagnosticsPage : ContentPage
{
    private readonly GoogleDriveConnection connection;
    private readonly GoogleDriveDiagnosticsReport report;
    private readonly Label text = new() { FontSize = 13, LineBreakMode = LineBreakMode.WordWrap };

    public GoogleDriveDiagnosticsPage(GoogleDriveConnection connection, GoogleDriveDiagnosticsReport report)
    {
        this.connection = connection;
        this.report = report;
        Title = "Drive Diagnostics";
        var refresh = new Button { Text = "Refresh local counts", Style = (Style)Application.Current!.Resources["QuietButton"], AutomationId = "DiagnosticLocalCounts" };
        var copy = new Button { Text = "Copy diagnostics", Style = (Style)Application.Current!.Resources["QuietButton"], AutomationId = "CopyDriveDiagnostics" };
        refresh.Clicked += async (_, _) => { await connection.CaptureDiagnosticLocalAsync(); Refresh(); };
        copy.Clicked += async (_, _) => await Clipboard.Default.SetTextAsync(report.Build());
        Content = new ScrollView
        {
            Content = new ReadableContentView
            {
                Content = new VerticalStackLayout
                {
                    Padding = 20, Spacing = 12,
                    Children =
                    {
                        new Label { Text = "Read-only connection and synchronization information for support. Cloud information reflects the most recent observation." },
                        new Label { Text = "Ticket counts exclude deleted records. Deletion tombstones are shown separately." },
                        refresh, copy, new Border { Style = (Style)Application.Current!.Resources["SurfaceCard"], Content = text }
                    }
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        connection.Changed += OnChanged;
        if (connection.Diagnostics is { } diagnostics) diagnostics.Changed += OnChanged;
        await connection.CaptureDiagnosticLocalAsync();
        Refresh();
    }

    protected override void OnDisappearing()
    {
        connection.Changed -= OnChanged;
        if (connection.Diagnostics is { } diagnostics) diagnostics.Changed -= OnChanged;
        base.OnDisappearing();
    }

    private void OnChanged() => MainThread.BeginInvokeOnMainThread(Refresh);
    private void Refresh()
    {
        text.Text = report.Build();
    }
}
