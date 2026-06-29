using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// End-to-end guard for the composer's slash-command selectors — <c>/agent</c>, <c>/model</c> and
/// <c>/harness</c>. Each submits a <c>nodeType:Skill</c> Pick behaviour that opens the GENERIC node picker
/// (<c>.thread-chat-widget</c>); choosing an item writes the node PATH to the composer field, and the
/// read-only status row (<c>.thread-chat-status-item</c>) reflects the selection. Drives the real DevLogin
/// portal: <c>memex-local e2e test ChatComposerSwitchSelectionTest</c>.
///
/// <para>The chatting user is pinned to the MeshWeaver harness first: <c>/agent</c> and <c>/model</c> are
/// MeshWeaver-harness selectors (under a CLI harness they'd be forwarded to the CLI, not intercepted).</para>
/// </summary>
[Collection("portal-e2e")]
public class ChatComposerSwitchSelectionTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task SlashCommands_OpenPicker_AndSelectionUpdatesStatusRow()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        // Pin the MeshWeaver harness so /agent + /model are intercepted as selectors (not forwarded to a CLI).
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

        // Wait until the composer is actually bound to MeshWeaver before exercising the MeshWeaver-only
        // /agent + /model selectors (under a CLI harness they'd be forwarded, so no picker opens). This
        // makes the test independent of any harness state a prior test left on the shared composer.
        var harnessReady = page.Locator(".thread-chat-status-item[title^='Harness']");
        (await PollAsync(async () =>
                await harnessReady.CountAsync() > 0
                && (await harnessReady.First.InnerTextAsync()).Contains("MeshWeaver", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the composer must bind to the MeshWeaver harness before /agent and /model are testable");

        // ── /agent → picker → pick first agent → the Agent status chip reflects the selection ─────────
        var agentName = await PickHighlightedFromAsync(page, "agent");
        agentName.Should().NotBeNullOrWhiteSpace("the /agent picker must list at least one selectable agent");
        var agentChip = page.Locator(".thread-chat-status-item[title^='Agent']");
        await agentChip.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        (await agentChip.First.InnerTextAsync()).Trim().Should().NotBeNullOrWhiteSpace(
            "selecting an agent via /agent must populate the read-only Agent status chip");

        // ── /model → picker → pick first model → the Model status chip reflects the selection ─────────
        var modelName = await PickHighlightedFromAsync(page, "model");
        modelName.Should().NotBeNullOrWhiteSpace("the /model picker must list at least one selectable model");
        var modelChip = page.Locator(".thread-chat-status-item[title^='Model']");
        await modelChip.First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        (await modelChip.First.InnerTextAsync()).Trim().Should().NotBeNullOrWhiteSpace(
            "selecting a model via /model must populate the read-only Model status chip");

        // ── /harness → picker (≥2 options) → switch to a DIFFERENT harness → the Harness chip CHANGES ──
        var harnessChip = page.Locator(".thread-chat-status-item[title^='Harness']");
        var before = (await harnessChip.First.InnerTextAsync()).Trim();
        var switchedTo = await SwitchToDifferentHarnessAsync(page, before);
        switchedTo.Should().NotBeNullOrWhiteSpace("the /harness picker must offer more than one harness to switch to");

        // The read-only Harness chip must reflect the switch (LastSegment of the new harness path) — poll
        // since the chip updates reactively off the composer write.
        var changed = await PollAsync(async () =>
            (await harnessChip.First.InnerTextAsync()).Trim() != before, TimeSpan.FromSeconds(15));
        changed.Should().BeTrue($"switching the harness via /harness must change the Harness status chip from '{before}'");

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/composer-switch.png", FullPage = true });
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────────

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

    /// <summary>
    /// Submits <c>/{word}</c> as a command (NOT an autocomplete accept) and waits for the generic picker.
    /// Typing then clicking Send routes through <c>SubmitMessageCore</c> → the skill's Pick action → the
    /// picker; clicking Send (outside the editor) closes the suggest widget without accepting it.
    /// </summary>
    private static async Task SubmitSlashCommandAsync(IPage page, string word)
    {
        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await page.Keyboard.PressAsync("Escape");
        await editor.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.TypeAsync($"/{word}");

        var send = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
        await send.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

        await page.Locator(".thread-chat-widget").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
    }

    /// <summary>
    /// Opens the <c>/{word}</c> picker and selects the highlighted (first selectable) item via the
    /// KEYBOARD — the widget auto-focuses and Enter commits the highlight (ThreadChatView.OnPickerKeyDown).
    /// Keyboard selection avoids the viewport problem where a long picker list (e.g. all agents) renders
    /// above the fold so the first row isn't clickable. Returns the selected item's visible name.
    /// </summary>
    private static async Task<string?> PickHighlightedFromAsync(IPage page, string word)
    {
        await SubmitSlashCommandAsync(page, word);
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = $"/tmp/picker-{word}.png", FullPage = true });

        var widget = page.Locator(".thread-chat-widget").First;
        var active = widget.Locator(".thread-chat-widget-item--active");
        var name = await active.CountAsync() > 0
            ? (await active.First.InnerTextAsync()).Trim()
            : await widget.Locator(".thread-chat-widget-item").CountAsync() > 0
                ? (await widget.Locator(".thread-chat-widget-item").First.InnerTextAsync()).Trim()
                : null;
        if (name is null)
            return null;

        await widget.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await widget.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 15_000 });
        return name;
    }

    /// <summary>
    /// Opens the /harness picker and selects a DIFFERENT harness than the highlighted/current one via the
    /// keyboard (ArrowDown to move off the current, Enter to commit). Returns the selected name.
    /// </summary>
    private static async Task<string?> SwitchToDifferentHarnessAsync(IPage page, string current)
    {
        await SubmitSlashCommandAsync(page, "harness");
        var widget = page.Locator(".thread-chat-widget").First;
        if (await widget.Locator(".thread-chat-widget-item").CountAsync() < 2)
        {
            await page.Keyboard.PressAsync("Escape");
            return null;
        }
        await widget.FocusAsync();
        // Move the highlight off the first (current) harness, then read + commit the newly-highlighted one.
        await page.Keyboard.PressAsync("ArrowDown");
        var active = widget.Locator(".thread-chat-widget-item--active");
        var name = await active.CountAsync() > 0 ? (await active.First.InnerTextAsync()).Trim() : null;
        await page.Keyboard.PressAsync("Enter");
        await widget.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 15_000 });
        return name;
    }

    /// <summary>Polls <paramref name="predicate"/> every 300ms until true or <paramref name="timeout"/> elapses.</summary>
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
