using Microsoft.Data.Sqlite;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class TicketCreationTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParkingHelper.Tickets", Guid.NewGuid().ToString());
    private string DatabasePath => Path.Combine(directory, "parking.db3");
    private SqliteParkingRepository Repository() => new(DatabasePath);
    private static readonly DateTimeOffset CapturedUtc = new(2026, 9, 11, 12, 42, 0, TimeSpan.Zero);
    private Task<VehiclePlate> AddPlate(string number) => new PlateService(Repository(), TimeProvider.System).AddAsync(number);
    private static ScanResult Scan(string format = "QrCode", string value = "exact ticket") => new(value, format, [0, 255, 42], CapturedUtc);

    [Theory]
    [InlineData("QrCode")]
    [InlineData("Pdf417")]
    [InlineData("Aztec")]
    [InlineData("DataMatrix")]
    public async Task SavesOriginalBarcodeSnapshotAndCaptureTimestampAcrossReopen(string format)
    {
        var plate = await AddPlate("ABC123");
        var created = await new TicketService(Repository()).CreateActiveTicketAsync(Scan(format, "  ticket\n"), plate);
        Assert.True(created.Created);
        var ticket = (await Repository().GetTicketAsync(created.Ticket.Id))!;
        Assert.NotEqual(Guid.Empty, ticket.Id);
        Assert.Equal(plate.Id, ticket.VehiclePlateId);
        Assert.Equal("ABC123", ticket.PlateNumberSnapshot);
        Assert.Equal("  ticket\n", ticket.BarcodeValue);
        Assert.Equal(format, ticket.BarcodeFormat);
        Assert.Equal(new byte[] { 0, 255, 42 }, ticket.RawBarcodeData);
        Assert.Equal(CapturedUtc.UtcDateTime, ticket.CreatedUtc);
        Assert.Equal(ticket.CreatedUtc, ticket.UpdatedUtc);
        Assert.Equal(DateTimeKind.Utc, ticket.CreatedUtc.Kind);
        Assert.Equal(ParkingTicketState.Active, ticket.State);
        await new PlateService(Repository(), TimeProvider.System).UpdateAsync(plate.Id, "EDITED");
        Assert.Equal("ABC123", (await Repository().GetTicketAsync(ticket.Id))!.PlateNumberSnapshot);
    }

    [Fact]
    public async Task DuplicateOnAnotherPlateReturnsExistingTicketWithoutModification()
    {
        var first = await AddPlate("FIRST");
        var second = await AddPlate("SECOND");
        var service = new TicketService(Repository());
        var original = await service.CreateActiveTicketAsync(Scan(), first);
        var duplicate = await service.CreateActiveTicketAsync(new("exact ticket", "QrCode", [99], CapturedUtc.AddHours(1)), second);
        Assert.False(duplicate.Created);
        Assert.Equal(original.Ticket.Id, duplicate.Ticket.Id);
        Assert.Equal(first.Id, duplicate.Ticket.VehiclePlateId);
        Assert.Equal("FIRST", duplicate.Ticket.PlateNumberSnapshot);
        Assert.Equal(CapturedUtc.UtcDateTime, duplicate.Ticket.UpdatedUtc);
        Assert.Equal(new byte[] { 0, 255, 42 }, duplicate.Ticket.RawBarcodeData);
        Assert.Single(await Repository().GetTicketsAsync(ParkingTicketState.Active));
    }

    [Fact]
    public async Task SeparateRepositoryInstancesCannotRaceToInsertDuplicate()
    {
        var first = await AddPlate("FIRST");
        var second = await AddPlate("SECOND");
        var attempts = Enumerable.Range(0, 16).Select(n => new TicketService(Repository())
            .CreateActiveTicketAsync(Scan(), n % 2 == 0 ? first : second));
        var results = await Task.WhenAll(attempts);
        Assert.Single(results, r => r.Created);
        Assert.Single(results.Select(r => r.Ticket.Id).Distinct());
        Assert.Single(await Repository().GetTicketsAsync(ParkingTicketState.Active));
    }

    [Fact]
    public async Task IdentityUsesExactValueAndFormatAndOnlyActiveTickets()
    {
        var plate = await AddPlate("ABC123");
        var service = new TicketService(Repository());
        var original = await service.CreateActiveTicketAsync(Scan(), plate);
        Assert.True((await service.CreateActiveTicketAsync(Scan("Pdf417"), plate)).Created);
        Assert.True((await service.CreateActiveTicketAsync(Scan(value: "Exact ticket"), plate)).Created);
        Assert.True((await service.CreateActiveTicketAsync(Scan(value: "exact ticket "), plate)).Created);
        // Seed an already archived record through the existing foundation API, not a new UI operation.
        await Repository().ChangeTicketStateAsync(original.Ticket.Id, ParkingTicketState.Archived, CapturedUtc.AddMinutes(1).UtcDateTime);
        Assert.True((await service.CreateActiveTicketAsync(Scan(), plate)).Created);
    }

    [Fact]
    public async Task InvalidCaptureAndDeletedPlateDoNotWriteTickets()
    {
        var plate = await AddPlate("ABC123");
        var service = new TicketService(Repository());
        await Assert.ThrowsAsync<TicketOperationException>(() => service.CreateActiveTicketAsync(null!, plate));
        await Assert.ThrowsAsync<TicketOperationException>(() => service.CreateActiveTicketAsync(Scan(value: "  "), plate));
        await Assert.ThrowsAsync<TicketOperationException>(() => service.CreateActiveTicketAsync(Scan(), null!));
        await Assert.ThrowsAsync<TicketOperationException>(() => service.CreateActiveTicketAsync(new("value", "QrCode", null, default), plate));
        await new PlateService(Repository(), TimeProvider.System).DeleteAsync(plate.Id);
        await Assert.ThrowsAsync<TicketOperationException>(() => service.CreateActiveTicketAsync(Scan(), plate));
        Assert.Empty(await Repository().GetTicketsAsync(ParkingTicketState.Active));
    }

    [Fact]
    public async Task InsertFailureRollsBackAndNextScanCanSucceed()
    {
        var plate = await AddPlate("ABC123");
        ExecuteSql("CREATE TRIGGER FailSave BEFORE INSERT ON ParkingTickets BEGIN SELECT RAISE(ABORT, 'test storage failure'); END;");
        var service = new TicketService(Repository());
        await Assert.ThrowsAsync<SqliteException>(() => service.CreateActiveTicketAsync(Scan(), plate));
        Assert.Empty(await Repository().GetTicketsAsync(ParkingTicketState.Active));
        ExecuteSql("DROP TRIGGER FailSave;");
        Assert.True((await service.CreateActiveTicketAsync(Scan(), plate)).Created);
        Assert.Single(await Repository().GetTicketsAsync(ParkingTicketState.Active));
    }

    [Fact]
    public async Task DatabaseTriggerRejectsDuplicateFromLegacyInsertPath()
    {
        var plate = await AddPlate("ABC123");
        var created = await new TicketService(Repository()).CreateActiveTicketAsync(Scan(), plate);
        await Assert.ThrowsAsync<SqliteException>(() => Repository().AddTicketAsync(created.Ticket with { Id = Guid.NewGuid() }));
        Assert.Single(await Repository().GetTicketsAsync(ParkingTicketState.Active));
    }

    [Fact]
    public async Task VersionTwoMigrationPreservesLegacyDuplicatesAndBackfillsSnapshot()
    {
        Directory.CreateDirectory(directory);
        var plateId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        // Real v2 column shape, including duplicate rows permitted by the old schema.
        ExecuteSql($"""
            CREATE TABLE VehiclePlates (Id TEXT PRIMARY KEY, PlateNumber TEXT NOT NULL,
                CreatedUtc INTEGER NOT NULL, UpdatedUtc INTEGER NOT NULL, SortOrder INTEGER NOT NULL);
            CREATE TABLE ParkingTickets (Id TEXT PRIMARY KEY, VehiclePlateId TEXT REFERENCES VehiclePlates(Id),
                BarcodeFormat TEXT NOT NULL, BarcodeValue TEXT NOT NULL, State INTEGER NOT NULL,
                CreatedUtc INTEGER NOT NULL, UpdatedUtc INTEGER NOT NULL, ArchivedUtc INTEGER NULL, DeletedUtc INTEGER NULL);
            INSERT INTO VehiclePlates VALUES ('{plateId}', 'LEGACY', {CapturedUtc.Ticks}, {CapturedUtc.Ticks}, 0);
            INSERT INTO ParkingTickets VALUES ('{firstId}', '{plateId}', 'QrCode', 'exact ticket', 0, {CapturedUtc.Ticks}, {CapturedUtc.Ticks}, NULL, NULL);
            INSERT INTO ParkingTickets VALUES ('{secondId}', '{plateId}', 'QrCode', 'exact ticket', 0, {CapturedUtc.AddMinutes(1).Ticks}, {CapturedUtc.AddMinutes(1).Ticks}, NULL, NULL);
            PRAGMA user_version = 2;
            """);
        var repository = Repository();
        var records = await repository.GetTicketsAsync(ParkingTicketState.Active);
        Assert.Equal(2, records.Count);
        Assert.All(records, r => { Assert.Equal("LEGACY", r.PlateNumberSnapshot); Assert.Null(r.RawBarcodeData); });
        var plate = Assert.Single(await repository.GetPlatesAsync());
        var duplicate = await new TicketService(repository).CreateActiveTicketAsync(Scan(), plate);
        Assert.False(duplicate.Created);
        Assert.Equal(firstId, duplicate.Ticket.Id);
        Assert.Equal(2, (await Repository().GetTicketsAsync(ParkingTicketState.Active)).Count);
    }

    private void ExecuteSql(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
