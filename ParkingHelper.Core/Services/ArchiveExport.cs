using System.Globalization;
using System.Text;
using System.Text.Json;
using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

public enum ArchiveExportScope
{
    Selected,
    DateRange,
    All
}

public enum ArchiveExportFormat
{
    Csv,
    Json
}

public sealed record ArchiveExportRequest(
    ArchiveExportScope Scope,
    ArchiveExportFormat Format,
    IReadOnlyCollection<Guid>? SelectedTicketIds = null,
    DateOnly? FromDate = null,
    DateOnly? ToDate = null);

public sealed record ArchiveExportResult(string FileName, string ContentType, byte[] Content, int TicketCount);

public interface IArchiveExportService
{
    Task<ArchiveExportResult> ExportAsync(ArchiveExportRequest request);
}

public sealed class ArchiveExportService(ITicketService tickets, TimeProvider? clock = null) : IArchiveExportService
{
    public async Task<ArchiveExportResult> ExportAsync(ArchiveExportRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var archived = await tickets.GetArchivedTicketsAsync().ConfigureAwait(false);
        var selected = Select(archived, request);
        if (selected.Count == 0)
            throw new InvalidOperationException(request.Scope == ArchiveExportScope.Selected
                ? "Select at least one archived ticket."
                : "No archived tickets match the selected scope.");

        var extension = request.Format == ArchiveExportFormat.Csv ? "csv" : "json";
        var contentType = request.Format == ArchiveExportFormat.Csv ? "text/csv" : "application/json";
        var datePart = request.Scope == ArchiveExportScope.DateRange && request.FromDate.HasValue && request.ToDate.HasValue
            ? $"{request.FromDate.Value:yyyy-MM-dd}-to-{request.ToDate.Value:yyyy-MM-dd}"
            : (clock ?? TimeProvider.System).GetUtcNow().ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var fileName = $"ParkingHelper-Archives-{datePart}.{extension}";
        var content = request.Format == ArchiveExportFormat.Csv ? Csv(selected) : Json(selected);
        return new ArchiveExportResult(fileName, contentType, content, selected.Count);
    }

    private static List<ParkingTicket> Select(IReadOnlyList<ParkingTicket> archived, ArchiveExportRequest request)
    {
        if (request.Scope == ArchiveExportScope.Selected)
        {
            var ids = request.SelectedTicketIds ?? [];
            return archived.Where(ticket => ids.Contains(ticket.Id)).ToList();
        }

        if (request.Scope == ArchiveExportScope.DateRange)
        {
            if (!request.FromDate.HasValue || !request.ToDate.HasValue)
                throw new InvalidOperationException("Choose both a start and end date.");
            if (request.FromDate > request.ToDate)
                throw new InvalidOperationException("The start date must be on or before the end date.");
            var fromUtc = LocalDateStartUtc(request.FromDate.Value);
            var toUtcExclusive = LocalDateStartUtc(request.ToDate.Value.AddDays(1));
            return archived.Where(ticket => ticket.ArchivedUtc >= fromUtc && ticket.ArchivedUtc < toUtcExclusive).ToList();
        }

        return archived.ToList();
    }

    private static DateTime LocalDateStartUtc(DateOnly date)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(local, TimeZoneInfo.Local);
    }

    private static byte[] Csv(IReadOnlyList<ParkingTicket> tickets)
    {
        var builder = new StringBuilder();
        builder.AppendLine("TicketId,PlateNumber,BarcodeFormat,BarcodeValue,CreatedUtc,ArchivedUtc,Duration");
        foreach (var ticket in tickets)
        {
            var archived = ticket.ArchivedUtc!.Value;
            AppendRow(builder, ticket.Id.ToString(), ticket.PlateNumberSnapshot ?? "", ticket.BarcodeFormat,
                ticket.BarcodeValue, FormatUtc(ticket.CreatedUtc), FormatUtc(archived),
                TicketDuration.Format(ticket with { State = ParkingTicketState.Archived, ArchivedUtc = archived }, archived));
        }
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void AppendRow(StringBuilder builder, params string[] values)
    {
        builder.AppendJoin(',', values.Select(Escape)).AppendLine();
    }

    private static string Escape(string value) =>
        value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    private static byte[] Json(IReadOnlyList<ParkingTicket> tickets)
    {
        var payload = new
        {
            exportedUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ticketCount = tickets.Count,
            tickets = tickets.Select(ticket => new
            {
                ticketId = ticket.Id,
                plateNumber = ticket.PlateNumberSnapshot,
                barcodeFormat = ticket.BarcodeFormat,
                barcodeValue = ticket.BarcodeValue,
                createdUtc = FormatUtc(ticket.CreatedUtc),
                archivedUtc = FormatUtc(ticket.ArchivedUtc!.Value),
                duration = TicketDuration.Format(ticket, ticket.ArchivedUtc.Value)
            })
        };
        return JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string FormatUtc(DateTime value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
