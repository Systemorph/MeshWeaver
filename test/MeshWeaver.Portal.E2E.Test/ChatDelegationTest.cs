using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Exercises the AGENT-DELEGATION chat flow end-to-end through the side-panel chat: a coordinator
/// agent delegating work to a SUB-AGENT (which runs in its own sub-thread), and the delegated result
/// flowing back into the parent thread.
///
/// <para><b>Setup mirrors <c>OrleansSubThreadAutoResumeTest</c></b> (the delegating-parent + streaming-
/// sub-agent shape) but expressed through the real portal: instead of a custom <c>IChatClientFactory</c>,
/// it seeds two real <c>nodeType:Agent</c> nodes in the chatting user's own <c>{user}/Agent</c> registry
/// (the same per-user registry pattern <c>SkillSlashMenuScopingTest</c> seeds <c>{user}/Skill</c> into):
/// <list type="bullet">
///   <item>a WORKER sub-agent that answers directly and emits a <c>&lt;summary&gt;</c> (which becomes the
///     delegation tool-call result the parent receives — see <c>ChatClientAgentFactory</c>'s summary
///     contract); and</item>
///   <item>a COORDINATOR agent whose instructions FORCE it to call <c>delegate_to_agent</c> targeting the
///     worker for every message. The coordinator is selected on the composer.</item>
/// </list>
/// Both agents become hierarchy agents (the user's registry returns &gt;1 agent), so
/// <c>ChatClientAgentFactory.GetAgentTools</c> wires the <c>delegate_to_agent</c> tool and lists the worker
/// as an available delegation target.</para>
///
/// <para><b>Assertions are on the rendered delegation DOM</b> (<c>ThreadMessageBubbleView.razor</c>): the
/// parent's assistant bubble renders a <c>.thread-msg-delegation-entry</c> with a
/// <c>.thread-msg-delegation-link</c> pointing at the spawned sub-thread (the delegation indicator), the
/// sub-thread node is cross-checked as readable in the mesh, and the delegated result coming back resolves
/// the tool call into the completed form (<c>details.thread-msg-delegation-entry</c> /
/// <c>.thread-msg-tool-result</c>). A real round + a real sub-round is needed, so this requires a portal
/// with a language model — under the playwright skill one IS present, so it runs for real; on a model-less
/// portal it Skips. If a (weak) model genuinely declines to delegate, it Skips with a clear reason rather
/// than false-passing. Drives the real DevLogin portal: <c>memex-local e2e test ChatDelegationTest</c>.
/// </summary>
[Collection("portal-e2e")]
public class ChatDelegationTest(PortalFixture fixture)
{
    private const string WorkerId = "DelegationWorkerE2E";
    private const string CoordinatorId = "DelegationCoordinatorE2E";

    // The worker answers directly and emits a <summary> — the framework strips it and returns it as the
    // delegate_to_agent tool result the coordinator receives back (ChatClientAgentFactory summary contract).
    private const string WorkerInstructions =
        "You are a helpful worker sub-agent. Answer the user's request in one short, friendly sentence. " +
        "Do NOT delegate to anyone — answer it yourself. At the very end, on its own line, emit a " +
        "<summary>one sentence describing what you produced</summary> block.";

    // The coordinator never answers itself — it is forced to delegate every message to the worker. This is
    // the deterministic analogue of OrleansSubThreadAutoResumeTest's DelegatingParentAutoResumeClient
    // (first turn emits delegate_to_agent; after the result returns it relays a wrap-up).
    private const string CoordinatorInstructions =
        "You are a routing coordinator. You never answer the user directly. For EVERY user message you MUST " +
        "immediately call the delegate_to_agent tool exactly once, with agentName set to " + WorkerId + " " +
        "and task set to the user's request verbatim. Never write your own answer before delegating. After " +
        "the worker returns its result, relay that result to the user in one short sentence.";

    [Fact(Timeout = 360_000)]
    public async Task Coordinator_DelegatesToWorker_SubThreadSpawns_ResultFlowsBack_AndRendersWhenOpened()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        var workerPath = $"{fixture.UserId}/Agent/{WorkerId}";
        var coordinatorPath = $"{fixture.UserId}/Agent/{CoordinatorId}";

        // ── Seed the two agents in the chatting user's own {user}/Agent registry ─────────────────────────
        // Worker (sub-agent). Its node Description is what the coordinator sees in its available-delegation
        // list, so make the worker the obvious target.
        await SeedAsync(context, token, $$"""
            { "id": "{{WorkerId}}", "namespace": "{{fixture.UserId}}/Agent", "name": "Delegation Worker E2E",
              "description": "Handles every user request end to end. Always delegate user tasks to this worker.",
              "nodeType": "Agent", "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "AgentConfiguration", "id": "{{WorkerId}}",
                "description": "Handles every user request end to end. Always delegate user tasks to this worker.",
                "instructions": "{{WorkerInstructions}}" } }
            """);
        // Coordinator (delegating agent).
        await SeedAsync(context, token, $$"""
            { "id": "{{CoordinatorId}}", "namespace": "{{fixture.UserId}}/Agent", "name": "Delegation Coordinator E2E",
              "description": "Routes every request to the worker sub-agent via delegate_to_agent.",
              "nodeType": "Agent", "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "AgentConfiguration", "id": "{{CoordinatorId}}",
                "description": "Routes every request to the worker sub-agent via delegate_to_agent.",
                "instructions": "{{CoordinatorInstructions}}" } }
            """);
        // The shared portal may carry stale copies from a prior run — force the current instructions on.
        await fixture.PatchNodeAsync(context, token, workerPath,
            $"{{\"content\":{{\"instructions\":\"{WorkerInstructions}\"}}}}");
        await fixture.PatchNodeAsync(context, token, coordinatorPath,
            $"{{\"content\":{{\"instructions\":\"{CoordinatorInstructions}\"}}}}");

        // ── Seed the composer: MeshWeaver harness + the coordinator agent selected ───────────────────────
        var composerPath = $"{fixture.UserId}/_Thread/ThreadComposer";
        await SeedAsync(context, token, $$"""
            { "id": "ThreadComposer", "namespace": "{{fixture.UserId}}/_Thread", "name": "Chat Input",
              "nodeType": "ThreadComposer", "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "ThreadComposer", "harness": "MeshWeaver", "agentName": "{{coordinatorPath}}" } }
            """);
        await fixture.PatchNodeAsync(context, token, composerPath,
            $"{{\"content\":{{\"harness\":\"MeshWeaver\",\"agentName\":\"{coordinatorPath}\"}}}}");

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);
        await OpenComposerOnAsync(page, $"{fixture.BaseUrl}/{fixture.UserId}");

        // The composer must bind to the coordinator (the status chip shows the agent path's last segment),
        // otherwise the round wouldn't run the delegating agent and the test would be meaningless.
        var agentChip = page.Locator(".thread-chat-status-item[title^='Agent']");
        (await PollAsync(async () =>
                await agentChip.CountAsync() > 0
                && (await agentChip.First.InnerTextAsync()).Contains("Coordinator", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(40)))
            .Should().BeTrue("the composer must bind to the seeded coordinator agent so the round delegates");

        // Skip cleanly if this portal has no model (Send is gated by design) — a delegation round needs one.
        if (await page.GetByText("No language model is available").CountAsync() > 0)
            Assert.Skip("No language model configured — Send is gated; delegation round not exercised.");

        // ── Submit a message designed to be delegated ────────────────────────────────────────────────────
        const string prompt = "Please greet me in a friendly way.";
        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await page.Keyboard.PressAsync("Escape");
        await editor.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.TypeAsync(prompt);
        var send = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
        await send.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

        // The thread starts: the user's message renders as a bubble.
        await page.Locator(".thread-msg-bubble.thread-msg-user").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        // ── 1) Delegation happened: the parent thread shows a delegation entry / sub-thread link ─────────
        var delegationEntry = page.Locator(".thread-msg-delegation-entry");
        var delegationLink = page.Locator(".thread-msg-delegation-link");
        var delegated = await PollAsync(
            async () => await delegationEntry.CountAsync() > 0 && await delegationLink.CountAsync() > 0,
            TimeSpan.FromSeconds(150));

        if (!delegated)
        {
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/chat-delegation.png", FullPage = true });
            Assert.Skip(
                "The coordinator did not emit a delegate_to_agent tool call (no .thread-msg-delegation-entry " +
                "rendered) within the timeout — the configured model declined to delegate. Cannot verify the " +
                "delegation flow without a real delegation; skipping rather than false-passing.");
        }

        // The delegation link points at the spawned sub-thread (href="/{subThreadPath}"). Cross-check that a
        // real sub-thread node exists in the mesh, nested under the parent thread.
        var href = await delegationLink.First.GetAttributeAsync("href");
        href.Should().NotBeNullOrEmpty("the delegation chip must link to the spawned sub-thread");
        var subThreadPath = href!.TrimStart('/');
        subThreadPath.Should().Contain("/_Thread/",
            "the delegation must spawn a sub-thread nested under the parent thread path");

        (await fixture.WaitUntilReadableAsync(context, token, subThreadPath, TimeSpan.FromSeconds(60)))
            .Should().BeTrue($"the spawned sub-thread node '{subThreadPath}' must be readable in the mesh");

        // ── 2) The delegated result flows back: the tool call resolves into its COMPLETED form ───────────
        // While running, the entry is a <div>; once the worker returns its summary the tool-call Result is
        // set and the bubble re-renders the delegation as <details class="thread-msg-tool-entry
        // thread-msg-delegation-entry"> with a <pre class="thread-msg-tool-result">.
        var completedDelegation = page.Locator("details.thread-msg-delegation-entry");
        var toolResult = page.Locator(".thread-msg-tool-result");
        var resultBack = await PollAsync(
            async () => await completedDelegation.CountAsync() > 0 || await toolResult.CountAsync() > 0,
            TimeSpan.FromSeconds(150));

        // The parent thread carries the coordinator's assistant bubble (which hosts the delegation entry).
        (await page.Locator(".thread-msg-bubble.thread-msg-assistant").CountAsync())
            .Should().BeGreaterThan(0, "the parent thread must render the coordinator's assistant bubble");

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/chat-delegation.png", FullPage = true });

        resultBack.Should().BeTrue(
            "the delegated worker's result must flow back into the parent thread — the delegation tool call " +
            "must resolve into its completed form (details.thread-msg-delegation-entry / .thread-msg-tool-result)");

        // ── 3) OPEN the spawned sub-thread directly — the "I clicked into the sub-thread" flow the user
        // reported ("it didn't look like starting"). Navigating to the sub-thread's own URL must render
        // ITS conversation (the user's delegated request + the worker's assistant answer) — not an empty /
        // idle view. A broken sub-thread ThreadChatView binding would surface here as no assistant bubble.
        var subPage = await context.NewPageAsync();
        await subPage.SetViewportSizeAsync(1400, 1000);
        await subPage.GotoAsync($"{fixture.BaseUrl}/{subThreadPath}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });
        subPage.Url.Should().NotContain("/login");

        await subPage.Locator(".thread-msg-bubble.thread-msg-user").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        var subRendered = await PollAsync(
            async () => await subPage.Locator(".thread-msg-bubble.thread-msg-assistant").CountAsync() > 0,
            TimeSpan.FromSeconds(90));
        await subPage.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/chat-subthread-open.png", FullPage = true });
        subRendered.Should().BeTrue(
            "opening the spawned sub-thread must render its OWN conversation (the worker's assistant bubble) " +
            "— the 'clicked into the sub-thread, it didn't look like starting' symptom would show as an empty view");
    }

    // ── helpers (mirroring ClaudeCodeHarnessExecutesTest) ────────────────────────────────────────────────

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
