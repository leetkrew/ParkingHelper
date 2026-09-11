using ParkingHelper.App.Services;
using ParkingHelper.App.ViewModels;

namespace ParkingHelper.App.Pages;

public partial class InitialSetupPage : ContentPage
{
    private readonly PlateEditorViewModel model;
    private readonly AppNavigation navigation;
    private bool continuing;

    public InitialSetupPage(PlateEditorViewModel model, AppNavigation navigation)
    {
        this.model = model;
        this.navigation = navigation;
        InitializeComponent();
        BindingContext = model;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await model.LoadAsync();
    }

    protected override bool OnBackButtonPressed() => true;

    private async void OnContinue(object? sender, EventArgs e)
    {
        if (continuing || !model.CanContinue) return;
        continuing = true;
        try
        {
            // Recheck persisted state so a stale list cannot bypass the minimum requirement.
            if (await model.LoadAsync() && model.CanContinue && Window is Window window)
                navigation.ShowHome(window);
        }
        finally { continuing = false; }
    }
}
