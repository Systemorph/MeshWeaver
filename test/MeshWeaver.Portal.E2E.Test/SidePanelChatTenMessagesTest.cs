using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Sustained-conversation stress test: submits TEN messages in a row in ONE thread through the
/// side-panel Monaco composer, and asserts after EVERY round that (a) the user's bubble appeared
/// promptly, (b) the assistant replied, (c) the round settled, and (d) the chat never vanished — the
/// composer stays mounted, the circuit doesn't drop (no reconnect overlay), and the page never bounces
/// to /login. A render-storm / wedge tears the circuit down or blanks the panel, so a regression breaks
/// these invariants somewhere in the 10-round run (the longer the conversation, the more reliably it
/// surfaces accumulation bugs). After the loop the conversation must hold exactly 10 user + 10 assistant
/// bubbles — none lost, none duplicated.
///
/// <para>Uses the small CPU-friendly model (qwen-small) so ten real rounds are feasible. Drives the real
/// DevLogin e2e portal: <c>memex-local e2e up &amp;&amp; memex-local e2e test SidePanelChatTenMessages</c>.</para>
/// </summary>
[Collection("portal-e2e")]
public class SidePanelChatTenMessagesTest(PortalFixture fixture)
{
    private const int MessageCount = 10;

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

    // The user bubble must appear far below the old 30s stall.
    private static readonly int PromptBubbleMs = 15_000;
    // A real round with the small model; generous but bounded so a genuine wedge fails rather than hangs.
    private static readonly int RoundMs = 120_000;

    private static readonly string ModelPath =
        Environment.GetEnvironmentVariable("E2E_MODEL") ?? "Provider/OpenAICompatible/qwen-small";

    [Fact(Timeout = 600_000)]
    public async Task SidePanelChat_SubmitsTenMessages_NoneLost_ThreadNeverVanishes()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();

        // Seed the per-user composer, then PATCH it onto the MeshWeaver harness + the small model. A
        // PATCH (not create) so the post-creation seed handler doesn't reset it; deterministic regardless
        // of run order (a prior ClaudeCode-harness test would otherwise leave it on ClaudeCode).
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

        // Open the side-panel chat.
        var chatToggle = page.Locator(".side-panel-toggle button, .side-panel-toggle fluent-button").First;
        await chatToggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await chatToggle.ClickAsync();

        var composer = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await composer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        // Bail clearly if the portal has no model — we cannot run real rounds.
        var noModel = page.Locator(".thread-chat-status-msg.error");
        if (await noModel.CountAsync() > 0
            && (await noModel.First.InnerTextAsync()).Contains("No language model", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("No language model configured on the target portal — cannot exercise ten rounds.");

        var userBubbles = page.Locator(".thread-msg-bubble.thread-msg-user");
        var assistantBubbles = page.Locator(".thread-msg-bubble.thread-msg-assistant");
        var execBar = page.Locator(".thread-exec-bar");
        // The composer container + a /login bounce / reconnect overlay are the "did the chat vanish?" tells.
        var composerFooter = page.Locator(".thread-chat-footer");
        var reconnectOverlay = page.Locator("#components-reconnect-modal");

        for (var i = 0; i < MessageCount; i++)
        {
            // Re-resolve the Monaco editor each turn (the side panel rebinds to the freshly-created thread
            // after the first message). Escape closes any open suggest widget, select-all+backspace clears
            // any residual text, then type. Force the click past Playwright's "stable" actionability check —
            // Monaco re-lays-out continuously, which is cosmetic; the keyboard typing lands in the focused
            // editor regardless (mirrors the robust pattern in ChatRepeatedlyNoVanishTest).
            var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
            await page.Keyboard.PressAsync("Escape");
            await editor.ClickAsync(new LocatorClickOptions { Force = true });
            await page.Keyboard.PressAsync("ControlOrMeta+A");
            await page.Keyboard.PressAsync("Backspace");
            await page.Keyboard.TypeAsync($"This is message number {i + 1}. Reply with only the digit {i + 1}.");

            var send = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
            await send.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

            // (a) The user's bubble landed — poll the COUNT (>= i+1), robust to bubbles re-keying on the
            //     thread rebind (a specific .Nth(i) wait is brittle across re-renders).
            (await PollAsync(async () => await userBubbles.CountAsync() >= i + 1, TimeSpan.FromSeconds(15)))
                .Should().BeTrue($"the user bubble for message {i + 1} must appear (saw {await userBubbles.CountAsync()})");

            // No submit error surfaced.
            (await page.Locator(".thread-chat-status-msg.error").CountAsync())
                .Should().Be(0, $"sending message {i + 1} must not surface an error");

            // (b) The assistant reply for this round (count >= i+1), then (c) the round settles so the next
            //     Send re-enables.
            (await PollAsync(async () => await assistantBubbles.CountAsync() >= i + 1, RoundDuration))
                .Should().BeTrue($"round {i + 1} must produce an assistant reply (saw {await assistantBubbles.CountAsync()})");
            await execBar.WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = RoundMs });

            // (d) The chat did NOT vanish: composer still mounted, no reconnect overlay, not bounced to /login.
            (await composerFooter.CountAsync()).Should().BeGreaterThan(0,
                $"the composer must stay mounted after round {i + 1} (a render-storm wedge blanks it)");
            // The Blazor reconnect modal (#components-reconnect-modal) is ALWAYS present in the DOM
            // (hidden via display:none) — so assert it is not VISIBLE, not that it is absent.
            (await reconnectOverlay.IsVisibleAsync()).Should().BeFalse(
                $"the circuit must not drop after round {i + 1} (reconnect overlay must stay hidden)");
            page.Url.Should().NotContain("/login", $"the page must not bounce to /login after round {i + 1}");
        }

        // Every submission landed — exactly N user + N assistant bubbles, none lost, none duplicated.
        (await userBubbles.CountAsync()).Should().Be(MessageCount,
            $"all {MessageCount} submitted messages must appear as user bubbles");
        (await assistantBubbles.CountAsync()).Should().Be(MessageCount,
            $"every one of {MessageCount} rounds must produce an assistant reply");

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/side-panel-ten-messages.png", FullPage = true });
    }

    // A real LLM round can take a while; poll the assistant-bubble count up to this bound.
    private static readonly TimeSpan RoundDuration = TimeSpan.FromMilliseconds(RoundMs);

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
