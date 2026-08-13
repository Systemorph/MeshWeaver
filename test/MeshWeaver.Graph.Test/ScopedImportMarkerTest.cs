using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Markdown;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 A SCOPED IMPORT MAY NOT LEAVE A FULL-CONTENT "ALREADY IMPORTED" MARKER (issue #1326).
///
/// <para>The importer short-circuits on a content-addressed marker: a Succeeded activity at
/// <c>{Partition}/_Activity/import-{fingerprint}</c> means "this partition already holds exactly this
/// content", so a later run with the same fingerprint returns <c>Skipped</c> — <b>without reading the
/// partition at all</b>. The fingerprint hashes EVERY source node.</para>
///
/// <para>But a git-diff-scoped run (<c>changedNodePaths</c>, the routine webhook path) only evaluates
/// the handful of nodes the diff named. Stamping the FULL-content marker after such a run asserts
/// completeness on the evidence of a partial pass — and it is permanent: from then on every import of
/// that same repo content reports <c>Skipped (0 nodes)</c>, and <c>GitHubSyncService</c> reads that as
/// "the mesh already has this commit" and advances <c>LastSyncCommitSha</c> to the head, so the next
/// diff is empty forever. A Space that under-imported once could never converge again; only
/// <c>force</c> — which bypasses both the diff and the marker — fixed it. That is the memex-cloud
/// report of 2026-08-12 (<c>ThreeBody</c>: "Imported Skipped (0 node(s))" while genuinely behind).</para>
///
/// <para>What is asserted is CONVERGENCE, not a log string: after a scoped run that could not have
/// seen node C, a later import of the same content must actually materialize C. Before the fix that
/// import returned <c>Skipped</c> and C stayed missing forever.</para>
/// </summary>
public class ScopedImportMarkerTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact(Timeout = 240000)]
    public async Task ScopedImport_DoesNotMakeALaterFullImportSkipTheNodesItNeverSaw()
    {
        var partition = "Sc" + Guid.NewGuid().ToString("N")[..8];
        var source = new FakeRepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "A", "v1"), Page(partition, "B", "v1")],
        };

        // 1. The FIRST import is unscoped — it really did materialize the whole content, so it may
        //    (and must) record the marker its fingerprint names.
        var first = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(120.Seconds());
        first.Outcome.Should().Be("Imported");
        (await Body($"{partition}/A")).Should().Contain("v1");
        (await Body($"{partition}/B")).Should().Contain("v1");

        // 2. BOTH pages change in the repo, but the git diff the webhook computed names only A. A
        //    scoped run skips the upsert of every EXISTING node outside the scope — so B is left at
        //    v1 while the source (and the fingerprint) say v2. That is exactly "the Space is behind":
        //    the nodes are all there, their content is stale.
        source.Nodes = [Page(partition, "A", "v2"), Page(partition, "B", "v2")];
        var scoped = await StaticRepoImporter
            .ImportSource(Mesh, source, null, null, new HashSet<string> { $"{partition}/A" })
            .FirstAsync().Timeout(120.Seconds());
        Output.WriteLine($"scoped run: outcome={scoped.Outcome} count={scoped.Count} "
            + $"written=[{string.Join(", ", scoped.WrittenPaths)}]");
        (await Body($"{partition}/B")).Should().Contain("v1",
            "the scope excluded B, so the scoped run must have left it stale — otherwise this test is "
            + "not exercising the under-import it exists to detect");

        // 3. …and now the same content is imported WITHOUT a scope (a manual "Update", a boot import).
        //    It must actually reconcile. Before the fix the scoped run had already stamped this exact
        //    fingerprint Succeeded, so this returned "Skipped" and B stayed at v1 forever.
        var full = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(120.Seconds());
        Output.WriteLine($"unscoped run: outcome={full.Outcome} count={full.Count}");

        full.Outcome.Should().NotBe("Skipped",
            "a scoped run evaluated only part of the content, so it must not license a later full "
            + "import to skip on its fingerprint — that marker is the 'Skipped (0 nodes) while behind' "
            + "lie, and it is permanent once written");
        (await Body($"{partition}/B")).Should().Contain("v2",
            "the unscoped import must converge the partition to the source; a Space left stale by a "
            + "scoped run has no other way back (only `force` bypassed the marker)");
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
        var partition = "Sk" + Guid.NewGuid().ToString("N")[..8];
        var source = new FakeRepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "A", "v1")],
        };

        (await StaticRepoImporter.ImportSource(Mesh, source).FirstAsync().Timeout(120.Seconds()))
            .Outcome.Should().Be("Imported");

        var again = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(120.Seconds());
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
            .FirstAsync().Timeout(30.Seconds());
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
