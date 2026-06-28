using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Verifies a chat actually EXECUTES through the Claude Code harness end-to-end (the `claude` CLI is
/// spawned and a result comes back) — the path the user kept hitting "Command failed with exit code 1" on.
/// The recurring root cause was the SDK building a broken <c>--mcp-config</c> arg from a
/// <c>Dictionary&lt;string, McpHttpServerConfig&gt;</c> (it only serializes a <c>Dictionary&lt;string,
/// object&gt;</c>, else passes <c>ToString()</c> → "MCP config file not found" → exit 1). This test asserts:
/// <list type="number">
///   <item>submitting a message under the ClaudeCode harness STARTS a thread (user bubble renders), and</item>
///   <item>the round COMPLETES with an assistant output bubble (a real reply when logged in, or a graceful
///     error cell otherwise) — it must NOT hang, and must NEVER surface the
///     <c>"MCP config file not found"</c> / dictionary-ToString() failure.</item>
/// </list>
/// Drives the real DevLogin portal: <c>memex-local e2e test ClaudeCodeHarnessExecutesTest</c>.
/// </summary>
[Collection("portal-e2e")]
public class ClaudeCodeHarnessExecutesTest(PortalFixture fixture)
{
    [Fact(Timeout = 240_000)]
    public async Task ClaudeCodeHarness_SubmitMessage_ExecutesRound_NoMcpConfigFailure()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        var composerPath = $"{fixture.UserId}/_Thread/ThreadComposer";
        await SeedAsync(context, token, $$"""
            { "id": "ThreadComposer", "namespace": "{{fixture.UserId}}/_Thread", "name": "Chat Input",
              "nodeType": "ThreadComposer", "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "ThreadComposer", "harness": "ClaudeCode" } }
            """);
        await fixture.PatchNodeAsync(context, token, composerPath, "{\"content\":{\"harness\":\"ClaudeCode\"}}");

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);
        await OpenComposerOnAsync(page, $"{fixture.BaseUrl}/{fixture.UserId}");

        var harnessChip = page.Locator(".thread-chat-status-item[title^='Harness']");
        (await PollAsync(async () =>
                await harnessChip.CountAsync() > 0
                && (await harnessChip.First.InnerTextAsync()).Contains("ClaudeCode", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the composer must bind to the ClaudeCode harness");

        // Skip cleanly if this portal has no model (Send is gated by design) — under the playwright skill a
        // model IS present, so this runs for real.
        if (await page.GetByText("No language model is available").CountAsync() > 0)
            Assert.Skip("No language model configured — Send is gated; harness execution not exercised.");

        // Submit a real message.
        const string prompt = "Reply with the single word: pong";
        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await page.Keyboard.PressAsync("Escape");
        await editor.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.TypeAsync(prompt);
        var send = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
        await send.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

        // 1) The thread starts: the user message renders as a bubble.
        await page.Locator(".thread-msg-bubble, .thread-allocating-submitted-text").GetByText("pong").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        // 2) The round COMPLETES — an assistant output bubble appears (≥2 .thread-msg-bubble: user +
        //    assistant). This is the proof the harness ran the CLI and a result came back (a real reply
        //    when logged in, or a graceful error cell like "Not logged in · Please run /login"), rather
        //    than hanging on a broken --mcp-config.
        var bubbles = page.Locator(".thread-msg-bubble");
        var completed = await PollAsync(async () => await bubbles.CountAsync() >= 2, TimeSpan.FromSeconds(150));

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/claude-execute.png", FullPage = true });

        // The MCP-config failure must NEVER appear (that's the bug this guards).
        (await page.GetByText("MCP config file not found", new() { Exact = false }).CountAsync())
            .Should().Be(0, "the --mcp-config arg must be valid JSON, never the dictionary's ToString()");
        (await page.GetByText("Dictionary`2", new() { Exact = false }).CountAsync())
            .Should().Be(0, "the CLI must never receive the McpServers dictionary's type name as --mcp-config");

        completed.Should().BeTrue(
            "submitting under the ClaudeCode harness must EXECUTE the round and surface an assistant output " +
            "(reply when logged in, or a graceful error cell) — it must not hang on the MCP-config arg");
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
            await Task.Delay(400);
        }
        return false;
    }

    private async Task SeedAsync(IBrowserContext context, string token, string nodeJson)
    {
        try { await fixture.CreateNodeAsync(context, token, nodeJson); }
        catch (InvalidOperationException) { /* already seeded — fine */ }
    }
}
