using System.Collections.Immutable;
using System.Reactive;
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
/// <para><b>Dependents first, each settled.</b> Packages are retired in reverse dependency order and
/// each request's phase 1 is OBSERVED to its settlement before the next package is decided, so a
/// dependent phase 1 refuses keeps what it requires installed; a refusal is reported at Error on the
/// same boot, naming the flag and the package.</para>
///
/// <para><b>Idempotent, and the same on every replica.</b> Every replica's boot runs this. Requests
/// are keyed by the install's identity (<see cref="RetireKeyFor"/>) and numbered per attempt
/// (<see cref="RequestIdFor"/>), so replicas deciding the same attempt collide on ONE node; at most
/// one attempt per install is active, a FAILED attempt is re-evaluated by the next boot under the
/// current declaration, and a later boot that finds the install record gone decides nothing.</para>
///
/// <para><b>One restart for the whole batch.</b> The restart that makes every replica drop the
/// retired modules is NOT this pass's, nor any one request's, to issue: before the first request is
/// written, every request this boot will run phase 1 for is enrolled
/// <see cref="PackageRestartFactKind.Pending"/> on the instance's ONE restart coordinator
/// (<see cref="PackageRestartCoordinator"/>, <c>Doc/Architecture/PackageUninstall</c> → "One restart
/// for every retirement"). Each phase 1 states <see cref="PackageRestartFactKind.Ready"/> once its
/// module is durably disabled, or withdraws; the coordinator's own hub dispatches ONE restart once no
/// member of the batch is pending. An enrolment whose request was never written (refused, or required
/// by a package that stays) is withdrawn when the walk ends.</para>
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
    /// <see cref="Decide"/> uses it. It decides what to RETIRE; whether an unattended writer must keep
    /// off an install is the retirement's state (<see cref="IsRetiring"/>), never this match. A
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
    /// The install identity a flag retire of <paramref name="record"/> is keyed by — the same on every
    /// replica for the same install, a new one for a new install (a person installing the package
    /// again while the flag is still off is retired again on the next boot: the record decides). The
    /// readable part is a sanitized package id; the IDENTITY is a SHA-256 over an unambiguous encoding
    /// of the full install (length-prefixed id, install time, version), so two packages whose ids
    /// sanitize alike (<c>Mcp.Host</c>, <c>Mcp-Host</c>) can never share a key (#7959 review). Pure.
    /// </summary>
    /// <param name="record">The install record.</param>
    public static string RetireKeyFor(PackageManifest record)
    {
        var id = record.Id ?? "";
        var version = record.Version ?? "";
        var identity = $"{id.Length}:{id}|{record.InstalledAtUtc?.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"}|{version.Length}:{version}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(identity)))[..16].ToLowerInvariant();
        return ActivationRecycle.Segment($"{RequestPrefix}-{id}") + "-" + hash;
    }

    /// <summary>The request id of attempt <paramref name="attempt"/> (1-based) at retiring the install
    /// <paramref name="retireKey"/> names. Every replica deciding the same attempt collides on one node. Pure.</summary>
    /// <param name="retireKey">The install identity (<see cref="RetireKeyFor"/>).</param>
    /// <param name="attempt">The attempt number.</param>
    public static string RequestIdFor(string retireKey, int attempt) => $"{retireKey}-a{attempt}";

    /// <summary>
    /// 🚨 THE rule every unattended writer of an install record or a module consults (#7959 review):
    /// the install identities (<see cref="RetireKeyFor"/>) a flag-issued request is retiring NOW or
    /// has retired — every such request whose status is not <see cref="PackageUninstallStatus.Failed"/>.
    /// It is the retirement's actual STATE, never the flag match: a package whose retire was REFUSED
    /// (required by a dependent — no request at all — or a phase-1 safety refusal — a failed one) is
    /// NOT in it, so it stays repairable and updatable; and a person's later re-install has a new
    /// identity, so it is not in it either. Read as System from a listing of the request namespace
    /// (paths) and each request's own node (status). Never faults: an unreadable state is logged at
    /// Warning and read as empty.
    /// </summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="logger">Receives a read failure.</param>
    public static IObservable<ImmutableHashSet<string>> RetiringInstalls(IMessageHub hub, ILogger? logger) =>
        hub.ServiceProvider.GetService<IMeshService>() is null
            ? Observable.Return(ImmutableHashSet<string>.Empty)
            : ExistingRequests(hub)
                .Select(all => all
                    .Where(x => x.Request.Status != PackageUninstallStatus.Failed)
                    .Select(x => x.Request.RetireKey!)
                    .ToImmutableHashSet(StringComparer.Ordinal))
                .Catch((Exception ex) =>
                {
                    logger?.LogWarning(ex,
                        "[FlagRetire] the flag retirements in flight could not be read; this pass treats no install as being retired.");
                    return Observable.Return(ImmutableHashSet<string>.Empty);
                });

    /// <summary>Whether <paramref name="record"/>'s install is being (or has been) retired by a flag —
    /// <see cref="RetiringInstalls"/> applied to one record. Pure.</summary>
    /// <param name="record">The install record as the writer read it.</param>
    /// <param name="retiring">The identities <see cref="RetiringInstalls"/> answered.</param>
    public static bool IsRetiring(PackageManifest record, ImmutableHashSet<string> retiring) =>
        IsRetiringKey(RetireKeyFor(record), retiring);

    /// <summary><see cref="IsRetiring"/> for a caller that
    /// carries the install identity (<see cref="RetireKeyFor"/>) rather than the record. Pure.</summary>
    /// <param name="retireKey">The install identity; null (no record) is never retiring.</param>
    /// <param name="retiring">The identities <see cref="RetiringInstalls"/> answered.</param>
    public static bool IsRetiringKey(string? retireKey, ImmutableHashSet<string> retiring) =>
        retireKey is not null && retiring.Contains(retireKey);

    /// <summary>How long one boot waits for one request's phase 1 to settle before it counts the
    /// package as staying (refusing whatever it requires this boot) and says so at Error.</summary>
    public static readonly TimeSpan SettleBudget = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Reads the install records AUTHORITATIVELY (CQRS: a listing names which records exist, each
    /// manifest is read from its own node), decides (<see cref="Decide"/>), and retires each package
    /// through phase 1 — DEPENDENTS FIRST, each one's phase 1 observed to its settlement before the
    /// next is decided, so a dependent that phase 1 refuses keeps what it requires installed. Every
    /// request whose phase 1 may need a restart is enrolled on the instance's ONE restart coordinator
    /// first, so the coordinator dispatches ONE restart for the batch. Never faults. Cold; emits once.
    /// </summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="composition">The environment's composition, read once for this boot pass.</param>
    /// <param name="logger">Receives every decision.</param>
    public static IObservable<Outcome> Retire(IMessageHub hub, FeatureComposition composition, ILogger logger) =>
        Retire(hub, composition, logger, SettleBudget);

    /// <summary><see cref="Retire(IMessageHub, FeatureComposition, ILogger)"/> with an explicit settle budget.</summary>
    internal static IObservable<Outcome> Retire(IMessageHub hub, FeatureComposition composition, ILogger logger, TimeSpan settleBudget)
    {
        if (composition.Retired.Count == 0)
            return Observable.Return(Outcome.Empty);
        return ReadInstalled(hub)
            .SelectMany(installed => Act(hub, installed, Decide(installed, composition.Retired), logger, settleBudget))
            .Catch((Exception ex) =>
            {
                logger.LogError(ex,
                    "[FlagRetire] the packages declared off by a feature flag could not be retired this boot: {Reason}. "
                    + "Nothing further was retired; the next boot decides again.", ex.Message);
                return Observable.Return(Outcome.Empty);
            });
    }

    /// <summary>The install records, read authoritatively: the listing names the record paths; each
    /// manifest is read from its own node stream. A record that cannot be read faults the whole read
    /// — a retire decided over a partial dependency picture would be the wrong decision.</summary>
    private static IObservable<IReadOnlyList<PackageManifest>> ReadInstalled(IMessageHub hub)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"path:{PackageInstaller.InstalledPartition} scope:children nodeType:{PackageInstaller.PackageNodeType}"))
                .Take(1)
                .Timeout(ActivationRecycle.ReadBudget))
            .SelectMany(change => change.Items.Select(n => n.Path).ToObservable()
                .Select(path => access.RunAsSystem(() => hub.GetMeshNodeStream(path).Take(1).Timeout(ActivationRecycle.ReadBudget))
                    // 🚨 A listed record that yields no manifest with an id FAULTS the read, naming its
                    // path (#7959 review): dropping it would hide a dependent from the dependency check.
                    .Select(node => node.ContentAs<PackageManifest>(hub.JsonSerializerOptions) is { Id: { } id } manifest
                                    && !string.IsNullOrWhiteSpace(id)
                        ? manifest
                        : throw new InvalidOperationException(
                            $"the install record {path} could not be read as a package manifest with an id")))
                .MergeBounded(8)
                .ToList())
            .Select(read => (IReadOnlyList<PackageManifest>)read.ToImmutableList());
    }

    private static IObservable<Outcome> Act(
        IMessageHub hub, IReadOnlyList<PackageManifest> installed, Decision decision, ILogger logger, TimeSpan settleBudget)
    {
        foreach (var refused in decision.RefusedRequired)
            LogRefusedRequired(logger, refused.Record.Id, refused.Flag, refused.RequiredBy);
        foreach (var unmatched in decision.NoSource)
            logger.LogWarning(
                "[FlagRetire] {Package} is named by the feature flag '{Flag}' declared off, but its install record names "
                + "no source, so a source-scoped pattern does not match it and it is NOT retired. Re-installing it "
                + "stamps its source.", unmatched.Record.Id, unmatched.Flag);
        var refusedLines = decision.RefusedRequired
            .Select(r => $"{r.Record.Id}: required by {string.Join(", ", r.RequiredBy)}")
            .ToImmutableList();

        // DEPENDENTS FIRST: the dependency order reversed — a package that requires another in the
        // set is retired (and settles) before the one it requires is even decided.
        var byId = decision.Retire.ToImmutableDictionary(c => c.Record.Id, StringComparer.Ordinal);
        var order = PackageDependencyGraph.InDependencyOrder([.. decision.Retire.Select(c => c.Record)], logger)
            .Where(p => byId.ContainsKey(p.Id))
            .Select(p => byId[p.Id])
            .Reverse()
            .ToImmutableList();

        return ExistingRequests(hub)
            .SelectMany(existing =>
            {
                // 🚨 The WHOLE batch is pending on the coordinator before its first request runs, so
                // the first one's ready never dispatches a restart the later ones would need again.
                var enrolled = order.Where(c => NeedsRestartEnrolment(hub, c, existing))
                    .Select(c => (Path: Attempt(c, existing).Path, Covered: c))
                    .ToImmutableList();
                return Enrol(hub, enrolled, logger)
                    .SelectMany(_ => Walk(hub, installed, order, existing, logger, settleBudget, new Progress(refusedLines)))
                    .SelectMany(progress => WithdrawUnwritten(hub,
                            [.. enrolled.Select(e => e.Path).Except([.. progress.Requested, .. progress.AlreadyRequested], StringComparer.Ordinal)],
                            logger)
                        .Select(_ => progress.ToOutcome()));
            });
    }

    /// <summary>The attempt <paramref name="covered"/> is retired by on this boot: the install's ACTIVE
    /// attempt when one exists (joined, never duplicated), else the next one. Pure.</summary>
    private static (string Path, PackageUninstallRequest? Live, int Number, ImmutableList<(string Path, PackageUninstallRequest Request)> Failed) Attempt(
        Covered covered, ImmutableList<(string Path, PackageUninstallRequest Request)> existing)
    {
        var key = RetireKeyFor(covered.Record);
        var attempts = existing.Where(x => string.Equals(x.Request.RetireKey, key, StringComparison.Ordinal)).ToImmutableList();
        var live = attempts.FirstOrDefault(x => x.Request.Status != PackageUninstallStatus.Failed);
        if (live.Path is not null)
            return (live.Path, live.Request, 0, []);
        var number = attempts.Count + 1;
        return (PackageUninstall.PathAt(RequestIdFor(key, number)), null, number, attempts);
    }

    /// <summary>Whether this boot runs phase 1 for <paramref name="covered"/>'s attempt and that phase 1
    /// may need a restart: the package declares a module, the host has a restart path, and the attempt
    /// is new or still in its phase 1 — an attempt already past phase 1 states nothing again, and a
    /// pending enrolment nobody resolves would hold every other restart back. Pure.</summary>
    private static bool NeedsRestartEnrolment(IMessageHub hub, Covered covered,
        ImmutableList<(string Path, PackageUninstallRequest Request)> existing) =>
        !string.IsNullOrWhiteSpace(covered.Record.Module)
        && hub.ServiceProvider.GetService<IModuleActivationRestart>() is not null
        && Attempt(covered, existing).Live is null or { Status: PackageUninstallStatus.Requested or PackageUninstallStatus.Uninstalling };

    /// <summary>States <see cref="PackageRestartFactKind.Pending"/> for each enrolled request, in order.
    /// Never faults: an enrolment that cannot be stated weakens only the batching (a restart may be
    /// dispatched before a later request is ready), never a retire.</summary>
    private static IObservable<Unit> Enrol(IMessageHub hub, ImmutableList<(string Path, Covered Covered)> enrolled, ILogger logger) =>
        enrolled.Select(e => PackageRestartCoordinator.Pending(hub, e.Path, e.Covered.Record.Module!.Trim(),
                    $"declared off by the feature flag '{e.Covered.Flag}'", logger)
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex,
                        "[FlagRetire] {Path} could not be enrolled with the restart coordinator {Coordinator}: {Reason}. "
                        + "Its retire runs all the same; a restart dispatched before it is ready may not cover it.",
                        e.Path, PackageRestartCoordinator.Path, ex.Message);
                    return Observable.Return(Unit.Default);
                }))
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .LastAsync();

    /// <summary>Withdraws the enrolment of every request this boot enrolled but never wrote, so it does
    /// not hold the batch's restart back for the pending budget. A withdraw of a member that is ready or
    /// covered is a no-op on the coordinator. Never faults.</summary>
    private static IObservable<Unit> WithdrawUnwritten(IMessageHub hub, IReadOnlyList<string> paths, ILogger logger) =>
        paths.Select(path => PackageRestartCoordinator.Withdraw(hub, path, logger)
                .Catch((Exception ex) =>
                {
                    logger.LogWarning(ex,
                        "[FlagRetire] withdrawing {Path}, which was never written, from the restart coordinator failed: {Reason}. "
                        + "It stops holding the batch's restart back after {Budget}.",
                        path, ex.Message, PackageRestartCoordinator.PendingBudget);
                    return Observable.Return(Unit.Default);
                }))
            .ToObservable()
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .LastAsync();

    /// <summary>What the walk has done so far — retired ids (settled), written and read request paths, refusals.</summary>
    private sealed record Progress(ImmutableList<string> Refused)
    {
        public ImmutableHashSet<string> Retired { get; init; } = ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase);
        public ImmutableList<string> Requested { get; init; } = [];
        public ImmutableList<string> AlreadyRequested { get; init; } = [];

        public Outcome ToOutcome() => new(Requested, AlreadyRequested, Refused);
    }

    /// <summary>One package at a time, each settled before the next is decided; a package is
    /// re-validated against what has settled so far (a dependent that failed stays installed).</summary>
    private static IObservable<Progress> Walk(
        IMessageHub hub, IReadOnlyList<PackageManifest> installed, ImmutableList<Covered> order,
        ImmutableList<(string Path, PackageUninstallRequest Request)> existing, ILogger logger, TimeSpan settleBudget, Progress progress)
    {
        if (order.IsEmpty)
            return Observable.Return(progress);
        var covered = order[0];
        var rest = order.RemoveAt(0);
        // Re-validate: everything installed that has NOT settled as retired stays — a dependent whose
        // phase 1 failed included — and must not lose what it requires.
        var stays = installed
            .Where(m => !progress.Retired.Contains(m.Id) && !string.Equals(m.Id, covered.Record.Id, StringComparison.OrdinalIgnoreCase))
            .Where(m => (m.Requires ?? []).Any(r => string.Equals(PackageDependencyGraph.DependencyId(r), covered.Record.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(m => m.Id)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToImmutableList();
        if (!stays.IsEmpty)
        {
            LogRefusedRequired(logger, covered.Record.Id, covered.Flag, stays);
            return Walk(hub, installed, rest, existing, logger, settleBudget,
                progress with { Refused = progress.Refused.Add($"{covered.Record.Id}: required by {string.Join(", ", stays)}") });
        }
        return One(hub, covered, existing, logger, settleBudget)
            .SelectMany(step => Walk(hub, installed, rest, existing, logger, settleBudget, progress with
            {
                Retired = step.Retired ? progress.Retired.Add(covered.Record.Id) : progress.Retired,
                Requested = step.Written ? progress.Requested.Add(step.Path) : progress.Requested,
                // Only a request that EXISTS joins the restart census — a refused or failed create names a path
                // with no node behind it, and point-reading it would fault the census (#7959 review).
                AlreadyRequested = step.Written || !step.Exists ? progress.AlreadyRequested : progress.AlreadyRequested.Add(step.Path),
                Refused = step.Failure is { } failure ? progress.Refused.Add($"{covered.Record.Id}: {failure}") : progress.Refused,
            }));
    }

    private static void LogRefusedRequired(ILogger logger, string package, string flag, IReadOnlyList<string> requiredBy) =>
        logger.LogError(
            "[FlagRetire] {Package} is declared off by the feature flag '{Flag}' but is NOT retired: the installed "
            + "package(s) {RequiredBy} require it, and would fail at use without it. Declare them off as well "
            + "(they are then retired together), or enable '{Flag}'.",
            package, flag, string.Join(", ", requiredBy), flag);

    /// <param name="Exists">Whether a request node exists at <paramref name="Path"/> — false when the
    /// create was refused or failed for any reason other than a collision (#7959 review).</param>
    private sealed record Step(string Path, bool Written, bool Retired, string? Failure, bool Exists = true);

    /// <summary>Every flag-issued request, read authoritatively (listing for paths, each from its node).</summary>
    private static IObservable<ImmutableList<(string Path, PackageUninstallRequest Request)>> ExistingRequests(IMessageHub hub)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(
                    $"namespace:{PackageUninstallRequest.Namespace} scope:children nodeType:{PackageUninstallRequest.NodeType}"))
                .Take(1)
                .Timeout(ActivationRecycle.ReadBudget))
            .SelectMany(change => change.Items
                .Where(n => n.Id.StartsWith(RequestPrefix + "-", StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Path)
                .ToObservable()
                .Select(path => access.RunAsSystem(() => hub.GetMeshNodeStream(path).Take(1).Timeout(ActivationRecycle.ReadBudget))
                    .Select(node => (Path: path, Request: node.ContentAs<PackageUninstallRequest>(hub.JsonSerializerOptions))))
                .MergeBounded(8)
                .ToList())
            .Select(read => read
                .Where(x => x.Request is { RetireKey: not null })
                .Select(x => (x.Path, x.Request!))
                .ToImmutableList());
    }

    /// <summary>
    /// Retires one package: joins the install's ACTIVE attempt if one exists (another replica's, or an
    /// earlier boot's still running), counts a settled one, and otherwise writes the next attempt —
    /// a FAILED attempt is re-evaluated under the current declaration by a new attempt, so at most one
    /// attempt per install is ever active. Then observes that attempt to its settlement and reports a
    /// failure at Error, on THIS boot, naming the flag and the package.
    /// </summary>
    private static IObservable<Step> One(
        IMessageHub hub, Covered covered, ImmutableList<(string Path, PackageUninstallRequest Request)> existing,
        ILogger logger, TimeSpan settleBudget)
    {
        var key = RetireKeyFor(covered.Record);
        var (path, live, attempt, failedAttempts) = Attempt(covered, existing);
        if (live is not null)
            return Settle(hub, covered, path, logger, settleBudget).Select(s => s with { Written = false });
        foreach (var failed in failedAttempts)
            logger.LogError(
                "[FlagRetire] {Package}, declared off by the feature flag '{Flag}': the earlier retire {Path} was refused — {Failure}. "
                + "Re-evaluating under the current declaration with a new attempt.",
                covered.Record.Id, covered.Flag, failed.Path, failed.Request.Failure ?? "(no reason recorded)");
        var id = RequestIdFor(key, attempt);
        var request = new PackageUninstallRequest
        {
            Package = covered.Record.Id,
            Reason = $"declared off by the feature flag '{covered.Flag}' (Features:Flags:{covered.Flag}:Enabled=false) — "
                     + $"retired at boot (attempt {attempt}); its data is retained, and no confirmation can drop it",
            RequestedBy = null,
            RetiredByFlag = covered.Flag,
            RetireOnEveryReplica = true,
            RetireKey = key,
        };
        return PackageUninstall.RequestAt(hub, request, id)
            .SelectMany(ticket =>
            {
                if (ticket.Refusal is { } refusal)
                {
                    logger.LogError(
                        "[FlagRetire] {Package}, declared off by the feature flag '{Flag}', could not be retired: {Refusal}",
                        covered.Record.Id, covered.Flag, refusal);
                    return Observable.Return(new Step(path, false, false, refusal, Exists: false));
                }
                logger.LogInformation(
                    "[FlagRetire] {Package} is declared off by the feature flag '{Flag}' — retire requested at {Path} "
                    + "(phase 1: module retired, install record removed, data retained)",
                    covered.Record.Id, covered.Flag, path);
                return Settle(hub, covered, path, logger, settleBudget);
            })
            .Catch((Exception ex) =>
            {
                // The create collided: another replica's boot wrote the same attempt first — join it.
                // Any OTHER fault is not a collision, and reading a node that may not exist would open
                // the storm-breaker on its path, so it is said and the package counts as staying.
                if (ex.IsNodeAlreadyExists())
                    return Settle(hub, covered, path, logger, settleBudget).Select(s => s with { Written = false });
                logger.LogError(ex,
                    "[FlagRetire] {Package}, declared off by the feature flag '{Flag}': the retire request {Path} could not be "
                    + "written: {Reason}. The next boot decides again.", covered.Record.Id, covered.Flag, path, ex.Message);
                return Observable.Return(new Step(path, false, false, ex.Message, Exists: false));
            });
    }

    /// <summary>Observes a request until phase 1 settles: retired (awaiting confirmation or beyond) or
    /// refused — the refusal at Error naming the flag and the package. Not settling within the budget
    /// is reported at Error too, and counts as staying.</summary>
    private static IObservable<Step> Settle(IMessageHub hub, Covered covered, string path, ILogger logger, TimeSpan budget)
    {
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        return access.RunAsSystem(() => hub.GetMeshNodeStream(path))
            .Select(node => node.ContentAs<PackageUninstallRequest>(hub.JsonSerializerOptions))
            .Where(r => r is { Status: PackageUninstallStatus.AwaitingConfirmation or PackageUninstallStatus.TearingDown
                                or PackageUninstallStatus.Done or PackageUninstallStatus.Failed })
            .Take(1)
            .Timeout(budget)
            .Select(r =>
            {
                if (r!.Status == PackageUninstallStatus.Failed)
                {
                    logger.LogError(
                        "[FlagRetire] {Package} is declared off by the feature flag '{Flag}' but its retire {Path} was refused: "
                        + "{Failure}. It stays installed; the next boot re-evaluates it.",
                        covered.Record.Id, covered.Flag, path, r.Failure ?? "(no reason recorded)");
                    return new Step(path, true, false, r.Failure ?? "refused");
                }
                logger.LogInformation(
                    "[FlagRetire] {Package}, declared off by the feature flag '{Flag}', is retired ({Path}: {Outcome})",
                    covered.Record.Id, covered.Flag, path, r.ModuleOutcome ?? "no module");
                return new Step(path, true, true, null);
            })
            .Catch((Exception ex) =>
            {
                logger.LogError(
                    "[FlagRetire] {Package}, declared off by the feature flag '{Flag}': its retire {Path} did not settle "
                    + "({Reason}); it counts as staying this boot, so nothing it requires is retired.",
                    covered.Record.Id, covered.Flag, path, ex.Message);
                return Observable.Return(new Step(path, true, false, $"did not settle: {ex.Message}"));
            });
    }
}
