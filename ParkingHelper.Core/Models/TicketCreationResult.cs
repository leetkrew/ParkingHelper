namespace ParkingHelper.Core.Models;

/// <summary>Duplicates return the original, unchanged ticket.</summary>
public sealed record TicketCreationResult(ParkingTicket Ticket, bool Created);
