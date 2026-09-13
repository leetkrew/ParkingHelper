using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class TicketPlateEditingTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParkingHelper.PlateEditing", Guid.NewGuid().ToString());
    private string DatabasePath => Path.Combine(directory, "parking.db3");
    private SqliteParkingRepository Repository => new(DatabasePath);
    private readonly Clock clock = new();
    private PlateService Plates => new(Repository, clock);
    private TicketService Tickets => new(Repository, clock);
    private TicketPreviewViewModel Preview() => new(Tickets, clock, NullLogger<TicketPreviewViewModel>.Instance, plates: Plates);

    private async Task<(ParkingTicket Ticket, VehiclePlate Other)> Setup(bool archived)
    {
        var current = await Plates.AddAsync("ABC123");
        var other = await Plates.AddAsync("DEF456");
        var ticket = (await Tickets.CreateActiveTicketAsync(new("original payload", "Pdf417", [1, 2, 255], clock.Now), current)).Ticket;
        clock.Now += TimeSpan.FromHours(1);
        if (archived) ticket = await Tickets.ArchiveTicketAsync(ticket.Id);
        clock.Now += TimeSpan.FromHours(1);
        return (ticket, other);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangePreservesIdentityPayloadStateAndStartWhileUpdatingCurrentSnapshot(bool archived)
    {
        var (original, other) = await Setup(archived);
        await Plates.UpdateAsync(other.Id, "UPDATED");
        var changed = await Tickets.ChangeTicketPlateAsync(original.Id, other.Id);
        Assert.Equal(original.Id, changed.Id);
        Assert.Equal(original.CreatedUtc, changed.CreatedUtc);
        Assert.Equal(original.State, changed.State);
        Assert.Equal(original.ArchivedUtc, changed.ArchivedUtc);
        Assert.Equal(original.DeletedUtc, changed.DeletedUtc);
        Assert.Equal(original.BarcodeValue, changed.BarcodeValue);
        Assert.Equal(original.BarcodeFormat, changed.BarcodeFormat);
        Assert.Equal(original.RawBarcodeData, changed.RawBarcodeData);
        Assert.Equal(other.Id, changed.VehiclePlateId);
        Assert.Equal("UPDATED", changed.PlateNumberSnapshot);
        Assert.Equal(clock.Now.UtcDateTime, changed.UpdatedUtc);
        Assert.True(changed.UpdatedUtc > original.UpdatedUtc);
        var stored = (await Tickets.GetTicketAsync(original.Id))!;
        Assert.Equal(changed.VehiclePlateId, stored.VehiclePlateId);
        Assert.Equal(changed.PlateNumberSnapshot, stored.PlateNumberSnapshot);
        Assert.Single(await Repository.GetTicketsAsync(original.State));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditorUsesOrderedSavedPlatesPreselectsByIdAndWritesOnlyAfterSave(bool archived)
    {
        var (original, other) = await Setup(archived);
        await Plates.UpdateAsync(original.VehiclePlateId, "RENAMED");
        await Plates.MoveAsync(other.Id, -1);
        var model = Preview();
        await model.LoadAsync(original.Id);
        await model.BeginEditPlateAsync();
        Assert.True(model.IsEditingPlate);
        Assert.Equal(new[] { other.Id, original.VehiclePlateId }, model.PlateChoices.Select(p => p.Id));
        Assert.Equal(original.VehiclePlateId, model.SelectedPlate!.Id);
        Assert.Equal("RENAMED", Assert.Single(model.PlateChoices, p => p.IsSelected).PlateNumber);
        Assert.Equal("ABC123", model.PlateNumber);
        model.SelectPlate(Guid.NewGuid());
        Assert.Equal(original.VehiclePlateId, model.SelectedPlate.Id);
        model.SelectPlate(other.Id);
        Assert.Equal(other.Id, Assert.Single(model.PlateChoices, p => p.IsSelected).Id);
        Assert.Equal(original.UpdatedUtc, (await Tickets.GetTicketAsync(original.Id))!.UpdatedUtc);
        model.CancelEditPlate();
        Assert.False(model.IsEditingPlate);
        Assert.Equal(original.VehiclePlateId, (await Tickets.GetTicketAsync(original.Id))!.VehiclePlateId);
        await model.BeginEditPlateAsync();
        model.SelectPlate(other.Id);
        Assert.True(await model.ConfirmPlateAsync());
        Assert.False(model.IsEditingPlate);
        Assert.Equal("DEF456", model.PlateNumber);
        Assert.Equal(archived, model.IsArchived);
        Assert.Equal(original.ArchivedUtc, (await Tickets.GetTicketAsync(original.Id))!.ArchivedUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SamePlateDoesNotUpdateEvenAfterPlateTextWasEdited(bool archived)
    {
        var (original, _) = await Setup(archived);
        await Plates.UpdateAsync(original.VehiclePlateId, "RENAMED");
        RejectTicketUpdates();
        var unchanged = await Tickets.ChangeTicketPlateAsync(original.Id, original.VehiclePlateId);
        Assert.Equal(original.UpdatedUtc, unchanged.UpdatedUtc);
        Assert.Equal(original.PlateNumberSnapshot, unchanged.PlateNumberSnapshot);
        var model = Preview();
        await model.LoadAsync(original.Id);
        await model.BeginEditPlateAsync();
        Assert.True(await model.ConfirmPlateAsync());
        Assert.False(model.IsEditingPlate);
        Assert.Equal(original.UpdatedUtc, (await Tickets.GetTicketAsync(original.Id))!.UpdatedUtc);
    }

    [Fact]
    public async Task MissingDeletedOrInvalidTargetsAreRejectedWithoutChanges()
    {
        var (original, other) = await Setup(false);
        await Plates.DeleteAsync(other.Id);
        await Assert.ThrowsAsync<TicketOperationException>(() => Tickets.ChangeTicketPlateAsync(original.Id, other.Id));
        await Assert.ThrowsAsync<TicketOperationException>(() => Tickets.ChangeTicketPlateAsync(original.Id, Guid.Empty));
        await Assert.ThrowsAsync<TicketOperationException>(() => Tickets.ChangeTicketPlateAsync(Guid.Empty, original.VehiclePlateId));
        await Assert.ThrowsAsync<TicketOperationException>(() => Tickets.ChangeTicketPlateAsync(Guid.NewGuid(), original.VehiclePlateId));
        Assert.Equal(original.UpdatedUtc, (await Tickets.GetTicketAsync(original.Id))!.UpdatedUtc);
        await Tickets.DeleteTicketAsync(original.Id);
        await Assert.ThrowsAsync<TicketOperationException>(() => Tickets.ChangeTicketPlateAsync(original.Id, original.VehiclePlateId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedSaveRetainsOriginalDisplayAndAllowsRetry(bool removePlate)
    {
        var (original, other) = await Setup(false);
        var model = Preview();
        await model.LoadAsync(original.Id);
        await model.BeginEditPlateAsync();
        model.SelectPlate(other.Id);
        if (removePlate) await Plates.DeleteAsync(other.Id); else RejectTicketUpdates();
        Assert.False(await model.ConfirmPlateAsync());
        Assert.True(model.IsEditingPlate);
        Assert.True(model.CanConfirmPlate);
        Assert.Equal(original.PlateNumberSnapshot, model.PlateNumber);
        var stored = (await Tickets.GetTicketAsync(original.Id))!;
        Assert.Equal(original.VehiclePlateId, stored.VehiclePlateId);
        Assert.Equal(original.UpdatedUtc, stored.UpdatedUtc);
        Assert.DoesNotContain("updated", model.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditEntryAndPlatePreservesScanAndUsesEntryForDuration(bool archived)
    {
        var (original, other) = await Setup(archived);
        Assert.Equal(original.ScannedUtc, original.EntryUtc);
        var entry = original.ScannedUtc.AddHours(-3);
        var changed = await Tickets.EditTicketAsync(original.Id, other.Id, entry);
        var stored = (await Tickets.GetTicketAsync(original.Id))!;
        Assert.Equal(entry, stored.EntryUtc);
        Assert.Equal(DateTimeKind.Utc, stored.EntryUtc.Kind);
        Assert.Equal(original.ScannedUtc, stored.ScannedUtc);
        Assert.Equal(original.Id, stored.Id);
        Assert.Equal(original.State, stored.State);
        Assert.Equal(original.ArchivedUtc, stored.ArchivedUtc);
        Assert.Equal(original.BarcodeValue, stored.BarcodeValue);
        Assert.Equal(original.BarcodeFormat, stored.BarcodeFormat);
        Assert.Equal(original.RawBarcodeData, stored.RawBarcodeData);
        Assert.Equal(other.Id, stored.VehiclePlateId);
        Assert.Equal(other.PlateNumber, stored.PlateNumberSnapshot);
        Assert.Equal(clock.Now.UtcDateTime, stored.UpdatedUtc);
        Assert.Equal((archived ? stored.ArchivedUtc!.Value : clock.Now.UtcDateTime) - entry,
            TicketDuration.Calculate(stored, clock.Now.UtcDateTime));
        RejectTicketUpdates();
        Assert.Equal(changed.UpdatedUtc, (await Tickets.EditTicketAsync(original.Id, other.Id, entry)).UpdatedUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidEntryRejectsEntireEditAndBoundaryIsAllowed(bool archived)
    {
        var (original, other) = await Setup(archived);
        var limit = archived ? original.ArchivedUtc!.Value : clock.Now.UtcDateTime;
        await Assert.ThrowsAsync<TicketOperationException>(() => Tickets.EditTicketAsync(original.Id, other.Id, limit.AddTicks(1)));
        await Assert.ThrowsAsync<TicketOperationException>(() => Tickets.EditTicketAsync(original.Id, other.Id, DateTime.SpecifyKind(limit, DateTimeKind.Local)));
        var unchanged = (await Tickets.GetTicketAsync(original.Id))!;
        Assert.Equal(original.EntryUtc, unchanged.EntryUtc);
        Assert.Equal(original.VehiclePlateId, unchanged.VehiclePlateId);
        Assert.Equal(original.UpdatedUtc, unchanged.UpdatedUtc);
        Assert.Equal(limit, (await Tickets.EditTicketAsync(original.Id, other.Id, limit)).EntryUtc);
    }

    [Fact]
    public async Task EditorSavesLocalEntryAndDetailsIncludeImmutableScanAndArchive()
    {
        var (original, _) = await Setup(true);
        var model = Preview();
        await model.LoadAsync(original.Id);
        await model.BeginEditPlateAsync();
        var local = original.EntryUtc.AddHours(-2).ToLocalTime();
        model.EntryDate = local.Date;
        model.EntryTime = local.TimeOfDay;
        Assert.True(await model.ConfirmPlateAsync());
        var saved = (await Tickets.GetTicketAsync(original.Id))!;
        Assert.Equal(original.EntryUtc.AddHours(-2), saved.EntryUtc);
        Assert.Equal(original.PlateNumberSnapshot, saved.PlateNumberSnapshot);
        Assert.Contains(original.Id.ToString(), model.Details);
        Assert.Contains(original.BarcodeValue, model.Details);
        Assert.Contains(original.BarcodeFormat, model.Details);
        Assert.Contains(original.ScannedUtc.ToLocalTime().ToString("MMM d, yyyy · h:mm:ss tt"), model.Details);
        Assert.Contains("Archived:", model.Details);
    }

    [Fact]
    public async Task VersionThreeMigrationBackfillsAllStatesAndIsIdempotent()
    {
        var (active, _) = await Setup(false);
        var plate = (await Plates.GetPlatesAsync())[0];
        var archived = (await Tickets.CreateActiveTicketAsync(new("archived", "QrCode", null, clock.Now), plate)).Ticket;
        await Tickets.ArchiveTicketAsync(archived.Id);
        var deleted = (await Tickets.CreateActiveTicketAsync(new("deleted", "QrCode", null, clock.Now), plate)).Ticket;
        await Tickets.DeleteTicketAsync(deleted.Id);
        using (var db = new SqliteConnection($"Data Source={DatabasePath}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "ALTER TABLE ParkingTickets DROP COLUMN EntryUtc; PRAGMA user_version = 3;";
            command.ExecuteNonQuery();
        }
        foreach (var id in new[] { active.Id, archived.Id, deleted.Id })
        {
            var migrated = (await Repository.GetTicketAsync(id))!;
            Assert.Equal(migrated.ScannedUtc, migrated.EntryUtc);
            Assert.Equal(id, migrated.Id);
        }
        var changed = await Tickets.EditTicketAsync(active.Id, active.VehiclePlateId, active.ScannedUtc.AddDays(-1));
        Assert.Equal(changed.EntryUtc, (await Repository.GetTicketAsync(active.Id))!.EntryUtc);
    }

    private void RejectTicketUpdates()
    {
        using var db = new SqliteConnection($"Data Source={DatabasePath}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TRIGGER RejectTicketUpdate BEFORE UPDATE ON ParkingTickets BEGIN SELECT RAISE(ABORT, 'test failure'); END;";
        command.ExecuteNonQuery();
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
