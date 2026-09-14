namespace ParkingHelper.App.Views;

// Custom swipe items avoid the platform's fixed menu-item widths and text padding.
public sealed class CompactSwipeAction : SwipeItemView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(nameof(Text), typeof(string), typeof(CompactSwipeAction), "",
        propertyChanged: (bindable, _, _) => ((CompactSwipeAction)bindable).UpdateAccessibility());
    public static readonly BindableProperty IconImageSourceProperty = BindableProperty.Create(nameof(IconImageSource), typeof(ImageSource), typeof(CompactSwipeAction), null,
        propertyChanged: (bindable, _, value) => ((CompactSwipeAction)bindable).icon.Source = (ImageSource?)value);
    private readonly Image icon = new() { WidthRequest = 24, HeightRequest = 24, Aspect = Aspect.AspectFit,
        HorizontalOptions = LayoutOptions.Center, VerticalOptions = LayoutOptions.Center, InputTransparent = true };
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public ImageSource? IconImageSource { get => (ImageSource?)GetValue(IconImageSourceProperty); set => SetValue(IconImageSourceProperty, value); }

    public CompactSwipeAction()
    {
        WidthRequest = ParkingHelper.App.Layout.SwipeLayout.PreferredActionWidth;
        MinimumWidthRequest = 0;
        HorizontalOptions = LayoutOptions.Fill;
        Content = new Grid { Padding = 0, MinimumHeightRequest = 48, IsClippedToBounds = true, Children = { icon } };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IsEnabled)) icon.Opacity = IsEnabled ? 1 : 0.35;
        };
    }

    private void UpdateAccessibility()
    {
        SemanticProperties.SetDescription(this, Text);
        ToolTipProperties.SetText(this, Text);
        AutomationProperties.SetIsInAccessibleTree(this, true);
    }
}
