using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.GitSync;
using MeshWeaver.Graph;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>An Ignore pattern added AFTER the first import retires what the sync imported under it</b>
/// (Systemorph/MeshWeaver#7611).
///
/// <para><b>What was measured.</b> A one-way Space imported four records; three were then added to the
/// Ignore list. The prune skipped every ignored path (#1326), so the three stayed — neither synced nor
/// removed, frozen at their last imported version — and the write cap on a system-owned Space left no
/// person able to delete them, its Admin included.</para>
///
/// <para><b>The two directions in one run.</b> The pattern <c>Other*</c> matches a node this sync
/// IMPORTED (<c>Other</c>, from <c>Other.md</c>) and a node the MESH created under the same pattern
/// (<c>OtherLocal</c>, never in any repository). The import manifest records only the first, so the
/// first must leave and the second must stay — the second is the #1326 case (mesh-minted records
/// under an ignored path) and is the negative control: a fix that pruned everything the pattern
/// matches would delete it. Before the fix, neither leaves.</para>
/// </summary>
public class AnIgnorePatternRetiresWhatTheSyncImportedTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private const string RepoUrl = "https://github.com/test/ignore-after-import";
    private const string FirstSha = "1111111122223333444455556666777788889999";
    private const string SecondSha = "aaaaaaaabbbbccccddddeeeeffff000011112222";

    private readonly ScriptedRepoClient repoClient = new();

    private static string UserId => TestUsers.Admin.ObjectId!;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddGitHubSyncTypes()
            .ConfigureServices(services =>
            {
                services.AddGitHubSyncServices();
                services.AddSingleton<IGitHubRepoClient>(repoClient);
                return services;
            });

    private GitHubSyncService Sync => Mesh.ServiceProvider.GetRequiredService<GitHubSyncService>();

    [Fact(Timeout = 240_000)]
    public async Task AnIgnorePatternAddedAfterImport_PrunesTheImportedNode_AndKeepsTheMeshMintedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = "Ig" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Ignore after import",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);

        // The mesh-minted node under the pattern-to-be — created BEFORE the Space becomes a one-way
        // (system-owned) mirror, exactly as a node the mesh writes itself would be.
        var meshMinted = $"{space}/OtherLocal";
        await NodeFactory.CreateNode(new MeshNode("OtherLocal", space)
        {
            NodeType = "Markdown",
            Name = "Minted by the mesh",
            State = MeshNodeState.Active,
        }).Timeout(TestTimeouts.Convergence).Await(ct);

        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false,
                direction: SyncDirection.ImportOnly)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var credentials = Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
        foreach (var owner in new[] { configNode.CreatedBy, UserId }.Where(o => !string.IsNullOrEmpty(o)).Distinct())
            await credentials
                .Save(owner!, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
                .Timeout(TestTimeouts.Convergence).Await(ct);

        // ── 1. Both files land.
        repoClient.Serve(FirstSha, complete: true, "Api.md", "Other.md");
        var first = await Sync.ReimportAtCommit(space, FirstSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await(ct);
        Output.WriteLine($"{space} import 1: outcome={first.Outcome} written=[{string.Join(", ", first.WrittenPaths)}]");
        first.WrittenPaths.Should().Contain($"{space}/Other", "precondition: the sync imported the node");

        // ── 2. The owner adds an Ignore pattern; the repository still carries BOTH files.
        await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Update(node => node with
            {
                Content = node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)! with { Ignore = ["Other*"] },
            })
            .Timeout(TestTimeouts.Convergence).Await(ct);
        await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Where(n => n?.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)?.Ignore is { Length: 1 })
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(ct);

        // ── 3. "Update to latest".
        repoClient.Serve(SecondSha, complete: true, "Api.md", "Other.md");
        var second = await Sync.ReimportAtCommit(space, SecondSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await(ct);
        Output.WriteLine($"{space} import 2: outcome={second.Outcome} pruned=[{string.Join(", ", second.PrunedPaths)}]");

        second.PrunedPaths.Should().Contain($"{space}/Other",
            "the sync imported this node and its owner's Ignore pattern now excludes it — left in "
            + "place it is a frozen projection nobody on a one-way Space can remove");
        second.PrunedPaths.Should().NotContain(meshMinted,
            "the mesh created this node; no manifest records the sync putting it there, so the "
            + "pattern matching it is no evidence about it (the #1326 case)");
        second.PrunedPaths.Should().NotContain($"{space}/Api", "the repository still carries it");

        var kept = await Mesh.GetWorkspace().GetMeshNodeStream(meshMinted)
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);
        kept.Should().NotBeNull("the mesh-minted node under the pattern is still there");
    }

    /// <summary>
    /// Review on #7641: an Ignore pattern that excludes EVERY remaining child (here <c>*</c>) leaves
    /// the source with no child nodes at all. The partition snapshot must still be read, or the
    /// manifest-owned nodes are never evaluated and stay frozen in place.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task AnIgnorePatternExcludingEveryChild_StillRetiresWhatTheSyncImported()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await SetUpSpace(ct);

        repoClient.Serve(FirstSha, complete: true, "Other.md");
        var first = await Sync.ReimportAtCommit(space, FirstSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await(ct);
        first.WrittenPaths.Should().Contain($"{space}/Other", "precondition: the sync imported the node");

        await SetIgnore(space, ["*"], ct);

        repoClient.Serve(SecondSha, complete: true, "Other.md");
        var second = await Sync.ReimportAtCommit(space, SecondSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await(ct);
        Output.WriteLine($"{space} import 2: outcome={second.Outcome} pruned=[{string.Join(", ", second.PrunedPaths)}]");

        second.PrunedPaths.Should().Contain($"{space}/Other",
            "the pattern excludes every child, and the manifest records this sync importing it");
    }

    /// <summary>
    /// Review on #7641: retiring an imported node must not delete a node the MESH created beneath it.
    /// <c>DeleteNode</c> is recursive, so pruning <c>Other</c> would take <c>Other/Local</c> with it —
    /// the #1326 loss the provenance guard exists to prevent.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task RetiringAnImportedNode_NeverDeletesAMeshMintedChildBeneathIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await SetUpSpace(ct, beforeConfig: async s =>
        {
            await NodeFactory.CreateNode(new MeshNode("Other", s)
            {
                NodeType = "Markdown",
                Name = "Parent",
                State = MeshNodeState.Active,
            }).Timeout(TestTimeouts.Convergence).Await(ct);
            await NodeFactory.CreateNode(new MeshNode("Local", $"{s}/Other")
            {
                NodeType = "Markdown",
                Name = "Minted by the mesh",
                State = MeshNodeState.Active,
            }).Timeout(TestTimeouts.Convergence).Await(ct);
        });
        var meshMinted = $"{space}/Other/Local";

        repoClient.Serve(FirstSha, complete: true, "Api.md", "Other.md");
        var first = await Sync.ReimportAtCommit(space, FirstSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await(ct);
        first.WrittenPaths.Should().Contain($"{space}/Other", "precondition: the sync imported the parent");

        await SetIgnore(space, ["Other*"], ct);

        repoClient.Serve(SecondSha, complete: true, "Api.md", "Other.md");
        var second = await Sync.ReimportAtCommit(space, SecondSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await(ct);
        Output.WriteLine($"{space} import 2: outcome={second.Outcome} pruned=[{string.Join(", ", second.PrunedPaths)}]");

        second.PrunedPaths.Should().NotContain($"{space}/Other",
            "its recursive delete would take the mesh-minted child with it");
        // Review on #7641: read the KNOWN child from its authoritative node stream, never a query —
        // the eventually-consistent index can still return the pre-delete row after a destructive
        // import, so a query-based survival assertion could not fail.
        (await ReadSurvivor(meshMinted, ct)).Should().NotBeNull(
            "the mesh-minted child under the retired node is still there");
    }

    /// <summary>
    /// Review on #7641: the survivor guard must count what THIS import writes, not only the snapshot
    /// read before the upserts. With <c>Other*</c> plus a <c>!Other/Local*</c> re-include, the
    /// repository's new <c>Other/Local.md</c> is imported in the same run that retires <c>Other</c>;
    /// <c>DeleteNode</c> is recursive, so pruning <c>Other</c> would delete the child it just wrote.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task RetiringAnImportedNode_NeverDeletesAChildTheSameImportWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var space = await SetUpSpace(ct);
        var child = $"{space}/Other/Local";

        repoClient.Serve(FirstSha, complete: true, "Api.md", "Other.md");
        var first = await Sync.ReimportAtCommit(space, FirstSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await(ct);
        first.WrittenPaths.Should().Contain($"{space}/Other", "precondition: the sync imported the parent");

        await SetIgnore(space, ["Other*", "!Other/Local*"], ct);

        repoClient.Serve(SecondSha, complete: true, "Api.md", "Other.md", "Other/Local.md");
        var second = await Sync.ReimportAtCommit(space, SecondSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await(ct);
        Output.WriteLine($"{space} import 2: outcome={second.Outcome} written=[{string.Join(", ", second.WrittenPaths)}] pruned=[{string.Join(", ", second.PrunedPaths)}]");

        second.WrittenPaths.Should().Contain(child, "precondition: the re-include lets the new child import");
        second.PrunedPaths.Should().NotContain($"{space}/Other",
            "its recursive delete would take the child this same import just wrote");
        (await ReadSurvivor(child, ct)).Should().NotBeNull("the child the import wrote is still there");
    }

    /// <summary>
    /// Review on #7641: the survivor guard must see the FINAL prune set. In a bidirectional Space a
    /// child edited on the server since the last sync is kept from the prune by the conflict policy
    /// (#604) — but that filter ran AFTER the survivor guard, so with the parent and the child both
    /// retired by one pattern the guard saw both as leaving, kept the parent a candidate, and the
    /// parent's recursive <c>DeleteNode</c> then took the protected edit with it.
    /// </summary>
    [Fact(Timeout = 240_000)]
    public async Task RetiringAnImportedParent_NeverDeletesAPolicyProtectedChildEdit()
    {
        var ct = TestContext.Current.CancellationToken;
        // Two-way, so the Space is not system-owned and a person may edit its content.
        var space = await SetUpSpace(ct, direction: SyncDirection.Bidirectional);
        var child = $"{space}/Other/Local";

        repoClient.Serve(FirstSha, complete: true, "Api.md", "Other.md", "Other/Local.md");
        var first = await Sync.ReimportAtCommit(space, FirstSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await(ct);
        first.WrittenPaths.Should().Contain($"{space}/Other", "precondition: the sync imported the parent");
        first.WrittenPaths.Should().Contain(child, "precondition: the sync imported the child");

        // The baseline the conflict policy compares against is recorded by the first import; the
        // edit must land after it to count as "changed on the server since the last sync".
        var since = await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Select(n => n?.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)?.LastSyncedAt)
            .Where(t => t is not null)
            .Select(t => t!.Value)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(ct);
        await Mesh.GetWorkspace().GetMeshNodeStream(child)
            .Update(node => node with { Name = "Edited on the server" })
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var edited = await Mesh.GetWorkspace().GetMeshNodeStream(child)
            .Where(n => n is { Name: "Edited on the server" })
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(ct);
        new ImportConflictPolicy(PreserveServerNewer: true, since, PreserveServerAdditions: true)
            .PreservesFromPruneOf(edited)
            .Should().BeTrue("precondition: the policy protects the server-side edit of the child");

        await SetIgnore(space, ["Other*"], ct);

        repoClient.Serve(SecondSha, complete: true, "Api.md", "Other.md", "Other/Local.md");
        var second = await Sync.ReimportAtCommit(space, SecondSha, UserId)
            .Timeout(TestTimeouts.Convergence * 2).Await(ct);
        Output.WriteLine($"{space} import 2: outcome={second.Outcome} pruned=[{string.Join(", ", second.PrunedPaths)}]");

        second.PrunedPaths.Should().NotContain($"{space}/Other",
            "its recursive delete would take the policy-protected child edit with it");
        (await ReadSurvivor(child, ct)).Name.Should().Be("Edited on the server",
            "the server-side edit the policy protects is still there");
    }

    /// <summary>
    /// Review on #7641: a manifest entry with the refusal prefix records that the source's write was
    /// REFUSED — not that the source put the node there. So an Ignore pattern that later excludes the
    /// path must leave the node spared as never-mirrored (it may be one the mesh created), while a
    /// successful-import token for the same path makes it retirable. Pins the exact predicate the
    /// import hands to <see cref="StaticRepoImporter.ComputePrunableNodes"/>.
    /// </summary>
    [Fact]
    public void ARefusalEntryInTheManifest_NeverMakesAnIgnoredNodePrunable()
    {
        var source = new InMemoryStaticRepoSource("Sp", [], null, ignore: new SyncIgnore(["Other*"]));
        var other = new MeshNode("Other", "Sp") { NodeType = "Markdown" };

        var refused = new Dictionary<string, string>
        {
            ["Sp/Other"] = StaticRepoImporter.RefusedTokenPrefix + "0123abcd|refused by a validator",
        };
        var refusedGuard = StaticRepoImporter.SparedAsNeverMirrored(source, refused);
        refusedGuard("Sp/Other").Should().BeTrue(
            "a refused write is no evidence the source put the node here, so it stays spared");
        StaticRepoImporter.ComputePrunableNodes(
                [other], [], refused.Keys, [], PartitionSyncMode.FullReplace, refusedGuard)
            .Should().BeEmpty("a refusal entry must not qualify the node for deletion");

        var imported = new Dictionary<string, string> { ["Sp/Other"] = "0123abcd" };
        var importedGuard = StaticRepoImporter.SparedAsNeverMirrored(source, imported);
        importedGuard("Sp/Other").Should().BeFalse("the sync imported it and the owner's pattern retires it");
        StaticRepoImporter.ComputePrunableNodes(
                [other], [], imported.Keys, [], PartitionSyncMode.FullReplace, importedGuard)
            .Should().ContainSingle(n => n.Path == "Sp/Other");

        importedGuard("Sp/Api").Should().BeFalse("no pattern excludes it, so the guard has nothing to spare");
    }

    /// <summary>Authoritative read of a node expected to exist; times out (fails) when it was deleted.</summary>
    private Task<MeshNode> ReadSurvivor(string path, CancellationToken ct)
        => Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence)
            .Await(ct);

    private async Task<string> SetUpSpace(CancellationToken ct, Func<string, Task>? beforeConfig = null,
        SyncDirection direction = SyncDirection.ImportOnly)
    {
        var space = "Ig" + Guid.NewGuid().ToString("N")[..8];
        await NodeFactory.CreateNode(new MeshNode(space)
        {
            NodeType = "Space",
            Name = "Ignore after import",
            State = MeshNodeState.Active,
            Content = new Space(),
        }).Timeout(TestTimeouts.Convergence).Await(ct);
        if (beforeConfig is not null)
            await beforeConfig(space);
        var configNode = await Sync
            .SaveConfig(space, RepoUrl, "main", null,
                createBranchIfMissing: false, createRepoIfMissing: false,
                direction: direction, twoWay: direction == SyncDirection.Bidirectional)
            .Timeout(TestTimeouts.Convergence).Await(ct);
        var credentials = Mesh.ServiceProvider.GetRequiredService<GitHubCredentialService>();
        foreach (var owner in new[] { configNode.CreatedBy, UserId }.Where(o => !string.IsNullOrEmpty(o)).Distinct())
            await credentials
                .Save(owner!, new GitHubToken("ghp_test_token", null, "bearer", "repo", null), "octocat")
                .Timeout(TestTimeouts.Convergence).Await(ct);
        return space;
    }

    private async Task SetIgnore(string space, string[] ignore, CancellationToken ct)
    {
        await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Update(node => node with
            {
                Content = node.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)! with { Ignore = ignore },
            })
            .Timeout(TestTimeouts.Convergence).Await(ct);
        await Mesh.GetWorkspace().GetMeshNodeStream(GitHubSyncService.ConfigPath(space))
            .Where(n => n?.ContentAs<GitHubSyncConfig>(Mesh.JsonSerializerOptions)?.Ignore is { } i
                        && i.SequenceEqual(ignore))
            .FirstAsync()
            .Timeout(TestTimeouts.Convergence).Await(ct);
    }

    /// <summary>
    /// The retirement predicate covers ONLY what the owner's patterns exclude. A HELD path (a NodeType
    /// whose sources this instance must keep, #3845) and the README slot a non-package repository
    /// never mirrors are excluded from the mirror too, but nobody wrote a rule retiring them — reading
    /// them as retired would let the prune delete sources a hold exists to keep.
    /// </summary>
    [Fact]
    public void OnlyAPatternRetiresANode_NeverAHoldOrTheReadmeSlot()
    {
        var source = new InMemoryStaticRepoSource(
            "Sp", [], null, ignore: new SyncIgnore(["Other*"]).WithHeldPaths(["Held"]));

        source.IsExcludedFromMirror("Sp/Other").Should().BeTrue();
        source.IsRetiredFromMirror("Sp/Other").Should().BeTrue("the owner's pattern excludes it");

        source.IsExcludedFromMirror("Sp/Held/Source").Should().BeTrue();
        source.IsRetiredFromMirror("Sp/Held/Source").Should().BeFalse("a hold keeps sources; it retires nothing");

        source.IsExcludedFromMirror("Sp/README").Should().BeTrue();
        source.IsRetiredFromMirror("Sp/README").Should().BeFalse("the README slot is never mirrored, not retired");

        source.IsRetiredFromMirror("Sp").Should().BeFalse("the Space root is never ignored");
        source.IsRetiredFromMirror("Elsewhere/Other").Should().BeFalse("another partition is not this source's");
        source.IsRetiredFromMirror("Sp/Api").Should().BeFalse("no pattern excludes it");
    }

    /// <summary>
    /// The GitHub transport reduced to the three things this test scripts: which commit, which files,
    /// and — the whole subject — whether the listing that produced them was COMPLETE. Everything else
    /// throws, so a future caller that starts depending on another operation is told rather than
    /// silently served a fake answer.
    /// </summary>
    private sealed class ScriptedRepoClient : IGitHubRepoClient
    {
        private RepoSnapshot snapshot = new("0000000000000000000000000000000000000000", [])
            { ListingIsComplete = true };

        /// <summary>Scripts the next fetch: the commit, the completeness verdict, and the files.</summary>
        public void Serve(string sha, bool complete, params string[] files) =>
            snapshot = new RepoSnapshot(
                sha,
                Array.ConvertAll(files, f => new RepoFile(f, $"# {f}\n\nContent of {f}.\n")))
            {
                // 🚨 THE ONE BIT UNDER TEST. Production sets this from GitHub's `truncated` flag on
                // the recursive-tree response — an HTTP 200 whose body is only part of the answer.
                ListingIsComplete = complete,
            };

        public IObservable<RepoSnapshot> Fetch(
            string repositoryUrl, string commitish, string? subdirectory, string accessToken)
            => Observable.Return(snapshot);

        // The default GetChangedPaths (null → full import) is deliberately inherited: a client with no
        // compare support is the documented safe fallback, and a diff-scoped run would not reach the
        // prune phase this test is about.

        public IObservable<GitHubPushResult> Push(GitHubPushRequest request) => NotUsed<GitHubPushResult>();

        public IObservable<GitHubBranchResult> CreateBranch(GitHubCreateBranchRequest request)
            => NotUsed<GitHubBranchResult>();

        public IObservable<GitHubPullRequestInfo> OpenPullRequest(GitHubOpenPullRequestRequest request)
            => NotUsed<GitHubPullRequestInfo>();

        public IObservable<GitHubPullRequestInfo> GetPullRequestStatus(
            string repositoryUrl, int number, string accessToken) => NotUsed<GitHubPullRequestInfo>();

        public IObservable<IReadOnlyList<GitHubIssue>> ListIssues(
            string repositoryUrl, GitHubIssueState? state, string accessToken)
            => NotUsed<IReadOnlyList<GitHubIssue>>();

        public IObservable<GitHubIssue> GetIssue(string repositoryUrl, int number, string accessToken)
            => NotUsed<GitHubIssue>();

        public IObservable<GitHubIssue> CreateIssue(GitHubCreateIssueRequest request) => NotUsed<GitHubIssue>();

        public IObservable<GitHubIssueComment> CommentIssue(
            string repositoryUrl, int number, string body, string accessToken)
            => NotUsed<GitHubIssueComment>();

        public IObservable<GitHubIssue> SetIssueState(
            string repositoryUrl, int number, GitHubIssueState state, string accessToken)
            => NotUsed<GitHubIssue>();

        public IObservable<IReadOnlyList<GitHubPullRequestSummary>> ListPullRequests(
            string repositoryUrl, PullRequestStatus? state, string accessToken)
            => NotUsed<IReadOnlyList<GitHubPullRequestSummary>>();

        public IObservable<GitHubPullRequestDetail> GetPullRequestDetail(
            string repositoryUrl, int number, string accessToken) => NotUsed<GitHubPullRequestDetail>();

        public IObservable<GitHubIssueComment> CommentPullRequest(
            string repositoryUrl, int number, string body, string accessToken)
            => NotUsed<GitHubIssueComment>();

        public IObservable<GitHubMergeResult> MergePullRequest(GitHubMergePullRequestRequest request)
            => NotUsed<GitHubMergeResult>();

        private static IObservable<T> NotUsed<T>() => Observable.Throw<T>(
            new NotSupportedException(
                "AnIgnorePatternRetiresWhatTheSyncImportedTest's repo client answers only Fetch."));
    }
}
