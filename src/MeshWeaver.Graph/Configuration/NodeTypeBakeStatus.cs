using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Configuration;

/// <summary>
/// What the SHARED ASSEMBLY STORE actually holds for one dynamic NodeType, on the live framework.
/// </summary>
public enum BakeState
{
    /// <summary>
    /// The store holds loadable bytes for this NodeType's last compiled version, built against the
    /// LIVE framework. Nothing to do — this is the only state that does not need a bake.
    /// </summary>
    Baked,

    /// <summary>No assembly was ever recorded for this NodeType. First build.</summary>
    NeverBuilt,

    /// <summary>
    /// An assembly is recorded, but it was compiled against a DIFFERENT framework than the live one.
    /// This is the ordinary every-deploy state: <see cref="NodeTypeCompilationHelpers.FrameworkVersion"/>
    /// (Graph's MVID) is baked into the store's filename AND its lookup glob, so a new image misses
    /// the whole cache by design — ABI safety, not a bug.
    /// </summary>
    FrameworkStale,

    /// <summary>
    /// 🚨 The NodeType's own record CLAIMS a usable build for the live framework — right collection,
    /// right path, matching <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/> — but the store
    /// has no bytes at that key. The record is writing a cheque the share cannot cash.
    ///
    /// <para>This is the state nothing else in the framework can see. <c>HasUsableBuild</c> is
    /// deliberately a pure record check ("no store probe, no File.Exists") because the kickoff path
    /// prefers a redundant compile over a blocking store round-trip on every stream emission. That is
    /// the right trade THERE, but it means an operator who clears <c>/data/assembly-cache</c> — or a
    /// remounted / re-provisioned / partially-restored volume — leaves every NodeType still claiming
    /// Ok while its bytes are gone. Nothing re-drives a compile, and the miss only surfaces LATER, one
    /// instance at a time, when activation tries to hydrate the assembly.</para>
    ///
    /// <para>Probing the store is what turns that into a state we can act on BEFORE serving.</para>
    /// </summary>
    BytesMissing,

    /// <summary>
    /// The last compile settled at <see cref="CompilationStatus.Error"/>. Needs a bake like any other
    /// non-<see cref="Baked"/> state — but see <see cref="NodeTypeBakeEntry.WasHealthy"/>: a type that
    /// was ALREADY broken before this image must not be allowed to block the rollout.
    /// </summary>
    PreviouslyBroken,
}

/// <summary>One dynamic NodeType's bake state, as read from the store rather than from its record.</summary>
/// <param name="TypePath">The NodeType's mesh path.</param>
/// <param name="State">What the store actually holds.</param>
/// <param name="Detail">Human-readable context for logs and the health payload.</param>
public sealed record NodeTypeBakeEntry(string TypePath, BakeState State, string? Detail = null)
{
    /// <summary>True when this type still has to be compiled against the live framework.</summary>
    public bool NeedsBake => State is not BakeState.Baked;

    /// <summary>
    /// 🚨 Whether this type was WORKING before this image — the regression baseline, and the reason
    /// a permanently-broken type cannot wedge every future deploy.
    ///
    /// <para>The rollout gate must fail on a REGRESSION (a type that used to compile and no longer
    /// does), not on pre-existing breakage. A NodeType that was already sitting at
    /// <see cref="CompilationStatus.Error"/> before the deploy is not made worse by the new image: it
    /// is broken in production right now, and blocking the rollout on it would mean one abandoned
    /// type freezes the platform's deploys forever — discovered, inevitably, at the worst possible
    /// moment. Such a type is logged loudly and skipped by the gate.</para>
    /// </summary>
    public bool WasHealthy => State is not BakeState.PreviouslyBroken;
}

/// <summary>
/// The bake state of every dynamic NodeType on this mesh — the "actual reality of the share",
/// resolved by asking the <see cref="IAssemblyStore"/> for bytes rather than trusting each
/// NodeType's own record.
/// </summary>
/// <param name="Entries">One entry per dynamic NodeType, in the order probed.</param>
/// <param name="FrameworkVersion">The live framework identity the probe resolved against.</param>
public sealed record NodeTypeBakeReport(
    ImmutableList<NodeTypeBakeEntry> Entries,
    string FrameworkVersion)
{
    /// <summary>An empty report — no dynamic NodeTypes, so the bake is trivially complete.</summary>
    public static NodeTypeBakeReport Empty(string frameworkVersion) =>
        new(ImmutableList<NodeTypeBakeEntry>.Empty, frameworkVersion);

    /// <summary>Every type is <see cref="BakeState.Baked"/> — the share is fully warm for this image.</summary>
    public bool IsComplete => Entries.All(e => !e.NeedsBake);

    /// <summary>The types that still need compiling, in probe order (callers re-order by dependency).</summary>
    public ImmutableList<NodeTypeBakeEntry> Pending =>
        Entries.Where(e => e.NeedsBake).ToImmutableList();

    /// <summary>
    /// Types whose bytes vanished from under a record that claims they are fine — the cleared-cache
    /// signal. Surfaced separately because it means the SHARE changed, not the code, and it is worth
    /// saying so out loud rather than reporting a generic recompile.
    /// </summary>
    public ImmutableList<NodeTypeBakeEntry> BytesMissing =>
        Entries.Where(e => e.State is BakeState.BytesMissing).ToImmutableList();

    /// <summary>
    /// The types the rollout gate is allowed to fail on: they must be baked AND they were healthy
    /// before this image, so a compile failure is a genuine regression. See
    /// <see cref="NodeTypeBakeEntry.WasHealthy"/>.
    /// </summary>
    public ImmutableList<NodeTypeBakeEntry> GateRelevant =>
        Entries.Where(e => e.NeedsBake && e.WasHealthy).ToImmutableList();

    /// <summary>One-line summary for logs and the health-check payload.</summary>
    public string Summary =>
        $"framework={FrameworkVersion[..Math.Min(8, FrameworkVersion.Length)]} "
        + $"total={Entries.Count} baked={Entries.Count(e => !e.NeedsBake)} pending={Pending.Count}"
        + string.Concat(Entries
            .GroupBy(e => e.State)
            .Where(g => g.Key is not BakeState.Baked)
            .OrderBy(g => g.Key)
            .Select(g => $" {g.Key.ToString().ToLowerInvariant()}={g.Count()}"));
}

/// <summary>
/// Classifies dynamic NodeTypes by what the shared assembly store ACTUALLY holds, so a bake can be
/// driven from the share's reality instead of from a marker that can lie.
///
/// <para><b>Level-triggered, never edge-triggered.</b> There is no "the bake ran" flag anywhere in
/// this design, on purpose. A flag records history; the share records truth, and the two diverge
/// exactly when it matters most — someone clears the cache, a volume is remounted or restored from a
/// stale snapshot, a bake is interrupted half-way. Every consumer re-probes and acts on what it finds,
/// so all of those heal by themselves on the next pass, and an interrupted bake RESUMES (the types
/// already written come back <see cref="BakeState.Baked"/>) instead of starting over.</para>
///
/// <para><b>The classification is pure.</b> <see cref="Classify"/> is a static function over a record,
/// a bool and a version string — no hub, no mesh, no I/O — so every state (including the ones that are
/// awkward to stage against a real store, like bytes disappearing under a healthy record) is
/// unit-testable without a fixture. Only <see cref="Probe"/> touches the store. Same split, and same
/// reason, as <see cref="NodeTypeDependencyGraph"/>.</para>
/// </summary>
public static class NodeTypeBakeStatus
{
    /// <summary>
    /// Decide one NodeType's bake state. Pure: <paramref name="storeHasBytes"/> is the caller's
    /// already-resolved answer to "does the store hold this key?", so this function never does I/O.
    ///
    /// <para>Order matters. A record with no assembly at all is <see cref="BakeState.NeverBuilt"/>
    /// regardless of status; a recorded assembly from another framework is
    /// <see cref="BakeState.FrameworkStale"/> and we do NOT probe for it (its key contains a different
    /// framework tag, so a miss is certain and tells us nothing). Only when the record claims a
    /// live-framework build does the store's answer decide between <see cref="BakeState.Baked"/> and
    /// <see cref="BakeState.BytesMissing"/>.</para>
    ///
    /// <para><see cref="BakeState.PreviouslyBroken"/> is checked FIRST among the needs-bake states so
    /// the regression baseline is preserved: a type sitting at Error keeps that label even though it
    /// is also, technically, framework-stale — otherwise it would look healthy-but-stale and the gate
    /// would start blocking deploys on it.</para>
    /// </summary>
    /// <param name="definition">The NodeType's definition, as persisted.</param>
    /// <param name="storeHasBytes">
    /// Whether <see cref="IAssemblyStore.TryGetAssemblyPath"/> resolved bytes for this type's
    /// <see cref="NodeTypeDefinition.LastCompiledVersion"/>. Ignored unless the record claims a
    /// live-framework build.
    /// </param>
    /// <param name="liveFrameworkVersion">
    /// The framework identity to compare against — injected rather than read from
    /// <see cref="NodeTypeCompilationHelpers.FrameworkVersion"/> so tests can stage a
    /// framework roll without rebuilding the framework.
    /// </param>
    public static BakeState Classify(
        NodeTypeDefinition definition,
        bool storeHasBytes,
        string liveFrameworkVersion)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.CompilationStatus == CompilationStatus.Error)
            return BakeState.PreviouslyBroken;

        var hasRecordedAssembly =
            !string.IsNullOrEmpty(definition.LatestAssemblyCollection)
            && !string.IsNullOrEmpty(definition.LatestAssemblyPath)
            && definition.LastCompiledVersion is not null;

        if (!hasRecordedAssembly)
            return BakeState.NeverBuilt;

        // 🚨 BYTES WIN OVER THE RECORD, in both directions — this is the whole premise.
        //
        // The store is looked up as (nodeTypePath, version) with the LIVE framework tag baked into
        // the key, so a hit means "bytes compiled against THIS framework exist", whatever the record
        // claims. A record can name another framework while the share already holds our build —
        // another replica compiled it and its write-back lagged, failed, or was never made at all.
        // Reporting that FrameworkStale would rebuild something already sitting on the volume, which
        // is exactly the wasted work this probe exists to avoid.
        if (storeHasBytes)
            return BakeState.Baked;

        if (!string.Equals(definition.CompiledFrameworkVersion, liveFrameworkVersion, StringComparison.Ordinal))
            return BakeState.FrameworkStale;

        // The record claims a live-framework build and the store does not have it.
        return BakeState.BytesMissing;
    }

    /// <summary>
    /// Probe the store for every supplied dynamic NodeType and build the report.
    ///
    /// <para>Probes run SEQUENTIALLY (<c>Concat</c>, never <c>Merge</c>): the store is typically a
    /// shared network volume or blob container, and a fan-out of lookups across every NodeType at
    /// startup is precisely the kind of cold burst this whole mechanism exists to remove. Each probe
    /// is a directory glob or a blob-exists — cheap — so sequential costs nothing worth having.</para>
    ///
    /// <para>Never throws: a store that faults on one type yields <see cref="BakeState.BytesMissing"/>
    /// for it (fail SAFE — an unreadable store means "assume it is not there and bake", never "assume
    /// it is fine and serve").</para>
    /// </summary>
    /// <param name="definitions">Dynamic NodeTypes to probe, keyed by mesh path.</param>
    /// <param name="store">The shared assembly store to interrogate.</param>
    /// <param name="liveFrameworkVersion">
    /// Framework identity to compare against; defaults to the live
    /// <see cref="NodeTypeCompilationHelpers.FrameworkVersion"/>.
    /// </param>
    /// <param name="logger">Optional logger for per-type probe outcomes.</param>
    public static IObservable<NodeTypeBakeReport> Probe(
        IReadOnlyDictionary<string, NodeTypeDefinition?> definitions,
        IAssemblyStore store,
        string? liveFrameworkVersion = null,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(store);

        var framework = liveFrameworkVersion ?? NodeTypeCompilationHelpers.FrameworkVersion;

        var probes = definitions
            .Where(kvp => kvp.Value is not null && !string.IsNullOrEmpty(kvp.Key))
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => ProbeOne(kvp.Key, kvp.Value!, store, framework, logger))
            .ToList();

        return probes.Count == 0
            ? Observable.Return(NodeTypeBakeReport.Empty(framework))
            : probes
                .Concat()
                .ToList()
                .Select(entries => new NodeTypeBakeReport(entries.ToImmutableList(), framework));
    }

    private static IObservable<NodeTypeBakeEntry> ProbeOne(
        string typePath,
        NodeTypeDefinition definition,
        IAssemblyStore store,
        string framework,
        ILogger? logger)
        => Observable.Defer(() =>
        {
            // Probe whenever there is a version to probe WITH — not only when the record already
            // agrees we are current. The store key carries the LIVE framework tag, so the lookup
            // answers "are there bytes for THIS framework?" independently of what the record claims,
            // and a record naming another framework can still be sitting on top of a usable build
            // (another replica compiled it; its write-back lagged or never landed).
            //
            // A type with no recorded assembly has no key to ask about, and one that terminally
            // failed keeps its PreviouslyBroken label — both are decided by the record alone.
            var probeable =
                !string.IsNullOrEmpty(definition.LatestAssemblyCollection)
                && !string.IsNullOrEmpty(definition.LatestAssemblyPath)
                && definition.LastCompiledVersion is { } version
                && version >= 0
                && definition.CompilationStatus != CompilationStatus.Error;

            if (!probeable)
                return Observable.Return(Describe(typePath, definition, Classify(definition, false, framework)));

            return store
                .TryGetAssemblyPath(typePath, definition.LastCompiledVersion!.Value)
                .Take(1)
                .Select(path => Describe(
                    typePath, definition, Classify(definition, !string.IsNullOrEmpty(path), framework)))
                // Fail SAFE, never fail OPEN: an unreadable store must mean "bake it", not "trust
                // the record and serve bytes that may not exist".
                .Catch<NodeTypeBakeEntry, Exception>(ex =>
                {
                    logger?.LogWarning(ex,
                        "Bake probe: assembly store lookup failed for {TypePath} — treating as missing bytes",
                        typePath);
                    return Observable.Return(new NodeTypeBakeEntry(
                        typePath, BakeState.BytesMissing, $"store probe failed: {ex.Message}"));
                });
        });

    private static NodeTypeBakeEntry Describe(string typePath, NodeTypeDefinition definition, BakeState state)
        => new(typePath, state, state switch
        {
            BakeState.Baked => null,
            BakeState.NeverBuilt => "no assembly recorded",
            BakeState.FrameworkStale =>
                $"built against framework {Short(definition.CompiledFrameworkVersion)}",
            BakeState.BytesMissing =>
                $"record claims {definition.LatestAssemblyCollection}/{definition.LatestAssemblyPath} "
                + "but the store has no bytes",
            BakeState.PreviouslyBroken => "last compile settled at Error",
            _ => null,
        });

    private static string Short(string? version) =>
        string.IsNullOrEmpty(version) ? "(none)" : version[..Math.Min(8, version.Length)];
}
