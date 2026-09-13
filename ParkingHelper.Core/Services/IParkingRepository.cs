using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

public interface IParkingRepository
{
    Task InitializeAsync();
    Task AddPlateAsync(VehiclePlate plate);
    Task<IReadOnlyList<VehiclePlate>> GetPlatesAsync();
    Task UpdatePlateAsync(Guid id, string plateNumber, DateTime utcNow);
    Task DeletePlateAsync(Guid id, DateTime utcNow);
    Task MovePlateAsync(Guid id, int direction, DateTime utcNow);
    Task<TicketCreationResult> CreateActiveTicketAsync(ParkingTicket ticket);
    Task AddTicketAsync(ParkingTicket ticket);
    Task<ParkingTicket?> GetTicketAsync(Guid id);
    Task<IReadOnlyList<ParkingTicket>> GetTicketsAsync(ParkingTicketState state);
    Task<ParkingTicket> EditTicketAsync(Guid ticketId, Guid plateId, DateTime entryUtc, DateTime utcNow);
    Task<ParkingTicket> ChangeTicketPlateAsync(Guid ticketId, Guid plateId, DateTime utcNow);
    Task<ParkingTicket> ChangeTicketStateAsync(Guid id, ParkingTicketState state, DateTime utcNow);
}
