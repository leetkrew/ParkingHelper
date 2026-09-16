using ParkingHelper.App.ViewModels;

namespace ParkingHelper.App.Pages;

public partial class ArchiveExportPage : ContentPage
{
    private readonly ArchiveExportViewModel model;
    private bool leaving;
    public ArchiveExportPage(ArchiveExportViewModel model)
    {
        InitializeComponent();
        BindingContext = this.model = model;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await model.LoadAsync();
    }

    private async void OnBack(object? sender, EventArgs e)
    {
        if (leaving) return;
        leaving = true;
        try { await Navigation.PopAsync(); }
        catch { await DisplayAlertAsync("Navigation unavailable", "Couldn’t go back. Please try again.", "OK"); }
        finally { leaving = false; }
    }

    private async void OnReload(object? sender, EventArgs e) => await model.LoadAsync();
    private void OnSelectAll(object? sender, EventArgs e) => model.SelectAll();
    private void OnClearSelection(object? sender, EventArgs e) => model.ClearSelection();
    private async void OnExport(object? sender, EventArgs e) => await model.ExportAsync();
}
