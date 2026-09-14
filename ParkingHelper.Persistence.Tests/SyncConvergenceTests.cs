using System.Text.Json;
using Microsoft.Data.Sqlite;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class SyncConvergenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParkingConvergence", Guid.NewGuid().ToString());
    private string PathFor(string device) => Path.Combine(directory, device + ".db3");
    private SqliteParkingRepository Repo(string device = "a") => new(PathFor(device));
    private static readonly DateTime Now = new(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}");
    private static VehiclePlate Plate(int id, string text) => new(Id(id), text, Now, Now);
    private static ParkingTicket Ticket(int id, int plate) => new(Id(id), Id(plate), "QR_CODE", $"PRIVATE-{id}", ParkingTicketState.Active, Now, Now, null, null)
        { PlateNumberSnapshot = "PRIVATE-SNAPSHOT", RawBarcodeData = [1, 2, 3] };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedTicketDoesNotBlockPlateDeletionAndCannotResurrect(bool archiveFirst)
    {
        var repository = Repo();
        var ticket = Ticket(11, 1);
        await repository.AddPlateAsync(Plate(1, "ABC123"));
        await repository.AddTicketAsync(ticket);
        if (archiveFirst) await repository.ChangeTicketStateAsync(ticket.Id, ParkingTicketState.Archived, Now.AddHours(1));
        await repository.ChangeTicketStateAsync(ticket.Id, ParkingTicketState.Deleted, Now.AddHours(2));
        await repository.DeletePlateAsync(Id(1), Now.AddHours(3));
        Assert.Empty(await Repo().GetPlatesAsync());
        Assert.Null(await Repo().GetTicketAsync(ticket.Id));
        foreach (var state in Enum.GetValues<ParkingTicketState>()) Assert.Empty(await Repo().GetTicketsAsync(state));
        var records = await repository.GetSyncRecordsAsync();
        var tombstone = records.Single(r => r.Id == ticket.Id);
        Assert.True(tombstone.IsDeleted);
        Assert.Equal(Guid.Empty, tombstone.Ticket!.VehiclePlateId);
        Assert.Empty(tombstone.Ticket.BarcodeValue);
        Assert.Null(tombstone.Ticket.PlateNumberSnapshot);
        Assert.Null(tombstone.Ticket.RawBarcodeData);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(records));
        var offline = SyncRecord.ForTicket(ticket with { UpdatedUtc = Now.AddDays(1) });
        var merged = SyncMergeEngine.Merge(records, [offline]);
        await Repo("b").ApplySyncRecordsAsync(merged);
        Assert.Null(await Repo("b").GetTicketAsync(ticket.Id));
        Assert.True((await Repo("b").GetSyncRecordsAsync()).Single(r => r.Id == ticket.Id).IsDeleted);
        Assert.Equal(0L, await Scalar("a", "SELECT COUNT(*) FROM ParkingTickets;"));
        Assert.Equal(0L, await Scalar("a", "SELECT COUNT(*) FROM VehiclePlates;"));
        var counts = DriveDiagnosticCounts.From(records);
        Assert.Equal(0, counts.Tickets);
        Assert.Equal(1, counts.Deleted);
    }

    [Theory]
    [InlineData("abc123")]
    [InlineData(" ABC123 ")]
    [InlineData("aBc123")]
    public async Task IndependentlyCreatedPlatesConvergeAndRemapEveryTicket(string otherText)
    {
        var a = Repo("a"); var b = Repo("b");
        await a.AddPlateAsync(Plate(2, "ABC123"));
        await b.AddPlateAsync(Plate(1, otherText));
        await a.AddTicketAsync(Ticket(11, 2));
        await b.AddTicketAsync(Ticket(12, 1));
        var aRecords = await a.GetSyncRecordsAsync(); var bRecords = await b.GetSyncRecordsAsync();
        var merged = SyncMergeEngine.Merge(aRecords, bRecords);
        Assert.Equal(JsonSerializer.Serialize(merged), JsonSerializer.Serialize(SyncMergeEngine.Merge(bRecords, aRecords)));
        await a.ApplySyncRecordsAsync(merged); await b.ApplySyncRecordsAsync(merged);
        foreach (var repository in new[] { Repo("a"), Repo("b") })
        {
            var plate = Assert.Single(await repository.GetPlatesAsync());
            Assert.Equal(Id(1), plate.Id); Assert.Equal("ABC123", plate.PlateNumber);
            var tickets = await repository.GetTicketsAsync(ParkingTicketState.Active);
            Assert.Equal(2, tickets.Count);
            Assert.All(tickets, ticket => Assert.Equal(plate.Id, ticket.VehiclePlateId));
            Assert.Equal(new[] { Id(11), Id(12) }, tickets.Select(t => t.Id).Order());
        }
        // A later offline mutation supplies only a ticket with the former plate ID.
        await a.ApplySyncRecordsAsync([SyncRecord.ForTicket(Ticket(11, 2) with { UpdatedUtc = Now.AddDays(1) })]);
        Assert.Equal(Id(1), (await a.GetTicketAsync(Id(11)))!.VehiclePlateId);
        await b.ApplySyncRecordsAsync(SyncMergeEngine.Merge(await b.GetSyncRecordsAsync(), await a.GetSyncRecordsAsync()));
        Assert.Equal(JsonSerializer.Serialize(await a.GetSyncRecordsAsync()), JsonSerializer.Serialize(await b.GetSyncRecordsAsync()));
        Assert.Equal(0L, await Scalar("a", "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(1L, await Scalar("a", "SELECT COUNT(*) FROM VehiclePlates;"));
    }

    [Fact]
    public async Task SameTicketIdUpdatesOneRecordAndTerminalDeletionWins()
    {
        var repository = Repo(); var original = Ticket(11, 1);
        await repository.ApplySyncRecordsAsync([SyncRecord.ForPlate(Plate(1, " abc123 ")), SyncRecord.ForTicket(original)]);
        var edited = original with { EntryUtc = Now.AddHours(-2), UpdatedUtc = Now.AddHours(2) };
        await repository.ApplySyncRecordsAsync([SyncRecord.ForTicket(edited)]);
        Assert.Equal(edited.EntryUtc, Assert.Single(await repository.GetTicketsAsync(ParkingTicketState.Active)).EntryUtc);
        await repository.ApplySyncRecordsAsync([SyncRecord.ForTicket(original)]);
        Assert.Equal(edited.UpdatedUtc, (await repository.GetTicketAsync(original.Id))!.UpdatedUtc);
        var deleted = edited.ChangeState(ParkingTicketState.Deleted, Now.AddHours(3));
        await repository.ApplySyncRecordsAsync([SyncRecord.ForTicket(deleted)]);
        await repository.ApplySyncRecordsAsync([SyncRecord.ForTicket(original with { UpdatedUtc = Now.AddYears(1) })]);
        Assert.Null(await repository.GetTicketAsync(original.Id));
        Assert.Single(await repository.GetSyncRecordsAsync(), r => r.RecordType == SyncRecordType.ParkingTicket);
    }

    [Fact]
    public async Task AliasChainsConvergeWhenThirdDeviceHasLowerCanonicalGuid()
    {
        SyncRecord[] Device(int id) => [SyncRecord.ForPlate(Plate(id, "abc123")), SyncRecord.ForTicket(Ticket(id + 10, id))];
        var ab = SyncMergeEngine.Merge(Device(3), Device(2));
        var bc = SyncMergeEngine.Merge(Device(2), Device(1));
        var one = SyncMergeEngine.Merge(ab, bc);
        var two = SyncMergeEngine.Merge(Device(3), SyncMergeEngine.Merge(Device(2), Device(1)));
        Assert.Equal(JsonSerializer.Serialize(one), JsonSerializer.Serialize(two));
        Assert.All(one.Where(r => r.CanonicalPlateId.HasValue), r => Assert.Equal(Id(1), r.CanonicalPlateId));
        await Repo().ApplySyncRecordsAsync(one);
        Assert.Single(await Repo().GetPlatesAsync());
        Assert.All(await Repo().GetTicketsAsync(ParkingTicketState.Active), t => Assert.Equal(Id(1), t.VehiclePlateId));
    }

    [Fact]
    public async Task VersionFiveRepairNormalizesDeduplicatesAndCompactsDeletedRows()
    {
        var repository = Repo();
        await repository.AddPlateAsync(Plate(1, "ABC123"));
        await repository.AddPlateAsync(Plate(2, "OTHER"));
        await repository.AddTicketAsync(Ticket(11, 2));
        await repository.AddTicketAsync(Ticket(12, 1));
        await Sql("a", $"""
            UPDATE VehiclePlates SET PlateNumber = ' abc123 ' WHERE Id = '{Id(2)}';
            UPDATE ParkingTickets SET PlateNumberSnapshot = ' abc123 ' WHERE Id = '{Id(11)}';
            UPDATE ParkingTickets SET State = 2, DeletedUtc = UpdatedUtc WHERE Id = '{Id(12)}';
            ALTER TABLE SyncTombstones DROP COLUMN DeletedUtc;
            ALTER TABLE SyncTombstones DROP COLUMN CanonicalPlateId;
            PRAGMA user_version = 5;
            """);
        var repaired = Repo();
        Assert.Equal("ABC123", Assert.Single(await repaired.GetPlatesAsync()).PlateNumber);
        Assert.Equal(Id(1), (await repaired.GetTicketAsync(Id(11)))!.VehiclePlateId);
        Assert.Equal("ABC123", (await repaired.GetTicketAsync(Id(11)))!.PlateNumberSnapshot);
        Assert.Null(await repaired.GetTicketAsync(Id(12)));
        Assert.Equal(Guid.Empty, (await repaired.GetSyncRecordsAsync()).Single(r => r.Id == Id(12)).Ticket!.VehiclePlateId);
        Assert.Equal(1L, await Scalar("a", "SELECT COUNT(*) FROM ParkingTickets;"));
        Assert.Equal(0L, await Scalar("a", "SELECT COUNT(*) FROM pragma_foreign_key_check;"));
        Assert.Equal(6L, await Scalar("a", "PRAGMA user_version;"));
    }

    [Theory]
    [InlineData(" abc123 ")]
    [InlineData("aBc123")]
    public async Task CloudImportNormalizesPlateAndSnapshotWithoutChangingBarcode(string text)
    {
        var ticket = Ticket(11, 1) with { PlateNumberSnapshot = text };
        await Repo().ApplySyncRecordsAsync([SyncRecord.ForPlate(Plate(1, text)), SyncRecord.ForTicket(ticket)]);
        Assert.Equal("ABC123", Assert.Single(await Repo().GetPlatesAsync()).PlateNumber);
        var saved = (await Repo().GetTicketAsync(ticket.Id))!;
        Assert.Equal("ABC123", saved.PlateNumberSnapshot);
        Assert.Equal(ticket.BarcodeValue, saved.BarcodeValue);
        Assert.Equal(ticket.RawBarcodeData, saved.RawBarcodeData);
    }

    private async Task Sql(string device, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={PathFor(device)};Pooling=False");
        await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private async Task<long> Scalar(string device, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={PathFor(device)};Pooling=False");
        await connection.OpenAsync(); using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
