using System.Xml.Linq;
using ParkingHelper.App.Services;
using Xunit;

namespace ParkingHelper.Persistence.Tests;

public sealed class SectionNavigationTests
{
    [Theory]
    [InlineData("scan")]
    [InlineData("tickets")]
    [InlineData("settings")]
    public async Task PrimaryTapAndReselectionPopNestedStackToRoot(string section)
    {
        var navigation = new SectionRootNavigation();
        var stack = new List<string> { section, "nested", "preview" };
        var active = false;
        Task Pop() { stack.RemoveRange(1, stack.Count - 1); return Task.CompletedTask; }
        Task Active() { Assert.Single(stack); active = true; return Task.CompletedTask; }
        await navigation.OpenAsync(section, Pop, Active);
        Assert.Equal(section, Assert.Single(stack));
        Assert.Equal(section == "tickets", active);
        // Explicit reselection has identical behavior, even if Tickets was Archived at root.
        active = false;
        await navigation.OpenAsync(section, Pop, Active);
        Assert.Equal(section == "tickets", active);
    }

    [Fact]
    public void NativeReselectionHooksAndSectionChangesUseRootPolicyButBackDoesNot()
    {
        var shell = Read("AppShell.xaml.cs");
        Assert.Contains("ShellNavigationSource.ShellSectionChanged", shell);
        Assert.DoesNotContain("ShellNavigationSource.Pop", shell);
        Assert.Contains("rootNavigation.OpenAsync", shell);
        Assert.Contains("OnTabReselected", Read("Platforms/Android/RootShellRenderer.cs"));
        Assert.Contains("shell?.ActivateSection(section)", Read("Platforms/Android/RootShellRenderer.cs"));
        Assert.Contains("selected == SelectedViewController", Read("Services/AppleRootShellRenderer.cs"));
        Assert.Contains("shell.ActivateSection(section)", Read("Services/AppleRootShellRenderer.cs"));
    }

    [Fact]
    public void DiagnosticsIsPermanentUnconditionallyRegisteredAndReadOnly()
    {
        var page = Read("Pages/GoogleDriveDiagnosticsPage.cs");
        Assert.DoesNotContain("temporary", page, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SyncAsync(", page);
        Assert.DoesNotContain("DisconnectAsync(", page);
        Assert.Contains("CaptureDiagnosticLocalAsync", page);
        var document = XDocument.Parse(Read("Pages/SettingsPage.xaml"));
        var row = Assert.Single(document.Descendants(), e => (string?)e.Attribute("AutomationId") == "DriveDiagnosticsRow");
        Assert.Equal("Drive Diagnostics", (string?)row.Attribute("Text"));
        Assert.Null(row.Attribute("IsVisible"));
        Assert.Contains("AddTransient<GoogleDriveDiagnosticsPage>()", Read("MauiProgram.cs"));
        var report = Read("Services/GoogleDriveDiagnosticsReport.cs");
        Assert.DoesNotContain("TEMPORARY", report);
        Assert.DoesNotContain(".BarcodeValue", report);
        Assert.DoesNotContain(".PlateNumber", report);
        Assert.DoesNotContain(".AccessToken", report);
        Assert.DoesNotContain(".RefreshToken", report);
    }

    private static string Read(string path)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "ParkingHelper.App"))) directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "ParkingHelper.App", path));
    }
}
