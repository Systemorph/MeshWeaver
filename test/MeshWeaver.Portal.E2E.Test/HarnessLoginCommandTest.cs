using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Repro + operate-shell guard for the recurring "harness selection / login is broken" report, for BOTH
/// co-hosted CLI harnesses (Claude Code AND GitHub Copilot). With the composer on a CLI harness, its own
/// <c>/login</c> command must:
/// <list type="number">
///   <item>NOT route <c>login</c> as a node path on completion-accept — i.e. NO
///     <c>"Something went wrong — No node found at 'login'"</c> modal and NO bogus <c>login</c>
///     attachment chip (the screenshot the user keeps reporting); and</item>
///   <item>on submit, OPEN the inline Connect (login) widget — the shell is operable.</item>
/// </list>
/// Drives the real DevLogin portal: <c>memex-local e2e test HarnessLoginCommandTest</c>.
/// </summary>
[Collection("portal-e2e")]
public class HarnessLoginCommandTest(PortalFixture fixture)
{
    [Theory(Timeout = 180_000)]
    [InlineData("ClaudeCode", "Claude Code")]
    [InlineData("Copilot", "GitHub Copilot")]
    public async Task CliHarness_SlashLogin_NoRoutingError_AndOpensConnectWidget(string harnessId, string harnessLabel)
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        // Put the shared composer on the CLI harness — the exact state in the bug screenshot.
        var composerPath = $"{fixture.UserId}/_Thread/ThreadComposer";
        await SeedAsync(context, token, $$"""
            { "id": "ThreadComposer", "namespace": "{{fixture.UserId}}/_Thread", "name": "Chat Input",
              "nodeType": "ThreadComposer", "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "ThreadComposer", "harness": "{{harnessId}}" } }
            """);
        await fixture.PatchNodeAsync(context, token, composerPath, $"{{\"content\":{{\"harness\":\"{harnessId}\"}}}}");

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);
        await OpenComposerOnAsync(page, $"{fixture.BaseUrl}/{fixture.UserId}");

        // The composer must bind to the CLI harness so /login is the harness's own command.
        var harnessChip = page.Locator(".thread-chat-status-item[title^='Harness']");
        (await PollAsync(async () =>
                await harnessChip.CountAsync() > 0
                && (await harnessChip.First.InnerTextAsync()).Contains(harnessId, StringComparison.OrdinalIgnoreCase),
                TimeSpan.FromSeconds(30)))
            .Should().BeTrue($"the composer must bind to the {harnessId} harness");

        // ── 1) Accept the /login completion (Tab) — this is what fired the bug ───────────────────────
        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await page.Keyboard.PressAsync("Escape");
        await editor.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.TypeAsync("/login");
        await page.WaitForTimeoutAsync(700);   // let the suggest widget populate
        await page.Keyboard.PressAsync("Tab"); // accept the highlighted "/login" completion

        var routingError = page.GetByText("No node found", new PageGetByTextOptions { Exact = false });
        var genericError = page.GetByText("Something went wrong", new PageGetByTextOptions { Exact = false });
        var bogusChip = page.Locator(".thread-chat-attachments").GetByText("login", new() { Exact = false });

        var sawError = await PollAsync(async () =>
            await routingError.CountAsync() > 0 || await genericError.CountAsync() > 0,
            TimeSpan.FromSeconds(6));

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = $"/tmp/harness-login-{harnessId}.png", FullPage = true });

        sawError.Should().BeFalse(
            $"accepting /login under {harnessId} must NOT route 'login' as a node path ('No node found at login')");
        (await bogusChip.CountAsync()).Should().Be(0,
            "accepting /login must NOT create a bogus 'login' node-reference attachment chip");

        // ── 2) Submit /login — the inline Connect (login) widget must open (shell is operable) ───────
        var send = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
        await send.ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

        var connectWidget = page.GetByText($"Log in to {harnessLabel}", new PageGetByTextOptions { Exact = false });
        var opened = await PollAsync(async () => await connectWidget.CountAsync() > 0, TimeSpan.FromSeconds(20));

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = $"/tmp/harness-login-{harnessId}-connect.png", FullPage = true });

        opened.Should().BeTrue(
            $"submitting /login under {harnessId} must open the inline Connect widget ('Log in to {harnessLabel}') — the shell login flow is operable");
        (await routingError.CountAsync()).Should().Be(0, "submitting /login must never produce the 'No node found' routing modal");
    }

    // ── helpers (mirroring ChatComposerSwitchSelectionTest) ─────────────────────────────────────────

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
