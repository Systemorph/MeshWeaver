using System.Collections.Immutable;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph.ControlLane;

/// <summary>One table of a space, and how many rows it holds.</summary>
/// <param name="Table">The table, as the space's own partition definition resolves each row's path.</param>
/// <param name="Rows">How many rows were read in it.</param>
public sealed record SpaceTableCount(string Table, int Rows);

/// <summary>
/// What a space holds, read AS SYSTEM by one inventory: its root, rows per table, grants, GitSync
/// configuration, NodeTypes and the addresses OUTSIDE it their dependency networks reach, its
/// <c>Admin/Partition</c> record and its backing store. <see cref="Unread"/> names every reading
/// that was a floor rather than an answer — a non-empty list is never acted on.
/// </summary>
public sealed record SpaceDeletionInventory
{
    /// <summary>The space (partition root id).</summary>
    public string Space { get; init; } = "";

    /// <summary>The backing-store schema.</summary>
    public string Schema { get; init; } = "";

    /// <summary>The root node, or null for a rootless (stranded) space.</summary>
    public MeshNode? Root { get; init; }

    /// <summary>Rows per table.</summary>
    public ImmutableList<SpaceTableCount> Tables { get; init; } = [];

    /// <summary>The access grants anywhere in the space.</summary>
    public ImmutableList<string> Grants { get; init; } = [];

    /// <summary>The GitSync configuration nodes anywhere in the space.</summary>
    public ImmutableList<string> GitSync { get; init; } = [];

    /// <summary>
    /// How many rows the per-node GitSync removal covers — every row at or under a GitSync path. It is
    /// the ONE per-node leg of a space deletion, bounded at plan time by
    /// <see cref="SpaceDeletion.PerNodeDeleteBound"/>.
    /// </summary>
    public int GitSyncRows { get; init; }

    /// <summary>The NodeType definitions the space holds.</summary>
    public ImmutableList<string> NodeTypes { get; init; } = [];

    /// <summary>The minimal set of content paths whose recursive deletes cover every main row.</summary>
    public ImmutableList<string> ContentRoots { get; init; } = [];

    /// <summary>Addresses OUTSIDE the space that its NodeTypes' dependency networks reach.</summary>
    public ImmutableList<string> Dependents { get; init; } = [];

    /// <summary>Whether <c>Admin/Partition/{space}</c> exists.</summary>
    public bool RecordExists { get; init; }

    /// <summary>Whether the backing store exists: true/false from any provider that can tell, null when none can.</summary>
    public bool? StoreExists { get; init; }

    /// <summary>Every reading that was not an answer, one sentence each.</summary>
    public ImmutableList<string> Unread { get; init; } = [];

    /// <summary>All rows in all tables.</summary>
    public int TotalRows => Tables.Sum(t => t.Rows);

    /// <summary>True when nothing of the space is left that a deletion removes. Pure.</summary>
    public bool IsGone => TotalRows == 0 && Grants.Count == 0 && GitSync.Count == 0 && !RecordExists && StoreExists != true;
}

/// <summary>A point read from the owning node stream, or a named reason it could not be read.</summary>
internal sealed record AuthoritativeNodeRead(string Path, MeshNode? Node, string? Error);

/// <summary>
/// THE engine of a governed space deletion — the break-glass removal of a space no user identity may
/// delete, AS SYSTEM, through the framework's own deletes and the platform's partition teardown,
/// verified afterwards. It lives in core so that the in-process <c>DeleteSpace</c> instance action
/// (MeshWeaver.Plugins <c>Hosting/DeleteSpaceAction</c>) and the control lane's remote execution
/// (<see cref="DeleteSpaceOperation"/>) run the SAME reads, the SAME plan and the SAME deletes.
///
/// <para>🚨 SYSTEM CREDENTIALS FROM THE FIRST STEP, PRE-FLIGHT AT PARK (policy
/// <c>governed-action-preflight</c>). Every step runs as system, and <see cref="Preflight"/> — asked
/// before the plan is offered and again before the run touches anything — checks that each step is
/// feasible as system and within its mechanism's bound. The content, grants and store go in ONE
/// <see cref="PartitionTeardown.TearDownPartition"/>, whatever the size: a per-node
/// recursive delete of a 31,138-descendant space stalled in its pre-validation fan-out after its grant
/// and GitSync configuration were already gone, leaving it half removed.</para>
///
/// <para>🚨 IDEMPOTENT FROM THE TOP: every step acts on what a fresh inventory lists, and every
/// framework delete is idempotent; a failure stops where it stands and a re-request plans what is
/// left.</para>
/// </summary>
public static class SpaceDeletion
{
    /// <summary>The partitions a space deletion never touches, whatever their state. A constant lookup.</summary>
    public static readonly ImmutableHashSet<string> ProtectedPartitions = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase,
        "Admin", "Auth", "User", "Portal", "Kernel", "ApiToken", "system-security", "Anonymous",
        "_Access", "_Activity", "_UserActivity", "_Thread",
        "Hosting", "Hosting.Instance", "Deployments", "Ops", "Store", "Plugins", "Governance",
        "Doc", "Documentation", "Essentials", "Agent", "Skill", "Provider", "Providers", "Model", "AI",
        "Approvals", "Feedback", "Home");

    /// <summary>The NodeType of a user's home root.</summary>
    public const string UserNodeType = "User";

    /// <summary>The NodeType of a package-installed partition's root.</summary>
    public const string PackageRootNodeType = "Store/Plugin";

    /// <summary>The NodeType of a GitSync configuration node.</summary>
    public const string GitSyncNodeType = "GitHubSyncConfig";

    /// <summary>The configuration key naming the instance's operational space (Hosting's convention).</summary>
    public const string OperationalSpaceKey = "Hosting:OperationalSpace";

    /// <summary>Budget for one dependency-network derivation.</summary>
    public static readonly TimeSpan NetworkBudget = TimeSpan.FromMinutes(2);

    /// <summary>Budget for one per-node framework delete (a GitSync configuration node and its subtree).</summary>
    public static readonly TimeSpan DeleteBudget = TimeSpan.FromMinutes(2);

    /// <summary>Budget for the whole-partition teardown — one drop per provider, whatever the size.</summary>
    public static readonly TimeSpan TeardownBudget = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The most rows a PER-NODE recursive delete may cover in a space deletion. Such a delete
    /// pre-validates every descendant (a <c>ValidateDeleteRequest</c> fan-out, at most 64 in flight,
    /// the whole of it bounded at 25 s), and 1,740 posted requests did not settle in that bound. The
    /// content never takes that path; the bound applies to the GitSync removal alone, and a plan over it
    /// is refused before it is offered.
    /// </summary>
    public const int PerNodeDeleteBound = 200;

    /// <summary>
    /// The most rows the teardown may SWEEP on a backend with no per-partition store (no provider reports
    /// the store present — a single in-memory adapter), where the drop removes nothing and the rows go
    /// through the storage seam one by one, below the pipeline and without validation. That cost does
    /// grow with the space, so it is bounded and refused at park above the bound; a backend that reports
    /// its store is one drop and has no bound.
    /// </summary>
    public const int SweepBound = 250_000;

    /// <summary>How many index reads run at once.</summary>
    public const int ReadConcurrency = 4;

    // ───────────────────────────── pure rules ─────────────────────────────

    /// <summary>
    /// Why <paramref name="inventory"/> may not be deleted, or null — a protected or mirror
    /// partition, the operational space or the partition the caller runs from, a partition served
    /// by configuration, a user's home, a package's partition, an unread reading, or nothing left.
    /// Pure.
    /// </summary>
    public static string? PlanRefusal(
        SpaceDeletionInventory inventory, string? operationalSpace, string callerPath, bool servedByConfiguration)
    {
        var space = inventory.Space;
        if (ProtectedPartitions.Contains(space) || WellKnownPartitions.IsMirror(space))
            return $"'{space}' is a system or fleet partition — a space deletion never touches one "
                + "(protected: " + string.Join(", ", ProtectedPartitions.OrderBy(p => p, StringComparer.Ordinal)) + ")";
        if (string.Equals(FirstSegment(operationalSpace), space, StringComparison.OrdinalIgnoreCase)
            || string.Equals(FirstSegment(callerPath), space, StringComparison.OrdinalIgnoreCase))
            return $"'{space}' holds this instance's operational space or this action itself — a space deletion "
                + "never deletes the partition it runs from";
        if (servedByConfiguration)
            return $"'{space}' is served by configuration (a static partition) — there is no store to delete; "
                + "it is removed from the host's configuration";
        if (string.Equals(inventory.Root?.NodeType, UserNodeType, StringComparison.OrdinalIgnoreCase))
            return $"'{space}' is a user's home partition (its root is a {UserNodeType}) — a space deletion never "
                + "deletes a person's home; that is the user-removal procedure";
        if (string.Equals(inventory.Root?.NodeType, PackageRootNodeType, StringComparison.OrdinalIgnoreCase))
            return $"'{space}' is a package's partition (its root is a {PackageRootNodeType}) — uninstall the package "
                + "through the Store, which also retires its install ledger; a space deletion would be re-installed";
        if (inventory.Unread.Count > 0)
            return $"could NOT ESTABLISH what '{space}' holds — {inventory.Unread.Count} reading(s) were a floor, not "
                + $"an answer: {string.Join(" | ", inventory.Unread)}. Nothing is planned from a space nobody could "
                + "read; re-request once the index answers";
        if (inventory.IsGone)
            return $"there is nothing left of '{space}' to delete — no row in any table, no grant, no GitSync "
                + "configuration, no Admin/Partition record, and "
                + (inventory.StoreExists is null ? "no provider that can say whether a store exists" : "no store")
                + ". A no-op is never reported as a deletion";
        return null;
    }

    /// <summary>The minimal set of paths whose RECURSIVE deletes cover every path given. Pure, ordinal.</summary>
    public static ImmutableList<string> MinimalRoots(IEnumerable<string> paths)
    {
        var set = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToImmutableHashSet(StringComparer.Ordinal);
        return set.Where(p => !Ancestors(p).Any(set.Contains)).OrderBy(p => p, StringComparer.Ordinal).ToImmutableList();
    }

    /// <summary>Every proper ancestor path of <paramref name="path"/>, nearest last. Pure.</summary>
    public static IEnumerable<string> Ancestors(string path)
    {
        for (var i = path.IndexOf('/'); i > 0; i = path.IndexOf('/', i + 1))
            yield return path[..i];
    }

    /// <summary>Whether a row is a GitSync configuration node (<c>…/_GitSync</c> or below, or typed as one). Pure.</summary>
    public static bool IsGitSync(MeshNode row) =>
        string.Equals(row.NodeType, GitSyncNodeType, StringComparison.Ordinal)
        || row.Path.Split('/').Contains(AccessAssignmentGuard.SyncConfigId, StringComparer.Ordinal);

    /// <summary>The inventory folded from its readings. Pure.</summary>
    public static SpaceDeletionInventory Fold(
        string space, MeshNode? root, IEnumerable<MeshNode> rows, bool recordExists, bool? storeExists,
        IEnumerable<string> networkAddresses, IEnumerable<string> unread, PartitionDefinition? definition = null)
    {
        definition ??= DefinitionOf(space);
        var distinct = rows
            .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.Path))
            .GroupBy(r => r.Path, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();
        var tables = distinct
            .GroupBy(r => definition.ResolveTable(r.Path), StringComparer.Ordinal)
            .Select(g => new SpaceTableCount(g.Key, g.Count()))
            .OrderBy(t => t.Table, StringComparer.Ordinal)
            .ToImmutableList();
        var grants = distinct
            .Where(r => string.Equals(r.NodeType, AccessAssignmentGuard.AccessAssignmentNodeType, StringComparison.Ordinal))
            .Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal).ToImmutableList();
        var gitSync = distinct.Where(IsGitSync).Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal).ToImmutableList();
        var gitSyncRoots = MinimalRoots(gitSync);
        var gitSyncRows = distinct.Count(r => gitSyncRoots.Any(g =>
            string.Equals(r.Path, g, StringComparison.Ordinal) || r.Path.StartsWith(g + "/", StringComparison.Ordinal)));
        var nodeTypes = distinct
            .Where(r => string.Equals(r.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal))
            .Select(r => r.Path).OrderBy(p => p, StringComparer.Ordinal).ToImmutableList();
        var content = distinct
            .Where(r => !SatelliteTableMapping.IsSatellitePath(r.Path) && !IsCustomSatellite(definition, r.Path) && !IsGitSync(r))
            .Select(r => r.Path)
            .Concat(root is null ? [] : new[] { root.Path });
        var prefix = space + "/";
        var dependents = networkAddresses
            .Where(a => !string.Equals(a, space, StringComparison.Ordinal) && !a.StartsWith(prefix, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(a => a, StringComparer.Ordinal)
            .ToImmutableList();
        return new SpaceDeletionInventory
        {
            Space = space,
            Schema = definition.Schema ?? DefinitionOf(space).Schema ?? "",
            Root = root,
            Tables = tables,
            Grants = grants,
            GitSync = gitSync,
            GitSyncRows = gitSyncRows,
            NodeTypes = nodeTypes,
            ContentRoots = MinimalRoots(content),
            Dependents = dependents,
            RecordExists = recordExists,
            StoreExists = storeExists,
            Unread = unread.ToImmutableList(),
        };
    }

    /// <summary>
    /// Why the plan of <paramref name="inventory"/> may NOT be offered on SCALE grounds, or null. Pure.
    /// The content goes with the partition's store in one teardown whatever its size, so the only
    /// per-node leg is the GitSync removal; one over <see cref="PerNodeDeleteBound"/> is refused here
    /// rather than timing out mid-run.
    /// </summary>
    public static string? PreflightRefusal(SpaceDeletionInventory inventory) =>
        inventory.GitSyncRows > PerNodeDeleteBound
            ? $"PRE-FLIGHT: removing the GitSync configuration of '{inventory.Space}' is a per-node recursive delete of "
              + $"{inventory.GitSyncRows} row(s), over the bound of {PerNodeDeleteBound} such a delete can pre-validate — it "
              + "would stall mid-run and leave the space half removed. Refused at park; nothing was touched"
            : inventory.StoreExists != true && inventory.TotalRows > SweepBound
                ? $"PRE-FLIGHT: no provider reports a per-partition store for '{inventory.Space}', so the teardown would SWEEP "
                  + $"its {inventory.TotalRows} row(s) one by one — over the sweep bound of {SweepBound}. Refused at park; "
                  + "nothing was touched"
                : null;

    /// <summary>
    /// How the teardown removes the rows, as the plan states it: one drop when a provider reports the
    /// store, a bounded sweep below the pipeline when none does. Pure.
    /// </summary>
    public static string TeardownMechanism(SpaceDeletionInventory inventory) =>
        inventory.StoreExists == true
            ? $"ONE drop of the store '{inventory.Schema}' on every provider (Postgres: DROP SCHEMA … CASCADE — every table, "
              + "satellites included), whatever its size"
            : $"no provider reports a per-partition store, so the provider drop removes nothing and the "
              + $"{inventory.TotalRows.ToString(CultureInfo.InvariantCulture)} row(s) are swept below the pipeline through the "
              + $"storage seam, without per-node validation (bound {SweepBound.ToString(CultureInfo.InvariantCulture)})";

    /// <summary>
    /// The WHOLE pre-flight, asked AS SYSTEM (policy <c>governed-action-preflight</c>): what may never be
    /// deleted (<see cref="PlanRefusal"/>), the scale of the per-node leg (<see cref="PreflightRefusal"/>)
    /// and whether the platform's whole-partition teardown will run for this space under the identity
    /// every step runs with (<see cref="PartitionTeardown.Refusal"/>). Null when every step
    /// is feasible.
    /// </summary>
    /// <param name="hub">The hub the deletion runs on.</param>
    /// <param name="callerPath">The path the caller runs from (never deleted).</param>
    /// <param name="inventory">What the space holds, read as system.</param>
    public static string? Preflight(IMessageHub hub, string callerPath, SpaceDeletionInventory inventory)
    {
        var space = inventory.Space;
        if (PlanRefusal(inventory, OperationalSpace(hub), callerPath, ServedByConfiguration(hub, space)) is { } refused)
            return refused;
        if (PreflightRefusal(inventory) is { } tooLarge)
            return tooLarge;
        var access = hub.ServiceProvider.GetService<AccessService>();
        if (access is null)
            return "PRE-FLIGHT: this hub has no AccessService, so no step can run as system — nothing was touched";
        using (access.ImpersonateAsSystem())
            return PartitionTeardown.Refusal(hub, space) is { } teardown
                ? $"PRE-FLIGHT: the partition teardown of '{space}' would be refused as system — {teardown}. Nothing was touched"
                : null;
    }

    /// <summary>
    /// The steps of a space deletion — what the approver reads and the approval binds — and the notes
    /// beside them, the per-step pre-flight among them. 🚨 A plan never contains a listing: every set
    /// a step acts on is a TARGET, an anchored, scoped query with its count
    /// (<see cref="ControlLanePlanTarget"/>) — the space's own address, its NodeTypes, grants and
    /// GitSync nodes with counts, the whole subtree with NO count (its rows move while a stranded space
    /// waits, so they are shown in the notes and never bound), and the outside dependents as a count in
    /// the command only. The content, grants and store go in ONE partition teardown (policy
    /// <c>governed-action-preflight</c>). The same plan MeshWeaver.Plugins' in-process
    /// <c>DeleteSpaceRunner.PlanOf</c> shows. Pure.
    /// </summary>
    public static (ImmutableList<ControlLanePlanStep> Steps, ImmutableList<string> Notes) PlanSteps(SpaceDeletionInventory inventory)
    {
        var space = inventory.Space;
        var steps = ImmutableList.CreateBuilder<ControlLanePlanStep>();
        var nodeTypes = Target(TargetNodeTypes, NodeTypesQuery(space), inventory.NodeTypes.Count);
        steps.Add(new ControlLanePlanStep
        {
            Name = "Dispose the space's hubs",
            Command = "DisposeRequest as system, from the node-operation hub, to the space's own address and to every NodeType defined in it"
                + (inventory.NodeTypes.Count == 0 ? "" : " (a NodeType definition's dispose cascades to its dependency network)"),
            Targets = inventory.NodeTypes.Count == 0
                ? [Target(TargetSpaceRoot, SpaceTargetQuery(space), 1)]
                : [Target(TargetSpaceRoot, SpaceTargetQuery(space), 1), nodeTypes],
        });
        if (inventory.GitSync.Count > 0)
            steps.Add(new ControlLanePlanStep
            {
                Name = "Remove GitSync configuration",
                Command = $"DeleteNodeRequest(recursive, CascadeRootPath={space}) as system for each GitSync configuration node — "
                    + "first, so no sync source re-imports while the space is torn down; the ONE per-node leg, "
                    + $"{inventory.GitSyncRows.ToString(CultureInfo.InvariantCulture)} row(s) ≤ bound {PerNodeDeleteBound.ToString(CultureInfo.InvariantCulture)}",
                Destructive = true,
                Targets = [Target(TargetGitSync, GitSyncQuery(space), inventory.GitSync.Count)],
            });
        var teardownTargets = ImmutableList.CreateBuilder<ControlLanePlanTarget>();
        if (inventory.Grants.Count > 0)
            teardownTargets.Add(Target(TargetGrants, GrantsQuery(space), inventory.Grants.Count));
        teardownTargets.Add(Target(TargetSubtree, SubtreeQuery(space), null));
        if (inventory.NodeTypes.Count > 0)
            teardownTargets.Add(nodeTypes);
        steps.Add(new ControlLanePlanStep
        {
            Name = "Tear down the partition",
            Command = TeardownCommand(inventory),
            Destructive = true,
            Targets = teardownTargets.ToImmutable(),
        });
        steps.Add(new ControlLanePlanStep
        {
            Name = "Verify and write the audit record",
            Command = $"dispose again; re-read every table, the grants, the GitSync configuration, Admin/Partition/{space} and "
                + $"the store '{inventory.Schema}' as system — any residue FAILS the run; the audit record is written on this node",
        });

        var notes = ImmutableList.Create(
            "Rows per table (shown, not bound — the build queue and compile watcher of a stranded space keep writing "
            + "into it): " + (inventory.Tables.Count == 0
                ? "none"
                : string.Join(", ", inventory.Tables.Select(t => $"{t.Table} {t.Rows.ToString(CultureInfo.InvariantCulture)}")))
            + $" — {inventory.TotalRows.ToString(CultureInfo.InvariantCulture)} in all.",
            $"Admin/Partition/{space}: {(inventory.RecordExists ? "present" : "absent")}. Store '{inventory.Schema}': "
            + (inventory.StoreExists switch { true => "present", false => "absent", _ => "no provider can tell" }) + ".",
            "PRE-FLIGHT (policy governed-action-preflight — checked before the plan is offered, every step as system):",
            $"• Dispose: DisposeRequest as system to {1 + inventory.NodeTypes.Count} address(es) — one message each, no bound.",
            inventory.GitSync.Count == 0
                ? "• GitSync removal: nothing to remove."
                : $"• GitSync removal: per-node recursive delete as system, {inventory.GitSyncRows} row(s) ≤ bound {PerNodeDeleteBound}.",
            "• Teardown: PartitionTeardown.TearDownPartition as system — " + TeardownMechanism(inventory)
            + "; PartitionTeardown.Refusal answered none.",
            "• Verify: read as system; any residue fails the run.",
            "Not touched: the build coordinator's own bookkeeping (Admin/Build/…) — it stops scheduling the space once its store is gone.");
        return (steps.ToImmutable(), notes);
    }

    /// <summary>Target label key: the space's own address.</summary>
    public const string TargetSpaceRoot = "space-root";

    /// <summary>Target label key: the NodeTypes the space defines.</summary>
    public const string TargetNodeTypes = "nodetypes";

    /// <summary>Target label key: the space's access grants.</summary>
    public const string TargetGrants = "grants";

    /// <summary>Target label key: the space's GitSync configuration.</summary>
    public const string TargetGitSync = "gitsync";

    /// <summary>Target label key: the content roots (kept for stored plans that name them).</summary>
    public const string TargetContentRoots = "content-roots";

    /// <summary>Target label key: everything beneath them — uncounted, the rows are in the notes.</summary>
    public const string TargetSubtree = "subtree";

    private static ControlLanePlanTarget Target(string label, string query, int? count) =>
        new() { Label = label, Query = query, Count = count };

    /// <summary>The space's own address, as a PLAN TARGET (a set of one the reader can test). Not
    /// <see cref="RootQuery"/>, which is the existence LISTING the inventory reads it through. Pure.</summary>
    public static string SpaceTargetQuery(string space) => $"path:{space}";

    /// <summary>The space's direct children — a rootless space's content roots. Pure.</summary>
    public static string ChildrenQuery(string space) => $"namespace:{space} scope:children";

    /// <summary>Everything in the space, root included. Pure.</summary>
    public static string SubtreeQuery(string space) => $"path:{space} scope:subtree";

    /// <summary>Every NodeType the space defines. Pure.</summary>
    public static string NodeTypesQuery(string space) => $"namespace:{space} scope:descendants nodeType:{MeshNode.NodeTypePath}";

    /// <summary>Every access grant in the space. Pure.</summary>
    public static string GrantsQuery(string space) =>
        $"namespace:{space} scope:descendants nodeType:{AccessAssignmentGuard.AccessAssignmentNodeType}";

    /// <summary>Every GitSync configuration node in the space. Pure.</summary>
    public static string GitSyncQuery(string space) => $"namespace:{space} scope:descendants nodeType:{GitSyncNodeType}";

    /// <summary>
    /// The teardown step's command, as the plan states and the approval binds it: the mechanism, and
    /// what goes with the store — as counts; the sets themselves are the step's targets. Pure.
    /// </summary>
    public static string TeardownCommand(SpaceDeletionInventory inventory)
    {
        var space = inventory.Space;
        return $"PartitionTeardown.TearDownPartition(\"{space}\") as system — {TeardownMechanism(inventory)}, then "
            + $"Admin/Partition/{space} deleted inside the same claim. Never a per-node recursive delete of the content, never raw SQL. "
            + (inventory.Root is null
                ? "The space has NO root. "
                : $"The root is a {inventory.Root.NodeType ?? "(untyped)"} created by {inventory.Root.CreatedBy ?? "(unattributed)"}. ")
            + (inventory.Grants.Count == 0
                ? "No access grant is left. "
                : $"{inventory.Grants.Count.ToString(CultureInfo.InvariantCulture)} access grant(s) go with it. ")
            + (inventory.Dependents.Count == 0
                ? "No address outside the space depends on its NodeTypes."
                : $"{inventory.Dependents.Count.ToString(CultureInfo.InvariantCulture)} address(es) OUTSIDE the space depend on its NodeTypes and lose a type or a dependency.");
    }

    /// <summary>The residue a verification found, or null when the space is gone. Pure.</summary>
    public static string? Residue(SpaceDeletionInventory after)
    {
        var left = new List<string>();
        if (after.Unread.Count > 0)
            left.Add($"{after.Unread.Count} reading(s) were a floor: {string.Join(" | ", after.Unread)}");
        foreach (var table in after.Tables.Where(t => t.Rows > 0))
            left.Add($"{table.Rows} row(s) in {table.Table}");
        if (after.Grants.Count > 0)
            left.Add($"{after.Grants.Count} grant(s)");
        if (after.GitSync.Count > 0)
            left.Add($"{after.GitSync.Count} GitSync node(s)");
        if (after.RecordExists)
            left.Add($"the record Admin/Partition/{after.Space}");
        if (after.StoreExists == true)
            left.Add($"the store '{after.Schema}'");
        return left.Count == 0
            ? null
            : $"the deletion of '{after.Space}' is NOT complete — still there: {string.Join("; ", left)}. The run stops "
              + "here; a re-request reads what is left, parks with that plan and continues once approved";
    }

    /// <summary>The partition definition the platform derives for <paramref name="space"/>. Pure.</summary>
    public static PartitionDefinition DefinitionOf(string space) =>
        (PartitionDefinition)PartitionOwnership.PartitionDefinitionNode(new MeshNode(space) { Name = space }, "").Content!;

    /// <summary>Whether <paramref name="path"/> lives in a satellite table only a CUSTOM mapping names. Pure.</summary>
    public static bool IsCustomSatellite(PartitionDefinition definition, string path) =>
        definition.TableMappings is { } mappings
        && mappings.Keys.Any(segment => segment.StartsWith('_') && path.Split('/').Contains(segment, StringComparer.Ordinal));

    /// <summary>
    /// The complete listing that establishes whether <paramref name="path"/> exists: its parent's
    /// direct children, or — for a TOP-LEVEL path (a space root), whose parent is the mesh root —
    /// the path itself, read exactly. Either way the answer lists the path when it exists. Pure.
    ///
    /// <para>🚨 A top-level path is NEVER listed through its parent (MeshWeaver#5508). The parent is
    /// the empty root, and <c>path: scope:children</c> names no partition: on a partitioned store it
    /// is one <c>UNION ALL</c> over every partition schema (the lock-bomb shape the planner reports as
    /// <c>[FanOut] UNANCHORED</c>), run to find ONE row that lives in exactly one schema — the one
    /// the path's own first segment names. <c>path:X</c> anchors there.</para>
    /// </summary>
    public static string ParentListingQuery(string path)
    {
        var separator = path.LastIndexOf('/');
        return separator <= 0
            ? $"path:{path} select:path"
            : $"path:{path[..separator]} scope:children select:path";
    }

    /// <summary>The listing that establishes whether the space's <c>Admin/Partition</c> record exists. Pure.</summary>
    public static string RecordQuery(string space) => ParentListingQuery($"{PartitionNodeType.Namespace}/{space}");

    /// <summary>The listing that establishes whether the space root exists. Pure.</summary>
    public static string RootQuery(string space) => ParentListingQuery(space);

    /// <summary>The queries one inventory runs: the root, the main rows, one per satellite NodeType (defaults AND custom). Pure.</summary>
    public static IReadOnlyList<string> InventoryQueries(string space, PartitionDefinition? definition = null)
    {
        var queries = new List<string>
        {
            RootQuery(space),
            $"namespace:{space} scope:descendants select:path,id,namespace,nodeType",
        };
        var custom = (definition?.NodeTypeTableMappings ?? new Dictionary<string, string>())
            .Where(kv => !string.Equals(kv.Value, definition!.Table, StringComparison.Ordinal))
            .Select(kv => kv.Key);
        foreach (var nodeType in SatelliteTableMapping.Defaults
                     .Where(m => m.Segment.StartsWith('_'))
                     .SelectMany(m => m.NodeTypes)
                     .Concat(custom)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
            queries.Add($"nodeType:{nodeType} namespace:{space} scope:descendants select:path,id,namespace,nodeType");
        return queries;
    }

    /// <summary>The OR-fold of provider answers about a store. Pure.</summary>
    public static bool? FoldStore(IEnumerable<bool?> answers)
    {
        var list = answers.ToList();
        return list.Any(a => a == true) ? true : list.Any(a => a == false) ? false : null;
    }

    /// <summary>The instance's configured operational space, if any.</summary>
    public static string? OperationalSpace(IMessageHub hub) =>
        hub.ServiceProvider.GetService<IConfiguration>()?[OperationalSpaceKey];

    /// <summary>Whether a static (configuration-served) node roots the space.</summary>
    public static bool ServedByConfiguration(IMessageHub hub, string space) =>
        hub.ServiceProvider.FindStaticNode(space) is { IsDefinitionOnly: false };

    // ───────────────────────────── reads (as system, writing nothing) ─────────────────────────────

    /// <summary>
    /// The inventory of <paramref name="space"/>, read AS SYSTEM. The STORE is read first (once a
    /// provider says it is gone, no row can have outlived it, and a query routed into a dropped
    /// schema is a floor), then the <c>Admin/Partition</c> record (its content is the space's own
    /// definition), then every table. Never reads a floor as "none". Cold.
    /// </summary>
    public static IObservable<SpaceDeletionInventory> Inventory(IMessageHub hub, string space)
    {
        var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
        var recordPath = $"{PartitionNodeType.Namespace}/{space}";
        return StoreExists(hub, space)
            .Zip(AsSystem(hub, () => MeshReading.Read(mesh, RecordQuery(space))), (store, record) => (store, record))
            .SelectMany(first =>
            {
                var listedRecord = first.record.Rows
                    .FirstOrDefault(r => string.Equals(r.Path, recordPath, StringComparison.OrdinalIgnoreCase));
                var recordRead = !first.record.IsAnswer || listedRecord is null
                    ? Observable.Return(new AuthoritativeNodeRead(recordPath, null, null))
                    : ReadCurrentNode(hub, recordPath);
                return recordRead.SelectMany(currentRecord =>
                {
                    var definition = currentRecord.Node?.ContentAs<PartitionDefinition>(hub.JsonSerializerOptions);
                    var unreadRecord = first.record.IsAnswer
                        ? []
                        : new[] { $"{first.record.Query}: {first.record.WhyNotAnAnswer}" };
                    var liveRecordUnread = first.record.IsAnswer && listedRecord is not null && currentRecord.Node is null
                        ? new[] { $"{recordPath}: the record was listed but its current node could not be read — {currentRecord.Error}" }
                        : [];
                    var definitionUnread = listedRecord is not null && currentRecord.Node is not null && definition is null
                        ? new[] { $"{recordPath}: the current node exists but its content is not a readable PartitionDefinition" }
                        : [];
                    var queries = first.store == false ? [] : InventoryQueries(space, definition).ToArray();
                    var rootQuery = RootQuery(space);
                    var readings = queries
                        .Select(q => AsSystem(hub, () => MeshReading.Read(mesh, q)))
                        .MergeBounded(ReadConcurrency)
                        .ToList();
                    return readings.SelectMany(items =>
                    {
                        var listing = items.FirstOrDefault(r => r.Query == rootQuery);
                        var rootRead = listing is null
                            ? Observable.Return(new AuthoritativeNodeRead(space, null,
                                first.store == false ? null : "the root listing was not returned"))
                            : !listing.IsAnswer
                                ? Observable.Return(new AuthoritativeNodeRead(space, null,
                                    $"the root listing did not answer — {listing.WhyNotAnAnswer}"))
                                : listing.Rows.FirstOrDefault(r => string.Equals(r.Path, space, StringComparison.Ordinal)) is null
                                    ? Observable.Return(new AuthoritativeNodeRead(space, null, null))
                                    : ReadCurrentNode(hub, space);
                        return rootRead.SelectMany(rootReadResult =>
                        {
                            var unread = unreadRecord.Concat(liveRecordUnread).Concat(definitionUnread)
                                .Concat(items.Where(r => !r.IsAnswer).Select(r => $"{r.Query}: {r.WhyNotAnAnswer}"))
                                .ToList();
                            if (rootReadResult.Error is not null)
                                unread.Add($"{space}: the root listing or current node could not be read — {rootReadResult.Error}");
                            var root = rootReadResult.Node;
                            // The root's indexed row is existence evidence only. Do not let its stale
                            // NodeType/CreatedBy/Name contribute to the deletion plan.
                            var rows = items.Where(r => r.Query != rootQuery).SelectMany(r => r.Rows)
                                .Where(r => !string.Equals(r.Path, space, StringComparison.Ordinal)
                                            && r.Path.StartsWith(space + "/", StringComparison.Ordinal))
                                .Concat(root is null ? [] : new[] { root })
                                .ToList();
                            var nodeTypes = rows.Where(r => string.Equals(r.NodeType, MeshNode.NodeTypePath, StringComparison.Ordinal))
                                .Select(r => r.Path).Distinct(StringComparer.Ordinal).ToList();
                            return Networks(hub, nodeTypes).Select(network => Fold(
                                space, root, rows, listedRecord is not null, first.store, network.Addresses,
                                unread.Concat(network.Unread), definition));
                        });
                    });
                });
            });
    }

    /// <summary>
    /// Reads current node content from its owning stream. Call only after a complete listing has
    /// established that the path exists; an empty/faulted/timeout stream is not evidence that the
    /// listed node is absent and must leave the deletion plan unreadable.
    /// </summary>
    internal static IObservable<AuthoritativeNodeRead> ReadCurrentNode(IMessageHub hub, string path) =>
        AsSystem(hub, () => hub.GetMeshNodeStream(path)
            .Take(1)
            .Timeout(MeshReading.DefaultBudget)
            .Select(node => new AuthoritativeNodeRead(path, node, null))
            .DefaultIfEmpty(new AuthoritativeNodeRead(path, null, "the node stream completed without a node"))
            .Catch((Exception ex) => Observable.Return(new AuthoritativeNodeRead(
                path, null, $"{ex.GetType().Name}: {ex.Message}"))));

    /// <summary>The union of the NodeTypes' dependency networks and the legs that could not be read. Never errors. Cold.</summary>
    private static IObservable<(ImmutableList<string> Addresses, ImmutableList<string> Unread)> Networks(
        IMessageHub hub, IReadOnlyList<string> nodeTypes)
    {
        if (nodeTypes.Count == 0)
            return Observable.Return((ImmutableList<string>.Empty, ImmutableList<string>.Empty));
        return nodeTypes
            .Select(type => AsSystem(hub, () => NodeTypeRecycleCascade.DependencyNetwork(hub, type))
                .Take(1)
                .Timeout(NetworkBudget)
                .Select(network => (Addresses: network.Addresses, Unread: network.Incomplete
                    .Select(leg => $"dependency network of {type}: {leg}").ToImmutableList()))
                .Catch((Exception ex) => Observable.Return((Addresses: ImmutableList<string>.Empty,
                    Unread: ImmutableList.Create($"dependency network of {type}: {ex.GetType().Name}: {ex.Message}"))))
                .DefaultIfEmpty((Addresses: ImmutableList<string>.Empty,
                    Unread: ImmutableList.Create($"dependency network of {type}: completed without an answer"))))
            .MergeBounded(ReadConcurrency)
            .Aggregate((Addresses: ImmutableList<string>.Empty, Unread: ImmutableList<string>.Empty),
                (all, one) => (all.Addresses.AddRange(one.Addresses), all.Unread.AddRange(one.Unread)));
    }

    /// <summary>Whether the space's backing store exists, OR-folded over every storage provider. Never errors. Cold.</summary>
    public static IObservable<bool?> StoreExists(IMessageHub hub, string space)
    {
        var providers = hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToList();
        if (providers.Count == 0)
            return Observable.Return<bool?>(null);
        return providers
            .Select(p => p.PartitionExists(space)
                .Take(1)
                .Timeout(MeshReading.DefaultBudget)
                .DefaultIfEmpty(null)
                .Catch((Exception _) => Observable.Return<bool?>(null)))
            .Merge()
            .ToList()
            .Select(FoldStore);
    }

    // ───────────────────────────── writes (as system) ─────────────────────────────

    /// <summary>
    /// Posts a <c>DisposeRequest</c> — as system, off the router — to the space's root and to every
    /// NodeType definition it holds (a definition's dispose cascades to its dependency network).
    /// </summary>
    public static IObservable<string> Dispose(IMessageHub hub, SpaceDeletionInventory inventory, string reason)
    {
        var targets = new[] { inventory.Space }.Concat(inventory.NodeTypes).ToList();
        return Observable.Defer(() =>
        {
            var access = hub.ServiceProvider.GetRequiredService<AccessService>();
            using (access.ImpersonateAsSystem())
                foreach (var target in targets)
                    hub.GetMeshHub().NodeOperationIssuingHub()
                        .Post(new DisposeRequest { Reason = reason }, o => o.WithTarget(new Address(target)));
            return Observable.Return($"dispose posted to {targets.Count} address(es): " + string.Join(", ", targets));
        });
    }

    /// <summary>
    /// Deletes each path through the framework — a recursive <c>DeleteNodeRequest</c> carrying
    /// <c>CascadeRootPath</c> = the space — AS SYSTEM, one after another. Idempotent: an absent path
    /// is a success. The first failure stops the phase, naming the path.
    /// </summary>
    public static IObservable<string> DeleteEach(IMessageHub hub, string space, string what, IReadOnlyList<string> targets)
    {
        if (targets.Count == 0)
            return Observable.Return($"no {what} to remove");
        return targets
            .Select(target => AsSystem(hub, () => DeleteInSpace(hub, target, space))
                .Take(1)
                .Timeout(DeleteBudget)
                // 🚨 An empty completion is NOT "already gone": no answer is no evidence either way.
                .Select(removed => (bool?)removed)
                .DefaultIfEmpty(null)
                .Select(removed => removed ?? throw new InvalidOperationException(
                    $"the delete of {what} '{target}' completed WITHOUT an answer — whether it was removed is unknown"))
                .Catch((Exception ex) => Observable.Throw<bool>(new InvalidOperationException(
                    $"the delete of {what} '{target}' failed — {ex.GetType().Name}: {ex.Message}", ex)))
                .Select(removed => (target, removed)))
            .Concat()
            .ToList()
            .Select(outcomes =>
                $"{what}(s): {outcomes.Count(o => o.removed)} removed, {outcomes.Count(o => !o.removed)} already gone — "
                + string.Join(", ", outcomes.Select(o => o.target)));
    }

    /// <summary>
    /// ONE framework delete of <paramref name="path"/> as part of deleting the whole of
    /// <paramref name="space"/> (<c>CascadeRootPath</c> = the space, which is what lets the LAST
    /// Admin grant of an orphaned space go). True = removed, false = already gone; errors on a
    /// refusal. Cold; the caller supplies the system identity.
    /// </summary>
    public static IObservable<bool> DeleteInSpace(IMessageHub hub, string path, string space) =>
        Observable.Defer(() => hub.GetMeshHub().NodeOperationIssuingHub()
            .Observe(new DeleteNodeRequest(path)
                {
                    Recursive = true,
                    CascadeRootPath = space,
                    DeletedBy = WellKnownUsers.System,
                },
                o => o.WithTarget(hub.NodeOperationTarget()))
            .SelectMany(delivery => delivery.Message.Success
                ? Observable.Return(!delivery.Message.AlreadyAbsent)
                : Observable.Throw<bool>(new InvalidOperationException(
                    $"{delivery.Message.RejectionReason}: {delivery.Message.Error ?? "the delete was refused"}"))));

    /// <summary>
    /// The space's content, grants and store, removed as ONE operation: the platform's whole-partition
    /// teardown (<see cref="PartitionTeardown.TearDownPartition"/>), AS SYSTEM — one drop
    /// per provider whatever the size, then the <c>Admin/Partition</c> record. Idempotent. Cold.
    /// </summary>
    public static IObservable<string> TearDown(IMessageHub hub, string space, string because) =>
        AsSystem(hub, () => hub.TearDownPartition(space, because))
            .Take(1)
            .Timeout(TeardownBudget)
            .Select(outcome => $"partition '{space}' torn down as system — one drop across {outcome.Providers} provider(s), "
                               + $"Admin/Partition/{space} {(outcome.RecordDeleted ? "deleted" : "already absent")}; "
                               + "the verification reads whether anything is left");

    /// <summary>
    /// The whole deletion after the plan was verified: dispose, GitSync, ONE partition teardown (content,
    /// grants and store together), then dispose again, re-read and verify — any residue FAILS. Emits one line per step and, last,
    /// the verified <c>after</c> inventory as the final line's subject. Cold.
    /// </summary>
    public static IObservable<(string Line, SpaceDeletionInventory? After)> Run(
        IMessageHub hub, SpaceDeletionInventory before, string disposeReason) =>
        Dispose(hub, before, disposeReason).Select(Line)
            .Concat(Observable.Defer(() => DeleteEach(hub, before.Space, "GitSync node", before.GitSync).Select(Line)))
            .Concat(Observable.Defer(() => TearDown(hub, before.Space, disposeReason).Select(Line)))
            .Concat(Observable.Defer(() => Dispose(hub, before, disposeReason).Select(Line)))
            .Concat(Observable.Defer(() => Inventory(hub, before.Space)
                .SelectMany(after => Residue(after) is { } residue
                    ? Observable.Throw<(string, SpaceDeletionInventory?)>(new InvalidOperationException(residue))
                    : Observable.Return<(string, SpaceDeletionInventory?)>(
                        ($"verified: nothing of '{before.Space}' is left", after)))));

    private static (string, SpaceDeletionInventory?) Line(string line) => (line, null);

    /// <summary>Runs <paramref name="work"/> with the system identity captured at the call.</summary>
    internal static IObservable<T> AsSystem<T>(IMessageHub hub, Func<IObservable<T>> work) =>
        hub.ServiceProvider.GetService<AccessService>().RunAsSystem(work);

    private static string FirstSegment(string? path)
    {
        var value = (path ?? "").Trim().Trim('/');
        var slash = value.IndexOf('/');
        return slash < 0 ? value : value[..slash];
    }
}
