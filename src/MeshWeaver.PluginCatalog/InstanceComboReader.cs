using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Reads THIS instance's combo — every module it carries, with its source and its pinned ref —
/// folding the two shapes a module's coordinate is recorded in into one list.
///
/// <para><b>Why a reader is the first task.</b> The candidate-release gate verifies a candidate
/// image against the module versions <i>that instance</i> installed, and an earlier reading of the
/// problem said an instance could not state its combo. It can: the coordinate was never missing,
/// it was in two shapes.</para>
///
/// <list type="table">
///   <listheader><term>path</term><description>coordinate</description></listheader>
///   <item>
///     <term><c>{Space}/_GitSync</c> (<c>nodeType:GitHubSyncConfig</c>)</term>
///     <description><c>repositoryUrl</c> + <c>branch</c> + <c>subdirectory</c> +
///       <c>lastSyncCommitSha</c> — how MOST modules actually arrive.</description>
///   </item>
///   <item>
///     <term><c>Plugins/{id}</c> (<c>nodeType:Package</c>)</term>
///     <description><see cref="PackageManifest.ModuleVersion"/> — the installer's record.</description>
///   </item>
/// </list>
///
/// <para>🚨 <b>A reader that handled only the install records would report almost nothing on a real
/// portal and look like a healthy empty set</b> — the same false-confidence failure in a new place.
/// Measured on memex 2026-08-10: <b>42</b> sync configs, <b>ZERO</b> install records
/// (<c>Plugins/*</c> holds only <c>_Policy</c>). That single fact is why folding is the feature.</para>
///
/// <para><b><see cref="ModuleDiscovery"/> is enrichment, never the set.</b> It records what a
/// configured repo SHIPS — most of the module set, in one place — but it exists only where a source
/// set <c>AutoDiscover</c>, and <c>Admin/_Discovery</c> is <b>empty on memex</b>. Its
/// <see cref="ModuleDiscovery.GitRef"/> is also repo-wide and may be a branch name, which moves and
/// so is not a coordinate. It therefore contributes <see cref="InstanceCombo.NotCarried"/> (modules
/// the repo ships that this instance lacks) and each module's source name — never the module set
/// itself, which comes from what the instance demonstrably HAS.</para>
///
/// <para><b>Reads as SYSTEM, deliberately.</b> A partial combo is exactly the failure this type
/// exists to prevent, and neither the sync entries nor the install records are readable by an
/// ordinary principal. 🚨 A caller that exposes this over any user-facing surface must gate on
/// <c>hub.IsGlobalAdmin()</c> — the reader itself does not, because a gate running unattended has no
/// principal at all.</para>
///
/// <para>Instance-scoped singleton (its lifetime is the mesh's — <c>Doc/Architecture/NoStaticState</c>),
/// reactive end to end, no <c>async</c>/<c>await</c>. Cold: the queries run on Subscribe.</para>
///
/// <para>See <c>Doc/Architecture/CandidateReleaseProtocol</c>.</para>
/// </summary>
/// <param name="hub">The hub whose instance this reads.</param>
/// <param name="logger">Diagnostics.</param>
public sealed class InstanceComboReader(IMessageHub hub, ILogger<InstanceComboReader>? logger = null)
{
    /// <summary>How long a mesh-wide set query gets before it is reported as UNREADABLE. It is
    /// never reported as empty — "this instance carries nothing" and "we could not find out" are
    /// the two answers a gate must never conflate.</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The standing honesty note, present whenever any module's ref came from a sync entry or an
    /// install record — i.e. essentially always.
    ///
    /// <para>Gate diagnostics, deliberately NOT localized: the consumer is an unattended deploy
    /// gate reading a machine report, the same call <see cref="DiscoveredModule.Detail"/> makes. A
    /// UI that renders these localizes at the render site.</para>
    /// </summary>
    public const string ProvenanceCaveat =
        "Every ref reported here is PROVENANCE: it records what a module was last materialised "
        + "FROM, not that the live mesh is byte-identical to it. Content authored in the mesh after "
        + "a module's materializedAt has diverged from its ref, and that is normal — the mesh is "
        + "authored as well as synced.";

    /// <summary>Raised when no <see cref="ModuleDiscovery"/> record exists, so the reader can only
    /// see what this instance HAS and cannot tell whether a configured repo ships more.</summary>
    public const string NoDiscoveryCaveat =
        "No module-discovery record exists (Admin/_Discovery is empty — the case on memex), so "
        + "modules a configured repo ships but this instance does NOT carry are invisible to this "
        + "read. Turn on PluginCatalog:Sources:N:AutoDiscover to make that set visible.";

    /// <summary>
    /// This instance's combo. Cold — subscribe to run it.
    ///
    /// <para>Never faults on an unreadable source: a fault would tell a gate nothing about what it
    /// DID manage to read. An unreadable source instead sets <see cref="InstanceCombo.IsComplete"/>
    /// false and names itself in <see cref="InstanceCombo.Caveats"/>.</para>
    /// </summary>
    /// <returns>One emission carrying the instance's full combo, then completes.</returns>
    public IObservable<InstanceCombo> Read()
    {
        var readAt = DateTimeOffset.UtcNow;
        return Observable.Zip(
            // Every {Space}/_GitSync and {Space}/_GitSync/{sourceId} on the instance.
            QuerySet($"nodeType:{GitHubSyncService.ConfigNodeType}", "sync entries ({Space}/_GitSync)"),
            // Every install record the package installer wrote.
            QuerySet(
                $"path:{PackageInstaller.InstalledPartition} scope:children "
                + $"nodeType:{PackageInstaller.PackageNodeType}",
                $"install records ({PackageInstaller.InstalledPartition}/*)"),
            // Enrichment only — see the type remarks.
            QuerySet($"nodeType:{ModuleDiscovery.NodeType}", "module-discovery records"),
            (sync, installs, discovery) => Fold(readAt, sync, installs, discovery));
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Reading
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// One set query, as SYSTEM. A SET query is the sanctioned use of <c>Query</c> (listing,
    /// existence) — no single node's content is read through it, which is the CQRS rule this stays
    /// on the right side of.
    /// </summary>
    private IObservable<SourceRead> QuerySet(string query, string what)
    {
        var meshService = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var accessService = hub.ServiceProvider.GetRequiredService<AccessService>();
        return Observable.Using(
                () => accessService.ImpersonateAsSystem(),
                _ => meshService.Query<MeshNode>(MeshQueryRequest.FromQuery(query)))
            .Take(1)
            .Timeout(QueryTimeout)
            .Select(change => new SourceRead(change.Items.ToImmutableList(), null))
            .Catch((Exception exception) =>
            {
                logger?.LogWarning(exception,
                    "[InstanceCombo] reading {What} ('{Query}') failed — the combo is reported as "
                    + "INCOMPLETE rather than short.", what, query);
                return Observable.Return(new SourceRead(
                    [],
                    $"Could not read {what}: {exception.Message}. This combo is INCOMPLETE — "
                    + "modules recorded there are missing from it."));
            });
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  Folding the two shapes into one list
    // ══════════════════════════════════════════════════════════════════════════

    private InstanceCombo Fold(
        DateTimeOffset readAt, SourceRead sync, SourceRead installs, SourceRead discovery)
    {
        var caveats = ImmutableList.CreateBuilder<string>();
        foreach (var failure in new[] { sync.Failure, installs.Failure, discovery.Failure })
            if (failure is not null)
                caveats.Add(failure);

        var syncByModule = ReadSyncEntries(sync.Items);
        var packagesByModule = ReadInstallRecords(installs.Items);
        var records = ReadDiscoveryRecords(discovery.Items);

        // The module SET is the union of what the instance demonstrably HAS. Discovery contributes
        // names and gaps, never membership — it is absent on real portals.
        var modules = syncByModule.Keys
            .Concat(packagesByModule.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Select(id =>
            {
                syncByModule.TryGetValue(id, out var gitSync);
                packagesByModule.TryGetValue(id, out var package);
                return new ModuleCoordinate
                {
                    ModuleId = id,
                    Name = NameOf(id, package, records),
                    GitSync = gitSync,
                    Package = package,
                };
            })
            .ToImmutableList();

        if (records.Count == 0)
            caveats.Add(NoDiscoveryCaveat);
        if (modules.Any(m => m.ProvenanceKind is not ProvenanceKind.None))
            caveats.Add(ProvenanceCaveat);
        foreach (var moving in modules.Where(m => m.Fidelity != RefFidelity.Exact))
            caveats.Add(
                $"'{moving.ModuleId}' is pinned only to {Describe(moving)} — two runs of this combo "
                + "can resolve it to different content, so a green result does not pin a tree.");

        return new InstanceCombo
        {
            ReadAt = readAt,
            Modules = modules,
            NotCarried = Gaps(records, syncByModule, packagesByModule),
            Caveats = caveats.ToImmutable(),
            IsComplete = sync.Failure is null && installs.Failure is null,
        };
    }

    /// <summary>
    /// The sync entries, keyed by the PARTITION they belong to. A Space's primary entry lives at
    /// <c>{Space}/_GitSync</c> and each additional source at <c>{Space}/_GitSync/{sourceId}</c>;
    /// both resolve to the owning partition through their namespace's first segment. The primary
    /// wins when a Space has several — it is the one the module itself is imported through.
    /// </summary>
    private ImmutableDictionary<string, GitSyncCoordinate> ReadSyncEntries(
        IReadOnlyList<MeshNode> nodes) =>
        nodes
            .Select(node => (
                Module: AccessAssignmentGuard.PartitionOf(node.Namespace ?? ""),
                Node: node,
                Config: node.ContentAs<GitHubSyncConfig>(hub.JsonSerializerOptions, logger)))
            .Where(x => x.Module.Length > 0 && x.Config is not null)
            .GroupBy(x => x.Module, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(
                g => g.Key,
                g =>
                {
                    var entries = g.ToImmutableList();
                    // The PRIMARY entry ({Space}/_GitSync) is the one the module itself is imported
                    // through; an additional source ({Space}/_GitSync/{sourceId}) mirrors a subtree.
                    // Index rather than FirstOrDefault: these are value tuples, whose default is a
                    // silently-empty entry rather than a null the ?? could catch.
                    var index = entries.FindIndex(
                        x => string.Equals(x.Node.Id, GitHubSyncService.ConfigId,
                            StringComparison.OrdinalIgnoreCase));
                    var primary = entries[index >= 0 ? index : 0];
                    return new GitSyncCoordinate
                    {
                        ConfigPath = primary.Node.Path,
                        RepositoryUrl = primary.Config!.RepositoryUrl,
                        Branch = primary.Config.Branch,
                        Subdirectory = primary.Config.Subdirectory,
                        LastSyncCommitSha = primary.Config.LastSyncCommitSha,
                        LastSyncedAt = primary.Config.LastSyncedAt,
                    };
                },
                StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The install records, keyed by the partition each installed INTO — which is what a sync entry
    /// keys on too, and so what makes folding the two shapes onto one module possible. The record's
    /// own id is kept as <see cref="PackageCoordinate.PackageId"/>: it can differ from the
    /// partition, and it is what the installer's APIs take.
    /// </summary>
    private ImmutableDictionary<string, PackageCoordinate> ReadInstallRecords(
        IReadOnlyList<MeshNode> nodes) =>
        nodes
            .Select(node => (
                Node: node,
                Manifest: node.ContentAs<PackageManifest>(hub.JsonSerializerOptions, logger)))
            .Where(x => x.Manifest is not null)
            .Select(x => (
                Module: string.IsNullOrWhiteSpace(x.Manifest!.TargetPartition)
                    ? (string.IsNullOrWhiteSpace(x.Manifest.Id) ? x.Node.Id : x.Manifest.Id)
                    : x.Manifest.TargetPartition!,
                x.Node,
                x.Manifest))
            .Where(x => x.Module.Length > 0)
            .GroupBy(x => x.Module, StringComparer.OrdinalIgnoreCase)
            .ToImmutableDictionary(
                g => g.Key,
                g =>
                {
                    var (_, node, manifest) = g.First();
                    return new PackageCoordinate
                    {
                        RecordPath = node.Path,
                        PackageId = string.IsNullOrWhiteSpace(manifest.Id) ? node.Id : manifest.Id,
                        Name = manifest.Name,
                        ModuleVersion = manifest.ModuleVersion,
                        Version = manifest.Version,
                        InstalledFromRef = manifest.InstalledFromRef,
                        InstalledAtUtc = manifest.InstalledAtUtc,
                        SourceName = manifest.Source,
                    };
                },
                StringComparer.OrdinalIgnoreCase);

    private ImmutableList<ModuleDiscovery> ReadDiscoveryRecords(IReadOnlyList<MeshNode> nodes) =>
        nodes
            .Select(node => node.ContentAs<ModuleDiscovery>(hub.JsonSerializerOptions, logger))
            .Where(record => record is not null)
            .Select(record => record!)
            .ToImmutableList();

    /// <summary>
    /// What a configured repo ships that this instance does NOT carry — the honest counterpart to
    /// the module list, and the only thing that lets a caller tell "the combo is the whole set" from
    /// "the combo is what happens to be here". Empty (with a caveat) when no scan has ever run.
    /// </summary>
    private static ImmutableList<ModuleGap> Gaps(
        ImmutableList<ModuleDiscovery> records,
        ImmutableDictionary<string, GitSyncCoordinate> synced,
        ImmutableDictionary<string, PackageCoordinate> installed) =>
        records
            .SelectMany(record => record.Modules.Select(module => (record, module)))
            .Where(x => x.module.Status is not (ModuleDiscoveryStatus.Synced
                or ModuleDiscoveryStatus.Installed))
            // A scan can be older than the provisioning it reported — never contradict what the
            // instance demonstrably carries right now.
            .Where(x => !synced.ContainsKey(x.module.Id) && !installed.ContainsKey(x.module.Id))
            .GroupBy(x => x.module.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var (record, module) = g.First();
                return new ModuleGap
                {
                    ModuleId = module.Id,
                    Name = module.Name,
                    RepositoryUrl = record.RepositoryUrl,
                    Status = module.Status,
                    Detail = module.Detail,
                };
            })
            .ToImmutableList();

    /// <summary>The module's display name: the install record's own, else whatever the last
    /// discovery scan recorded for it, else none. A name is presentation — never an identity — so a
    /// missing one is a null, never a fabricated fallback.</summary>
    private static string? NameOf(
        string moduleId, PackageCoordinate? package, ImmutableList<ModuleDiscovery> records) =>
        string.IsNullOrWhiteSpace(package?.Name)
            ? records
                .SelectMany(record => record.Modules)
                .FirstOrDefault(m => string.Equals(m.Id, moduleId, StringComparison.OrdinalIgnoreCase))
                ?.Name
            : package!.Name;

    private static string Describe(ModuleCoordinate module) => module.Fidelity switch
    {
        RefFidelity.Moving => $"a moving ref ('{module.ProvenanceRef}')",
        _ => "nothing — neither a sync entry nor an install record names a ref for it",
    };

    /// <summary>One source query's outcome: what it returned, or why it could not be read.
    /// A failure is NEVER folded into an empty result.</summary>
    private sealed record SourceRead(ImmutableList<MeshNode> Items, string? Failure);
}
