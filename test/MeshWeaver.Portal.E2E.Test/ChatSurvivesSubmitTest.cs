using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Repro + guard for the recurring "the chat window DISAPPEARS when I submit a message" report.
///
/// <para>Submitting the FIRST message creates the thread (<c>StartThread</c>) and, on the
/// thread-created continuation, the home/compact composer navigates to the new thread path
/// (<c>NavigationManager.NavigateTo($"/{path}")</c>). If that reactive continuation has lost the
/// circuit's <c>AccessContext</c>, <c>NavigationService</c> resolves the user as Anonymous and
/// <b>force-loads <c>/login</c></b> — a full page reload that unmounts the whole GUI (the
/// "chat window vanished" symptom). This test pins that: after Send, the user bubble must appear,
/// the URL must NOT become <c>/login</c>, and the composer must still be in the DOM.</para>
///
/// <para>Drives the real DevLogin portal with a model: <c>memex-local e2e test ChatSurvivesSubmitTest</c>.</para>
/// </summary>
[Collection("portal-e2e")]
public class ChatSurvivesSubmitTest(PortalFixture fixture)
{
    // Default (empty) composer → MeshWeaver harness, so a round actually runs against the e2e model.
    private static string ComposerSeedJson(string userId) => $$"""
        { "id": "ThreadComposer", "namespace": "{{userId}}/_Thread", "name": "Chat Input",
          "nodeType": "ThreadComposer", "mainNode": "{{userId}}",
          "content": { "$type": "ThreadComposer" } }
        """;

    [Fact(Timeout = 180_000)]
    public async Task SubmittingFirstMessage_KeepsTheChatVisible_NeverForceLoadsLogin()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);
        try { await fixture.CreateNodeAsync(context, token, ComposerSeedJson(fixture.UserId)); }
        catch (InvalidOperationException) { /* already seeded — fine */ }

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);

        // Open the home composer (compact mode → the onCreated NavigateTo path that can /login-redirect).
        await page.GotoAsync($"{fixture.BaseUrl}/{fixture.UserId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });
        page.Url.Should().NotContain("/login", "we must start authenticated, not on the login page");

        var toggle = page.Locator(".side-panel-toggle button, .side-panel-toggle fluent-button").First;
        await toggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await toggle.ClickAsync();

        var footer = page.Locator(".thread-chat-footer");
        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        // Skip clearly if the target portal has no model (can't run a round / create a real thread).
        var noModel = page.Locator(".thread-chat-status-msg.error");
        if (await noModel.CountAsync() > 0
            && (await noModel.First.InnerTextAsync()).Contains("No language model", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("No language model configured on the target portal — cannot submit a real message.");

        // Type a plain message (NOT a slash command) and Send.
        await editor.ClickAsync();
        await page.Keyboard.TypeAsync("Reply with exactly the word OK.");
        var send = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
        await send.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

        // Race the outcomes after submit:
        //   • user bubble appears + chat still mounted  → CORRECT
        //   • URL became /login (full reload)           → THE BUG ("chat disappeared")
        var userBubble = page.Locator(".thread-msg-bubble.thread-msg-user");
        string outcome = "timeout";
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (page.Url.Contains("/login", StringComparison.OrdinalIgnoreCase)) { outcome = "login-redirect"; break; }
            if (await userBubble.CountAsync() > 0) { outcome = "bubble"; break; }
            await Task.Delay(250);
        }

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/chat-survives-submit.png", FullPage = true });

        page.Url.Should().NotContain("/login",
            "submitting a message must NEVER force-load /login (that full reload IS the 'chat window disappeared' bug)");
        (await footer.CountAsync()).Should().BeGreaterThan(0,
            "the chat composer must still be mounted after submit — it must not disappear");
        outcome.Should().Be("bubble",
            "after Send the user's message bubble must appear in the still-mounted chat (outcome=" + outcome + ")");
    }
}
