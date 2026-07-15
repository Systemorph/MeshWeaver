using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Repro for the user report: "in Safari I keep getting situations where I cannot input anything in
/// controls — I get a blinking cursor but typing has no effect, and I have to reload the page." It tends
/// to happen while a thread is executing and messages stream, after another control takes focus and the
/// user clicks back into the composer.
///
/// <para>The existing <see cref="ChatComposerStreamingFocusTest"/> already types mid-stream and asserts
/// it lands — but the fixture only ever launched <b>Chromium</b>, so the Safari-only failure was never
/// exercised. This test drives the SAME flow but is meant to run under BOTH engines
/// (<c>E2E_BROWSER=chromium</c> vs <c>E2E_BROWSER=webkit</c>) so we can prove the divergence: it steals
/// focus to a non-composer control mid-stream, clicks back, types, and requires the text to land in
/// Monaco's own DOM (<c>.view-lines</c>). On failure it dumps the discriminating state — the REAL
/// <c>document.activeElement</c> vs what Monaco THINKS (<c>editor.hasTextFocus()</c>) — so we can tell a
/// focus desync (A) from a reconcile-wipe (B).</para>
/// </summary>
[Collection("portal-e2e")]
public class ChatComposerSafariFocusReproTest(PortalFixture fixture, ITestOutputHelper output)
{
    private string ComposerSeedJson => $$"""
        {
          "id": "ThreadComposer",
          "namespace": "{{fixture.UserId}}/_Thread",
          "name": "Chat Input",
          "nodeType": "ThreadComposer",
          "mainNode": "{{fixture.UserId}}",
          "content": { "$type": "ThreadComposer" }
        }
        """;

    private static readonly string ModelPath =
        Environment.GetEnvironmentVariable("E2E_MODEL") ?? "Provider/OpenAICompatible/qwen-small";

    private const int PromptBubbleMs = 20_000;
    private const int SettleMs = 180_000;

    // Discriminating probe: the REAL keyboard focus vs Monaco's internal belief + editor/view identity.
    private const string DiagJs = """
        () => {
          const ae = document.activeElement;
          const mon = document.querySelector('.thread-chat-footer .monaco-editor');
          const ta = document.querySelector('.thread-chat-footer textarea.inputarea');
          let monacoThinksFocused = null;
          try { monacoThinksFocused = (window.monaco?.editor?.getEditors?.() || []).map(e => e.hasTextFocus()); }
          catch { /* monaco global may not be exposed */ }
          const idEl = document.querySelector(".thread-chat-footer [id^='monaco-editor-']");
          const viewEl = document.querySelector('.thread-chat-container');
          return {
            activeElement: ae ? (ae.className || ae.tagName) : null,
            focusInsideComposer: !!(mon && ae && mon.contains(ae)),
            realTextareaIsActive: !!(ta && ae === ta),
            monacoHasTextFocus: monacoThinksFocused,
            editorId: idEl ? idEl.id : null,
            viewInstance: viewEl ? viewEl.getAttribute('data-instance') : null,
            composerText: (document.querySelector('.thread-chat-footer .monaco-editor .view-lines')?.innerText) ?? null
          };
        }
        """;

    [Fact(Timeout = 420_000)]
    public async Task Composer_AcceptsTyping_AfterFocusStealDuringStream()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        output.WriteLine($"engine = {fixture.BrowserName}");

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);

        try { await fixture.CreateNodeAsync(context, token, ComposerSeedJson); }
        catch (InvalidOperationException) { /* already exists — fine */ }
        await context.APIRequest.PostAsync($"{fixture.BaseUrl}/api/mesh/patch", new APIRequestContextOptions
        {
            Headers = new Dictionary<string, string> { ["authorization"] = $"Bearer {token}" },
            DataObject = new
            {
                Path = $"{fixture.UserId}/_Thread/ThreadComposer",
                // agentName = a TOOL-LESS built-in agent (no plugins) so the round sends NO tool
                // definitions — otherwise the local Ollama models reject tools with HTTP 400 and the
                // round never streams. With no tools any model streams; a plain prose answer is exactly
                // the sustained token storm we need.
                Fields = "{\"content\":{\"$type\":\"ThreadComposer\",\"harness\":\"Harness/MeshWeaver\","
                       + "\"agentName\":\"Agent/DescriptionWriter\","
                       + "\"modelName\":\"" + ModelPath + "\",\"contextPath\":\"" + fixture.UserId + "\"}}"
            }
        });

        var chatPageId = "e2e-sfx-" + Guid.NewGuid().ToString("N")[..8];
        await fixture.CreateNodeAsync(context, token, $$"""
            {
              "id": "{{chatPageId}}",
              "namespace": "{{fixture.UserId}}",
              "name": "E2E Safari Focus Chat Page",
              "nodeType": "Markdown",
              "content": { "$type": "MarkdownContent", "content": "# E2E safari focus chat page" }
            }
            """);

        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1400, 950);
        await page.GotoAsync($"{fixture.BaseUrl}/{fixture.UserId}/{chatPageId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var chatToggle = page.Locator(".side-panel-toggle button, .side-panel-toggle fluent-button").First;
        await chatToggle.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await chatToggle.ClickAsync();

        var composer = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await composer.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        var noModel = page.Locator(".thread-chat-status-msg.error");
        if (await noModel.CountAsync() > 0
            && (await noModel.First.InnerTextAsync()).Contains("No language model", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("No language model configured on the target portal — cannot exercise a streaming round.");

        var userBubbles = page.Locator(".thread-msg-bubble.thread-msg-user");
        var execBar = page.Locator(".thread-exec-bar");
        var send = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
        var viewLines = page.Locator(".thread-chat-footer .monaco-editor .view-lines").Last;

        // ── Round 1: create the thread, let it settle. ──
        await composer.ClickAsync();
        await page.Keyboard.TypeAsync("Say hello in one short sentence.");
        await send.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });
        await userBubbles.Nth(0).WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = PromptBubbleMs });
        await execBar.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = SettleMs });

        // ── Round 2: submit an answer that streams for a WHILE, so the re-render storm coincides with our
        //    focus-steal/type cycles. The tiny model is fast, so ask for a lot of output. ──
        await composer.ClickAsync();
        await page.Keyboard.TypeAsync(
            "Write a numbered list from 1 to 150, one number per line, each with a full sentence. "
            + "Do not stop early; produce all 150 lines.");
        await page.Keyboard.PressAsync("Enter");
        await userBubbles.Nth(1).WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = PromptBubbleMs });

        // JS: steal keyboard focus to a REAL out-of-composer control (the nav shell is tabindex=0 — the
        // exact element issue #199 warns swallows keystrokes — else a toolbar/footer button).
        const string stealFocusJs = """
            () => {
              const ae = document.activeElement; if (ae && ae.blur) ae.blur();
              const outside =
                document.querySelector('.thread-nav-shell')
                || document.querySelector('.thread-nav-new, .thread-nav-cycle')
                || document.querySelector('.thread-chat-footer .selector-bar button, .thread-chat-footer .selector-bar fluent-button')
                || document.querySelector('button, fluent-button, a[href]');
              if (outside && outside.focus) outside.focus();
              return document.activeElement ? (document.activeElement.className || document.activeElement.tagName) : null;
            }
            """;

        // ── The reported trigger, exercised REPEATEDLY across the whole streaming window: while messages
        //    stream, another control takes focus, the user clicks back into the composer and types. Each
        //    cycle we require the text to (a) land AND (b) SURVIVE the next second of re-render storm (a
        //    reconcile-wipe removes it a beat later; a focus desync stops it landing at all). ──
        var failures = new List<string>();
        var streamingSeen = false;
        var cycles = 0;
        var overall = System.Diagnostics.Stopwatch.StartNew();
        while (overall.ElapsedMilliseconds < 90_000)
        {
            var execVisible = await execBar.CountAsync() > 0 && await execBar.First.IsVisibleAsync();
            if (execVisible) streamingSeen = true;
            else if (streamingSeen) break; // round finished — stop looping
            if (!execVisible) { await Task.Delay(100); continue; } // not streaming yet — wait for the round to start

            cycles++;
            var marker = $"rf{cycles:D2}";

            // Steal focus away, then click back and type as tightly as possible so a re-render is likely
            // to land in the click→type→settle window (where the reconcile/focus race lives).
            var stolenTo = await page.EvaluateAsync<string?>(stealFocusJs);
            await Task.Delay(120); // brief — a couple of streaming re-renders land while focus is away
            await composer.ClickAsync();                 // user clicks back into the composer
            await page.Keyboard.TypeAsync(marker);

            // (a) did it land at all? short poll.
            var landed = false;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 1_500)
            {
                if ((await viewLines.InnerTextAsync()).Contains(marker, StringComparison.Ordinal)) { landed = true; break; }
                await Task.Delay(50);
            }
            // (b) does it SURVIVE the next burst of re-render storm (reconcile-wipe check)?
            await Task.Delay(500);
            var survived = (await viewLines.InnerTextAsync()).Contains(marker, StringComparison.Ordinal);

            if (!landed || !survived)
            {
                var diag = await page.EvaluateAsync<System.Text.Json.JsonElement>(DiagJs);
                output.WriteLine($"[{fixture.BrowserName}] cycle {cycles} exec={execVisible} stolenTo={stolenTo} " +
                                 $"landed={landed} survived={survived} diag={diag}");
                if (!landed) failures.Add($"cycle {cycles}: '{marker}' NEVER landed (focus desync). diag={diag}");
                else failures.Add($"cycle {cycles}: '{marker}' landed then VANISHED (reconcile-wipe). diag={diag}");
            }

            // clear the composer for the next cycle so markers don't accumulate.
            await page.Keyboard.PressAsync("Meta+A");
            await page.Keyboard.PressAsync("Delete");
        }

        output.WriteLine($"[{fixture.BrowserName}] streamingSeen={streamingSeen} cycles={cycles} failures={failures.Count}");
        // Environmental precondition, not a product assertion: without a sustained streaming round the
        // storm never occurs, so there's nothing to reproduce. On local Ollama this needs a tool-less
        // agent + a tool-capable model that actually streams for several seconds (E2E_MODEL / agent
        // wiring). Skip rather than red-fail so the harness stays green when the model can't stream.
        Assert.SkipUnless(streamingSeen,
            "Round 2 never entered a sustained streaming state (model finished too fast or the round errored) — " +
            "cannot exercise the refocus-during-stream scenario.");
        Assert.True(failures.Count == 0,
            $"[{fixture.BrowserName}] Composer became untypeable after a mid-stream focus-steal + click-back " +
            $"(the reported 'blinking cursor, typing has no effect' state) in {failures.Count}/{cycles} cycles:\n"
            + string.Join("\n", failures));
    }
}
