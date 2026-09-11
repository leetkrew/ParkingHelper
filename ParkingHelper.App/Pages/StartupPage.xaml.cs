using Microsoft.Extensions.Logging;
using ParkingHelper.App.Services;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Pages;

public partial class StartupPage : ContentPage
{
    private readonly IPlateService plates;
    private readonly AppNavigation navigation;
    private readonly ILogger<StartupPage> logger;
    private bool loading;

    public StartupPage(IPlateService plates, AppNavigation navigation, ILogger<StartupPage> logger)
    {
        this.plates = plates;
        this.navigation = navigation;
        this.logger = logger;
        InitializeComponent();
    }
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async void OnRetry(object? sender, EventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (loading) return;
        loading = true;
        Loading.IsRunning = true;
        ErrorMessage.IsVisible = RetryButton.IsVisible = false;
        try
        {
            var needsSetup = await plates.NeedsSetupAsync();
            if (Window is not Window window) return;
            if (needsSetup) navigation.ShowSetup(window);
            else navigation.ShowHome(window);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Could not determine plate setup state");
            ErrorMessage.IsVisible = RetryButton.IsVisible = true;
        }
        finally { loading = false; Loading.IsRunning = false; }
    }
}
