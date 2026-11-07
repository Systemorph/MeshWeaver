using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Features;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>A flag declared off retires what is installed</b> (MeshWeaver.Plugins#5055,
/// <c>Doc/Architecture/EnvironmentComposition</c> → "A disabled flag retires what is installed").
///
/// <para>An installed package that a flag declared with an explicit <c>Enabled=false</c> covers is
/// retired at boot through phase 1 of the ONE package-uninstall request
/// (<see cref="PackageUninstall"/>, <see cref="PackageUninstallExecutor"/>): its module is retired,
/// its install record removed, its data RETAINED. There is no second removal mechanism here — this
/// class only decides WHAT the declaration retires and writes the request. Phase 2 can never follow:
/// the request names no requester, and the executor refuses a confirmation for such a request.</para>
///
/// <para><b>Declared, never derived</b> (policy <c>declared-not-derived</c>). Only
/// <see cref="FeatureComposition.Retired"/> is read — a flag that is ABSENT retires nothing (an
/// absent contributor is never a removal), and a non-boolean <c>Enabled</c> excludes from install
/// without retiring (<see cref="FeatureFlag.DeclaredDisabled"/>).</para>
///
/// <para><b>A package another installed package requires is REFUSED</b>, by name, at Error (which
/// the instance's error telemetry carries as a signature): retiring it would leave the dependent
/// installed and failing at use. Declaring the dependent off too retires both.</para>
///
/// <para><b>Idempotent, and the same on every replica.</b> Every replica's boot runs this. The
/// request id is derived from the install record (<see cref="RequestIdFor"/>), so replicas booting
/// together collide on ONE request node rather than each opening one; a later boot finds the install
/// record gone and decides nothing, and a request that already exists is read and reported — a
/// refused one at Error on every boot until the record or the instance changes.</para>
/// </summary>
public static class DeclaredDisabledRetirement
{
    /// <summary>The request-id prefix of a flag-issued retire.</summary>
    public const string RequestPrefix = "flag-retire";

    /// <summary>One installed package the declaration covers, and the flag that declared it.</summary>
    /// <param name="Record">The install record.</param>
    /// <param name="Flag">The flag declared off.</param>
    public sealed record Covered(PackageManifest Record, string Flag);

    /// <summary>A covered package that is NOT retired because installed packages that stay require it.</summary>
    /// <param name="Record">The install record.</param>
    /// <param name="Flag">The flag declared off.</param>
    /// <param name="RequiredBy">The installed packages that require it and are not retired.</param>
    public sealed record Refused(PackageManifest Record, string Flag, ImmutableList<string> RequiredBy);

    /// <summary>What the declaration retires, refuses, and cannot match. Pure output of <see cref="Decide"/>.</summary>
    /// <param name="Retire">Retired through phase 1.</param>
    /// <param name="RefusedRequired">Refused: required by an installed package that stays.</param>
    /// <param name="NoSource">Install records naming no source whose id an exact pattern names —
    /// not retired (a source-scoped pattern matches nothing on them, failing closed), but said.</param>
    public sealed record Decision(
        ImmutableList<Covered> Retire,
        ImmutableList<Refused> RefusedRequired,
        ImmutableList<Covered> NoSource)
    {
        /// <summary>Nothing to do.</summary>
        public static Decision None { get; } = new([], [], []);
    }

    /// <summary>What one boot's retire did, per package.</summary>
    /// <param name="Requested">Request paths written this boot.</param>
    /// <param name="AlreadyRequested">Request paths that existed already (another replica, an earlier boot).</param>
    /// <param name="Refused">Package ids refused, with why.</param>
    public sealed record Outcome(
        ImmutableList<string> Requested,
        ImmutableList<string> AlreadyRequested,
        ImmutableList<string> Refused)
    {
        /// <summary>Nothing done.</summary>
        public static Outcome Empty { get; } = new([], [], []);
    }

    /// <summary>
    /// Which installed packages the flags declared off retire. Pure. A package an installed package
    /// that is NOT itself retired requires is refused, to a fixed point: refusing one keeps it
    /// installed, so whatever IT requires is refused in turn.
    /// </summary>
    /// <param name="installed">The install records.</param>
    /// <param name="retired">The declared-off patterns (<see cref="FeatureComposition.Retired"/>).</param>
    public static Decision Decide(IReadOnlyList<PackageManifest> installed, IReadOnlyList<FeaturePackage> retired)
    {
        var patterns = Patterns(retired);
        if (patterns.Count == 0 || installed.Count == 0)
            return Decision.None;

        var covered = new List<Covered>();
        var noSource = new List<Covered>();
        foreach (var record in installed.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (FlagMatching(patterns, record.Source, record.Id) is { } flag)
            {
                covered.Add(new Covered(record, flag));
                continue;
            }
            if (string.IsNullOrWhiteSpace(record.Source)
                && patterns.FirstOrDefault(p => p.Entry.PackageId != PluginGrantEntry.AllPackages
                                                && string.Equals(p.Entry.PackageId, record.Id, StringComparison.Ordinal))
                    is { Entry: not null } byId)
                noSource.Add(new Covered(record, byId.Flag));
        }

        var retiring = covered.Select(c => c.Record.Id).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var refusedNow = retiring
                .Where(id => RequiredBy(installed, retiring, id).Count > 0)
                .ToList();
            if (refusedNow.Count == 0)
                break;
            retiring = retiring.Except(refusedNow);
        }

        return new Decision(
            [.. covered.Where(c => retiring.Contains(c.Record.Id))],
            [.. covered.Where(c => !retiring.Contains(c.Record.Id))
                .Select(c => new Refused(c.Record, c.Flag, RequiredBy(installed, retiring, c.Record.Id)))],
            [.. noSource]);
    }

    /// <summary>
    /// THE rule for "a flag declared off covers this package" — the declared-off flag whose
    /// source-scoped pattern matches <paramref name="source"/>/<paramref name="packageId"/>, or null.
    /// <see cref="Decide"/> uses it, and so does every lane that must not write a package the
    /// declaration retires (the repair's re-assert), so there is one decision, not two copies. A
    /// package naming no source matches nothing (fails closed, as every source-scoped lane does).
    /// Pure.
    /// </summary>
    /// <param name="source">The package's source, as its record or listing carries it.</param>
    /// <param name="packageId">The package id.</param>
    /// <param name="retired">The declared-off patterns (<see cref="FeatureComposition.Retired"/>).</param>
    public static string? CoveredBy(string? source, string packageId, IReadOnlyList<FeaturePackage> retired) =>
        FlagMatching(Patterns(retired), source, packageId);

    private static string? FlagMatching(IReadOnlyList<(string Flag, PluginGrantEntry Entry)> patterns, string? source, string packageId) =>
        patterns.FirstOrDefault(p => p.Entry.Matches(source ?? "", packageId)) is { Entry: not null } hit ? hit.Flag : null;

    private static IReadOnlyList<(string Flag, PluginGrantEntry Entry)> Patterns(IReadOnlyList<FeaturePackage> retired) =>
        retired
            .Select(p => (p.Flag, Entry: PluginGrantEntry.TryParse(p.Package)))
            .Where(p => p.Entry is not null)
            .Select(p => (p.Flag, Entry: p.Entry!))
            .ToList();

    /// <summary>The installed packages, outside <paramref name="retiring"/>, that require <paramref name="id"/>.</summary>
    private static ImmutableList<string> RequiredBy(IReadOnlyList<PackageManifest> installed, ImmutableHashSet<string> retiring, string id) =>
        [.. installed
            .Where(m => !retiring.Contains(m.Id) && !string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
            .Where(m => (m.Requires ?? []).Any(r => string.Equals(PackageDependencyGraph.DependencyId(r), id, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Id)
            .OrderBy(m => m, StringComparer.Ordinal)];

    /// <summary>
    /// The request id a flag retire of <paramref name="record"/> uses — the same on every replica
    /// for the same install, a new one for a new install (a person installing the package again
    /// while the flag is still off is retired again on the next boot: the record decides). Pure.
    /// </summary>
    /// <param name="record">The install record.</param>
    public static string RequestIdFor(PackageManifest record) =>
        ActivationRecycle.Segment(
            $"{RequestPrefix}-{record.Id}-{record.InstalledAtUtc?.ToUnixTimeMilliseconds() ?? 0}-{record.Version ?? "0"}");

    /// <summary>
    /// Reads the install records, decides (<see cref="Decide"/>), and writes one phase-1 request per
    /// retired package at its derived id. Never faults: a failure is logged at Error and the boot
    /// pass continues. Cold; emits once.
    /// </summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="composition">The environment's composition, read once for this boot pass.</param>
    /// <param name="logger">Receives every decision.</param>
    public static IObservable<Outcome> Retire(IMessageHub hub, FeatureComposition composition, ILogger logger)
    {
        if (composition.Retired.Count == 0)
            return Observable.Return(Outcome.Empty);
        return ModuleDependencyFloor.ReadInstalled(hub)
            .Select(installed => Decide(installed, composition.Retired))
            .SelectMany(decision => Act(hub, decision, logger))
            .Catch((Exception ex) =>
            {
                logger.LogError(ex,
                    "[FlagRetire] the packages declared off by a feature flag could not be retired this boot: {Reason}. "
                    + "Nothing was retired; the next boot decides again.", ex.Message);
                return Observable.Return(Outcome.Empty);
            });
    }

    private static IObservable<Outcome> Act(IMessageHub hub, Decision decision, ILogger logger)
    {
        foreach (var refused in decision.RefusedRequired)
            logger.LogError(
                "[FlagRetire] {Package} is declared off by the feature flag '{Flag}' but is NOT retired: the installed "
                + "package(s) {RequiredBy} require it, and would fail at use without it. Declare them off as well "
                + "(they are then retired together), or enable '{Flag}'.",
                refused.Record.Id, refused.Flag, string.Join(", ", refused.RequiredBy), refused.Flag);
        foreach (var unmatched in decision.NoSource)
            logger.LogWarning(
                "[FlagRetire] {Package} is named by the feature flag '{Flag}' declared off, but its install record names "
                + "no source, so a source-scoped pattern does not match it and it is NOT retired. Re-installing it "
                + "stamps its source.", unmatched.Record.Id, unmatched.Flag);
        var refusedLines = decision.RefusedRequired
            .Select(r => $"{r.Record.Id}: required by {string.Join(", ", r.RequiredBy)}")
            .ToImmutableList();
        if (decision.Retire.Count == 0)
            return Observable.Return(Outcome.Empty with { Refused = refusedLines });

        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        // CQRS: a LISTING says which requests exist (stale-tolerant — a stale negative only costs a
        // create that collides on the derived id, read below); each one's status is read from its own node.
        return access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{PackageUninstallRequest.Namespace} scope:children nodeType:{PackageUninstallRequest.NodeType}"))
                .Take(1)
                .Timeout(ActivationRecycle.ReadBudget))
            .Select(change => change.Items.Select(n => n.Path).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase))
            .SelectMany(existing => decision.Retire.ToObservable()
                .Select(covered => One(hub, covered, existing, logger))
                .Concat()
                .ToList())
            .Select(results => new Outcome(
                [.. results.Where(r => r.Written).Select(r => r.Path)],
                [.. results.Where(r => !r.Written).Select(r => r.Path)],
                refusedLines));
    }

    private sealed record Step(string Path, bool Written);

    private static IObservable<Step> One(IMessageHub hub, Covered covered, ImmutableHashSet<string> existing, ILogger logger)
    {
        var id = RequestIdFor(covered.Record);
        var path = PackageUninstall.PathAt(id);
        if (existing.Contains(path))
            return Report(hub, covered, path, logger).Select(_ => new Step(path, false));
        var request = new PackageUninstallRequest
        {
            Package = covered.Record.Id,
            Reason = $"declared off by the feature flag '{covered.Flag}' (Features:Flags:{covered.Flag}:Enabled=false) — "
                     + "retired at boot; its data is retained, and no confirmation can drop it",
            RequestedBy = null,
            RetiredByFlag = covered.Flag,
            RetireOnEveryReplica = true,
        };
        return PackageUninstall.RequestAt(hub, request, id)
            .Select(ticket =>
            {
                if (ticket.Refusal is { } refusal)
                    logger.LogError(
                        "[FlagRetire] {Package}, declared off by the feature flag '{Flag}', could not be retired: {Refusal}",
                        covered.Record.Id, covered.Flag, refusal);
                else
                    logger.LogInformation(
                        "[FlagRetire] {Package} is declared off by the feature flag '{Flag}' — retire requested at {Path} "
                        + "(phase 1: module retired, install record removed, data retained)",
                        covered.Record.Id, covered.Flag, path);
                return new Step(path, ticket.Accepted);
            })
            // The create collided: another replica's boot wrote the same derived id first — read it.
            // Any OTHER fault is not a collision, and reading a node that may not exist would open
            // the storm-breaker on its path, so it is said and nothing is read.
            .Catch((Exception ex) =>
            {
                if (ex.IsNodeAlreadyExists())
                    return Report(hub, covered, path, logger).Select(_ => new Step(path, false));
                logger.LogError(ex,
                    "[FlagRetire] {Package}, declared off by the feature flag '{Flag}': the retire request {Path} could not be "
                    + "written: {Reason}. The next boot decides again.", covered.Record.Id, covered.Flag, path, ex.Message);
                return Observable.Empty<Step>();
            });
    }

    /// <summary>Reads an existing flag-retire request and says where it stands — a refused one at Error.</summary>
    private static IObservable<System.Reactive.Unit> Report(IMessageHub hub, Covered covered, string path, ILogger logger)
    {
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => hub.GetMeshNodeStream(path).Take(1).Timeout(ActivationRecycle.ReadBudget))
            .Select(node => node.ContentAs<PackageUninstallRequest>(hub.JsonSerializerOptions))
            .Do(request =>
            {
                if (request is { Status: PackageUninstallStatus.Failed })
                    logger.LogError(
                        "[FlagRetire] {Package} is declared off by the feature flag '{Flag}' but its retire {Path} was refused: "
                        + "{Failure}. It stays installed until that is resolved.",
                        covered.Record.Id, covered.Flag, path, request.Failure ?? "(no reason recorded)");
                else
                    logger.LogInformation(
                        "[FlagRetire] {Package}, declared off by the feature flag '{Flag}', already has its retire at {Path} ({Status})",
                        covered.Record.Id, covered.Flag, path, request?.Status ?? "unreadable");
            })
            .Select(_ => System.Reactive.Unit.Default)
            .Catch((Exception ex) =>
            {
                logger.LogError(ex,
                    "[FlagRetire] {Package}, declared off by the feature flag '{Flag}': its retire request {Path} could be "
                    + "neither written nor read: {Reason}", covered.Record.Id, covered.Flag, path, ex.Message);
                return Observable.Return(System.Reactive.Unit.Default);
            });
    }
}
