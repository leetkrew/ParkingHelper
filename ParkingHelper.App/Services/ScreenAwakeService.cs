namespace ParkingHelper.App.Services;

public interface IScreenAwakeService
{
    IDisposable Acquire();
}

// Called on the UI thread. Restore the prior setting after the last visible presentation exits.
public sealed class ScreenAwakeService : IScreenAwakeService
{
    private int leases;
    private bool previous;
    public IDisposable Acquire()
    {
        try
        {
            if (leases == 0) { previous = DeviceDisplay.Current.KeepScreenOn; DeviceDisplay.Current.KeepScreenOn = true; }
            leases++;
            return new Lease(this);
        }
        catch { return new Lease(null); }
    }
    private void Release()
    {
        if (--leases != 0) return;
        try { DeviceDisplay.Current.KeepScreenOn = previous; }
        catch { /* Screen-awake support is optional. */ }
    }
    private sealed class Lease(ScreenAwakeService? owner) : IDisposable
    {
        private ScreenAwakeService? service = owner;
        public void Dispose() => Interlocked.Exchange(ref service, null)?.Release();
    }
}
