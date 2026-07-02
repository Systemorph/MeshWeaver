using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Hosting.Persistence;

/// <summary>
/// Lazy repair of LEGACY user partitions (pre-v10 layout: the user node lives at
/// <c>User/{id}</c> and no partition-root <c>{id}</c> node exists). Post-v10 the user's home IS
/// the partition root — the Activity dashboard, DevLogin's authoritative read, and every
/// <c>GetMeshNodeStream(userId)</c> consumer bind the root node, so a legacy store renders an
/// empty home ("awaiting first data") forever.
///
/// <para>The repair composes over a raw storage read (<see cref="ReadWithRepair"/>) and runs on
/// BOTH read seams — <see cref="PersistenceService.Read"/> (direct adapter chain) and
/// <see cref="PartitionStorage.RoutingProxyAdapter"/> (partition-storage-hub routing): a read
/// MISS on a bare partition-root path — or a typeless stub a satellite write (ApiToken,
/// UserActivity) auto-anchored — whose <c>User/{id}</c> twin holds an Active <c>User</c> node
/// materializes the post-v10 shape durably: the partition-root node (same content/identity, the
/// exact <c>UserOnboardingService.CreateUser</c> shape) plus the self-admin
/// <see cref="AccessAssignment"/> at <c>{id}/_Access</c> (the <c>GrantSelfAdmin</c> twin, without
/// which the user can read but never write their own partition). Idempotent: once written, the
/// root read hits (with NodeType) and the repair never fires again. This heals any legacy store
/// on first access — including partitions imported from a file system dump — with no separate
/// migration step.</para>
///
/// <para>The legacy <c>User/{id}</c> subtree (threads, notes, satellites) stays readable at its
/// legacy paths — <c>UserNodeType</c> keeps the transitional <c>User/</c> passthrough.</para>
/// </summary>
public static class LegacyUserPartitionRepair
{
    /// <summary>
    /// A partition-root read with the legacy-user repair applied: serves the raw result untouched
    /// unless it is a miss or an upgradable stub whose legacy twin holds the real user — then the
    /// repaired root is written through <paramref name="write"/> and emitted. Internal reads
    /// (the legacy twin, the grant-existence probe) use the RAW <paramref name="read"/> — the
    /// repair never recurses.
    /// </summary>
    /// <param name="path">The path being read.</param>
    /// <param name="read">The raw storage read.</param>
    /// <param name="write">The durable storage write; may emit null when no backend claims the
    /// node — the repaired root is still served (the next read repairs again).</param>
    /// <param name="logger">Optional logger; each repair logs once at Information.</param>
    public static IObservable<MeshNode?> ReadWithRepair(
        string path,
        Func<string, IObservable<MeshNode?>> read,
        Func<MeshNode, IObservable<MeshNode?>> write,
        ILogger? logger)
        => read(path).SelectMany(node => node is null
            ? Repair(path, existing: null, read, write, logger)
            : IsUpgradableStubRoot(path, node)
                // A satellite write (ApiToken, UserActivity) may have auto-anchored the legacy
                // partition with a typeless stub — upgrade it from the legacy twin; when there is
                // no legacy twin the stub itself stays the result.
                ? Repair(path, node, read, write, logger).Select(repaired => repaired ?? node)
                : Observable.Return<MeshNode?>(node));

    private static IObservable<MeshNode?> Repair(
        string path,
        MeshNode? existing,
        Func<string, IObservable<MeshNode?>> read,
        Func<MeshNode, IObservable<MeshNode?>> write,
        ILogger? logger)
    {
        if (!IsPartitionRootCandidate(path))
            return Observable.Return(default(MeshNode?));

        return read(LegacyPathFor(path))
            .SelectMany(legacy =>
            {
                if (!IsRepairableLegacyUser(legacy))
                    return Observable.Return(default(MeshNode?));

                logger?.LogInformation(
                    "[Persistence] Repairing legacy user partition '{Partition}' ({Mode}): materializing the "
                    + "post-v10 partition root + self-admin grant from '{LegacyPath}'.",
                    path, existing is null ? "missing root" : "stub root", LegacyPathFor(path));

                var root = MaterializeRoot(path, legacy!, existing);
                return write(root)
                    // Grant self-admin only when the repaired partition has no grant yet — never
                    // clobber a custom assignment that already lives at the target path.
                    .SelectMany(saved => read(SelfAdminGrantPath(path))
                        .SelectMany(existingGrant => existingGrant is not null
                            ? Observable.Return(saved)
                            : write(SelfAdminGrant(path, legacy!)).Select(_ => saved))
                        .Select(saved2 => (MeshNode?)(saved2 ?? root)));
            });
    }

    /// <summary>A bare, non-reserved single segment — the only shape a user partition root has.</summary>
    public static bool IsPartitionRootCandidate(string path)
        => !string.IsNullOrWhiteSpace(path)
           && !path.Contains('/')
           && !path.StartsWith('_');

    /// <summary>The legacy twin of a partition-root path.</summary>
    public static string LegacyPathFor(string userId) => $"User/{userId}";

    /// <summary>Only an Active node of type <c>User</c> with content is a repairable legacy user.</summary>
    public static bool IsRepairableLegacyUser(MeshNode? legacy)
        => legacy is { Content: not null, State: MeshNodeState.Active }
           && string.Equals(legacy.NodeType, "User", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A typeless, content-less partition-root placeholder. Writing a satellite (an ApiToken, a
    /// UserActivity record) into a legacy user's partition auto-anchors it with exactly this stub —
    /// the root then EXISTS, so a missing-root check alone never repairs, yet the Activity home
    /// still binds a User-less node and renders empty. Such a stub upgrades from the legacy twin.
    /// </summary>
    public static bool IsUpgradableStubRoot(string path, MeshNode node)
        => IsPartitionRootCandidate(path)
           && string.IsNullOrEmpty(node.NodeType)
           && node.Content is null
           && node.State == MeshNodeState.Active;

    /// <summary>
    /// The post-v10 partition-root node materialized from its legacy twin — path <c>{id}</c>
    /// (namespace ''), identity and content preserved (the <c>UserOnboardingService.CreateUser</c>
    /// shape). When a stub root already <paramref name="existing"/>s, its creation metadata and
    /// version lineage are preserved (the upgrade is an update, not a recreate).
    /// </summary>
    public static MeshNode MaterializeRoot(string userId, MeshNode legacy, MeshNode? existing = null)
        => new(userId)
        {
            Name = legacy.Name,
            Description = legacy.Description,
            NodeType = "User",
            Category = legacy.Category,
            Icon = legacy.Icon,
            Order = legacy.Order,
            State = MeshNodeState.Active,
            Content = legacy.Content,
            CreatedDate = existing?.CreatedDate ?? legacy.CreatedDate,
            CreatedBy = existing?.CreatedBy ?? legacy.CreatedBy,
            LastModified = legacy.LastModified,
            LastModifiedBy = legacy.LastModifiedBy,
            Version = existing is null ? legacy.Version : existing.Version + 1,
        };

    /// <summary>Path of the user's self-admin grant in the repaired partition.</summary>
    public static string SelfAdminGrantPath(string userId) => $"{userId}/_Access/{userId}_Access";

    /// <summary>
    /// The self-admin <see cref="AccessAssignment"/> for the repaired partition — the exact
    /// <c>UserOnboardingService.GrantSelfAdmin</c> shape. Without it the user can read their own
    /// partition root (public read) but every write fails ("Create permission required").
    /// </summary>
    public static MeshNode SelfAdminGrant(string userId, MeshNode legacy)
        => new($"{userId}_Access", $"{userId}/_Access")
        {
            NodeType = "AccessAssignment",
            Name = $"{userId} Access",
            MainNode = userId,
            Content = new AccessAssignment
            {
                AccessObject = userId,
                DisplayName = legacy.Name ?? userId,
                Roles = [new RoleAssignment { Role = Role.Admin.Id, Denied = false }],
            },
        };
}
