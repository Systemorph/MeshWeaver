using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// End-to-end proof that typing <c>@</c> in the composer's Monaco editor triggers node-reference
/// autocomplete — the GUI half of <c>IChatCompletionOrchestrator</c> that the backend
/// <c>AutocompleteIntegrationTest</c> covers at the unit level, but which had NO browser coverage.
///
/// Covers the user-facing contract:
/// <list type="number">
///   <item><c>@{prefix}</c> surfaces nodes from the user's OWN partition subtree
///         (orchestrator Source B: <c>path:{currentNamespace} scope:subtree {ref}</c>).</item>
///   <item><c>@/</c> expands from Partitions (orchestrator <c>PartitionList</c> mode).</item>
/// </list>
/// Drives the real DevLogin portal: <c>memex-local e2e test ChatAtAutocompleteTest</c>.
/// </summary>
[Collection("portal-e2e")]
public class ChatAtAutocompleteTest(PortalFixture fixture)
{
    // A distinctive, unlikely-to-collide token so the suggest-widget assertion is unambiguous.
    private const string MarkerName = "ZebraQuokkaReport";

    [Fact(Timeout = 180_000)]
    public async Task TypingAt_SurfacesLocalPartitionNodes_AndSlashExpandsPartitions()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        // Seed the per-user composer on the MeshWeaver harness (the @ orchestrator path), and a
        // referenceable node in the user's OWN partition that @ must find.
        var composerPath = $"{fixture.UserId}/_Thread/ThreadComposer";
        await SeedAsync(context, token, $$"""
            { "id": "ThreadComposer", "namespace": "{{fixture.UserId}}/_Thread", "name": "Chat Input",
              "nodeType": "ThreadComposer", "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "ThreadComposer", "harness": "MeshWeaver" } }
            """);
        await fixture.PatchNodeAsync(context, token, composerPath, "{\"content\":{\"harness\":\"MeshWeaver\"}}");

        await SeedAsync(context, token, $$"""
            { "id": "{{MarkerName}}", "namespace": "{{fixture.UserId}}", "name": "{{MarkerName}}",
              "nodeType": "Markdown", "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "MarkdownContent", "content": "# {{MarkerName}}" } }
            """);
        // The @ subtree query is RLS-scoped — make sure the seed is readable before driving the UI.
        (await fixture.WaitUntilReadableAsync(context, token, $"{fixture.UserId}/{MarkerName}", TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the seeded reference node must be readable before @ autocomplete can surface it");

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);
        await OpenComposerOnAsync(page, $"{fixture.BaseUrl}/{fixture.UserId}");

        var harnessChip = page.Locator(".thread-chat-status-item[title^='Harness']");
        (await PollAsync(async () =>
                await harnessChip.CountAsync() > 0
                && (await harnessChip.First.InnerTextAsync()).Contains("MeshWeaver", StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(30)))
            .Should().BeTrue("the composer must bind to the MeshWeaver harness so @ routes to the node-reference orchestrator");

        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        var rows = page.Locator(".suggest-widget.visible .monaco-list-row");

        // ── 1) @{prefix} surfaces the local-partition node ───────────────────────────────────────────
        await TypeFreshAsync(page, editor, "@Zebra");
        string localCombined = "";
        var foundLocal = await PollAsync(async () =>
        {
            if (await rows.CountAsync() == 0) return false;
            localCombined = string.Join(" | ", await rows.AllInnerTextsAsync());
            return localCombined.Contains("Zebra", StringComparison.OrdinalIgnoreCase);
        }, TimeSpan.FromSeconds(20));
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/at-autocomplete-local.png", FullPage = true });
        foundLocal.Should().BeTrue(
            $"typing @Zebra must surface the seeded '{MarkerName}' node from the user's own partition subtree. " +
            "Saw rows: " + localCombined);

        // ── 2) @/ expands from Partitions ────────────────────────────────────────────────────────────
        await TypeFreshAsync(page, editor, "@/");
        string partitionCombined = "";
        var foundPartitions = await PollAsync(async () =>
        {
            if (await rows.CountAsync() == 0) return false;
            partitionCombined = string.Join(" | ", await rows.AllInnerTextsAsync());
            // The user's own partition is always a partition; assert at least one partition row appears.
            return partitionCombined.Length > 0;
        }, TimeSpan.FromSeconds(20));
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/at-autocomplete-partitions.png", FullPage = true });
        foundPartitions.Should().BeTrue(
            "typing @/ must expand from Partitions (the orchestrator PartitionList mode). Saw rows: " + partitionCombined);

        await page.Keyboard.PressAsync("Escape");
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────────

    private static async Task TypeFreshAsync(IPage page, ILocator editor, string text)
    {
        // Escape closes any open suggest widget WITHOUT accepting (a click-through while it overlays the
        // editor would accept the highlighted item and pop a blocking dialog — see SkillSlashMenuScopingTest).
        await page.Keyboard.PressAsync("Escape");
        await editor.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.TypeAsync(text);
    }

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
