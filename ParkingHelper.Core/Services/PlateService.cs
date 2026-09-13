using ParkingHelper.Core.Models;

namespace ParkingHelper.Core.Services;

public sealed class PlateService(IParkingRepository repository, TimeProvider clock, ISynchronizationTrigger? syncTrigger = null) : IPlateService
{
    public Task<IReadOnlyList<VehiclePlate>> GetPlatesAsync() => repository.GetPlatesAsync();

    public async Task<bool> NeedsSetupAsync() => (await GetPlatesAsync()).Count == 0;

    public async Task<VehiclePlate> AddAsync(string plateNumber)
    {
        var normalized = PlateNumberRules.Normalize(plateNumber);
        var now = clock.GetUtcNow().UtcDateTime;
        var plate = new VehiclePlate(Guid.NewGuid(), normalized, now, now);
        await repository.AddPlateAsync(plate);
        syncTrigger?.RequestSync();
        return (await repository.GetPlatesAsync()).First(p => p.Id == plate.Id);
    }

    public Task UpdateAsync(Guid id, string plateNumber) =>
        UpdateAndTriggerAsync(id, plateNumber);

    private async Task UpdateAndTriggerAsync(Guid id, string plateNumber)
    {
        await repository.UpdatePlateAsync(id, PlateNumberRules.Normalize(plateNumber), clock.GetUtcNow().UtcDateTime);
        syncTrigger?.RequestSync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await repository.DeletePlateAsync(id, clock.GetUtcNow().UtcDateTime);
        syncTrigger?.RequestSync();
    }

    public Task MoveAsync(Guid id, int direction)
    {
        if (direction is not (-1 or 1))
            throw new ArgumentOutOfRangeException(nameof(direction), "Use -1 to move up or 1 to move down.");
        return MoveAndTriggerAsync(id, direction);
    }

    private async Task MoveAndTriggerAsync(Guid id, int direction)
    {
        await repository.MovePlateAsync(id, direction, clock.GetUtcNow().UtcDateTime);
        syncTrigger?.RequestSync();
    }
}
