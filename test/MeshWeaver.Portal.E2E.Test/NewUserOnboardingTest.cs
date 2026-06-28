using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// New-user onboarding: signing in as a never-seen DevLogin id self-provisions the user (their
/// partition + <c>_Access</c> grant + <c>User</c> node), and navigating to their home renders the
/// default configurable welcome page — proving a brand-new user lands on a working, owned home, not an
/// error, the visitor profile, or a bounce to <c>/login</c>.
/// </summary>
[Collection("portal-e2e")]
public class NewUserOnboardingTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task NewUser_SignIn_LandsOnConfigurableWelcomeHome()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        // A unique id so this is a genuine first-time onboarding even on a reused e2e portal/db.
        var newUser = "e2eonb" + System.Guid.NewGuid().ToString("N")[..8];

        // Signing in self-provisions the unknown user; the fixture throws if onboarding fails (4xx, or a
        // persistent 5xx after the self-provisioning retries), so reaching here means onboarding worked.
        await using var context = await fixture.NewAuthenticatedContextAsync(newUser);
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 900);

        await page.GotoAsync($"{fixture.BaseUrl}/User/{newUser}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // The freshly-onboarded user has an empty Body, so they see the default welcome — as the OWNER
        // (the visitor profile has no "Welcome back" banner).
        await page.GetByText("Welcome back", new() { Exact = false }).First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 });
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/onboarding-home.png", FullPage = true });

        page.Url.Should().NotContain("/login", "a freshly onboarded user must not be bounced to login");
        (await page.Locator("a[href*='ConfigurablePages']").CountAsync()).Should().BeGreaterThan(0,
            "the new user's default home shows the 'it's configurable' note linking to the guide");
    }
}
