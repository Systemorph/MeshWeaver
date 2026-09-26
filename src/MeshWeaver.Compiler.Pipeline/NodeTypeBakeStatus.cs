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
    /// An assembly is recorded, but it is out of this process's reach: it was compiled for a
    /// DIFFERENT platform compatibility key (a declared epoch or major break, or a record from
    /// before the key existed), or — within the live key — its platform RANGE excludes this build
    /// (produced by a NEWER build, or a ceiling below this one).
    ///
    /// <para>🚨 <b>No longer the every-deploy state.</b> It was, while the key was the per-build
    /// surface/MVID identity: every new image missed the whole cache. The key is now the
    /// compatibility key (<see cref="NodeTypeCompilationHelpers.FrameworkVersion"/>, policy
    /// <c>platform-backwards-compatibility</c>), equal across every platform build of one epoch, so
    /// an ordinary roll leaves every type <see cref="Baked"/>. A large count here after a roll means
    /// a declared break, or records from the pre-key scheme being re-keyed ONCE.</para>
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

    /// <summary>
    /// The record's per-type DEPENDENCY RECORD (#1707 slice 2) no longer validates against this
    /// environment — a module the type binds was updated/removed, or the toolchain closure moved
    /// — while the FRAMEWORK identity still matches. 🚨 Checked BEFORE the store's bytes-win rule:
    /// the store key carries the framework tag but NOT the dependency record, so a bytes-hit under
    /// the live framework can be exactly the drifted build this state exists to replace.
    ///
    /// <para>🚨 <b>"while the FRAMEWORK identity still matches" is now ENFORCED, not just
    /// described.</b> It never held: the reserved <c>!toolchain</c> entry hashes the
    /// <see cref="Compiler.FrameworkBuildIdentity.FullMvidAssemblies"/> closure's implementation MVIDs and
    /// <see cref="Compiler.FrameworkBuildIdentity.FrameworkVersion"/> folds those same MVIDs, so a framework
    /// roll moves the record BY CONSTRUCTION. This state therefore reported every ordinary deploy
    /// as a dependency drift and made <see cref="FrameworkStale"/> unreachable for any
    /// record-stamped type — measured on memex-cloud 2026-08-27 as 273 of 273 uncovered types
    /// DependencyStale and zero FrameworkStale, with nothing changed but the image. A mismatch the
    /// framework identity already accounts for is now classified <see cref="FrameworkStale"/>;
    /// reaching THIS state means the framework held still and a binding genuinely moved, and the
    /// entry that moved is named in <see cref="NodeTypeBakeEntry.Detail"/>.</para>
    /// </summary>
    DependencyStale,
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

    /// <summary>
    /// The platform build (<see cref="NodeTypeDefinition.CompiledPlatformVersion"/>) that produced
    /// the build this record names, or <c>null</c> when the record names none or predates the
    /// field. Read by <see cref="IsRegressionBaselineFor"/>: WHO built the working build decides
    /// whether a failure here can be this image's regression.
    /// </summary>
    public string? ProducedByPlatformBuild { get; init; }

    /// <summary>
    /// 🚨 Whether a WORKING BUILD of this type is on record at all — the thing a regression
    /// regresses FROM (#5544).
    ///
    /// <para>Deliberately NOT <see cref="WasHealthy"/>. That property answers "was this type
    /// known to be broken?" and counts <see cref="BakeState.NeverBuilt"/> as healthy because a type
    /// nobody has built is not damaged goods. But a type nobody has built cannot REGRESS either, and
    /// reading "not known broken" as "working" is how <c>BinaryClickerV2/BinaryToggle</c> — whose
    /// source has failed CS1929 on every image since it was authored — was filed as a regression on
    /// every new pod AND on a restarted pod of the image that was serving, which refused readiness
    /// on memex.systemorph.com until nothing served (2026-09-25/26).</para>
    ///
    /// <para>It also closes the loop that kept that type's record reading <c>Ok</c>: the Error
    /// stamp a failing compile writes goes through <c>MeshPublicationGate</c>, which DISCARDS it on
    /// a refused pod — so as long as the failure refused the pod, the record could never learn the
    /// type was broken, and the next pod read it as healthy again. A never-built type no longer
    /// refuses, the pod is admitted, and the held Error stamp is released.</para>
    /// </summary>
    public bool HadWorkingBuild => State is not (BakeState.PreviouslyBroken or BakeState.NeverBuilt);

    /// <summary>
    /// 🚨 Whether a failure of this type on a process running <paramref name="livePlatformVersion"/>
    /// is evidence that THIS IMAGE broke it — the per-type half of the regression question (#5544).
    ///
    /// <para>True only when a working build is on record (<see cref="HadWorkingBuild"/>) AND it was
    /// produced by a DIFFERENT, not-newer platform build. A working build produced by this same
    /// build proves this image CAN build the type, so a failure now is a content or environment
    /// change, not an image regression; one produced by a NEWER build means this process is the
    /// OLD image of a roll — refusing it protects nothing, it only takes away the replicas the
    /// rollout is falling back on. An unknown producer (a record from before the field, or no live
    /// build stamp) keeps the strict reading.</para>
    /// </summary>
    /// <param name="livePlatformVersion">The running platform build, or <c>null</c> when unknown.</param>
    public bool IsRegressionBaselineFor(string? livePlatformVersion)
        => HadWorkingBuild
           && !(ProducedByPlatformBuild is { Length: > 0 } producer
                && !string.IsNullOrWhiteSpace(livePlatformVersion)
                && (string.Equals(producer, livePlatformVersion, StringComparison.Ordinal)
                    || Compiler.PlatformCompatibility.ProducerIsNewer(producer, livePlatformVersion)));
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

    /// <summary>
    /// 🚨 <b>WHAT POPULATION THIS REPORT IS ABOUT — the half that was missing when two counters were
    /// read as contradicting each other</b> (#3703).
    ///
    /// <para>How many <see cref="Entries"/> were classified from a definition THIS PROCESS wrote (a
    /// prebuilt adoption) rather than from the mesh-wide enumeration snapshot, because the snapshot's
    /// node version proves it predates that write. Zero on every steady-state boot.</para>
    ///
    /// <para><b>Why the report has to say this at all.</b> A bake report is NOT a census of the
    /// assembly store, and reading it as one is the mistake #3703 was filed as. Every number here is
    /// a statement about RECORDS: the store is consulted exactly once per type, at the version the
    /// record names, so a record the reader has not caught up with makes the store unreachable for
    /// that type no matter what bytes are on the share. The adoption pass's own summary — "78
    /// prebuilt assembly(ies) … are backed by the assembly store" — counts BUNDLE ENTRIES whose
    /// bytes are on the share. The two lines have different units, different populations and
    /// different sources, and on memex's 2026-09-08 00:31 cold boot they printed 78 and 5 ten
    /// seconds apart with nothing wrong on the share. Whenever this is non-zero, the sweep is saying
    /// out loud that its input was behind.</para>
    /// </summary>
    public int ClassifiedFromLocalAdoption { get; init; }

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
    /// The types the rollout gate is allowed to fail on: they must be baked AND a working build of
    /// them was produced by another, older image, so a compile failure is a genuine regression —
    /// and nothing at all when <see cref="ThisBuildHasServed"/>. See
    /// <see cref="NodeTypeBakeEntry.IsRegressionBaselineFor"/> (#5544).
    /// </summary>
    public ImmutableList<NodeTypeBakeEntry> GateRelevant =>
        ThisBuildHasServed
            ? ImmutableList<NodeTypeBakeEntry>.Empty
            : Entries.Where(e => e.NeedsBake && e.IsRegressionBaselineFor(LivePlatformVersion))
                .ToImmutableList();

    /// <summary>
    /// The platform build of the process that produced this report, or <c>null</c> when unknown.
    /// The reference point for <see cref="NodeTypeBakeEntry.IsRegressionBaselineFor"/> and
    /// <see cref="ThisBuildHasServed"/>.
    /// </summary>
    public string? LivePlatformVersion { get; init; }

    /// <summary>
    /// 🚨 <b>A replica of THIS platform build has already been admitted to this mesh</b> (#5544).
    /// When this is true, the pod is a RESTART of an image that has served, not a candidate in a roll.
    /// The gate's whole justification, "refuse so the rollout stalls with the previous image still
    /// serving", does not apply to it, because this image IS the one the rollout falls back on.
    /// Refusing it is what took memex.systemorph.com fully down when the last serving pod of the
    /// previous image restarted (2026-09-26).
    ///
    /// <para>Two witnesses, either one sufficient, and both are admission-gated publications:</para>
    /// <list type="bullet">
    /// <item><see cref="ServedBefore"/>: the durable admission marker (the host's
    /// <c>ServedBuildWitness</c>). It is the witness that works in the ORDINARY case. An ordinary
    /// roll compiles nothing, and prebuilt adoption keeps the producer's version, so a serving image
    /// may leave no record naming itself.</item>
    /// <item>A record whose working build this very build produced. A compile stamp carrying this
    /// build's identity is released only once the stamping process was admitted.</item>
    /// </list>
    /// </summary>
    /// <summary>
    /// Set from the durable admission marker: a replica of <see cref="LivePlatformVersion"/> has
    /// been admitted to this mesh before. <c>false</c> when unread or unknown, which is the strict
    /// reading. See <see cref="ThisBuildHasServed"/>.
    /// </summary>
    public bool ServedBefore { get; init; }

    public bool ThisBuildHasServed =>
        ServedBefore
        || !string.IsNullOrWhiteSpace(LivePlatformVersion)
        && Entries.Any(e => e.HadWorkingBuild
                            && string.Equals(
                                e.ProducedByPlatformBuild, LivePlatformVersion, StringComparison.Ordinal));

    /// <summary>
    /// One-line summary for logs and the health-check payload.
    ///
    /// <para>🚨 Every count here is over RECORDS, not over the store — see
    /// <see cref="ClassifiedFromLocalAdoption"/>, which is appended whenever the enumeration
    /// snapshot was behind this process's own adoptions, so the line can never again be read as a
    /// store census that disagrees with the adoption pass's.</para>
    /// </summary>
    public string Summary =>
        $"framework={FrameworkVersion[..Math.Min(8, FrameworkVersion.Length)]} "
        + $"total={Entries.Count} baked={Entries.Count(e => !e.NeedsBake)} pending={Pending.Count}"
        + string.Concat(Entries
            .GroupBy(e => e.State)
            .Where(g => g.Key is not BakeState.Baked)
            .OrderBy(g => g.Key)
            .Select(g => $" {g.Key.ToString().ToLowerInvariant()}={g.Count()}"))
        + (ClassifiedFromLocalAdoption > 0
            ? $" fromlocaladoption={ClassifiedFromLocalAdoption}"
            : string.Empty);

    /// <summary>
    /// How many distinct partitions one state's paths may be NAMED before the rest are counted.
    /// </summary>
    private const int MaxNamedPartitions = 12;

    /// <summary>
    /// 🚨 <b>WHOSE the non-baked types are — the identity half of <see cref="Summary"/>, which
    /// counts and names nothing</b> (#4258).
    ///
    /// <para><see cref="Summary"/> can say <c>previouslybroken=1</c>: exactly one NodeType on this
    /// replica has a record at <c>CompilationStatus.Error</c> and was never healthy. That number is
    /// the ONLY record of a permanently-broken type anywhere in the system, because the rollout gate
    /// deliberately skips such a type so one abandoned NodeType cannot freeze the platform's deploys
    /// (<see cref="NodeTypeBakeEntry.WasHealthy"/>). A count nobody can resolve to an owner is not an
    /// answer: #3883 failed to close three times on the ambiguity between "the type is gone" and "I
    /// may not read it", and the RLS-filtered <c>search</c> sweep a session can run cannot settle it
    /// — the broken type lives in one of the partitions that sweep holds no grant on, which is
    /// exactly why this census exists. Every entry has carried its <see cref="NodeTypeBakeEntry.TypePath"/>
    /// all along; it was discarded here, one call before publication.</para>
    ///
    /// <para>🚨 <b>The PARTITION, never the node title.</b> This is published on <c>/health</c>,
    /// which is PUBLIC and unauthenticated, so it names the path's FIRST SEGMENT and stops
    /// (<c>BinaryClickerV2/…</c>). A partition name routes the finding to an owner, which is all
    /// #3883 ever needed; a node title would widen a public disclosure surface that #3890 closed a
    /// narrower version of — <i>a control that works by disclosing other people's node titles is a
    /// disclosure surface wearing an instrument's colours</i>. Naming the whole path is a
    /// disclosure-policy call for whoever owns that surface, and it is now one projection away
    /// rather than a re-plumbing.</para>
    ///
    /// <para>Every non-<see cref="BakeState.Baked"/> state is covered, not just the broken one:
    /// <c>pending=58 frameworkstale=55</c> has the same shape and the same "nobody can enumerate
    /// them" consequence.</para>
    /// </summary>
    public string Ownership =>
        string.Join("; ", Entries
            .Where(e => e.NeedsBake)
            .GroupBy(e => e.State)
            .OrderBy(g => g.Key)
            .Select(g =>
                $"{g.Key.ToString().ToLowerInvariant()} in {PartitionsOf(g.Select(e => e.TypePath))}"));

    /// <summary>
    /// The distinct partitions a set of NodeType paths lives in — the first path segment of each,
    /// deduplicated, ordered, and capped so one mesh-wide state cannot turn the health body into a
    /// partition listing.
    /// </summary>
    private static string PartitionsOf(IEnumerable<string> typePaths)
    {
        var owners = typePaths
            .Select(PartitionOf)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (owners.Count == 0)
            return "(no path recorded)";

        var named = string.Join(", ", owners.Take(MaxNamedPartitions).Select(p => $"{p}/…"));
        return owners.Count > MaxNamedPartitions
            ? $"{named} (+{owners.Count - MaxNamedPartitions} more partition(s))"
            : named;
    }

    /// <summary>
    /// The partition a NodeType path belongs to: everything before the first <c>/</c>. A path with
    /// no separator IS a partition-level name, so it is returned whole — that is the same
    /// disclosure class, not a node title.
    /// </summary>
    private static string PartitionOf(string typePath)
    {
        if (string.IsNullOrWhiteSpace(typePath))
            return string.Empty;
        var slash = typePath.IndexOf('/');
        return slash < 0 ? typePath : typePath[..slash];
    }
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
    // 🚨 THE PUBLIC ARITY IS FROZEN. A caller compiled against a five-parameter Classify emits a
    // call to a method a six-parameter one does not have, and that break is invisible to every
    // compiler in this repo — the same hazard scripts/check-record-signatures.py exists for on the
    // record side. The re-evaluation lane's live content key (#1976) therefore rides on the
    // INTERNAL ClassifyDetailed, which every in-repo caller already goes through.
    public static BakeState Classify(
        NodeTypeDefinition definition,
        bool storeHasBytes,
        string liveFrameworkVersion,
        Func<string, string?>? liveDependencyIdOf = null,
        string? liveToolchainId = null)
        => ClassifyDetailed(
            definition, storeHasBytes, liveFrameworkVersion, liveDependencyIdOf, liveToolchainId)
            .State;

    /// <summary>
    /// <see cref="Classify"/> plus the dependency record's FIRST mismatch, when one decided the
    /// verdict.
    ///
    /// <para>🚨 The reason exists and was being thrown away. <see cref="Classify"/> asked
    /// <c>FindMismatch(…) is not null</c> and the caller then printed a fixed sentence — "a bound
    /// module/toolchain dependency changed" — so the one string that says WHICH dependency moved
    /// (<c>'name' built against X, live is Y</c>) never reached an operator. Three independent
    /// investigations across three repos could not tell an ordinary framework roll from a module
    /// drift because of it. The verdict is unchanged; only the diagnostics are recovered.</para>
    /// </summary>
    /// <param name="liveGeneratedInputDigest">🚨 The stage-1 digest of this type's compile input as
    /// REGENERATED now (#1976), or null — the default, and every caller today — when it was not.
    /// Null means the metadata-only rule applies unchanged; it never means "unchanged". See
    /// <see cref="Compiler.ContentKeyReevaluation"/>.</param>
    internal static (BakeState State, string? DependencyMismatch) ClassifyDetailed(
        NodeTypeDefinition definition,
        bool storeHasBytes,
        string liveFrameworkVersion,
        Func<string, string?>? liveDependencyIdOf = null,
        string? liveToolchainId = null,
        string? liveGeneratedInputDigest = null)
        => ClassifyAgainst(
            definition, storeHasBytes, liveFrameworkVersion, NodeTypeCompilationHelpers.LivePlatformVersion,
            liveDependencyIdOf, liveToolchainId, liveGeneratedInputDigest);

    /// <summary>
    /// <see cref="ClassifyDetailed"/> with the running platform BUILD explicit — the seam a test
    /// stages a mixed roll through (policy <c>platform-backwards-compatibility</c>): within one
    /// compatibility key, a record produced by a NEWER build than <paramref name="livePlatformVersion"/>
    /// is <see cref="BakeState.FrameworkStale"/> even when the store holds its bytes.
    /// </summary>
    internal static (BakeState State, string? DependencyMismatch) ClassifyAgainst(
        NodeTypeDefinition definition,
        bool storeHasBytes,
        string liveFrameworkVersion,
        string? livePlatformVersion,
        Func<string, string?>? liveDependencyIdOf = null,
        string? liveToolchainId = null,
        string? liveGeneratedInputDigest = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        if (definition.CompilationStatus == CompilationStatus.Error)
            return (BakeState.PreviouslyBroken, null);

        var hasRecordedAssembly =
            !string.IsNullOrEmpty(definition.LatestAssemblyCollection)
            && !string.IsNullOrEmpty(definition.LatestAssemblyPath)
            && definition.LastCompiledVersion is not null;

        if (!hasRecordedAssembly)
            return (BakeState.NeverBuilt, null);

        // 🚨 The KEY is the platform compatibility key (policy platform-backwards-compatibility):
        // equal across every platform build of one epoch, so an ordinary roll moves nothing. What
        // DOES move a build out of reach within one key is its platform RANGE: bytes a NEWER build
        // produced (or whose ceiling this build exceeds) are not this process's to serve, and a
        // store hit must not launder them — so the range is judged BEFORE the bytes-win rule.
        var outOfRange = string.Equals(
                definition.CompiledFrameworkVersion, liveFrameworkVersion, StringComparison.Ordinal)
            && NodeTypeBuildIdentity.RefusalReason(
                definition, liveFrameworkVersion, livePlatformVersion) is not null;
        var frameworkMoved = outOfRange || !string.Equals(
            definition.CompiledFrameworkVersion, liveFrameworkVersion, StringComparison.Ordinal);

        // 🚨 The per-type dependency record (#1707 slice 2) is checked BEFORE the bytes-win rule:
        // the store key carries the framework tag but NOT the record, so a bytes-hit under the
        // live framework can be exactly the drifted build this state exists to replace (a module
        // the type binds was updated; the framework rule cannot see it). Conservative by design —
        // a record stamped by a replica whose write-back lagged may cost one redundant rebuild,
        // never a stale serve.
        if (definition.CompiledDependencies is { } record
            && liveDependencyIdOf is not null
            // 🚨 The RE-EVALUATION LANE's read half (#1976). Without a regenerated digest
            // LiveContentKeyOf returns null and this is byte-for-byte the metadata-only
            // FindMismatch it replaced; with one, the reserved '!toolchain' entry demotes from an
            // invalidation unit to a trigger and the store's bytes-win rule below is allowed to
            // decide. An absent or inconclusive key NEVER reads as a match.
            && Compiler.CompiledDependencies.FindMismatchAfterReevaluation(
                record, liveDependencyIdOf, liveToolchainId ?? "",
                Compiler.CompiledDependencies.LiveContentKeyOf(
                    record, liveDependencyIdOf, liveGeneratedInputDigest)) is { } mismatch)
            // 🚨 ATTRIBUTE THE MISMATCH TO THE FRAMEWORK WHEN THE FRAMEWORK EXPLAINS IT.
            // The reserved '!toolchain' entry is a hash over the FullMvidAssemblies closure's
            // implementation MVIDs, and FrameworkVersion folds those SAME MVIDs (every closure
            // member is a ContentSurfaceAssemblies member). A framework roll therefore moves
            // '!toolchain' BY CONSTRUCTION, and every platform ref-asm entry moves with it — so
            // for any record-stamped type the dependency check fires on every deploy and
            // DependencyStale swallowed FrameworkStale whole, leaving the ordinary,
            // documented-as-benign every-deploy state unreachable in practice.
            //
            // Measured on memex-cloud 2026-08-27: 273 of 273 uncovered types reported
            // DependencyStale ("a bound module/toolchain dependency changed") and ZERO reported
            // FrameworkStale, while the only thing that had actually changed was the image —
            // the types carried framework sceaefc9…/'!toolchain' mvid:bc0ed79d… against a live
            // s66ba35f…/mvid:f8a7cc07…. Three separate investigations then hunted a module
            // drift that did not exist.
            //
            // The DECLINE is unchanged (both states are NeedsBake and WasHealthy, and this still
            // preempts the bytes-win rule below) — only the NAME changes, and only when the
            // framework identity already accounts for it.
            return frameworkMoved
                ? (BakeState.FrameworkStale, mismatch)
                : (BakeState.DependencyStale, mismatch);

        // 🚨 BYTES WIN OVER THE RECORD, in both directions — this is the whole premise.
        //
        // The store is looked up as (nodeTypePath, version) with the LIVE framework tag baked into
        // the key, so a hit means "bytes compiled against THIS framework exist", whatever the record
        // claims. A record can name another framework while the share already holds our build —
        // another replica compiled it and its write-back lagged, failed, or was never made at all.
        // Reporting that FrameworkStale would rebuild something already sitting on the volume, which
        // is exactly the wasted work this probe exists to avoid.
        //
        // …EXCEPT for a record whose platform RANGE excludes this build (a newer producer, or a
        // ceiling below it): the store key cannot tell those bytes from ours, so a hit would serve
        // bytes built against surface this platform lacks.
        if (outOfRange)
            return (BakeState.FrameworkStale, null);
        if (storeHasBytes)
            return (BakeState.Baked, null);

        if (frameworkMoved)
            return (BakeState.FrameworkStale, null);

        // The record claims a live-framework build and the store does not have it.
        return (BakeState.BytesMissing, null);
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
        ILogger? logger = null,
        Func<string, string?>? liveDependencyIdOf = null,
        string? liveToolchainId = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(store);

        var framework = liveFrameworkVersion ?? NodeTypeCompilationHelpers.FrameworkVersion;

        var probes = definitions
            .Where(kvp => kvp.Value is not null && !string.IsNullOrEmpty(kvp.Key))
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => ProbeOne(
                kvp.Key, kvp.Value!, store, framework, logger, liveDependencyIdOf, liveToolchainId))
            .ToList();

        return probes.Count == 0
            ? Observable.Return(NodeTypeBakeReport.Empty(framework))
            : probes
                .Concat()
                .ToList()
                .Select(entries => new NodeTypeBakeReport(entries.ToImmutableList(), framework)
                {
                    LivePlatformVersion = NodeTypeCompilationHelpers.LivePlatformVersion,
                });
    }

    private static IObservable<NodeTypeBakeEntry> ProbeOne(
        string typePath,
        NodeTypeDefinition definition,
        IAssemblyStore store,
        string framework,
        ILogger? logger,
        Func<string, string?>? liveDependencyIdOf,
        string? liveToolchainId)
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
                return Observable.Return(Describe(typePath, definition,
                    ClassifyDetailed(
                        definition, false, framework, liveDependencyIdOf, liveToolchainId)));

            return store
                .TryGetAssemblyPath(typePath, definition.LastCompiledVersion!.Value)
                .Take(1)
                .Select(path => Describe(
                    typePath, definition,
                    ClassifyDetailed(
                        definition, !string.IsNullOrEmpty(path), framework,
                        liveDependencyIdOf, liveToolchainId)))
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

    private static NodeTypeBakeEntry Describe(
        string typePath,
        NodeTypeDefinition definition,
        (BakeState State, string? DependencyMismatch) verdict)
        => new(typePath, verdict.State, verdict.State switch
        {
            BakeState.Baked => null,
            BakeState.NeverBuilt => "no assembly recorded",
            // The framework line is the explanation; the record mismatch that came with it is a
            // CONSEQUENCE of the roll, so it is named as corroboration rather than as a cause.
            BakeState.FrameworkStale =>
                $"built against framework {Short(definition.CompiledFrameworkVersion)}"
                + (verdict.DependencyMismatch is { } corroboration
                    ? $" (its dependency record moved with it: {corroboration})"
                    : string.Empty),
            BakeState.BytesMissing =>
                $"record claims {definition.LatestAssemblyCollection}/{definition.LatestAssemblyPath} "
                + "but the store has no bytes",
            BakeState.PreviouslyBroken => "last compile settled at Error",
            // 🚨 NAME THE DEPENDENCY. This state now means what it says — the framework did NOT
            // move and something this build binds did — so the entry that moved is the whole
            // finding, and printing it is the difference between an actionable line and a sweep.
            BakeState.DependencyStale =>
                "the stamped dependency record no longer validates here"
                + (verdict.DependencyMismatch is { } drift
                    ? $": {drift}"
                    : " (a bound module/toolchain dependency changed)"),
            _ => null,
        })
        {
            // Who built what this record names — only meaningful where it names a build.
            ProducedByPlatformBuild = verdict.State is BakeState.NeverBuilt or BakeState.PreviouslyBroken
                ? null
                : definition.CompiledPlatformVersion,
        };

    private static string Short(string? version) =>
        string.IsNullOrEmpty(version) ? "(none)" : version[..Math.Min(8, version.Length)];
}
