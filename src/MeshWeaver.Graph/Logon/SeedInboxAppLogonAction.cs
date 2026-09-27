using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Logon;

/// <summary>
/// Installs the <b>Inbox app</b> (<c>{user}/_App/Inbox</c>, opening <see cref="InboxLayoutArea"/>) for
/// every user, exactly once — the first administrator of a fresh instance and every invited user alike.
///
/// <para><b>Why a run-once action of its own, not a <c>HomeConfig.DefaultApps</c> entry.</b> The default
/// list is the DEPLOYMENT's to edit (<c>Admin/HomeConfig</c>), and an instance that has configured its
/// own list would silently never seed an entry added to the shipped defaults. The Inbox is not a
/// per-deployment choice: it is where "something needs you" lands. So it is seeded independently, and a
/// user who removes the tile keeps it removed — the ledger says the action ran, nothing re-creates it.
/// Because the ledger key is new, a person who existed before this action shipped gets the tile on
/// their next sign-in, once.</para>
///
/// <para>🚨 <b>Create-if-absent, never overwrite</b>, exactly like <see cref="SeedDefaultAppsLogonAction"/>:
/// an "already exists" is success (a concurrent logon, or a deployment that lists <c>~/Inbox</c> in its
/// own defaults, wrote it first), every other failure propagates so the unrecorded action retries on
/// the next logon.</para>
/// </summary>
public sealed class SeedInboxAppLogonAction : ILogonAction
{
    /// <summary>🚨 The ledger key. Changing it re-seeds the tile for everyone, including people who
    /// deliberately removed it.</summary>
    public string Id => "seed-inbox-app";

    /// <inheritdoc />
    public LogonActionMode Mode => LogonActionMode.RunOnce;

    /// <summary>Right after the default apps, before the icon adoption that operates on records.</summary>
    public int Order => -99;

    /// <inheritdoc />
    public IObservable<LogonActionOutcome> Run(LogonActionContext context)
    {
        var mesh = context.Hub.ServiceProvider.GetService<IMeshService>();
        var logger = context.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.Logon.SeedInboxApp");
        var ownerId = context.UserPath;
        if (mesh is null || string.IsNullOrEmpty(ownerId))
            return Observable.Return(LogonActionOutcome.Nothing);

        var node = UserActivityLayoutAreas.BuildAppRecord(
            ownerId, UserActivityLayoutAreas.InboxAppSpec(ownerId, context.Identity.Locale));
        var access = context.Hub.ServiceProvider.GetService<AccessService>();
        // Re-establish the identity AT THE WRITE, as LogonActionRunner.Commit does: the runner
        // Concats the actions, so this cold create may be subscribed on whichever thread the previous
        // action completed on, after RunFor's ambient scope is gone — and CreateNode captures the
        // context when it is subscribed, so a write with none fails CLOSED (the tile would never land
        // and the unrecorded action would retry on every logon).
        return access.RunAs(context.Identity, () => mesh.CreateNode(node))
            .Select(_ => LogonActionOutcome.Nothing)
            .Catch((Exception exception) =>
            {
                if (exception.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                {
                    logger?.LogDebug("Inbox app record {Path} already existed", node.Path);
                    return Observable.Return(LogonActionOutcome.Nothing);
                }
                logger?.LogWarning(exception, "Inbox app record create failed at {Path}", node.Path);
                return Observable.Throw<LogonActionOutcome>(exception);
            });
    }
}
