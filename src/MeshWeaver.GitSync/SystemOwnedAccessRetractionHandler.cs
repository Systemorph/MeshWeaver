using System.Collections.Immutable;
using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// 🚨 THE MOMENT A PARTITION GAINS A <c>_GitSync</c> IT BECOMES SYSTEM-OWNED — so every privileged
/// account grant on it is retracted, right there.
///
/// <para><b>The window this closes.</b> <c>AccessAssignmentGuard.IsForbiddenOnSystemOwned</c> makes
/// it impossible to WRITE such a grant onto a space that is already system-owned, and
/// <c>EnsurePartitionBootstrap</c> stopped re-minting one on every deploy. Neither helps when the
/// order is reversed: a Space hand-created by a person is an ORDINARY space for as long as it takes
/// to wire the sync, its creator gets Admin the ordinary way, and that grant then sits on a space
/// the repo owns — legal when written, wrong one second later.</para>
///
/// <para>Measured on memex 2026-08-04: <c>SST/_Access/rbuergi_Access</c> (Admin) at 14:48:07,
/// <c>SST/_GitSync</c> at 14:48:14. Seven seconds. Seventeen Admin grants across three meshes
/// entered through that window, and nothing anywhere reported them — the space looked correct,
/// the content synced, the pages rendered.</para>
///
/// <para><b>Why retract rather than refuse the sync.</b> Refusing to wire <c>_GitSync</c> while a
/// grant exists turns a deploy into a permissions error at the worst moment and teaches people to
/// hand-clean access lists. Retracting states the invariant instead: the repo owns this space now,
/// so nobody else does. It is also SELF-HEALING — a mesh that already drifted converges the next
/// time its sync is (re)wired.</para>
///
/// <para><b>What survives, deliberately:</b> <c>Viewer</c>/<c>Commenter</c> entitlements (a
/// purchase, a redeemed coupon, an admin-issued grant — the only door into a gated plugin), every
/// <c>Denied</c> assignment (that IS the gating), <c>system-security</c> itself (the identity
/// the importer writes under — retracting it would make the space unwritable by anything), and
/// the space's LAST administrator (#1120): the last-admin invariant
/// (<c>SpaceAdminInvariantValidator</c> — "a space must always have at least one admin")
/// outranks the sweep, so when no grant outside the doomed set still confers Admin, one admin
/// grant is spared — the earliest-created, i.e. the space's original owner — and logged at
/// Warning. The delete pipeline would refuse exactly that delete anyway; detecting it up front
/// turns a guaranteed rejection (formerly a fail-level "FAILED to retract") into the expected,
/// stated outcome. The spared grant converges like the rest of the sweep: the next time the
/// sync is (re)wired after another admin exists, it is retracted.</para>
///
/// <para>Best-effort by design (<c>FailsCreateOnError</c> stays false): a partition that cannot be
/// swept must not block its own sync from being configured. Every retraction is logged at Warning
/// naming the subject and the space, because access silently disappearing is its own bug class.</para>
/// </summary>
public sealed class SystemOwnedAccessRetractionHandler(
    IMessageHub hub,
    IMeshService meshService,
    IStorageAdapter persistence,
    AccessService? accessService,
    ILogger<SystemOwnedAccessRetractionHandler>? logger) : INodePostCreationHandler
{
    /// <summary>Fires for the sync-config node — the thing whose existence MAKES a space system-owned.</summary>
    public string NodeType => GitHubSyncService.ConfigNodeType;

    public IObservable<Unit> Handle(MeshNode createdNode, string? createdBy)
    {
        // The config lives at {partition}/_GitSync, so the owning partition is its namespace's
        // first segment. A root-level config owns nothing and is not this handler's business.
        var partition = AccessAssignmentGuard.PartitionOf(createdNode.Namespace ?? "");
        if (string.IsNullOrEmpty(partition))
            return Observable.Return(Unit.Default);

        var accessFolder = $"{partition}/_Access";

        return persistence.ListChildPaths(accessFolder)
            .Take(1)
            .Catch<(IEnumerable<string> NodePaths, IEnumerable<string> DirectoryPaths), Exception>(ex =>
            {
                logger?.LogDebug(ex,
                    "[SystemOwned] could not list '{Folder}'; nothing to retract", accessFolder);
                return Observable.Return<(IEnumerable<string>, IEnumerable<string>)>(([], []));
            })
            .SelectMany(children =>
            {
                var paths = children.NodePaths?.ToArray() ?? [];
                return paths.Length == 0
                    ? Observable.Return<IList<MeshNode>>([])
                    : persistence.ReadMany(paths, hub.JsonSerializerOptions).ToList();
            })
            .SelectMany(grants => RetractAll(partition, grants));
    }

    private IObservable<Unit> RetractAll(string partition, IList<MeshNode> grants)
    {
        // The SAME predicate the write boundary uses — one rule, two enforcement points, so a
        // grant that could not be created here can never survive here either.
        var candidates = grants
            .Select(n => (Node: n, Assignment: n.ContentAs<AccessAssignment>(hub.JsonSerializerOptions)))
            .ToArray();

        var doomed = candidates
            .Where(c => AccessAssignmentGuard.IsForbiddenOnSystemOwned(
                c.Node, c.Assignment, systemOwned: true, out _))
            .ToArray();

        if (doomed.Length == 0)
            return Observable.Return(Unit.Default);

        // 🚨 The last-admin invariant OUTRANKS the sweep (#1120). SpaceAdminInvariantValidator
        // refuses to delete a partition's last non-denied Admin assignment — a space must always
        // keep at least one admin who can manage it (the System identity holds Permission.All
        // WITHOUT an assignment node, so it does not count). Attempting that delete is therefore
        // a guaranteed rejection: a known-valid guard outcome, not a fault. Detect it up front —
        // when no grant OUTSIDE the doomed set still confers Admin, spare exactly one doomed
        // admin grant (deterministically the earliest-created: the space's original owner) and
        // retract the rest. The spared grant self-heals like the rest of the sweep: it is
        // retracted the next time the sync is (re)wired once another admin exists.
        var doomedPaths = doomed
            .Select(c => c.Node.Path)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var adminSurvives = candidates.Any(c =>
            !doomedPaths.Contains(c.Node.Path)
            && string.Equals(c.Node.NodeType, AccessAssignmentGuard.AccessAssignmentNodeType,
                StringComparison.OrdinalIgnoreCase)
            && AccessAssignmentGuard.GrantsAdmin(c.Assignment));

        var spared = adminSurvives
            ? default
            : doomed
                .Where(c => AccessAssignmentGuard.GrantsAdmin(c.Assignment))
                .OrderBy(c => c.Node.CreatedDate)
                .ThenBy(c => c.Node.Path, StringComparer.Ordinal)
                .FirstOrDefault();

        if (spared.Node is not null)
        {
            logger?.LogWarning(
                "[SystemOwned] keeping '{Path}' — '{Subject}' is the last administrator of "
                + "'{Partition}' and a space must always have at least one admin. The grant still "
                + "confers write access to a system-owned space; it is retracted the next time the "
                + "sync is wired once another admin exists.",
                spared.Node.Path, spared.Assignment?.AccessObject ?? "?", partition);
        }

        var doomedList = doomed
            .Select(c => c.Node.Path)
            .Where(p => spared.Node is null
                || !string.Equals(p, spared.Node.Path, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (doomedList.Length == 0)
            return Observable.Return(Unit.Default);

        logger?.LogWarning(
            "[SystemOwned] retracting {Count} privileged grant(s) — the partition is now system-owned "
            + "and only {System} may write it: {Paths}",
            doomedList.Length, WellKnownUsers.System, string.Join(", ", doomedList));

        // Sequential (Concat, never Merge): these are node deletes into a partition that may have
        // been created seconds ago, and a fan-out of cold per-node hub activations is what crashes
        // writes into a fresh partition. Under System — the retraction is the platform's decision,
        // not the caller's, and the caller may hold no rights on the space at all.
        return AsSystem(() => doomedList
            .Select(path => meshService.DeleteNode(path)
                .Take(1)
                .Select(_ => Unit.Default)
                .Catch<Unit, Exception>(ex =>
                {
                    logger?.LogError(ex,
                        "[SystemOwned] FAILED to retract '{Path}' — it still grants write access to a "
                        + "system-owned space", path);
                    return Observable.Return(Unit.Default);
                }))
            .Concat()
            .DefaultIfEmpty(Unit.Default)
            .TakeLast(1));
    }

    /// <summary>Establishes the System identity on the write's own subscribe thread — mirrors
    /// <c>MeshExtensions.AsSystem</c>, so the deletes are attributed to the platform.</summary>
    private IObservable<T> AsSystem<T>(Func<IObservable<T>> write)
        => accessService is null
            ? Observable.Defer(write)
            : Observable.Using(() => accessService.ImpersonateAsSystem(), _ => write());
}
