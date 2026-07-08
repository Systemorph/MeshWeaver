using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Regression test: the chat composer must KEEP keyboard focus while an assistant response streams,
/// so the user can type the next message without clicking back in.
///
/// <para>Root cause it guards (fixed in <c>NamedAreaView</c>): when a round started executing, the
/// thread area's child control stream momentarily emitted a null (the "round-N vanish" transient).
/// <c>NamedAreaView</c> cleared <c>RootControl</c> on that null, flipping its
/// <c>@if (RootControl != null)</c> to false — DISPOSING the child <c>DispatchView</c> and the whole
/// <c>ThreadChatView</c>, which was recreated a frame later. The Monaco editor was destroyed and
/// rebuilt mid-stream, so focus was lost every time a response streamed. The fix keeps the last-good
/// control on a transient null.</para>
///
/// <para>The test submits round 2 in an EXISTING thread via Enter (focus stays in Monaco), then across
/// a fixed streaming window asserts three invariants: the composer's Monaco DOM id is stable (editor
/// not recreated), the ThreadChatView instance id is stable (view not remounted), and focus never
/// leaves the composer. Runs against the DevLogin e2e portal:
/// <c>memex-local e2e up &amp;&amp; memex-local e2e test ChatComposerStreamingFocus</c>.</para>
/// </summary>
[Collection("portal-e2e")]
public class ChatComposerStreamingFocusTest(PortalFixture fixture)
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

    // Watch the streaming window long enough to comfortably cover model-start (cold qwen-small) + the
    // IsExecuting flip (~2s) that used to trigger the remount.
    private const int StreamingWatchMs = 12_000;
    private const int SampleEveryMs = 100;

    // JS: is keyboard focus currently inside the composer's Monaco editor (in the footer)?
    private const string FocusInComposerJs = """
        () => {
          const ae = document.activeElement;
          const mon = document.querySelector('.thread-chat-footer .monaco-editor');
          return !!(mon && ae && mon.contains(ae));
        }
        """;

    // JS: the composer Monaco container id (monaco-editor-{guid}); changes iff the editor is recreated.
    private const string ComposerEditorIdJs = """
        () => {
          const el = document.querySelector(".thread-chat-footer [id^='monaco-editor-']");
          return el ? el.id : null;
        }
        """;

    // JS: the ThreadChatView instance id stamped on .thread-chat-container; changes iff the WHOLE
    // view remounts.
    private const string ViewInstanceJs = """
        () => {
          const el = document.querySelector('.thread-chat-container');
          return el ? el.getAttribute('data-instance') : null;
        }
        """;

    [Fact(Timeout = 420_000)]
    public async Task Composer_KeepsFocus_WhileResponseStreams()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

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
                Fields = "{\"content\":{\"$type\":\"ThreadComposer\",\"harness\":\"Harness/MeshWeaver\","
                       + "\"modelName\":\"" + ModelPath + "\",\"contextPath\":\"" + fixture.UserId + "\"}}"
            }
        });

        var chatPageId = "e2e-focus-" + Guid.NewGuid().ToString("N")[..8];
        await fixture.CreateNodeAsync(context, token, $$"""
            {
              "id": "{{chatPageId}}",
              "namespace": "{{fixture.UserId}}",
              "name": "E2E Focus Chat Page",
              "nodeType": "Markdown",
              "content": { "$type": "MarkdownContent", "content": "# E2E focus chat page" }
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

        // ── Round 1: create the thread and let it settle (its focus behaviour is a separate case). ──
        await composer.ClickAsync();
        await page.Keyboard.TypeAsync("Say hello in one short sentence.");
        await send.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });
        await userBubbles.Nth(0).WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = PromptBubbleMs });
        await execBar.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = SettleMs });

        // ── Round 2: in the now-EXISTING thread, focus the composer and submit via ENTER (focus stays
        //    in Monaco). Then across the streaming window require the editor id AND the view instance id
        //    to stay constant (no recreate / no remount) and focus to never leave the composer. ───────
        var editorIdBefore = await page.EvaluateAsync<string?>(ComposerEditorIdJs);
        var viewInstanceBefore = await page.EvaluateAsync<string?>(ViewInstanceJs);
        await composer.ClickAsync();
        await page.Keyboard.TypeAsync(
            "Count from 1 to 40, one number per line, and write a short sentence about each number.");
        await page.Keyboard.PressAsync("Enter");

        await userBubbles.Nth(1).WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = PromptBubbleMs });

        var samples = 0;
        var focusLostSamples = 0;
        var idChanges = 0;
        var viewChanges = 0;
        var lastId = editorIdBefore;
        var lastView = viewInstanceBefore;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < StreamingWatchMs)
        {
            samples++;
            var id = await page.EvaluateAsync<string?>(ComposerEditorIdJs);
            if (id is not null && id != lastId) { idChanges++; lastId = id; }
            var view = await page.EvaluateAsync<string?>(ViewInstanceJs);
            if (view is not null && view != lastView) { viewChanges++; lastView = view; }
            if (!await page.EvaluateAsync<bool>(FocusInComposerJs)) focusLostSamples++;
            await Task.Delay(SampleEveryMs);
        }

        // Live proof the composer stays usable mid-stream: type and require it to land.
        await composer.ClickAsync();
        await page.Keyboard.TypeAsync("draft-while-streaming");
        var viewLines = page.Locator(".thread-chat-footer .monaco-editor .view-lines").Last;
        await Assertions.Expect(viewLines).ToContainTextAsync("draft-while-streaming",
            new LocatorAssertionsToContainTextOptions { Timeout = 10_000 });

        Assert.True(idChanges == 0 && viewChanges == 0 && focusLostSamples == 0,
            $"composer was disrupted while streaming: {focusLostSamples}/{samples} samples lost focus; " +
            $"editor recreated {idChanges}× ({editorIdBefore} → {lastId}); " +
            $"view remounted {viewChanges}× ({viewInstanceBefore} → {lastView}).");
    }
}
