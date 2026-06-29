using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Verifies the platform skill catalog is DISCOVERABLE: under the MeshWeaver harness, typing <c>/</c>
/// surfaces the <c>nodeType:Skill</c> catalog in the editor's suggest widget (the built-in <c>/agent</c>,
/// <c>/model</c>, <c>/harness</c> skills at minimum). This is the read-side proof that the <c>Skill</c>
/// partition is readable — i.e. its PublicRead <c>_Policy</c> resolves (the
/// <c>PartitionAccessPolicy</c> <c>$type</c> registration). If the policy can't type-resolve, the
/// partition reads empty and skills vanish — the same path the co-hosted Claude Code / Copilot harnesses
/// use to find skills (via the <c>meshweaver</c> MCP over the same partition). Drives the real DevLogin
/// portal: <c>memex-local e2e test SkillCatalogDiscoverableTest</c>.
/// </summary>
[Collection("portal-e2e")]
public class SkillCatalogDiscoverableTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task MeshWeaverHarness_SlashMenu_SurfacesSkillCatalog()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        // Pin the MeshWeaver harness — under a CLI harness the "/" menu shows the harness's own commands,
        // not the skill catalog; the catalog surfaces under MeshWeaver.
        var composerPath = $"{fixture.UserId}/_Thread/ThreadComposer";
        await SeedAsync(context, token, $$"""
            { "id": "ThreadComposer", "namespace": "{{fixture.UserId}}/_Thread", "name": "Chat Input",
              "nodeType": "ThreadComposer", "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "ThreadComposer", "harness": "MeshWeaver" } }
            """);
        await fixture.PatchNodeAsync(context, token, composerPath, "{\"content\":{\"harness\":\"MeshWeaver\"}}");

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);
        await OpenComposerOnAsync(page, $"{fixture.BaseUrl}/{fixture.UserId}");

        var harnessChip = page.Locator(".thread-chat-status-item[title^='Harness']");
        (await PollAsync(async () =>
                await harnessChip.CountAsync() > 0
                && (await harnessChip.First.InnerTextAsync()).Contains("MeshWeaver", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the composer must bind to the MeshWeaver harness so the / menu surfaces skills");

        // Type "/" to open the slash-command suggest (the nodeType:Skill catalog).
        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await page.Keyboard.PressAsync("Escape");
        await editor.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.TypeAsync("/");

        // The Monaco suggest widget must list skill rows. Poll the combined text for the built-in skills.
        var rows = page.Locator(".suggest-widget.visible .monaco-list-row");
        string combined = "";
        var found = await PollAsync(async () =>
        {
            if (await rows.CountAsync() == 0) return false;
            combined = (await rows.AllInnerTextsAsync()) is { } texts ? string.Join(" | ", texts).ToLowerInvariant() : "";
            return combined.Contains("agent") && combined.Contains("model");
        }, TimeSpan.FromSeconds(20));

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/skill-catalog.png", FullPage = true });

        found.Should().BeTrue(
            "typing / under the MeshWeaver harness must surface the nodeType:Skill catalog (at least /agent + /model) — " +
            "an empty menu means the Skill partition is unreadable (PartitionAccessPolicy did not type-resolve). " +
            "Saw rows: " + combined);
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static async Task OpenComposerOnAsync(IPage page, string url)
    {
        await page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });
        page.Url.Should().NotContain("/login");
        var toggle = page.Locator(".side-panel-toggle button, .side-panel-toggle fluent-button").First;
        await toggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await toggle.ClickAsync();
        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await page.WaitForTimeoutAsync(800);
    }

    private static async Task<bool> PollAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return true;
            await Task.Delay(300);
        }
        return false;
    }

    private async Task SeedAsync(IBrowserContext context, string token, string nodeJson)
    {
        try { await fixture.CreateNodeAsync(context, token, nodeJson); }
        catch (InvalidOperationException) { /* already seeded — fine */ }
    }
}
