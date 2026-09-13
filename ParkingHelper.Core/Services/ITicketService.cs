using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

public interface ITicketService
{
    Task<IReadOnlyList<ParkingTicket>> GetActiveTicketsAsync();
    Task<IReadOnlyList<ParkingTicket>> GetArchivedTicketsAsync();
    Task<ParkingTicket> ArchiveTicketAsync(Guid id);
    Task<ParkingTicket> RestoreTicketAsync(Guid id);
    Task<ParkingTicket> EditTicketAsync(Guid ticketId, Guid plateId, DateTime entryUtc);
    Task<ParkingTicket> ChangeTicketPlateAsync(Guid ticketId, Guid plateId);
    Task<ParkingTicket> DeleteTicketAsync(Guid id);
    Task<ParkingTicket?> GetTicketAsync(Guid id);
    Task<TicketCreationResult> CreateActiveTicketAsync(ScanResult scan, VehiclePlate selectedPlate);
}
