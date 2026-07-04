using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// E2E: navigating to an INSTANCE of a NodeType whose source does NOT compile must
/// serve the emergency compile-error PAGE — a real "this page can't be displayed,
/// here's the compiler error, please correct the code" page — never a blank or an
/// indefinite spinner (the 2026-06-26 atioz wedge). Proves the nice error page
/// actually renders in a real browser.
///
/// <para>Gated like the rest of the suite: set <c>E2E_BASE_URL</c> (or
/// <c>E2E_LAUNCH=1</c>) to run; otherwise it Skips.</para>
/// </summary>
[Collection("portal-e2e")]
public class CompileErrorPageE2ETest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task BrokenNodeType_Instance_ServesNiceCompileErrorPage()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        // Unique ids so reruns against a persisted portal don't collide.
        var partition = fixture.UserId;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var nodeTypeId = $"BrokenE2EType{suffix}";
        var instanceId = $"broken-e2e-instance-{suffix}";

        // 1. A NodeType whose Configuration is not valid C# — the kickoff compile
        //    fails and CompilationStatus settles at Error.
        await fixture.CreateNodeAsync(context, token, $$"""
            {
              "id": "{{nodeTypeId}}",
              "namespace": "{{partition}}",
              "name": "Broken E2E Type",
              "nodeType": "NodeType",
              "content": {
                "$type": "NodeTypeDefinition",
                "configuration": "config => this is not valid C# at all ((await ("
              }
            }
            """);

        // 2. An instance of the broken type — its page must serve the emergency overlay.
        await fixture.CreateNodeAsync(context, token, $$"""
            {
              "id": "{{instanceId}}",
              "namespace": "{{partition}}",
              "name": "Broken E2E Instance",
              "nodeType": "{{partition}}/{{nodeTypeId}}",
              "state": "Active"
            }
            """);

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1280, 900);
        await page.GotoAsync($"{fixture.BaseUrl}/{partition}/{instanceId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // 3. THE CONTRACT: the nice error page COMES BACK with its plain-language
        //    headline. Cold Roslyn compile can take a while, so wait generously — but
        //    it MUST resolve (a timeout here = the wedge: the page never came back).
        var headline = page.GetByText("This page can't be displayed");
        await headline.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible,
            Timeout = 120_000
        });
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = "/tmp/compile-error-page.png",
            FullPage = true
        });

        // 4. The page must NAME the failure class and tell the author how to recover.
        var bodyText = await page.Locator("body").InnerTextAsync();
        bodyText.Should().Contain("compilation error",
            "the page names the failure class so the user knows it's their code, not an outage");
        bodyText.Should().Contain("Please correct the code",
            "the page gives a clear call to action to fix the source");
    }
}
