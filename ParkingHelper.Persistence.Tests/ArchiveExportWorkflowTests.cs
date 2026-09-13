using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using ParkingHelper.App.Services;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class ArchiveExportWorkflowTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ParkingHelper.ExportWorkflow", Guid.NewGuid().ToString());
    private SqliteParkingRepository Repository => new(Path.Combine(directory, "parking.db3"));
    private TicketService Tickets => new(Repository);
    private sealed class ShareStub(Func<ArchiveExportResult, Task>? action = null) : IArchiveFileShareService
    {
        public List<ArchiveExportResult> Files { get; } = [];
        public Task ShareAsync(ArchiveExportResult export) { Files.Add(export); return action?.Invoke(export) ?? Task.CompletedTask; }
    }
    private ArchiveExportViewModel Model(ShareStub share) => new(new ArchiveExportService(Tickets), share, Tickets,
        NullLogger<ArchiveExportViewModel>.Instance);

    private async Task<ParkingTicket[]> Seed()
    {
        var repository = Repository;
        var plate = await new PlateService(repository, TimeProvider.System).AddAsync("EXPORT");
        var created = new DateTime(2026, 9, 1, 1, 0, 0, DateTimeKind.Utc);
        var result = new List<ParkingTicket>();
        foreach (var payload in new[] { "first", "second", "active", "deleted" })
        {
            var ticket = (await Tickets.CreateActiveTicketAsync(new(payload, "Pdf417", [1, 2], created), plate)).Ticket;
            if (payload != "active") ticket = await repository.ChangeTicketStateAsync(ticket.Id, ParkingTicketState.Archived, created.AddHours(2));
            if (payload == "deleted") ticket = await repository.ChangeTicketStateAsync(ticket.Id, ParkingTicketState.Deleted, created.AddHours(3));
            result.Add(ticket);
        }
        return result.ToArray();
    }

    [Theory]
    [InlineData(ArchiveExportScope.All, ArchiveExportFormat.Csv)]
    [InlineData(ArchiveExportScope.All, ArchiveExportFormat.Json)]
    [InlineData(ArchiveExportScope.Selected, ArchiveExportFormat.Csv)]
    [InlineData(ArchiveExportScope.Selected, ArchiveExportFormat.Json)]
    [InlineData(ArchiveExportScope.DateRange, ArchiveExportFormat.Csv)]
    [InlineData(ArchiveExportScope.DateRange, ArchiveExportFormat.Json)]
    public async Task EveryScopeAndFormatExportsArchivedOnlyWithoutAnyWrites(ArchiveExportScope scope, ArchiveExportFormat format)
    {
        var seeded = await Seed();
        var before = new List<string>();
        foreach (var ticket in seeded) before.Add(JsonSerializer.Serialize(await Repository.GetTicketAsync(ticket.Id)));
        var share = new ShareStub();
        var model = Model(share);
        await model.LoadAsync();
        Assert.Equal(2, model.Items.Count);
        model.Scope = scope;
        model.Format = format;
        model.FromDate = model.ToDate = seeded[0].ArchivedUtc!.Value.ToLocalTime().Date;
        model.Items[0].IsSelected = true;
        Assert.True(await model.ExportAsync());
        var file = Assert.Single(share.Files);
        Assert.Equal(scope == ArchiveExportScope.Selected ? 1 : 2, file.TicketCount);
        var text = Encoding.UTF8.GetString(file.Content);
        Assert.DoesNotContain(seeded[2].Id.ToString(), text);
        Assert.DoesNotContain(seeded[3].Id.ToString(), text);
        if (format == ArchiveExportFormat.Json)
        {
            using var json = JsonDocument.Parse(file.Content);
            Assert.Equal(file.TicketCount, json.RootElement.GetProperty("tickets").GetArrayLength());
        }
        else Assert.StartsWith("TicketId,PlateNumber,BarcodeFormat,BarcodeValue", text);
        for (var i = 0; i < seeded.Length; i++)
            Assert.Equal(before[i], JsonSerializer.Serialize(await Repository.GetTicketAsync(seeded[i].Id)));
    }

    [Fact]
    public async Task SelectedTicketsAreRequeriedAfterRestoreOrDelete()
    {
        var seeded = await Seed();
        var share = new ShareStub();
        var model = Model(share);
        await model.LoadAsync();
        model.Scope = ArchiveExportScope.Selected;
        model.SelectAll();
        await Tickets.RestoreTicketAsync(seeded[0].Id);
        await Tickets.DeleteTicketAsync(seeded[1].Id);
        Assert.False(await model.ExportAsync());
        Assert.Empty(share.Files);
        Assert.True(model.HasStatus);
        Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData(ArchiveExportScope.All)]
    [InlineData(ArchiveExportScope.Selected)]
    [InlineData(ArchiveExportScope.DateRange)]
    public async Task ZeroMatchingArchivesShowsFriendlyMessageWithoutOpeningShare(ArchiveExportScope scope)
    {
        var share = new ShareStub();
        var model = Model(share);
        await model.LoadAsync();
        model.Scope = scope;
        Assert.False(await model.ExportAsync());
        Assert.True(model.HasStatus);
        Assert.Empty(share.Files);
        Assert.True(model.CanExport);
    }

    [Fact]
    public async Task EmptySelectionAndClearedOrReversedDatesAreHandled()
    {
        await Seed();
        var share = new ShareStub();
        var model = Model(share);
        await model.LoadAsync();
        model.Scope = ArchiveExportScope.Selected;
        model.ClearSelection();
        Assert.False(await model.ExportAsync());
        model.Scope = ArchiveExportScope.DateRange;
        model.FromDate = null;
        Assert.False(await model.ExportAsync());
        Assert.Contains("both", model.Status);
        model.FromDate = DateTime.Today.AddDays(1);
        model.ToDate = DateTime.Today;
        Assert.False(await model.ExportAsync());
        Assert.Contains("on or before", model.Status);
        Assert.Empty(share.Files);
    }

    [Fact]
    public async Task NativeCancellationIsNotAnErrorAndAnotherExportCanRun()
    {
        await Seed();
        var cancel = true;
        var share = new ShareStub(_ => cancel ? Task.FromCanceled(new CancellationToken(true)) : Task.CompletedTask);
        var model = Model(share);
        await model.LoadAsync();
        Assert.False(await model.ExportAsync());
        Assert.False(model.HasStatus);
        Assert.False(model.IsBusy);
        cancel = false;
        Assert.True(await model.ExportAsync());
        Assert.False(model.HasStatus);
        Assert.Equal(2, share.Files.Count);
    }

    [Fact]
    public async Task UnavailableNativeShareAndFileErrorsAreContained()
    {
        await Seed();
        foreach (var error in new Exception[] { new NotSupportedException("native platform detail"), new IOException("private path"), new InvalidOperationException("native controller failure") })
        {
            var model = Model(new ShareStub(_ => Task.FromException(error)));
            await model.LoadAsync();
            Assert.False(await model.ExportAsync());
            Assert.Equal("Couldn’t create or share the export. Please try again.", model.Status);
            Assert.True(model.CanExport);
        }
    }

    [Fact]
    public async Task RapidExportsDoNotOpenMultipleNativeDialogsAndLoadCannotRaceExport()
    {
        await Seed();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var share = new ShareStub(_ => completion.Task);
        var model = Model(share);
        await model.LoadAsync();
        var first = model.ExportAsync();
        while (share.Files.Count == 0) await Task.Yield();
        for (var i = 0; i < 30; i++) Assert.False(await model.ExportAsync());
        await model.LoadAsync();
        Assert.Single(share.Files);
        Assert.True(model.IsBusy);
        completion.SetResult();
        Assert.True(await first);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task LoadFailureIsContainedAndReloadCanRecover()
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "parking.db3");
        await File.WriteAllTextAsync(path, "not a SQLite database");
        var model = Model(new ShareStub());
        await model.LoadAsync();
        Assert.True(model.HasLoadError);
        Assert.Contains("Reload archives", model.Status);
        Assert.True(model.CanExport);
        File.Delete(path);
        await model.LoadAsync();
        Assert.False(model.HasLoadError);
        Assert.False(model.HasStatus);
        Assert.Empty(model.Items);
    }

    [Fact]
    public async Task InvalidEnumsAndNullRequestAreRejectedBeforeNativeSharing()
    {
        var exporter = new ArchiveExportService(Tickets);
        await Assert.ThrowsAsync<ArgumentNullException>(() => exporter.ExportAsync(null!));
        await Assert.ThrowsAsync<ArchiveExportException>(() => exporter.ExportAsync(new((ArchiveExportScope)99, ArchiveExportFormat.Csv)));
        await Assert.ThrowsAsync<ArchiveExportException>(() => exporter.ExportAsync(new(ArchiveExportScope.All, (ArchiveExportFormat)99)));
    }

    [Fact]
    public void ExportPageStaticResourcesExistInMergedAppResources()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !Directory.Exists(Path.Combine(root.FullName, "ParkingHelper.App"))) root = root.Parent;
        Assert.NotNull(root);
        var app = Path.Combine(root.FullName, "ParkingHelper.App");
        XNamespace x = "http://schemas.microsoft.com/winfx/2009/xaml";
        var keys = Directory.GetFiles(Path.Combine(app, "Resources", "Styles"), "*.xaml")
            .SelectMany(path => XDocument.Load(path).Descendants().Attributes(x + "Key").Select(key => key.Value)).ToHashSet();
        var page = File.ReadAllText(Path.Combine(app, "Pages", "ArchiveExportPage.xaml"));
        foreach (Match resource in Regex.Matches(page, @"\{StaticResource\s+([^}]+)\}"))
            Assert.Contains(resource.Groups[1].Value, keys);
    }

    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}
