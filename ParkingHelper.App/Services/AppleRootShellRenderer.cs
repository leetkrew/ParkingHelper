#if IOS || MACCATALYST
using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform.Compatibility;

namespace ParkingHelper.App.Platforms;

public sealed class RootShellRenderer : ShellRenderer
{
    protected override IShellItemRenderer CreateShellItemRenderer(ShellItem item) => new RootItemRenderer(this) { ShellItem = item };

    private sealed class RootItemRenderer : ShellItemRenderer
    {
        private readonly AppShell? shell;
        public RootItemRenderer(IShellContext context) : base(context) => shell = context.Shell as AppShell;
#if IOS
        private double selectionWidth;
        private UIKit.UIUserInterfaceStyle selectionTheme;

        public override void ViewDidLayoutSubviews()
        {
            base.ViewDidLayoutSubviews();
            var count = TabBar.Items?.Length ?? 0;
            if (count == 0 || TabBar.Bounds.Width <= 0) return;
            var width = Math.Min(112, TabBar.Bounds.Width / count - 16);
            var theme = TraitCollection.UserInterfaceStyle;
            if (width == selectionWidth && theme == selectionTheme) return;
            selectionWidth = width;
            selectionTheme = theme;
            var dark = theme == UIKit.UIUserInterfaceStyle.Dark;
            var appearance = new UIKit.UITabBarAppearance();
            appearance.ConfigureWithOpaqueBackground();
            appearance.BackgroundColor = dark
                ? UIKit.UIColor.FromRGB(27, 43, 64) : UIKit.UIColor.White;
            appearance.ShadowColor = UIKit.UIColor.Clear;
            var muted = dark ? UIKit.UIColor.FromRGB(184, 200, 222) : UIKit.UIColor.FromRGB(82, 100, 122);
            var selected = dark ? UIKit.UIColor.FromRGB(167, 204, 255) : UIKit.UIColor.FromRGB(8, 102, 230);
            foreach (var item in new[] { appearance.StackedLayoutAppearance, appearance.InlineLayoutAppearance, appearance.CompactInlineLayoutAppearance })
            {
                item.Normal.IconColor = muted;
                item.Normal.TitleTextAttributes = new UIKit.UIStringAttributes { ForegroundColor = muted };
                item.Selected.IconColor = selected;
                item.Selected.TitleTextAttributes = new UIKit.UIStringAttributes { ForegroundColor = selected };
            }
            TabBar.StandardAppearance = appearance;
            TabBar.ScrollEdgeAppearance = appearance;
            using var renderer = new UIKit.UIGraphicsImageRenderer(new CoreGraphics.CGSize(width, 48));
            TabBar.SelectionIndicatorImage = renderer.CreateImage(_ =>
            {
                (dark ? UIKit.UIColor.FromRGB(37, 62, 95) : UIKit.UIColor.FromRGB(234, 242, 255)).SetFill();
                using var pill = UIKit.UIBezierPath.FromRoundedRect(new CoreGraphics.CGRect(0, 0, width, 48), 24);
                pill.Fill();
            });
        }
#endif

        public override void ViewDidLoad()
        {
            base.ViewDidLoad();
            var original = ShouldSelectViewController;
            ShouldSelectViewController = (controller, selected) =>
            {
                var accepted = original?.Invoke(controller, selected) ?? true;
                if (accepted && selected == SelectedViewController && ShellItem.CurrentItem is { } section
                    && shell is not null)
                {
                    shell.ActivateSection(section);
                    return false; // Let MAUI pop once, keeping the managed Back stack consistent.
                }
                return accepted;
            };
        }
    }
}
#endif
