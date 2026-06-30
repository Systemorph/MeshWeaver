using System.Net.Sockets;
using OpenQA.Selenium.Appium;
using OpenQA.Selenium.Appium.Mac;
using Xunit;

namespace MeshWeaver.Maui.E2E.Test;

/// <summary>
/// Native end-to-end smoke for the maccatalyst app, driven through Appium's Mac2 driver — the
/// Playwright-equivalent for native MAUI. Verifies the real app launches and the shell renders (the
/// search box is present + visible). SKIPS itself when no Appium server is reachable, so a normal CI run
/// is unaffected; it executes only with the local Appium stack up (see csproj.disabled-note.md).
/// </summary>
public class AppLaunchSmokeTest
{
    private const string AppiumUrl = "http://127.0.0.1:4723";
    private const string BundleId = "com.companyname.memex.client";

    [Fact]
    public void App_Launches_And_ShellSearchBox_IsPresent()
    {
        Assert.SkipUnless(AppiumReachable(),
            $"Appium server not reachable at {AppiumUrl} — start `appium` + grant Accessibility (see csproj.disabled-note.md).");

        var options = new AppiumOptions { PlatformName = "Mac", AutomationName = "Mac2" };
        options.AddAdditionalAppiumOption("appPath", AppPath());
        options.AddAdditionalAppiumOption("bundleId", BundleId);

        // Generous server-init timeout: the first Mac2 session builds/launches WebDriverAgentMac.
        using var driver = new MacDriver(new Uri(AppiumUrl), options, TimeSpan.FromSeconds(180));
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.FromSeconds(20);

        // AccessibilityId maps to the MAUI AutomationId set on the shell's search box.
        var search = driver.FindElement(MobileBy.AccessibilityId("mesh-search"));
        search.Should().NotBeNull();
        search.Displayed.Should().BeTrue();
    }

    private static string AppPath() =>
        Environment.GetEnvironmentVariable("MEMEX_APP_PATH")
        ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../memex/Memex.Client/bin/Debug/net10.0-maccatalyst/maccatalyst-arm64/Memex.Client.app"));

    private static bool AppiumReachable()
    {
        // APM BeginConnect + WaitOne — a non-Task reachability probe (no blocking Task.Wait → no xUnit1031).
        try
        {
            using var client = new TcpClient();
            var ar = client.BeginConnect("127.0.0.1", 4723, null, null);
            return ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1)) && client.Connected;
        }
        catch { return false; }
    }
}
