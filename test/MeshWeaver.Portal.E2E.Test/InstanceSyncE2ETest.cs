using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// End-to-end instance sync through the REAL portal: a Space is synced to "another instance"
/// which is the portal ITSELF (pod-internal loopback URL + a freshly minted ApiToken), so the
/// whole chain runs live — the Instance Sync settings tab, the sync worker, the MCP-over-HTTP
/// remote client (Bearer auth, search envelope), initial replication, and cancel:
/// <list type="number">
///   <item>add a syncing party on the Space's Settings → Instance Sync tab (the UI form);</item>
///   <item>configure the connection (loopback URL, minted token, a different target space);</item>
///   <item>the initial replication materializes the target space + child on the "remote";</item>
///   <item>the party card shows the live Syncing status;</item>
///   <item>Remove (cancel) deletes the registration and the list empties.</item>
/// </list>
/// </summary>
[Collection("portal-e2e")]
public class InstanceSyncE2ETest(PortalFixture fixture)
{
    // The worker runs inside the portal pod; the "remote" must be reachable from THERE —
    // the pod's own Kestrel endpoint, not the host-side ingress port-forward.
    private const string LoopbackUrl = "http://localhost:8080";

    private const string Space = "synce2e";
    private const string MirrorSpace = "synce2e-mirror";

    [Fact(Timeout = 300_000)]
    public async Task Space_syncs_to_remote_instance_and_cancel_stops_it()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        try
        {
            // ── Seed: a Space with one content node ────────────────────────────
            try
            {
                await fixture.CreateNodeAsync(context, token, $$"""
                    {
                      "id": "{{Space}}",
                      "name": "Sync E2E",
                      "nodeType": "Space",
                      "content": { "$type": "Space", "name": "Sync E2E" }
                    }
                    """);
            }
            catch (InvalidOperationException)
            {
                // Persisted from a prior run — fine.
            }
            // A token-created Space grants its creator under the TOKEN identity (the lowercase
            // partition key, e.g. "roland"), while the browser circuit authenticates as the
            // DevLogin ObjectId (e.g. "Roland") — so the circuit would be denied on the fresh
            // space. Grant the circuit identity explicitly (the token user is the space admin
            // and may write _Access), mirroring what the UI's share flow would do.
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
            catch (InvalidOperationException)
            {
            }
            try
            {
                await fixture.CreateNodeAsync(context, token, $$"""
                    {
                      "id": "hello",
                      "namespace": "{{Space}}",
                      "name": "Hello",
                      "nodeType": "Markdown",
                      "content": { "$type": "MarkdownContent", "content": "sync me across instances" }
                    }
                    """);
            }
            catch (InvalidOperationException)
            {
            }
            (await fixture.WaitUntilReadableAsync(context, token, $"{Space}/hello", TimeSpan.FromSeconds(60)))
                .Should().BeTrue("the seeded space must be readable before driving the UI");

            var page = await context.NewPageAsync();
            await page.SetViewportSizeAsync(1600, 1000);

            // ── The Instance Sync tab on the Space's settings page ─────────────
            // The creator-admin grant on a fresh space propagates eventually; a circuit that
            // subscribes before it lands is denied and keeps last-good display without
            // retrying — so retry the navigation (each reload is a fresh subscription), the
            // same "wait for the grant BEFORE driving the UI" rule the other E2E tests follow.
            var tabVisible = false;
            for (var attempt = 0; attempt < 8 && !tabVisible; attempt++)
            {
                await page.GotoAsync($"{fixture.BaseUrl}/{Space}/Settings/InstanceSync",
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });
                try
                {
                    await page.GetByText("Instance Sync").First.WaitForAsync(
                        new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
                    tabVisible = true;
                }
                catch (TimeoutException)
                {
                    // grant not propagated to the circuit yet — reload
                }
                catch (PlaywrightException)
                {
                    // grant not propagated to the circuit yet — reload
                }
            }
            tabVisible.Should().BeTrue("the Instance Sync tab must render once the creator grant propagated");
            await page.GetByText("No syncing parties yet", new() { Exact = false }).First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });

            // ── Add a syncing party through the UI (one-click, auto-named party-1) ──
            await page.GetByText("Add syncing party", new() { Exact = true }).ClickAsync();

            // The parties list live-binds — the new card appears with its editor.
            await page.GetByText("not configured", new() { Exact = false }).First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });

            // ── Configure the connection (merge patch onto the registry node) ──
            await fixture.PatchNodeAsync(context, token, $"{Space}/_Sync/party-1", $$"""
                {
                  "content": {
                    "remoteUrl": "{{LoopbackUrl}}",
                    "remoteToken": "{{token}}",
                    "remoteSpace": "{{MirrorSpace}}"
                  }
                }
                """);

            // ── The initial replication runs through the REAL loopback MCP ─────
            (await fixture.WaitUntilReadableAsync(context, token, MirrorSpace, TimeSpan.FromMinutes(2)))
                .Should().BeTrue("initial replication must create the target space on the 'remote'");
            (await fixture.WaitUntilReadableAsync(context, token, $"{MirrorSpace}/hello", TimeSpan.FromMinutes(2)))
                .Should().BeTrue("initial replication must copy the space's content nodes");

            // The party card reflects the live status (WatchConfigNodes re-renders the list).
            await page.GetByText("Status:", new() { Exact = false }).First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
            await page.GetByText("Syncing", new() { Exact = false }).First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 120_000 });
            await page.ScreenshotAsync(new PageScreenshotOptions
                { Path = "/tmp/instance-sync-syncing.png", FullPage = true });

            // ── Cancel: Remove stops the sync and empties the list ─────────────
            await page.GetByText("Remove (stop syncing)", new() { Exact = false }).First.ClickAsync();
            await page.GetByText("No syncing parties yet", new() { Exact = false }).First.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
            await page.ScreenshotAsync(new PageScreenshotOptions
                { Path = "/tmp/instance-sync-cancelled.png", FullPage = true });
        }
        finally
        {
            // Best-effort cleanup so reruns start clean.
            await fixture.DeleteNodeAsync(context, token, $"{Space}/_Sync/party-1");
            await fixture.DeleteNodeAsync(context, token, MirrorSpace);
            await fixture.DeleteNodeAsync(context, token, Space);
        }
    }
}
