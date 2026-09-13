using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

public sealed class TicketService(IParkingRepository repository, TimeProvider? clock = null) : ITicketService
{
    public Task<IReadOnlyList<ParkingTicket>> GetActiveTicketsAsync() => repository.GetTicketsAsync(ParkingTicketState.Active);
    public Task<IReadOnlyList<ParkingTicket>> GetArchivedTicketsAsync() => repository.GetTicketsAsync(ParkingTicketState.Archived);
    public Task<ParkingTicket> ArchiveTicketAsync(Guid id) => ChangeStateAsync(id, ParkingTicketState.Archived);
    public Task<ParkingTicket> RestoreTicketAsync(Guid id) => ChangeStateAsync(id, ParkingTicketState.Active);
    public Task<ParkingTicket> DeleteTicketAsync(Guid id) => ChangeStateAsync(id, ParkingTicketState.Deleted);

    public Task<ParkingTicket> EditTicketAsync(Guid ticketId, Guid plateId, DateTime entryUtc)
    {
        if (ticketId == Guid.Empty) throw new TicketOperationException("The ticket identifier is invalid.");
        if (plateId == Guid.Empty) throw new TicketOperationException("Select a saved plate.");
        return repository.EditTicketAsync(ticketId, plateId, entryUtc, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
    }

    public Task<ParkingTicket> ChangeTicketPlateAsync(Guid ticketId, Guid plateId)
    {
        if (ticketId == Guid.Empty) throw new TicketOperationException("The ticket identifier is invalid.");
        if (plateId == Guid.Empty) throw new TicketOperationException("Select a saved plate.");
        return repository.ChangeTicketPlateAsync(ticketId, plateId, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime);
    }

    private async Task<ParkingTicket> ChangeStateAsync(Guid id, ParkingTicketState state)
    {
        if (id == Guid.Empty) throw new TicketOperationException("The ticket identifier is invalid.");
        try { return await repository.ChangeTicketStateAsync(id, state, (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime); }
        catch (KeyNotFoundException) { throw new TicketOperationException("This ticket could not be found."); }
        catch (InvalidOperationException) { throw new TicketOperationException("This ticket has been deleted."); }
    }

    public async Task<ParkingTicket?> GetTicketAsync(Guid id)
    {
        if (id == Guid.Empty) throw new TicketOperationException("The ticket identifier is invalid.");
        var ticket = await repository.GetTicketAsync(id);
        return ticket?.State == ParkingTicketState.Deleted ? null : ticket;
    }

    public Task<TicketCreationResult> CreateActiveTicketAsync(ScanResult scan, VehiclePlate selectedPlate)
    {
        if (scan == null || string.IsNullOrWhiteSpace(scan.Value) || string.IsNullOrWhiteSpace(scan.Format))
            throw new TicketOperationException("Couldn’t read a valid ticket. Please scan again.");
        if (selectedPlate == null || selectedPlate.Id == Guid.Empty)
            throw new TicketOperationException("Select a saved plate before scanning.");
        if (scan.DetectedUtc == default)
            throw new TicketOperationException("The scan has no capture time. Please scan again.");

        // ScanSession timestamps successful decoding, not preview frames or database completion.
        var capturedUtc = scan.DetectedUtc.UtcDateTime;
        var ticket = new ParkingTicket(Guid.NewGuid(), selectedPlate.Id, scan.Format, scan.Value,
            ParkingTicketState.Active, capturedUtc, capturedUtc, null, null)
        {
            PlateNumberSnapshot = selectedPlate.PlateNumber,
            RawBarcodeData = scan.RawBytes
        };
        // Existence validation, duplicate read and insert are one atomic repository operation.
        return repository.CreateActiveTicketAsync(ticket);
    }
}
