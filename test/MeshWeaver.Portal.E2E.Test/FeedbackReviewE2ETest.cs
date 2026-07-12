using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// End-to-end validation of the private-feedback model on the REAL portal (real PG row-level security):
/// <list type="number">
///   <item>each user files feedback under their OWN partition (<c>{user}/Feedback/{id}</c>);</item>
///   <item>that entry is readable by its author and NOT by another user (self-scope isolation);</item>
///   <item>a PLATFORM ADMIN sees ALL feedback across user partitions in the admin "Feedback" review
///     tab — the System-scoped cross-partition query the InMemory unit tests can't exercise.</item>
/// </list>
/// Two users (alice, bob) each file one entry; Roland (the DevLogin global admin) reviews both.
/// </summary>
[Collection("portal-e2e")]
public class FeedbackReviewE2ETest(PortalFixture fixture)
{
    private const string AliceFeedback = "alice/Feedback/fb-e2e-alice";
    private const string BobFeedback = "bob/Feedback/fb-e2e-bob";

    [Fact(Timeout = 240_000)]
    public async Task Feedback_is_private_per_user_and_admin_sees_all()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        // Admin context FIRST so fixture.UserId resolves to the DevLogin admin (Roland).
        await using var adminCtx = await fixture.NewAuthenticatedContextAsync();
        var adminUserId = fixture.UserId;

        await using var aliceCtx = await fixture.NewAuthenticatedContextAsync("alice");
        var aliceToken = await fixture.MintTokenAsync(aliceCtx);
        await using var bobCtx = await fixture.NewAuthenticatedContextAsync("bob");
        var bobToken = await fixture.MintTokenAsync(bobCtx);

        try
        {
            // ── Seed: each user files ONE feedback entry under their OWN partition ──
            await SeedFeedback(aliceCtx, aliceToken, "alice", "fb-e2e-alice", "Alice E2E feedback", "alice says search is slow");
            await SeedFeedback(bobCtx, bobToken, "bob", "fb-e2e-bob", "Bob E2E feedback", "bob wants dark mode");

            (await fixture.WaitUntilReadableAsync(aliceCtx, aliceToken, AliceFeedback, TimeSpan.FromSeconds(60)))
                .Should().BeTrue("alice must be able to read her own feedback");
            (await fixture.WaitUntilReadableAsync(bobCtx, bobToken, BobFeedback, TimeSpan.FromSeconds(60)))
                .Should().BeTrue("bob must be able to read his own feedback");

            // ── Isolation: neither user can read the other's feedback (private per-user) ──
            (await fixture.CanReadNodeAsync(bobCtx, bobToken, AliceFeedback))
                .Should().BeFalse("another user must NOT be able to read someone else's feedback");
            (await fixture.CanReadNodeAsync(aliceCtx, aliceToken, BobFeedback))
                .Should().BeFalse("and vice-versa");

            // ── Admin review: the global admin sees BOTH across partitions in the tab ──
            // The tab is IsGlobalAdmin-gated (resolves asynchronously, StartWith(none)) AND the grid
            // reads the eventually-consistent cross-schema search index — a freshly-registered user
            // partition takes a moment to become searchable. Reload until BOTH entries surface (each
            // reload is a fresh subscription + query), the same pattern the other admin-tab E2E uses.
            var page = await adminCtx.NewPageAsync();
            await page.SetViewportSizeAsync(1600, 1000);

            var bothVisible = false;
            for (var attempt = 0; attempt < 12 && !bothVisible; attempt++)
            {
                await page.GotoAsync($"{fixture.BaseUrl}/{adminUserId}/Settings/FeedbackReview",
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });
                try
                {
                    // The grid lists BOTH users' entries — proving the System-scoped cross-partition query.
                    await page.GetByText("Alice E2E feedback", new() { Exact = false }).First
                        .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
                    await page.GetByText("Bob E2E feedback", new() { Exact = false }).First
                        .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 10_000 });
                    bothVisible = true;
                }
                catch (TimeoutException) { }
                catch (PlaywrightException) { }
            }
            bothVisible.Should().BeTrue(
                "the global admin must see BOTH users' feedback in the review grid (cross-partition System query)");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/feedback-admin-review.png", FullPage = true });
        }
        finally
        {
            await fixture.DeleteNodeAsync(aliceCtx, aliceToken, AliceFeedback);
            await fixture.DeleteNodeAsync(bobCtx, bobToken, BobFeedback);
        }
    }

    private async Task SeedFeedback(IBrowserContext ctx, string token, string user, string id, string name, string text)
    {
        try
        {
            await fixture.CreateNodeAsync(ctx, token, $$"""
                {
                  "id": "{{id}}", "namespace": "{{user}}/Feedback",
                  "name": "{{name}}", "nodeType": "Feedback",
                  "content": {
                    "$type": "Feedback", "text": "{{text}}",
                    "location": "ACME/Reports", "submittedBy": "{{user}}",
                    "submittedByName": "{{name}}", "status": "New"
                  }
                }
                """);
        }
        catch (InvalidOperationException)
        {
            // Persisted from a prior run — fine.
        }
    }
}
