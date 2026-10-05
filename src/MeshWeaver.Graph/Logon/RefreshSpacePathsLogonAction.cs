using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Logon;

/// <summary>
/// Keeps the home page's path MANIFESTS on the user's own profile — <see cref="User.SpacePaths"/>
/// (the partition-root spaces the user can read) and <see cref="User.SharedPaths"/> (the scopes in
/// other partitions they were granted) — so the home anchors its queries on them
/// (<c>path:a|b|c</c>) and painting it issues no mesh-wide query at all.
///
/// <para>🚨 <b>Why this moved here from the render path.</b> Both reads are mesh-wide by nature: a
/// partition root lives in its own partition, and a share grant lives in the GRANTING partition
/// (Doc/Architecture/UnanchoredSecurityReads). The home ran them on EVERY render — measured on the
/// public instance 2026-10-04 as a <c>UNION ALL</c> over 251 partition schemas, ~1.3 s each, whose
/// relation locks also queue every anchored read on the database behind them (the apps band
/// included). Maintainer, 2026-10-04: "installed apps must be in manifest on user's home. only this
/// must be read … rest must be slow, page must load quickly". So the read runs once per logon
/// session, in the background, and the render reads the profile it already streams.</para>
///
/// <para><b>EveryLogon</b>, because the answer changes: spaces are created and grants are issued
/// after a user's first logon. The cost is bounded the way every every-logon action's must be — two
/// bounded reads, and a write only when either list actually changed.</para>
///
/// <para>Runs as the LOGGING-ON USER, which is what makes the lists correct: the reads are access
/// filtered for exactly the person whose home will list them. A list scopes what the home LISTS,
/// never what the user may open — a stale list hides a space from the home until the next logon,
/// and grants nothing.</para>
/// </summary>
public sealed class RefreshSpacePathsLogonAction : ILogonAction
{
    /// <inheritdoc />
    public string Id => "platform.refresh-space-paths";

    /// <inheritdoc />
    public LogonActionMode Mode => LogonActionMode.EveryLogon;

    /// <summary>Early: nothing else depends on it, and the sooner the home's manifest is fresh the
    /// sooner a first-time home repaints with its spaces.</summary>
    public int Order => 10;

    /// <summary>Bound on each mesh-wide read. A slow store costs this logon's refresh — the home keeps
    /// the lists from the previous one — never the logon.</summary>
    private static readonly TimeSpan QueryBound = TimeSpan.FromSeconds(20);

    /// <summary>The root leg the home used to run per render: every partition root that is a Space.
    /// Mesh-wide by nature, and it says so (#3202 — fan-out is opt-in).</summary>
    internal const string SpaceRootsQuery =
        "namespace: is:main is:content nodeType:Space select:path,id,namespace,nodeType "
        + ParsedQuery.CrossPartitionQualifier;

    /// <summary>The share grants naming this user — the read the home's "shared with me" leg used to
    /// run per render.</summary>
    internal static string SharedGrantsQuery(string owner) =>
        MeshWideQuery.Declare($"nodeType:AccessAssignment content.accessObject:{owner}");

    /// <inheritdoc />
    public IObservable<LogonActionOutcome> Run(LogonActionContext context)
    {
        var mesh = context.Hub.ServiceProvider.GetService<IMeshService>();
        var logger = context.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.Logon.RefreshSpacePaths");
        var owner = context.UserPath;
        if (mesh is null || string.IsNullOrEmpty(owner))
            return Observable.Return(LogonActionOutcome.Nothing);

        // Sequential, not Zip/Merge: two concurrent fan-outs per logon is twice the lock pressure
        // this action exists to remove from the render path.
        return Initial(mesh, SpaceRootsQuery)
            .SelectMany(roots => Initial(mesh, SharedGrantsQuery(owner))
                .Select(grants => (
                    Spaces: SpacePathsFrom(roots, owner),
                    Shared: UserActivityLayoutAreas.SharedTargetPaths(grants, owner))))
            .Select(lists => Decide(lists.Spaces, lists.Shared))
            // A failed refresh keeps the previous lists — Nothing, not a fault: an every-logon action
            // that throws logs a failure on every single logon (see AppIconAdoptionLogonAction).
            .Catch<LogonActionOutcome, Exception>(ex =>
            {
                logger?.LogDebug(ex, "Space-path refresh skipped for {User}", owner);
                return Observable.Return(LogonActionOutcome.Nothing);
            });
    }

    private static IObservable<IReadOnlyCollection<MeshNode>> Initial(IMeshService mesh, string query) =>
        mesh.Query<MeshNode>(MeshQueryRequest.FromQuery(query))
            .Where(change => change.ChangeType == QueryChangeType.Initial)
            .Select(change => (IReadOnlyCollection<MeshNode>)change.Items.ToArray())
            .Take(1)
            .Timeout(QueryBound);

    /// <summary>
    /// The partition-root space paths from the root read, the owner's own home excluded (it is the
    /// home's own leg), distinct and sorted so an unchanged answer compares equal. Pure.
    /// </summary>
    internal static IReadOnlyList<string> SpacePathsFrom(IEnumerable<MeshNode> roots, string owner) =>
        Normalize(roots
            .Select(n => n.Path?.Trim('/'))
            .Where(p => !string.IsNullOrEmpty(p) && !p!.Contains('/'))
            .Where(p => !string.Equals(p, owner, StringComparison.OrdinalIgnoreCase))
            .Select(p => p!));

    /// <summary>
    /// The profile change for a fresh answer: the two lists, written together — or a change that
    /// keeps the profile as it is when neither list differs, so the runner's no-op detection skips
    /// the write. Decided against the profile the runner hands the change (its rebase re-runs it on
    /// fresher state), never against a copy read earlier. Pure.
    /// </summary>
    internal static LogonActionOutcome Decide(IReadOnlyList<string> spaces, IReadOnlyList<string> shared)
    {
        var s = Normalize(spaces);
        var g = Normalize(shared);
        return LogonActionOutcome.Profile(user => Apply(user, s, g));
    }

    /// <summary>The user with the two lists replaced — the SAME instance when neither changed. Pure.</summary>
    internal static User Apply(User user, IReadOnlyList<string> spaces, IReadOnlyList<string> shared) =>
        SameList(user.SpacePaths, spaces) && SameList(user.SharedPaths, shared)
            ? user
            : user with { SpacePaths = spaces, SharedPaths = shared };

    private static IReadOnlyList<string> Normalize(IEnumerable<string> paths) =>
        paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static bool SameList(IReadOnlyList<string>? stored, IReadOnlyList<string> fresh) =>
        Normalize(stored ?? []).SequenceEqual(fresh, StringComparer.OrdinalIgnoreCase);
}
