using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using ParkingHelper.App.Services;
using ParkingHelper.App.ViewModels;
using ParkingHelper.Core.Models;
using ParkingHelper.Core.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class SwipeActionsTests
{
    [Fact]
    public void OpeningAnotherRowClosesPreviousAndLateClosedEventsCannotLoseNewOwner()
    {
        var closed = new List<object>();
        var owner = new SingleOpenRow<object>(closed.Add);
        var a = new object(); var b = new object(); var c = new object();
        owner.Open(a); owner.Open(a); Assert.Empty(closed);
        owner.Open(b); Assert.Equal(new[] { a }, closed);
        owner.Closed(a); // Native animation callback for the old row arrives late.
        owner.Open(c); Assert.Equal(new[] { a, b }, closed);
        owner.Close(); owner.Close(); Assert.Equal(new[] { a, b, c }, closed);
    }

    [Fact]
    public void RowUnloadedAndReentrantNativeCloseDoNotRetainOpenOwnership()
    {
        var row = new object(); var closed = 0;
        SingleOpenRow<object>? owner = null;
        owner = new(item => { closed++; owner!.Closed(item); });
        owner.Open(row); owner.Closed(row); owner.Close(); Assert.Equal(0, closed);
        owner.Open(row); owner.Close(); owner.Close(); Assert.Equal(1, closed);
    }

    [Fact]
    public void PlateRowsHaveOnlyPlateTextWithRightSwipeActionsAndDisabledReorderEndpoints()
    {
        var document = XDocument.Parse(Read("Views/PlateEditorView.xaml"));
        var template = Assert.Single(document.Descendants(), e => e.Name.LocalName == "DataTemplate");
        Assert.DoesNotContain(template.Descendants(), e => e.Name.LocalName == "Button");
        var swipe = Assert.Single(template.Descendants(), e => e.Name.LocalName == "BoundedSwipeView");
        Assert.Equal("OnSwipeStarted", (string?)swipe.Attribute("SwipeStarted"));
        var right = Assert.Single(swipe.Elements(), e => e.Name.LocalName == "BoundedSwipeView.RightItems");
        var actions = right.Descendants().Where(e => e.Name.LocalName == "CompactSwipeAction").ToArray();
        Assert.Equal(new[] { "Move Up", "Move Down", "Edit", "Delete" }, actions.Select(e => (string?)e.Attribute("Text")));
        Assert.Equal("{Binding CanMoveUp}", (string?)actions[0].Attribute("IsEnabled"));
        Assert.Equal("{Binding CanMoveDown}", (string?)actions[1].Attribute("IsEnabled"));
        Assert.Equal("OnEdit", (string?)actions[2].Attribute("Invoked"));
        Assert.Equal("OnDelete", (string?)actions[3].Attribute("Invoked"));
        Assert.Equal("{Binding PlateNumber}", (string?)Assert.Single(template.Descendants(), e => e.Name.LocalName == "Label").Attribute("Text"));
    }

    [Theory]
    [InlineData(ParkingTicketState.Active, "Archive", "action_archive.png")]
    [InlineData(ParkingTicketState.Archived, "Restore", "action_restore.png")]
    public void TicketCardUsesStateAppropriateSwipeActionsAndKeepsTapPreview(ParkingTicketState state, string action, string icon)
    {
        var now = DateTime.UtcNow;
        var row = new TicketRowViewModel(new(Guid.NewGuid(), Guid.NewGuid(), "QR_CODE", "PRIVATE", state, now, now,
            state == ParkingTicketState.Archived ? now : null, null));
        Assert.Equal(action, row.StateAction); Assert.Equal(icon, row.StateActionIcon);
        var document = XDocument.Parse(Read("Pages/TicketsPage.xaml"));
        var template = Assert.Single(document.Descendants(), e => e.Name.LocalName == "DataTemplate");
        Assert.DoesNotContain(template.Descendants(), e => e.Name.LocalName == "Button");
        var actions = template.Descendants().Where(e => e.Name.LocalName == "CompactSwipeAction").ToArray();
        Assert.Equal(new[] { "Edit", "{Binding StateAction}", "Delete" }, actions.Select(e => (string?)e.Attribute("Text")));
        Assert.Equal(new[] { "OnEditTicket", "OnChangeTicketState", "OnDeleteTicket" }, actions.Select(e => (string?)e.Attribute("Invoked")));
        Assert.Equal("OnTicketTapped", (string?)Assert.Single(template.Descendants(), e => e.Name.LocalName == "TapGestureRecognizer").Attribute("Tapped"));
        Assert.Contains("page.EditOnOpen()", Read("Services/AppNavigation.cs"));
        Assert.Contains("await model.BeginEditPlateAsync()", Read("Pages/TicketPreviewPage.xaml.cs"));
    }

    [Fact]
    public void MacContextMenusExposeSameActionsWithoutAddingCardControls()
    {
        var plates = Read("Views/PlateEditorView.xaml.cs"); var tickets = Read("Pages/TicketsPage.xaml.cs");
        foreach (var code in new[] { plates, tickets })
        {
            Assert.Contains("#if MACCATALYST", code);
            Assert.Contains("FlyoutBase.SetContextFlyout(border, menu)", code);
            Assert.Contains("Control-click", code);
            Assert.Contains("openRow.Close()", code);
        }
        Assert.Contains("Item(\"Move Up\", OnMoveUp, row.CanMoveUp)", plates);
        Assert.Contains("Item(\"Move Down\", OnMoveDown, row.CanMoveDown)", plates);
        Assert.Contains("Item(row.StateAction, OnChangeTicketState)", tickets);
    }

    [Fact]
    public void EmptyStateAndSpinnerHaveExclusiveBindingsAndNativeRefreshHasOneOwner()
    {
        var document = XDocument.Parse(Read("Pages/TicketsPage.xaml"));
        var spinner = Assert.Single(document.Descendants(), e => e.Name.LocalName == "ActivityIndicator");
        Assert.Equal("{Binding IsLoading}", (string?)spinner.Attribute("IsRunning"));
        Assert.Equal("{Binding IsLoading}", (string?)spinner.Attribute("IsVisible"));
        var empty = Assert.Single(document.Descendants(), e => e.Name.LocalName == "CollectionView.EmptyView");
        Assert.Equal("{Binding ShowEmpty}", (string?)Assert.Single(empty.Elements()).Attribute("IsVisible"));
        Assert.DoesNotContain("TicketRefresh.IsRefreshing =", Read("Pages/TicketsPage.xaml.cs"));
        Assert.Contains("if (model.IsRefreshing) return", Read("Pages/TicketsPage.xaml.cs"));
    }

    [Fact]
    public void AllQuickActionIconsAreBundledVectorSources()
    {
        foreach (var name in new[] { "move_up", "move_down", "edit", "delete", "archive", "restore" })
        {
            var svg = XDocument.Parse(Read($"Resources/Images/action_{name}.svg"));
            Assert.Equal("svg", svg.Root!.Name.LocalName);
            Assert.Contains(svg.Descendants(), e => e.Name.LocalName == "path");
            Assert.DoesNotContain(svg.Descendants(), e => e.Name.LocalName == "image");
        }
    }

    [Fact]
    public async Task QuickActionsUseExistingTicketServicesAndPreserveDeletionTombstones()
    {
        var directory = Path.Combine(Path.GetTempPath(), "QuickActions", Guid.NewGuid().ToString());
        try
        {
            var repo = new SqliteParkingRepository(Path.Combine(directory, "data.db3"));
            var now = DateTime.UtcNow; var plate = new VehiclePlate(Guid.NewGuid(), " abc123 ", now, now);
            await repo.AddPlateAsync(plate);
            var service = new TicketService(repo);
            var ticket = (await service.CreateActiveTicketAsync(new("PRIVATE", "QR_CODE", null, DateTimeOffset.UtcNow),
                Assert.Single(await repo.GetPlatesAsync()))).Ticket;
            var model = new TicketsViewModel(service, TimeProvider.System, NullLogger<TicketsViewModel>.Instance);
            await model.LoadAsync(); Assert.Single(model.Items);
            await model.ChangeStateAsync(ticket.Id, ParkingTicketState.Archived);
            await model.ReloadAsync(); Assert.Empty(model.Items);
            await model.LoadAsync(true); Assert.True(Assert.Single(model.Items).IsArchived);
            await model.ChangeStateAsync(ticket.Id, ParkingTicketState.Active);
            await model.ReloadAsync(); Assert.Empty(model.Items);
            await model.LoadAsync(false); Assert.Single(model.Items);
            await model.ChangeStateAsync(ticket.Id, ParkingTicketState.Deleted);
            await model.ReloadAsync(); Assert.Empty(model.Items); Assert.True(model.ShowEmpty);
            Assert.True((await repo.GetSyncRecordsAsync()).Single(r => r.Id == ticket.Id).IsDeleted);
            await repo.DeletePlateAsync(plate.Id, DateTime.UtcNow);
            Assert.Empty(await repo.GetPlatesAsync());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static string Read(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "ParkingHelper.App"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "ParkingHelper.App", path));
    }
}
