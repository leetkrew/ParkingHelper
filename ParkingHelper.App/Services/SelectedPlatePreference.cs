using Microsoft.Maui.Storage;
using ParkingHelper.Core.Services;

namespace ParkingHelper.App.Services;

public sealed class SelectedPlatePreference(IPreferences preferences) : ISelectedPlatePreference
{
    public Guid? SelectedPlateId
    {
        get => Guid.TryParse(preferences.Get("scanner.lastSelectedPlateId", ""), out var id) && id != Guid.Empty ? id : null;
        set => preferences.Set("scanner.lastSelectedPlateId", value?.ToString("D") ?? "");
    }
}
