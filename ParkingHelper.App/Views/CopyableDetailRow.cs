namespace ParkingHelper.App.Views;

// The full field is a native button for keyboard and accessibility activation.
// Native long-press recognizers reveal an explicit Copy button without editing the value.
public sealed class CopyableDetailRow : ContentView
{
    private readonly Button fieldButton;
    private readonly Button copyButton;
    public event EventHandler? CopyRequested;
#if ANDROID
    private Android.Views.View? nativeView;
#elif IOS || MACCATALYST
    private UIKit.UIView? nativeView;
    private UIKit.UILongPressGestureRecognizer? longPress;
#endif

    public CopyableDetailRow(string key, string value)
    {
        var keyLabel = new Label { Text = key, FontSize = 17 };
        var valueLabel = new Label { Text = value, FontSize = 14, LineBreakMode = LineBreakMode.CharacterWrap };
        valueLabel.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("52647A"), Color.FromArgb("B8C8DE"));
        var labels = new VerticalStackLayout { Spacing = 4, InputTransparent = true, Children = { keyLabel, valueLabel } };
        fieldButton = new Button { BackgroundColor = Colors.Transparent, BorderWidth = 0, Padding = 0,
            MinimumHeightRequest = 52, HorizontalOptions = LayoutOptions.Fill, VerticalOptions = LayoutOptions.Fill };
        SemanticProperties.SetDescription(fieldButton, $"{key}: {value}");
        SemanticProperties.SetHint(fieldButton, "Long press or activate to reveal Copy");
        fieldButton.Clicked += (_, _) => RevealCopy();
        fieldButton.HandlerChanged += (_, _) => AttachLongPress();
        fieldButton.HandlerChanging += (_, _) => DetachLongPress();
        Loaded += (_, _) => AttachLongPress();
        Unloaded += (_, _) => { DetachLongPress(); HideCopy(); };
        var field = new Grid();
        field.Add(fieldButton);
        field.Add(labels);
        copyButton = new Button { Text = "Copy", IsVisible = false, HorizontalOptions = LayoutOptions.End,
            Style = (Style)Application.Current!.Resources["QuietButton"] };
        SemanticProperties.SetDescription(copyButton, $"Copy {key}");
        copyButton.Clicked += async (_, _) =>
        {
            try
            {
                await Clipboard.Default.SetTextAsync(value);
                copyButton.Text = "Copied";
                SemanticScreenReader.Default.Announce($"{key} copied");
            }
            catch { copyButton.Text = "Try copying again"; }
        };
        Content = new VerticalStackLayout { Padding = new Thickness(20, 14), Spacing = 8, Children = { field, copyButton } };
    }

    private void RevealCopy()
    {
        CopyRequested?.Invoke(this, EventArgs.Empty);
        copyButton.Text = "Copy";
        copyButton.IsVisible = true;
    }

    public void HideCopy() => copyButton.IsVisible = false;

    private void AttachLongPress()
    {
        DetachLongPress();
#if ANDROID
        nativeView = fieldButton.Handler?.PlatformView as Android.Views.View;
        if (nativeView != null) nativeView.LongClick += OnLongClick;
#elif IOS || MACCATALYST
        nativeView = fieldButton.Handler?.PlatformView as UIKit.UIView;
        if (nativeView == null) return;
        longPress = new UIKit.UILongPressGestureRecognizer(gesture =>
        {
            if (gesture.State == UIKit.UIGestureRecognizerState.Began) RevealCopy();
        });
        nativeView.AddGestureRecognizer(longPress);
#endif
    }

    private void DetachLongPress()
    {
#if ANDROID
        if (nativeView != null) nativeView.LongClick -= OnLongClick;
        nativeView = null;
#elif IOS || MACCATALYST
        if (longPress != null)
        {
            nativeView?.RemoveGestureRecognizer(longPress);
            longPress.Dispose();
            longPress = null;
        }
        nativeView = null;
#endif
    }
#if ANDROID
    private void OnLongClick(object? sender, Android.Views.View.LongClickEventArgs e)
    {
        e.Handled = true;
        RevealCopy();
    }
#endif
}
