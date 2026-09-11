using Microsoft.Data.Sqlite;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParkingHelper.Tests", Guid.NewGuid().ToString());
    private string DatabasePath => Path.Combine(directory, "parking.db3");
    private SqliteParkingRepository Repository() => new(DatabasePath);
    private static ParkingService Service(SqliteParkingRepository repository) => new(repository, TimeProvider.System);

    [Fact]
    public async Task DataSurvivesReopeningWithExactBarcodeAndUtcTimestamps()
    {
        var repository = Repository();
        var service = Service(repository);
        var plate = await service.AddPlateAsync(" abc-123 ");
        var ticket = await service.SaveTicketAsync(plate.Id, new("CODE_128", " 000123'\n "));
        var reopened = Repository();
        Assert.Equal(plate, Assert.Single(await reopened.GetPlatesAsync()));
        Assert.Equal("ABC-123", plate.PlateNumber);
        var saved = await reopened.GetTicketAsync(ticket.Id);
        Assert.Equal(ticket, saved);
        Assert.Equal(DateTimeKind.Utc, saved!.CreatedUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, saved.UpdatedUtc.Kind);
        Assert.Equal(ticket, Assert.Single(await reopened.GetTicketsAsync(ParkingTicketState.Active)));
    }

    [Fact]
    public async Task ArchiveRestoreAndDeletePreserveLifecycleAndTombstone()
    {
        var repository = Repository();
        var service = Service(repository);
        var plate = await service.AddPlateAsync("ABC-123");
        var ticket = await service.SaveTicketAsync(plate.Id, new("QR_CODE", "value"));
        var archived = await service.ArchiveTicketAsync(ticket.Id);
        Assert.Equal(archived.UpdatedUtc, archived.ArchivedUtc);
        Assert.Equal(ticket.CreatedUtc, archived.CreatedUtc);
        Assert.Empty(await repository.GetTicketsAsync(ParkingTicketState.Active));
        Assert.Single(await repository.GetTicketsAsync(ParkingTicketState.Archived));
        Assert.Equal(archived, await service.ArchiveTicketAsync(ticket.Id));
        var restored = await service.RestoreArchivedTicketAsync(ticket.Id);
        Assert.Null(restored.ArchivedUtc);
        Assert.Equal(ParkingTicketState.Active, restored.State);
        archived = await service.ArchiveTicketAsync(ticket.Id);
        var deleted = await service.DeleteTicketAsync(ticket.Id);
        Assert.Equal(deleted.UpdatedUtc, deleted.DeletedUtc);
        Assert.Equal(archived.ArchivedUtc, deleted.ArchivedUtc);
        Assert.Empty(await repository.GetTicketsAsync(ParkingTicketState.Archived));
        Assert.Equal(deleted, Assert.Single(await Repository().GetTicketsAsync(ParkingTicketState.Deleted)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreArchivedTicketAsync(ticket.Id));
        Assert.Equal(deleted, await repository.GetTicketAsync(ticket.Id));
    }

    [Fact]
    public async Task ForeignKeyAndDuplicatePlateConstraintsAreEnforced()
    {
        var repository = Repository();
        var service = Service(repository);
        await service.AddPlateAsync("ABC-123");
        await Assert.ThrowsAsync<PlateOperationException>(() => service.AddPlateAsync(" abc-123 "));
        var orphan = await Assert.ThrowsAsync<SqliteException>(() =>
            service.SaveTicketAsync(Guid.NewGuid(), new("CODE_128", "123")));
        Assert.Equal(19, orphan.SqliteErrorCode);
        Assert.Empty(await repository.GetTicketsAsync(ParkingTicketState.Active));
    }

    [Fact]
    public async Task InitializationAndConcurrentWritesAreSafe()
    {
        var repository = Repository();
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => repository.InitializeAsync()));
        var service = Service(repository);
        var plate = await service.AddPlateAsync("ABC-123");
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            service.SaveTicketAsync(plate.Id, new("CODE_128", i.ToString()))));
        Assert.Equal(20, (await Repository().GetTicketsAsync(ParkingTicketState.Active)).Count);
    }

    [Fact]
    public async Task InvalidStateAndNonUtcDataAreRejected()
    {
        var repository = Repository();
        var service = Service(repository);
        var plate = await service.AddPlateAsync("ABC-123");
        var ticket = await service.SaveTicketAsync(plate.Id, new("QR_CODE", "123"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.ChangeTicketStateAsync(ticket.Id, (ParkingTicketState)99, DateTime.UtcNow));
        await Assert.ThrowsAsync<ArgumentException>(() => repository.AddTicketAsync(ticket with
        {
            Id = Guid.NewGuid(), CreatedUtc = DateTime.SpecifyKind(ticket.CreatedUtc, DateTimeKind.Unspecified)
        }));
        await Assert.ThrowsAsync<SqliteException>(() => repository.AddTicketAsync(ticket with
        {
            Id = Guid.NewGuid(), State = ParkingTicketState.Archived
        }));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteTicketAsync(Guid.NewGuid()));
        Assert.Equal(ticket, await repository.GetTicketAsync(ticket.Id));
    }

    [Fact]
    public async Task BackwardsClockDoesNotRegressUpdatedTimestamp()
    {
        var repository = Repository();
        var service = Service(repository);
        var plate = await service.AddPlateAsync("ABC-123");
        var ticket = await service.SaveTicketAsync(plate.Id, new("QR_CODE", "123"));
        var archived = await repository.ChangeTicketStateAsync(ticket.Id, ParkingTicketState.Archived,
            ticket.CreatedUtc.AddDays(-1));
        Assert.True(archived.UpdatedUtc > ticket.UpdatedUtc);
    }

    [Fact]
    public async Task NewerSchemaIsRejectedWithoutDeletingData()
    {
        var repository = Repository();
        var plate = await Service(repository).AddPlateAsync("ABC-123");
        using (var connection = new SqliteConnection($"Data Source={DatabasePath};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99;";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => Repository().InitializeAsync());
        using var check = new SqliteConnection($"Data Source={DatabasePath};Pooling=False");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT Id FROM VehiclePlates;";
        Assert.Equal(plate.Id.ToString(), query.ExecuteScalar());
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
