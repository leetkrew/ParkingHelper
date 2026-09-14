using Microsoft.Maui.Controls.Shapes;
using ParkingHelper.App.Layout;

namespace ParkingHelper.App.Views;

public sealed class BoundedSwipeView : SwipeView
{
    private double lastWidth;
    private double lastHeight;

    public BoundedSwipeView()
    {
        HorizontalOptions = LayoutOptions.Fill;
        MinimumWidthRequest = 0;
        // Threshold remains zero: the native reveal distance is the measured tray width.
        SizeChanged += (_, _) => Fit();
        Loaded += (_, _) => Fit();
    }

    private void Fit()
    {
        if (Width <= 0 || Height <= 0) return;
        if (lastWidth == Width && lastHeight == Height) return;
        lastWidth = Width;
        lastHeight = Height;
        Close(false); // Resizing must not leave an old native swipe offset behind.
        Clip = new RectangleGeometry(new Rect(0, 0, Width, Height));
        if (Content is { } content)
            content.Clip = new RectangleGeometry(new Rect(0, 0, Width, Height));
        var actionWidth = SwipeLayout.ActionWidth(Width, RightItems.Count);
        foreach (var action in RightItems.OfType<CompactSwipeAction>())
        {
            action.WidthRequest = actionWidth;
            action.HeightRequest = Height;
        }
    }
}
