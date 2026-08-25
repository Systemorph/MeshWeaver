using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Logon;

/// <summary>
/// Announces <see cref="UserSignedIn"/> to every registered
/// <see cref="SignInNotificationTargets">subscriber</see>, once per sign-in.
///
/// <para><b>Fire-and-forget, and that is the design.</b> Each address gets a <c>Post</c>; nothing is
/// observed, no response type exists, and a subscriber that is absent, slow or throwing costs the
/// signing-in user nothing. Sign-in is on the critical path — the moment it can be held up by
/// another partition's health, a Store outage becomes a login outage.</para>
///
/// <para><b>EveryLogon.</b> A run-once announcement is a contradiction: subscribers exist to react
/// to each sign-in, and a ledger entry would silence every one after the first. There is nothing to
/// make idempotent here because core writes nothing — what a subscriber does with the event is its
/// own business, including deciding it has already done it.</para>
/// </summary>
public sealed class AnnounceSignInLogonAction : ILogonAction
{
    /// <inheritdoc />
    public string Id => "announce-sign-in";

    /// <inheritdoc />
    public LogonActionMode Mode => LogonActionMode.EveryLogon;

    /// <summary>Last. Core's own per-user work (seeding, icon repair) settles first, so a
    /// subscriber reacting to the announcement sees a coherent set of records rather than one
    /// mid-repair. Not a correctness requirement — the event carries no state — but it removes an
    /// ordering question a subscriber would otherwise have to think about.</summary>
    public int Order => int.MaxValue;

    /// <inheritdoc />
    public IObservable<LogonActionOutcome> Run(LogonActionContext context)
    {
        var registry = context.Hub.ServiceProvider.GetService<SignInNotificationTargets>();
        var logger = context.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.Logon.AnnounceSignIn");
        var targets = registry?.Targets;
        if (targets is not { Count: > 0 })
            return Observable.Return(LogonActionOutcome.Nothing);

        var announcement = new UserSignedIn(context.UserPath);
        foreach (var target in targets)
        {
            try
            {
                context.Hub.Post(announcement, o => o.WithTarget(target));
            }
            catch (Exception exception)
            {
                // A throw from Post is a routing problem, not the user's problem. Debug, not
                // Warning: on a portal where a subscriber's partition is simply absent this would
                // otherwise be a line on every single sign-in.
                logger?.LogDebug(exception, "Announcing sign-in to {Target} failed", target);
            }
        }

        return Observable.Return(LogonActionOutcome.Nothing);
    }
}
