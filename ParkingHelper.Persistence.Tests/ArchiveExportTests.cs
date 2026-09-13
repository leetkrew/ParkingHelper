using System.Text.Json;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class ArchiveExportTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParkingHelper.Export", Guid.NewGuid().ToString());
    private string DatabasePath => Path.Combine(directory, "parking.db3");
    private SqliteParkingRepository Repository() => new(DatabasePath);

    [Fact]
    public async Task CsvExportsOnlyArchivedSnapshotWithEscapingAndFinalDuration()
    {
        var repository = Repository();
        var plate = await new PlateService(repository, TimeProvider.System).AddAsync("ABC123");
        var service = new TicketService(repository);
        var ticket = (await service.CreateActiveTicketAsync(
            new("value,\"with\nline", "QrCode", null, new DateTimeOffset(2026, 9, 12, 3, 0, 0, TimeSpan.Zero)), plate)).Ticket;
        var archived = await repository.ChangeTicketStateAsync(ticket.Id, ParkingTicketState.Archived,
            new DateTime(2026, 9, 12, 5, 23, 6, DateTimeKind.Utc));
        var export = await new ArchiveExportService(service).ExportAsync(
            new(ArchiveExportScope.All, ArchiveExportFormat.Csv));
        var csv = System.Text.Encoding.UTF8.GetString(export.Content);
        Assert.Contains("\"value,\"\"with\nline\"", csv);
        Assert.Contains("02:23:06", csv);
        Assert.DoesNotContain("Active", csv);
        Assert.Equal(ticket.CreatedUtc, archived.CreatedUtc);
        Assert.Equal(archived.UpdatedUtc, (await repository.GetTicketAsync(ticket.Id))!.UpdatedUtc);
    }

    [Fact]
    public async Task DateRangeAndSelectedScopeExcludeActiveAndDeletedTickets()
    {
        var repository = Repository();
        var plate = await new PlateService(repository, TimeProvider.System).AddAsync("RANGE");
        var service = new TicketService(repository);
        var first = (await service.CreateActiveTicketAsync(new("first", "QrCode", null,
            new DateTimeOffset(2026, 9, 1, 1, 0, 0, TimeSpan.Zero)), plate)).Ticket;
        var second = (await service.CreateActiveTicketAsync(new("second", "Pdf417", null,
            new DateTimeOffset(2026, 9, 20, 1, 0, 0, TimeSpan.Zero)), plate)).Ticket;
        await repository.ChangeTicketStateAsync(first.Id, ParkingTicketState.Archived,
            new DateTime(2026, 9, 10, 12, 0, 0, DateTimeKind.Utc));
        await repository.ChangeTicketStateAsync(second.Id, ParkingTicketState.Archived,
            new DateTime(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc));
        var range = await new ArchiveExportService(service).ExportAsync(new(
            ArchiveExportScope.DateRange, ArchiveExportFormat.Json, FromDate: new(2026, 9, 1), ToDate: new(2026, 9, 12)));
        using var json = JsonDocument.Parse(range.Content);
        Assert.Equal(1, json.RootElement.GetProperty("ticketCount").GetInt32());
        Assert.Equal("first", json.RootElement.GetProperty("tickets")[0].GetProperty("barcodeValue").GetString());
        var selected = await new ArchiveExportService(service).ExportAsync(new(
            ArchiveExportScope.Selected, ArchiveExportFormat.Json, [second.Id]));
        Assert.Contains("second", System.Text.Encoding.UTF8.GetString(selected.Content));
        Assert.DoesNotContain("first", System.Text.Encoding.UTF8.GetString(selected.Content));
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
