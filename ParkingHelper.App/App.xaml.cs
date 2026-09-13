namespace ParkingHelper.App;

public partial class App : Application
{
    private readonly IServiceProvider services;

    public App(IServiceProvider services)
    {
        this.services = services;
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        if (Environment.GetEnvironmentVariable("PARKING_SCAN_PROBE") is { } probe) return ScanRuntimeProbe.Create(services, probe);
        var window = new Window(services.GetRequiredService<Pages.StartupPage>());
#if MACCATALYST
        // Allow compact and wide windows while keeping the plate editor and actions usable.
        // Leave the maximum unrestricted so macOS resizing and full screen remain available.
        window.MinimumWidth = 400;
        window.MinimumHeight = 640;
#endif
        return window;
    }
}
