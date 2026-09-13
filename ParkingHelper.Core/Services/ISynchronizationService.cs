namespace ParkingHelper.Core.Services;

public interface ISynchronizationService
{
    Task SynchronizeAsync(CancellationToken cancellationToken = default);
}
