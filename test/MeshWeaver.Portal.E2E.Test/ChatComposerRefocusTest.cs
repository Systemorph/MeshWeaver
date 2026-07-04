using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Regression test for issue #199: the side-chat composer must keep accepting keyboard
/// input after the user clicks away into the main content area and back. The pre-fix
/// failure mode: the AutoGrow container had no initial height, the inner
/// <c>height: 100% !important</c> chain collapsed Monaco's hit-target to ~one text line,
/// and a click at the visual CENTER of the input box landed on the <c>thread-nav-shell</c>
/// (<c>tabindex="0"</c>, outline suppressed) which silently swallowed every printable
/// keystroke — the input looked active but nothing typed.
///
/// <para>Deliberately clicks the composer's <c>.input-container</c> CENTER (the
/// previously dead zone) — NOT <c>.view-lines</c>, which the other chat tests target and
/// which sat inside the one live band and therefore never caught this. No model round is
/// needed; typing alone exercises the defect. Runs against the DevLogin e2e portal:
/// <c>memex-local e2e up &amp;&amp; memex-local e2e test ChatComposerRefocus</c>.</para>
/// </summary>
[Collection("portal-e2e")]
public class ChatComposerRefocusTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task Composer_AcceptsTyping_AfterClickingAwayAndBack()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 950);
        await page.GotoAsync($"{fixture.BaseUrl}/User/Roland",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Open the side-panel chat.
        var chatToggle = page.Locator(".side-panel-toggle button, .side-panel-toggle fluent-button").First;
        await chatToggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await chatToggle.ClickAsync();

        var inputContainer = page.Locator(".thread-chat-footer .input-container").Last;
        await inputContainer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        var viewLines = page.Locator(".thread-chat-footer .monaco-editor .view-lines").Last;

        // 1. Baseline: click the container CENTER (not .view-lines) and type — must land.
        await inputContainer.ClickAsync();
        await page.Keyboard.TypeAsync("hello");
        await Assertions.Expect(viewLines).ToContainTextAsync("hello",
            new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });

        // 2. Click away into the MAIN content area (left of the side panel) — the exact
        //    step of the report ("click a citation / open a node / scroll the dashboard").
        await page.Mouse.ClickAsync(300, 400);

        // 3. Click BACK into the composer at its CENTER — the pre-fix dead zone — and type.
        await inputContainer.ClickAsync();
        await page.Keyboard.TypeAsync(" again");

        // 4. The keystrokes must appear; pre-fix they were silently swallowed by the
        //    focused nav shell while the input looked active.
        await Assertions.Expect(viewLines).ToContainTextAsync("again",
            new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });
    }
}
