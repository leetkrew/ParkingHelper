using Microsoft.Maui.Controls.Handlers.Compatibility;
using Microsoft.Maui.Controls.Platform.Compatibility;

namespace ParkingHelper.App.Platforms;

public sealed class RootShellRenderer(Android.Content.Context context) : ShellRenderer(context)
{
    protected override IShellItemRenderer CreateShellItemRenderer(ShellItem item) => new RootItemRenderer(this);

    private sealed class RootItemRenderer : ShellItemRenderer
    {
        private readonly AppShell? shell;
        public RootItemRenderer(IShellContext context) : base(context) => shell = context.Shell as AppShell;
        protected override void OnTabReselected(ShellSection section)
        {
            shell?.ActivateSection(section);
        }
    }
}
