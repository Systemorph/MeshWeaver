using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Guards the <c>/clear</c> skill (<see cref="MeshWeaver.AI.SkillActionKind.NewThread"/>): typing
/// <c>/clear</c> in a running side-panel thread must REPLACE that thread with a fresh new-chat composer
/// IN THE SIDE PANEL, and leave the main pane alone (no navigation). The harness/agent/model selection
/// is kept.
///
/// <para>Flow: open the side panel, start a thread (a user bubble appears), run <c>/clear</c>, then assert
/// the thread's message bubbles are gone, a composer is still mounted in the SAME still-open side panel,
/// and the main pane's URL is unchanged.</para>
///
/// <para>Requires a portal with a language model. Drive it:
/// <c>memex-local e2e test ChatClearSkillNewComposerTest</c>.</para>
/// </summary>
[Collection("portal-e2e")]
public class ChatClearSkillNewComposerTest(PortalFixture fixture)
{
    private const string FirstMessage = "Reply with exactly the word OK.";

    // The user bubble appears once the thread is created + shown. /clear only needs a RUNNING thread (a user
    // message showing) — NOT the assistant's reply, so we never wait on a model round. Generous because the
    // FIRST message on a cold portal cold-starts several hubs before the side-panel swap renders the bubble.
    private static readonly int BubbleMs = 90_000;

    // A LanguageModel node that actually EXISTS in the catalog (env-overridable via E2E_MODEL). NOTE: the
    // older chat E2E tests default to "…/qwen-small", but that path has drifted to a ":latest" tag and no
    // longer resolves — so we default to a current node. This test is robust to the model anyway: the user
    // bubble it asserts is optimistic (StartThread does not resolve the model), and SkipIfNoModelAsync skips
    // when the catalog is empty; the model only affects the (unawaited) assistant round.
    private static readonly string ModelPath =
        Environment.GetEnvironmentVariable("E2E_MODEL") ?? "Provider/OpenAICompatible/qwen3.6-code:latest";

    // The side panel wraps its composer in .side-panel; the main pane does not.
    private const string SideFooter = ".side-panel .thread-chat-footer";

    private string ComposerSeedJson => $$"""
        {
          "id": "ThreadComposer",
          "namespace": "{{fixture.UserId}}/_Thread",
          "name": "Chat Input",
          "nodeType": "ThreadComposer",
          "mainNode": "{{fixture.UserId}}",
          "content": { "$type": "ThreadComposer" }
        }
        """;

    [Fact(Timeout = 360_000)]
    public async Task ClearSkill_InSidePanel_ReplacesThreadWithComposer_AndLeavesMainAlone()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await SeedComposerOnMeshWeaverAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 950);
        await page.GotoAsync($"{fixture.BaseUrl}/User/{fixture.UserId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        var mainUrl = page.Url;

        await OpenSidePanelAsync(page);
        await WaitComposerReadyAsync(page, SideFooter);
        if (await SkipIfNoModelAsync(page, SideFooter)) return;

        // 1) Start a real thread in the side panel: type + Send. The user's bubble appearing = a thread now
        //    exists and is running. We do NOT wait for the assistant reply.
        await TypeAndSendAsync(page, SideFooter, FirstMessage);
        await page.Locator(".thread-msg-bubble.thread-msg-user").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = BubbleMs });
        (await page.Locator(".thread-msg-bubble").CountAsync()).Should().BeGreaterThan(0,
            "a thread is running in the side panel before /clear");

        // 2) Run /clear in the side-panel thread.
        await RunClearAsync(page, SideFooter);

        // 3) The thread is REPLACED by a fresh composer — in the SAME, still-open side panel.
        await Assertions.Expect(page.Locator(".thread-msg-bubble")).ToHaveCountAsync(0,
            new LocatorAssertionsToHaveCountOptions { Timeout = 20_000 });
        await Assertions.Expect(page.Locator(".side-panel")).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 10_000 });
        await Assertions.Expect(page.Locator($"{SideFooter} .monaco-editor").Last).ToBeVisibleAsync(
            new LocatorAssertionsToBeVisibleOptions { Timeout = 15_000 });

        // 4) The main pane is LEFT ALONE — /clear does not navigate it.
        page.Url.Should().Be(mainUrl, "/clear replaces the side panel in place and must not navigate the main pane");

        // 5) The harness selection is KEPT on the fresh composer (we patched it onto MeshWeaver).
        await Assertions.Expect(page.Locator($"{SideFooter} .thread-chat-status-item[title^='Harness']").First)
            .ToContainTextAsync("MeshWeaver",
                new LocatorAssertionsToContainTextOptions { Timeout = 15_000 });

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/clear-sidepanel.png", FullPage = true });
    }

    // ─── helpers ────────────────────────────────────────────────────────────────

    /// <summary>Authenticated context with the per-user composer seeded and PATCHed onto MeshWeaver + a
    /// model, and any leftover draft cleared — so the test starts from a clean new-chat composer regardless
    /// of run order in the shared collection.</summary>
    private async Task<IBrowserContext> SeedComposerOnMeshWeaverAsync()
    {
        var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);
        try { await fixture.CreateNodeAsync(context, token, ComposerSeedJson); }
        catch (InvalidOperationException) { /* already seeded — fine */ }
        // PatchNodeAsync throws on a non-success response, so setup is deterministic (never silently
        // inherits a prior test's harness/model left on the shared composer node).
        await fixture.PatchNodeAsync(context, token, $"{fixture.UserId}/_Thread/ThreadComposer",
            "{\"content\":{\"$type\":\"ThreadComposer\",\"harness\":\"Harness/MeshWeaver\","
            + "\"agentName\":\"Agent/Assistant\",\"modelName\":\"" + ModelPath + "\","
            + "\"contextPath\":\"" + fixture.UserId + "\",\"messageContent\":null,\"openThreadPath\":null}}");
        return context;
    }

    private static async Task OpenSidePanelAsync(IPage page)
    {
        var toggle = page.Locator(".side-panel-toggle button, .side-panel-toggle fluent-button").First;
        await toggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await toggle.ClickAsync();
    }

    private static async Task WaitComposerReadyAsync(IPage page, string footer)
        => await page.Locator($"{footer} .monaco-editor").Last.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

    private static async Task<bool> SkipIfNoModelAsync(IPage page, string footer)
    {
        var noModel = page.Locator($"{footer} .thread-chat-status-msg.error");
        if (await noModel.CountAsync() > 0
            && (await noModel.First.InnerTextAsync()).Contains("No language model", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("No language model configured on the target portal — cannot run a round to clear.");
            return true;
        }
        return false;
    }

    /// <summary>Types a plain message into the composer scoped by <paramref name="footer"/> and Sends it.
    /// The Send button is <c>disabled</c> until <c>MessageText</c> is populated by the editor, so we wait for
    /// the ENABLED Send (the real condition that Monaco's ValueChanged synced the text) before clicking —
    /// no debounce guessing. This is what makes StartThread receive a non-empty first message.</summary>
    private static async Task TypeAndSendAsync(IPage page, string footer, string text)
    {
        var editor = page.Locator($"{footer} .monaco-editor").Last;
        await editor.ClickAsync();
        await page.Keyboard.TypeAsync(text);
        var enabledSend = page.Locator($"{footer} .selector-bar fluent-button:not([disabled])").Last;
        await enabledSend.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20_000 });
        await enabledSend.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });
    }

    /// <summary>Types <c>/clear</c> into the running thread's composer (scoped by <paramref name="footer"/>)
    /// and runs it via the Send button (which dispatches the slash skill through SubmitMessageCore). Waits
    /// for the ENABLED Send (MessageText = "/clear") before clicking; the Send click blurs Monaco and closes
    /// its slash-autocomplete popup, so the skill is dispatched rather than a completion accepted.</summary>
    private static async Task RunClearAsync(IPage page, string footer)
    {
        var editor = page.Locator($"{footer} .monaco-editor").Last;
        await editor.ClickAsync();
        await page.Keyboard.TypeAsync("/clear");
        var enabledSend = page.Locator($"{footer} .selector-bar fluent-button:not([disabled])").Last;
        await enabledSend.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20_000 });
        await enabledSend.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });
    }
}
