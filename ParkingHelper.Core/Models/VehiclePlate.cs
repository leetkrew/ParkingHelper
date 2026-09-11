namespace ParkingHelper.Core.Models;

public sealed record VehiclePlate(Guid Id, string PlateNumber, DateTime CreatedUtc, DateTime UpdatedUtc)
{
    public int SortOrder { get; init; }
}
