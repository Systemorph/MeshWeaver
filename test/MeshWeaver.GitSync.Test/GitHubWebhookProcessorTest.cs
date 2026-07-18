using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
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

    // ── push → auto-update ───────────────────────────────────────────────────

    [Fact]
    public void TryParsePush_BranchFilesAndTruncation()
    {
        // A branch push with two commits: the changed set is the union of added/modified/removed.
        var push = ParsePush("""
        {
          "ref": "refs/heads/main", "size": 2,
          "commits": [
            { "added": ["Store/index.json"], "modified": [], "removed": [] },
            { "added": [], "modified": ["Store/Plugin.json"], "removed": ["Old.md"] }
          ]
        }
        """);
        Assert.NotNull(push);
        Assert.Equal("main", push!.Branch);
        Assert.NotNull(push.ChangedPaths);
        Assert.Equal(
            new[] { "Old.md", "Store/Plugin.json", "Store/index.json" },
            push.ChangedPaths!.OrderBy(p => p, StringComparer.Ordinal));

        // GitHub caps the commits array at 20 — when size exceeds it, the change set is UNKNOWN.
        Assert.Null(ParsePush("""
        { "ref": "refs/heads/main", "size": 25,
          "commits": [ { "added": ["A.md"], "modified": [], "removed": [] } ] }
        """)!.ChangedPaths);

        // Tag pushes and branch deletions carry nothing to import.
        Assert.Null(ParsePush("""{ "ref": "refs/tags/v1.0", "commits": [] }"""));
        Assert.Null(ParsePush("""{ "ref": "refs/heads/main", "deleted": true, "commits": [] }"""));

        static GitHubWebhookProcessor.PushEvent? ParsePush(string json)
            => GitHubWebhookProcessor.TryParsePush(JsonDocument.Parse(json).RootElement, out var p) ? p : null;
    }

    [Fact]
    public void ConfigMatchesPush_BranchDirectionAndSubdirectory()
    {
        var main = new GitHubWebhookProcessor.PushEvent("main", ["Store/index.json", "README.md"]);
        var cfg = new GitHubSyncConfig { RepositoryUrl = "https://github.com/o/r", Branch = "main" };

        // Branch must match; export-only sources never import.
        Assert.True(GitHubWebhookProcessor.ConfigMatchesPush(cfg, main));
        Assert.False(GitHubWebhookProcessor.ConfigMatchesPush(cfg with { Branch = "develop" }, main));
        Assert.False(GitHubWebhookProcessor.ConfigMatchesPush(
            cfg with { Direction = SyncDirection.ExportOnly }, main));
        Assert.False(GitHubWebhookProcessor.ConfigMatchesPush(null, main));

        // A subdirectory-scoped source updates only when the push touched its subdirectory.
        Assert.True(GitHubWebhookProcessor.ConfigMatchesPush(cfg with { Subdirectory = "Store" }, main));
        Assert.False(GitHubWebhookProcessor.ConfigMatchesPush(cfg with { Subdirectory = "Chess" }, main));
        // Prefix must be segment-aligned: "Store" must not match "StoreFront/…".
        Assert.False(GitHubWebhookProcessor.ConfigMatchesPush(
            cfg with { Subdirectory = "Store" },
            new GitHubWebhookProcessor.PushEvent("main", ["StoreFront/index.json"])));

        // UNKNOWN change set (truncated push) → every candidate syncs rather than missing one.
        var unknown = new GitHubWebhookProcessor.PushEvent("main", null);
        Assert.True(GitHubWebhookProcessor.ConfigMatchesPush(cfg with { Subdirectory = "Chess" }, unknown));

        // An empty change set (no-op push) syncs nothing.
        var empty = new GitHubWebhookProcessor.PushEvent("main", []);
        Assert.False(GitHubWebhookProcessor.ConfigMatchesPush(cfg, empty));
        Assert.False(GitHubWebhookProcessor.ConfigMatchesPush(cfg with { Subdirectory = "Store" }, empty));
    }

    [Fact(Timeout = 120000)]
    public async Task Process_PushEvent_TriggersUpdateToLatestOnMatchingSpaces()
    {
        await Connect();
        var repo = "https://github.com/test/push-auto";

        // Space A authors content and commits it — the fake repo now holds the canonical files.
        var a = "GhPa" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(a, "Push Source Space");
        await CreateMarkdown($"{a}/Welcome", "Welcome", "# Welcome\n\nPushed content.");
        await Sync.SaveConfig(a, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();
        await Sync.SyncToGitHub(a, UserId).Timeout(60.Seconds()).ToTask();

        // Space B syncs the SAME repo but has never imported — the push must update it headlessly.
        var b = "GhPb" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(b, "Push Target Space");
        await Sync.SaveConfig(b, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        // 🚨 The webhook HTTP request is ANONYMOUS (its authorization is the HMAC signature).
        // Drop every ambient identity — the DevLogin persistent circuit fallback masked the
        // prod defect where the config-node query ran as anonymous and matched NOTHING on an
        // access-gated portal. The processor must impersonate System for its own lookups.
        var accessService = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        accessService.ClearPersistentCircuitContext();
        accessService.SetCircuitContext(null);
        accessService.SetContext(null);
        int triggered;
        try
        {
            // A push on a DIFFERENT branch matches nothing.
            var otherBranch = JsonDocument.Parse("""
            { "ref": "refs/heads/develop", "size": 1,
              "repository": { "full_name": "test/push-auto" },
              "commits": [ { "added": ["Welcome.md"], "modified": [], "removed": [] } ] }
            """).RootElement;
            Assert.Equal(0, await Webhooks.Process("push", otherBranch).Timeout(60.Seconds()).ToTask());

            // The main-branch push triggers the headless update for BOTH sync sources.
            var payload = JsonDocument.Parse("""
            { "ref": "refs/heads/main", "size": 1,
              "repository": { "full_name": "test/push-auto" },
              "commits": [ { "added": [], "modified": ["Welcome.md"], "removed": [] } ] }
            """).RootElement;
            triggered = await Webhooks.Process("push", payload).Timeout(60.Seconds()).ToTask();
        }
        finally
        {
            accessService.SetCircuitContext(new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
        }
        Assert.Equal(2, triggered);

        // The import runs as a background activity — observe the node landing in Space B.
        var imported = await WaitForNode($"{b}/Welcome");
        Assert.Equal("Markdown", imported.NodeType);
        Assert.Contains("Pushed content.", MarkdownBody(imported));
    }
}
