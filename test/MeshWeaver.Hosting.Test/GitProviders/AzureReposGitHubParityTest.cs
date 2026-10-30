#pragma warning disable CS1591
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Net.Http;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Mesh.Threading;
using MeshWeaver.Messaging;
using MeshWeaver.Reactive.Assertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace MeshWeaver.Hosting.Test.GitProviders;

/// <summary>
/// 🚨 <b>GitHub and Azure Repos behave identically on the shared scenarios</b> (MeshWeaver#6500,
/// policy <c>azure-repos-two-way</c>). Every scenario is a <see cref="TheoryAttribute"/> over the two
/// providers and asserts the SAME outcome: export, import into a new Space, mirror within a
/// subdirectory, the two-way rules (a server addition survives every later repository commit; a
/// server edit newer than the horizon is preserved and holds both the baseline and the horizon),
/// the direction refusals, and the branch state.
///
/// <para><b>What runs.</b> The real <see cref="GitHubSyncService"/> behind the production provider
/// seam (<see cref="GitProviderRepoClient"/>). GitHub is faked at the <see cref="IGitHubRepoClient"/>
/// seam every GitSync suite fakes it at (<see cref="InMemoryGitHubRepoClient"/>); Azure Repos runs the
/// production <see cref="AzureReposRepoClient"/> against Azure DevOps faked at the HTTP boundary
/// (<see cref="FakeAzureDevOpsServer"/>) — so the Azure half exercises the REST calls, the payloads
/// and the Bearer credential, not a stand-in for them. The Azure token is the one the DECLARED
/// identity mints (<see cref="AzureReposTokenService"/> over a test credential, registered as a
/// contributed <see cref="IGitHostingProvider"/>); the fake refuses
/// any other with 401, so every green Azure scenario proves the credential travelled.</para>
/// </summary>
public class AzureReposGitHubParityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    public const string GitHub = GitRepositoryProvider.GitHub;
    public const string AzureRepos = GitRepositoryProvider.AzureRepos;

    private const string EntraToken = "entra-token-for-the-declared-identity";
    private const string GitHubToken = "ghp_parity_token";

    private readonly InMemoryGitHubRepoClient github = new();
    private readonly FakeAzureDevOpsServer azure = new() { ExpectedToken = EntraToken };

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                // The DECLARED identity (AzureDevOps:TenantId/ClientId) with a test credential in place
                // of the workload-identity exchange — last registration wins.
                // Registered as a CONTRIBUTED hosting provider — the shape a module registers it in —
                // so it replaces the platform's built-in Azure Repos provider by kind.
                services.AddGitHostingProvider(sp => new AzureReposGitHostingProvider(
                    new AzureReposTokenService(
                        sp.GetRequiredService<IoPoolRegistry>(),
                        Options.Create(new AzureDevOpsOptions { TenantId = "client-tenant", ClientId = "client-app" }),
                        credentialFactory: _ => new StaticCredential(EntraToken),
                        environment: name => name == AzureReposTokenService.FederatedTokenFileVariable
                            ? "/var/run/secrets/azure/tokens/azure-identity-token"
                            : null),
                    new AzureReposRepoClient(sp.GetRequiredService<IoPoolRegistry>(), new HttpClient(azure))));
                // The production seam, with GitHub faked at the client and Azure DevOps at HTTP.
                services.AddSingleton<IGitHubRepoClient>(sp => new GitProviderRepoClient(
                    github, sp.GetRequiredService<GitHostingProviders>()));
                return services;
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();
    private PullRequestService PullRequests => Mesh.ServiceProvider.GetRequiredService<PullRequestService>();
    private IssueService Issues => Mesh.ServiceProvider.GetRequiredService<IssueService>();

    /// <summary>The production Azure Repos client over the HTTP fake, outside the sync.</summary>
    private AzureReposRepoClient Client()
        => new(Mesh.ServiceProvider.GetRequiredService<IoPoolRegistry>(), new HttpClient(azure));

    private GitHubCredentialService Credentials => Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
    private static string UserId => TestUsers.Admin.ObjectId!;

    // ══════════════════════════════════════════════════════════════════════════
    //  Export and import
    // ══════════════════════════════════════════════════════════════════════════

    [Theory(Timeout = 180_000)]
    [InlineData(GitHub)]
    [InlineData(AzureRepos)]
    public async Task Export_WritesTheSpace_AndAnImportIntoANewSpaceRoundTripsIt(string provider)
    {
        var ct = TestContext.Current.CancellationToken;
        var (space, url, remote) = await ArmedSpace(provider, "Export", ct);
        await CreateMarkdown($"{space}/Welcome", "# Welcome\n\nv1.", ct);
        await CreateMarkdown($"{space}/Guide", "# Guide\n\nthe guide.", ct);
        await SaveConfig(space, url, twoWay: false, ct: ct);

        var pushed = await Sync.SyncToGitHub(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct);

        pushed.CommitSha.Should().Be(remote.Head("main"), "the result names the commit the branch now points at");
        var tree = remote.TextTreeAt("main");
        Output.WriteLine($"{provider} tree: {string.Join(", ", tree.Keys)}");
        tree.Keys.Should().Contain(p => p.StartsWith("Welcome.", StringComparison.Ordinal));
        tree.Keys.Should().Contain(p => p.StartsWith("Guide.", StringComparison.Ordinal));
        tree.Values.Should().Contain(v => v.Contains("v1.", StringComparison.Ordinal));
        await ConfigWhen(space, c => c.LastSyncCommitSha == pushed.CommitSha && c.LastSyncedAt != null, ct);

        // Import the same repository into a NEW Space.
        var imported = "Imported" + Guid.NewGuid().ToString("N")[..8];
        await Sync.ImportFromGitHub(url, "main", imported, "Imported", subdirectory: null, UserId)
            .Timeout(TestTimeouts.Convergence).Await(ct);

        (await Body($"{imported}/Welcome", ct)).Should().Contain("v1.");
        (await Body($"{imported}/Guide", ct)).Should().Contain("the guide.");
    }

    [Theory(Timeout = 180_000)]
    [InlineData(GitHub)]
    [InlineData(AzureRepos)]
    public async Task Export_MirrorsWithinTheSubdirectory_AndLeavesEverythingElseAlone(string provider)
    {
        var ct = TestContext.Current.CancellationToken;
        var (space, url, remote) = await ArmedSpace(provider, "Mirror", ct);
        // The repository already carries a file OUTSIDE the mirrored folder and a stale one INSIDE it.
        remote.CommitTo("main", tree => tree
            .SetItem("other/keep.txt", Encoding.UTF8.GetBytes("not the Space's"))
            .SetItem("content/Stale.json", Encoding.UTF8.GetBytes("{}")), "seed");
        await CreateMarkdown($"{space}/Welcome", "# Welcome\n\nv1.", ct);
        await SaveConfig(space, url, subdirectory: "content", twoWay: false, ct: ct);

        var first = await Sync.SyncToGitHub(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct);

        var tree = remote.TextTreeAt("main");
        tree["other/keep.txt"].Should().Be("not the Space's", "a path outside the subdirectory is never the export's");
        tree.Keys.Should().NotContain("content/Stale.json", "inside the subdirectory the repository is made to match the Space");
        tree.Keys.Should().Contain(p => p.StartsWith("content/Welcome.", StringComparison.Ordinal));
        first.FilesDeleted.Should().Be(1);

        // An export with nothing changed leaves the content exactly as it was, and the recorded
        // commit is the one the branch points at.
        var before = remote.TextTreeAt("main");
        var second = await Sync.SyncToGitHub(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct);
        Flatten(remote.TextTreeAt("main")).Should().Be(Flatten(before));
        second.CommitSha.Should().Be(remote.Head("main"));
        second.FilesDeleted.Should().Be(0);
        await ConfigWhen(space, c => c.LastSyncCommitSha == second.CommitSha, ct);
    }

    [Theory(Timeout = 180_000)]
    [InlineData(GitHub)]
    [InlineData(AzureRepos)]
    public async Task ARepositoryCommit_IsImportedByUpdateToLatest_AndTheBranchStateFollowsIt(string provider)
    {
        var ct = TestContext.Current.CancellationToken;
        var (space, url, remote) = await ArmedSpace(provider, "Update", ct);
        await CreateMarkdown($"{space}/Welcome", "# Welcome\n\nv1.", ct);
        await SaveConfig(space, url, twoWay: true, ct: ct);
        var pushed = await Sync.SyncToGitHub(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct);
        await ConfigWhen(space, c => c.LastSyncCommitSha == pushed.CommitSha, ct);

        (await Sync.AskBranchState(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct))
            .UpToDate.Should().BeTrue("nothing was committed to the repository since the export");

        var repoCommit = remote.CommitTo("main", InMemoryGitRemote.WithFile("Docs.json", MarkdownFile("Docs", "from the repo")), "add Docs");
        var state = await Sync.AskBranchState(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct);
        state.HeadCommitSha.Should().Be(repoCommit.Sha);
        state.UpToDate.Should().BeFalse();
        (await Sync.GetBranchHead(url, "main", UserId).Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Be(repoCommit.Sha);

        await Sync.ReimportAtCommit(space, "main", UserId).Timeout(TestTimeouts.Convergence).Await(ct);

        (await Body($"{space}/Docs", ct)).Should().Contain("from the repo");
        await ConfigWhen(space, c => c.LastSyncCommitSha == repoCommit.Sha, ct);
        (await Sync.AskBranchState(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct))
            .UpToDate.Should().BeTrue();
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  The two-way baseline rules
    // ══════════════════════════════════════════════════════════════════════════

    [Theory(Timeout = 180_000)]
    [InlineData(GitHub)]
    [InlineData(AzureRepos)]
    public async Task TwoWay_AServerAddition_SurvivesEveryLaterRepositoryCommit(string provider)
    {
        var ct = TestContext.Current.CancellationToken;
        var (space, url, remote) = await ArmedSpace(provider, "Addition", ct);
        await CreateMarkdown($"{space}/Welcome", "# Welcome\n\nv1.", ct);
        await SaveConfig(space, url, twoWay: true, ct: ct);
        var pushed = await Sync.SyncToGitHub(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct);
        await ConfigWhen(space, c => c.LastSyncedAt != null && c.LastSyncCommitSha == pushed.CommitSha, ct);

        // Added on the SERVER after the baseline — in no commit of the repository.
        await CreateMarkdown($"{space}/ServerOnly", "# ServerOnly\n\nadded on the server.", ct);

        foreach (var (id, text) in new[] { ("Docs", "first repo commit"), ("Docs2", "second repo commit") })
        {
            var commit = remote.CommitTo("main", InMemoryGitRemote.WithFile($"{id}.json", MarkdownFile(id, text)), "add " + id);
            var update = await Sync.ReimportAtCommit(space, "main", UserId).Timeout(TestTimeouts.Convergence).Await(ct);

            (await Body($"{space}/{id}", ct)).Should().Contain(text);
            (await Body($"{space}/ServerOnly", ct)).Should().Contain("added on the server",
                "a node in no import manifest is never the repository's to prune");
            update.PrunedPaths.Should().NotContain($"{space}/ServerOnly");
            await ConfigWhen(space, c => c.LastSyncCommitSha == commit.Sha, ct);
        }
    }

    [Theory(Timeout = 180_000)]
    [InlineData(GitHub)]
    [InlineData(AzureRepos)]
    public async Task TwoWay_AServerEditNewerThanTheHorizon_IsPreserved_AndHoldsTheHorizon(string provider)
    {
        var ct = TestContext.Current.CancellationToken;
        var (space, url, remote) = await ArmedSpace(provider, "Edit", ct);
        await CreateMarkdown($"{space}/Welcome", "# Welcome\n\nv1.", ct);
        await SaveConfig(space, url, twoWay: true, ct: ct);
        var pushed = await Sync.SyncToGitHub(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct);
        var exported = await ConfigWhen(space, c => c.LastSyncedAt != null && c.LastSyncCommitSha == pushed.CommitSha, ct);

        // The same node is edited on BOTH sides after the horizon.
        await Mesh.GetWorkspace().GetMeshNodeStream($"{space}/Welcome")
            .Update(n => n with { Content = new MarkdownContent { Content = "# Welcome\n\nserver edit." } })
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var welcomeFile = remote.TreeAt("main").Keys.Single(p => p.StartsWith("Welcome.", StringComparison.Ordinal));
        var repoCommit = remote.CommitTo("main", tree => tree.SetItem(welcomeFile,
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(tree[welcomeFile]).Replace("v1.", "repo edit.", StringComparison.Ordinal))),
            "edit Welcome in the repository");

        var update = await Sync.ReimportAtCommit(space, "main", UserId).Timeout(TestTimeouts.Convergence).Await(ct);

        update.Preserved.Should().BeGreaterThan(0, "the server's newer edit is kept, to be carried back on the next commit");
        (await Body($"{space}/Welcome", ct)).Should().Contain("server edit.");
        // The attempt is recorded, but a preserving import did not bring the mesh in sync with the
        // commit: the baseline (the commit the mesh holds) and the conflict horizon both stay put, so
        // the next diff still carries the repository's change and the edit stays "newer on the server".
        var held = await ConfigWhen(space, c => c.LastAttemptedCommitSha == repoCommit.Sha, ct);
        held.LastSyncCommitSha.Should().Be(pushed.CommitSha);
        held.LastSyncedAt.Should().Be(exported.LastSyncedAt);

        // The next export carries the server's edit back to the repository.
        await Sync.SyncToGitHub(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct);
        remote.TextTreeAt("main")[welcomeFile].Should().Contain("server edit.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Direction rules — the same refusal, whatever the provider
    // ══════════════════════════════════════════════════════════════════════════

    [Theory(Timeout = 180_000)]
    [InlineData(GitHub)]
    [InlineData(AzureRepos)]
    public async Task TheDirectionRules_RefuseTheSameWay(string provider)
    {
        var ct = TestContext.Current.CancellationToken;
        var (space, url, _) = await ArmedSpace(provider, "Direction", ct);

        await SaveConfig(space, url, direction: SyncDirection.ImportOnly, ct: ct);
        var export = await Error(Sync.SyncToGitHub(space, UserId), ct);
        export.GetType().Should().Be(typeof(InvalidOperationException));
        export.Message.Should().Be(
            $"This sync source is import-only (repo → mesh): exporting to {url} is not allowed. " +
            "Change the source's Sync direction to Bidirectional or Export-only to commit.");

        await SaveConfig(space, url, direction: SyncDirection.ExportOnly, ct: ct);
        var import = await Error(Sync.ReimportAtCommit(space, "main", UserId), ct);
        import.GetType().Should().Be(typeof(InvalidOperationException));
        import.Message.Should().Be(
            $"This sync source is export-only (mesh → repo): importing from {url} is not allowed. " +
            "Change the source's Sync direction to Bidirectional or Import-only to re-import.");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Side by side, and what only Azure Repos has to prove
    // ══════════════════════════════════════════════════════════════════════════

    [Fact(Timeout = 180_000)]
    public async Task TheSameSpace_ExportsTheSameFiles_ToBothProviders()
    {
        var ct = TestContext.Current.CancellationToken;
        var trees = new Dictionary<string, ImmutableSortedDictionary<string, string>>();
        foreach (var provider in new[] { GitHub, AzureRepos })
        {
            var (space, url, remote) = await ArmedSpace(provider, "Same", ct);
            await CreateMarkdown($"{space}/Welcome", "# Welcome\n\nv1.", ct);
            await CreateMarkdown($"{space}/Docs/Intro", "# Intro\n\nnested.", ct);
            await SaveConfig(space, url, subdirectory: "site", ct: ct);
            await Sync.SyncToGitHub(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct);
            // Paths are partition-relative; the bodies of the non-root nodes are compared too.
            trees[provider] = remote.TextTreeAt("main");
        }

        trees[AzureRepos].Keys.Should().Equal(trees[GitHub].Keys, "both providers receive the same mirror");
        foreach (var path in trees[GitHub].Keys.Where(p => p.Contains("Welcome") || p.Contains("Intro")))
            Normalized(trees[AzureRepos][path]).Should().Be(Normalized(trees[GitHub][path]), path);
    }

    [Fact(Timeout = 180_000)]
    public async Task AzureRepos_ReadsAndWritesThroughTheRestApi_WithTheDeclaredIdentity_AndADiffScopedImport()
    {
        var ct = TestContext.Current.CancellationToken;
        var (space, url, remote) = await ArmedSpace(AzureRepos, "Rest", ct);
        await CreateMarkdown($"{space}/Welcome", "# Welcome\n\nv1.", ct);
        await SaveConfig(space, url, twoWay: true, ct: ct);
        var pushed = await Sync.SyncToGitHub(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct);
        await Sync.ReimportAtCommit(space, "main", UserId).Timeout(TestTimeouts.Convergence).Await(ct);
        await ConfigWhen(space, c => c.LastSyncCommitSha == pushed.CommitSha, ct);

        remote.CommitTo("main", InMemoryGitRemote.WithFile("Docs.json", MarkdownFile("Docs", "diffed in")), "add Docs");
        await Sync.ReimportAtCommit(space, "main", UserId).Timeout(TestTimeouts.Convergence).Await(ct);
        (await Body($"{space}/Docs", ct)).Should().Contain("diffed in");

        var requests = azure.Requests;
        Output.WriteLine(string.Join("\n", requests));
        requests.Should().Contain(r => r.StartsWith("POST /pushes", StringComparison.Ordinal), "the export is one push");
        requests.Should().Contain(r => r.StartsWith("GET /items", StringComparison.Ordinal));
        requests.Should().Contain(r => r.StartsWith("GET /blobs/", StringComparison.Ordinal));
        requests.Should().Contain(r => r.StartsWith("GET /diffs/commits", StringComparison.Ordinal),
            "an import after a recorded baseline is scoped by the diff, exactly as on GitHub");
        github.Tokens.Should().BeEmpty("no Azure Repos call may reach the GitHub client");
    }

    [Fact(Timeout = 180_000)]
    public async Task AMissingAzureReposRepository_IsRefusedByName_AndNothingIsCreated()
    {
        var ct = TestContext.Current.CancellationToken;
        var (space, _, _) = await ArmedSpace(AzureRepos, "Missing", ct);
        var missing = $"https://dev.azure.com/parity/Content/_git/never-created-{Guid.NewGuid():N}";
        await CreateMarkdown($"{space}/Welcome", "# Welcome\n\nv1.", ct);
        await SaveConfig(space, missing, ct: ct);

        var error = await Error(Sync.SyncToGitHub(space, UserId), ct);

        GitSyncRefusalException.TryGet(error, out var refusal).Should().BeTrue(error.ToString());
        refusal.Key.Should().Be("gitsync.azure.repositoryNotFound");
        error.Message.Should().Contain(missing);
        azure.Requests.Should().NotContain(r => r.StartsWith("POST", StringComparison.Ordinal),
            "a refused export writes nothing");
    }

    [Fact(Timeout = 180_000)]
    public async Task AnAzureFetch_ReadsAMissingSubdirectoryAsEmpty_ButNeverAnUnavailableRepository()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, url, remote) = await ArmedSpace(AzureRepos, "Fetch", ct);
        var head = remote.CommitTo("main", InMemoryGitRemote.WithFile("a/x.txt", "x"), "seed");
        var client = Client();

        var missing = await client.Fetch(url, "main", "not-there", EntraToken).Timeout(TestTimeouts.Convergence).Await(ct);
        missing.CommitSha.Should().Be(head.Sha);
        missing.Files.Should().BeEmpty("a folder the commit does not have IS an empty listing — the importer's own rules judge it");

        // The repository goes away (or the identity loses Read) between the ref lookup and the listing:
        // that is a FAILURE, scoped or not — an empty, complete snapshot would let a full import prune.
        azure.ItemsSeeNoRepository = true;
        try
        {
            foreach (var subdirectory in new[] { "a", null })
            {
                var error = await Error(client.Fetch(url, "main", subdirectory, EntraToken), ct);
                GitSyncRefusalException.TryGet(error, out var refusal).Should().BeTrue(error.ToString());
                refusal.Key.Should().Be("gitsync.azure.failed.listItems");
                error.Message.Should().Contain("TF401019", "Azure DevOps' own diagnostic travels verbatim");
            }
        }
        finally
        {
            azure.ItemsSeeNoRepository = false;
        }
    }

    [Fact(Timeout = 180_000)]
    public async Task AnUnavailableAzureRepository_FailsTheImport_AndPrunesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (space, url, remote) = await ArmedSpace(AzureRepos, "Unavailable", ct);
        await CreateMarkdown($"{space}/Welcome", "# Welcome\n\nv1.", ct);
        await SaveConfig(space, url, ct: ct);
        var pushed = await Sync.SyncToGitHub(space, UserId).Timeout(TestTimeouts.Convergence).Await(ct);
        await Sync.ReimportAtCommit(space, "main", UserId).Timeout(TestTimeouts.Convergence).Await(ct);
        await ConfigWhen(space, c => c.LastSyncCommitSha == pushed.CommitSha, ct);
        remote.CommitTo("main", InMemoryGitRemote.WithFile("Docs.json", MarkdownFile("Docs", "new")), "add Docs");

        azure.ItemsSeeNoRepository = true;
        try
        {
            await Error(Sync.ReimportAtCommit(space, "main", UserId, force: true), ct);
        }
        finally
        {
            azure.ItemsSeeNoRepository = false;
        }
        (await Body($"{space}/Welcome", ct)).Should().Contain("v1.", "an unread repository deletes nothing");
    }

    [Fact(Timeout = 180_000)]
    public async Task AnAnnotatedTag_ResolvesToItsCommit_NotToTheTagObject()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, url, remote) = await ArmedSpace(AzureRepos, "Tag", ct);
        var tagged = remote.CommitTo("main", InMemoryGitRemote.WithFile("a.txt", "a"), "release");
        remote.CommitTo("main", InMemoryGitRemote.WithFile("b.txt", "b"), "after the release");
        var repo = url[(url.LastIndexOf('/') + 1)..];
        azure.AddAnnotatedTag("parity", "Content", repo, "v1.0", tagged.Sha);
        var client = Client();

        (await client.GetHeadSha(url, "v1.0", EntraToken).Timeout(TestTimeouts.Convergence).Await(ct))
            .Should().Be(tagged.Sha);
        var snapshot = await client.Fetch(url, "v1.0", null, EntraToken).Timeout(TestTimeouts.Convergence).Await(ct);
        snapshot.CommitSha.Should().Be(tagged.Sha);
        snapshot.Files.Select(f => f.Path).Should().Equal("a.txt");
    }

    [Fact(Timeout = 180_000)]
    public async Task OnAzureRepos_ABranchIsCreatedWithTheDeclaredIdentity_AndIssuesAreRefusedBeforeAnyGitHubCredential()
    {
        var ct = TestContext.Current.CancellationToken;
        // No GitHub account connected: nothing on this path may ask for one.
        var (space, url, remote) = await ArmedSpace(AzureRepos, "Branch", ct, connectGitHub: false);
        var head = remote.CommitTo("main", InMemoryGitRemote.WithFile("a.txt", "a"), "seed");
        await SaveConfig(space, url, ct: ct);

        var branch = await PullRequests.CreateBranch(space, "feature/x", "main", UserId)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        branch.CommitSha.Should().Be(head.Sha);
        remote.Head("feature/x").Should().Be(head.Sha);

        var issues = await Error(Issues.SyncIssues(space, UserId), ct);
        GitSyncRefusalException.TryGet(issues, out var refusal).Should().BeTrue(issues.ToString());
        refusal.Key.Should().Be("gitsync.azure.unsupported.issues");
        var pr = await Error(PullRequests.CreateDraft(space, null, "main"), ct);
        GitSyncRefusalException.TryGet(pr, out var prRefusal).Should().BeTrue(pr.ToString());
        prRefusal.Key.Should().Be("gitsync.azure.unsupported.pullRequest");
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Plumbing
    // ══════════════════════════════════════════════════════════════════════════

    private async Task<(string Space, string Url, InMemoryGitRemote Remote)> ArmedSpace(
        string provider, string label, CancellationToken ct, bool connectGitHub = true)
    {
        var id = label + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(id)
        {
            NodeType = "Space",
            Name = label,
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        if (connectGitHub)
            await Credentials.Save(UserId, new GitHubToken(GitHubToken, null, "bearer", "repo", null), "octocat")
                .Timeout(TestTimeouts.Convergence).Await(ct);
        var repo = id.ToLowerInvariant();
        return provider == AzureRepos
            ? (id, $"https://dev.azure.com/parity/Content/_git/{repo}", azure.Repository("parity", "Content", repo))
            : (id, $"https://github.com/parity/{repo}", github.Remote($"https://github.com/parity/{repo}"));
    }

    private Task<MeshNode> SaveConfig(
        string space, string url, string? subdirectory = null, bool twoWay = false,
        SyncDirection direction = SyncDirection.Bidirectional, CancellationToken ct = default)
        => Sync.SaveConfig(space, url, "main", subdirectory,
                createBranchIfMissing: true, createRepoIfMissing: true, direction: direction, twoWay: twoWay)
            .Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<MeshNode> CreateMarkdown(string path, string body, CancellationToken ct)
        => NodeFactory.CreateNode(MeshNode.FromPath(path) with
        {
            NodeType = "Markdown",
            Name = path[(path.LastIndexOf('/') + 1)..],
            State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = body },
        }).Timeout(TestTimeouts.Convergence).Await(ct);

    /// <summary>A Markdown node file as a repository carries one (the shape the importer reads).</summary>
    private static string MarkdownFile(string id, string text)
        => $$$"""{"id":"{{{id}}}","nodeType":"Markdown","name":"{{{id}}}","content":{"$type":"MarkdownContent","content":"# {{{id}}}\n\n{{{text}}}"}}""";

    /// <summary>The Markdown body of the node at <paramref name="path"/>, once it exists.</summary>
    private Task<string> Body(string path, CancellationToken ct)
        => Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .Select(n => n!.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content ?? "")
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private Task<GitHubSyncConfig> ConfigWhen(string space, Func<GitHubSyncConfig, bool> predicate, CancellationToken ct)
        => Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Select(n => n?.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions))
            .Where(c => c is not null && predicate(c))
            .Select(c => c!)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private static Task<Exception> Error<T>(IObservable<T> operation, CancellationToken ct)
        => operation.Materialize()
            .Where(n => n.Kind == System.Reactive.NotificationKind.OnError)
            .Select(n => n.Exception!)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(ct);

    private static string Flatten(ImmutableSortedDictionary<string, string> tree)
        => string.Join("\n", tree.Select(kv => kv.Key + "=" + kv.Value));

    /// <summary>A file's text with the Space-specific ids masked, for a side-by-side comparison.</summary>
    private static string Normalized(string text)
        => System.Text.RegularExpressions.Regex.Replace(text, @"Same[0-9a-f]{8}", "<space>");

    private sealed class StaticCredential(string token) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => new(token, DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
            => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
