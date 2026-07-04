using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// End-to-end guard for the composer CONTEXT chip. When the user views a satellite (a <c>_Thread</c>),
/// the composer's context must pin the satellite's OWNER (its <c>mainNode</c>) and LABEL the chip by
/// that owner — NEVER the satellite's own name. The live regression this pins: viewing a thread named
/// "hi" showed a context chip reading "hi" (the satellite) instead of its owner partition. Drives the
/// real DevLogin portal built from the working tree (see the <c>playwright</c> skill).
/// </summary>
[Collection("portal-e2e")]
public class ComposerContextChipTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task ComposerContext_OnThreadPage_PinsOwnerMainNode_NotSatelliteName()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        // Seed: the per-user composer node (so the composer area resolves) and a THREAD satellite
        // named "hi" whose owner (mainNode) is the user's partition. All ids use the RESOLVED user
        // node id — a hardcoded 'Roland' seeded a SECOND capital-cased User root (the casing seam).
        var uid = fixture.UserId;
        await SeedAsync(context, token, $$"""
            { "id": "ThreadComposer", "namespace": "{{uid}}/_Thread", "name": "Chat Input",
              "nodeType": "ThreadComposer", "mainNode": "{{uid}}", "content": { "$type": "ThreadComposer" } }
            """);
        await SeedAsync(context, token, $$"""
            { "id": "hi", "namespace": "{{uid}}/_Thread", "name": "hi", "nodeType": "Thread",
              "mainNode": "{{uid}}", "content": { "$type": "Thread" } }
            """);

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);

        // Navigate to the satellite (the thread) — mesh URL shape is {base}/{meshpath}, no /node/ segment.
        await page.GotoAsync($"{fixture.BaseUrl}/{uid}/_Thread/hi",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });

        // The composer rendered — proves the view did NOT disappear on a render storm.
        var chip = page.Locator(".context-chip").First;
        await chip.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 });
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/composer-context-chip.png", FullPage = true });

        // 🎯 The chip pins the OWNER main node, not the satellite. The chip's title attribute IS the
        // resolved context PATH (ReferenceChipCollection: title="@attachment.Path").
        var title = await chip.GetAttributeAsync("title");
        title.Should().Be(uid,
            $"the composer context resolves the satellite ({uid}/_Thread/hi) to its OWNER (mainNode={uid})");

        // 🎯 …and the chip is LABELED by the main node, never the satellite thread's name.
        var chipText = (await chip.Locator(".chip-text").InnerTextAsync()).Trim();
        chipText.Should().NotBe("hi",
            "the chip label comes from the OWNER node (DisplayNameOf), not the navigated satellite 'hi'");

        // The view stays mounted (no storm/teardown after the initial render): give it a beat and
        // re-assert the composer footer is still present.
        await page.WaitForTimeoutAsync(3000);
        (await page.Locator(".thread-chat-footer").CountAsync()).Should().BeGreaterThan(0,
            "the composer must remain rendered — a render storm would have torn the circuit/view down");
    }

    private async Task SeedAsync(IBrowserContext context, string token, string nodeJson)
    {
        try
        {
            await fixture.CreateNodeAsync(context, token, nodeJson);
        }
        catch (InvalidOperationException)
        {
            // Already seeded (persisted from a prior run) — fine.
        }
    }
}
