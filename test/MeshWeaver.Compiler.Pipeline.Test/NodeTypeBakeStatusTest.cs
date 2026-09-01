using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Unit tests for <see cref="NodeTypeBakeStatus"/> — the probe that decides what still has to be
/// compiled by asking the SHARED ASSEMBLY STORE, rather than trusting each NodeType's own record.
///
/// <para>The case worth reading first is
/// <see cref="Classify_RecordClaimsLiveBuild_ButStoreHasNoBytes_IsBytesMissing"/>: it is the state the
/// rest of the framework structurally cannot see, because <c>HasUsableBuild</c> is a pure record check
/// by design. Clearing the assembly-cache volume leaves every NodeType claiming Ok with its bytes
/// gone, and nothing re-drives a compile.</para>
/// </summary>
public class NodeTypeBakeStatusTest
{
    private const string Live = "03d6f01eb6654e199d31fc59668d7b62";
    private const string Previous = "b7e11c9a44d24f0d8e2a5c31f9048ab6";

    /// <summary>A NodeType record that compiled cleanly against <paramref name="framework"/>.</summary>
    private static NodeTypeDefinition Healthy(string framework = Live, long version = 845) => new()
    {
        Configuration = "config => config",
        Sources = ["namespace:Source scope:subtree"],
        CompilationStatus = CompilationStatus.Ok,
        CompiledFrameworkVersion = framework,
        LastCompiledVersion = version,
        LatestAssemblyCollection = "local",
        LatestAssemblyPath = $"Store_Plugin/v{version}-{framework[..8]}-fef027803a74.dll",
    };

    // ---- Classify: the pure rules -------------------------------------------------------------

    [Fact]
    public void Classify_RecordClaimsLiveBuild_AndStoreHasBytes_IsBaked() =>
        NodeTypeBakeStatus.Classify(Healthy(), storeHasBytes: true, Live)
            .Should().Be(BakeState.Baked);

    /// <summary>
    /// 🚨 The cleared-cache case. The record is pristine — status Ok, framework matches, assembly
    /// coordinates populated — and the bytes are simply not there. Every record-only check in the
    /// framework calls this healthy; only a store probe catches it.
    /// </summary>
    [Fact]
    public void Classify_RecordClaimsLiveBuild_ButStoreHasNoBytes_IsBytesMissing() =>
        NodeTypeBakeStatus.Classify(Healthy(), storeHasBytes: false, Live)
            .Should().Be(BakeState.BytesMissing);

    [Fact]
    public void Classify_AssemblyBuiltAgainstAnotherFramework_AndNoBytes_IsFrameworkStale() =>
        NodeTypeBakeStatus.Classify(Healthy(Previous), storeHasBytes: false, Live)
            .Should().Be(BakeState.FrameworkStale);

    /// <summary>
    /// 🚨 Bytes win over the record. The store is keyed with the LIVE framework tag, so a hit means
    /// "a build for THIS framework exists" no matter what the record says — another replica compiled
    /// it and its write-back lagged, failed, or never happened. Rebuilding it would recompile
    /// something already sitting on the shared volume, which is the exact waste this probe exists to
    /// prevent.
    /// </summary>
    [Fact]
    public void Classify_RecordNamesAnotherFramework_ButStoreHasOurBytes_IsBaked() =>
        NodeTypeBakeStatus.Classify(Healthy(Previous), storeHasBytes: true, Live)
            .Should().Be(BakeState.Baked);

    [Fact]
    public void Classify_NoAssemblyRecorded_IsNeverBuilt() =>
        NodeTypeBakeStatus.Classify(
                new NodeTypeDefinition { Configuration = "config => config" }, storeHasBytes: false, Live)
            .Should().Be(BakeState.NeverBuilt);

    /// <summary>
    /// Assembly coordinates without a compiled VERSION cannot be probed — the store is keyed on
    /// (path, version), so there is no key to ask about. Treated as never built rather than assumed
    /// good.
    /// </summary>
    [Fact]
    public void Classify_AssemblyCoordinatesButNoCompiledVersion_IsNeverBuilt() =>
        NodeTypeBakeStatus.Classify(
                Healthy() with { LastCompiledVersion = null }, storeHasBytes: true, Live)
            .Should().Be(BakeState.NeverBuilt);

    [Fact]
    public void Classify_LastCompileErrored_IsPreviouslyBroken() =>
        NodeTypeBakeStatus.Classify(
                Healthy() with { CompilationStatus = CompilationStatus.Error }, storeHasBytes: true, Live)
            .Should().Be(BakeState.PreviouslyBroken);

    /// <summary>
    /// A broken type is ALSO framework-stale on a new image. It must keep the broken label, or the
    /// gate would treat it as a healthy-but-stale type and start blocking deploys on a type that was
    /// already failing before the image changed.
    /// </summary>
    [Fact]
    public void Classify_BrokenAndFrameworkStale_StaysPreviouslyBroken() =>
        NodeTypeBakeStatus.Classify(
                Healthy(Previous) with { CompilationStatus = CompilationStatus.Error },
                storeHasBytes: false, Live)
            .Should().Be(BakeState.PreviouslyBroken);

    // ---- The regression baseline --------------------------------------------------------------

    [Fact]
    public void PreviouslyBrokenEntry_IsNotGateRelevant() =>
        new NodeTypeBakeEntry("A", BakeState.PreviouslyBroken).WasHealthy.Should().BeFalse();

    [Theory]
    [InlineData(BakeState.Baked)]
    [InlineData(BakeState.NeverBuilt)]
    [InlineData(BakeState.FrameworkStale)]
    [InlineData(BakeState.BytesMissing)]
    public void NonBrokenEntries_AreGateRelevant(BakeState state) =>
        new NodeTypeBakeEntry("A", state).WasHealthy.Should().BeTrue();

    [Fact]
    public void OnlyBaked_NeedsNoBake()
    {
        new NodeTypeBakeEntry("A", BakeState.Baked).NeedsBake.Should().BeFalse();
        foreach (var state in Enum.GetValues<BakeState>().Where(s => s != BakeState.Baked))
            new NodeTypeBakeEntry("A", state).NeedsBake.Should().BeTrue();
    }

    /// <summary>
    /// A type that was already broken must not appear in the gate set even though it needs a bake —
    /// otherwise one abandoned NodeType freezes every future deploy.
    /// </summary>
    [Fact]
    public void GateRelevant_ExcludesPreviouslyBroken_ButKeepsStaleAndMissing()
    {
        var report = Report(
            new NodeTypeBakeEntry("Ok", BakeState.Baked),
            new NodeTypeBakeEntry("Stale", BakeState.FrameworkStale),
            new NodeTypeBakeEntry("Cleared", BakeState.BytesMissing),
            new NodeTypeBakeEntry("Broken", BakeState.PreviouslyBroken));

        report.GateRelevant.Select(e => e.TypePath).Should().Equal("Stale", "Cleared");
        report.Pending.Select(e => e.TypePath).Should().Equal("Stale", "Cleared", "Broken");
        report.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void AllBaked_IsComplete()
    {
        var report = Report(
            new NodeTypeBakeEntry("A", BakeState.Baked),
            new NodeTypeBakeEntry("B", BakeState.Baked));

        report.IsComplete.Should().BeTrue();
        report.Pending.Should().BeEmpty();
        report.GateRelevant.Should().BeEmpty();
    }

    [Fact]
    public void EmptyReport_IsComplete() =>
        NodeTypeBakeReport.Empty(Live).IsComplete.Should().BeTrue();

    [Fact]
    public void Summary_NamesTheFrameworkAndTheOutstandingStates()
    {
        var summary = Report(
            new NodeTypeBakeEntry("A", BakeState.Baked),
            new NodeTypeBakeEntry("B", BakeState.BytesMissing),
            new NodeTypeBakeEntry("C", BakeState.FrameworkStale)).Summary;

        summary.Should().Contain("framework=03d6f01e");
        summary.Should().Contain("total=3").And.Contain("baked=1").And.Contain("pending=2");
        summary.Should().Contain("bytesmissing=1").And.Contain("frameworkstale=1");
    }

    // ---- Probe: against a store ---------------------------------------------------------------

    /// <summary>The whole point: a wiped share re-bakes, even though every record still says Ok.</summary>
    [Fact]
    public void Probe_ClearedCache_ReportsEveryTypeAsBytesMissing()
    {
        var report = Probe(new FakeStore(), ("Store/Plugin", Healthy()), ("Edu/Course", Healthy(version: 12)));

        report.IsComplete.Should().BeFalse();
        report.Entries.Should().OnlyContain(e => e.State == BakeState.BytesMissing);
        report.BytesMissing.Should().HaveCount(2);
        report.GateRelevant.Should().HaveCount(2);
    }

    [Fact]
    public void Probe_WarmShare_ReportsComplete()
    {
        var store = new FakeStore();
        store.Add("Store/Plugin", 845);
        store.Add("Edu/Course", 12);

        var report = Probe(store, ("Store/Plugin", Healthy()), ("Edu/Course", Healthy(version: 12)));

        report.IsComplete.Should().BeTrue();
        report.Entries.Should().OnlyContain(e => e.State == BakeState.Baked);
    }

    /// <summary>
    /// An interrupted bake resumes: what is already on the share comes back Baked, and only the rest
    /// is pending. This is what makes the sweep restartable without a checkpoint file.
    /// </summary>
    [Fact]
    public void Probe_PartiallyBakedShare_ReportsOnlyTheRemainderAsPending()
    {
        var store = new FakeStore();
        store.Add("Store/Plugin", 845);

        var report = Probe(store, ("Store/Plugin", Healthy()), ("Edu/Course", Healthy(version: 12)));

        report.IsComplete.Should().BeFalse();
        report.Pending.Select(e => e.TypePath).Should().Equal("Edu/Course");
    }

    /// <summary>A framework roll with an empty share: everything is stale and must be rebuilt.</summary>
    [Fact]
    public void Probe_FrameworkRoll_WithEmptyShare_ReportsStale()
    {
        var store = new FakeStore();
        var report = Probe(store, ("Store/Plugin", Healthy(Previous)), ("Edu/Course", Healthy(Previous, 12)));

        report.Entries.Should().OnlyContain(e => e.State == BakeState.FrameworkStale);
        report.IsComplete.Should().BeFalse();
    }

    /// <summary>
    /// The share is still consulted on a framework roll — that is what lets a pod inherit a build
    /// another replica already produced for this framework, instead of repeating it because the
    /// record has not caught up.
    /// </summary>
    [Fact]
    public void Probe_FrameworkRoll_ButShareAlreadyWarm_ReportsComplete()
    {
        var store = new FakeStore();
        store.Add("Store/Plugin", 845);

        var report = Probe(store, ("Store/Plugin", Healthy(Previous)));

        report.IsComplete.Should().BeTrue("bytes for the live framework outrank a lagging record");
        store.Lookups.Should().NotBeEmpty("the share must actually be consulted");
    }

    /// <summary>Fail SAFE: an unreadable store means "bake it", never "trust the record and serve".</summary>
    [Fact]
    public void Probe_StoreThrows_TreatsTypeAsBytesMissing()
    {
        var report = Probe(new ThrowingStore(), ("Store/Plugin", Healthy()));

        report.Entries.Should().ContainSingle()
            .Which.State.Should().Be(BakeState.BytesMissing);
        report.IsComplete.Should().BeFalse();
    }

    [Fact]
    public void Probe_NoDynamicTypes_IsCompleteAndProbesNothing()
    {
        var store = new FakeStore();
        var report = NodeTypeBakeStatus
            .Probe(new Dictionary<string, NodeTypeDefinition?>(), store, Live)
            .Wait();

        report.IsComplete.Should().BeTrue();
        report.Entries.Should().BeEmpty();
        store.Lookups.Should().BeEmpty();
    }

    /// <summary>Probes are sequential — a shared network volume must not see a fan-out at startup.</summary>
    [Fact]
    public void Probe_IsSequential()
    {
        var store = new FakeStore();
        var report = Probe(store,
            ("A/One", Healthy(version: 1)),
            ("B/Two", Healthy(version: 2)),
            ("C/Three", Healthy(version: 3)));

        report.Entries.Should().HaveCount(3);
        store.MaxConcurrent.Should().Be(1);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static NodeTypeBakeReport Report(params NodeTypeBakeEntry[] entries) =>
        new([.. entries], Live);

    private static NodeTypeBakeReport Probe(
        IAssemblyStore store, params (string Path, NodeTypeDefinition Definition)[] types) =>
        NodeTypeBakeStatus
            .Probe(types.ToDictionary(t => t.Path, t => (NodeTypeDefinition?)t.Definition), store, Live)
            .Wait();

    /// <summary>In-memory <see cref="IAssemblyStore"/> that records what was asked of it.</summary>
    private sealed class FakeStore : IAssemblyStore
    {
        private readonly HashSet<string> present = [];
        private int concurrent;

        public List<string> Lookups { get; } = [];
        public int MaxConcurrent { get; private set; }

        public void Add(string nodeTypePath, long version) => present.Add(Key(nodeTypePath, version));

        public IObservable<string?> TryGetAssemblyPath(string nodeTypePath, long version) =>
            Observable.Defer(() =>
            {
                var key = Key(nodeTypePath, version);
                Lookups.Add(key);
                MaxConcurrent = Math.Max(MaxConcurrent, ++concurrent);
                try
                {
                    return Observable.Return<string?>(present.Contains(key) ? $"/data/assembly-cache/{key}.dll" : null);
                }
                finally
                {
                    concurrent--;
                }
            });

        public IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes)
        {
            present.Add(Key(nodeTypePath, version));
            return Observable.Return($"/data/assembly-cache/{Key(nodeTypePath, version)}.dll");
        }

        private static string Key(string nodeTypePath, long version) =>
            $"{nodeTypePath.Replace('/', '_')}_v{version}";
    }

    private sealed class ThrowingStore : IAssemblyStore
    {
        public IObservable<string?> TryGetAssemblyPath(string nodeTypePath, long version) =>
            Observable.Throw<string?>(new InvalidOperationException("share unreachable"));

        public IObservable<string> Put(string nodeTypePath, long version, byte[] assemblyBytes, byte[]? pdbBytes) =>
            Observable.Return(string.Empty);
    }
}
