using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Guards the harness DEFAULT: a brand-new composer must default to the MeshWeaver harness (the native
/// one, <c>Order = 0</c>), NOT an arbitrary CLI harness. This is the root cause behind "certain users got
/// no auto-expand on skills": a composer stuck on a CLI harness (Claude Code / Copilot) shows the
/// harness's own slash-commands in the <c>/</c> menu, never the <c>nodeType:Skill</c> catalog
/// (ThreadChatView.GetCommandCompletions). The fix stamps <c>Order</c> onto the harness MeshNode
/// (BuiltInHarnessProvider) so <c>ObserveDefaultComposer</c>'s <c>OrderBy(n =&gt; n.Order ?? 0)</c> can
/// pick MeshWeaver instead of falling through to whatever the registry returns first.
///
/// <para>Removes the chatting user's composer and waits for the delete to propagate, so the chat
/// recreates it from the order-resolved defaults on next open. Run:
/// <c>memex-local e2e test DefaultHarnessIsMeshWeaverTest</c>.</para>
/// </summary>
[Collection("portal-e2e")]
public class DefaultHarnessIsMeshWeaverTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task FreshComposer_DefaultsToMeshWeaverHarness_SoTheSlashMenuShowsSkills()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        // Remove the per-user composer and WAIT for the delete to propagate before reopening — otherwise
        // EnsureComposer's create races the delete and reads a half-deleted node (no harness → no chip).
        var composerPath = $"{fixture.UserId}/_Thread/ThreadComposer";
        await fixture.DeleteNodeAsync(context, token, composerPath);
        await PollAsync(async () => !await fixture.CanReadNodeAsync(context, token, composerPath),
            TimeSpan.FromSeconds(15));

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);
        await page.GotoAsync($"{fixture.BaseUrl}/{fixture.UserId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });
        page.Url.Should().NotContain("/login");

        // Open the side-panel chat → EnsureComposer creates the composer with the default harness.
        var toggle = page.Locator(".side-panel-toggle button, .side-panel-toggle fluent-button").First;
        await toggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await toggle.ClickAsync();
        await page.Locator(".thread-chat-footer .monaco-editor").Last
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        // The read-only Harness status chip must resolve to MeshWeaver (the chip text is the harness id's
        // last segment). Poll: the chip renders reactively after the default-fill write lands.
        var harnessChip = page.Locator(".thread-chat-status-item[title^='Harness']");
        var isMeshWeaver = await PollAsync(async () =>
            await harnessChip.CountAsync() > 0
            && (await harnessChip.First.InnerTextAsync()).Contains("MeshWeaver", StringComparison.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(40));

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/default-harness.png", FullPage = true });
        isMeshWeaver.Should().BeTrue(
            "a fresh composer must default to the MeshWeaver harness so the / menu surfaces the nodeType:Skill " +
            "catalog; defaulting to a CLI harness hides skills (the atioz 'no skills auto-expand' root cause)");
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
}
