using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeshWeaver.GitSync;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// The GitHub webhook receiver: HMAC signature verification (pure) and payload-driven,
/// token-free upsert of <c>{space}/_Issue/{number}</c> nodes for every Space that syncs the
/// event's repository.
/// </summary>
public class GitHubWebhookProcessorTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    [Fact]
    public void VerifySignature_MatchesGitHubHmac_AndRejectsForgeries()
    {
        const string secret = "s3cr3t";
        var body = Encoding.UTF8.GetBytes("{\"hello\":\"world\"}");
        var sig = "sha256=" + Convert.ToHexString(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), body)).ToLowerInvariant();

        Assert.True(GitHubWebhookProcessor.VerifySignature(secret, body, sig));
        Assert.True(GitHubWebhookProcessor.VerifySignature(secret, body, sig.ToUpperInvariant())); // hex case-insensitive
        Assert.False(GitHubWebhookProcessor.VerifySignature(secret, body, "sha256=deadbeef"));
        Assert.False(GitHubWebhookProcessor.VerifySignature("wrong-secret", body, sig));
        Assert.False(GitHubWebhookProcessor.VerifySignature(secret, body, null));
        Assert.False(GitHubWebhookProcessor.VerifySignature("", body, sig));
        Assert.False(GitHubWebhookProcessor.VerifySignature(secret, body, "md5=whatever"));
    }

    [Fact(Timeout = 120000)]
    public async Task Process_IssuesEvent_UpsertsIssueNodeInMatchingSpace()
    {
        await Connect();
        var space = "GhWo" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Webhook Open Space");
        var repo = "https://github.com/test/webhook-open";
        await Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        var payload = JsonDocument.Parse($$"""
        {
          "action": "opened",
          "repository": { "full_name": "test/webhook-open" },
          "issue": {
            "number": 42, "title": "Webhook issue", "body": "from hook", "state": "open",
            "user": { "login": "octocat" },
            "labels": [ { "name": "bug" } ], "assignees": [],
            "comments": 0, "html_url": "https://github.com/test/webhook-open/issues/42",
            "created_at": "2026-07-04T10:00:00Z", "updated_at": "2026-07-04T10:00:00Z"
          }
        }
        """).RootElement;

        var updated = await Webhooks.Process("issues", payload).Timeout(60.Seconds()).ToTask();
        Assert.Equal(1, updated);

        var issue = await WaitForIssue(IssueService.IssuePath(space, 42), i => i.Number == 42);
        Assert.Equal("Webhook issue", issue.Title);
        Assert.Equal(GitHubIssueState.Open, issue.State);
        Assert.Equal("octocat", issue.AuthorLogin);
        Assert.Contains("bug", issue.Labels);
    }

    [Fact(Timeout = 120000)]
    public async Task Process_IssueCommentEvent_MergesCommentOntoExistingNode()
    {
        await Connect();
        var space = "GhWc" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Webhook Comment Space");
        var repo = "https://github.com/test/webhook-comment";
        await Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        // Sync issue #7 first (node starts with zero comments).
        var number = Fake.SeedIssue(repo, "Existing issue");
        await Issues.SyncIssue(space, number, UserId).Timeout(60.Seconds()).ToTask();
        await WaitForIssue(IssueService.IssuePath(space, number), i => i.CommentsCount == 0);

        var payload = JsonDocument.Parse($$"""
        {
          "action": "created",
          "repository": { "full_name": "test/webhook-comment" },
          "issue": {
            "number": {{number}}, "title": "Existing issue", "state": "open",
            "user": { "login": "octocat" }, "labels": [], "assignees": [],
            "comments": 1, "html_url": "https://github.com/test/webhook-comment/issues/{{number}}"
          },
          "comment": {
            "id": 555, "body": "a webhook comment", "user": { "login": "reviewer" },
            "created_at": "2026-07-04T11:00:00Z",
            "html_url": "https://github.com/test/webhook-comment/issues/7#issuecomment-555"
          }
        }
        """).RootElement;

        var updated = await Webhooks.Process("issue_comment", payload).Timeout(60.Seconds()).ToTask();
        Assert.Equal(1, updated);

        var issue = await WaitForIssue(IssueService.IssuePath(space, number), i => i.CommentsCount == 1);
        Assert.Contains(issue.Comments, c => c.Id == 555 && c.Body == "a webhook comment");
    }

    [Fact(Timeout = 120000)]
    public async Task Process_UnmatchedRepo_UpsertsNothing()
    {
        await Connect();
        var space = "GhWu" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Webhook Unmatched Space");
        await Sync.SaveConfig(space, "https://github.com/test/some-repo", "main", null, true, true)
            .Timeout(30.Seconds()).ToTask();

        var payload = JsonDocument.Parse("""
        {
          "action": "opened",
          "repository": { "full_name": "someone/other-repo" },
          "issue": { "number": 1, "title": "nope", "state": "open", "comments": 0 }
        }
        """).RootElement;

        var updated = await Webhooks.Process("issues", payload).Timeout(60.Seconds()).ToTask();
        Assert.Equal(0, updated);
    }
}
