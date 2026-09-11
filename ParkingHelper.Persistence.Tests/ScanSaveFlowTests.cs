using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Maui.Storage;
using ParkingHelper.App.Services;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class ScanSaveFlowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParkingHelper.ScanFlow", Guid.NewGuid().ToString());
    private SqliteParkingRepository Repository => new(Path.Combine(directory, "parking.db3"));
    private IPlateService Plates => new PlateService(Repository, TimeProvider.System);
    private readonly MemoryPreferences memory = new();
    private ISelectedPlatePreference Preference => new SelectedPlatePreference(memory);
    private ScanViewModel Model(ITicketService? tickets = null) => new(Plates, Preference,
        tickets ?? new TicketService(Repository), NullLogger<ScanViewModel>.Instance);
    private static ScanResult Scan() => new("ticket", "Pdf417", null,
        new DateTimeOffset(2026, 9, 11, 12, 42, 0, TimeSpan.Zero));

    [Fact]
    public async Task FirstInSavedOrderIsSelectedBeforeAnyScanAndLastSelectionSurvivesNewInstance()
    {
        var first = await Plates.AddAsync("FIRST");
        var second = await Plates.AddAsync("SECOND");
        await Plates.MoveAsync(second.Id, -1);
        var model = Model();
        await model.LoadAsync();
        Assert.True(model.CanScan);
        Assert.Equal(new[] { second.Id, first.Id }, model.Plates.Select(p => p.Id));
        Assert.Equal(second.Id, model.SelectedPlate!.Id);
        Assert.Equal(second.Id, Preference.SelectedPlateId);
        model.SelectPlate(first.Id);
        Assert.Equal(first.Id, Preference.SelectedPlateId);
        Assert.Equal(first.Id, Assert.Single(model.Plates, p => p.IsSelected).Id);
        var reopened = Model();
        await reopened.LoadAsync();
        Assert.Equal(first.Id, reopened.SelectedPlate!.Id);
    }

    [Fact]
    public async Task EditKeepsIdentityAndDeletedSelectionFallsBackAndPersists()
    {
        var first = await Plates.AddAsync("FIRST");
        var second = await Plates.AddAsync("SECOND");
        var model = Model();
        await model.LoadAsync();
        model.SelectPlate(second.Id);
        await Plates.UpdateAsync(second.Id, "EDITED");
        await model.LoadAsync();
        Assert.Equal(second.Id, model.SelectedPlate!.Id);
        Assert.Equal("EDITED", model.SelectedPlate.PlateNumber);
        await Plates.DeleteAsync(second.Id);
        await model.LoadAsync();
        Assert.Equal(first.Id, model.SelectedPlate!.Id);
        Assert.Equal(first.Id, Preference.SelectedPlateId);
        await Plates.DeleteAsync(first.Id);
        await model.LoadAsync();
        Assert.Null(model.SelectedPlate);
        Assert.Null(Preference.SelectedPlateId);
        Assert.False(model.CanScan);
    }

    [Fact]
    public async Task InvalidStoredIdFallsBackToSavedPlate()
    {
        var plate = await Plates.AddAsync("FIRST");
        memory.Set("scanner.lastSelectedPlateId", "not-a-guid");
        var model = Model();
        await model.LoadAsync();
        Assert.Equal(plate.Id, model.SelectedPlate!.Id);
        Assert.Equal(plate.Id.ToString("D"), memory.Get("scanner.lastSelectedPlateId", ""));
    }

    [Fact]
    public async Task InflightSaveRetainsAcceptedPlateAndRepeatedCallbacksAreIgnored()
    {
        var first = await Plates.AddAsync("FIRST");
        var second = await Plates.AddAsync("SECOND");
        var completion = new TaskCompletionSource<TicketCreationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        VehiclePlate? acceptedPlate = null;
        var service = new StubTickets((_, selected) => { acceptedPlate = selected; return completion.Task; });
        var model = Model(service);
        await model.LoadAsync();
        var scan = Scan();
        var pending = model.SaveScanAsync(scan);
        Assert.True(model.IsSaving);
        Assert.False(model.SaveSucceeded);
        Assert.Equal("Saving ticket…", model.Feedback);
        model.SelectPlate(second.Id);
        Assert.Equal(second.Id, model.SelectedPlate!.Id);
        Assert.Equal(second.Id, Preference.SelectedPlateId);
        Assert.Equal(first.Id, acceptedPlate!.Id);
        Assert.Null(await model.SaveScanAsync(scan));
        Assert.Null(await model.SaveScanAsync(Scan()));
        Assert.Equal(1, service.Calls);
        var ticket = new ParkingTicket(Guid.NewGuid(), first.Id, scan.Format, scan.Value, ParkingTicketState.Active,
            scan.DetectedUtc.UtcDateTime, scan.DetectedUtc.UtcDateTime, null, null) { PlateNumberSnapshot = first.PlateNumber };
        completion.SetResult(new(ticket, true));
        await pending;
        Assert.False(model.IsSaving);
        Assert.True(model.SaveSucceeded);
        Assert.Contains("FIRST", model.FeedbackDetail);
        Assert.Null(await model.SaveScanAsync(scan));
        Assert.Equal(1, service.Calls);
    }

    [Fact]
    public async Task ActualAutoSaveAndDuplicateFeedbackUseOriginalPlate()
    {
        var first = await Plates.AddAsync("FIRST");
        var second = await Plates.AddAsync("SECOND");
        var model = Model();
        await model.LoadAsync();
        Assert.True((await model.SaveScanAsync(Scan()))!.Created);
        Assert.Equal("✓ Ticket saved", model.Feedback);
        model.SelectPlate(second.Id);
        model.ClearFeedback();
        var duplicate = await model.SaveScanAsync(Scan());
        Assert.False(duplicate!.Created);
        Assert.False(model.SaveSucceeded);
        Assert.False(model.HasSaveError);
        Assert.Equal("Ticket already saved", model.Feedback);
        Assert.Contains(first.PlateNumber, model.FeedbackDetail);
        Assert.DoesNotContain(second.PlateNumber, model.FeedbackDetail);
        Assert.Single(await Repository.GetTicketsAsync(ParkingTicketState.Active));
    }

    [Fact]
    public async Task SaveFailureIsNotSuccessAndNewCaptureCanRetry()
    {
        await Plates.AddAsync("FIRST");
        var fail = true;
        var actual = new TicketService(Repository);
        var service = new StubTickets((scan, plate) => fail
            ? Task.FromException<TicketCreationResult>(new IOException("Disk unavailable"))
            : actual.CreateActiveTicketAsync(scan, plate));
        var model = Model(service);
        await model.LoadAsync();
        Assert.Null(await model.SaveScanAsync(Scan()));
        Assert.True(model.HasSaveError);
        Assert.False(model.SaveSucceeded);
        Assert.False(model.IsSaving);
        Assert.Equal("Ticket not saved", model.Feedback);
        Assert.Empty(await Repository.GetTicketsAsync(ParkingTicketState.Active));
        fail = false;
        model.ClearFeedback();
        Assert.True((await model.SaveScanAsync(Scan()))!.Created);
        Assert.True(model.SaveSucceeded);
        Assert.False(model.HasSaveError);
    }

    [Fact]
    public async Task NoSelectionAndEmptyPayloadNeverProduceSuccess()
    {
        var model = Model();
        await model.LoadAsync();
        Assert.Null(await model.SaveScanAsync(Scan()));
        Assert.True(model.HasSaveError);
        await Plates.AddAsync("FIRST");
        await model.LoadAsync();
        Assert.Null(await model.SaveScanAsync(new("", "QrCode", null, DateTimeOffset.UtcNow)));
        Assert.True(model.HasSaveError);
        Assert.False(model.SaveSucceeded);
        Assert.Empty(await Repository.GetTicketsAsync(ParkingTicketState.Active));
    }

    private sealed class StubTickets(Func<ScanResult, VehiclePlate, Task<TicketCreationResult>> action) : ITicketService
    {
        public Task<IReadOnlyList<ParkingTicket>> GetActiveTicketsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<ParkingTicket>> GetArchivedTicketsAsync() => throw new NotSupportedException();
        public Task<ParkingTicket> ArchiveTicketAsync(Guid id) => throw new NotSupportedException();
        public Task<ParkingTicket> RestoreTicketAsync(Guid id) => throw new NotSupportedException();
        public Task<ParkingTicket> ChangeTicketPlateAsync(Guid ticketId, Guid plateId) => throw new NotSupportedException();
        public Task<ParkingTicket> DeleteTicketAsync(Guid id) => throw new NotSupportedException();
        public Task<ParkingTicket?> GetTicketAsync(Guid id) => Task.FromResult<ParkingTicket?>(null);
        public int Calls { get; private set; }
        public Task<TicketCreationResult> CreateActiveTicketAsync(ScanResult scan, VehiclePlate selectedPlate)
        {
            Calls++;
            return action(scan, selectedPlate);
        }
    }

    private sealed class MemoryPreferences : IPreferences
    {
        private readonly Dictionary<string, object> values = [];
        public bool ContainsKey(string key, string? sharedName = null) => values.ContainsKey(key);
        public void Remove(string key, string? sharedName = null) => values.Remove(key);
        public void Clear(string? sharedName = null) => values.Clear();
        public void Set<T>(string key, T value, string? sharedName = null) => values[key] = value!;
        public T Get<T>(string key, T defaultValue, string? sharedName = null) => values.TryGetValue(key, out var value) ? (T)value : defaultValue;
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
