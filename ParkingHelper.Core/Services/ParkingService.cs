using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

public sealed class ParkingService(IParkingRepository repository, TimeProvider clock)
{
    public Task<VehiclePlate> AddPlateAsync(string plateNumber) =>
        new PlateService(repository, clock).AddAsync(plateNumber);

    public async Task<ParkingTicket> SaveTicketAsync(Guid vehiclePlateId, BarcodeScanResult barcode)
    {
        ArgumentNullException.ThrowIfNull(barcode);
        if (vehiclePlateId == Guid.Empty)
            throw new ArgumentException("A vehicle plate is required.", nameof(vehiclePlateId));
        ArgumentException.ThrowIfNullOrWhiteSpace(barcode.Format);
        ArgumentException.ThrowIfNullOrWhiteSpace(barcode.Value);
        var now = clock.GetUtcNow().UtcDateTime;
        var ticket = new ParkingTicket(Guid.NewGuid(), vehiclePlateId, barcode.Format,
            barcode.Value, ParkingTicketState.Active, now, now, null, null);
        await repository.AddTicketAsync(ticket);
        return ticket;
    }

    public Task<ParkingTicket> ArchiveTicketAsync(Guid id) =>
        repository.ChangeTicketStateAsync(id, ParkingTicketState.Archived, clock.GetUtcNow().UtcDateTime);

    public Task<ParkingTicket> RestoreArchivedTicketAsync(Guid id) =>
        repository.ChangeTicketStateAsync(id, ParkingTicketState.Active, clock.GetUtcNow().UtcDateTime);

    public Task<ParkingTicket> DeleteTicketAsync(Guid id) =>
        repository.ChangeTicketStateAsync(id, ParkingTicketState.Deleted, clock.GetUtcNow().UtcDateTime);
}
