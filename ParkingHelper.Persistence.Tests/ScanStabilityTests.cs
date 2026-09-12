using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class ScanStabilityTests
{
    [Theory]
    [InlineData("QrCode")]
    [InlineData("Pdf417")]
    [InlineData("Aztec")]
    [InlineData("DataMatrix")]
    [InlineData("Code128")]
    [InlineData("Code39")]
    [InlineData("Ean13")]
    [InlineData("Ean8")]
    [InlineData("UpcA")]
    [InlineData("UpcE")]
    [InlineData("Itf")]
    [InlineData("PharmaCode")]
    public void TwoAdjacentReadsConfirmWithoutAnExtraDelay(string format)
    {
        var gate = new ScanStabilityGate();
        Assert.False(gate.Observe(1, 1, 100, "ticket", format));
        Assert.True(gate.Observe(1, 2, 140, "ticket", format));
    }

    [Theory]
    [InlineData(1, 2, 140, "other", "QrCode")]
    [InlineData(1, 2, 140, "ticket", "Pdf417")]
    [InlineData(1, 3, 140, "ticket", "QrCode")]
    [InlineData(1, 2, 601, "ticket", "QrCode")]
    [InlineData(2, 2, 140, "ticket", "QrCode")]
    [InlineData(1, 1, 140, "ticket", "QrCode")]
    [InlineData(1, 2, 99, "ticket", "QrCode")]
    public void ChangedOrMissingOrStaleFramesStartANewCandidate(
        long generation, long frame, long timestamp, string value, string format)
    {
        var gate = new ScanStabilityGate();
        Assert.False(gate.Observe(1, 1, 100, "ticket", "QrCode"));
        Assert.False(gate.Observe(generation, frame, timestamp, value, format));
        Assert.True(gate.Observe(generation, frame + 1, timestamp + 40, value, format));
    }

    [Fact]
    public void EmptyDetectionAndResetDiscardCandidate()
    {
        var gate = new ScanStabilityGate();
        Assert.False(gate.Observe(1, 1, 100, "ticket", "QrCode"));
        Assert.False(gate.Observe(1, 2, 140, null, null));
        Assert.False(gate.Observe(1, 3, 180, "ticket", "QrCode"));
        gate.Reset();
        Assert.False(gate.Observe(1, 4, 220, "ticket", "QrCode"));
        Assert.True(gate.Observe(1, 5, 260, "ticket", "QrCode"));
    }
}
