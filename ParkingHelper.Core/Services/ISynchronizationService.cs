namespace ParkingHelper.Core.Services;

// Optional future adapter. Local reads and writes must never depend on this service.
// Authentication, transport, conflict resolution and checkpoints are intentionally deferred.
public interface ISynchronizationService
{
    Task SynchronizeAsync(CancellationToken cancellationToken = default);
}
