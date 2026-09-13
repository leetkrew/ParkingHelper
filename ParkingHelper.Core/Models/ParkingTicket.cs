namespace ParkingHelper.Core.Models;

public sealed record ParkingTicket(
    Guid Id,
    Guid VehiclePlateId,
    string BarcodeFormat,
    string BarcodeValue,
    ParkingTicketState State,
    DateTime CreatedUtc,
    DateTime UpdatedUtc,
    DateTime? ArchivedUtc,
    DateTime? DeletedUtc)
{
    // CreatedUtc remains the persisted, immutable scan timestamp for compatibility.
    public DateTime ScannedUtc => CreatedUtc;
    public DateTime EntryUtc { get; init; } = CreatedUtc;
    public string? PlateNumberSnapshot { get; init; }
    public byte[]? RawBarcodeData { get; init; }

    public ParkingTicket ChangeState(ParkingTicketState state, DateTime utcNow)
    {
        if (!Enum.IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state));
        if (utcNow.Kind != DateTimeKind.Utc)
            throw new ArgumentException("A UTC timestamp is required.", nameof(utcNow));
        if (State == state)
            return this;
        if (State == ParkingTicketState.Deleted)
            throw new InvalidOperationException("Deleted tickets cannot be restored in this milestone.");

        // Keep timestamps monotonic even if the device clock moves backwards.
        var updated = utcNow > UpdatedUtc ? utcNow : UpdatedUtc.AddTicks(1);
        return this with
        {
            State = state,
            UpdatedUtc = updated,
            ArchivedUtc = state == ParkingTicketState.Archived ? updated
                : state == ParkingTicketState.Active ? null : ArchivedUtc,
            DeletedUtc = state == ParkingTicketState.Deleted ? updated : null
        };
    }
}
