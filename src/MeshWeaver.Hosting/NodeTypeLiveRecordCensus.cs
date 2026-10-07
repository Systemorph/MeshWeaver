using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh.Services;

namespace MeshWeaver.Hosting;

/// <summary>
/// 🚨 <b>The LIVE half of <c>bake-report</c>: what the NodeType catalog says RIGHT NOW about which
/// records this replica can bind</b> — Systemorph/MeshWeaver#4632.
///
/// <para>Every other reading on <c>bake-report</c> is taken ONCE, at boot: the adopt-only probe and
/// the compiling sweep enumerate the catalog, classify each record against this replica's framework
/// identity, and publish; nothing re-reads. That was enough while a record could only move
/// <i>towards</i> this replica's identity. Mid-roll it is not: a replica on the OTHER image compiles
/// or adopts a type and stamps the shared record with ITS framework identity, and from that instant
/// the serving replicas have no usable assembly for the type — <c>HasUsableBuild</c> refuses the
/// foreign build at activation, its layout areas never register, and every page that asks for one
/// renders the area-not-found frame. Measured on memex.systemorph.com 2026-09-17: <c>Approvals/Desk</c>
/// re-stamped at 14:33 by a replica on <c>3.0.0-ci.8812</c> while the two READY replicas ran
/// <c>ci.8710</c>; a customer letter rendered <i>"No renderer is registered for area Approvals"</i>
/// and no instrument named it. <c>content-types</c> records a degradation only when a content READ
/// degrades (an area that never registers is not one); <c>bake-report</c>'s plan and outcome census
/// were taken before the stamp landed; the RLS-filtered sweep a reader can run says <c>Ok</c>, which is
/// true for the replica that wrote it.</para>
///
/// <para><b>What this is.</b> A pure fold over the catalog as a standing subscription hands it over
/// (<c>DynamicTypePreWarmer.ObserveLiveRecordCensus</c>): for each dynamic NodeType record, the SAME
/// per-process verdict every load path applies — <see cref="NodeTypeBuildIdentity.ReportedStatus(NodeTypeDefinition?, string)"/>,
/// which folds <c>CompilationStatus.Ok</c> with <c>CompiledFrameworkVersion</c> into
/// <see cref="CompilationStatus.Foreign"/> when the record names a build keyed to a framework this
/// process does not run. Nothing here probes a store or activates a hub: the record IS what
/// activation decides from (<c>HasUsableBuild</c> is a pure record check by design), so a census over
/// records is faithful to what a reader would hit.</para>
///
/// <para>🚨 <b>Two counts, because the same verdict has two meanings.</b> A foreign record stamped
/// BEFORE this replica booted is the ordinary every-deploy state — the previous image's records,
/// which the bake or the first access rebuilds — and every adopt-only portal carries hundreds of them
/// for its whole life. Degrading on those would make the check one that cannot pass. A foreign record
/// stamped AFTER this replica booted (<see cref="ForeignSinceBoot"/>) is the mid-roll cross-stamp
/// this census exists for: something running another framework re-keyed a type this replica may have
/// been serving, and no boot-time reading can know. Only that count degrades the entry; both are
/// printed with their denominator.</para>
///
/// <para>🚨 <b>Partitions only, never a node title.</b> This reaches <c>/health</c>, a PUBLIC,
/// unauthenticated body; a partition routes the finding to an owner without disclosing what #3890
/// closed. Identities print as the same eight characters the assembly-store filename carries, so a
/// line can be compared by eye to a DLL name and to the CD run that produced it.</para>
/// </summary>
/// <param name="FrameworkVersion">The live framework identity every record was compared against.</param>
/// <param name="Total">Dynamic NodeType records in the catalog at this reading — the denominator.</param>
/// <param name="Untyped">Catalog nodes whose content this hub could not type as a definition —
/// decided about by nothing, so named rather than folded into either count.</param>
/// <param name="Foreign">Records naming a build keyed to a framework this replica does not run.</param>
/// <param name="ForeignSinceBoot">The subset of <paramref name="Foreign"/> whose successful compile
/// (or adoption) is stamped AFTER <paramref name="BootedAt"/> — the never-benign count.</param>
/// <param name="ForeignDetail">The foreign records as <c>&lt;identity&gt;×N in &lt;partition&gt;/… (M since boot)</c>.</param>
/// <param name="BootedAt">When this replica's warm-up started — the boundary the two counts split on.</param>
/// <param name="At">When this reading was folded.</param>
public sealed record NodeTypeLiveRecordCensus(
    string FrameworkVersion,
    int Total,
    int Untyped,
    int Foreign,
    int ForeignSinceBoot,
    string ForeignDetail,
    DateTimeOffset BootedAt,
    DateTimeOffset At)
{
    /// <summary>
    /// Whether this reading degrades <c>bake-report</c>: only a record re-keyed to another framework
    /// AFTER this replica booted does. A foreign record from before boot is the ordinary state every
    /// deploy passes through and every adopt-only portal lives in.
    /// </summary>
    public bool IsClean => ForeignSinceBoot == 0 && StalePastBound == 0;

    /// <summary>
    /// Records serving a build the source has moved past (<see cref="BuildProvenance.StaleAdopted"/>
    /// with assembly coordinates) — Systemorph/Memex#668. Printed on every reading, so a
    /// stale-but-serving type is never silent on <c>/health</c>.
    /// </summary>
    public int StaleServing { get; init; }

    /// <summary>
    /// The subset of <see cref="StaleServing"/> further behind its source than the stale-adoption
    /// bound (<see cref="StaleBound"/>). The judgement refuses such a build, so a non-zero count is a
    /// record nothing has re-judged since the bound took effect — it DEGRADES the entry.
    /// </summary>
    public int StalePastBound { get; init; }

    /// <summary>The bound the stale counts were taken against (negative = disabled).</summary>
    public int StaleBound { get; init; } = StaleAdoptionBound.DefaultMaxMinorVersionsBehind;

    /// <summary>The stale-but-serving records as <c>&lt;partition&gt;/… ×N (max M minor(s) behind)</c>.</summary>
    public string StaleDetail { get; init; } = string.Empty;

    /// <summary>
    /// How many distinct (identity, partition) groups the detail names before the rest are counted —
    /// bounded so a mesh whose every record is foreign cannot turn the health body into a listing.
    /// </summary>
    private const int MaxNamedGroups = 12;

    /// <summary>
    /// 🚨 <b>THE FOLD — pure, so every row is unit-testable with no mesh and no clock.</b>
    ///
    /// <para><paramref name="records"/> is the catalog as enumerated: each dynamic NodeType's path
    /// and its definition, or <c>null</c> where the content could not be typed. Static/framework
    /// types (no compilable source) are the caller's to exclude, exactly as the bake excludes them —
    /// they ship their assembly with the process and name no build to be foreign.</para>
    /// </summary>
    /// <param name="records">The catalog: <c>(path, definition-or-null)</c> per dynamic NodeType.</param>
    /// <param name="liveFrameworkVersion">This process's framework identity — injected, like
    /// <see cref="NodeTypeBakeStatus.Classify"/>'s, so a test can stage a roll without rebuilding.</param>
    /// <param name="bootedAt">The boundary <see cref="ForeignSinceBoot"/> splits on.</param>
    /// <param name="at">When the reading is taken.</param>
    /// <returns>The census.</returns>
    public static NodeTypeLiveRecordCensus Of(
        IEnumerable<(string Path, NodeTypeDefinition? Definition)> records,
        string liveFrameworkVersion,
        DateTimeOffset bootedAt,
        DateTimeOffset at)
        => Of(records, liveFrameworkVersion, bootedAt, at, StaleAdoptionBound.DefaultMaxMinorVersionsBehind);

    /// <summary>
    /// <see cref="Of(IEnumerable{ValueTuple{string, NodeTypeDefinition}}, string, DateTimeOffset, DateTimeOffset)"/>
    /// with the mesh's stale-adoption bound (Systemorph/Memex#668), against which the
    /// stale-but-serving records are counted.
    /// </summary>
    /// <param name="records">The catalog: <c>(path, definition-or-null)</c> per dynamic NodeType.</param>
    /// <param name="liveFrameworkVersion">This process's framework identity.</param>
    /// <param name="bootedAt">The boundary <see cref="ForeignSinceBoot"/> splits on.</param>
    /// <param name="at">When the reading is taken.</param>
    /// <param name="maxMinorVersionsBehind">The stale-adoption bound; negative = disabled.</param>
    /// <returns>The census.</returns>
    public static NodeTypeLiveRecordCensus Of(
        IEnumerable<(string Path, NodeTypeDefinition? Definition)> records,
        string liveFrameworkVersion,
        DateTimeOffset bootedAt,
        DateTimeOffset at,
        int maxMinorVersionsBehind)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(liveFrameworkVersion);

        var total = 0;
        var untyped = 0;
        var foreign = ImmutableList.CreateBuilder<(string Partition, string Identity, bool SinceBoot)>();
        var stale = ImmutableList.CreateBuilder<(string Partition, int? Behind, bool PastBound)>();
        foreach (var (path, definition) in records)
        {
            total++;
            if (definition is null)
            {
                untyped++;
                continue;
            }
            // Memex#668 — a build the source moved past that still has coordinates is SERVING —
            // but only if THIS replica may load it. A record keyed to another framework serves
            // nothing here (it is counted as Foreign below), so a pre-boot record from the previous
            // image must never degrade the entry as stale-past-bound.
            if (definition.BuildProvenance is BuildProvenance.StaleAdopted
                && !string.IsNullOrEmpty(definition.LatestAssemblyPath)
                && NodeTypeBuildIdentity.RefusalReason(definition, liveFrameworkVersion) is null)
                stale.Add((
                    PartitionOf(path),
                    ModuleVersionCompatibility.MinorVersionsBehind(
                        definition.AdoptedModuleVersion, definition.CurrentModuleVersion),
                    StaleAdoptionBound.Exceeds(
                        definition.AdoptedModuleVersion, definition.CurrentModuleVersion,
                        maxMinorVersionsBehind)));
            // THE per-process verdict, never the raw field: Ok is scoped to CompiledFrameworkVersion
            // (#3472), and this is the one function every load path folds the pair through.
            if (NodeTypeBuildIdentity.ReportedStatus(definition, liveFrameworkVersion)
                is not CompilationStatus.Foreign)
                continue;
            // A stamp with no time is foreign of UNKNOWN age: counted as foreign, never as
            // since-boot — an absent reading may not decide the never-benign count in either
            // direction.
            var sinceBoot = NodeTypeBuildIdentity.StampedAfter(definition, bootedAt);
            foreign.Add((PartitionOf(path), Short(definition.CompiledFrameworkVersion), sinceBoot));
        }

        var groups = foreign
            .GroupBy(f => (f.Identity, f.Partition))
            .Select(g => (g.Key.Identity, g.Key.Partition, Count: g.Count(), SinceBoot: g.Count(f => f.SinceBoot)))
            .OrderByDescending(g => g.SinceBoot)
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.Partition, StringComparer.Ordinal)
            .ThenBy(g => g.Identity, StringComparer.Ordinal)
            .ToList();
        var detail = string.Join("; ", groups
            .Take(MaxNamedGroups)
            .Select(g => $"{g.Identity}×{g.Count} in {g.Partition}/… ({g.SinceBoot} since boot)"));
        if (groups.Count > MaxNamedGroups)
            detail += $"; +{groups.Count - MaxNamedGroups} more group(s)";

        var staleGroups = stale
            .GroupBy(s => s.Partition)
            .Select(g => (Partition: g.Key, Count: g.Count(), PastBound: g.Count(s => s.PastBound),
                MaxBehind: g.Max(s => s.Behind)))
            .OrderByDescending(g => g.PastBound)
            .ThenByDescending(g => g.Count)
            .ThenBy(g => g.Partition, StringComparer.Ordinal)
            .ToList();
        var staleDetail = string.Join("; ", staleGroups
            .Take(MaxNamedGroups)
            .Select(g => $"{g.Partition}/… ×{g.Count} (max "
                         + (g.MaxBehind is { } behind ? $"{behind} minor(s)" : "unmeasured")
                         + $" behind, {g.PastBound} past the bound)"));
        if (staleGroups.Count > MaxNamedGroups)
            staleDetail += $"; +{staleGroups.Count - MaxNamedGroups} more partition(s)";

        return new NodeTypeLiveRecordCensus(
            liveFrameworkVersion, total, untyped, foreign.Count, foreign.Count(f => f.SinceBoot),
            detail, bootedAt, at)
        {
            StaleServing = stale.Count,
            StalePastBound = stale.Count(s => s.PastBound),
            StaleBound = maxMinorVersionsBehind,
            StaleDetail = staleDetail,
        };
    }

    /// <summary>
    /// The stale-but-serving sentence (Systemorph/Memex#668) appended to every reading — "none" is
    /// printed as such, never omitted, so its absence cannot be read as a clean measurement.
    /// </summary>
    /// <param name="census">The reading.</param>
    /// <returns>The sentence, with a leading space.</returns>
    private static string DescribeStale(NodeTypeLiveRecordCensus census)
    {
        var bound = StaleAdoptionBound.DescribeBound(census.StaleBound);
        if (census.StaleServing == 0)
            return $" STALE-BUT-SERVING: none — no typed record serves a build its source has moved past ({bound}).";
        if (census.StalePastBound == 0)
            return $" STALE-BUT-SERVING: {census.StaleServing} record(s) serve a build their source has "
                   + $"moved past, all within the stale-adoption {bound}: {census.StaleDetail}. Each keeps "
                   + "serving until a build of the current source lands (MeshWeaver#3583, Memex#668).";
        return $" 🚨 STALE-BUT-SERVING: {census.StalePastBound} of {census.StaleServing} record(s) serve a "
               + $"build FURTHER behind its source than the stale-adoption {bound}: {census.StaleDetail}. "
               + "The judgement refuses such a build; these records have not been re-judged since — a "
               + "release request (the Compile button, the recycle verb) re-runs it (Memex#668).";
    }

    /// <summary>
    /// The one sentence an operator reads on <c>/health</c>. Pure — it is the whole publication.
    ///
    /// <para>🚨 Three readings, three different sentences, and an absent one is never a clean one:
    /// "no census has been taken", "a census was taken and nothing was re-keyed since boot", and
    /// "N records were re-keyed since boot" must never be confusable, and every count states its
    /// denominator.</para>
    /// </summary>
    /// <param name="census">The reading, or <c>null</c> when none has been taken.</param>
    /// <returns>The sentence.</returns>
    public static string Describe(NodeTypeLiveRecordCensus? census)
    {
        if (census is null)
            return "LIVE RECORD CENSUS: NONE taken on this replica — the standing NodeType-catalog "
                   + "watch has delivered no reading (the pre-warmer is not registered here, or the "
                   + "catalog subscription has not emitted yet), so nothing here says whether a "
                   + "record was re-keyed to another framework after this replica booted. This is an "
                   + "absence of measurement, NOT a clean one (#4632).";

        var at = census.At.ToString("O", CultureInfo.InvariantCulture);
        var bootedAt = census.BootedAt.ToString("O", CultureInfo.InvariantCulture);
        var denominator =
            $" Denominator: {census.Total} dynamic NodeType record(s) in the catalog, of which "
            + $"{census.Untyped} could not be typed on this hub and were decided about by nothing; "
            + $"this replica's framework is {Short(census.FrameworkVersion)}; booted at {bootedAt}.";

        // 🚨 "typed", not "every": an untyped record was decided about by NOTHING, and a sentence
        // that folded it into the clean case would be a false clean description for exactly the
        // unmeasured case the denominator exists to expose.
        var typed = census.Total - census.Untyped;
        var undecided = census.Untyped == 0
            ? string.Empty
            : $" {census.Untyped} untyped record(s) remain UNDECIDED — neither foreign nor clean.";

        if (census.Foreign == 0)
            return $"LIVE RECORD CENSUS at {at}: every one of the {typed} typed record(s) names a "
                   + "build for this replica's framework, or no build at all — nothing typed is keyed "
                   + "to a framework this replica does not run." + undecided + DescribeStale(census) + denominator;

        if (census.ForeignSinceBoot == 0)
            return $"LIVE RECORD CENSUS at {at}: {census.Foreign} of {typed} typed record(s) name a build keyed to a "
                   + $"framework this replica does not run ({census.ForeignDetail}), ALL stamped before "
                   + "this replica booted — the ordinary previous-image state, which the bake or the "
                   + "first access rebuilds; NONE was re-keyed since boot." + undecided + DescribeStale(census) + denominator;

        return $"🚨 LIVE RECORD CENSUS at {at}: {census.ForeignSinceBoot} NodeType record(s) were "
               + "RE-KEYED to a framework this replica does not run AFTER it booted "
               + $"({census.Foreign} foreign in total): {census.ForeignDetail}. A process on another "
               + "image compiled or adopted those types and stamped the shared record, so THIS replica "
               + "has no usable assembly for them: activation refuses the foreign build, their layout "
               + "areas never register here, and every page that asks for one renders the "
               + "area-not-found frame. No boot-time reading above can see this — the stamp landed "
               + "after they were taken — and content-types cannot either, because an area that never "
               + "registers is not a degraded content read (#4632). The PARTITION each one lives in is "
               + "printed and the node's own name deliberately is not; this body is public (#4258, "
               + "#3890). It clears when the fleet converges on one image, or when this replica "
               + "rebuilds the type against its own framework (the recycle verb, the Compile button)."
               + DescribeStale(census) + denominator;
    }

    /// <summary>The partition a type path lives in — its first segment, and nothing else.</summary>
    private static string PartitionOf(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "(unknown)";
        var slash = path.IndexOf('/');
        return slash > 0 ? path[..slash] : path;
    }

    /// <summary>First eight characters — the width the assembly-store filename tag carries.</summary>
    private static string Short(string? identity)
        => string.IsNullOrEmpty(identity)
            ? "(none)"
            : identity[..Math.Min(8, identity.Length)];
}
