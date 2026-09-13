using Microsoft.Extensions.Logging;
using ParkingHelper.App.Pages;
using ParkingHelper.App.Services;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using ParkingHelper.Persistence;

namespace ParkingHelper.App;

internal static class ScanRuntimeProbe
{
    public static Window Create(IServiceProvider original, string log)
    {
        File.WriteAllText(log, "SCAN PROBE\n");
        var nav = new NavigationPage(new ContentPage());
        var window = new Window(nav);
        var started = false;
        window.Activated += async (_, _) =>
        {
            if (started) return;
            started = true;
            try
            {
                var repo = new SqliteParkingRepository(Path.Combine(FileSystem.Current.CacheDirectory, "scan-probe-" + Guid.NewGuid() + ".db3"));
                var plates = new PlateService(repo, TimeProvider.System);
                var plate = await plates.AddAsync("PROBE ONLY");
                var tickets = new TicketService(repo);
                var model = new ScanViewModel(plates, new Preference { SelectedPlateId = plate.Id }, tickets, original.GetRequiredService<ILogger<ScanViewModel>>());
                var scanner = new Scanner(log, () => model.SelectedPlate?.Id);
                var provider = new Provider(original, tickets, log);
                var page = new ScanPage(scanner, model, provider);
                page.ToolbarItems.Add(new ToolbarItem("Probe capture", null, scanner.Capture));
                await nav.PushAsync(page);
                File.AppendAllText(log, "SCAN PAGE OPEN\n");
            }
            catch (Exception ex) { File.AppendAllText(log, ex.ToString()); }
        };
        return window;
    }
    private sealed class Preference : ISelectedPlatePreference { public Guid? SelectedPlateId { get; set; } }
    private sealed class Provider(IServiceProvider original, ITicketService tickets, string log) : IServiceProvider
    {
        public object? GetService(Type type)
        {
            if (type == typeof(AppNavigation)) return new AppNavigation(this);
            if (type == typeof(TicketPreviewPage))
            {
                File.AppendAllText(log, "PREVIEW NAVIGATION\n");
                return new TicketPreviewPage(new TicketPreviewViewModel(tickets, TimeProvider.System,
                    original.GetRequiredService<ILogger<TicketPreviewViewModel>>()), new AppNavigation(this), original.GetRequiredService<IWalletLauncherService>());
            }
            if (type == typeof(IScanFeedbackService)) return new Feedback(original.GetRequiredService<IScanFeedbackService>(), tickets, log);
            return original.GetService(type);
        }
    }
    private sealed class Feedback(IScanFeedbackService actual, ITicketService tickets, string log) : IScanFeedbackService
    {
        public Task PrepareAsync() => actual.PrepareAsync();
        public async Task NotifySavedAsync(TicketCreationResult? result, CancellationToken cancellationToken = default)
        {
            var persisted = await tickets.GetTicketAsync(result!.Ticket.Id);
            File.AppendAllText(log, "FEEDBACK AFTER COMMIT " + persisted!.Id + " " + persisted.BarcodeFormat + "\n");
            await actual.NotifySavedAsync(result, cancellationToken);
        }
    }
    private sealed class Scanner(string log, Func<Guid?> plate) : IBarcodeScannerService
    {
        public View Preview { get; } = new ContentView { BackgroundColor = Colors.DarkSlateBlue };
        public ScanResult? Result { get; private set; }
        public string Status { get; private set; } = "Ready";
        public IReadOnlyList<ScannerCamera> Cameras { get; } = [new(null, "Automatic"), new("external", "Preferred")];
        public string? SelectedCameraId => "external";
        public string VideoSource => "Preferred";
        public bool CanUseTorch => false;
        public bool IsTorchOn => false;
        public bool HasActiveVideoStream { get; private set; }
        public bool HasCameraError => false;
        public bool NeedsPermissionSettings => false;
        public event EventHandler? Changed;
        public event EventHandler<ScanResult>? Captured;
        private int starts;
        public async Task StartAsync(bool detectBarcodes = true)
        {
            File.AppendAllText(log, $"START {++starts} plate={plate()} camera={SelectedCameraId}\n");
            HasActiveVideoStream = false;
            Status = "Restarting camera…";
            Changed?.Invoke(this, EventArgs.Empty);
            await Task.Delay(2500);
            HasActiveVideoStream = true;
            Status = "Scanning";
            Changed?.Invoke(this, EventArgs.Empty);
            File.AppendAllText(log, "START COMPLETE\n");
        }
        public void Stop() { HasActiveVideoStream = false; File.AppendAllText(log, "STOP\n"); }
        public void Rescan() { Result = null; Status = "Scanning…"; File.AppendAllText(log, $"RESCAN plate={plate()} camera={SelectedCameraId}\n"); Changed?.Invoke(this, EventArgs.Empty); }
        public void Cancel() { }
        public void ToggleTorch() { }
        public Task SelectCameraAsync(string? id) => Task.CompletedTask;
        public Task SwitchCameraAsync() => Task.CompletedTask;
        public void Capture()
        {
            Result = new("runtime-probe-ticket", "Pdf417", [1, 2], DateTimeOffset.UtcNow);
            File.AppendAllText(log, "ACCEPTED DECODE\n");
            Captured?.Invoke(this, Result);
        }
    }
}
