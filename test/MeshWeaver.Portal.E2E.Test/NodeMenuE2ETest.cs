using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// The node menu must be driven by the routed page and reachable from it. Two ways in exist during
/// the transition, and the test accepts either: the header's labelled <b>⋯ More</b> dropdown beside
/// the page title (<c>.node-actions-more</c>, MeshNodeLayoutAreas.MoreActionsClass — the primary
/// entry point), and the legacy top-bar cube (<c>#node-menu-anchor</c>), which
/// MeshWeaver.Plugins removes once the header menu reaches a sealed platform. Originally:
/// the space "⋯" Node menu (Cube icon, <c>#node-menu-anchor</c>) must be driven by the routed
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

    /// <summary>The header ⋯ More trigger's stable class (MeshNodeLayoutAreas.MoreActionsClass).</summary>
    private const string HeaderMoreClass = "node-actions-more";

    /// <summary>Either way into the node menu — the header ⋯ More first, the legacy top-bar cube second.</summary>
    private const string NodeMenuTrigger =
        "." + HeaderMoreClass + ", #node-menu-anchor, [aria-label='Node menu']";

    /// <summary>
    /// The Delete entry, in whichever dropdown opened: a role=menuitem in the top-bar menu, or a
    /// <c>.node-actions-item</c> button in the header ⋯ (MeshNodeLayoutAreas.MoreActionsItemClass) —
    /// scoped to a menu ENTRY either way, never to page-body text.
    /// </summary>
    private static ILocator DeleteEntry(IPage page)
        => page.GetByRole(AriaRole.Menuitem, new() { Name = "Delete", Exact = false })
            .Or(page.Locator(".node-actions-item", new() { HasText = "Delete" }));

    [Fact(Timeout = 180_000)]
    public async Task SpaceNodeMenu_ShowsDelete_HeadedByTheSpaceName()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync(cancellationToken: TestContext.Current.CancellationToken);
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

            (await fixture.WaitUntilReadableAsync(context, token, Space, TimeSpan.FromSeconds(60), cancellationToken: TestContext.Current.CancellationToken))
                .Should().BeTrue("the seeded space must be readable before driving the UI");

            var page = await context.NewPageAsync();
            await page.SetViewportSizeAsync(1400, 1000);

            // The creator-admin grant on a fresh space propagates eventually; retry the load so a
            // circuit that subscribed before the grant landed re-subscribes (same rule as the sync E2E).
            var menuOk = false;
            var usedHeaderMenu = false;
            for (var attempt = 0; attempt < 8 && !menuOk; attempt++)
            {
                await page.GotoAsync($"{fixture.BaseUrl}/{Space}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });
                var button = page.Locator(NodeMenuTrigger).First;
                try
                {
                    await button.WaitForAsync(
                        new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
                    await button.ClickAsync();
                    // Scoped to a MENU ITEM (role=menuitem) — not page-body text — so this only passes
                    // when Delete is genuinely IN the Node dropdown that the routed page drives.
                    usedHeaderMenu = await button.EvaluateAsync<bool>(
                        "el => el.closest('." + HeaderMoreClass + "') !== null || el.classList.contains('" + HeaderMoreClass + "')");
                    await DeleteEntry(page).First
                        .WaitForAsync(
                            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
                    menuOk = true;
                }
                catch (TimeoutException) { /* grant not propagated yet — reload */ }
                catch (PlaywrightException) { }
            }

            menuOk.Should().BeTrue(
                "the Space's Node menu must be driven by the routed page and contain a Delete menu item "
                + "(an empty menu means the routed page stopped driving $Menu:Node)");

            if (usedHeaderMenu)
            {
                // The header ⋯ sits beside the node's OWN title, so the page heading is the check
                // that the menu belongs to this Space rather than a stale previewed node.
                await page.Locator("h1").GetByText("Menu E2E", new() { Exact = false }).First.WaitForAsync(
                    new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
            }
            else
            {
                // The top-bar dropdown that holds Delete is headed by the Space's OWN name — scoped to
                // THAT <fluent-menu> (not the page's <h1>), so a stale previewed-node header would fail here.
                var nodeMenu = page.Locator("fluent-menu").Filter(new()
                {
                    Has = page.GetByRole(AriaRole.Menuitem, new() { Name = "Delete", Exact = false }),
                });
                await nodeMenu.GetByText("Menu E2E", new() { Exact = false }).First.WaitForAsync(
                    new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
            }

            await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/node-menu.png" });
        }
        finally
        {
            await fixture.DeleteNodeAsync(context, token, Space);
        }
    }
}
