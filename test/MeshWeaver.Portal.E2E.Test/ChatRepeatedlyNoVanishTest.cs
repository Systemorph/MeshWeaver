using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Repro for the recurring "thread vanishes / GUI disappears after the 2nd–3rd new chat" render storm
/// (reported ~100×). Submits SEVERAL messages in a row — some WHILE the previous round is still
/// streaming — and asserts after each that the chat is still mounted: the composer + container are
/// present, the circuit did NOT drop (no "reconnect" overlay), and the page did NOT bounce to /login.
/// A render-storm wedge tears the circuit down / blanks the panel, so those invariants break.
///
/// <para>Drives the real DevLogin e2e portal (host Ollama provides the model so Send actually executes):
/// <c>memex-local e2e up &amp;&amp; memex-local e2e test ChatRepeatedlyNoVanish</c>.</para>
/// </summary>
[Collection("portal-e2e")]
public class ChatRepeatedlyNoVanishTest(PortalFixture fixture)
{
    [Fact(Timeout = 360_000)]
    public async Task Chatting_FiveTimes_DoesNotVanishTheThread()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        // Seed the per-user composer (MeshWeaver harness → host Ollama model).
        await SeedAsync(context, token, $$"""
            { "id": "ThreadComposer", "namespace": "{{fixture.UserId}}/_Thread", "name": "Chat Input",
              "nodeType": "ThreadComposer", "mainNode": "{{fixture.UserId}}",
              "content": { "$type": "ThreadComposer", "harness": "MeshWeaver" } }
            """);

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 1000);
        await page.GotoAsync($"{fixture.BaseUrl}/User/{fixture.UserId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });
        page.Url.Should().NotContain("/login");

        var editor = page.Locator(".thread-chat-footer .monaco-editor, .thread-chat-footer textarea").Last;
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 60_000 });

        if (await page.GetByText("No language model is available").CountAsync() > 0)
            Assert.Skip("No language model configured — Send is gated; storm not exercised.");

        var send = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
        var container = page.Locator(".thread-chat-container");

        // Fire 6 messages; on the even ones do NOT wait for completion (submit while the prior round is
        // still streaming) — that's the "sometimes while it is running" case.
        for (var i = 1; i <= 6; i++)
        {
            await TypeAsync(page, editor, $"Reply with just the number {i}.");
            await send.ClickAsync(new() { Timeout = 15_000 });

            if (i % 2 == 1)
                // wait for the round to land an assistant bubble before the next send
                await PollAsync(async () => await page.Locator(".thread-msg-bubble").CountAsync() >= i + 1,
                    TimeSpan.FromSeconds(90));
            else
                await page.WaitForTimeoutAsync(1200);   // submit again mid-stream

            // INVARIANTS — the chat must still be mounted and the circuit alive after every send.
            page.Url.Should().NotContain("/login", $"login bounce after message {i} = the vanish");
            (await container.CountAsync()).Should().BeGreaterThan(0, $"chat container vanished after message {i}");
            (await page.Locator(".components-reconnect-show, .reconnect-overlay, #components-reconnect-modal")
                    .CountAsync())
                .Should().Be(0, $"the Blazor circuit dropped (reconnect overlay) after message {i} — the render storm");
        }

        // Settle, then confirm it's still alive (a storm keeps re-rendering and eventually tears down).
        await page.WaitForTimeoutAsync(4000);
        await page.ScreenshotAsync(new() { Path = "/tmp/chat-repeat.png", FullPage = true });
        (await container.IsVisibleAsync()).Should().BeTrue("the chat must remain mounted after 6 rounds");
        page.Url.Should().NotContain("/login");
    }

    private static async Task TypeAsync(IPage page, ILocator editor, string text)
    {
        await page.Keyboard.PressAsync("Escape");
        await editor.ClickAsync();
        await page.Keyboard.PressAsync("ControlOrMeta+A");
        await page.Keyboard.PressAsync("Backspace");
        await page.Keyboard.TypeAsync(text);
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
        catch (InvalidOperationException) { /* already seeded */ }
    }
}
