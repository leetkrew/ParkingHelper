using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class TicketManagementTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParkingHelper.Management", Guid.NewGuid().ToString());
    private readonly Clock clock = new();
    private SqliteParkingRepository Repository => new(Path.Combine(directory, "tickets.db3"));
    private TicketService Service => new(Repository, clock);
    private async Task<ParkingTicket> Create(string value = "parking")
    {
        var plates = new PlateService(Repository, clock);
        var plate = (await plates.GetPlatesAsync()).FirstOrDefault() ?? await plates.AddAsync("ABC123");
        return (await Service.CreateActiveTicketAsync(new(value, "Pdf417", [0, 42, 255], clock.Now), plate)).Ticket;
    }

    [Fact]
    public async Task LifecyclePreservesSnapshotPayloadAndIdentityWithFrozenThenResumedDuration()
    {
        var original = await Create();
        clock.Now += TimeSpan.FromHours(2) + TimeSpan.FromSeconds(3);
        var archived = await Service.ArchiveTicketAsync(original.Id);
        Assert.Equal(clock.Now.UtcDateTime, archived.ArchivedUtc);
        Assert.Equal(archived.ArchivedUtc, archived.UpdatedUtc);
        Assert.Empty(await Service.GetActiveTicketsAsync());
        Assert.Single(await Service.GetArchivedTicketsAsync());
        var preview = new TicketPreviewViewModel(Service, clock, NullLogger<TicketPreviewViewModel>.Instance);
        await preview.LoadAsync(original.Id);
        Assert.True(preview.IsArchived);
        Assert.Equal("02:00:03", preview.Duration);
        clock.Now += TimeSpan.FromDays(1);
        preview.UpdateDuration();
        Assert.Equal("02:00:03", preview.Duration);
        Assert.True(await preview.ChangeStateAsync(ParkingTicketState.Active));
        Assert.Equal("1d 02:00:03", preview.Duration);
        var restored = (await Service.GetTicketAsync(original.Id))!;
        Assert.Equal(original.Id, restored.Id);
        Assert.Null(restored.ArchivedUtc);
        Assert.Equal(clock.Now.UtcDateTime, restored.UpdatedUtc);
        Assert.Equal(original.CreatedUtc, restored.CreatedUtc);
        Assert.Equal(original.BarcodeFormat, restored.BarcodeFormat);
        Assert.Equal(original.BarcodeValue, restored.BarcodeValue);
        Assert.Equal(original.RawBarcodeData, restored.RawBarcodeData);
        Assert.Equal(original.PlateNumberSnapshot, restored.PlateNumberSnapshot);
        Assert.Empty(await Service.GetArchivedTicketsAsync());
        Assert.Single(await Service.GetActiveTicketsAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteIsAPersistentTombstoneExcludedFromListsAndPreview(bool archiveFirst)
    {
        var ticket = await Create();
        clock.Now += TimeSpan.FromMinutes(5);
        if (archiveFirst) await Service.ArchiveTicketAsync(ticket.Id);
        clock.Now += TimeSpan.FromMinutes(5);
        var deleted = await Service.DeleteTicketAsync(ticket.Id);
        Assert.Equal(ParkingTicketState.Deleted, deleted.State);
        Assert.Equal(clock.Now.UtcDateTime, deleted.DeletedUtc);
        Assert.Equal(deleted.DeletedUtc, deleted.UpdatedUtc);
        Assert.Null(await Service.GetTicketAsync(ticket.Id));
        Assert.Empty(await Service.GetActiveTicketsAsync());
        Assert.Empty(await Service.GetArchivedTicketsAsync());
        var tombstone = (await Repository.GetTicketAsync(ticket.Id))!;
        Assert.Equal(ticket.BarcodeValue, tombstone.BarcodeValue);
        Assert.Equal(ticket.CreatedUtc, tombstone.CreatedUtc);
        Assert.Equal(ticket.RawBarcodeData, tombstone.RawBarcodeData);
        await Assert.ThrowsAsync<TicketOperationException>(() => Service.RestoreTicketAsync(ticket.Id));
    }

    [Fact]
    public async Task ActiveOrdersByCreationArchivedOrdersByArchivalAndListRefreshes()
    {
        var old = await Create("old");
        clock.Now += TimeSpan.FromHours(1);
        var newer = await Create("new");
        Assert.Equal(new[] { newer.Id, old.Id }, (await Service.GetActiveTicketsAsync()).Select(t => t.Id));
        clock.Now += TimeSpan.FromMinutes(1);
        await Service.ArchiveTicketAsync(newer.Id);
        clock.Now += TimeSpan.FromMinutes(1);
        await Service.ArchiveTicketAsync(old.Id);
        Assert.Equal(new[] { old.Id, newer.Id }, (await Service.GetArchivedTicketsAsync()).Select(t => t.Id));
        var model = new TicketsViewModel(Service, clock, NullLogger<TicketsViewModel>.Instance);
        await model.LoadAsync();
        Assert.True(model.IsActive);
        Assert.Empty(model.Items);
        await model.LoadAsync(true);
        Assert.Equal(new[] { old.Id, newer.Id }, model.Items.Select(t => t.Id));
        var duration = model.Items[0].Duration;
        clock.Now += TimeSpan.FromHours(1);
        model.UpdateDurations();
        Assert.Equal(duration, model.Items[0].Duration);
        await Service.RestoreTicketAsync(old.Id);
        await model.LoadAsync(false);
        Assert.Single(model.Items);
        var before = model.Items[0].Duration;
        clock.Now += TimeSpan.FromSeconds(1);
        model.UpdateDurations();
        Assert.NotEqual(before, model.Items[0].Duration);
        await Service.DeleteTicketAsync(old.Id);
        await model.LoadAsync();
        Assert.Empty(model.Items);
    }

    [Fact]
    public async Task RestoreConflictLeavesBothTicketsAndTimestampsUnchanged()
    {
        var old = await Create();
        clock.Now += TimeSpan.FromHours(1);
        var archived = await Service.ArchiveTicketAsync(old.Id);
        var active = await Create();
        var preview = new TicketPreviewViewModel(Service, clock, NullLogger<TicketPreviewViewModel>.Instance);
        await preview.LoadAsync(old.Id);
        Assert.False(await preview.ChangeStateAsync(ParkingTicketState.Active));
        Assert.True(preview.IsArchived);
        Assert.Contains("active ticket already", preview.Status);
        var after = (await Service.GetTicketAsync(old.Id))!;
        Assert.Equal(archived.UpdatedUtc, after.UpdatedUtc);
        Assert.Equal(archived.ArchivedUtc, after.ArchivedUtc);
        Assert.Equal(active.Id, Assert.Single(await Service.GetActiveTicketsAsync()).Id);
    }

    [Fact]
    public async Task DatabaseMutationFailureDoesNotReportSuccessOrAlterStoredState()
    {
        var ticket = await Create();
        using var connection = new SqliteConnection($"Data Source={Path.Combine(directory, "tickets.db3")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER RejectUpdate BEFORE UPDATE ON ParkingTickets BEGIN SELECT RAISE(ABORT, 'disk failure'); END;";
        command.ExecuteNonQuery();
        var preview = new TicketPreviewViewModel(Service, clock, NullLogger<TicketPreviewViewModel>.Instance);
        await preview.LoadAsync(ticket.Id);
        Assert.False(await preview.ChangeStateAsync(ParkingTicketState.Archived));
        Assert.True(preview.IsActive);
        Assert.True(preview.CanAct);
        Assert.Contains("Couldn’t update", preview.Status);
        Assert.Equal(ticket.UpdatedUtc, (await Service.GetTicketAsync(ticket.Id))!.UpdatedUtc);
        Assert.Single(await Service.GetActiveTicketsAsync());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
