using ParkingHelper.App.Layout;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class ResponsiveLayoutTests
{
    [Theory]
    [InlineData(390, 844, false, 1, false)]
    [InlineData(834, 1194, true, 2, false)]
    [InlineData(1194, 834, true, 3, true)]
    [InlineData(500, 700, false, 1, false)]
    [InlineData(1440, 900, true, 3, true)]
    public void SharedBreakpointsCoverPhoneTabletOrientationsAndDesktopWindows(
        double width, double height, bool wide, int columns, bool sideBySide)
    {
        Assert.Equal(wide, ResponsiveLayout.IsWide(width));
        Assert.Equal(columns, ResponsiveLayout.TicketColumns(width));
        Assert.Equal(sideBySide, ResponsiveLayout.UseLandscapePresentation(width, height));
        Assert.InRange(ResponsiveLayout.PresentationBarcodeHeight(width, height), 240, 520);
        Assert.InRange(ResponsiveLayout.CameraHeight(width, height), 260, 560);
    }

    [Fact]
    public void NarrowingAndExpandingDoesNotDependOnADeviceIdiomOrPreviousLayout()
    {
        foreach (var width in new[] { 1400d, 500, 1000, 390, 1400 })
        {
            var count = ResponsiveLayout.PlateColumns(width, false);
            Assert.InRange(count, 1, 6);
            Assert.True((width - (count - 1) * 8) / count >= 144);
        }
        Assert.Equal(1, ResponsiveLayout.PlateColumns(390, true));
        Assert.True(ResponsiveLayout.PlateColumns(700, true) > 1);
        Assert.Equal(1, ResponsiveLayout.PlateColumns(0, false));
    }

    [Fact]
    public void VeryLargeOrShortWindowsKeepCameraAndBarcodeWithinPracticalBounds()
    {
        Assert.Equal(520, ResponsiveLayout.PresentationBarcodeHeight(2500, 2000));
        Assert.Equal(560, ResponsiveLayout.CameraHeight(2500, 2000));
        Assert.Equal(240, ResponsiveLayout.PresentationBarcodeHeight(900, 250));
        Assert.Equal(320, ResponsiveLayout.CameraHeight(900, 250));
        Assert.Equal(340, ResponsiveLayout.CameraHeight(390, 844));
    }
}
