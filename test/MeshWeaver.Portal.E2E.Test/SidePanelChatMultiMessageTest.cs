using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Drives the SIDE-PANEL chat (the layout's "Chat" toggle → <c>ThreadChatView</c>) end-to-end:
/// opens it, sends MULTIPLE messages through the MeshWeaver harness, and asserts each one is
/// accepted PROMPTLY — the user's bubble appears within a few seconds of pressing Send, never
/// after a 30s request-timeout.
///
/// <para>This is the browser-level guard for the "emit as soon as the activity is STARTED, not
/// finished" fix: submitting a message writes the thread node via <c>stream.Update</c>, which now
/// emits on receive (bounded ~2s + optimistic fallback) instead of blocking up to 30s on the
/// owner's response while a round runs. Before the fix this surfaced in the GUI as
/// "Response did not arrive in time".</para>
///
/// <para>Requires a portal with at least one language model configured (so the MeshWeaver harness
/// can actually run a round). Skips when E2E is disabled (see <see cref="PortalFixture"/>) or when
/// the target portal has no model.</para>
/// </summary>
[Collection("portal-e2e")]
public class SidePanelChatMultiMessageTest(PortalFixture fixture)
{
    private const string ComposerSeedJson = """
        {
          "id": "ThreadComposer",
          "namespace": "Roland/_Thread",
          "name": "Chat Input",
          "nodeType": "ThreadComposer",
          "mainNode": "Roland",
          "content": { "$type": "ThreadComposer" }
        }
        """;

    // The user bubble MUST appear far below the old 30s stall. Generous enough for a cold
    // thread-create on the first message, tight enough to fail hard if the 30s stall returns.
    private static readonly int PromptBubbleMs = 12_000;

    // A real LLM round can take a while; this only bounds the wait for the assistant's reply.
    private static readonly int RoundMs = 120_000;

    // The model node path the composer selects (configurable via E2E_MODEL to match the portal),
    // applied by the PATCH below alongside the MeshWeaver harness.
    private static readonly string ModelPath =
        Environment.GetEnvironmentVariable("E2E_MODEL") ?? "Provider/OpenAICompatible/qwen-small";

    [Fact(Timeout = 360_000)]
    public async Task SidePanelChat_MeshWeaverHarness_SendsMultipleMessages_EachEmitsOnReceive()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();

        // Seed the per-user composer so the side-panel chat has a harness/model selection
        // (a fresh monolith has none — same seed HomeComposerVerifyTest uses), then PATCH it onto
        // the MeshWeaver harness + model. A PATCH (not create) because the composer node PERSISTS
        // across tests in the [Collection("portal-e2e")] run — a prior ClaudeCode-harness test
        // (ClaudeCodeHarnessExecutesTest) leaves it on ClaudeCode, so the create-or-noop seed alone
        // inherits that stale harness and the default-harness assertion below fails. The PATCH makes
        // this test deterministic regardless of run order (same pattern as SidePanelChatSubmitWhileExecutingTest).
        var token = await fixture.MintTokenAsync(context);
        try { await fixture.CreateNodeAsync(context, token, ComposerSeedJson); }
        catch (InvalidOperationException) { /* already seeded — fine */ }
        await context.APIRequest.PostAsync($"{fixture.BaseUrl}/api/mesh/patch", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { ["authorization"] = $"Bearer {token}" },
            DataObject = new
            {
                Path = "Roland/_Thread/ThreadComposer",
                Fields = "{\"content\":{\"$type\":\"ThreadComposer\",\"harness\":\"Harness/MeshWeaver\","
                       + "\"modelName\":\"" + ModelPath + "\",\"contextPath\":\"Roland\"}}"
            }
        });

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 950);
        await page.GotoAsync($"{fixture.BaseUrl}/User/Roland",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // 1) Open the side-panel chat via the layout's "Chat" toggle button.
        var chatToggle = page.Locator(".side-panel-toggle button, .side-panel-toggle fluent-button").First;
        await chatToggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await chatToggle.ClickAsync();

        // 2) The side panel renders ThreadChatView with a Monaco composer.
        var composer = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await composer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        // 3) Bail clearly if the target portal has no language model — we can't run a round.
        var noModel = page.Locator(".thread-chat-status-msg.error");
        if (await noModel.CountAsync() > 0
            && (await noModel.First.InnerTextAsync()).Contains("No language model", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("No language model configured on the target portal — cannot exercise the MeshWeaver harness.");

        // 4) Confirm the MeshWeaver harness is the active one (the read-only status chip). The
        //    new composer defaults to Harnesses.MeshWeaver; this asserts it rather than assuming.
        var harnessChip = page.Locator(".thread-chat-status-item[title^='Harness']");
        if (await harnessChip.CountAsync() > 0)
            (await harnessChip.First.InnerTextAsync()).Should().Contain("MeshWeaver",
                "the side-panel chat should run on the MeshWeaver harness by default");

        var messages = new[]
        {
            "Reply with exactly one short sentence.",
            "Now reply with just the word OK.",
            "Finally, reply with a single digit.",
        };

        var userBubbles = page.Locator(".thread-msg-bubble.thread-msg-user");
        var assistantBubbles = page.Locator(".thread-msg-bubble.thread-msg-assistant");
        var execBar = page.Locator(".thread-exec-bar");

        for (var i = 0; i < messages.Length; i++)
        {
            // Type into the Monaco composer (re-resolve each turn — the side panel rebinds to the
            // freshly-created thread after the first message).
            var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
            await editor.ClickAsync();
            await page.Keyboard.TypeAsync(messages[i]);

            // Send — the accent button auto-waits until it is enabled (text present + thread idle).
            var send = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
            await send.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

            // 🚦 THE FIX: the user's bubble appears PROMPTLY — submission emits on receive, it does
            // NOT stall on the round (the old 30s "Response did not arrive in time").
            await userBubbles.Nth(i).WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = PromptBubbleMs });

            // No submit error surfaced in the footer.
            (await page.Locator(".thread-chat-status-msg.error").CountAsync())
                .Should().Be(0, "sending a message must not surface an error (e.g. a request timeout)");

            // The assistant's reply for this turn arrives, then the round settles (exec bar gone)
            // so the next Send re-enables.
            await assistantBubbles.Nth(i).WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = RoundMs });
            await execBar.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = RoundMs });
        }

        // The conversation grew to N user + N assistant bubbles — every submission landed.
        (await userBubbles.CountAsync()).Should().Be(messages.Length,
            "every submitted message must appear as a user bubble");
        (await assistantBubbles.CountAsync()).Should().Be(messages.Length,
            "every round must produce an assistant reply");

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/side-panel-chat.png", FullPage = true });
    }
}
