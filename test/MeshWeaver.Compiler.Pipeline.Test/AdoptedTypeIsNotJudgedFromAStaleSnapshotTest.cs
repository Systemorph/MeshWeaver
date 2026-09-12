#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>Two instruments, ten seconds apart, reported 78 and 5 about a mesh whose share was
/// perfectly fine</b> — MeshWeaver#3703, memex, 2026-09-08 00:30–00:31 UTC, one cold boot:
///
/// <code>
/// 00:31:08  ShippedPrebuiltBundles: 78 prebuilt assembly(ies) … are backed by the assembly store
///                                   — 78 adopted now, 0 already current
/// 00:31:18  DynamicTypePreWarmer:   204 of 209 … need building — 5 already on the share.
///                                   framework=sb43f928 baked=5 pending=204 frameworkstale=201
/// </code>
///
/// <para><b>Neither number was wrong, and they were never comparable.</b> The first counts BUNDLE
/// ENTRIES whose bytes are on the store, in assemblies. The second counts NODETYPES whose RECORD,
/// as it appeared in one mesh-wide <c>Query&lt;MeshNode&gt;(…).Take(1)</c>, names the live framework
/// AND resolves bytes <i>at the version that record names</i>. The store is asked exactly ONE
/// question per type, keyed by the record — so a reader whose record snapshot is behind cannot
/// reach the bytes no matter what is on the share. "The sweep's store probe counts 5" was the
/// wrong reading: the probe is not a census of the store, and this file is the pin that says so.</para>
///
/// <para><b>The real defect is an ORDERING one.</b> The prebuilt seeding pass writes each adopted
/// type's record through <c>GetMeshNodeStream(path).Update(…)</c> — authoritative. The sweep then
/// reads a CQRS projection, which is eventually consistent (<c>Doc/Architecture/CqrsAndContentAccess</c>:
/// <i>"a query's answer for one path can be minutes old"</i>). So the sweep can be handed records
/// this same process has already replaced, and no classification can tell that apart from a
/// genuinely stale record — they are the same bytes. On that boot it cost 197 compiles instead of
/// ~20, and it is what put four already-adopted <c>Doc/**</c> types on the compile path where a
/// short source-discovery pass could turn them into false regressions (#3663).</para>
///
/// <para><b>The fix is neither a retry nor a wait.</b>
/// <see cref="NodeTypeAdoptionRegistry.RecordAdopted"/> remembers what THIS process stamped and
/// over which node version; <see cref="NodeTypeAdoptionRegistry.OverlayOnto"/> classifies each type
/// from the NEWER of the two facts. The ordering is decided by <see cref="MeshNode.Version"/> and
/// is a fact, not a heuristic: an adoption stamps the version it READ and its own write bumps the
/// node, so a snapshot at or below that version cannot contain the write, and one above it contains
/// the write or something later — an owner refusal, a recompile — and then the snapshot wins.</para>
///
/// <para>Pure: no hub, no mesh, no store beyond the in-memory one below — same split, and same
/// reason, as <see cref="NodeTypeBakeStatus"/> itself.</para>
/// </summary>
public class AdoptedTypeIsNotJudgedFromAStaleSnapshotTest
{
    private const string Live = "sb43f9287dbd6922a7937bd24be103937";
    private const string Previous = "sceaefc9a44d24f0d8e2a5c31f9048ab6";

    /// <summary>The cheapest real instance from #3703's own "measurement that would settle it".</summary>
    private const string Path = "Doc/DataMesh/SocialMedia/Profile";

    private static NodeTypeDefinition Definition(string framework, long compiledVersion) => new()
    {
        Configuration = "config => config",
        Sources = ["namespace:Source scope:subtree"],
        CompilationStatus = CompilationStatus.Ok,
        CompiledFrameworkVersion = framework,
        LastCompiledVersion = compiledVersion,
        LatestAssemblyCollection = "local",
        LatestAssemblyPath = $"Doc_Profile/v{compiledVersion}-{framework[..8]}.dll",
    };

    /// <summary>
    /// 🚨 THE CONTROL, in both directions, in one test — the first assertion IS the pre-fix
    /// behaviour, so this file cannot go green by construction.
    ///
    /// <para>The stage is exactly #3703's boot: the adoption uploaded bytes under version 25 and
    /// stamped the live framework; the enumeration snapshot still shows the node at version 25 with
    /// the PREVIOUS framework's record naming version 24.</para>
    /// </summary>
    [Fact]
    public void SnapshotOlderThanThisProcessAdoption_ClassifiesFromTheAdoption_NotTheSnapshot()
    {
        var store = new FakeStore();
        // What the adoption actually put on the share: (path, 25).
        store.Add(Path, 25);

        var stale = Definition(Previous, compiledVersion: 24);
        var snapshot = Node(version: 25, stale);

        // --- the pre-fix reading: classify straight off the snapshot -----------------------------
        // The store IS consulted — but at version 24, the only key the stale record offers — so it
        // misses, and the type is rebuilt with correct live-framework bytes sitting on the volume.
        Probe(store, (Path, stale)).Entries.Single().State
            .Should().Be(
                BakeState.FrameworkStale,
                "the pre-#3703 sweep asks the store at the version the SNAPSHOT names, so a "
                + "snapshot that predates this process's own adoption can never reach the bytes "
                + "that adoption wrote — this is the 201-of-209 reading, reproduced");
        store.Lookups.Should().ContainSingle().Which
            .Should().Be(Key(Path, 24), "the miss is a key mismatch, never an absent byte");

        // --- the fix: classify from the newer of the two facts -----------------------------------
        var registry = new NodeTypeAdoptionRegistry();
        registry.RecordAdopted(Path, observedVersion: 25, Definition(Live, compiledVersion: 25));

        var overlay = registry.OverlayOnto(
            Definitions((Path, stale)), Nodes((Path, snapshot)));

        var report = Probed(NodeTypeBakeStatus.Probe(overlay.Definitions, store, Live));

        report.Entries.Single().State.Should().Be(
            BakeState.Baked,
            "bytes compiled against the live framework exist for this type, and the record this "
            + "process wrote is what names their key — recompiling it is the 197-instead-of-20 "
            + "waste #3703 measured");
        report.IsComplete.Should().BeTrue();

        overlay.Applied.Should().Equal(
            [Path],
            "the snapshot's node version (25) is at or below the version the adoption wrote over "
            + "(25), so it provably cannot contain that write");
    }

    /// <summary>
    /// 🚨 THE REVERSE CONTROL — and the one that fails against an UNCONDITIONAL overlay.
    ///
    /// <para>The owner refused the adoption after it landed (#2813) and cleared the assembly
    /// coordinates. That write bumped the node past the version the adoption wrote over, so the
    /// snapshot is the NEWER evidence and the ledger must stand aside. Drop the
    /// <c>snapshotVersion &lt;= ObservedVersion</c> guard from
    /// <see cref="NodeTypeAdoptionRegistry.StampSuperseding"/> and this reports
    /// <see cref="BakeState.Baked"/> over a record that no longer names any assembly — a stale
    /// serve, which is the one outcome the bake's conservatism exists to prevent.</para>
    /// </summary>
    [Fact]
    public void SnapshotNewerThanThisProcessAdoption_KeepsTheSnapshot_EvenWhenTheStoreHasBytes()
    {
        var store = new FakeStore();
        store.Add(Path, 25);

        var refused = new NodeTypeDefinition
        {
            Configuration = "config => config",
            Sources = ["namespace:Source scope:subtree"],
            // The owner's refusal cleared the coordinates the adoption had stamped.
            CompilationStatus = CompilationStatus.Pending,
        };
        // 26 > the 25 the adoption wrote over: this snapshot contains that write, or something later.
        var snapshot = Node(version: 26, refused);

        var registry = new NodeTypeAdoptionRegistry();
        registry.RecordAdopted(Path, observedVersion: 25, Definition(Live, compiledVersion: 25));

        var overlay = registry.OverlayOnto(
            Definitions((Path, refused)), Nodes((Path, snapshot)));

        Probed(NodeTypeBakeStatus.Probe(overlay.Definitions, store, Live))
            .Entries.Single().State
            .Should().Be(
                BakeState.NeverBuilt,
                "the record the owner left is what decides; serving the adopted bytes over it "
                + "would be exactly the stale serve #3703's fix must not introduce");

        overlay.Applied.Should().BeEmpty(
            "a snapshot ABOVE the version the adoption wrote over is the newer evidence, so the "
            + "ledger must not outrank it");
    }

    /// <summary>
    /// A type this process did NOT adopt is untouched, and a genuinely stale record still rebuilds.
    /// Without this the first test would pass for a registry that overlays everything it is asked
    /// about.
    /// </summary>
    [Fact]
    public void ATypeThisProcessNeverAdopted_IsClassifiedFromTheSnapshotAsBefore()
    {
        var registry = new NodeTypeAdoptionRegistry();
        registry.RecordAdopted("Other/Type", observedVersion: 25, Definition(Live, 25));

        var stale = Definition(Previous, compiledVersion: 24);
        var overlay = registry.OverlayOnto(
            Definitions((Path, stale)), Nodes((Path, Node(version: 25, stale))));

        overlay.Applied.Should().BeEmpty();
        overlay.Definitions[Path].Should().BeSameAs(stale);
        Probed(NodeTypeBakeStatus.Probe(overlay.Definitions, new FakeStore(), Live))
            .Entries.Single().State.Should().Be(BakeState.FrameworkStale);
    }

    /// <summary>
    /// A path the snapshot has no NODE for keeps its snapshot definition: with no version to
    /// compare, the overlay cannot establish that the snapshot is older, and applying it on faith
    /// would be the unconditional trust the ordering rule exists to refuse.
    /// </summary>
    [Fact]
    public void NoNodeInTheSnapshot_MeansNoVersionToCompare_SoTheOverlayStandsAside()
    {
        var registry = new NodeTypeAdoptionRegistry();
        registry.RecordAdopted(Path, observedVersion: 25, Definition(Live, 25));

        var stale = Definition(Previous, compiledVersion: 24);
        var overlay = registry.OverlayOnto(
            Definitions((Path, stale)), new Dictionary<string, MeshNode>());

        overlay.Applied.Should().BeEmpty();
        overlay.Definitions[Path].Should().BeSameAs(stale);
    }

    /// <summary>The ledger's ordering boundary, stated as an equality rather than inferred.</summary>
    [Theory]
    [InlineData(24, true)]   // strictly older than the write ⇒ superseded
    [InlineData(25, true)]   // the very version the write went over ⇒ superseded
    [InlineData(26, false)]  // the write, or something after it ⇒ the snapshot wins
    public void StampSuperseding_IsDecidedByTheNodeVersionTheAdoptionWroteOver(
        long snapshotVersion, bool superseded)
    {
        var registry = new NodeTypeAdoptionRegistry();
        registry.RecordAdopted(Path, observedVersion: 25, Definition(Live, 25));

        (registry.StampSuperseding(Path, snapshotVersion) is not null)
            .Should().Be(superseded);
    }

    /// <summary>
    /// 🚨 THE REPORTING HALF — the outcome that makes two counters legible rather than
    /// contradictory. A report that classified anything from this process's own write SAYS so, so
    /// nobody reading the sweep's line can take it for a store census again.
    /// </summary>
    [Fact]
    public void AReportThatUsedThisProcessAdoptions_SaysSo_AndOneThatDidNot_StaysSilent()
    {
        var report = new NodeTypeBakeReport(
            [new NodeTypeBakeEntry(Path, BakeState.Baked)], Live);

        report.Summary.Should().NotContain(
            "fromlocaladoption",
            "a steady-state boot classifies nothing from a local adoption, and a field that is "
            + "always present carries no information");

        (report with { ClassifiedFromLocalAdoption = 73 }).Summary
            .Should().Contain(
                "fromlocaladoption=73",
                "when the enumeration was behind, the line that reports the verdict has to report "
                + "that too — otherwise the next reader repeats #3703's reading");
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static MeshNode Node(long version, NodeTypeDefinition definition) =>
        new("Profile", "Doc/DataMesh/SocialMedia") { Version = version, Content = definition };

    private static Dictionary<string, NodeTypeDefinition?> Definitions(
        params (string Path, NodeTypeDefinition Definition)[] types) =>
        types.ToDictionary(
            t => t.Path, t => (NodeTypeDefinition?)t.Definition, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, MeshNode> Nodes(params (string Path, MeshNode Node)[] nodes) =>
        nodes.ToDictionary(n => n.Path, n => n.Node, StringComparer.OrdinalIgnoreCase);

    private static NodeTypeBakeReport Probe(
        IAssemblyStore store, params (string Path, NodeTypeDefinition Definition)[] types) =>
        Probed(NodeTypeBakeStatus.Probe(Definitions(types), store, Live));

    /// <summary>
    /// Subscribe and collect — never <c>.Wait()</c>, which parks the calling thread in a native wait
    /// that xUnit's <c>methodTimeout</c> cannot abort, so a self-deadlock costs the whole shard and
    /// reports <c>exit=124 TIMEOUT</c> with no test named (#2013).
    ///
    /// <para>Every source here is a <see cref="FakeStore"/> lookup composed of <c>Return</c> and
    /// <c>Defer</c>, so the probe emits AND completes inside the <c>Subscribe</c> call. That makes
    /// this stricter than a bounded await as well as safer: a probe that ever became asynchronous
    /// fails loudly here instead of passing slowly. There is no timeout because there is nothing to
    /// wait for.</para>
    /// </summary>
    private static NodeTypeBakeReport Probed(IObservable<NodeTypeBakeReport> probe)
    {
        NodeTypeBakeReport? emitted = null;
        Exception? faulted = null;
        using (probe.Subscribe(report => emitted = report, ex => faulted = ex))
        {
            // The subscription's whole life: a synchronous source has already terminated by here.
        }

        if (faulted is not null)
            throw faulted;
        return emitted ?? throw new InvalidOperationException(
            "the probe did not emit synchronously — see the note on Probed");
    }

    private static string Key(string nodeTypePath, long version) =>
        $"{nodeTypePath.Replace('/', '_')}_v{version}";

    /// <summary>In-memory <see cref="IAssemblyStore"/> that records what was asked of it — the same
    /// shape <c>NodeTypeBakeStatusTest</c> uses, so the two files stage the store identically.</summary>
    private sealed class FakeStore : IAssemblyStore
    {
        private readonly HashSet<string> present = [];

        public List<string> Lookups { get; } = [];

        public void Add(string nodeTypePath, long version) => present.Add(Key(nodeTypePath, version));

        public IObservable<string?> TryGetAssemblyPath(string nodeTypePath, long version) =>
            Observable.Defer(() =>
            {
                var key = Key(nodeTypePath, version);
                Lookups.Add(key);
                return Observable.Return<string?>(
                    present.Contains(key) ? $"/data/assembly-cache/{key}.dll" : null);
            });

        public IObservable<string> Put(
            string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
        {
            present.Add(Key(nodeTypePath, version));
            return Observable.Return($"/data/assembly-cache/{Key(nodeTypePath, version)}.dll");
        }
    }
}
