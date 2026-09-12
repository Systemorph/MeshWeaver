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
/// <c>Hosting/Deployment</c> records as the DENOMINATOR — every non-retired record is an expected
/// consumer — and refuses the whole pass when one of them has no report, an unreadable report or a
/// report older than <see cref="StaleAfter"/>, naming each. An incomplete inventory is a refusal,
/// never a shorter reference list. #3438/#3858.</para>
/// </summary>
public static class DeploymentPinnedReferences
{
    /// <summary>The Deployment record's node type (a mesh NodeType shipped by the Hosting plugin).</summary>
    public const string DeploymentNodeType = "Hosting/Deployment";

    /// <summary>The Deployment record's pinned image tag field.</summary>
    public const string PinnedImageTagField = "pinnedImageTag";

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
        return access.RunAsSystem(() =>
            Records(meshService, DeploymentNodeType)
                .Zip(Records(meshService, DeploymentReportService.InventoryNodeType),
                    (deployments, inventories) => Resolve(
                        deployments, inventories, hub.JsonSerializerOptions, DateTimeOffset.UtcNow, StaleAfter,
                        IsControlInstance(hub.ServiceProvider.GetService<IConfiguration>()), logger)));
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
    /// <c>Hosting/Deployment</c> record that is not explicitly retired is an EXPECTED consumer, and
    /// an expected consumer with no report, an unreadable report or a report older than
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
    public static ImmutableList<PinnedPlatformReference> Resolve(
        IReadOnlyList<MeshNode> deployments,
        IReadOnlyList<MeshNode> inventories,
        JsonSerializerOptions options,
        DateTimeOffset now,
        TimeSpan freshnessBudget,
        bool isControlInstance,
        ILogger? logger = null)
    {
        // 🚨 AN INSTANCE MAY HAVE MORE THAN ONE RECORD HERE, and which one answers matters. A
        // control instance files its own report locally while remote ones arrive through the inbox,
        // so two nodes can describe one deployment. Every one of them contributes its references —
        // an extra consumer is a consumer — but the FRESHNESS verdict is taken from the newest,
        // because a stale duplicate beside a current report is not a stale instance.
        var unclaimed = inventories
            .GroupBy(node => Field(node, nameof(DeploymentReport.Deployment), options) is { } id
                    && !string.IsNullOrWhiteSpace(id) ? id : node.Id,
                StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(group => group.Key, group => group.ToImmutableList(),
                StringComparer.OrdinalIgnoreCase);

        var references = ImmutableList.CreateBuilder<PinnedPlatformReference>();
        var refusals = ImmutableList.CreateBuilder<string>();
        var expected = 0;
        var retired = 0;
        var fresh = 0;

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
            expected++;
            var pin = DeploymentPinOf(record, options);
            if (pin is not null)
                references.Add(pin);

            if (!unclaimed.TryGetValue(record.Id, out var reports))
            {
                refusals.Add(
                    $"{record.Path}: no {DeploymentReportService.InventoryNodeType} report has ever arrived, so what this "
                    + "instance runs is unknown. An instance that has not reported is not an instance that consumes "
                    + "nothing — retention must not delete. Either the reporter is not configured there "
                    + "(Hosting:Deployment / Hosting:ReportTo / Hosting:ModuleReportSecret), or the record is for an "
                    + "installation that is gone and should say so (retired / retiredAt).");
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
            unclaimed = unclaimed.Remove(record.Id);
            if (failed)
                continue;
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
        if (expected == 0 && retired == 0 && unclaimed.Count > 0)
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
        else if (expected == 0 && retired == 0 && isControlInstance)
            refusals.Add(
                $"this host is a control instance ({DeploymentReportService.DeploymentKey} is set and "
                + $"{DeploymentReportService.ReportToKey} is not) and the {DeploymentNodeType} query returned NOTHING. "
                + "An eventually-consistent index that has not caught up answers exactly as a host with no fleet does, "
                + "and only one of those two readings may authorise deleting a remote consumer's artifacts.");

        // Whatever is left reported for no record: an unexpected consumer is still a consumer.
        foreach (var orphan in unclaimed.Values.SelectMany(bucket => bucket))
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
            "PrebuiltBundleRetention consumer inventory: {Expected} expected instance(s) ({Retired} retired, "
            + "{Orphan} reporting without a record), {Fresh} with a report no older than {Budget}, "
            + "{References} protected reference(s){Verdict}",
            expected, retired, unclaimed.Count, fresh, freshnessBudget, references.Count,
            refusals.Count == 0 ? " — COMPLETE" : $" — INCOMPLETE, {refusals.Count} refusal(s)");

        if (refusals.Count > 0)
            // 🚨 THE COUNTS ARE SEPARATE BECAUSE THEY ANSWER DIFFERENT QUESTIONS. "N of M expected"
            // reads as a fraction of the expected set and silently lies when the refusal was an
            // ORPHAN report or the missing denominator itself — "1 of 0 expected instance(s)" is
            // the shape of an operator message nobody can act on.
            throw new InvalidOperationException(
                $"the fleet consumer inventory is INCOMPLETE — {refusals.Count} refusal(s) over {expected} expected "
                + $"instance(s) ({retired} retired, {unclaimed.Count} reporting without a record), so no artifact can "
                + "be shown to be unreferenced and nothing may be collected:"
                + string.Concat(refusals.Select(r => Environment.NewLine + "  • " + r)));

        return references.ToImmutable();
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
