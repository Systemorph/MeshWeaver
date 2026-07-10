using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mcp;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// The MCP <c>github_sync</c> tool (<see cref="McpMeshPlugin.GitHubSync"/>) — the headless trigger for
/// a Space's GitHub Commit / Update / Check, the same ops the browser's <c>GitHubAction</c> layout area
/// runs. The tool FIRES the op as an activity under the caller's identity and returns the activity
/// handle IMMEDIATELY (never blocking on the GitHub I/O). These tests drive the tool exactly as an MCP
/// client would — over the real <see cref="McpMeshPlugin"/>, against the in-memory
/// <see cref="FakeGitHubRepoClient"/> so the whole export loop runs offline.
/// </summary>
public class McpGitHubSyncToolTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    private McpMeshPlugin CreatePlugin() =>
        new(Mesh, Options.Create(new McpConfiguration { BaseUrl = "https://test.local" }));

    // The plugin reads the caller identity from AccessService.Context ?? CircuitContext (the effective
    // identity every write primitive uses). In production UserContextMiddleware sets Context per MCP
    // request; in a test we set the circuit context to the DevLogin admin the base logged in.
    private void SignInAsAdmin()
    {
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetCircuitContext(new AccessContext { ObjectId = UserId, Name = TestUsers.Admin.Name });
    }

    // ── Argument validation (pure — no Space, no config, no GitHub) ─────────────────────────────

    [Fact(Timeout = 60000)]
    public async Task GitHubSync_BlankSpace_ReturnsError()
    {
        var result = await CreatePlugin().GitHubSync(space: "  ", op: "commit");
        Assert.StartsWith("Error:", result);
        Assert.Contains("'space' is required", result);
    }

    [Fact(Timeout = 60000)]
    public async Task GitHubSync_UnknownOp_ReturnsError()
    {
        var result = await CreatePlugin().GitHubSync(space: "ACME", op: "frobnicate");
        Assert.StartsWith("Error:", result);
        Assert.Contains("must be 'commit', 'update', or 'check'", result);
    }

    [Fact(Timeout = 60000)]
    public async Task GitHubSync_NoIdentity_ReturnsSignInError()
    {
        // No SignInAsAdmin() — the circuit context is null, so the tool cannot resolve a caller.
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        access.SetCircuitContext(null);
        var result = await CreatePlugin().GitHubSync(space: "ACME", op: "commit");
        Assert.StartsWith("Error:", result);
        Assert.Contains("sign-in required", result);
    }

    // ── Commit round-trip: tool → activity → fake GitHub ────────────────────────────────────────

    [Fact(Timeout = 120000)]
    public async Task GitHubSync_Commit_FiresActivity_AndCommitsToRepo()
    {
        await Connect();
        var space = "McpGh" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Mcp GitHub Space");
        await CreateMarkdown($"{space}/Page", "Page", "# page");
        var repo = $"https://github.com/test/{space.ToLowerInvariant()}";
        await Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();

        SignInAsAdmin();

        // The tool returns immediately with the activity handle — it does NOT block on the push.
        var raw = await CreatePlugin().GitHubSync(space, "commit");
        Assert.DoesNotContain("Error:", raw);
        using var doc = JsonDocument.Parse(raw);
        Assert.Equal("Started", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal("commit", doc.RootElement.GetProperty("op").GetString());
        var activityPath = doc.RootElement.GetProperty("activityPath").GetString();
        Assert.False(string.IsNullOrEmpty(activityPath));
        Assert.StartsWith($"{space}/_Activity/", activityPath);

        // The activity converges to Succeeded and its log carries the commit line (observed reactively).
        var log = await WaitForActivity(activityPath!, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Succeeded, log.Status);
        Assert.Contains(log.Messages, m => m.Message.Contains("Committed"));

        // The commit actually landed on the fake repo.
        Assert.Contains(Fake.Tree(repo), f => f.Path == "Page.md");
    }

    // ── Check round-trip (read-only op) ─────────────────────────────────────────────────────────

    [Fact(Timeout = 120000)]
    public async Task GitHubSync_Check_FiresActivity_AndReportsBranchState()
    {
        await Connect();
        var space = "McpGhChk" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Mcp GitHub Check Space");
        await CreateMarkdown($"{space}/Page", "Page", "# page");
        var repo = $"https://github.com/test/{space.ToLowerInvariant()}";
        await Sync.SaveConfig(space, repo, "main", null, true, true).Timeout(30.Seconds()).ToTask();
        await Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).ToTask();

        SignInAsAdmin();

        var raw = await CreatePlugin().GitHubSync(space, "check");
        Assert.DoesNotContain("Error:", raw);
        using var doc = JsonDocument.Parse(raw);
        var activityPath = doc.RootElement.GetProperty("activityPath").GetString();
        Assert.False(string.IsNullOrEmpty(activityPath));

        var log = await WaitForActivity(activityPath!, l => l.Status != ActivityStatus.Running);
        Assert.Equal(ActivityStatus.Succeeded, log.Status);
        Assert.Contains(log.Messages, m => m.Message.Contains("up to date"));
    }

    private async Task<ActivityLog> WaitForActivity(string activityPath, Func<ActivityLog, bool> predicate) =>
        await Mesh.GetWorkspace().GetMeshNodeStream(activityPath)
            .Select(n => n?.Content as ActivityLog)
            .Where(l => l is not null && predicate(l))
            .Select(l => l!)
            .FirstAsync()
            .Timeout(60.Seconds())
            .ToTask();
}
