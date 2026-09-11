namespace ParkingHelper.App.Layout;

// Device-independent layout policy in logical pixels: resizing and split-screen use the same rules.
public static class ResponsiveLayout
{
    public const double WideBreakpoint = 760;
    public const double ContentMaximum = 1280;
    public const double ReadingMaximum = 720;
    public const double ActionsMaximum = 600;
    public static bool IsWide(double width) => width >= WideBreakpoint;
    public static int TicketColumns(double width) => width >= 1120 ? 3 : width >= 720 ? 2 : 1;
    public static int PlateColumns(double width, bool compactSingleColumn) => compactSingleColumn && width < 600
        ? 1 : Math.Clamp((int)((Math.Max(0, width) + 8) / 152), 1, 6);
    public static bool UseLandscapePresentation(double width, double height) => IsWide(width) && width > height * 1.15;
    public static double CameraHeight(double width, double height) => IsWide(width)
        ? Math.Clamp(height * 0.62, 320, 560) : height < 500 ? 260 : 340;
    public static double PresentationBarcodeHeight(double width, double height) => IsWide(width)
        ? Math.Clamp(height - (UseLandscapePresentation(width, height) ? 64 : 300), 240, 520) : 300;
}
