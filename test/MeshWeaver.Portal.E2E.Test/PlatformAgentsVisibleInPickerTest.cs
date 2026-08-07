using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// THE REPORTED OUTAGE (#902), asserted the way it was reported: <i>"no platform agents are
/// available from the portal"</i>. Every other test on this fix is server-side — it proves the
/// registry query returns rows, or that the partition is readable. None of that is what the user
/// saw. A projection can return rows while the picker still renders empty, which is precisely the
/// failure this test exists to make impossible: it drives the real browser, opens the real
/// composer, and asserts on the <b>rendered DOM of the agent picker</b>.
///
/// <para>Two identities, because the regression was about who could see them: the installing
/// admin, and a SECOND signed-in person who installed nothing and holds no grant anywhere. The
/// platform's agents are published read-only to everyone BY THE INSTALLER
/// (<c>PackageInstaller.EnsurePreInstalledPublicRead</c>), so the second person must see exactly
/// the same catalog. If the partition's <c>_Policy</c> is missing or unreadable, that user's
/// picker is the one that comes up empty.</para>
///
/// <para>🚨 Asserts at least one agent BY NAME. An "the picker has rows" assertion passes on a
/// picker showing only the user's own agents — the platform catalog could still be gone. Run:
/// <c>memex-local e2e up</c> → <c>memex-local e2e test PlatformAgentsVisibleInPickerTest</c>.</para>
/// </summary>
[Collection("portal-e2e")]
public class PlatformAgentsVisibleInPickerTest(PortalFixture fixture)
{
    /// <summary>
    /// The agent the platform always ships — the general Assistant, id <c>Assistant</c>, authored
    /// at <c>content/ai/Agent/Assistant.md</c> and installed into the <c>Agent</c> partition by the
    /// default install. Naming it is the point: it cannot be satisfied by a user's own agents.
    /// </summary>
    private const string ShippedAgent = "assistant";

    [Fact(Timeout = 240_000)]
    public async Task AgentPicker_ListsThePlatformAgents_ForTheInstallerAndForAPlainUser()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        // The installing admin sees them …
        await AssertAgentPickerListsPlatformAgentsAsync(personId: null);

        // … and so does a DIFFERENT signed-in person, who installed nothing and holds no grant.
        // This is the sglauser case: everyone in the portal, not just whoever provisioned it.
        await AssertAgentPickerListsPlatformAgentsAsync(personId: "Sandra");
    }

    private async Task AssertAgentPickerListsPlatformAgentsAsync(string? personId)
    {
        var who = personId ?? fixture.UserId;
        await using var context = await fixture.NewAuthenticatedContextAsync(personId);
        var token = await fixture.MintTokenAsync(context);

        // Pin the MeshWeaver harness: under a CLI harness "/agent" is forwarded to the CLI instead
        // of opening the picker, so the catalog would never render (SkillCatalogDiscoverableTest's
        // lesson).
        var composerPath = $"{who}/_Thread/ThreadComposer";
        try
        {
            await fixture.CreateNodeAsync(context, token, $$"""
                { "id": "ThreadComposer", "namespace": "{{who}}/_Thread", "name": "Chat Input",
                  "nodeType": "ThreadComposer", "mainNode": "{{who}}",
                  "content": { "$type": "ThreadComposer", "harness": "MeshWeaver" } }
                """);
        }
        catch (InvalidOperationException) { /* already seeded — fine */ }
        await fixture.PatchNodeAsync(context, token, composerPath, "{\"content\":{\"harness\":\"MeshWeaver\"}}");

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);

        await page.GotoAsync($"{fixture.BaseUrl}/{who}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });
        page.Url.Should().NotContain("/login", $"'{who}' must be signed in");

        var toggle = page.Locator(".side-panel-toggle button, .side-panel-toggle fluent-button").First;
        await toggle.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible, Timeout = 60_000
        });
        await toggle.ClickAsync();

        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await editor.WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Visible, Timeout = 60_000
        });

        var harnessChip = page.Locator(".thread-chat-status-item[title^='Harness']");
        (await PollAsync(async () =>
                await harnessChip.CountAsync() > 0
                && (await harnessChip.First.InnerTextAsync())
                    .Contains("MeshWeaver", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the composer must bind to the MeshWeaver harness before /agent opens the picker");

        // Open the agent picker the way a user does: type "/agent" and SUBMIT it. Submitting is
        // what opens the picker widget — pressing Enter inside the editor only adds a line.
        // ChatComposerSwitchSelectionTest.SubmitSlashCommandAsync is the reference interaction.
        await page.Keyboard.PressAsync("Escape");
        await editor.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.TypeAsync("/agent");
        await page.Locator(".thread-chat-footer .selector-bar fluent-button").Last
            .ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

        // The picker's rendered rows — this is what the user actually looks at.
        var rows = page.Locator(".thread-chat-widget .thread-chat-widget-item");
        var seen = "";
        var found = await PollAsync(async () =>
        {
            if (await rows.CountAsync() == 0) return false;
            seen = string.Join(" | ", await rows.AllInnerTextsAsync()).ToLowerInvariant();
            return seen.Contains(ShippedAgent, StringComparison.Ordinal);
        }, TimeSpan.FromSeconds(30));

        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = $"/tmp/agent-picker-{who}.png", FullPage = true
        });

        found.Should().BeTrue(
            $"'{who}' must see the platform's shipped agents in the picker — at minimum "
            + $"'{ShippedAgent}'. An empty or own-agents-only picker is the #902 outage: the Agent "
            + $"partition is missing, unreadable, or was never published read-only by the "
            + $"installer. Saw rows: {seen}");
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
}
