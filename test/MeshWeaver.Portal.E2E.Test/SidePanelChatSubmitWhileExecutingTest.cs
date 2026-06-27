using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Browser-level guard for the two-stage inbox channel + re-enabled mid-round <c>check_inbox</c>:
/// submit a follow-up message WHILE a round is still executing, and assert the thread does NOT
/// wedge — the follow-up is accepted promptly (it queues via <c>PendingUserMessages</c>), the
/// running round still settles, and every message is handled (none lost).
///
/// <para>This is the end-to-end proof of the redesign that the unit/integration tests cover
/// deterministically (<c>InboxToolIntegrationTest</c>): the thread node is no longer mutated
/// mid-stream (the wedge/crash cause), <c>check_inbox</c> drains a per-thread in-memory channel
/// synchronously (no TaskCompletionSource resuming on the hub action-block scheduler — the old
/// "thread disappears" deadlock), and concurrent submits merge in-turn (no message loss). With a
/// REAL model present (this skill's Colima portal serves one) the round actually executes, so the
/// mid-round submission races a live streaming round exactly as it does in production.</para>
///
/// <para>Skips when E2E is disabled (see <see cref="PortalFixture"/>) or the target portal has no
/// language model configured.</para>
/// </summary>
[Collection("portal-e2e")]
public class SidePanelChatSubmitWhileExecutingTest(PortalFixture fixture)
{
    // Seed the per-user composer pinned to the MeshWeaver harness (so check_inbox is in play —
    // the e2e portal defaults to Copilot, which needs a GitHub connection it doesn't have) and
    // with ContextPath = "Roland" (the user's writable partition, proven by CompileErrorPageE2ETest)
    // so the thread anchors at Roland/_Thread/… instead of the system-managed "User" partition.
    private const string ComposerSeedJson = """
        {
          "id": "ThreadComposer",
          "namespace": "Roland/_Thread",
          "name": "Chat Input",
          "nodeType": "ThreadComposer",
          "mainNode": "Roland",
          "content": { "$type": "ThreadComposer", "harness": "Harness/MeshWeaver", "contextPath": "Roland" }
        }
        """;

    // The follow-up's user bubble MUST appear well below the old 30s stall even though a round is
    // in flight — submission emits on receive, it does not block on the running round.
    private static readonly int PromptBubbleMs = 15_000;

    // A real LLM round can take a while; generous enough for BOTH the in-flight round AND a
    // follow-up round (one user message per round per PlanNextRound), tight enough that a genuine
    // wedge (the thing this guards) fails the test instead of hanging forever.
    private static readonly int SettleMs = 180_000;

    [Fact(Timeout = 420_000)]
    public async Task SidePanelChat_SubmitWhileExecuting_QueuesFollowUp_RoundSettles_NoneLost()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();

        // Seed the per-user composer pinned to MeshWeaver harness + ContextPath=Roland. It may
        // already exist (DB persists across portal restarts) with the wrong content, so delete it
        // first (best-effort) then create it fresh — CreateNodeAsync has no update path.
        var token = await fixture.MintTokenAsync(context);
        await context.APIRequest.PostAsync($"{fixture.BaseUrl}/api/mesh/delete", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { ["authorization"] = $"Bearer {token}" },
            DataObject = new { Path = "Roland/_Thread/ThreadComposer" }
        });
        try { await fixture.CreateNodeAsync(context, token, ComposerSeedJson); }
        catch (InvalidOperationException) { /* already seeded — fine */ }

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 950);
        await page.GotoAsync($"{fixture.BaseUrl}/User/Roland",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Open the side-panel chat (the layout's "Chat" toggle → ThreadChatView).
        var chatToggle = page.Locator(".side-panel-toggle button, .side-panel-toggle fluent-button").First;
        await chatToggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await chatToggle.ClickAsync();

        var composer0 = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await composer0.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        // Bail clearly if no model — we cannot run a real round.
        var noModel = page.Locator(".thread-chat-status-msg.error");
        if (await noModel.CountAsync() > 0
            && (await noModel.First.InnerTextAsync()).Contains("No language model", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("No language model configured on the target portal — cannot exercise a mid-round submission.");

        var userBubbles = page.Locator(".thread-msg-bubble.thread-msg-user");
        var assistantBubbles = page.Locator(".thread-msg-bubble.thread-msg-assistant");
        var execBar = page.Locator(".thread-exec-bar");

        // ── Round 1: a prompt with a long, slow streaming answer so we have a window to submit a
        //    follow-up while it is genuinely executing. ──────────────────────────────────────────
        var editor1 = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await editor1.ClickAsync();
        await page.Keyboard.TypeAsync(
            "Count from 1 to 40, one number per line, and write a short sentence about each number.");
        var send1 = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
        await send1.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

        // The first user bubble appears promptly, and the round starts executing (exec bar shown).
        await userBubbles.Nth(0).WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = PromptBubbleMs });
        await execBar.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        // ── Mid-round: while the exec bar is STILL visible (round 1 streaming), submit a follow-up.
        //    The composer allows this — it queues via PendingUserMessages and is drained either
        //    inline (check_inbox) or in a follow-up round. This is the exact "submit while
        //    executing" flow that used to wedge/crash. ─────────────────────────────────────────────
        (await execBar.IsVisibleAsync()).Should().BeTrue(
            "round 1 must still be executing when the follow-up is submitted (this is the mid-round case)");

        var editor2 = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await editor2.ClickAsync();
        await page.Keyboard.TypeAsync("Also, briefly: what is the capital of France?");
        var send2 = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
        await send2.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

        // 🚦 THE GUARD: the follow-up is accepted PROMPTLY even though a round is in flight — the
        // second user bubble appears, it does not stall (old 30s "Response did not arrive in time")
        // and the thread does not wedge.
        await userBubbles.Nth(1).WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = PromptBubbleMs });
        (await page.Locator(".thread-chat-status-msg.error").CountAsync())
            .Should().Be(0, "a mid-round submission must not surface an error (e.g. a request timeout)");

        // 🚦 NO WEDGE: the thread settles — the in-flight round finishes AND the queued follow-up is
        // drained (inline or as a follow-up round), so the exec bar eventually detaches. A wedge
        // (the regression this guards) would leave it spinning until the timeout.
        await execBar.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = SettleMs });

        // 🚦 NONE LOST + output produced: both user messages are present and at least one assistant
        // reply was rendered (the follow-up is folded inline into round 1's reply via check_inbox,
        // or answered by a follow-up round — either is correct; the invariant is no loss + no wedge).
        (await userBubbles.CountAsync()).Should().Be(2,
            "both submissions — the initial and the mid-round follow-up — must be present, none lost");
        (await assistantBubbles.CountAsync()).Should().BeGreaterThan(0,
            "the round(s) must produce at least one assistant reply");

        await page.ScreenshotAsync(new PageScreenshotOptions
        { Path = "/tmp/submit-while-executing.png", FullPage = true });
    }
}
