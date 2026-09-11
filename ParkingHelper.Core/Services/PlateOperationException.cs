namespace ParkingHelper.Core.Services;

/// <summary>A plate validation or conflict message that is safe to show to the user.</summary>
public sealed class PlateOperationException(string message) : Exception(message);
