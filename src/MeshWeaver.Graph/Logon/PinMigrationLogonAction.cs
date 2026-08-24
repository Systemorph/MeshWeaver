using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Logon;

/// <summary>
/// Runs one DATA-declared <see cref="LogonAction"/>: unpin some paths from the user's profile, then
/// pin others. The shipped use is the docs→courses migration, but nothing about it is specific to
/// that — the target lists come entirely from the node.
///
/// <para>🚨 <b>Pins are existence-checked; unpins are not.</b> Deliberately asymmetric. Unpinning a
/// path that has gone away is exactly right — that is often WHY it is being unpinned. Pinning one
/// that does not exist writes a dead tile onto every user's home, which is the failure mode a
/// data-declared action makes easy to reach: the same declaration can be copied to a portal that
/// does not carry the content it names. A deployment missing the targets therefore pins nothing and
/// records the action as done, rather than crashing or leaving a dangling path.</para>
///
/// <para><b>It never clobbers a user's own curation.</b> An unpin removes only paths the
/// declaration names; a pin appends only what is not already there and preserves the user's
/// existing order. A user who re-pins something by hand after the migration keeps it, because the
/// run-once ledger means the migration never looks at them again.</para>
/// </summary>
/// <param name="id">The node id, which is the action id and therefore the ledger key.</param>
/// <param name="declaration">The node's content.</param>
public sealed class PinMigrationLogonAction(string id, LogonAction declaration) : ILogonAction
{
    /// <summary>Bound on the existence check. A slow index costs the pins, never the logon.</summary>
    private static readonly TimeSpan ExistenceCheckBound = TimeSpan.FromSeconds(10);

    /// <inheritdoc />
    public string Id => id;

    /// <inheritdoc />
    public LogonActionMode Mode => declaration.Mode;

    /// <inheritdoc />
    public int Order => declaration.Order;

    /// <summary>The declaration this action interprets — exposed so tests and diagnostics can see
    /// what a node actually asked for without re-reading it.</summary>
    public LogonAction Declaration => declaration;

    /// <inheritdoc />
    public IObservable<LogonActionOutcome> Run(LogonActionContext context) =>
        ResolveExistingTargets(context)
            .Select(pinnable => pinnable.Count == 0 && declaration.UnpinPaths.Count == 0
                ? LogonActionOutcome.Nothing
                : LogonActionOutcome.Profile(user => Apply(user, pinnable)));

    /// <summary>
    /// The pure profile transform: drop the declared unpins, then append the declared pins that are
    /// not already there. Pure and total, because the runner may re-run it against fresher state
    /// when the owning hub rebases a stale patch.
    /// </summary>
    internal User Apply(User user, IReadOnlyCollection<string> pinnable)
    {
        var remaining = user.PinnedPaths
            .Where(p => !declaration.UnpinPaths.Any(u => string.Equals(u, p, StringComparison.OrdinalIgnoreCase)))
            .ToImmutableList();

        var toAdd = pinnable
            .Where(p => !remaining.Any(existing => string.Equals(existing, p, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var updated = remaining.AddRange(toAdd);
        // Reference-equal when nothing moved, which is how the runner decides not to write at all.
        return updated.SequenceEqual(user.PinnedPaths, StringComparer.OrdinalIgnoreCase)
            ? user
            : user with { PinnedPaths = updated };
    }

    /// <summary>
    /// Which of the declared pin targets this deployment actually has.
    ///
    /// <para>One query for the whole set, not one per path — a per-path point read would activate a
    /// hub per target on the logon path. <c>QueryAsync</c> is the lagged index and would be wrong
    /// for reading a node's CONTENT, but "does a node exist at this path" is precisely what the
    /// index is for (<c>Doc/Architecture/CqrsAndContentAccess</c> → valid query uses).</para>
    /// </summary>
    private IObservable<IReadOnlyCollection<string>> ResolveExistingTargets(LogonActionContext context)
    {
        var wanted = declaration.PinPaths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (wanted.Length == 0 || !declaration.RequireTargetsExist)
            return Observable.Return<IReadOnlyCollection<string>>(wanted);

        var mesh = context.Hub.ServiceProvider.GetService<IMeshService>();
        var logger = context.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.Logon.PinMigration");
        if (mesh is null)
            return Observable.Return<IReadOnlyCollection<string>>([]);

        // Runs as the LOGGING-ON USER, so the check is "exists AND this user may see it" — the only
        // question worth asking before pinning something to their home. A target they cannot read
        // would render as an empty card, which is the same broken tile as a dangling path.
        return wanted
            .Select(path => mesh
                .Query<MeshNode>(MeshQueryRequest.FromQuery($"path:{path} select:path"))
                .Where(change => change.ChangeType == QueryChangeType.Initial)
                .Select(change => change.Items.Any() ? path : null)
                .Take(1))
            .Merge()
            .Where(path => path is not null)
            .Select(path => path!)
            .ToArray()
            .Timeout(ExistenceCheckBound)
            .Select(found =>
            {
                // Order by the DECLARATION, not by which query answered first — the pins are an
                // ordered list the admin wrote, and a home that reorders itself per logon is a bug.
                var present = found.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
                var kept = wanted.Where(present.Contains).ToArray();
                if (kept.Length != wanted.Length)
                    logger?.LogInformation(
                        "Logon action {Action}: {Missing} of {Total} pin target(s) are not on this "
                        + "deployment and were skipped ({Paths})",
                        id, wanted.Length - kept.Length, wanted.Length,
                        string.Join(", ", wanted.Except(kept, StringComparer.OrdinalIgnoreCase)));
                return (IReadOnlyCollection<string>)kept;
            })
            .Catch<IReadOnlyCollection<string>, Exception>(ex =>
            {
                // A failed existence check must NOT fall through to pinning everything blindly —
                // that is the dangling-pin outcome the check exists to prevent. Pin nothing, and
                // let the action be retried: it is not recorded, because Run never emits.
                logger?.LogWarning(ex,
                    "Logon action {Action}: pin targets could not be resolved; nothing pinned", id);
                return Observable.Throw<IReadOnlyCollection<string>>(ex);
            });
    }
}
