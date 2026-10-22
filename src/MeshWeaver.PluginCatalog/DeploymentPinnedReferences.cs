using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The <see cref="PinnedPlatformReferenceSource"/> of a control instance: every platform build a
/// <c>Hosting/Deployment</c> record PINS (<c>pinnedImageTag</c>) and every platform build a
/// registered instance REPORTS running or adopted (<c>Hosting/ModuleInventory</c> —
/// <see cref="DeploymentReport.PlatformVersion"/> and <see cref="DeploymentReport.FrameworkIdentity"/>).
///
/// <para>On the registry (memex-cloud) a remote instance pulls its OWN identity's seal over the
/// HTTP prebuilt surface, so an identity such an instance pins or runs is referenced however old
/// it is. Age alone cannot reveal whether a remote instance still uses that build.
/// A Deployment record's <c>pinnedImageTag</c> is a mesh NodeType defined outside this assembly,
/// so it is read as JSON (case-insensitively) rather than through a CLR type.</para>
///
/// <para>Read as System, mesh-wide, on every pass. Errors propagate: a pin set that could not be
/// read aborts the pass, as does a report with an incomplete adoption inventory.</para>
///
/// <para>🚨 A REPORT THAT NEVER ARRIVED IS NOT A CONSUMER THAT IS NOT THERE. The per-report
/// completeness signal (<c>adoptedFrameworkInventoryComplete</c>) says whether ONE instance
/// answered fully; it says nothing about whether every instance answered at all, or whether the
/// answer still describes the instance. <see cref="Resolve"/> therefore takes the
/// <c>Hosting/Deployment</c> records as the DENOMINATOR — every non-retired record whose
/// <c>pluginRepos</c> DECLARE that its instance pulls from THIS host's registry is an expected
/// consumer — and refuses the whole pass when one of them has no report, an unreadable report or a
/// report older than <see cref="StaleAfter"/>, naming each. An incomplete inventory is a refusal,
/// never a shorter reference list. #3438/#3858.</para>
///
/// <para>🚨 HOLDING A RECORD IS NOT CONSUMING THIS STORE (#6989). The denominator used to be every
/// record the host held, and a host holding a SYNCED copy of the Deployments folder whose reports
/// all went to the control instance refused on every pass, for ever — the shared volume was never
/// reclaimed. The consumer relation is declared on the consumer's record (its mount names the
/// registry it pulls from), and the reports reach the store because each instance also delivers its
/// report to every registry it mounts (<see cref="DeploymentReportService"/>, filed there under the
/// instance id the registry AUTHENTICATED). An expected consumer that is silent still refuses.</para>
///
/// <para>🚨 A SUSPENDED CONSUMER IS PAUSED, NOT GONE (#6989). A suspended instance stops reporting,
/// so its last report ages past <see cref="StaleAfter"/> about a day later and used to refuse every
/// pass until the record was retired. Its record DECLARES the suspension
/// (<c>status: Suspended</c>, <see cref="IsSuspended"/>), so the pass reads it from there and never
/// derives it from the silence: the stale report no longer refuses, and the builds that report
/// names stay PINNED, because the instance may be resumed onto exactly those. It is not retired —
/// its pins are kept, never dropped — and a suspended consumer with no readable report still
/// refuses, since what it would resume onto is then unknown.</para>
/// </summary>
public static class DeploymentPinnedReferences
{
    /// <summary>The Deployment record's node type (a mesh NodeType shipped by the Hosting plugin).</summary>
    public const string DeploymentNodeType = "Hosting/Deployment";

    /// <summary>The Deployment record's pinned image tag field.</summary>
    public const string PinnedImageTagField = "pinnedImageTag";

    /// <summary>The Deployment record's lifecycle status field (an open string vocabulary owned by
    /// the Hosting plugin).</summary>
    public const string StatusField = "status";

    /// <summary>The <see cref="StatusField"/> value of a record whose instance is suspended: paused,
    /// resumable, and not retired.</summary>
    public const string SuspendedStatus = "Suspended";

    private static readonly TimeSpan EnumerationBudget = TimeSpan.FromSeconds(60);

    /// <summary>The source, bound to the mesh hub the host resolves at call time.</summary>
    public static PinnedPlatformReferenceSource SourceFor(IServiceProvider services) =>
        () =>
        {
            var hub = services.GetService<IMessageHub>();
            var meshService = hub?.ServiceProvider.GetService<IMeshService>();
            if (hub is null || meshService is null)
                return Observable.Throw<ImmutableList<PinnedPlatformReference>>(new InvalidOperationException(
                    "no mesh hub / IMeshService on this host — the pinned platform builds cannot be read"));
            var logger = hub.ServiceProvider.GetService<ILogger<PrebuiltBundleRetentionHostedService>>();
            return Read(hub, meshService, logger);
        };

    /// <summary>The pins and the reported builds, in one list.</summary>
    public static IObservable<ImmutableList<PinnedPlatformReference>> Read(
        IMessageHub hub, IMeshService meshService, ILogger? logger = null)
    {
        var access = hub.ServiceProvider.GetService<AccessService>();
        var scope = ScopeOf(hub.ServiceProvider.GetService<IConfiguration>(),
            hub.ServiceProvider.GetService<PluginCatalogOptions>());
        return access.RunAsSystem(() =>
            Records(meshService, DeploymentNodeType)
                .Zip(Records(meshService, DeploymentReportService.InventoryNodeType),
                    (deployments, inventories) => Resolve(
                        deployments, inventories, hub.JsonSerializerOptions, DateTimeOffset.UtcNow, StaleAfter,
                        scope, logger)));
    }

    /// <summary>
    /// The store this host's retention protects, as configuration names it: the deployment id this
    /// host IS (<c>Hosting:Deployment</c>), whether it is the fleet's report sink
    /// (<see cref="IsControlInstance"/>), and the public host it serves its registry under
    /// (<c>PluginCatalog:HomeUrl</c>). The host named on this host's OWN Deployment record is added
    /// by <see cref="Resolve"/>, because that record is one of its inputs.
    /// </summary>
    /// <param name="configuration">The host's configuration.</param>
    /// <param name="options">The host's plugin-catalog options, whose <c>HomeUrl</c> wins over the raw key.</param>
    public static ConsumerScope ScopeOf(IConfiguration? configuration, PluginCatalogOptions? options)
    {
        var self = configuration?[DeploymentReportService.DeploymentKey]?.Trim();
        var homeUrl = !string.IsNullOrWhiteSpace(options?.HomeUrl)
            ? options!.HomeUrl
            : configuration?["PluginCatalog:HomeUrl"];
        var ops = configuration?[DeploymentReportService.OperationalSpaceKey]?.Trim();
        return new ConsumerScope(
            string.IsNullOrWhiteSpace(self) ? null : self,
            HostOf(homeUrl) is { } host ? [host] : [],
            IsControlInstance(configuration))
        {
            OperationalSpace = string.IsNullOrEmpty(ops) ? DeploymentReportService.DefaultOperationalSpace : ops,
        };
    }

    /// <summary>
    /// How long after its <see cref="DeploymentReport.SampledAt"/> a report still describes the
    /// instance. The reporter's cadence is <c>Hosting:ReportInterval</c>, one hour by default
    /// ([DeploymentInventory](/Doc/Architecture/DeploymentInventory)), so this is twenty-four
    /// consecutive missed ticks — long enough that a restart, a slow tick or a maintenance window
    /// is not a refusal, short enough that a version an instance stopped running yesterday cannot
    /// still be the only thing protection knows about it.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// Does this host own a fleet? The reporter's own contract answers it
    /// ([DeploymentInventory](/Doc/Architecture/DeploymentInventory)): an instance names itself with
    /// <c>Hosting:Deployment</c>, and one with no <c>Hosting:ReportTo</c> IS the control instance —
    /// it files its own record locally instead of posting it.
    ///
    /// <para>🚨 THIS IS THE SIGNAL THAT MAKES A ZERO HONEST. Without it, "no Deployment records" is
    /// read the same way on an ordinary portal (a true measured zero — it has no fleet) and on a
    /// control instance whose record index has not caught up (an empty protected set over a fleet
    /// that is very much running). Configuration is authoritative where a query is eventually
    /// consistent, so the ONE place the two cases can be told apart is here.</para>
    /// </summary>
    public static bool IsControlInstance(IConfiguration? configuration) =>
        configuration is not null
        && !string.IsNullOrWhiteSpace(configuration[DeploymentReportService.DeploymentKey])
        && string.IsNullOrWhiteSpace(configuration[DeploymentReportService.ReportToKey]);

    /// <summary>
    /// The fleet's consumer inventory, or a refusal — never a smaller number.
    ///
    /// <para>Pure over its inputs so the whole verdict can be driven from fixtures. Every
    /// <c>Hosting/Deployment</c> record that is not explicitly retired, is not this host, and mounts a
    /// registry served as one of this store's hosts (<paramref name="scope"/>, plus the <c>host</c> of
    /// this host's own record) is an EXPECTED consumer, and an expected consumer with no report, an unreadable report or a report older than
    /// <paramref name="freshnessBudget"/> throws. That asymmetry is the point: an instance whose
    /// report never arrived is indistinguishable, in the references it contributes, from an
    /// instance that consumes nothing — and one of those two readings authorises deleting what it
    /// is running. #3438/#3858.</para>
    ///
    /// <para>Reports from instances with no Deployment record are still read: an unexpected
    /// consumer is a consumer. Only the EXPECTED set decides whether the inventory is complete.</para>
    ///
    /// <para>🚨 ZERO EXPECTED CONSUMERS IS A TRUE ANSWER, NOT A VACUOUS ONE — but only on a host
    /// that is nobody's fleet. This source is registered on every portal
    /// (<c>MemexConfiguration</c>), so an ordinary installation holds no Deployment records and has
    /// nothing to account for; its own live identity, its adoption stamps and the 30-day floor are
    /// what protect it. A host that RECEIVES reports is a control instance by construction, and one
    /// holding reports but no records is refused rather than read as a fleet of zero. A read that
    /// FAILS never reaches here at all: the queries carry their own budget and their error aborts
    /// the pass.</para>
    /// </summary>
    /// <param name="deployments">Every active <c>Hosting/Deployment</c> record this host holds.</param>
    /// <param name="inventories">Every active <c>Hosting/ModuleInventory</c> report this host holds.</param>
    /// <param name="options">The hub's serializer options.</param>
    /// <param name="now">The instant freshness is judged at.</param>
    /// <param name="freshnessBudget">How old a report may be and still describe its instance.</param>
    /// <param name="scope">The store this host serves (<see cref="ScopeOf"/>).</param>
    /// <param name="logger">Where the verdict is logged, if anywhere.</param>
    public static ImmutableList<PinnedPlatformReference> Resolve(
        IReadOnlyList<MeshNode> deployments,
        IReadOnlyList<MeshNode> inventories,
        JsonSerializerOptions options,
        DateTimeOffset now,
        TimeSpan freshnessBudget,
        ConsumerScope scope,
        ILogger? logger = null)
    {
        var isControlInstance = scope.IsControlInstance;
        // 🚨 THE STORE'S OWN NAME decides who consumes it. Configuration names it, and so does this
        // host's OWN Deployment record (its `host`) — the name the consumers' mounts are written
        // against.
        var storeHosts = scope.StoreHosts;
        if (scope.SelfDeployment is { } selfId
            && deployments.FirstOrDefault(r => string.Equals(r.Id, selfId, StringComparison.OrdinalIgnoreCase))
                is { } selfRecord
            && HostOf(Field(selfRecord, "host", options)) is { } ownHost)
            storeHosts = storeHosts.Add(ownHost);

        // 🚨 AN INSTANCE MAY HAVE MORE THAN ONE RECORD HERE, and which one answers matters. A
        // control instance files its own report locally while remote ones arrive through the inbox,
        // so two nodes can describe one deployment. Every one of them contributes its references —
        // an extra consumer is a consumer — but the FRESHNESS verdict is taken from the newest,
        // because a stale duplicate beside a current report is not a stale instance.
        //
        // 🚨 TWO KINDS OF REPORT, MATCHED TWO WAYS (#6993 review). A report a STORE filed carries the
        // registry instance id the store AUTHENTICATED (`registeredInstance`); it may stand only for
        // the record that DECLARES that registry identity (`registryInstanceId`, else the record id).
        // A report the trusted report sink filed carries no such stamp and is matched by the
        // deployment id it names. Mixing the two let an unrelated registry account whose id happens
        // to equal a record id satisfy a record that declared another registry identity.
        //
        // 🚨 #6989 — A REPORT IS WHERE THE PLATFORM FILES ONE, NOT ANY NODE OF THE TYPE. Inventories are
        // read mesh-wide, and `Hosting/ModuleInventory` is a type anyone may create in a partition they
        // own. A node outside the two filing locations is not a report: it is no evidence of what an
        // instance runs, and above all it must never SATISFY a record — a fresh, complete body written
        // by hand in a user's partition would complete the pass and authorise deleting what that
        // instance actually runs. Measured on the registry: a leftover probe under a user's partition
        // (`…/OpsProbe/Modules/memex`) was matched as the `memex` record's sink report and refused every
        // pass ("adopted artifact inventory is incomplete or legacy"), standing in for the report that
        // never arrived. Only the store's filing location carries the authenticated
        // `registeredInstance`; only the sink's filing location is matched by the deployment it names.
        var storeLocation = $"{scope.OperationalSpace}/{DeploymentReportService.StoreReportsSegment}";
        var sinkLocation = $"{scope.OperationalSpace}/Modules";
        var notReports = inventories
            .Where(node => !IsIn(node, storeLocation) && !IsIn(node, sinkLocation))
            .ToImmutableList();
        if (!notReports.IsEmpty)
            logger?.LogInformation(
                "PrebuiltBundleRetention: {Count} {NodeType} node(s) outside {StoreLocation} and {SinkLocation} are not "
                + "reports and are not read: {Paths}",
                notReports.Count, DeploymentReportService.InventoryNodeType, storeLocation, sinkLocation,
                string.Join(", ", notReports.Select(node => node.Path)));
        var storeReports = inventories
            .Where(node => IsIn(node, storeLocation))
            .GroupBy(node => RegisteredInstanceOf(node, options) ?? node.Id, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(group => group.Key, group => group.ToImmutableList(),
                StringComparer.OrdinalIgnoreCase);
        var sinkReports = inventories
            .Where(node => IsIn(node, sinkLocation))
            .GroupBy(node => SinkReporterOf(node, options), StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(group => group.Key, group => group.ToImmutableList(),
                StringComparer.OrdinalIgnoreCase);

        var references = ImmutableList.CreateBuilder<PinnedPlatformReference>();
        var refusals = ImmutableList.CreateBuilder<string>();
        var expected = 0;
        var retired = 0;
        var suspended = 0;
        var elsewhere = 0;
        var fresh = 0;

        // 🚨 A HOST THAT CANNOT NAME ITSELF CANNOT TELL WHO MOUNTS IT. Records that mount a registry
        // are a declaration about SOME store; without this host's own name none of them can be shown
        // to be about another one, and reading them all as "not mine" would authorise deleting what a
        // remote consumer pulls from here.
        if (storeHosts.IsEmpty
            && deployments.Any(r => RetirementOf(r, options) is null && !MountedHostsOf(r, options).IsEmpty))
            refusals.Add(
                $"this host cannot name the store it serves — {DeploymentReportService.DeploymentKey} names no "
                + "Deployment record with a `host` and PluginCatalog:HomeUrl is not set — so the records that "
                + "mount a plugin registry cannot be split into consumers of THIS store and consumers of another "
                + "one. A consumer set that cannot be read is not an empty one.");

        foreach (var record in deployments)
        {
            if (RetirementOf(record, options) is { } retirement)
            {
                retired++;
                logger?.LogInformation(
                    "PrebuiltBundleRetention: {Deployment} is excluded from the expected consumer set — {Reason}",
                    record.Path, retirement);
                continue;
            }
            var pin = DeploymentPinOf(record, options);
            if (pin is not null)
                references.Add(pin);

            // A record's reports: the store-filed ones authenticated as the registry identity it
            // DECLARES (`registryInstanceId`, else its id), and the sink-filed ones naming its id.
            var registryKey = RegistryIdentityOf(record, options);
            var fromStore = storeReports.TryGetValue(registryKey, out var stored) ? stored : [];
            var fromSink = sinkReports.TryGetValue(record.Id, out var sunk) ? sunk : [];
            storeReports = storeReports.Remove(registryKey);
            sinkReports = sinkReports.Remove(record.Id);
            var reports = fromStore.AddRange(fromSink);

            // 🚨 #6989 — HOLDING A RECORD IS NOT CONSUMING THIS STORE. A host can hold a synced copy of
            // the whole Deployments folder; what makes a record an EXPECTED consumer of THIS store is
            // the record's own declaration that its instance pulls from here — a `pluginRepos` mount
            // naming this host. Counting every held record made every host that is not the report sink
            // refuse on every tick, for ever: the records were there and their reports went to the
            // sink. The host itself is not its own remote consumer — what it runs is protected by its
            // live identity and its adoption stamps — and the reports of an instance that declares it
            // pulls from elsewhere describe another store's consumer.
            var isSelf = string.Equals(record.Id, scope.SelfDeployment, StringComparison.OrdinalIgnoreCase);
            if (isSelf || !MountedHostsOf(record, options).Overlaps(storeHosts))
            {
                // 🚨 BUT A REPORT THIS STORE RECEIVED STILL PROTECTS (#6993 review). It arrived
                // authenticated, so its instance pulled from here recently — a record may have moved
                // to another registry before the instance applied it, and the builds it is still
                // being served from here must not be collected. It protects; it does not count the
                // record as expected, so it cannot make the pass complete. Sink-filed reports of such
                // a record describe another store's consumer and are not read.
                foreach (var report in fromStore)
                {
                    try
                    {
                        references.AddRange(ReportedBuildsOf(report, options));
                    }
                    catch (InvalidOperationException exception)
                    {
                        refusals.Add($"{record.Path}: {exception.Message}");
                    }
                }
                elsewhere++;
                logger?.LogInformation(
                    "PrebuiltBundleRetention: {Deployment} is not an expected consumer of this store — {Reason}",
                    record.Path, isSelf
                        ? "it is this host"
                        : $"it mounts no registry served as {string.Join(" / ", storeHosts)}");
                continue;
            }
            expected++;

            if (reports.IsEmpty)
            {
                refusals.Add(
                    $"{record.Path}: no {DeploymentReportService.InventoryNodeType} report has ever arrived, so what this "
                    + "instance runs is unknown. An instance that has not reported is not an instance that consumes "
                    + "nothing — retention must not delete. Either the reporter is not configured there "
                    + "(Hosting:Deployment / Hosting:ReportTo / Hosting:ModuleReportSecret), it reaches this store "
                    + "under a registry instance id the record does not declare (registryInstanceId), or the "
                    + "record is for an installation that is gone and should say so (retired / retiredAt).");
                continue;
            }

            var failed = false;
            foreach (var report in reports)
            {
                try
                {
                    references.AddRange(ReportedBuildsOf(report, options));
                }
                catch (InvalidOperationException exception)
                {
                    refusals.Add($"{record.Path}: {exception.Message}");
                    failed = true;
                }
            }
            if (failed)
                continue;

            // 🚨 #6989 — A SUSPENDED CONSUMER'S LAST REPORT IS WHAT IT RESUMES ONTO. The record
            // declares the suspension; a suspended instance does not report, so its report goes stale
            // by construction and the age budget says nothing about it. Its references were added
            // above and stay pinned; only the freshness verdict is excused. Not counted as retired:
            // its pins are kept, never dropped.
            if (IsSuspended(record, options))
            {
                suspended++;
                logger?.LogInformation(
                    "PrebuiltBundleRetention: {Deployment} is suspended — its last report is not held to the "
                    + "{Budget} freshness budget, and the builds it names stay protected so the instance can resume",
                    record.Path, freshnessBudget);
                continue;
            }

            var newest = reports
                .Select(report => (report, age: FreshnessOf(report, options, now)))
                .OrderBy(entry => entry.age ?? TimeSpan.MaxValue)
                .First();
            if (newest.age is null)
            {
                refusals.Add(
                    $"{record.Path}: its report {newest.report.Path} carries no usable {nameof(DeploymentReport.SampledAt)} — "
                    + "absent, unparseable, or in the FUTURE — so it cannot be shown to describe the instance as it is "
                    + "now. A report of unknown age is not a fresh one.");
                continue;
            }
            if (newest.age > freshnessBudget)
            {
                refusals.Add(
                    $"{record.Path}: its newest report {newest.report.Path} was sampled {newest.age.Value.TotalHours:F1} h ago, past the "
                    + $"{freshnessBudget.TotalHours:F0} h budget. The instance may have rolled since; the build it reports is "
                    + "protected, and the one it is actually running is not known to anybody. Retention must not delete.");
                continue;
            }
            fresh++;
        }

        // 🚨 REPORTS WITHOUT RECORDS IS THE ONE ZERO THAT CANNOT BE HONEST. This source is
        // registered on EVERY portal, not only the control instance, so a host with no
        // Hosting/Deployment records is the ordinary case and a true measured zero — an instance
        // that is nobody's fleet has nobody to account for. But an instance OTHERS report to is a
        // control instance by construction, and a control instance whose Deployment records have
        // gone missing would otherwise read as "zero expected consumers, inventory complete" over
        // a fleet that is very much running.
        // Every report no record claimed, one bucket per reporting instance.
        var unclaimed = storeReports.Values.Concat(sinkReports.Values).ToImmutableList();
        if (deployments.Count == 0 && unclaimed.Count > 0)
            refusals.Add(
                $"{unclaimed.Count} instance(s) file {DeploymentReportService.InventoryNodeType} reports here, which makes "
                + $"this a control instance, and yet it holds NO {DeploymentNodeType} record to account for — so the "
                + "expected-consumer set is empty for a fleet that is reporting. That is a missing denominator, not a "
                + "fleet of zero.");
        // 🚨 AND THE SAME ZERO WITH NOTHING REPORTED EITHER, which the clause above cannot see.
        // `Records` reads an eventually-consistent index, so a control instance whose Deployment and
        // inventory indexes have both not caught up answers exactly as an ordinary portal does —
        // empty — and an empty expected set authorises collecting every remote consumer's artifacts.
        // Configuration is authoritative where the query is not: a host that NAMES itself and posts
        // its report NOWHERE owns a fleet, and a fleet of zero is then a read that has not landed.
        else if (deployments.Count == 0 && isControlInstance)
            refusals.Add(
                $"this host is a control instance ({DeploymentReportService.DeploymentKey} is set and "
                + $"{DeploymentReportService.ReportToKey} is not) and the {DeploymentNodeType} query returned NOTHING. "
                + "An eventually-consistent index that has not caught up answers exactly as a host with no fleet does, "
                + "and only one of those two readings may authorise deleting a remote consumer's artifacts.");

        // Whatever is left reported for no record: an unexpected consumer is still a consumer.
        foreach (var orphan in unclaimed.SelectMany(bucket => bucket))
        {
            try
            {
                references.AddRange(ReportedBuildsOf(orphan, options));
            }
            catch (InvalidOperationException exception)
            {
                refusals.Add($"{orphan.Path}: {exception.Message}");
            }
        }

        logger?.LogInformation(
            "PrebuiltBundleRetention consumer inventory of the store served as {Store}: {Expected} expected "
            + "instance(s) ({Retired} retired, {Suspended} suspended with their last report pinned, {Elsewhere} not "
            + "mounting this store, {Orphan} reporting without a record), {Fresh} with a report no older than {Budget}, "
            + "{References} protected reference(s){Verdict}",
            storeHosts.IsEmpty ? "(unnamed)" : string.Join(" / ", storeHosts),
            expected, retired, suspended, elsewhere, unclaimed.Count, fresh, freshnessBudget, references.Count,
            refusals.Count == 0 ? " — COMPLETE" : $" — INCOMPLETE, {refusals.Count} refusal(s)");

        if (refusals.Count > 0)
            // 🚨 THE COUNTS ARE SEPARATE BECAUSE THEY ANSWER DIFFERENT QUESTIONS. "N of M expected"
            // reads as a fraction of the expected set and silently lies when the refusal was an
            // ORPHAN report or the missing denominator itself — "1 of 0 expected instance(s)" is
            // the shape of an operator message nobody can act on.
            throw new InvalidOperationException(
                $"the fleet consumer inventory is INCOMPLETE — {refusals.Count} refusal(s) over {expected} expected "
                + $"instance(s) ({retired} retired, {suspended} suspended with their last report pinned, {elsewhere} "
                + $"not mounting this store, {unclaimed.Count} reporting "
                + "without a record), so no artifact can "
                + "be shown to be unreferenced and nothing may be collected:"
                + string.Concat(refusals.Select(r => Environment.NewLine + "  • " + r)));

        return references.ToImmutable();
    }

    /// <summary>The Deployment record's registry instance id field.</summary>
    public const string RegistryInstanceIdField = "registryInstanceId";

    /// <summary>The Deployment record's plugin-registry mounts field.</summary>
    public const string PluginReposField = "pluginRepos";

    /// <summary>The registry instance id a STORE authenticated a report's sender as
    /// (<see cref="DeploymentReport.RegisteredInstance"/>, stamped by the store, never by the sender),
    /// or null on a report the trusted report sink filed.</summary>
    private static string? RegisteredInstanceOf(MeshNode node, JsonSerializerOptions options) =>
        Field(node, nameof(DeploymentReport.RegisteredInstance), options) is { } registered
        && !string.IsNullOrWhiteSpace(registered)
            ? registered.Trim()
            : null;

    /// <summary>Whether <paramref name="node"/> is filed directly under <paramref name="location"/>.</summary>
    private static bool IsIn(MeshNode node, string location) =>
        string.Equals(node.Namespace, location, StringComparison.OrdinalIgnoreCase);

    /// <summary>The deployment a sink-filed report names, else the node's own id.</summary>
    private static string SinkReporterOf(MeshNode node, JsonSerializerOptions options) =>
        Field(node, nameof(DeploymentReport.Deployment), options) is { } id && !string.IsNullOrWhiteSpace(id)
            ? id
            : node.Id;

    /// <summary>The ONE registry identity a record's store-filed reports may carry: its declared
    /// <c>registryInstanceId</c>, else its record id.</summary>
    private static string RegistryIdentityOf(MeshNode record, JsonSerializerOptions options) =>
        Field(record, RegistryInstanceIdField, options) is { } registered && !string.IsNullOrWhiteSpace(registered)
            ? registered.Trim()
            : record.Id;

    /// <summary>
    /// The hosts of every plugin registry this record DECLARES its instance consumes — its
    /// <c>pluginRepos</c> mounts. A mount marked <c>isRegistrySource</c> is a repository a registry
    /// serves FROM (a git source), not a registry the instance pulls from, and is not one.
    /// </summary>
    /// <param name="record">A <c>Hosting/Deployment</c> record.</param>
    /// <param name="options">The hub's serializer options.</param>
    public static ImmutableHashSet<string> MountedHostsOf(MeshNode record, JsonSerializerOptions options)
    {
        if (record.Content is null)
            return [];
        var content = record.Content is JsonElement e ? e : JsonSerializer.SerializeToElement(record.Content, options);
        if (content.ValueKind != JsonValueKind.Object)
            return [];
        var mounts = content.EnumerateObject()
            .FirstOrDefault(p => string.Equals(p.Name, PluginReposField, StringComparison.OrdinalIgnoreCase)).Value;
        if (mounts.ValueKind != JsonValueKind.Array)
            return [];
        var hosts = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mount in mounts.EnumerateArray())
        {
            if (mount.ValueKind != JsonValueKind.Object)
                continue;
            string? url = null;
            var isSource = false;
            foreach (var property in mount.EnumerateObject())
            {
                if (string.Equals(property.Name, "url", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                    url = property.Value.GetString();
                else if (string.Equals(property.Name, "isRegistrySource", StringComparison.OrdinalIgnoreCase))
                    isSource = property.Value.ValueKind == JsonValueKind.True;
            }
            if (!isSource && HostOf(url) is { } host)
                hosts.Add(host);
        }
        return hosts.ToImmutable();
    }

    /// <summary>The lower-case host of an absolute http(s) URL or of a bare host name; null when there is none.</summary>
    internal static string? HostOf(string? urlOrHost)
    {
        if (string.IsNullOrWhiteSpace(urlOrHost))
            return null;
        var value = urlOrHost.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            return uri.Host.ToLowerInvariant();
        return Uri.CheckHostName(value) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6
            ? value.ToLowerInvariant()
            : null;
    }

    /// <summary>Why this record is not an expected consumer, or null when it is one.</summary>
    private static string? RetirementOf(MeshNode record, JsonSerializerOptions options)
    {
        if (record.State != MeshNodeState.Active)
            return $"its record is {record.State}";
        var retiredAt = Field(record, "retiredAt", options);
        if (!string.IsNullOrWhiteSpace(retiredAt))
            return $"retiredAt = {retiredAt}";
        return Flag(record, "retired", options) is true ? "retired = true" : null;
    }

    /// <summary>
    /// Whether the record DECLARES its instance suspended (<see cref="StatusField"/> =
    /// <see cref="SuspendedStatus"/>, case-insensitive, trimmed). Any other status, including one
    /// this assembly has never heard of, is not a suspension and keeps the full freshness verdict.
    /// </summary>
    /// <param name="record">A <c>Hosting/Deployment</c> record.</param>
    /// <param name="options">The hub's serializer options.</param>
    public static bool IsSuspended(MeshNode record, JsonSerializerOptions options) =>
        string.Equals(Field(record, StatusField, options)?.Trim(), SuspendedStatus, StringComparison.OrdinalIgnoreCase);

    /// <summary>How long ago the report was sampled, or null when that cannot be read at all.</summary>
    private static TimeSpan? FreshnessOf(MeshNode report, JsonSerializerOptions options, DateTimeOffset now)
    {
        var sampledAt = Field(report, nameof(DeploymentReport.SampledAt), options);
        if (!DateTimeOffset.TryParse(sampledAt, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var stamp))
            return null;
        var age = now - stamp;
        // 🚨 A STAMP IN THE FUTURE IS AN UNKNOWN AGE, NOT A FRESH ONE. A negative age passes
        // `age > budget` trivially, so a skewed clock or a malformed producer would make every
        // report it files permanently fresh — protecting one identity for ever while the
        // installation moves on, which is precisely the state this budget exists to detect.
        return age < TimeSpan.Zero ? null : age;
    }

    /// <summary>One boolean field of a node's content, read the same way <see cref="Field"/> reads a string.</summary>
    internal static bool? Flag(MeshNode node, string name, JsonSerializerOptions options)
    {
        if (node.Content is null)
            return null;
        var element = node.Content is JsonElement e ? e : JsonSerializer.SerializeToElement(node.Content, options);
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
        return null;
    }

    private static IObservable<IReadOnlyList<MeshNode>> Records(IMeshService meshService, string nodeType) =>
        meshService
            .Query<MeshNode>(MeshQueryRequest.FromQuery(MeshWideQuery.OfType(nodeType)))
            .Take(1)
            .Timeout(EnumerationBudget)
            .Select(change => change.Items.Where(n => n.State == MeshNodeState.Active).ToList());

    /// <summary>A Deployment record's pin, or null when the record pins nothing.</summary>
    public static PinnedPlatformReference? DeploymentPinOf(MeshNode node, JsonSerializerOptions options)
    {
        var tag = Field(node, PinnedImageTagField, options);
        return string.IsNullOrWhiteSpace(tag)
            ? null
            : new PinnedPlatformReference($"Deployment {node.Path}", tag, null);
    }

    /// <summary>
    /// The running platform and every adopted build in a complete consumer inventory.
    /// Legacy, incomplete or malformed inventories abort retention instead of implying no consumers.
    /// </summary>
    public static ImmutableList<PinnedPlatformReference> ReportedBuildsOf(MeshNode node, JsonSerializerOptions options)
    {
        var content = node.Content is JsonElement element ? element : JsonSerializer.SerializeToElement(node.Content, options);
        JsonElement Find(string name) => content.ValueKind == JsonValueKind.Object
            ? content.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value
            : default;
        var complete = Find(nameof(DeploymentReport.AdoptedFrameworkInventoryComplete));
        var identities = Find(nameof(DeploymentReport.AdoptedFrameworkIdentities));
        if (complete.ValueKind != JsonValueKind.True || identities.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"instance report {node.Path}: adopted artifact inventory is incomplete or legacy; retention must not delete");
        var references = ImmutableList.CreateBuilder<PinnedPlatformReference>();
        var running = ReportedBuildOf(node, options)
            ?? throw new InvalidOperationException($"instance report {node.Path}: running build is missing; retention must not delete");
        references.Add(running);
        foreach (var identity in identities.EnumerateArray())
        {
            if (identity.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(identity.GetString()))
                throw new InvalidOperationException($"instance report {node.Path}: an adopted artifact identity is malformed; retention must not delete");
            references.Add(new PinnedPlatformReference($"adopted build reported by {node.Path}", null, identity.GetString()));
        }
        return references.ToImmutable();
    }

    /// <summary>An inventory record's reported build, or null when it reports neither a version nor an identity.</summary>
    public static PinnedPlatformReference? ReportedBuildOf(MeshNode node, JsonSerializerOptions options)
    {
        var version = Field(node, nameof(DeploymentReport.PlatformVersion), options);
        var identity = Field(node, nameof(DeploymentReport.FrameworkIdentity), options);
        return string.IsNullOrWhiteSpace(version) && string.IsNullOrWhiteSpace(identity)
            ? null
            : new PinnedPlatformReference($"instance report {node.Path}",
                string.IsNullOrWhiteSpace(version) ? null : version,
                string.IsNullOrWhiteSpace(identity) ? null : identity);
    }

    /// <summary>
    /// One string field of a node's content, whatever CLR shape the content arrived in — the
    /// record's type is a mesh NodeType this assembly does not carry, so the content is read as a
    /// JSON document and the field matched case-insensitively.
    /// </summary>
    internal static string? Field(MeshNode node, string name, JsonSerializerOptions options)
    {
        if (node.Content is null)
            return null;
        var element = node.Content is JsonElement e ? e : JsonSerializer.SerializeToElement(node.Content, options);
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in element.EnumerateObject())
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString();
        return null;
    }
}

/// <summary>
/// Whose consumers a host's prebuilt-bundle retention must account for: the store THIS host serves.
/// </summary>
/// <param name="SelfDeployment">The Deployment record id this host IS (<c>Hosting:Deployment</c>), or
/// null when it names none. That record's <c>host</c> is a name of the store, and the record itself is
/// never one of the store's remote consumers.</param>
/// <param name="StoreHosts">The public host names this host serves its registry under, as configuration
/// states them (<c>PluginCatalog:HomeUrl</c>), lower-case.</param>
/// <param name="IsControlInstance">Whether this host is the fleet's report sink
/// (<see cref="DeploymentPinnedReferences.IsControlInstance"/>).</param>
public sealed record ConsumerScope(
    string? SelfDeployment,
    ImmutableHashSet<string> StoreHosts,
    bool IsControlInstance)
{
    /// <summary>
    /// The partition reports are FILED in on this host (<c>Hosting:OperationalSpace</c>): the sink
    /// files under <c>{space}/Modules</c>, a store under <c>{space}/ConsumerReports</c>, and a
    /// <c>Hosting/ModuleInventory</c> node anywhere else is not a report (#6989). An init property,
    /// not a positional parameter, so the record's primary constructor keeps its shape.
    /// </summary>
    public string OperationalSpace { get; init; } = DeploymentReportService.DefaultOperationalSpace;

    /// <summary>A host that names no store and owns no fleet — an ordinary installation.</summary>
    public static ConsumerScope None { get; } = new(null, [], false);
}
