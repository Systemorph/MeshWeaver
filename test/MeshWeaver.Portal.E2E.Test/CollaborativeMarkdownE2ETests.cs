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
}
