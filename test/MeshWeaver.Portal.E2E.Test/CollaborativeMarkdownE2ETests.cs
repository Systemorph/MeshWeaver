using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Browser click-through tests for the collaborative-markdown view: that an authenticated user can
/// open a markdown doc and add a text-anchored comment, seeing it inline AND in the sidebar.
///
/// These drive the real portal — they Skip unless E2E is enabled (see <see cref="PortalFixture"/>).
/// </summary>
[Collection("portal-e2e")]
public class CollaborativeMarkdownE2ETests(PortalFixture fixture)
{
    private const string DocPath = "Doc/DataMesh/CollaborativeEditing";
    // Plain prose that survives in the cleaned demo doc — the comment's anchor text.
    private const string AnchorText = "satellite entities";

    // Selects <paramref>needle</paramref> inside the content area and raises mouseup, exactly as a
    // real drag-selection would, so the floating "Comment" button appears.
    private const string SelectTextJs = """
        (needle) => {
          const content = document.querySelector('.collab-md-content');
          if (!content) return false;
          const walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT);
          let node;
          while ((node = walker.nextNode())) {
            const idx = node.textContent.indexOf(needle);
            if (idx >= 0) {
              const range = document.createRange();
              range.setStart(node, idx);
              range.setEnd(node, idx + needle.length);
              const sel = window.getSelection();
              sel.removeAllRanges();
              sel.addRange(range);
              content.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
              return true;
            }
          }
          return false;
        }
        """;

    [Fact(Timeout = 120_000)]
    public async Task DocPage_RendersCollaborativeMarkdown_WhenAuthenticated()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();

        await page.GotoAsync($"{fixture.BaseUrl}/{DocPath}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // The collaborative markdown view renders, and we are NOT bounced to the login page.
        page.Url.Should().NotContain("/login");
        await page.Locator(".collab-md-content").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        (await page.Locator(".collab-md-content").InnerTextAsync())
            .Should().Contain(AnchorText, "the cleaned demo doc renders its prose");
    }

    [Fact(Timeout = 120_000)]
    public async Task Comment_AddViaSelection_ShowsInlineHighlightAndSidebarCard()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/{DocPath}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.Locator(".collab-md-content").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        // 1) Select the anchor text → the floating Comment button appears.
        var selected = await page.EvaluateAsync<bool>(SelectTextJs, AnchorText);
        selected.Should().BeTrue("the anchor text should be present and selectable in the rendered doc");

        var commentButton = page.Locator(".comment-selection-btn");
        await commentButton.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
        await commentButton.ClickAsync();

        // 2) The dialog opens; type the comment and submit.
        var commentText = $"e2e-{Guid.NewGuid():N}"[..16];
        var textarea = page.Locator(".comment-input-textarea");
        await textarea.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await textarea.FillAsync(commentText);
        await page.Locator(".comment-btn-submit").ClickAsync();

        // 3) The highlight is rendered INLINE (a comment-highlight span in the content)...
        await page.Locator(".collab-md-content .comment-highlight").First.WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });

        // ...AND a sidebar card carries the comment text.
        var card = page.Locator(".annotation-card.annotation-type-comment",
            new PageLocatorOptions { HasTextString = commentText });
        await card.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        (await card.Locator(".comment-text").InnerTextAsync()).Should().Contain(commentText);

        // Cleanup — delete the comment we created so re-runs stay clean (best effort).
        try
        {
            await card.Locator(".btn-delete").ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
            await card.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 10_000 });
        }
        catch (TimeoutException)
        {
            // Non-fatal: the assertion above already proved the feature works.
        }
    }

    // ---- Tracked changes: seed a suggestion via the API (no GUI creates one), then accept/reject ----

    // A pure insertion at offset 0 — no anchor-text matching needed, so it deterministically renders
    // a track-insert span at the top of the doc with NewText.
    private static string InsertionChangeJson(string id, string newText) =>
        $$"""
        {
          "id": "{{id}}",
          "namespace": "{{DocPath}}/_Tracking",
          "name": "Suggestion (e2e)",
          "nodeType": "TrackedChange",
          "mainNode": "{{DocPath}}",
          "content": {
            "$type": "TrackedChange",
            "id": "{{id}}",
            "primaryNodePath": "{{DocPath}}",
            "markerId": "{{id}}",
            "changeType": "Insertion",
            "author": "e2e",
            "status": "Pending",
            "start": 0,
            "length": 0,
            "newText": "{{newText}}"
          }
        }
        """;

    [Fact(Timeout = 120_000)]
    public async Task Change_Accept_AppliesTheSuggestionToTheDocument()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);
        var changeId = $"e2e{Guid.NewGuid():N}"[..12];
        var inserted = $"E2E_ACCEPT_{changeId}_ ";
        await fixture.CreateNodeAsync(context, token, InsertionChangeJson(changeId, inserted));

        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/{DocPath}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // The diff view renders the suggested insertion + a change card with an Accept button.
        await page.Locator($".collab-md-content .track-insert[data-change-id='{changeId}']").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        var card = page.Locator($".annotation-card.annotation-for-{changeId}");
        await card.Locator(".btn-accept").ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

        // Accepting applies the text to the document and removes the suggestion.
        await page.Locator(".collab-md-content")
            .Filter(new LocatorFilterOptions { HasTextString = inserted.Trim() })
            .WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await card.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 15_000 });
    }

    [Fact(Timeout = 120_000)]
    public async Task Change_Reject_DropsTheSuggestionAndLeavesTheDocument()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var token = await fixture.MintTokenAsync(context);
        var changeId = $"e2e{Guid.NewGuid():N}"[..12];
        var inserted = $"E2E_REJECT_{changeId}_ ";
        await fixture.CreateNodeAsync(context, token, InsertionChangeJson(changeId, inserted));

        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/{DocPath}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        var card = page.Locator($".annotation-card.annotation-for-{changeId}");
        await card.Locator(".btn-reject").ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

        // Rejecting drops the suggestion; the document is left unchanged.
        await card.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Detached, Timeout = 15_000 });
        (await page.Locator(".collab-md-content").InnerTextAsync())
            .Should().NotContain(inserted.Trim(), "a rejected suggestion is never applied to the document");
    }

    // ---- Reply: drive the page-level comments section (the inline sidebar has no reply UI) ----

    [Fact(Timeout = 120_000)]
    public async Task Comment_Reply_AddsAReplyUnderThePageComment()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync($"{fixture.BaseUrl}/{DocPath}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await page.Locator(".collab-md-content").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        // The page-level "+ Add Comment" opens a textarea; submit a page comment.
        var commentText = $"e2e-c-{Guid.NewGuid():N}"[..14];
        await page.GetByText("Add Comment", new PageGetByTextOptions { Exact = false }).First
            .ClickAsync(new LocatorClickOptions { Timeout = 15_000 });
        var pageTextarea = page.Locator(".page-comment-form .comment-input-textarea");
        await pageTextarea.WaitForAsync(new LocatorWaitForOptions { Timeout = 10_000 });
        await pageTextarea.FillAsync(commentText);
        await page.Locator(".page-comment-form .comment-btn-submit").ClickAsync();

        // The comment shows in the bottom comments section; open its reply box, type, and create.
        var replyText = $"e2e-r-{Guid.NewGuid():N}"[..14];
        await page.GetByText(commentText).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "Reply" }).First
            .ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

        // The reply editor is a Monaco instance — focus it and type.
        var editor = page.Locator(".monaco-editor").Last;
        await editor.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
        await editor.ClickAsync();
        await page.Keyboard.TypeAsync(replyText);
        await page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { NameString = "Create" }).First
            .ClickAsync(new LocatorClickOptions { Timeout = 15_000 });

        await page.GetByText(replyText).First.WaitForAsync(new LocatorWaitForOptions { Timeout = 20_000 });
    }
}
