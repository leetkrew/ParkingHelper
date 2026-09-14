namespace ParkingHelper.App.Views;

// A full-width native button supplies keyboard/accessibility behavior; the visible
// label and chevron retain the quiet appearance of a standard settings list row.
public sealed class SettingsRow : ContentView
{
    public static readonly BindableProperty TextProperty = BindableProperty.Create(
        nameof(Text), typeof(string), typeof(SettingsRow), "",
        propertyChanged: (view, _, value) => ((SettingsRow)view).UpdateText((string)value));
    private readonly Label label;
    private readonly Button button;

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public event EventHandler? Clicked;

    public SettingsRow()
    {
        MinimumHeightRequest = 52;
        button = new Button
        {
            BackgroundColor = Colors.Transparent, BorderWidth = 0, CornerRadius = 0,
            Padding = 0, MinimumHeightRequest = 52, HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        var states = new VisualStateGroup { Name = "CommonStates" };
        states.States.Add(new VisualState { Name = "Normal", Setters = { new Setter { Property = Button.BackgroundColorProperty, Value = Colors.Transparent } } });
        states.States.Add(new VisualState { Name = "Pressed", Setters = { new Setter { Property = Button.BackgroundColorProperty, Value = Color.FromArgb("20777777") } } });
        states.States.Add(new VisualState { Name = "PointerOver", Setters = { new Setter { Property = Button.BackgroundColorProperty, Value = Color.FromArgb("10777777") } } });
        states.States.Add(new VisualState { Name = "Focused", Setters = { new Setter { Property = Button.BackgroundColorProperty, Value = Color.FromArgb("20777777") } } });
        states.States.Add(new VisualState { Name = "Disabled" });
        VisualStateManager.SetVisualStateGroups(button, new VisualStateGroupList { states });
        button.Clicked += (_, _) => Clicked?.Invoke(this, EventArgs.Empty);

        label = new Label { FontSize = 17, VerticalOptions = LayoutOptions.Center, LineBreakMode = LineBreakMode.WordWrap };
        label.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("172B46"), Colors.White);
        var chevron = new Label { Text = "›", FontSize = 24, VerticalOptions = LayoutOptions.Center, HorizontalOptions = LayoutOptions.End };
        chevron.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("52647A"), Color.FromArgb("ACACAC"));
        AutomationProperties.SetIsInAccessibleTree(label, false);
        AutomationProperties.SetIsInAccessibleTree(chevron, false);
        var content = new Grid
        {
            Padding = new Thickness(20, 14), ColumnSpacing = 16, InputTransparent = true,
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto) }
        };
        content.Add(label);
        content.Add(chevron, 1);
        var row = new Grid();
        row.Add(button);
        row.Add(content);
        Content = row;
        PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(IsEnabled)) Opacity = IsEnabled ? 1 : .45;
        };
    }

    private void UpdateText(string text)
    {
        label.Text = text;
        SemanticProperties.SetDescription(button, text);
    }
}
