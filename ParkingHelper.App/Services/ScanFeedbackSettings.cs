using Microsoft.Maui.Storage;

namespace ParkingHelper.App.Services;

public interface IScanFeedbackSettings
{
    bool ScanSoundEnabled { get; set; }
}

// A future settings toggle can bind to this property; no UI is needed in this milestone.
public sealed class ScanFeedbackSettings(IPreferences preferences) : IScanFeedbackSettings
{
    public bool ScanSoundEnabled
    {
        get => preferences.Get("scanner.soundEnabled", true);
        set => preferences.Set("scanner.soundEnabled", value);
    }
}
