using ParkingHelper.App.ViewModels;

namespace ParkingHelper.App.Views;

public partial class PlateEditorView : ContentView
{
    private bool confirmingDeletion;
    private PlateEditorViewModel Model => (PlateEditorViewModel)BindingContext;
    public event EventHandler? LastPlateDeleted;

    public PlateEditorView() => InitializeComponent();

    private async void OnSubmit(object? sender, EventArgs e)
    {
        if (!Model.CanSubmit || confirmingDeletion) return;
        PlateInput.Unfocus();
        await Model.SaveAsync();
    }

    private void OnCancel(object? sender, EventArgs e) => Model.CancelEdit();

    private async void OnRefresh(object? sender, EventArgs e)
    {
        if (!confirmingDeletion) await Model.LoadAsync();
    }

    private void OnEdit(object? sender, EventArgs e)
    {
        if (sender is BindableObject { BindingContext: PlateRow row } && !confirmingDeletion)
        {
            Model.Edit(row);
            PlateInput.Focus();
        }
    }

    private async void OnDelete(object? sender, EventArgs e)
    {
        if (!Model.IsReady || confirmingDeletion || sender is not BindableObject { BindingContext: PlateRow row }
            || Window?.Page is not Page page) return;
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
        if (Model.IsReady && !confirmingDeletion && sender is BindableObject { BindingContext: PlateRow row })
            await Model.MoveAsync(row.Id, -1);
    }

    private async void OnMoveDown(object? sender, EventArgs e)
    {
        if (Model.IsReady && !confirmingDeletion && sender is BindableObject { BindingContext: PlateRow row })
            await Model.MoveAsync(row.Id, 1);
    }
}
