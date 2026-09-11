using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Services;
using Microsoft.Maui.Storage;
using ParkingHelper.App.Services;
using ParkingHelper.Core.Models;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class ScanFeedbackTests
{
    private static TicketCreationResult Created() => new(new ParkingTicket(Guid.NewGuid(), Guid.NewGuid(), "Pdf417", "ticket",
        ParkingTicketState.Active, DateTime.UtcNow, DateTime.UtcNow, null, null), true);
    private static ScanFeedbackService Service(IScanFeedbackSettings settings, IScanFeedbackPlayer player) =>
        new(settings, player, NullLogger<ScanFeedbackService>.Instance);

    [Fact]
    public void SoundDefaultsOnAndPreferencePersistsAcrossInstances()
    {
        var memory = new MemoryPreferences();
        var settings = new ScanFeedbackSettings(memory);
        Assert.True(settings.ScanSoundEnabled);
        settings.ScanSoundEnabled = false;
        Assert.False(new ScanFeedbackSettings(memory).ScanSoundEnabled);
        new ScanFeedbackSettings(memory).ScanSoundEnabled = true;
        Assert.True(settings.ScanSoundEnabled);
    }

    [Fact]
    public async Task PreparingNeverEmitsFeedback()
    {
        var player = new Player();
        await Service(new Settings(), player).PrepareAsync();
        Assert.Equal(1, player.Preparations);
        Assert.Equal(0, player.Sounds);
        Assert.Equal(0, player.Haptics);
    }

    [Fact]
    public async Task OnlyNewCommittedActiveTicketEmitsBothChannelsOnce()
    {
        var player = new Player();
        var service = Service(new Settings(), player);
        var result = Created();
        await service.NotifySavedAsync(null); // Decode, validation, or save failure: no created result.
        await service.NotifySavedAsync(result with { Created = false });
        await service.NotifySavedAsync(result with { Ticket = result.Ticket with { Id = Guid.Empty } });
        Assert.Equal(0, player.Sounds);
        Assert.Equal(0, player.Haptics);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => service.NotifySavedAsync(result)));
        Assert.Equal(1, player.Sounds);
        Assert.Equal(1, player.Haptics);
        await service.NotifySavedAsync(Created());
        Assert.Equal(2, player.Sounds);
        Assert.Equal(2, player.Haptics);
    }

    [Fact]
    public async Task SoundDisabledStillUsesStrongHaptic()
    {
        var player = new Player();
        await Service(new Settings { ScanSoundEnabled = false }, player).NotifySavedAsync(Created());
        Assert.Equal(0, player.Sounds);
        Assert.Equal(1, player.Haptics);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PlaybackOrHapticFailuresDoNotThrowOrSuppressOtherChannel(bool soundFails, bool hapticFails)
    {
        var player = new Player { SoundFails = soundFails, HapticFails = hapticFails };
        await Service(new Settings(), player).NotifySavedAsync(Created());
        Assert.Equal(1, player.Sounds);
        Assert.Equal(1, player.Haptics);
    }

    [Fact]
    public async Task PreparationFailureStillAllowsHapticAndNextSuccessRetriesAudio()
    {
        var player = new Player { PreparationFails = true };
        var service = Service(new Settings(), player);
        await service.PrepareAsync();
        await service.NotifySavedAsync(Created());
        Assert.Equal(0, player.Sounds);
        Assert.Equal(1, player.Haptics);
        player.PreparationFails = false;
        await service.NotifySavedAsync(Created());
        Assert.Equal(1, player.Sounds);
        Assert.Equal(2, player.Haptics);
    }

    [Fact]
    public async Task CancelledForegroundSessionDoesNotStartFeedback()
    {
        var player = new Player();
        await Service(new Settings(), player).NotifySavedAsync(Created(), new CancellationToken(true));
        Assert.Equal(0, player.Sounds);
        Assert.Equal(0, player.Haptics);
    }

    [Fact]
    public async Task UnavailablePreferencesDoNotSuppressHapticOrThrow()
    {
        var player = new Player();
        await Service(new BrokenSettings(), player).NotifySavedAsync(Created());
        Assert.Equal(0, player.Sounds);
        Assert.Equal(1, player.Haptics);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("duplicate")]
    [InlineData("invalid")]
    [InlineData("database-failure")]
    public async Task RealSaveFeedbackOnlySeesCommittedNewTicket(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "ParkingHelper.Feedback", Guid.NewGuid().ToString());
        var path = Path.Combine(directory, "parking.db3");
        try
        {
            var repository = new SqliteParkingRepository(path);
            var plates = new PlateService(repository, TimeProvider.System);
            var plate = await plates.AddAsync("ABC123");
            var tickets = new TicketService(repository);
            var scan = new ScanResult(scenario == "invalid" ? " " : "ticket", "Pdf417", [1, 2], DateTimeOffset.UtcNow);
            if (scenario == "duplicate") await tickets.CreateActiveTicketAsync(scan, plate);
            if (scenario == "database-failure")
            {
                using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TRIGGER FailFeedbackSave BEFORE INSERT ON ParkingTickets BEGIN SELECT RAISE(ABORT, 'test disk failure'); END;";
                command.ExecuteNonQuery();
            }
            var model = new ScanViewModel(plates, new SelectedPlatePreference(new MemoryPreferences()), tickets,
                NullLogger<ScanViewModel>.Instance);
            await model.LoadAsync();
            var player = new Player();
            var feedback = Service(new Settings(), player);
            await feedback.PrepareAsync();
            Assert.Equal(0, player.Sounds);
            Assert.Equal(0, player.Haptics);

            var outcome = await model.SaveScanAsync(scan);
            var visibleAtPlayback = false;
            player.OnSound = async () =>
            {
                // An independent connection can only see the row after the write committed.
                var stored = await new SqliteParkingRepository(path).GetTicketAsync(outcome!.Ticket.Id);
                visibleAtPlayback = stored?.Id == outcome.Ticket.Id && stored.VehiclePlateId == plate.Id;
            };
            await feedback.NotifySavedAsync(outcome);
            Assert.Equal(scenario == "new" ? 1 : 0, player.Sounds);
            Assert.Equal(scenario == "new" ? 1 : 0, player.Haptics);
            Assert.Equal(scenario == "new", visibleAtPlayback);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class Player : IScanFeedbackPlayer
    {
        public int Preparations { get; private set; }
        public int Sounds { get; private set; }
        public int Haptics { get; private set; }
        public Func<Task>? OnSound { get; set; }
        public bool PreparationFails { get; set; }
        public bool SoundFails { get; set; }
        public bool HapticFails { get; set; }
        public Task PrepareAsync() { Preparations++; return PreparationFails ? Task.FromException(new IOException()) : Task.CompletedTask; }
        public async Task PlaySuccessSoundAsync()
        {
            Sounds++;
            if (SoundFails) throw new InvalidOperationException();
            if (OnSound != null) await OnSound();
        }
        public Task PerformStrongHapticAsync() { Haptics++; return HapticFails ? Task.FromException(new NotSupportedException()) : Task.CompletedTask; }
    }
    private sealed class Settings : IScanFeedbackSettings { public bool ScanSoundEnabled { get; set; } = true; }
    private sealed class BrokenSettings : IScanFeedbackSettings
    {
        public bool ScanSoundEnabled { get => throw new IOException(); set => throw new IOException(); }
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
