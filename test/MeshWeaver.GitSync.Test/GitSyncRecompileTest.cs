using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.GitSync.Test;

/// <summary>
/// Gap C-1: a GitSync UPDATE must RECOMPILE what it changed — the sync transaction itself requests
/// the NodeType releases, no human "sync → recompile → verify compiledSources" ritual.
///
/// <para>Pins both halves of the recompile derivation through the FULL sync loop (export → edit in
/// the repo → re-import):</para>
/// <list type="bullet">
///   <item>a changed <b>shared Source node</b> release-requests the OWNING type and every
///     <c>shared=@</c> SHARER (the generalized 2026-08-04 ParameterSegment incident);</item>
///   <item>a <b>content-only</b> change (no Code/NodeType nodes) requests ZERO releases — releasing
///     types on every sync is the compile-storm failure mode.</item>
/// </list>
/// </summary>
public class GitSyncRecompileTest(ITestOutputHelper output) : GitHubSyncTestBase(output)
{
    /// <summary>
    /// A synced Space with the shared-source shape: type <c>Widget</c> owning
    /// <c>Widget/Source/Helper</c>, type <c>Consumer</c> compiling that same source via
    /// <c>shared=@{space}/Widget/Source</c>, and a plain markdown node as the content-only control.
    /// Exported, then RE-imported once so the per-node import manifest exists — the follow-up
    /// import in each test therefore writes ONLY what the test actually changed.
    /// </summary>
    private async Task<(string Space, string Repo)> SetUpSyncedSpace()
    {
        await Connect();
        var space = "GhR" + Guid.NewGuid().ToString("N")[..8];
        await CreateSpace(space, "Recompile");

        await NodeFactory.CreateNode(new MeshNode("Widget", space)
        {
            NodeType = MeshNode.NodeTypePath,
            Name = "Widget",
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition { Configuration = "config => config" },
        }).Timeout(60.Seconds()).ToTask();
        await NodeFactory.CreateNode(new MeshNode("Helper", $"{space}/Widget/Source")
        {
            NodeType = CodeNodeType.NodeType,
            Name = "Helper",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Code = "public static class WidgetHelper { }", Language = "csharp" },
        }).Timeout(60.Seconds()).ToTask();
        await NodeFactory.CreateNode(new MeshNode("Consumer", space)
        {
            NodeType = MeshNode.NodeTypePath,
            Name = "Consumer",
            State = MeshNodeState.Active,
            Content = new NodeTypeDefinition
            {
                Configuration = "config => config",
                Sources = ["namespace:Source scope:subtree", $"shared=@{space}/Widget/Source"],
            },
        }).Timeout(60.Seconds()).ToTask();
        await CreateMarkdown($"{space}/Notes", "Notes", "hello");

        var repo = "https://github.com/test/space-recompile";
        await Sync.SaveConfig(space, repo, "main", subdirectory: null,
                createBranchIfMissing: true, createRepoIfMissing: true,
                direction: SyncDirection.Bidirectional, sourceId: null, twoWay: false)
            .Timeout(30.Seconds()).ToTask();
        var commit = await Sync.SyncToGitHub(space, UserId).Timeout(60.Seconds()).ToTask();
        await WaitForConfig(space, c => c.LastSyncCommitSha == commit.CommitSha);

        // Baseline import: the FIRST re-import has no per-node manifest, so it writes every node —
        // including both type nodes, which therefore get release-requested here. Wait for those
        // baseline triggers to LAND so each test's before-snapshot is stable.
        await Sync.ReimportAtCommit(space, "main", UserId).Timeout(90.Seconds()).ToTask();
        await WaitForRelease($"{space}/Widget", after: null);
        await WaitForRelease($"{space}/Consumer", after: null);
        return (space, repo);
    }

    private async Task<DateTimeOffset?> ReleaseRequestedAt(string typePath) =>
        (await ReadNode(typePath).Timeout(30.Seconds()).ToTask())
            ?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.RequestedReleaseAt;

    /// <summary>Waits until the type's release trigger has ADVANCED past <paramref name="after"/>
    /// (null = any trigger at all) and returns the new timestamp.</summary>
    private async Task<DateTimeOffset> WaitForRelease(string typePath, DateTimeOffset? after) =>
        await Observable.Interval(100.Milliseconds()).StartWith(0L)
            .SelectMany(_ => ReadNode(typePath))
            .Select(n => n?.ContentAs<NodeTypeDefinition>(Mesh.JsonSerializerOptions)?.RequestedReleaseAt)
            .Where(at => at is not null && (after is null || at > after))
            .Select(at => at!.Value)
            .FirstAsync()
            .Timeout(30.Seconds())
            .ToTask();

    /// <summary>Replaces one file's content in the fake repo and pushes the tree as the new HEAD.</summary>
    private async Task PushEdit(string repo, string pathSuffix, Func<string, string> edit)
    {
        var tree = Fake.Tree(repo);
        var file = tree.Single(f => f.Path.EndsWith(pathSuffix, StringComparison.Ordinal));
        var seeded = tree
            .Select(f => f.Path == file.Path ? f with { Content = edit(f.Content) } : f)
            .ToImmutableList();
        await Fake.Push(new GitHubPushRequest
        {
            RepositoryUrl = repo,
            Branch = "main",
            Files = seeded,
            CommitMessage = $"edit {pathSuffix}",
            AuthorName = "test",
            AuthorEmail = "test@test",
            AccessToken = "x",
        }).Timeout(30.Seconds()).ToTask();
    }

    [Fact(Timeout = 180000)]
    public async Task SharedSourceChange_ReleasesOwnerAndSharer()
    {
        var (space, repo) = await SetUpSyncedSpace();
        var ownerBefore = await ReleaseRequestedAt($"{space}/Widget");
        var sharerBefore = await ReleaseRequestedAt($"{space}/Consumer");

        // Change ONLY the shared source file in the repo, then sync.
        await PushEdit(repo, "Helper.cs", content => content + "\n// touched by the update");
        var result = await Sync.ReimportAtCommit(space, "main", UserId).Timeout(90.Seconds()).ToTask();

        // The import wrote exactly the changed source node…
        result.WrittenPaths.Should().Equal([$"{space}/Widget/Source/Helper"],
            "the per-node manifest skip must confine the write to what the commit changed");
        // …and the sync transaction release-requested BOTH affected types: the owner (its own
        // Source subtree) and the sharer (shared=@…/Widget/Source) — the ParameterSegment fix.
        await WaitForRelease($"{space}/Widget", after: ownerBefore);
        await WaitForRelease($"{space}/Consumer", after: sharerBefore);
    }

    [Fact(Timeout = 180000)]
    public async Task ContentOnlyChange_ReleasesNothing()
    {
        var (space, repo) = await SetUpSyncedSpace();
        var ownerBefore = await ReleaseRequestedAt($"{space}/Widget");
        var sharerBefore = await ReleaseRequestedAt($"{space}/Consumer");

        // Change ONLY the markdown node, then sync.
        await PushEdit(repo, "Notes.md", content => content.Replace("hello", "hello again"));
        var result = await Sync.ReimportAtCommit(space, "main", UserId).Timeout(90.Seconds()).ToTask();

        result.WrittenPaths.Should().Equal([$"{space}/Notes"]);
        // Negative: give a (wrong) release request time to land, then confirm neither type moved.
        // Sanctioned Task.Delay — a "wait to confirm nothing happened" test has no positive signal
        // to filter for.
        await Task.Delay(2000);
        (await ReleaseRequestedAt($"{space}/Widget")).Should().Be(ownerBefore,
            "a content-only sync must trigger zero recompiles");
        (await ReleaseRequestedAt($"{space}/Consumer")).Should().Be(sharerBefore,
            "a content-only sync must trigger zero recompiles");
    }
}
