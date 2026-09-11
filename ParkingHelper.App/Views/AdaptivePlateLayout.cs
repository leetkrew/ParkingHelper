using ParkingHelper.App.Layout;

namespace ParkingHelper.App.Views;

// Reuses BindableLayout templates and their existing selection handlers at every window width.
public sealed class AdaptivePlateLayout : Grid
{
    public bool CompactSingleColumn { get; set; }
    public AdaptivePlateLayout() { ColumnSpacing = RowSpacing = 8; }
    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        ArrangeChoices(width);
    }
    protected override void OnChildAdded(Element child)
    {
        base.OnChildAdded(child);
        ArrangeChoices(Width);
    }
    protected override void OnChildRemoved(Element child, int oldLogicalIndex)
    {
        base.OnChildRemoved(child, oldLogicalIndex);
        ArrangeChoices(Width);
    }
    private void ArrangeChoices(double width)
    {
        var columns = ResponsiveLayout.PlateColumns(width, CompactSingleColumn);
        var rows = (Children.Count + columns - 1) / columns;
        if (ColumnDefinitions.Count != columns)
        {
            ColumnDefinitions.Clear();
            for (var i = 0; i < columns; i++) ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }
        if (RowDefinitions.Count != rows)
        {
            RowDefinitions.Clear();
            for (var i = 0; i < rows; i++) RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        for (var i = 0; i < Children.Count; i++)
        {
            SetRow(Children[i], i / columns);
            SetColumn(Children[i], i % columns);
        }
    }
}
