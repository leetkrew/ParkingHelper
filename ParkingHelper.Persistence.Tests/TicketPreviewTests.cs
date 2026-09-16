using Microsoft.Extensions.Logging.Abstractions;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class TicketPreviewTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParkingHelper.Preview", Guid.NewGuid().ToString());
    private SqliteParkingRepository Repository => new(Path.Combine(directory, "parking.db3"));
    private readonly MutableClock clock = new();
    private TicketPreviewViewModel Model(ITicketService? service = null) =>
        new(service ?? new TicketService(Repository), clock, NullLogger<TicketPreviewViewModel>.Instance);

    [Fact]
    public async Task DetailFieldsPreserveExactValuesAndArchivedMetadata()
    {
        var ticket = Ticket(clock.UtcNow.UtcDateTime) with
        {
            BarcodeValue = "  first line\n\nStatus: embedded text\t ",
            PlateNumberSnapshot = "ABC123",
            State = ParkingTicketState.Archived,
            ArchivedUtc = clock.UtcNow.UtcDateTime.AddHours(1)
        };
        var model = Model(new ReadService(_ => Task.FromResult<ParkingTicket?>(ticket)));
        await model.LoadAsync(ticket.Id);
        var fields = model.DetailFields.ToDictionary(field => field.Key, field => field.Value);
        Assert.Equal(ticket.BarcodeValue, fields["Barcode value"]);
        Assert.Equal(ticket.BarcodeFormat, fields["Barcode format"]);
        Assert.Equal(ticket.Id.ToString(), fields["Ticket ID"]);
        Assert.Equal("ABC123", fields["Plate number"]);
        Assert.Equal("Archived", fields["Status"]);
        Assert.Equal(ticket.ArchivedUtc.Value.ToLocalTime().ToString("MMM d, yyyy · h:mm:ss tt"), fields["Archived"]);
        ticket = ticket with { State = ParkingTicketState.Active, ArchivedUtc = null };
        await model.LoadAsync(ticket.Id);
        Assert.DoesNotContain(model.DetailFields, field => field.Key == "Archived");
    }

    [Fact]
    public async Task PreviewLoadsCommittedRecordByPermanentIdAndUsesSavedPlateSnapshot()
    {
        var plates = new PlateService(Repository, clock);
        var plate = await plates.AddAsync("ABC123");
        var created = await new TicketService(Repository).CreateActiveTicketAsync(
            new("ticket", "Pdf417", [1, 2, 3], clock.UtcNow), plate);
        await plates.UpdateAsync(plate.Id, "EDITED");
        var readService = new ReadService(id => new TicketService(Repository).GetTicketAsync(id));
        var preview = Model(readService);
        await preview.LoadAsync(created.Ticket.Id);
        Assert.Equal(created.Ticket.Id, readService.LastId);
        Assert.True(preview.IsLoaded);
        Assert.Equal("ABC123", preview.PlateNumber);
        Assert.Equal(created.Ticket.CreatedUtc.ToLocalTime().ToString("MMM d, yyyy · h:mm:ss tt"), preview.SavedLocalTime);
        Assert.False(preview.CanRetry);
        Assert.False(preview.IsBusy);
    }

    [Fact]
    public async Task LiveDurationRecomputesFromUtcAcrossDaysWithoutReadingDatabaseAgain()
    {
        var ticket = Ticket(clock.UtcNow.UtcDateTime);
        var service = new ReadService(_ => Task.FromResult<ParkingTicket?>(ticket));
        var model = Model(service);
        await model.LoadAsync(ticket.Id);
        Assert.Equal("00:00:00", model.Duration);
        clock.UtcNow += TimeSpan.FromMinutes(12) + TimeSpan.FromSeconds(5);
        model.UpdateDuration();
        Assert.Equal("00:12:05", model.Duration);
        clock.UtcNow += TimeSpan.FromDays(2);
        model.UpdateDuration();
        Assert.Equal("2d 00:12:05", model.Duration);
        Assert.Equal(1, service.Reads);
        clock.UtcNow = new DateTimeOffset(ticket.CreatedUtc).AddHours(-1);
        model.UpdateDuration();
        Assert.Equal("00:00:00", model.Duration);
    }

    [Fact]
    public async Task MissingRecordAndReadFailureRemainRetryableWithoutShowingStaleTicket()
    {
        var ticket = Ticket(clock.UtcNow.UtcDateTime);
        var fail = false;
        ParkingTicket? saved = ticket;
        var service = new ReadService(_ => fail
            ? Task.FromException<ParkingTicket?>(new IOException("read failure"))
            : Task.FromResult<ParkingTicket?>(saved));
        var model = Model(service);
        await model.LoadAsync(ticket.Id);
        Assert.True(model.IsLoaded);
        fail = true;
        await model.LoadAsync(ticket.Id);
        Assert.False(model.IsLoaded);
        Assert.True(model.CanRetry);
        Assert.Contains("retry", model.Status);
        fail = false;
        saved = null;
        await model.LoadAsync(ticket.Id);
        Assert.False(model.IsLoaded);
        Assert.Contains("not be found", model.Status);
        saved = ticket;
        await model.LoadAsync(ticket.Id);
        Assert.True(model.IsLoaded);
        Assert.False(model.CanRetry);
    }

    [Fact]
    public async Task LateReadCannotReplaceNewerTicket()
    {
        var pending = new TaskCompletionSource<ParkingTicket?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Ticket(clock.UtcNow.UtcDateTime);
        var newer = Ticket(clock.UtcNow.UtcDateTime) with { PlateNumberSnapshot = "NEW" };
        var model = Model(new ReadService(id => id == old.Id ? pending.Task : Task.FromResult<ParkingTicket?>(newer)));
        var oldRead = model.LoadAsync(old.Id);
        await model.LoadAsync(newer.Id);
        pending.SetResult(old);
        await oldRead;
        Assert.Equal("NEW", model.PlateNumber);
    }

    [Fact]
    public async Task ServiceRejectsEmptyIdAndReturnsNullForMissingRecord()
    {
        var service = new TicketService(Repository);
        await Assert.ThrowsAsync<TicketOperationException>(() => service.GetTicketAsync(Guid.Empty));
        Assert.Null(await service.GetTicketAsync(Guid.NewGuid()));
    }

    private static ParkingTicket Ticket(DateTime created) => new(Guid.NewGuid(), Guid.NewGuid(), "QrCode", "value",
        ParkingTicketState.Active, created, created, null, null) { PlateNumberSnapshot = "SAVED" };

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    private sealed class ReadService(Func<Guid, Task<ParkingTicket?>> read) : ITicketService
    {
        public Guid LastId { get; private set; }
        public int Reads { get; private set; }
        public Task<IReadOnlyList<ParkingTicket>> GetActiveTicketsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<ParkingTicket>> GetArchivedTicketsAsync() => throw new NotSupportedException();
        public Task<ParkingTicket> ArchiveTicketAsync(Guid id) => throw new NotSupportedException();
        public Task<ParkingTicket> RestoreTicketAsync(Guid id) => throw new NotSupportedException();
        public Task<ParkingTicket> EditTicketAsync(Guid ticketId, Guid plateId, DateTime entryUtc) => throw new NotSupportedException();
        public Task<ParkingTicket> ChangeTicketPlateAsync(Guid ticketId, Guid plateId) => throw new NotSupportedException();
        public Task<ParkingTicket> DeleteTicketAsync(Guid id) => throw new NotSupportedException();
        public Task<ParkingTicket?> GetTicketAsync(Guid id) { LastId = id; Reads++; return read(id); }
        public Task<TicketCreationResult> CreateActiveTicketAsync(ScanResult scan, VehiclePlate selectedPlate) =>
            throw new NotSupportedException("The preview must only read saved tickets.");
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
