using Microsoft.Data.Sqlite;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class PlateManagementTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParkingHelper.Plates.Tests", Guid.NewGuid().ToString());
    private string DatabasePath => Path.Combine(directory, "plates.db3");
    private SqliteParkingRepository Repository() => new(DatabasePath);
    private PlateService Service() => new(Repository(), TimeProvider.System);

    [Theory]
    [InlineData(" abc123 ", "ABC123")]
    [InlineData("ab12cd", "AB12CD")]
    [InlineData("motor1", "MOTOR1")]
    [InlineData(" my custom-vehicle 7 ", "MY CUSTOM-VEHICLE 7")]
    public async Task NormalizesUserDefinedText(string input, string expected)
    {
        var plate = await Service().AddAsync(input);
        Assert.Equal(expected, plate.PlateNumber);
        Assert.NotEqual(Guid.Empty, plate.Id);
        Assert.Equal(DateTimeKind.Utc, plate.CreatedUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, plate.UpdatedUtc.Kind);
        Assert.Equal(plate, Assert.Single(await Service().GetPlatesAsync()));
    }

    [Fact]
    public async Task SetupDependsOnlyOnSavedPlatesAcrossRestarts()
    {
        Assert.True(await Service().NeedsSetupAsync());
        var plate = await Service().AddAsync("ABC123");
        Assert.False(await Service().NeedsSetupAsync());
        await Service().DeleteAsync(plate.Id);
        Assert.True(await Service().NeedsSetupAsync());
        Assert.Empty(await Service().GetPlatesAsync());
    }

    [Fact]
    public async Task EditPreservesIdentityCreationAndPositionAndRejectsDuplicates()
    {
        var first = await Service().AddAsync("ABC123");
        var second = await Service().AddAsync("MOTOR1");
        await Service().UpdateAsync(second.Id, " ab12cd ");
        var edited = (await Service().GetPlatesAsync())[1];
        Assert.Equal(second.Id, edited.Id);
        Assert.Equal(second.CreatedUtc, edited.CreatedUtc);
        Assert.Equal(second.SortOrder, edited.SortOrder);
        Assert.Equal("AB12CD", edited.PlateNumber);
        Assert.True(edited.UpdatedUtc > second.UpdatedUtc);
        await Assert.ThrowsAsync<PlateOperationException>(() => Service().UpdateAsync(second.Id, " abc123 "));
        await Assert.ThrowsAsync<PlateOperationException>(() => Service().AddAsync(" abc123 "));
        Assert.Equal(new[] { first, edited }, await Service().GetPlatesAsync());
        await Service().UpdateAsync(edited.Id, " ab12cd ");
        Assert.Equal(edited, (await Service().GetPlatesAsync())[1]);
    }

    [Fact]
    public async Task EnforcesReasonableLimitOnAddAndEditWithoutTruncation()
    {
        await Assert.ThrowsAsync<PlateOperationException>(() => Service().AddAsync(" \t "));
        await Assert.ThrowsAsync<PlateOperationException>(() => Service().AddAsync(new string('A', 65)));
        var plate = await Service().AddAsync(new string('A', 64));
        await Assert.ThrowsAsync<PlateOperationException>(() => Service().UpdateAsync(plate.Id, new string('B', 65)));
        Assert.Equal(plate, Assert.Single(await Service().GetPlatesAsync()));
    }

    [Fact]
    public async Task ReorderAndDeletePersistDenseOrderAcrossRestart()
    {
        var a = await Service().AddAsync("ZZZ");
        var b = await Service().AddAsync("AAA");
        var c = await Service().AddAsync("MOTOR1");
        Assert.Equal(new[] { a.Id, b.Id, c.Id }, (await Service().GetPlatesAsync()).Select(p => p.Id));
        await Service().MoveAsync(c.Id, -1);
        await Service().MoveAsync(c.Id, -1);
        var reordered = await Service().GetPlatesAsync();
        Assert.Equal(new[] { c.Id, a.Id, b.Id }, reordered.Select(p => p.Id));
        Assert.Equal(new[] { 0, 1, 2 }, reordered.Select(p => p.SortOrder));
        Assert.True(reordered[0].UpdatedUtc > c.UpdatedUtc);
        await Service().MoveAsync(c.Id, -1); // First cannot move up.
        await Service().MoveAsync(b.Id, 1); // Last cannot move down.
        Assert.Equal(reordered, await Service().GetPlatesAsync());
        await Service().MoveAsync(c.Id, 1);
        await Service().DeleteAsync(c.Id);
        var remaining = await Service().GetPlatesAsync();
        Assert.Equal(new[] { a.Id, b.Id }, remaining.Select(p => p.Id));
        Assert.Equal(new[] { 0, 1 }, remaining.Select(p => p.SortOrder));
        var next = await Service().AddAsync("NEXT");
        Assert.Equal(2, next.SortOrder);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Service().MoveAsync(a.Id, 3));
    }

    [Fact]
    public async Task ConcurrentAppendsReceiveDistinctPositions()
    {
        var repository = Repository();
        var service = new PlateService(repository, TimeProvider.System);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => service.AddAsync($"PLATE{i}")));
        Assert.Equal(Enumerable.Range(0, 20), (await Service().GetPlatesAsync()).Select(p => p.SortOrder));
    }

    [Fact]
    public async Task FailedDeletionPreservesExistingTicketAndPlateOrder()
    {
        var repository = Repository();
        var plate = await Service().AddAsync("ABC123");
        var ticket = await new ParkingService(repository, TimeProvider.System).SaveTicketAsync(plate.Id, new("QR_CODE", "123"));
        await Assert.ThrowsAsync<PlateOperationException>(() => Service().DeleteAsync(plate.Id));
        Assert.Equal(plate, Assert.Single(await Service().GetPlatesAsync()));
        Assert.Equal(ticket, await repository.GetTicketAsync(ticket.Id));
    }

    [Fact]
    public async Task MigrationPreservesOldRecordsAndTheirPreviouslyDisplayedOrder()
    {
        Directory.CreateDirectory(directory);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var created = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE VehiclePlates (Id TEXT NOT NULL PRIMARY KEY, PlateNumber TEXT NOT NULL,
                    CreatedUtc INTEGER NOT NULL, UpdatedUtc INTEGER NOT NULL);
                CREATE UNIQUE INDEX IX_VehiclePlates_PlateNumber ON VehiclePlates(PlateNumber);
                CREATE TABLE ParkingTickets (
                    Id TEXT PRIMARY KEY, VehiclePlateId TEXT REFERENCES VehiclePlates(Id),
                    BarcodeFormat TEXT NOT NULL, BarcodeValue TEXT NOT NULL, State INTEGER NOT NULL,
                    CreatedUtc INTEGER NOT NULL, UpdatedUtc INTEGER NOT NULL, ArchivedUtc INTEGER NULL, DeletedUtc INTEGER NULL);
                INSERT INTO VehiclePlates VALUES ($first, 'ZZZ', $time, $time), ($second, 'AAA', $time, $time);
                INSERT INTO ParkingTickets VALUES ('legacy-ticket', $first, 'QrCode', 'legacy-value', 0, $time, $time, NULL, NULL);
                PRAGMA user_version = 1;
                """;
            command.Parameters.AddWithValue("$first", firstId.ToString());
            command.Parameters.AddWithValue("$second", secondId.ToString());
            command.Parameters.AddWithValue("$time", created.Ticks);
            command.ExecuteNonQuery();
        }
        var plates = await Service().GetPlatesAsync();
        Assert.Equal(new[] { secondId, firstId }, plates.Select(p => p.Id));
        Assert.Equal(new[] { 0, 1 }, plates.Select(p => p.SortOrder));
        Assert.All(plates, plate => { Assert.Equal(created, plate.CreatedUtc); Assert.Equal(created, plate.UpdatedUtc); });
        Assert.Equal(plates, await Service().GetPlatesAsync());
        using var check = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "PRAGMA user_version;";
        Assert.Equal(3L, query.ExecuteScalar());
        query.CommandText = "SELECT VehiclePlateId FROM ParkingTickets WHERE Id = 'legacy-ticket';";
        Assert.Equal(firstId.ToString(), query.ExecuteScalar());
    }

    [Fact]
    public async Task EditAndMoveKeepUtcMonotonicWhenClockMovesBackwards()
    {
        var first = await Service().AddAsync("ABC123");
        var second = await Service().AddAsync("MOTOR1");
        await Repository().UpdatePlateAsync(first.Id, "ABC124", first.CreatedUtc.AddDays(-1));
        await Repository().MovePlateAsync(second.Id, -1, first.CreatedUtc.AddDays(-1));
        var plates = await Service().GetPlatesAsync();
        Assert.All(plates, p => Assert.Equal(DateTimeKind.Utc, p.UpdatedUtc.Kind));
        Assert.True(plates.Single(p => p.Id == first.Id).UpdatedUtc > first.UpdatedUtc);
        Assert.True(plates.Single(p => p.Id == second.Id).UpdatedUtc > second.UpdatedUtc);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
