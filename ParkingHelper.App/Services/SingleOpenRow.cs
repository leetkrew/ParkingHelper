namespace ParkingHelper.App.Services;

// Weak ownership accommodates CollectionView recycling without retaining off-screen rows.
public sealed class SingleOpenRow<T>(Action<T> close) where T : class
{
    private WeakReference<T>? current;
    public void Open(T row)
    {
        if (current?.TryGetTarget(out var previous) == true && ReferenceEquals(previous, row)) return;
        Close();
        current = new(row);
    }
    public void Closed(T row)
    {
        if (current?.TryGetTarget(out var previous) == true && ReferenceEquals(previous, row)) current = null;
    }
    public void Close()
    {
        var old = current;
        current = null; // A native Close callback can reenter Closed.
        if (old?.TryGetTarget(out var row) == true) close(row);
    }
}
