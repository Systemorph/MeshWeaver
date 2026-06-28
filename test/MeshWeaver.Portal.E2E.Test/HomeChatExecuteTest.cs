using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// The User Page's home composer is the per-user <c>{user}/Chat</c> node's Overview area, which renders
/// the SAME side-panel <see cref="ThreadChatControl"/> — so it carries every side-panel feature (the
/// message editor, status bar with harness/agent/model, /commands + @-autocomplete, attachments, Send).
/// This test asserts that chrome is present and, when a model is configured, that typing + Send STARTS a
/// thread (no thread yet → StartThread) and the conversation opens.
///
/// <para>NOTE: after the "user home is one editable markdown page" + TextAreaView redesign, the composer's
/// message editor is a <c>FluentTextArea</c> (NOT the old Monaco <c>.monaco-editor</c>), and the home is
/// reached at <c>/User/{id}</c>. Mirrors <see cref="HomeComposerVerifyTest"/>'s setup (seed the per-user
/// ThreadComposer node, then navigate to the user page).</para>
/// </summary>
[Collection("portal-e2e")]
public class HomeChatExecuteTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task HomePage_Composer_HasSidePanelChrome_AndStartsThread()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();

        // The per-user ThreadComposer node is normally seeded at onboarding; a fresh monolith has none,
        // so the composer area would error ("No node found at {user}/_Thread/ThreadComposer"). Seed it,
        // same as HomeComposerVerifyTest.
        var token = await fixture.MintTokenAsync(context);
        try
        {
            await fixture.CreateNodeAsync(context, token, $$"""
                {
                  "id": "ThreadComposer",
                  "namespace": "{{fixture.UserId}}/_Thread",
                  "name": "Chat Input",
                  "nodeType": "ThreadComposer",
                  "mainNode": "{{fixture.UserId}}",
                  "content": { "$type": "ThreadComposer" }
                }
                """);
        }
        catch (InvalidOperationException)
        {
            // Already seeded (persisted from a prior run) — fine.
        }

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);

        // The signed-in user's home page embeds the composer (the {user}/Chat Overview). Reach it at
        // /User/{id} (the post-login landing), matching HomeComposerVerifyTest.
        await page.GotoAsync($"{fixture.BaseUrl}/User/{fixture.UserId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });

        // The home composer IS the side-panel chat view. Assert its tell-tale chrome, all of which only
        // ThreadChatView renders:
        //   • the message editor — a FluentTextArea after the TextAreaView redesign (NOT the old Monaco),
        //   • the .thread-chat-footer (the composer footer),
        //   • the status bar (.thread-chat-status-item — harness / agent / model), and
        //   • the Send button.
        var editor = page.Locator("fluent-text-area textarea, fluent-text-area, .thread-chat-input-content textarea").Last;
        await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 });
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/home-chat-execute.png", FullPage = true });

        (await page.Locator(".thread-chat-footer").CountAsync()).Should().BeGreaterThan(0,
            "the User Page mounts the side-panel composer footer, proving it is the ThreadChatControl");
        (await page.Locator(".thread-chat-status-item").CountAsync()).Should().BeGreaterThan(0,
            "the composer shows the side-panel status bar (harness / agent / model)");
        var sendButton = page.Locator(".thread-chat-input-content fluent-button").Last;
        (await sendButton.CountAsync()).Should().BeGreaterThan(0, "the composer footer has a Send button");

        // Type a real message into the FluentTextArea.
        await editor.ClickAsync();
        await page.Keyboard.TypeAsync("Hello from the home page composer");
        await page.WaitForTimeoutAsync(300);

        // Execute ONLY when a model is configured. With no model the composer shows an explicit notice
        // and Send is gated by design — assert that state and skip execution (rather than false-pass on
        // the typed-but-unsent text still showing in the editor).
        var noModel = await page.GetByText("No language model is available").CountAsync() > 0;
        if (noModel)
        {
            Assert.Skip("Composer renders with full side-panel chrome (footer + status bar + FluentTextArea " +
                        "+ Send), but no language model is configured in this portal — Send is gated, so " +
                        "thread execution is not exercised here.");
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
