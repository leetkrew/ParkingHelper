using ParkingHelper.App.Layout;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class SwipeLayoutTests
{
    [Theory]
    [InlineData(240, 4)]
    [InlineData(280, 4)] // 320 logical-pixel phone minus page padding.
    [InlineData(280, 3)]
    [InlineData(320, 4)]
    [InlineData(344, 4)]
    [InlineData(728, 4)]
    [InlineData(360, 3)]
    public void WholeTrayFitsWithAccessibleHitTargets(double rowWidth, int count)
    {
        var width = SwipeLayout.ActionWidth(rowWidth, count);
        Assert.InRange(width, 48, 56);
        Assert.True(width * count <= rowWidth - SwipeLayout.ContentRemainder);
    }

    [Fact]
    public void ResizingAndNativePixelRoundingCannotOverflowTheRow()
    {
        for (var rowWidth = 180; rowWidth <= 1440; rowWidth++)
        foreach (var count in new[] { 3, 4 })
        foreach (var density in new[] { 1d, 1.5, 1.875, 2, 2.75, 3 })
        {
            var itemPixels = Math.Ceiling(SwipeLayout.ActionWidth(rowWidth, count) * density);
            Assert.True(itemPixels * count < Math.Floor(rowWidth * density));
        }
    }

    [Fact]
    public void NativeRevealUsesMeasuredWidthAndBothLayersAreClipped()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "ParkingHelper.App"))) root = root.Parent;
        string Read(string path) => File.ReadAllText(Path.Combine(root!.FullName, "ParkingHelper.App", path));
        foreach (var path in new[] { "Pages/TicketsPage.xaml", "Views/PlateEditorView.xaml" })
        {
            var markup = Read(path);
            Assert.DoesNotContain("Threshold=", markup);
            Assert.Contains("views:BoundedSwipeView", markup);
            Assert.Contains("views:CompactSwipeAction", markup);
        }
        var row = Read("Views/BoundedSwipeView.cs");
        Assert.Contains("Clip = new RectangleGeometry", row);
        Assert.Contains("content.Clip = new RectangleGeometry", row);
        Assert.Contains("SwipeLayout.ActionWidth(Width, RightItems.Count)", row);
        Assert.Contains("SemanticProperties.SetDescription(this, Text)", Read("Views/CompactSwipeAction.cs"));
        // Resolve the shared row style so both themes still use opaque swipe content.
        Assert.Contains("Style=\"{StaticResource PlateCard}\"", Read("Pages/TicketsPage.xaml"));
        Assert.Contains("BasedOn=\"{StaticResource SurfaceCard}\"", Read("Resources/Styles/PlateStyles.xaml"));
        Assert.Contains("Property=\"BackgroundColor\" Value=\"{AppThemeBinding Light=White, Dark={StaticResource NightSurface}}\"",
            Read("Resources/Styles/Styles.xaml"));
    }
}
