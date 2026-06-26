using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Visual verification that the home / activity-dashboard chat composer renders full width — i.e. the
/// <c>FluentTextArea</c> fills its container after the TextAreaView + composer width fixes.
/// </summary>
[Collection("portal-e2e")]
public class HomeComposerVerifyTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task Home_ChatComposer_RendersFullWidth()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();

        // The per-user ThreadComposer node is normally seeded at onboarding; a fresh monolith has none,
        // so the composer area would error ("No node found at Roland/_Thread/ThreadComposer"). Seed it.
        var token = await fixture.MintTokenAsync(context);
        try
        {
            await fixture.CreateNodeAsync(context, token, """
                {
                  "id": "ThreadComposer",
                  "namespace": "Roland/_Thread",
                  "name": "Chat Input",
                  "nodeType": "ThreadComposer",
                  "mainNode": "Roland",
                  "content": { "$type": "ThreadComposer" }
                }
                """);
        }
        catch (InvalidOperationException)
        {
            // Already seeded (persisted from a prior run) — fine.
        }

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 900);
        await page.GotoAsync($"{fixture.BaseUrl}/User/Roland",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // The composer's message editor (a FluentTextArea) is the last text area on the page.
        var textarea = page.Locator("fluent-text-area, textarea").Last;
        await textarea.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 });
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/home-page.png", FullPage = true });

        var taBox = await textarea.BoundingBoxAsync();
        var pageWidth = await page.EvaluateAsync<float>(
            "() => (document.querySelector('.body-content, main, body')).clientWidth");
        taBox.Should().NotBeNull();
        (taBox!.Width / pageWidth).Should().BeGreaterThan(0.8f,
            $"the chat composer text area should fill its width (textarea={taBox.Width:F0}px of {pageWidth:F0}px)");
    }
}
