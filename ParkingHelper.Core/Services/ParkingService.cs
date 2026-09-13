using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

public sealed class ParkingService(IParkingRepository repository, TimeProvider clock, ISynchronizationTrigger? syncTrigger = null)
{
    public Task<VehiclePlate> AddPlateAsync(string plateNumber) =>
        new PlateService(repository, clock, syncTrigger).AddAsync(plateNumber);

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
        syncTrigger?.RequestSync();
        return ticket;
    }

    public Task<ParkingTicket> ArchiveTicketAsync(Guid id) =>
        ChangeStateAsync(id, ParkingTicketState.Archived);

    public Task<ParkingTicket> RestoreArchivedTicketAsync(Guid id) =>
        ChangeStateAsync(id, ParkingTicketState.Active);

    public Task<ParkingTicket> DeleteTicketAsync(Guid id) =>
        ChangeStateAsync(id, ParkingTicketState.Deleted);

    private async Task<ParkingTicket> ChangeStateAsync(Guid id, ParkingTicketState state)
    {
        var result = await repository.ChangeTicketStateAsync(id, state, clock.GetUtcNow().UtcDateTime);
        syncTrigger?.RequestSync();
        return result;
    }
}
