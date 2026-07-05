using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Regression guard for the recurring "chat popup renders BEHIND the sticky top bar" bug. The <c>/login</c>
/// dialog — and the <c>/agent</c> · <c>/model</c> pickers, same <c>.thread-chat-widget</c> — must paint
/// ABOVE the header. The popup is <c>position:fixed</c> (z-index 10000) so it escapes BOTH the chat's
/// overflow clip AND the header's stacking context (z-index 1100); a z-index-only fix never worked because
/// <c>.body-content { z-index: 1 }</c> trapped it in a lower stacking context and <c>overflow</c> clipped
/// the header band. This asserts BY RENDERING: <c>elementFromPoint</c> over the header returns the dialog.
/// Drives the real DevLogin portal: <c>memex-local e2e test LoginDialogAboveHeaderTest</c>.
/// </summary>
[Collection("portal-e2e")]
public class LoginDialogAboveHeaderTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task LoginDialog_RendersAboveTheStickyHeader()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        // The composer must be on the ClaudeCode harness so /login opens the harness LOGIN dialog rather
        // than resolving as a MeshWeaver skill.
        var composerPath = $"{fixture.UserId}/_Thread/ThreadComposer";
        try
        {
            await fixture.CreateNodeAsync(context, token, $$"""
                { "id": "ThreadComposer", "namespace": "{{fixture.UserId}}/_Thread", "name": "Chat Input",
                  "nodeType": "ThreadComposer", "mainNode": "{{fixture.UserId}}",
                  "content": { "$type": "ThreadComposer", "harness": "ClaudeCode" } }
                """);
        }
        catch (InvalidOperationException) { /* already seeded — fine */ }
        await fixture.PatchNodeAsync(context, token, composerPath, "{\"content\":{\"harness\":\"ClaudeCode\"}}");

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 800);
        await page.GotoAsync($"{fixture.BaseUrl}/{fixture.UserId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });
        page.Url.Should().NotContain("/login");

        // Open the composer (side panel) + focus its Monaco editor.
        var toggle = page.Locator(".side-panel-toggle button, .side-panel-toggle fluent-button").First;
        await toggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await toggle.ClickAsync();
        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await page.WaitForTimeoutAsync(800);

        // Type /login and submit — Escape first dismisses Monaco's command-autocomplete so Enter submits
        // the message (which routes to the harness Connect command → OpenLoginDialog) instead of accepting
        // a completion.
        await editor.ClickAsync();
        await page.Keyboard.TypeAsync("/login");
        await page.WaitForTimeoutAsync(400);
        await page.Keyboard.PressAsync("Escape");
        await page.WaitForTimeoutAsync(200);
        await page.Keyboard.PressAsync("Enter");

        var widget = page.Locator(".thread-chat-widget");
        await widget.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 20_000 });
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/login-dialog-above-header.png" });

        // Assert BY RENDERING: at a point inside the sticky header's band AND the dialog's horizontal span,
        // the topmost painted element is the dialog (not the header). Also assert it is position:fixed.
        var verdict = await page.EvaluateAsync<Dictionary<string, object>>(@"() => {
            const w = document.querySelector('.thread-chat-widget');
            const hdr = document.querySelector('.layout > header, header');
            if (!w || !hdr) return { found: false };
            const cs = getComputedStyle(w), wr = w.getBoundingClientRect(), hr = hdr.getBoundingClientRect();
            let overHeader = null;
            if (wr.top < hr.bottom) {
                const px = Math.round((Math.max(wr.left, hr.left) + Math.min(wr.right, hr.right)) / 2);
                const py = Math.round(hr.top + hr.height / 2);
                const el = document.elementFromPoint(px, py);
                overHeader = !!(el && el.closest('.thread-chat-widget'));
            }
            return { found: true, position: cs.position, overlapsHeader: wr.top < hr.bottom, popupAboveHeader: overHeader };
        }");

        verdict.Should().ContainKey("found");
        verdict["found"].Should().Be(true, "the /login dialog (.thread-chat-widget) must open");
        verdict["position"].Should().Be("fixed", "the popup must be position:fixed to escape the chat overflow clip + stacking trap");
        // When the popup overlaps the header band, the topmost element there MUST be the dialog, not the header.
        if (verdict.TryGetValue("overlapsHeader", out var ov) && ov is true)
            verdict["popupAboveHeader"].Should().Be(true, "the login dialog must render ABOVE the sticky header, not behind it");
    }
}
