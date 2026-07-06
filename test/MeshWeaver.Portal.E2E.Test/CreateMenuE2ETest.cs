using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// The node <b>Create</b> flow must create the node and land on its Edit area — it must NOT show
/// "Area not found — No renderer is registered for area …".
/// <para>Regression guard for the transient-create bug: the old flow wrote a <c>Transient</c>
/// MeshNode and then navigated to <c>/{path}/Create</c>. On Postgres, path-resolution only returns
/// <c>state = 2</c> (Active) rows, so the just-written transient node was invisible — the router
/// treated the node id as an <i>area name</i> on the parent hub and rendered
/// "No renderer is registered for area {id} on hub {parent}". This is a REAL portal (PG-backed)
/// running the working tree, so it reproduces exactly what the browser hit.</para>
/// <para>The fix writes ONE <c>Active</c> node via <c>CreateNode</c> and navigates to
/// <c>/{path}/Edit</c>, whose owning hub knows the content type and renders the editor.</para>
/// </summary>
[Collection("portal-e2e")]
public class CreateMenuE2ETest(PortalFixture fixture)
{
    [Theory(Timeout = 180_000)]
    [InlineData("Markdown", "TomNote", "TomNote")]
    [InlineData("Agent", "Tom", "Tom")]
    public async Task Create_LandsOnEditArea_NotAreaNotFound(string nodeType, string nodeName, string expectedId)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var space = $"createe2e{nodeType.ToLowerInvariant()}";

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        try
        {
            // Seed a Space to create under (idempotent across reruns).
            try
            {
                await fixture.CreateNodeAsync(context, token, $$"""
                    {
                      "id": "{{space}}",
                      "name": "Create E2E",
                      "nodeType": "Space",
                      "content": { "$type": "Space", "name": "Create E2E" }
                    }
                    """);
            }
            catch (InvalidOperationException) { /* persisted from a prior run */ }

            // The browser circuit authenticates as the DevLogin ObjectId; grant it Admin on the
            // Space so it has Create there (same shape as the Node-menu / sync E2E).
            try
            {
                await fixture.CreateNodeAsync(context, token, $$"""
                    {
                      "id": "{{fixture.UserId}}_CircuitAccess",
                      "namespace": "{{space}}/_Access",
                      "name": "{{fixture.UserId}} Access",
                      "nodeType": "AccessAssignment",
                      "mainNode": "{{space}}",
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

            (await fixture.WaitUntilReadableAsync(context, token, space, TimeSpan.FromSeconds(60)))
                .Should().BeTrue("the seeded space must be readable before driving the UI");

            var page = await context.NewPageAsync();
            await page.SetViewportSizeAsync(1400, 1000);

            // Drive the Create form directly with the type preset (the "+"/Create menu item lands
            // here with ?type=). The creator-admin grant on a fresh space propagates eventually, so
            // retry the whole create round until the form is reachable and the create succeeds.
            var landedOnEdit = false;
            string? lastUrl = null;
            for (var attempt = 0; attempt < 8 && !landedOnEdit; attempt++)
            {
                await page.GotoAsync($"{fixture.BaseUrl}/{space}/Create?type={nodeType}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });

                var nameField = page.GetByPlaceholder("Enter a name...").First;
                try
                {
                    await nameField.WaitForAsync(
                        new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
                }
                catch (TimeoutException) { continue; /* grant not propagated — reload */ }

                // The Name field is a <fluent-text-field> web component (its inner <input> lives in
                // the shadow DOM), so Fill on the host fails — click to focus, then type.
                await nameField.ClickAsync();
                await page.Keyboard.TypeAsync(nodeName);

                await page.GetByRole(AriaRole.Button, new() { Name = "Create", Exact = true }).First
                    .ClickAsync();

                try
                {
                    // The fix navigates to /{space}/{id}/Edit. Wait for that URL specifically.
                    await page.WaitForURLAsync($"**/{space}/{expectedId}/Edit", new() { Timeout = 20_000 });
                    landedOnEdit = true;
                }
                catch (TimeoutException)
                {
                    lastUrl = page.Url;
                    // Not on Edit — could be a propagation retry, or the bug (stuck on /Create with
                    // an "Area not found"). Loop unless the page shows the regression signature.
                }

                // Hard failure the moment the regression text appears — never silently retry past it.
                var bodyText = await page.InnerTextAsync("body");
                bodyText.Should().NotContain("No renderer is registered for area",
                    $"creating a {nodeType} must not render the area-not-found page (transient-create regression). URL={page.Url}");
                bodyText.Should().NotContain("Area not found",
                    $"creating a {nodeType} must land on the node's Edit area, not 'Area not found'. URL={page.Url}");
            }

            landedOnEdit.Should().BeTrue(
                $"creating a {nodeType} named '{nodeName}' must land on /{space}/{expectedId}/Edit; last URL was {lastUrl}");

            // The created node must actually exist (Active) — the Edit URL alone could be reached
            // with a broken node, so confirm via the read API the browser's grant can see.
            (await fixture.WaitUntilReadableAsync(context, token, $"{space}/{expectedId}", TimeSpan.FromSeconds(30)))
                .Should().BeTrue($"the created node {space}/{expectedId} must be persisted and readable");

            await page.ScreenshotAsync(new PageScreenshotOptions { Path = $"/tmp/create-{nodeType}.png" });
        }
        finally
        {
            await fixture.DeleteNodeAsync(context, token, $"{space}/{expectedId}");
            await fixture.DeleteNodeAsync(context, token, space);
        }
    }
}
