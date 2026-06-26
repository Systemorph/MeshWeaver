using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// The User Page's home composer is the per-user <c>{user}/Chat</c> node's Overview area, which renders
/// the SAME side-panel <see cref="ThreadChatControl"/> — so it carries every side-panel feature (Monaco
/// editor, status bar with harness/agent/model, /commands + @-autocomplete, attachments, Send). This
/// test asserts that chrome is present and, when a model is configured, that typing + Send STARTS a
/// thread (no thread yet → StartThread) and the conversation opens.
/// </summary>
[Collection("portal-e2e")]
public class HomeChatExecuteTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task HomePage_Composer_HasSidePanelChrome_AndStartsThread()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);

        await page.GotoAsync($"{fixture.BaseUrl}/User/Roland",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });

        // The home composer IS the side-panel chat view. Assert its tell-tale chrome, all of which only
        // ThreadChatView renders:
        //   • the Monaco editor (rich input — NOT the old plain textarea),
        //   • the .thread-chat-footer (the composer footer),
        //   • the status bar (.thread-chat-status-item — harness / agent / model), and
        //   • the Send button.
        var editor = page.Locator(".monaco-editor").Last;
        await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 });
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/home-chat-execute.png", FullPage = true });

        (await page.Locator(".thread-chat-footer").CountAsync()).Should().BeGreaterThan(0,
            "the User Page mounts the side-panel composer footer, proving it is the ThreadChatControl");
        (await page.Locator(".thread-chat-status-item").CountAsync()).Should().BeGreaterThan(0,
            "the composer shows the side-panel status bar (harness / agent / model)");
        var sendButton = page.Locator(".thread-chat-input-content fluent-button").Last;
        (await sendButton.CountAsync()).Should().BeGreaterThan(0, "the composer footer has a Send button");

        // Slash-command + @-autocomplete: typing "/" opens a suggestion surface (Monaco suggest widget
        // or the inline node picker). Best-effort — recorded, then cleared.
        await editor.ClickAsync();
        await page.Keyboard.TypeAsync("/");
        await page.WaitForTimeoutAsync(900);
        var autocompleteShown = await page.Locator(
            ".monaco-editor .suggest-widget.visible, .editor-widget.suggest-widget, [class*='picker']").CountAsync() > 0;
        await page.Keyboard.PressAsync("Escape");
        await page.Keyboard.PressAsync("Backspace");

        // Type a real message.
        await editor.ClickAsync();
        await page.Keyboard.TypeAsync("Hello from the home page composer");
        await page.WaitForTimeoutAsync(300);

        // Execute ONLY when a model is configured. With no model the composer shows an explicit notice
        // and Send is gated by design — assert that state and skip execution (rather than false-pass on
        // the typed-but-unsent text still showing in the editor).
        var noModel = await page.GetByText("No language model is available").CountAsync() > 0;
        if (noModel)
        {
            Assert.Skip("Composer renders with full side-panel chrome (footer + status bar + Monaco + Send; " +
                        $"autocomplete={autocompleteShown}), but no language model is configured in this portal — " +
                        "Send is gated, so thread execution is not exercised here.");
            return;
        }

        await sendButton.ClickAsync();

        // Sending with no thread starts one: the composer EMPTIES and the message appears as a rendered
        // bubble (.thread-msg…) — assert the bubble, not the editor text, so this can't false-pass.
        await page.Locator(".thread-msg, .thread-allocating-submitted-text")
            .GetByText("Hello from the home page composer").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/home-chat-thread.png", FullPage = true });
    }
}
