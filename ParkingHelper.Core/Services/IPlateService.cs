using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

public interface IPlateService
{
    Task<IReadOnlyList<VehiclePlate>> GetPlatesAsync();
    Task<bool> NeedsSetupAsync();
    Task<VehiclePlate> AddAsync(string plateNumber);
    Task UpdateAsync(Guid id, string plateNumber);
    // The UI requests deletion by stable ID; hard/soft deletion is a storage concern.
    Task DeleteAsync(Guid id);
    Task MoveAsync(Guid id, int direction);
}
