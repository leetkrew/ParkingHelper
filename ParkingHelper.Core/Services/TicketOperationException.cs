namespace ParkingHelper.Core.Services;

public sealed class TicketOperationException(string message) : Exception(message);
