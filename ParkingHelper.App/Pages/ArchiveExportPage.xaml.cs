using ParkingHelper.App.ViewModels;

namespace ParkingHelper.App.Pages;

public partial class ArchiveExportPage : ContentPage
{
    private readonly ArchiveExportViewModel model;
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

    private void OnSelectAll(object? sender, EventArgs e) => model.SelectAll();
    private void OnClearSelection(object? sender, EventArgs e) => model.ClearSelection();
    private async void OnExport(object? sender, EventArgs e) => await model.ExportAsync();
}
