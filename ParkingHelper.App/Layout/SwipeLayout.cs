namespace ParkingHelper.App.Layout;

public static class SwipeLayout
{
    public const double PreferredActionWidth = 56;
    public const double ContentRemainder = 16;

    public static double ActionWidth(double rowWidth, int actions)
    {
        if (!double.IsFinite(rowWidth) || rowWidth <= 0 || actions <= 0) return 0;
        // Round down so native pixel rounding cannot accumulate into overflow.
        return Math.Floor(Math.Min(PreferredActionWidth, Math.Max(0, rowWidth - ContentRemainder) / actions));
    }
}
