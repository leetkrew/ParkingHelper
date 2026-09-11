using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

public sealed class PlateService(IParkingRepository repository, TimeProvider clock) : IPlateService
{
    public Task<IReadOnlyList<VehiclePlate>> GetPlatesAsync() => repository.GetPlatesAsync();

    public async Task<bool> NeedsSetupAsync() => (await GetPlatesAsync()).Count == 0;

    public async Task<VehiclePlate> AddAsync(string plateNumber)
    {
        var normalized = PlateNumberRules.Normalize(plateNumber);
        var now = clock.GetUtcNow().UtcDateTime;
        var plate = new VehiclePlate(Guid.NewGuid(), normalized, now, now);
        await repository.AddPlateAsync(plate);
        return (await repository.GetPlatesAsync()).First(p => p.Id == plate.Id);
    }

    public Task UpdateAsync(Guid id, string plateNumber) =>
        repository.UpdatePlateAsync(id, PlateNumberRules.Normalize(plateNumber), clock.GetUtcNow().UtcDateTime);

    public Task DeleteAsync(Guid id) => repository.DeletePlateAsync(id, clock.GetUtcNow().UtcDateTime);

    public Task MoveAsync(Guid id, int direction)
    {
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction), "Use -1 to move up or 1 to move down.");
        return repository.MovePlateAsync(id, direction, clock.GetUtcNow().UtcDateTime);
    }
}
