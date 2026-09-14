using ParkingHelper.App.ViewModels;
using ParkingHelper.App.Services;

namespace ParkingHelper.App.Views;

public partial class PlateEditorView : ContentView
{
    private bool confirmingDeletion;
    private readonly SingleOpenRow<SwipeView> openRow = new(row => row.Close(false));
    private PlateEditorViewModel Model => (PlateEditorViewModel)BindingContext;
    public event EventHandler? LastPlateDeleted;

    public void CloseSwipeActions() => openRow.Close();

    public PlateEditorView()
    {
        InitializeComponent();
        Unloaded += (_, _) => openRow.Close();
    }

    private void OnSwipeStarted(object? sender, SwipeStartedEventArgs e)
    {
        if (sender is SwipeView row) openRow.Open(row);
    }
    private void OnSwipeEnded(object? sender, SwipeEndedEventArgs e)
    {
        if (!e.IsOpen && sender is SwipeView row) openRow.Closed(row);
    }
    private void OnRowUnloaded(object? sender, EventArgs e)
    {
        if (sender is SwipeView row) { openRow.Closed(row); row.Close(false); }
    }
    private void OnRowContextChanged(object? sender, EventArgs e)
    {
        if (sender is not Border { BindingContext: PlateRow row } border) return;
        if (border.Parent is SwipeView swipe) { openRow.Closed(swipe); swipe.Close(false); }
#if MACCATALYST
        var menu = new MenuFlyout();
        MenuFlyoutItem Item(string text, EventHandler action, bool enabled = true)
        {
            var item = new MenuFlyoutItem { Text = text, BindingContext = row, IsEnabled = enabled };
            item.Command = new Command(() => action(item, EventArgs.Empty), () => enabled);
            return item;
        }
        menu.Add(Item("Move Up", OnMoveUp, row.CanMoveUp));
        menu.Add(Item("Move Down", OnMoveDown, row.CanMoveDown));
        menu.Add(Item("Edit", OnEdit));
        menu.Add(Item("Delete", OnDelete));
        FlyoutBase.SetContextFlyout(border, menu);
        ToolTipProperties.SetText(border, "Right-click or Control-click for plate actions");
#endif
    }

    private async void OnSubmit(object? sender, EventArgs e)
    {
        if (!Model.CanSubmit || confirmingDeletion) return;
        openRow.Close();
        PlateInput.Unfocus();
        await Model.SaveAsync();
    }

    private void OnCancel(object? sender, EventArgs e) => Model.CancelEdit();

    private async void OnRefresh(object? sender, EventArgs e)
    {
        openRow.Close();
        if (!confirmingDeletion) await Model.LoadAsync();
    }

    private void OnEdit(object? sender, EventArgs e)
    {
        if (sender is BindableObject { BindingContext: PlateRow row } && !confirmingDeletion)
        {
            openRow.Close();
            Model.Edit(row);
            PlateInput.Focus();
        }
    }

    private async void OnDelete(object? sender, EventArgs e)
    {
        if (!Model.IsReady || confirmingDeletion || sender is not BindableObject { BindingContext: PlateRow row }
            || Window?.Page is not Page page) return;
        openRow.Close();
        confirmingDeletion = true;
        try
        {
            if (!await page.DisplayAlertAsync($"Delete {row.PlateNumber}?", "", "Delete", "Cancel")) return;
            if (await Model.DeleteAsync(row.Id) && Model.Plates.Count == 0)
                LastPlateDeleted?.Invoke(this, EventArgs.Empty);
        }
        finally { confirmingDeletion = false; }
    }

    private async void OnMoveUp(object? sender, EventArgs e)
    {
        if (Model.IsReady && !confirmingDeletion && sender is BindableObject { BindingContext: PlateRow { CanMoveUp: true } row })
        {
            openRow.Close();
            await Model.MoveAsync(row.Id, -1);
        }
    }

    private async void OnMoveDown(object? sender, EventArgs e)
    {
        if (Model.IsReady && !confirmingDeletion && sender is BindableObject { BindingContext: PlateRow { CanMoveDown: true } row })
        {
            openRow.Close();
            await Model.MoveAsync(row.Id, 1);
        }
    }
}
