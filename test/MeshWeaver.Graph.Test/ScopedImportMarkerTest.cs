using MeshWeaver.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A SCOPED IMPORT MAY NOT LEAVE A FULL-CONTENT "ALREADY IMPORTED" MARKER (issue #1326), and a Git
/// diff cannot stand in for the current parser's view of unchanged source files.
///
/// <para>The importer short-circuits on a content-addressed marker: a Succeeded activity at
/// <c>{Partition}/_Activity/import-{fingerprint}</c> means "this partition already holds exactly this
/// content", so a later run with the same fingerprint returns <c>Skipped</c> — <b>without reading the
/// partition at all</b>. The fingerprint hashes EVERY source node.</para>
///
/// <para>A git-diff-scoped run (<c>changedNodePaths</c>, the routine webhook path) normally evaluates
/// only the nodes named by the diff. However, an unchanged JSON file can parse into different typed
/// content after a model upgrade: an older image may have silently discarded a property that a newer
/// image now recognizes. The per-node import manifest detects that materialization drift and makes the
/// importer re-evaluate just that node, while missing/stale manifest entries keep the existing cheap
/// out-of-diff skip.</para>
///
/// <para>A scoped pass still cannot stamp a whole-partition success marker (issue #1326), because the
/// git diff does not prove every node was evaluated. These tests assert the actual live node content
/// and verify the normal two-way conflict path still preserves a newer server-authored edit.</para>
/// </summary>
public class ScopedImportMarkerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private static readonly AccessContext Author = new()
    {
        ObjectId = "alice",
        Name = "Alice",
        Email = "alice@acme.com",
    };

    [Fact(Timeout = 240000)]
    public async Task ScopedImport_ReconcilesManifestDriftOutsideGitDiff_WithoutStampingAFullMarker()
    {
        var partition = "Sc" + Guid.NewGuid().ToString("N")[..8];
        var source = new FakeRepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "A", "v1"), Page(partition, "B", "v1"), Page(partition, "C", "v1")],
        };

        // 1. The FIRST import is unscoped — it really did materialize the whole content, so it may
        //    (and must) record the marker its fingerprint names.
        var first = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken);
        first.Outcome.Should().Be("Imported");
        (await Body($"{partition}/A")).Should().Contain("v1");
        (await Body($"{partition}/B")).Should().Contain("v1");
        (await Body($"{partition}/C")).Should().Contain("v1");

        // 2. BOTH parsed pages change, but the git diff names only A. In GitSync this can happen
        //    when a newer platform's JSON model recognizes fields the previous model discarded: the
        //    repo bytes for B are unchanged, but the source token from parsing them has advanced.
        //    The manifest proves the token differs, so B must be re-evaluated despite being outside
        //    the Git diff.
        source.Nodes = [Page(partition, "A", "v2"), Page(partition, "B", "v2"), Page(partition, "C", "v1")];
        var scoped = await StaticRepoImporter
            .ImportSource(Mesh, source, null, null, new HashSet<string> { $"{partition}/A" })
            .FirstAsync().Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"scoped run: outcome={scoped.Outcome} count={scoped.Count} "
            + $"written=[{string.Join(", ", scoped.WrittenPaths)}]");
        scoped.WrittenPaths.Should().Contain($"{partition}/B",
            "the token recorded by the prior parser differs, so the importer must evaluate this node "
            + "even though Git reports no file change");
        scoped.WrittenPaths.Should().NotContain($"{partition}/C",
            "a matching manifest token outside the Git diff must remain a no-op");
        (await Body($"{partition}/B")).Should().Contain("v2",
            "an unchanged Git file can parse to different content after a model upgrade; the token "
            + "mismatch must re-materialize it");
        (await Body($"{partition}/C")).Should().Contain("v1",
            "an unchanged parser output outside the diff should remain untouched");

        // 3. …and now the same content is imported WITHOUT a scope (a manual "Update", a boot import).
        //    A scoped run still cannot stamp the full-content success marker, even when manifest drift
        //    happened to bring every node up to date.
        var full = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken);
        Output.WriteLine($"unscoped run: outcome={full.Outcome} count={full.Count}");

        full.Outcome.Should().NotBe("Skipped",
            "a scoped run evaluated only part of the content, so it must not license a later full "
            + "import to skip on its fingerprint — that marker is the 'Skipped (0 nodes) while behind' "
            + "lie, and it is permanent once written");
        (await Body($"{partition}/B")).Should().Contain("v2",
            "the unscoped import must agree with the source after the schema-drift re-evaluation");
    }

    [Fact(Timeout = 240000)]
    public async Task ScopedImport_ManifestDriftStillPreservesANewerTwoWayServerEdit()
    {
        var partition = "Sc" + Guid.NewGuid().ToString("N")[..8];
        var path = $"{partition}/A";
        var horizon = DateTimeOffset.UtcNow.AddMinutes(-1);
        var source = new FakeRepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "A", "repo-v1")],
        };

        (await StaticRepoImporter.ImportSource(Mesh, source)
                .FirstAsync().Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken))
            .Outcome.Should().Be("Imported");

        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        IObservable<MeshNode> edit;
        using (access.SwitchAccessContext(Author))
            edit = Mesh.GetWorkspace().GetMeshNodeStream(path).Update(node => node with
            {
                Content = new MarkdownContent { Content = "# Page A\n\nlocal-edit" }
            });
        await edit.FirstAsync().Timeout(60.Seconds()).Await(TestContext.Current.CancellationToken);

        source.Nodes = [Page(partition, "A", "repo-v2")];
        var reimport = await StaticRepoImporter.ImportSource(
                Mesh,
                source,
                policy: new ImportConflictPolicy(PreserveServerNewer: true, Since: horizon),
                changedNodePaths: new HashSet<string>())
            .FirstAsync().Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken);

        reimport.Preserved.Should().Be(1,
            "a parsed-token drift makes the node eligible for conflict evaluation; it does not "
            + "disable the two-way protection");
        reimport.WrittenPaths.Should().NotContain(path);
        (await Body(path)).Should().Contain("local-edit",
            "the newer server-authored node must remain authoritative under two-way sync");

        // A PRESERVED drifted node was not written, so the manifest may not claim its new source
        // token (#1326's rule, the two-way direction). Were it claimed, a later pass in which the
        // server copy no longer wins — here: no two-way policy at all, e.g. the local edit was
        // discarded — would read "already at this content", skip the node, and leave it divergent
        // from the source for good. Keeping the prior token is what makes this pass evaluate it.
        var afterDiscard = await StaticRepoImporter.ImportSource(
                Mesh,
                source,
                changedNodePaths: new HashSet<string>())
            .FirstAsync().Timeout(120.Seconds()).Await(TestContext.Current.CancellationToken);
        afterDiscard.WrittenPaths.Should().Contain(path,
            "the preserved pass must not have recorded repo-v2's token in the manifest; the node "
            + "still differs from what the manifest last saw land, so it must be re-evaluated");
        (await BodyContaining(path, "repo-v2")).Should().Contain("repo-v2",
            "once the server copy no longer wins, the source must converge the node");
    }

    /// <summary>
    /// The complement, so the fix cannot be "never short-circuit again": an UNSCOPED import really did
    /// evaluate the whole content, so re-importing it unchanged must still take the cheap path. Without
    /// this, disarming the marker for every run would silently re-materialize whole partitions on every
    /// boot — the recompile storm the fingerprint gate exists to prevent.
    /// </summary>
    [Fact(Timeout = 240000)]
    public async Task UnscopedImport_StillShortCircuitsOnItsOwnFingerprint()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var partition = "Sk" + Guid.NewGuid().ToString("N")[..8];
        var source = new FakeRepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "A", "v1")],
        };

        (await StaticRepoImporter.ImportSource(Mesh, source).FirstAsync().Timeout(120.Seconds()).Await())
            .Outcome.Should().Be("Imported");

        var again = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(120.Seconds()).Await();
        again.Outcome.Should().Be("Skipped",
            "the previous run WAS unscoped, so its marker is honest evidence and the short-circuit "
            + "must still fire — the fix narrows who may write the marker, not who may read it");
    }

    /// <summary>The live markdown body of a node — read through the authoritative node stream, never
    /// the eventually-consistent query, so "still stale" is a fact and not a lag.</summary>
    private async Task<string> Body(string path)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n is not null)
            .FirstAsync().Timeout(30.Seconds()).Await();
        return node.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content ?? "";
    }

    /// <summary>Waits for the write's content change to reach the live node stream.</summary>
    private async Task<string> BodyContaining(string path, string expected)
    {
        var node = await Mesh.GetWorkspace().GetMeshNodeStream(path)
            .Where(n => n?.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content?.Contains(expected) == true)
            .FirstAsync().Timeout(TestTimeouts.Convergence).Await(TestContext.Current.CancellationToken);
        return node.ContentAs<MarkdownContent>(Mesh.JsonSerializerOptions)?.Content ?? "";
    }

    private static MeshNode Space(string partition) => new(partition)
    {
        Name = "Scoped Import", NodeType = "Space", State = MeshNodeState.Active,
        Content = new MarkdownContent { Content = "# Scoped Import\n\nfixture." }
    };

    private static MeshNode Page(string partition, string id, string revision) =>
        new(id, partition)
        {
            NodeType = "Markdown", Name = $"Page {id}", State = MeshNodeState.Active,
            Content = new MarkdownContent { Content = $"# Page {id}\n\n{revision}" }
        };

    private sealed class FakeRepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; set; } = [];
        public MeshNode? Root { get; set; }
        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
    }
}
