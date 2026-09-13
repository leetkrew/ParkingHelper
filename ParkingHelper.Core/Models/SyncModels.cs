using System.Text.Json;

namespace ParkingHelper.Core.Models;

public enum SyncRecordType
{
    VehiclePlate = 0,
    ParkingTicket = 1
}

public sealed record SyncRecord(
    int SchemaVersion,
    SyncRecordType RecordType,
    Guid Id,
    DateTime UpdatedUtc,
    bool IsDeleted,
    VehiclePlate? Plate,
    ParkingTicket? Ticket)
{
    public static SyncRecord ForPlate(VehiclePlate plate) =>
        new(SyncSchema.CurrentVersion, SyncRecordType.VehiclePlate, plate.Id, plate.UpdatedUtc, false, plate, null);

    public static SyncRecord ForPlateTombstone(Guid id, DateTime updatedUtc) =>
        new(SyncSchema.CurrentVersion, SyncRecordType.VehiclePlate, id, updatedUtc, true, null, null);

    public static SyncRecord ForTicket(ParkingTicket ticket) =>
        new(SyncSchema.CurrentVersion, SyncRecordType.ParkingTicket, ticket.Id, ticket.UpdatedUtc,
            ticket.State == ParkingTicketState.Deleted, null, ticket);
}

public sealed record SyncEnvelope(int SchemaVersion, IReadOnlyList<SyncRecord> Records);

public static class SyncSchema
{
    public const int CurrentVersion = 1;

    public static void Validate(SyncEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.SchemaVersion > CurrentVersion)
            throw new SyncSchemaException($"Sync schema version {envelope.SchemaVersion} is newer than this app supports.");
        foreach (var record in envelope.Records)
        {
            if (record.SchemaVersion > CurrentVersion)
                throw new SyncSchemaException($"Sync record schema version {record.SchemaVersion} is newer than this app supports.");
            if (!Enum.IsDefined(record.RecordType))
                throw new SyncSchemaException("A sync record has an unknown record type.");
            if (record.Id == Guid.Empty)
                throw new SyncSchemaException("A sync record has an empty identifier.");
            if (record.UpdatedUtc.Kind != DateTimeKind.Utc)
                throw new SyncSchemaException("Sync timestamps must be UTC.");
            if (record.RecordType == SyncRecordType.VehiclePlate && !record.IsDeleted &&
                (record.Plate is null || record.Plate.Id != record.Id))
                throw new SyncSchemaException("A live plate record has an invalid payload.");
            if (record.RecordType == SyncRecordType.ParkingTicket &&
                (record.Ticket is null || record.Ticket.Id != record.Id ||
                 (record.IsDeleted != (record.Ticket.State == ParkingTicketState.Deleted))))
                throw new SyncSchemaException("A ticket record has an invalid payload.");
        }
    }
}

public sealed class SyncSchemaException(string message) : InvalidOperationException(message);

public static class SyncMergeEngine
{
    public static IReadOnlyList<SyncRecord> Merge(IEnumerable<SyncRecord> local, IEnumerable<SyncRecord> remote)
    {
        var localRecords = local.ToArray();
        var remoteRecords = remote.ToArray();
        var envelope = new SyncEnvelope(SyncSchema.CurrentVersion, [.. localRecords, .. remoteRecords]);
        SyncSchema.Validate(envelope);
        return envelope.Records
            .GroupBy(record => (record.RecordType, record.Id))
            .Select(group => group.Aggregate(Choose))
            .OrderBy(record => record.RecordType)
            .ThenBy(record => record.Id)
            .ToArray();
    }

    public static SyncRecord Choose(SyncRecord first, SyncRecord second)
    {
        SyncSchema.Validate(new SyncEnvelope(SyncSchema.CurrentVersion, [first, second]));
        var comparison = first.UpdatedUtc.CompareTo(second.UpdatedUtc);
        if (comparison != 0) return comparison > 0 ? Preserve(first, second) : Preserve(second, first);
        if (first.IsDeleted != second.IsDeleted)
            return first.IsDeleted ? first : second;

        // A canonical JSON comparison makes equal-clock conflicts converge on every device.
        var firstJson = JsonSerializer.Serialize(first);
        var secondJson = JsonSerializer.Serialize(second);
        return string.CompareOrdinal(firstJson, secondJson) >= 0
            ? Preserve(first, second)
            : Preserve(second, first);
    }

    private static SyncRecord Preserve(SyncRecord winner, SyncRecord loser)
    {
        if (winner.RecordType != SyncRecordType.ParkingTicket || winner.Ticket is null || loser.Ticket is null)
            return winner;
        var ticket = winner.Ticket;
        var fallback = loser.Ticket;
        // These fields were added over time; do not erase a useful value when merging
        // a record produced by an older v1 client.
        if (ticket.EntryUtc == default && fallback.EntryUtc != default)
            ticket = ticket with { EntryUtc = fallback.EntryUtc };
        if (string.IsNullOrEmpty(ticket.BarcodeFormat) && !string.IsNullOrEmpty(fallback.BarcodeFormat))
            ticket = ticket with { BarcodeFormat = fallback.BarcodeFormat };
        if (string.IsNullOrEmpty(ticket.BarcodeValue) && !string.IsNullOrEmpty(fallback.BarcodeValue))
            ticket = ticket with { BarcodeValue = fallback.BarcodeValue };
        if (ticket.PlateNumberSnapshot is null && fallback.PlateNumberSnapshot is not null)
            ticket = ticket with { PlateNumberSnapshot = fallback.PlateNumberSnapshot };
        if (ticket.RawBarcodeData is null && fallback.RawBarcodeData is not null)
            ticket = ticket with { RawBarcodeData = fallback.RawBarcodeData };
        return winner with { Ticket = ticket };
    }
}
