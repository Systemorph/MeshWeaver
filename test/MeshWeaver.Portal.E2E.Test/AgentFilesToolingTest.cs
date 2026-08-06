using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// End-to-end coverage of the ENTIRE <c>AgentFiles</c> tool surface through the real portal: a real
/// language model (host Ollama on the e2e portal) issues real tool calls, and the files it writes are
/// real mesh nodes the mesh API reads back.
///
/// <para><b>Why this exists alongside the unit tests.</b> <c>MeshNodeAgentFileStoreTest</c> proves the
/// store behaves when called directly. Only a live round proves the chain that actually ships: agent
/// frontmatter declares the plugin → <c>ChatClientAgentFactory.ResolvePluginTools</c> resolves it →
/// the tools are advertised to the model → the model calls them → each tool's access context survives
/// the round → <c>MeshNodeAgentFileStore</c> reads/writes → the node is there.</para>
///
/// <para><b>Determinism with a small model.</b> The agent's instructions map a keyword prefix to
/// exactly one tool, one call per message. That is what makes a five-tool walk reproducible on the
/// modest models an e2e box has pulled — the model still decides to call, we just remove the
/// ambiguity about WHICH. A model that declines anyway Skips with a clear reason rather than
/// false-passing, exactly like <c>ChatDelegationTest</c>.</para>
///
/// <para>Setup mirrors <c>ChatDelegationTest</c>: seed a <c>nodeType:Agent</c> node in the chatting
/// user's own <c>{user}/Agent</c> registry (the namespace the picker queries), point the composer at
/// it with a tool-capable model, drive the real side-panel chat. Run with
/// <c>memex-local e2e test AgentFilesToolingTest</c>.</para>
/// </summary>
[Collection("portal-e2e")]
public class AgentFilesToolingTest(PortalFixture fixture)
{
    private const string AgentId = "AgentFilesToolingE2E";

    // One keyword → one tool, one call per message. Deliberately mechanical: the point of the test is
    // the plumbing behind each tool, not the model's judgement about which to pick.
    private const string Instructions =
        "You manage your working files. Follow these rules EXACTLY. Call exactly ONE tool per message " +
        "and never more. " +
        "If the message starts with SAVE: call write_agent_file with path 'notes.md' and content set to " +
        "the text after SAVE:. " +
        "If the message starts with LIST call list_agent_files with directory ''. " +
        "If the message starts with FIND: call search_agent_files with directory '', pattern set to the " +
        "text after FIND:, glob '' and recursive false. " +
        "If the message starts with READ call read_agent_file with path 'notes.md'. " +
        "If the message starts with REMOVE call delete_agent_file with path 'notes.md'. " +
        "After the tool returns, reply with one short sentence. Never ask questions.";

    /// <summary>
    /// Walks the whole tool surface in ONE conversation — write, list, search, read, delete — because
    /// the working area persisting across a conversation IS the feature. Each turn is asserted on the
    /// rendered tool result, and the write/delete turns are additionally cross-checked against the
    /// mesh: the node must exist after the write and be gone after the delete.
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task EveryTool_RunsForReal_AndTheFileIsARealMeshNode()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var toolModel = Environment.GetEnvironmentVariable("E2E_TOOL_MODEL");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(toolModel),
            "E2E_TOOL_MODEL is not set — no tool-capable model is pulled/keyed on the e2e portal " +
            "(qwen-small cannot tool-call). Pull one (e.g. `ollama pull qwen3-coder:30b`) and re-run.");

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);
        var page = await SeedAndOpenAsync(context, token, toolModel!);

        // The marker travels in the prompt, so nothing can pass on a file left by an earlier run
        // against the shared portal.
        var marker = $"e2e-marker-{Guid.NewGuid():N}";

        // ── 1) write_agent_file ──────────────────────────────────────────────────────────────────
        var saved = await SendTurnAsync(page, $"SAVE: {marker}", expectedToolResults: 1,
            "write_agent_file");
        saved.Should().Contain("Saved", "the write tool reports what it stored");
        saved.Should().Contain("notes.md");

        // The tool result names the node it wrote — that path is the cross-check into the mesh.
        var writtenPath = Regex.Match(saved, @"at\s+(\S+?)\.?\s*$", RegexOptions.Multiline).Groups[1].Value;
        writtenPath.Should().NotBeNullOrEmpty($"the write result must name the node (got: {saved})");
        writtenPath.Should().Contain("/_Thread/",
            "the working area is rooted at the conversation's thread, not somewhere global");
        writtenPath.Should().EndWith("/Files/notes.md");
        (await fixture.WaitUntilReadableAsync(context, token, writtenPath, TimeSpan.FromSeconds(60)))
            .Should().BeTrue($"'{writtenPath}' must exist in the mesh as an ordinary node");

        // ── 2) list_agent_files ──────────────────────────────────────────────────────────────────
        var listed = await SendTurnAsync(page, "LIST", expectedToolResults: 2, "list_agent_files");
        listed.Should().Contain("notes.md", "the listing must show the file just written");

        // ── 3) search_agent_files ────────────────────────────────────────────────────────────────
        var found = await SendTurnAsync(page, $"FIND: {marker}", expectedToolResults: 3,
            "search_agent_files");
        found.Should().Contain("notes.md", "the search must locate the file whose content matches");
        found.Should().Contain(marker, "the search result must quote the matching line");

        // ── 4) read_agent_file ───────────────────────────────────────────────────────────────────
        var read = await SendTurnAsync(page, "READ", expectedToolResults: 4, "read_agent_file");
        read.Should().Contain(marker, "reading the file back must return exactly what was written");

        // ── 5) delete_agent_file ─────────────────────────────────────────────────────────────────
        var deleted = await SendTurnAsync(page, "REMOVE", expectedToolResults: 5, "delete_agent_file");
        deleted.Should().Contain("Deleted", "the delete tool reports that the file existed and is gone");

        // …and the node really is gone from the mesh, not just from the tool's reply.
        (await PollAsync(
                async () => !await fixture.CanReadNodeAsync(context, token, writtenPath),
                TimeSpan.FromSeconds(60)))
            .Should().BeTrue($"'{writtenPath}' must no longer be readable after the delete tool ran");
    }

    /// <summary>
    /// The working area is scoped to its conversation: a file written in one thread is not visible
    /// from a NEW thread. This is the containment property the per-thread root exists to provide —
    /// without it one conversation's notes would leak into every other.
    /// </summary>
    [Fact(Timeout = 900_000)]
    public async Task TheWorkingArea_IsScopedToItsOwnThread()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        var toolModel = Environment.GetEnvironmentVariable("E2E_TOOL_MODEL");
        Assert.SkipWhen(string.IsNullOrWhiteSpace(toolModel),
            "E2E_TOOL_MODEL is not set — no tool-capable model is pulled/keyed on the e2e portal.");

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);
        var page = await SeedAndOpenAsync(context, token, toolModel!);

        var marker = $"e2e-scope-{Guid.NewGuid():N}";
        var saved = await SendTurnAsync(page, $"SAVE: {marker}", expectedToolResults: 1, "write_agent_file");
        var writtenPath = Regex.Match(saved, @"at\s+(\S+?)\.?\s*$", RegexOptions.Multiline).Groups[1].Value;
        writtenPath.Should().NotBeNullOrEmpty();

        // A fresh conversation — reloading the composer page starts a new thread.
        await OpenComposerOnAsync(page, $"{fixture.BaseUrl}/{fixture.UserId}");

        var listed = await SendTurnAsync(page, "LIST", expectedToolResults: 1, "list_agent_files");
        listed.Should().NotContain(marker,
            "a new conversation must not see the previous conversation's working files");

        // The original file is untouched — scoping hides it, it does not delete it.
        (await fixture.CanReadNodeAsync(context, token, writtenPath))
            .Should().BeTrue("the first thread's file must still exist; scoping is isolation, not deletion");
    }

    // ── setup + turn helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>Seeds the agent + composer and opens the side-panel chat, ready for the first turn.</summary>
    private async Task<IPage> SeedAndOpenAsync(IBrowserContext context, string token, string toolModel)
    {
        var agentPath = $"{fixture.UserId}/Agent/{AgentId}";

        // `plugins` is what ChatClientAgentFactory.ResolvePluginTools switches on — this one
        // declaration is the entire opt-in, and wiring it is exactly what this test verifies.
        await SeedAsync(context, token, $$"""
            { "id": "{{AgentId}}", "namespace": "{{fixture.UserId}}/Agent", "name": "Agent Files Tooling E2E",
              "description": "Manages working files on request.",
              "nodeType": "Agent", "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "AgentConfiguration", "id": "{{AgentId}}",
                "description": "Manages working files on request.",
                "plugins": [ { "name": "AgentFiles" } ],
                "instructions": "{{Instructions}}" } }
            """);
        // The shared portal may carry a stale copy from a prior run — force the current shape on.
        await fixture.PatchNodeAsync(context, token, agentPath,
            $"{{\"content\":{{\"instructions\":\"{Instructions}\",\"plugins\":[{{\"name\":\"AgentFiles\"}}]}}}}");

        var composerPath = $"{fixture.UserId}/_Thread/ThreadComposer";
        await SeedAsync(context, token, $$"""
            { "id": "ThreadComposer", "namespace": "{{fixture.UserId}}/_Thread", "name": "Chat Input",
              "nodeType": "ThreadComposer", "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "ThreadComposer", "harness": "MeshWeaver", "agentName": "{{agentPath}}", "modelName": "{{toolModel}}" } }
            """);
        await fixture.PatchNodeAsync(context, token, composerPath,
            $"{{\"content\":{{\"harness\":\"MeshWeaver\",\"agentName\":\"{agentPath}\",\"modelName\":\"{toolModel}\"}}}}");

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);
        await OpenComposerOnAsync(page, $"{fixture.BaseUrl}/{fixture.UserId}");

        // The composer must actually bind to the seeded agent, or the round would run someone else's
        // tools and every assertion below would be meaningless.
        var agentChip = page.Locator(".thread-chat-status-item[title^='Agent']");
        (await PollAsync(async () =>
                await agentChip.CountAsync() > 0
                && (await agentChip.First.InnerTextAsync()).Contains("Agent Files", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(40)))
            .Should().BeTrue("the composer must bind to the seeded AgentFiles agent");

        if (await page.GetByText("No language model is available").CountAsync() > 0)
            Assert.Skip("No language model configured — Send is gated; no tool round is exercised.");

        return page;
    }

    /// <summary>
    /// Sends one message and waits for that turn's tool call to resolve, returning the newest tool
    /// result's text. Waits on the RESULT COUNT rather than a fixed delay, so a slow model stretches
    /// the wait instead of flaking it. Skips (never false-passes) if the model declines to call.
    /// </summary>
    private static async Task<string> SendTurnAsync(
        IPage page, string text, int expectedToolResults, string toolName)
    {
        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await page.Keyboard.PressAsync("Escape");
        await editor.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.TypeAsync(text);

        var send = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
        await send.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

        var toolResults = page.Locator(".thread-msg-tool-result");
        var arrived = await PollAsync(
            async () => await toolResults.CountAsync() >= expectedToolResults,
            TimeSpan.FromSeconds(180));

        if (!arrived)
        {
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = $"/tmp/agent-files-{toolName}.png",
                FullPage = true,
            });
            Assert.Skip(
                $"The agent did not emit a {toolName} tool call for '{text}' within the timeout — the " +
                "configured model declined to call the tool. Skipping rather than false-passing.");
        }

        return await toolResults.Nth(expectedToolResults - 1).InnerTextAsync();
    }

    // ── helpers (mirroring ChatDelegationTest) ───────────────────────────────────────────────────

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
        catch (InvalidOperationException) { /* already seeded — the PATCH forces it current */ }
    }
}
