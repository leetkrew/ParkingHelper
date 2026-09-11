using Microsoft.Extensions.Logging;
using ParkingHelper.Core.Models;

namespace ParkingHelper.App.Services;

public interface IScanFeedbackService
{
    Task PrepareAsync();
    Task NotifySavedAsync(TicketCreationResult? outcome, CancellationToken cancellationToken = default);
}

public interface IScanFeedbackPlayer
{
    Task PrepareAsync();
    // Completes when playback starts, not when the sound finishes.
    Task PlaySuccessSoundAsync();
    Task PerformStrongHapticAsync();
}

public sealed class ScanFeedbackService(IScanFeedbackSettings settings, IScanFeedbackPlayer player,
    ILogger<ScanFeedbackService> logger) : IScanFeedbackService
{
    private readonly object sync = new();
    private Guid? lastAnnouncedTicket;

    // Loading the bundled asset never emits a sound or haptic.
    public Task PrepareAsync() => TryAsync(player.PrepareAsync, "prepare scan sound");

    public async Task NotifySavedAsync(TicketCreationResult? outcome, CancellationToken cancellationToken = default)
    {
        if (outcome is not { Created: true, Ticket: { State: ParkingTicketState.Active } }
            || outcome.Ticket.Id == Guid.Empty || cancellationToken.IsCancellationRequested) return;
        lock (sync)
        {
            if (lastAnnouncedTicket == outcome.Ticket.Id) return;
            lastAnnouncedTicket = outcome.Ticket.Id;
        }

        // Failure of either channel must not suppress the other or fail a committed save.
        await Task.WhenAll(PlaySoundAsync(cancellationToken),
            TryAsync(player.PerformStrongHapticAsync, "perform scan haptic"));
    }

    private async Task PlaySoundAsync(CancellationToken token)
    {
        try
        {
            if (!settings.ScanSoundEnabled) return;
            await player.PrepareAsync();
            if (!token.IsCancellationRequested) await player.PlaySuccessSoundAsync();
        }
        catch (Exception exception) { logger.LogWarning(exception, "Could not play scan success sound"); }
    }

    private async Task TryAsync(Func<Task> action, string operation)
    {
        try { await action(); }
        catch (Exception exception) { logger.LogWarning(exception, "Could not {Operation}", operation); }
    }
}
