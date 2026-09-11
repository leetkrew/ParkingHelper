namespace ParkingHelper.App.Views;

// A centered, bounded content area that still fills compact viewports and preserves vertical scrolling.
public sealed class ReadableContentView : ContentView
{
    public double ContentMaximumWidth { get; set; } = 720;
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (Content == null || width <= 0) return;
        Content.HorizontalOptions = LayoutOptions.Center;
        Content.WidthRequest = Math.Min(Math.Max(0, width - Padding.HorizontalThickness), ContentMaximumWidth);
    }
}
