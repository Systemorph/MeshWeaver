using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Hosting.AspNetCore;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mcp;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// The MCP <c>github_issue</c> tool (<see cref="McpMeshPlugin.GitHubIssue"/>) — filing, commenting on,
/// and mirroring a Space's GitHub issues from MCP.
///
/// <para>🚨 <b>Why the tool exists.</b> <see cref="IssueService"/> was fully built and reachable only
/// from the browser, so an agent working through MCP — which is how agents work here — could read and
/// write the whole mesh but could not report a defect anywhere a human triages. It could leave a mesh
/// thread, which nothing routes to a backlog.</para>
///
/// <para>These drive the real <see cref="McpMeshPlugin"/> against the in-memory
/// <see cref="FakeGitHubRepoClient"/>, so the whole loop runs offline.</para>
/// </summary>
public class McpGitHubIssueToolTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    private McpMeshPlugin CreatePlugin() =>
        new(Mesh, Options.Create(new McpConfiguration { BaseUrl = "https://test.local" }));

    // The plugin reads the caller from AccessService.Context ?? CircuitContext, exactly as every write
    // primitive does. In production UserContextMiddleware sets it per MCP request.
    private void SignInAsAdmin()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetHostIdentity(new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
    }

    private async Task<string> SpaceWithRepo(string prefix)
    {
        var space = prefix + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, $"{prefix} Space");
        var repo = $"https://github.com/test/{space.ToLowerInvariant()}";
        await Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();
        return space;
    }

    private static string Repo(string space) => $"https://github.com/test/{space.ToLowerInvariant()}";

    // ── Argument validation (pure — no Space, no config, no GitHub) ─────────────────────────────

    [Fact(Timeout = 60000)]
    public async Task GitHubIssue_BlankSpace_ReturnsError()
    {
        var result = await CreatePlugin().GitHubIssue(space: "  ");
        Assert.StartsWith("Error:", result);
        Assert.Contains("'space' is required", result);
    }

    [Fact(Timeout = 60000)]
    public async Task GitHubIssue_UnknownOp_ReturnsError()
    {
        var result = await CreatePlugin().GitHubIssue(space: "ACME", op: "frobnicate");
        Assert.StartsWith("Error:", result);
        Assert.Contains("must be 'create', 'comment', or 'sync'", result);
    }

    [Fact(Timeout = 60000)]
    public async Task GitHubIssue_NoIdentity_ReturnsSignInError()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetHostIdentity(null);
        var result = await CreatePlugin().GitHubIssue(space: "ACME", title: "x");
        Assert.StartsWith("Error:", result);
        Assert.Contains("sign-in required", result);
    }

    /// <summary>A missing title is answered as a sentence, not relayed as a 422 from GitHub.</summary>
    [Fact(Timeout = 60000)]
    public async Task GitHubIssue_CreateWithoutTitle_ReturnsError()
    {
        SignInAsAdmin();
        var result = await CreatePlugin().GitHubIssue(space: "ACME", op: "create", body: "no title");
        Assert.StartsWith("Error:", result);
        Assert.Contains("'title' is required", result);
    }

    [Fact(Timeout = 60000)]
    public async Task GitHubIssue_CommentWithoutNumberOrBody_ReturnsError()
    {
        SignInAsAdmin();
        var noNumber = await CreatePlugin().GitHubIssue(space: "ACME", op: "comment", body: "hi");
        Assert.Contains("'number'", noNumber);

        var noBody = await CreatePlugin().GitHubIssue(space: "ACME", op: "comment", number: 7);
        Assert.Contains("'body' is required", noBody);
    }

    // ── 🚨 The security boundary ────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 <b>Without a connected GitHub account the tool REFUSES — it never falls back to a system or
    /// app identity.</b> This is the whole authorization model: the caller's own OAuth token decides
    /// which repositories are reachable, so an agent can file exactly where its own account may and
    /// nowhere else. A system-identity fallback would turn "file an issue" into "write to every
    /// repository the platform can see", authored by nobody you could ask about it.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task GitHubIssue_WithoutAConnectedAccount_Refuses()
    {
        // Deliberately NO Connect() — the caller has no stored credential.
        var space = await SpaceWithRepo("McpIssNoCred");
        SignInAsAdmin();

        var result = await CreatePlugin().GitHubIssue(space, "create", title: "should not be filed");

        Assert.StartsWith("Error:", result);
        Assert.Contains("Connect your GitHub account", result);
        // Nothing reached GitHub.
        Assert.Empty(Fake.IssuesOn(Repo(space)));
    }

    // ── Create round-trip: tool → IssueService → GitHub → mesh node ─────────────────────────────

    [Fact(Timeout = 120000)]
    public async Task GitHubIssue_Create_FilesOnGitHub_AndMirrorsTheNode()
    {
        await Connect();
        var space = await SpaceWithRepo("McpIssCreate");
        SignInAsAdmin();

        var raw = await CreatePlugin().GitHubIssue(
            space, "create", title: "Scheduler fired with stale state", body: "Timer captured at arm time.");

        Assert.DoesNotContain("Error:", raw);
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("Created", doc.RootElement.GetProperty("status").GetString());
        var number = doc.RootElement.GetProperty("number").GetInt32();
        Assert.True(number > 0, "the envelope must carry the issue number — it is the handle for commenting");

        // It landed on GitHub…
        var filed = Assert.Single(Fake.IssuesOn(Repo(space)));
        Assert.Equal("Scheduler fired with stale state", filed.Title);

        // …and was mirrored to the canonical node path, so the mesh can read it back.
        var node = await WaitForNode(IssueService.IssuePath(space, number));
        Assert.Equal(IssueService.NodeType, node.NodeType);
    }

    /// <summary>Labels pass through, so an agent can file straight into an existing triage lane.</summary>
    [Fact(Timeout = 120000)]
    public async Task GitHubIssue_Create_AppliesLabels()
    {
        await Connect();
        var space = await SpaceWithRepo("McpIssLabels");
        SignInAsAdmin();

        var raw = await CreatePlugin().GitHubIssue(
            space, "create", title: "Labelled", body: "b", labels: "bug, scheduler");

        Assert.DoesNotContain("Error:", raw);
        var filed = Assert.Single(Fake.IssuesOn(Repo(space)));
        Assert.Contains("bug", filed.Labels);
        Assert.Contains("scheduler", filed.Labels);
    }

    // ── Comment round-trip ──────────────────────────────────────────────────────────────────────

    [Fact(Timeout = 120000)]
    public async Task GitHubIssue_Comment_PostsOnTheIssue()
    {
        await Connect();
        var space = await SpaceWithRepo("McpIssComment");
        var number = Fake.SeedIssue(Repo(space), "Existing issue", "seeded");
        SignInAsAdmin();

        var raw = await CreatePlugin().GitHubIssue(space, "comment", number: number, body: "Reproduced on main.");

        Assert.DoesNotContain("Error:", raw);
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("Commented", doc.RootElement.GetProperty("status").GetString());

        var node = await WaitForIssue(IssueService.IssuePath(space, number), i => i.CommentsCount > 0);
        Assert.Contains(node.Comments, c => c.Body == "Reproduced on main.");
    }

    // ── Sync: mirror the repo's issues so an agent can check for duplicates ─────────────────────

    [Fact(Timeout = 120000)]
    public async Task GitHubIssue_Sync_MirrorsIssuesIntoTheSpace()
    {
        await Connect();
        var space = await SpaceWithRepo("McpIssSync");
        var first = Fake.SeedIssue(Repo(space), "First", "a");
        var second = Fake.SeedIssue(Repo(space), "Second", "b");
        SignInAsAdmin();

        var raw = await CreatePlugin().GitHubIssue(space, "sync");

        Assert.DoesNotContain("Error:", raw);
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("Synced", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("synced").GetInt32());

        await WaitForNode(IssueService.IssuePath(space, first));
        await WaitForNode(IssueService.IssuePath(space, second));
    }

    /// <summary>An unknown state value is refused rather than silently treated as 'open'.</summary>
    [Fact(Timeout = 60000)]
    public async Task GitHubIssue_Sync_RejectsAnUnknownState()
    {
        SignInAsAdmin();
        var result = await CreatePlugin().GitHubIssue(space: "ACME", op: "sync", state: "halfopen");
        Assert.StartsWith("Error:", result);
        Assert.Contains("must be 'open', 'closed', or 'all'", result);
    }
}
