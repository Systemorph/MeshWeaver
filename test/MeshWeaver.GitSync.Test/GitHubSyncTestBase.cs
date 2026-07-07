using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Data;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Slides;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Base for GitHub-sync integration tests: wires the GitHub-sync types, services, a
/// real <see cref="IProviderKeyProtector"/> (so credential encryption is exercised),
/// the per-node GitHub Sync settings tab, and the in-memory <see cref="FakeGitHubRepoClient"/>
/// so the full export/import loop runs offline.
/// </summary>
public abstract class GitHubSyncTestBase(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected readonly FakeGitHubRepoClient Fake = new();
    // Deterministic AI-draft stub so the open-PR flow runs offline (no model configured). Records
    // each draft request so a test can assert the AI-draft step ran with the change context.
    protected readonly StubPullRequestDraftService DraftStub = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            // Slides are an extracted module (MeshWeaver.Plugins); AddGraph no longer ships them.
            // Register so the Slide round-trip test exercises the SlideMarkdownContentMapper seam.
            .AddSlides()
            .AddGitHubSyncTypes()
            .ConfigureDefaultNodeHub(c => c.AddGitHubSyncSettingsTab().AddGitHubIssuesTab())
            .ConfigureServices(s =>
            {
                s.AddGitHubSyncServices();
                // Override the Octokit client with the in-memory fake (last registration wins).
                s.AddSingleton<IGitHubRepoClient>(Fake);
                // Override the AI draft with the deterministic stub (last registration wins) so the
                // open-PR flow does not depend on a configured model.
                s.AddSingleton<IPullRequestDraftService>(DraftStub);
                // Real encryptor with a fixed master key so the credential round-trip is encrypted.
                s.AddSingleton<IMasterKeyProvider>(new FixedMasterKey());
                s.AddSingleton<IProviderKeyProtector, ProviderKeyProtector>();
                return s;
            });

    protected GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();
    protected PullRequestService PullRequests => Mesh.ServiceProvider.GetRequiredService<PullRequestService>();
    protected IssueService Issues => Mesh.ServiceProvider.GetRequiredService<IssueService>();
    protected GitHubWebhookProcessor Webhooks => Mesh.ServiceProvider.GetRequiredService<GitHubWebhookProcessor>();
    protected GitHubCredentialService Credentials => Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
    protected IProviderKeyProtector Protector => Mesh.ServiceProvider.GetRequiredService<IProviderKeyProtector>();
    // The DevLogin user the test base logs in (TestUsers.Admin). Read directly rather than
    // from AccessService.Context, which is circuit-scoped and null on the test-method thread.
    protected string UserId => TestUsers.Admin.ObjectId!;

    /// <summary>Saves a GitHub credential for the current user and waits for it to be readable.</summary>
    protected async Task<GitHubCredential> Connect(string token = "ghp_test_token", string login = "octocat")
    {
        await Credentials.Save(UserId, new GitHubToken(token, null, "bearer", "repo", null), login)
            .Timeout(30.Seconds()).ToTask();
        return await WaitForCredential();
    }

    protected async Task<GitHubCredential> WaitForCredential() =>
        (await Observable.Interval(50.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Credentials.Get(UserId))
            .Where(c => c is { AccessToken.Length: > 0 })
            .FirstAsync()
            .Timeout(10.Seconds())
            .ToTask())!;

    protected async Task<MeshNode> CreateSpace(string id, string? name = null) =>
        await NodeFactory.CreateNode(new MeshNode(id)
        {
            NodeType = "Space",
            Name = name ?? id,
            State = MeshNodeState.Active,
            Content = new Space { Name = name ?? id },
        }).Timeout(60.Seconds()).ToTask();

    protected async Task<MeshNode> CreateMarkdown(string path, string name, string body)
    {
        var seed = MeshNode.FromPath(path);
        return await NodeFactory.CreateNode(seed with
        {
            NodeType = "Markdown",
            Name = name,
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = body },
        }).Timeout(60.Seconds()).ToTask();
    }

    /// <summary>Waits for a node to be readable at <paramref name="path"/> and returns it.</summary>
    protected async Task<MeshNode> WaitForNode(string path) =>
        (await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => ReadNode(path))
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask())!;

    /// <summary>Waits for the issue node at <paramref name="path"/> to satisfy <paramref name="predicate"/>.</summary>
    protected async Task<GitHubIssue> WaitForIssue(string path, Func<GitHubIssue, bool> predicate) =>
        (await Issues.WatchIssue(path)
            .Where(i => i is not null && predicate(i))
            .Select(i => i!)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask())!;

    /// <summary>Confirms a node is absent (stays null over a short window) — for prune assertions.</summary>
    protected async Task<bool> IsAbsent(string path) =>
        await ReadNode(path).Timeout(10.Seconds()).ToTask() is null;

    /// <summary>Polls the Space's GitHub sync config until it satisfies <paramref name="predicate"/>.</summary>
    protected async Task<GitHubSyncConfig> WaitForConfig(string spacePath, Func<GitHubSyncConfig, bool> predicate) =>
        await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => Sync.ReadConfig(spacePath))
            .Where(c => c is not null && predicate(c))
            .Select(c => c!)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask();

    protected static string MarkdownBody(MeshNode node) => node.Content switch
    {
        MarkdownContent mc => mc.Content,
        string s => s,
        _ => "",
    };

    private sealed class FixedMasterKey : IMasterKeyProvider
    {
        // 32 bytes = AES-256. Fixed so encrypt→decrypt round-trips deterministically.
        private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        public byte[]? GetMasterKey() => Key;
    }
}

/// <summary>
/// Deterministic <see cref="IPullRequestDraftService"/> for tests: returns a fixed draft built
/// from the change context (so assertions can confirm the context flowed through), and records
/// every request so a test can verify the AI-draft step ran. Stand-in for the live
/// PullRequestWriter agent, which would otherwise need a configured model.
/// </summary>
public sealed class StubPullRequestDraftService : IPullRequestDraftService
{
    public sealed record Call(string SpaceName, string? SpaceSummary, string HeadBranch, string BaseBranch);

    private readonly System.Collections.Concurrent.ConcurrentQueue<Call> calls = new();

    /// <summary>The draft requests this stub has received (in order).</summary>
    public IReadOnlyList<Call> Calls => calls.ToArray();

    public IObservable<PullRequestDraft> DraftAsync(
        string spaceName, string? spaceSummary, string headBranch, string baseBranch,
        CancellationToken ct = default)
    {
        calls.Enqueue(new Call(spaceName, spaceSummary, headBranch, baseBranch));
        return System.Reactive.Linq.Observable.Return(new PullRequestDraft(
            $"Sync {spaceName} ({headBranch} → {baseBranch})",
            $"AI-drafted body for **{spaceName}**.\n\nMerges `{headBranch}` into `{baseBranch}`."));
    }
}
