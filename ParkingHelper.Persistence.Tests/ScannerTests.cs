using Microsoft.Maui.Storage;
using ParkingHelper.App.Services;
using ParkingHelper.Core.Services;
using ZXing.Net.Maui;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class ScannerTests
{
    [Fact]
    public void AutoUsesCommonFormatsWhileEveryFormatRemainsSelectable()
    {
        var settings = new ScannerSettingsService(new MemoryPreferences());
        Assert.Null(settings.Format);
        Assert.Equal(Enum.GetValues<BarcodeFormat>().Distinct().Count() + 1, settings.Formats.Count);
        foreach (var format in Enum.GetValues<BarcodeFormat>())
        {
            settings.Format = format.ToString();
            Assert.Equal(format.ToString(), settings.Format);
            Assert.Equal(format, ScannerFormatCatalog.Resolve(format.ToString()));
        }
        var common = new[] { BarcodeFormat.QrCode, BarcodeFormat.Pdf417, BarcodeFormat.Aztec,
            BarcodeFormat.DataMatrix, BarcodeFormat.Code128, BarcodeFormat.Code39, BarcodeFormat.Ean13,
            BarcodeFormat.Ean8, BarcodeFormat.UpcA, BarcodeFormat.UpcE, BarcodeFormat.Itf };
        foreach (var format in Enum.GetValues<BarcodeFormat>())
            Assert.Equal(common.Contains(format), ScannerFormatCatalog.Resolve(null).HasFlag(format));
        Assert.Equal(ScannerFormatCatalog.Auto, ScannerFormatCatalog.Resolve("UnknownFutureFormat"));
        Assert.Equal(BarcodeFormat.PharmaCode, ScannerFormatCatalog.Resolve("PharmaCode"));
    }

    [Fact]
    public void PreferencesSurviveNewServiceAndInvalidFormatFallsBackToAuto()
    {
        var preferences = new MemoryPreferences();
        var settings = new ScannerSettingsService(preferences) { Format = "Pdf417", PreferredCameraId = "opaque-device-id" };
        var reopened = new ScannerSettingsService(preferences);
        Assert.Equal("Pdf417", reopened.Format);
        Assert.Equal("opaque-device-id", reopened.PreferredCameraId);
        preferences.Set("scanner.format", "UnknownFutureFormat");
        Assert.Null(reopened.Format);
        reopened.PreferredCameraId = null;
        Assert.Null(new ScannerSettingsService(preferences).PreferredCameraId);
    }

    [Theory]
    [InlineData("QrCode")]
    [InlineData("Pdf417")]
    [InlineData("Aztec")]
    [InlineData("DataMatrix")]
    public void CapturePreservesSymbologyValueRawBytesAndUtc(string format)
    {
        var session = new ScanSession(new FixedClock());
        byte[] raw = [0, 255, 17];
        Assert.True(session.TryCapture(session.Generation, "  original value\n", format, raw));
        raw[0] = 99;
        var result = session.Result!;
        Assert.Equal("  original value\n", result.Value);
        Assert.Equal(format, result.Format);
        Assert.Equal(new byte[] { 0, 255, 17 }, result.RawBytes);
        result.RawBytes![0] = 88;
        Assert.Equal(0, result.RawBytes![0]);
        Assert.Equal(TimeSpan.Zero, result.DetectedUtc.Offset);
        Assert.Equal(new FixedClock().GetUtcNow(), result.DetectedUtc);
    }

    [Fact]
    public void ConcurrentFramesAcceptExactlyOneResultUntilRescan()
    {
        var session = new ScanSession(TimeProvider.System);
        var generation = session.Generation;
        var accepted = 0;
        Parallel.For(0, 100, n =>
        {
            if (session.TryCapture(generation, n.ToString(), "Pdf417", null)) Interlocked.Increment(ref accepted);
        });
        Assert.Equal(1, accepted);
        Assert.Null(session.Result!.RawBytes);
        session.Clear();
        Assert.Null(session.Result);
        Assert.False(session.TryCapture(generation, "stale frame", "QrCode", null));
        Assert.True(session.TryCapture(session.Generation, "new scan", "Aztec", null));
    }

    [Fact]
    public void LifecycleInvalidationRetainsResultAndRejectsQueuedFrames()
    {
        var session = new ScanSession(TimeProvider.System);
        var old = session.Generation;
        session.InvalidateFrames();
        Assert.False(session.TryCapture(old, "old", "QrCode", null));
        Assert.True(session.TryCapture(session.Generation, "ticket", "Pdf417", null));
        var result = session.Result;
        session.InvalidateFrames();
        Assert.Same(result, session.Result);
        Assert.False(session.TryCapture(session.Generation, null, "Pdf417", null));
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 11, 8, 30, 0, TimeSpan.Zero);
    }

    private sealed class MemoryPreferences : IPreferences
    {
        private readonly Dictionary<string, object> values = [];
        public bool ContainsKey(string key, string? sharedName = null) => values.ContainsKey(key);
        public void Remove(string key, string? sharedName = null) => values.Remove(key);
        public void Clear(string? sharedName = null) => values.Clear();
        public void Set<T>(string key, T value, string? sharedName = null) => values[key] = value!;
        public T Get<T>(string key, T defaultValue, string? sharedName = null) => values.TryGetValue(key, out var value) ? (T)value : defaultValue;
    }
}
