using System;
using System.Collections.Generic;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Utils;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Issue #3101 — a Space whose content sync is refused stopped syncing SILENTLY.</b>
///
/// <para><b>The mechanism.</b> Both content passes folded a failed sync into <c>0</c> files:</para>
///
/// <code>
/// .Select(r => r.Success ? r.FilesImported : 0)   // no log at all on the false arm
/// </code>
///
/// <para>Zero files is exactly what a Space with no content reports, so the import summed zero,
/// returned <c>"Imported"</c>, and <c>StampLock</c> wrote <b>Succeeded</b> at that fingerprint. And
/// Succeeded at a fingerprint IS the durable short-circuit: every later import of the same repo
/// content skipped the Space <i>without reading it</i>. A Space whose assets were refused on every
/// attempt was indistinguishable from one fully in sync — permanently, and the person who found out
/// was a learner opening a page with a missing video.</para>
///
/// <para>This is not a new argument. The importer's own terminal-status block already makes it for a
/// blocked create (#2211): <i>"the source declares it, the claim refuses its creation, and no boot
/// can ever change that … recording it there froze the divergence permanently and invisibly."</i>
/// A transport that refuses a Space's assets is that shape verbatim, and it was missing the same
/// escalation.</para>
///
/// <para>🚨 <b>What this pins is the OUTCOME, not the log.</b> The attempt activity is the
/// human-readable record; the <c>Outcome</c> is what becomes the marker, and the marker is what
/// decides whether the next boot looks at the Space at all. A fix that coloured only the attempt
/// would leave the lock green and the Space skipped for ever — it would look right and change
/// nothing, so that is the assertion.</para>
/// </summary>
public class RefusedContentSyncIsNotSuccessTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>A source that ships nodes and, optionally, one inline content sync.</summary>
    private sealed class ContentRepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; init; } = [];
        public MeshNode? Root { get; init; }
        public List<StaticContentSync> Syncs { get; init; } = [];

        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
        public IReadOnlyList<StaticContentSync> EnumerateInlineContentSyncs() => Syncs;
    }

    private static MeshNode Space(string partition) =>
        new(partition) { Name = partition, NodeType = "Space", State = MeshNodeState.Active };

    private static MeshNode Page(string partition, string id) =>
        new(id, partition) { Name = id, NodeType = "Markdown", State = MeshNodeState.Active };

    /// <summary>
    /// 🚨 THE PIN. A refused content sync must NOT produce the outcome that stamps a green marker.
    ///
    /// <para>The sync is aimed at a node in a partition this source does not own and that does not
    /// exist, so the delivery cannot land — the deterministic stand-in for the oversized-delivery
    /// refusal that took <c>AgenticEngineering</c>'s 106 MB of assets out of the mesh. What matters
    /// is that the sync did not succeed, not which of the two ways it failed: both arms previously
    /// folded to <c>0</c> and neither reached the outcome.</para>
    ///
    /// <para>Pre-fix this returns <c>"Imported"</c> → the lock is stamped <b>Succeeded</b> → the
    /// Space is skipped for ever.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ARefusedContentSync_DoesNotStampAGreenMarker()
    {
        var partition = "Rc" + Guid.NewGuid().ToString("N")[..8];
        var source = new ContentRepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "Lesson")],
            Syncs =
            [
                new StaticContentSync(
                    // A node that does not exist, in a partition this source does not own.
                    NodePath: $"{partition}Missing/Nowhere",
                    Files: [new InlineContentFile("video.mp4", "bytes"u8.ToArray())]),
            ],
        };

        var result = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(240.Seconds());

        Output.WriteLine($"outcome = {result.Outcome}");

        result.Outcome.Should().Be("ImportedWithRefusedContent",
            "the source declares assets the sync could not deliver, so this pass did NOT put the "
            + "Space in the state the marker would claim — pre-fix it returned \"Imported\", the "
            + "lock was stamped Succeeded, and every later import skipped the Space without "
            + "reading it (#3101)");
    }

    /// <summary>
    /// 🚨 The case that could have FALSIFIED the one above. A rule that escalated every import would
    /// satisfy it just as well and would be far worse: no partition would ever short-circuit again,
    /// so every boot would re-import every Space — the cost the marker exists to avoid.
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AnImportWithNoContentAtAll_StillStampsAGreenMarker()
    {
        var partition = "Rc" + Guid.NewGuid().ToString("N")[..8];
        var source = new ContentRepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "Lesson")],
            // No content syncs at all — the overwhelmingly common case.
        };

        var result = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(240.Seconds());

        Output.WriteLine($"outcome = {result.Outcome}");

        result.Outcome.Should().Be("Imported",
            "a Space with no content has nothing to refuse — escalating it would stop every "
            + "partition short-circuiting and make every boot re-import the whole mesh");
    }
}
