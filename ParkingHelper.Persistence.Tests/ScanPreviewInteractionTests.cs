using ParkingHelper.Core.Services;
using ParkingHelper.App.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class ScanPreviewInteractionTests
{
    private sealed class Clock : TimeProvider
    {
        public long Ticks { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Ticks;
        public void Cooldown() => Ticks += 1000;
    }

    [Fact]
    public void PreferredSourceSurvivesRetryAndUnavailableSourceFallsBackToAutomatic()
    {
        const string preferred = "external-usb";
        Assert.Equal(preferred, ScannerCameraSelection.Resolve(preferred, ["builtin", preferred]));
        Assert.Null(ScannerCameraSelection.Resolve(preferred, ["builtin"]));
        Assert.Null(ScannerCameraSelection.Resolve(preferred, []));
        Assert.Null(ScannerCameraSelection.Resolve(null, ["builtin", preferred]));
        Assert.Equal(preferred, ScannerCameraSelection.Resolve(preferred, [preferred, "builtin"]));
    }

    [Fact]
    public async Task FirstTapRescansSecondAfterCooldownRestartsThenStartsFreshCycle()
    {
        var clock = new Clock();
        var interaction = new ScanPreviewInteraction(clock);
        var rescans = 0;
        var restarts = 0;
        Task<PreviewTapAction> Tap() => interaction.TapAsync(() => true, () => true,
            () => rescans++, () => { restarts++; return Task.CompletedTask; });
        Assert.Equal(PreviewTapAction.Rescan, await Tap());
        for (var i = 0; i < 100; i++) Assert.Equal(PreviewTapAction.Ignored, await Tap());
        clock.Cooldown();
        Assert.Equal(PreviewTapAction.RetryCamera, await Tap());
        clock.Cooldown();
        Assert.Equal(PreviewTapAction.Rescan, await Tap());
        Assert.Equal(2, rescans);
        Assert.Equal(1, restarts);
    }

    [Fact]
    public async Task UnhealthyCameraRestartsDirectlyAndHammeringNeverQueues()
    {
        var clock = new Clock();
        var interaction = new ScanPreviewInteraction(clock);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        Task<PreviewTapAction> Tap() => interaction.TapAsync(() => true, () => false,
            () => throw new Exception("Unhealthy camera must not rescan"), () => { starts++; return completion.Task; });
        var first = Tap();
        Assert.True(interaction.IsBusy);
        clock.Cooldown();
        var hammer = await Task.WhenAll(Enumerable.Range(0, 100).Select(_ => Tap()));
        Assert.All(hammer, action => Assert.Equal(PreviewTapAction.Ignored, action));
        Assert.Equal(1, starts);
        completion.SetResult();
        Assert.Equal(PreviewTapAction.RetryCamera, await first);
        Assert.False(interaction.IsBusy);
        Assert.Equal(PreviewTapAction.Ignored, await Tap());
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task SavingSavedNavigatingAndUnavailablePlateStatesDoNotConsumeFirstTap()
    {
        var interaction = new ScanPreviewInteraction(new Clock());
        Assert.Equal(PreviewTapAction.Ignored, await interaction.TapAsync(() => false, () => true,
            () => throw new Exception("Must not reset accepted save"), () => throw new Exception("Must not restart")));
        Assert.Equal(PreviewTapAction.Rescan, await interaction.TapAsync(() => true, () => true,
            () => { }, () => throw new Exception("First valid tap must rescan")));
    }

    [Fact]
    public async Task LifecycleInitializationAndPreviewOperationsAreMutuallyExclusive()
    {
        var interaction = new ScanPreviewInteraction(new Clock());
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialization = interaction.InitializeAsync(() => completion.Task, CancellationToken.None);
        Assert.Equal(PreviewTapAction.Ignored, await interaction.TapAsync(() => true, () => false,
            () => throw new Exception(), () => throw new Exception()));
        using var cancelled = new CancellationTokenSource();
        var next = interaction.InitializeAsync(() => throw new Exception("Cancelled lifecycle must not start"), cancelled.Token);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => next);
        completion.SetResult();
        await initialization;
        Assert.False(interaction.IsBusy);
    }

    [Fact]
    public async Task FailureAndCancellationReleaseTheGateAndRetainCooldown()
    {
        var clock = new Clock();
        var interaction = new ScanPreviewInteraction(clock);
        await Assert.ThrowsAsync<IOException>(() => interaction.TapAsync(() => true, () => false,
            () => { }, () => throw new IOException("device disappeared")));
        Assert.False(interaction.IsBusy);
        Assert.Equal(PreviewTapAction.Ignored, await interaction.TapAsync(() => true, () => false,
            () => { }, () => Task.CompletedTask));
        clock.Cooldown();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => interaction.TapAsync(() => true, () => false,
            () => { }, () => throw new OperationCanceledException()));
        Assert.False(interaction.IsBusy);
        clock.Cooldown();
        Assert.Equal(PreviewTapAction.Rescan, await interaction.TapAsync(() => true, () => true,
            () => { }, () => Task.CompletedTask));
    }

    [Fact]
    public async Task RescanInvalidatesOldResultsAndConsecutiveCandidatesWithoutRestart()
    {
        var session = new ScanSession(TimeProvider.System);
        var stability = new ScanStabilityGate();
        var oldGeneration = session.Generation;
        Assert.False(stability.Observe(oldGeneration, 1, 0, "ticket", "Pdf417"));
        Assert.True(session.TryCapture(oldGeneration, "unsaved", "Pdf417", null));
        await new ScanPreviewInteraction(new Clock()).TapAsync(() => true, () => true,
            () => { session.Clear(); stability.Reset(); }, () => throw new Exception("No initialization needed"));
        Assert.Null(session.Result);
        Assert.False(session.TryCapture(oldGeneration, "stale", "Pdf417", null));
        Assert.False(stability.Observe(session.Generation, 2, 20, "ticket", "Pdf417"));
        Assert.True(stability.Observe(session.Generation, 3, 40, "ticket", "Pdf417"));
    }
}
