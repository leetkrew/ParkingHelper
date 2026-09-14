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
