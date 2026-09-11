using Microsoft.Extensions.Logging;

namespace ParkingHelper.App.Services;

/// <summary>Native playback is kept alive across Scan → Preview navigation.</summary>
public sealed class NativeScanFeedbackPlayer(ILogger<NativeScanFeedbackPlayer> logger) : IScanFeedbackPlayer, IDisposable
{
    private readonly SemaphoreSlim preparation = new(1, 1);
    private string? soundPath;
    private volatile bool disposed;
#if ANDROID
    private Android.Media.MediaPlayer? androidPlayer;
#elif IOS || MACCATALYST
    private AVFoundation.AVAudioPlayer? applePlayer;
#endif

    public async Task PrepareAsync()
    {
        await preparation.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (soundPath == null || !File.Exists(soundPath))
            {
                var path = Path.Combine(FileSystem.Current.CacheDirectory, "parking-scan-success-v1.wav");
                await using var source = await FileSystem.Current.OpenAppPackageFileAsync("scan_success.wav");
                await using (var destination = File.Create(path)) await source.CopyToAsync(destination);
                soundPath = path;
            }
            await MainThread.InvokeOnMainThreadAsync(() => PrepareNative(soundPath));
        }
        finally { preparation.Release(); }
    }

    private void PrepareNative(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
#if ANDROID
        if (androidPlayer != null) return;
        var player = new Android.Media.MediaPlayer();
        try
        {
            using var attributes = new Android.Media.AudioAttributes.Builder()
                .SetUsage(Android.Media.AudioUsageKind.AssistanceSonification)!
                .SetContentType(Android.Media.AudioContentType.Sonification)!.Build()!;
            player.SetAudioAttributes(attributes);
            player.SetDataSource(path);
            player.Looping = false;
            player.SetVolume(1f, 1f); // Relative player gain only; never change device volume.
            player.Prepare();
            player.Error += OnAndroidError;
            androidPlayer = player;
        }
        catch { ReleaseAndroidPlayer(player); throw; }
#elif IOS || MACCATALYST
        if (applePlayer != null) return;
        // PrepareToPlay may activate the session, so establish mixing/mute behavior before preloading.
        if (AVFoundation.AVAudioSession.SharedInstance().SetCategory(AVFoundation.AVAudioSessionCategory.Ambient) is { } categoryError)
            throw new InvalidOperationException(categoryError?.LocalizedDescription ?? "Audio session unavailable.");
        using var url = Foundation.NSUrl.FromFilename(path);
        var player = AVFoundation.AVAudioPlayer.FromUrl(url, out var error);
        if (player == null) throw new InvalidOperationException(error?.LocalizedDescription ?? "Audio player unavailable.");
        player.Volume = 1f;
        player.NumberOfLoops = 0;
        if (!player.PrepareToPlay()) { player.Dispose(); throw new InvalidOperationException("Could not prepare scan sound."); }
        applePlayer = player;
#endif
        logger.LogDebug("Bundled scan success sound prepared");
    }

    public Task PlaySuccessSoundAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
#if ANDROID
        var player = androidPlayer ?? throw new InvalidOperationException("Scan sound is not prepared.");
        try { player.SeekTo(0); player.Start(); }
        catch { androidPlayer = null; ReleaseAndroidPlayer(player); throw; }
#elif IOS || MACCATALYST
        // Ambient mixes with other audio and respects the silent switch/lock and system routing.
        var session = AVFoundation.AVAudioSession.SharedInstance();
        if (session.SetCategory(AVFoundation.AVAudioSessionCategory.Ambient) is { } error)
            throw new InvalidOperationException(error?.LocalizedDescription ?? "Audio session unavailable.");
        var player = applePlayer ?? throw new InvalidOperationException("Scan sound is not prepared.");
        player.CurrentTime = 0;
        if (!player.Play()) throw new InvalidOperationException("Could not start scan sound.");
#endif
    });

    public Task PerformStrongHapticAsync() => MainThread.InvokeOnMainThreadAsync(() =>
    {
#if ANDROID
        var context = Android.App.Application.Context;
        // Respect the user's haptic-feedback preference. Do not substitute notification vibrations.
        if (Android.Provider.Settings.System.GetInt(context.ContentResolver, "haptic_feedback_enabled", 1) == 0) return;
        Android.OS.Vibrator? vibrator;
        if (OperatingSystem.IsAndroidVersionAtLeast(31))
            vibrator = (context.GetSystemService(Android.Content.Context.VibratorManagerService) as Android.OS.VibratorManager)?.DefaultVibrator;
        else
            vibrator = context.GetSystemService(Android.Content.Context.VibratorService) as Android.OS.Vibrator;
        if (vibrator?.HasVibrator != true) return;
        using var attributes = new Android.Media.AudioAttributes.Builder()
            .SetUsage(Android.Media.AudioUsageKind.AssistanceSonification)!
            .SetContentType(Android.Media.AudioContentType.Sonification)!.Build()!;
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            using var effect = Android.OS.VibrationEffect.CreateOneShot(200, 255)
                ?? throw new InvalidOperationException("Haptic effect unavailable.");
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                using var vibrationAttributes = new Android.OS.VibrationAttributes.Builder()
                    .SetUsage((int)Android.OS.VibrationAttributesUsageType.Touch)!.Build()!;
                vibrator.Vibrate(effect, vibrationAttributes);
            }
            else
                vibrator.Vibrate(effect, attributes);
        }
        else
        {
#pragma warning disable CS0618
            vibrator.Vibrate(200L, attributes);
#pragma warning restore CS0618
        }
#elif IOS
        if (!OperatingSystem.IsIOS()) return;
        if (OperatingSystem.IsIOSVersionAtLeast(17, 5))
        {
            var window = UIKit.UIApplication.SharedApplication.ConnectedScenes.OfType<UIKit.UIWindowScene>()
                .SelectMany(scene => scene.Windows).FirstOrDefault(candidate => candidate.IsKeyWindow);
            if (window == null) return;
            using var generator = UIKit.UIImpactFeedbackGenerator.GetFeedbackGenerator(UIKit.UIImpactFeedbackStyle.Heavy, window);
            generator.Prepare();
            generator.ImpactOccurred(1f);
        }
        else
        {
            using var generator = new UIKit.UIImpactFeedbackGenerator(UIKit.UIImpactFeedbackStyle.Heavy);
            generator.Prepare();
            generator.ImpactOccurred(1f);
        }
        // UIKit has no supported equivalent for Mac Catalyst desktop hardware.
#endif
    });

#if ANDROID
    private void OnAndroidError(object? sender, Android.Media.MediaPlayer.ErrorEventArgs args)
    {
        args.Handled = true;
        if (sender is not Android.Media.MediaPlayer player) return;
        if (ReferenceEquals(androidPlayer, player)) androidPlayer = null;
        logger.LogWarning("Native scan sound playback became unavailable");
        ReleaseAndroidPlayer(player);
    }

    private void ReleaseAndroidPlayer(Android.Media.MediaPlayer player)
    {
        try { player.Error -= OnAndroidError; }
        catch (Exception exception) { logger.LogDebug(exception, "Could not detach audio callback"); }
        try { player.Release(); }
        catch (Exception exception) { logger.LogDebug(exception, "Could not release audio player"); }
        try { player.Dispose(); }
        catch (Exception exception) { logger.LogDebug(exception, "Could not dispose audio player"); }
    }
#endif

    public void Dispose()
    {
        disposed = true;
        // Native state and callbacks are owned by the UI thread, including teardown.
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
#if ANDROID
                var player = androidPlayer;
                androidPlayer = null;
                if (player != null) ReleaseAndroidPlayer(player);
#elif IOS || MACCATALYST
                applePlayer?.Stop();
                applePlayer?.Dispose();
                applePlayer = null;
#endif
            }
            catch (Exception exception) { logger.LogDebug(exception, "Could not dispose scan feedback player"); }
        });
    }
}
