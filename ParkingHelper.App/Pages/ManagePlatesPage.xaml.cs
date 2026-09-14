using ParkingHelper.App.Services;
using ParkingHelper.App.ViewModels;

namespace ParkingHelper.App.Pages;

public partial class ManagePlatesPage : ContentPage
{
    private readonly PlateEditorViewModel model;
    private readonly AppNavigation navigation;

    public ManagePlatesPage(PlateEditorViewModel model, AppNavigation navigation)
    {
        this.model = model;
        this.navigation = navigation;
        InitializeComponent();
        BindingContext = model;
        Editor.LastPlateDeleted += OnLastPlateDeleted;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        Editor.CloseSwipeActions();
        if (await model.LoadAsync() && model.Plates.Count == 0 && Window is Window window)
            navigation.ShowSetup(window);
    }

    protected override void OnDisappearing()
    {
        Editor.CloseSwipeActions();
        base.OnDisappearing();
    }

    private void OnLastPlateDeleted(object? sender, EventArgs e)
    {
        if (Window is Window window) navigation.ShowSetup(window);
    }
}
