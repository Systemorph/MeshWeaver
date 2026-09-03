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
/// 🚨 <b>Issue #3146 — an import that fails on CONTENT RULES was re-run in full on every trigger, at
/// the same fingerprint, forever.</b>
///
/// <para><b>Measured on memex.meshweaver.cloud (2026-09-02, 15:40–18:40Z):</b> 19 complete passes in
/// three hours, each ≈425 identical <c>InvalidOperationException</c>s — <i>"A 'Space' owns its
/// partition, so it must be top-level"</i>, <i>"NodeType 'Northwind/Article' is not registered"</i> —
/// plus a NodeType compile of the same sample tree every pass, on a portal already at 8/8 replicas.
/// The trigger is a webhook per green core CI run, and core merged ~30 times that day.</para>
///
/// <para><b>The mechanism.</b> The marker is content-addressed (<c>import-{fingerprint}</c>) and the
/// skip arm read only one thing: <c>Status == Succeeded</c>. Everything else — including a pass
/// whose every failure was a verdict about the bytes — was read as "try again". But the marker's id
/// IS the fingerprint, so a re-run re-reads the same nodes and re-derives the same refusal. Nothing
/// about re-running could ever help.</para>
///
/// <para>🚨 <b>Why this is not simply "skip on any failure".</b> That is #3101 inverted, and worse:
/// a Space frozen out of the mesh by a marker nobody re-examines. A store blip, an owner that did
/// not answer, a hub going down mid-import — all say "not evaluated, ask again", and all must keep
/// re-importing. So only a pass whose failures were <b>every one</b> a content verdict records a
/// final verdict; a single retryable failure among them keeps the ordinary Warning. The two tests
/// below are that distinction, and the second is the one that could falsify the first.</para>
/// </summary>
public class FailedImportIsNotRetriedAtTheSameFingerprintTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    private sealed class RepoSource(string partition) : IStaticRepoSource
    {
        public string Partition => partition;
        public bool Versioned => false;
        public List<MeshNode> Nodes { get; init; } = [];
        public MeshNode? Root { get; init; }

        public IReadOnlyList<MeshNode> EnumerateSourceNodes() => Nodes;
        public MeshNode? PartitionRoot => Root;
        public IReadOnlyList<StaticContentSync> EnumerateInlineContentSyncs() => [];
    }

    private static MeshNode Space(string partition) =>
        new(partition) { Name = partition, NodeType = "Space", State = MeshNodeState.Active };

    private static MeshNode Page(string partition, string id) =>
        new(id, partition) { Name = id, NodeType = "Markdown", State = MeshNodeState.Active };

    /// <summary>
    /// A node the mesh's own rules refuse: a nested <c>Space</c>. The importer's upsert raises
    /// <i>"A 'Space' owns its partition, so it must be top-level"</i> — one of the exact exceptions
    /// the memex-cloud measurement counted 425 of, per pass, 19 passes running.
    /// </summary>
    private static MeshNode NestedSpace(string partition, string id) =>
        new(id, partition) { Name = id, NodeType = "Space", State = MeshNodeState.Active };

    /// <summary>
    /// 🚨 THE PIN. Import twice with the SAME content. The second pass must not touch the mesh.
    ///
    /// <para>Pre-fix both passes return an importing outcome and re-run every upsert and every
    /// compile; post-fix the second is <c>Skipped</c>, which is the same word the green short-circuit
    /// uses — because it is the same fact: this fingerprint has a verdict.</para>
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task AContentVerdict_IsFinalForThatFingerprint()
    {
        var partition = "Cv" + Guid.NewGuid().ToString("N")[..8];
        var source = new RepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "Lesson"), NestedSpace(partition, "Nested")],
        };

        var first = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(240.Seconds());
        Output.WriteLine($"first  = {first.Outcome}");

        first.Outcome.Should().Be("ImportedWithContentErrors",
            "every failure in this pass is a verdict about the bytes — the same content re-read at "
            + "the same fingerprint produces the same refusal, and the outcome has to say so or the "
            + "marker cannot record it");

        // Same source, same fingerprint — the state memex-cloud was in on every webhook.
        var second = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(240.Seconds());
        Output.WriteLine($"second = {second.Outcome}");

        second.Outcome.Should().Be("Skipped",
            "the marker is content-addressed, so re-running re-reads the same nodes and re-derives "
            + "the same refusal; doing it anyway cost memex-cloud 19 full passes in 3 h — ≈425 "
            + "failing upserts plus a NodeType compile each — on a portal already at 8/8 replicas");
    }

    /// <summary>
    /// 🚨 The case that could FALSIFY the one above, and the reason the fix is not "skip on any
    /// failure". A clean import must still stamp a green marker and still short-circuit — a rule
    /// that recorded a final verdict for every pass would stop every partition re-importing when it
    /// should, which is the far more expensive mistake (#3101).
    /// </summary>
    [Fact(Timeout = 300_000)]
    public async Task ACleanImport_StillSucceedsAndStillShortCircuits()
    {
        var partition = "Cv" + Guid.NewGuid().ToString("N")[..8];
        var source = new RepoSource(partition)
        {
            Root = Space(partition),
            Nodes = [Page(partition, "Lesson")],
        };

        var first = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(240.Seconds());
        Output.WriteLine($"first  = {first.Outcome}");
        first.Outcome.Should().Be("Imported",
            "nothing here breaks a content rule");

        var second = await StaticRepoImporter.ImportSource(Mesh, source)
            .FirstAsync().Timeout(240.Seconds());
        Output.WriteLine($"second = {second.Outcome}");
        second.Outcome.Should().Be("Skipped",
            "the green short-circuit is unchanged — this is the behaviour the marker exists for");
    }
}
