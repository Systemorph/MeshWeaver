using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// The space "⋯" Node menu (Cube icon, <c>#node-menu-anchor</c>) must be driven by the routed
/// page and show the standard per-node operations — headed by the node's own name. This guards
/// the fix that decoupled "drives the header menu" from <c>Top</c>: only the routed primary page
/// (ApplicationPage / AreaPage) drives the menu now, so a wiring slip (e.g. forgetting
/// <c>DrivesMenu</c> on the page) would leave the Node menu EMPTY — which this test catches.
/// The dropdown is populated per-circuit from <c>$Menu:Node</c>; with a healthy grant the viewer
/// has Delete, so "Delete" must be present and the header must be the Space's name (not a stale
/// previewed node's).
/// </summary>
[Collection("portal-e2e")]
public class NodeMenuE2ETest(PortalFixture fixture)
{
    private const string Space = "menue2e";

    [Fact(Timeout = 180_000)]
    public async Task SpaceNodeMenu_ShowsDelete_HeadedByTheSpaceName()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        try
        {
            try
            {
                await fixture.CreateNodeAsync(context, token, $$"""
                    {
                      "id": "{{Space}}",
                      "name": "Menu E2E",
                      "nodeType": "Space",
                      "content": { "$type": "Space", "name": "Menu E2E" }
                    }
                    """);
            }
            catch (InvalidOperationException) { /* persisted from a prior run */ }

            // A token-created Space grants the token identity (lowercase partition key); the browser
            // circuit authenticates as the DevLogin ObjectId. Grant the circuit identity so it has
            // Delete on the Space (the whole point of the assertion) — same shape as the sync E2E.
            try
            {
                await fixture.CreateNodeAsync(context, token, $$"""
                    {
                      "id": "{{fixture.UserId}}_CircuitAccess",
                      "namespace": "{{Space}}/_Access",
                      "name": "{{fixture.UserId}} Access",
                      "nodeType": "AccessAssignment",
                      "mainNode": "{{Space}}",
                      "content": {
                        "$type": "AccessAssignment",
                        "accessObject": "{{fixture.UserId}}",
                        "displayName": "{{fixture.UserId}}",
                        "roles": [ { "$type": "RoleAssignment", "role": "Admin" } ]
                      }
                    }
                    """);
            }
            catch (InvalidOperationException) { }

            (await fixture.WaitUntilReadableAsync(context, token, Space, TimeSpan.FromSeconds(60)))
                .Should().BeTrue("the seeded space must be readable before driving the UI");

            var page = await context.NewPageAsync();
            await page.SetViewportSizeAsync(1400, 1000);

            // The creator-admin grant on a fresh space propagates eventually; retry the load so a
            // circuit that subscribed before the grant landed re-subscribes (same rule as the sync E2E).
            var menuOk = false;
            for (var attempt = 0; attempt < 8 && !menuOk; attempt++)
            {
                await page.GotoAsync($"{fixture.BaseUrl}/{Space}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });
                var button = page.Locator("#node-menu-anchor, [aria-label='Node menu']").First;
                try
                {
                    await button.WaitForAsync(
                        new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
                    await button.ClickAsync();
                    // The routed page drives $Menu:Node → Delete must be an item.
                    await page.GetByText("Delete", new() { Exact = false }).First.WaitForAsync(
                        new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
                    menuOk = true;
                }
                catch (TimeoutException) { /* grant not propagated yet — reload */ }
                catch (PlaywrightException) { }
            }

            menuOk.Should().BeTrue(
                "the Space's Node menu must be driven by the routed page and contain Delete "
                + "(an empty menu means the routed page stopped driving $Menu:Node)");

            // The menu is headed by the Space's OWN name — not a stale previewed node's.
            await page.GetByText("Menu E2E", new() { Exact = false }).First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });

            await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/node-menu.png" });
        }
        finally
        {
            await fixture.DeleteNodeAsync(context, token, Space);
        }
    }
}
