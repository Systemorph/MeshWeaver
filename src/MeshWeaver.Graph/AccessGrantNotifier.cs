using System.Reactive;
using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Watches the mesh change feed for newly-created <c>AccessAssignment</c> nodes and notifies the
/// granted user — "You've been given &lt;role&gt; access to &lt;node&gt;" — with a link to the node.
/// This is the ONE place that reacts to grants, so it covers every grant path (the access-control
/// tab, a Space's "Invite people" for an existing user, MCP, the event-subscription grant on
/// sign-up) without touching each call site.
///
/// <para>Delivery goes through <see cref="NotificationService.Dispatch"/>, so it honours the
/// recipient's <see cref="NotificationSettings"/> (bell and/or email for the
/// <see cref="NotificationCategory.AccessGranted"/> category). Runs as System (the change feed has
/// no user context). Only <b>grants</b> (a non-denied role) to a real <see cref="Mesh.Security.User"/>
/// notify — denials and group/role subjects are skipped, and a self-grant (creator granting
/// themselves, e.g. on space creation) is suppressed so it is never noise. Modelled on
/// <see cref="EventSubscriptionRunner"/>.</para>
/// </summary>
public sealed class AccessGrantNotifier(
    IMessageHub hub,
    IMeshChangeFeed changeFeed,
    AccessService accessService,
    ILogger<AccessGrantNotifier>? logger = null) : IHostedService, IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private IDisposable? subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Live-only feed (no history replay): historical/seed grants written before this subscription
        // never fire, so a restart does not re-notify. See EventSubscriptionRunner.
        subscription = changeFeed.Subscribe(OnCreated, MeshChangeKind.Created);
        logger?.LogInformation("AccessGrantNotifier: watching AccessAssignment creations");
        return Task.CompletedTask;
    }

    private void OnCreated(MeshChangeEvent e)
    {
        if (!string.Equals(e.NodeType, AccessAssignmentNodeType.NodeType, StringComparison.Ordinal))
            return;
        AsSystem(() => hub.GetMeshNode(e.Path, ReadTimeout))
            .SelectMany(Handle)
            .Subscribe(
                _ => { },
                ex => logger?.LogWarning(ex, "AccessGrantNotifier: failed for {Path}", e.Path));
    }

    private IObservable<Unit> Handle(MeshNode? assignmentNode)
    {
        if (assignmentNode is null
            || !TryResolveGrant(assignmentNode, hub.JsonSerializerOptions,
                out var recipient, out var grantedNodePath, out var roleText))
            return Observable.Return(Unit.Default);

        // Notify only real users (a group/role subject has no User content → skip).
        return AsSystem(() => hub.GetMeshNode(recipient, ReadTimeout)).SelectMany(userNode =>
        {
            if (userNode?.ContentAs<User>(hub.JsonSerializerOptions) is null)
                return Observable.Return(Unit.Default);

            return AsSystem(() => hub.GetMeshNode(grantedNodePath, ReadTimeout)).SelectMany(grantedNode =>
            {
                var name = string.IsNullOrWhiteSpace(grantedNode?.Name) ? grantedNodePath : grantedNode!.Name;
                // Resolve the granter's display name so the recipient sees WHO shared it — for most
                // invitees this email is their first contact with Memex, and "someone gave you access"
                // with no name is exactly the anonymous, off-putting message we're fixing. A granter
                // that can't be resolved (or is System) falls back to a name-less phrasing. Skip the
                // lookup entirely when there is no granter id (no empty-path NotFound churn).
                var granterLookup = string.IsNullOrEmpty(assignmentNode.CreatedBy)
                    ? Observable.Return<MeshNode?>(null)
                    : AsSystem(() => hub.GetMeshNode(assignmentNode.CreatedBy!, ReadTimeout))
                        .Catch(Observable.Return<MeshNode?>(null));
                return granterLookup
                    .SelectMany(granterNode =>
                    {
                        var granter = ResolveGranterName(granterNode, assignmentNode.CreatedBy, hub.JsonSerializerOptions);
                        var message = granter is null
                            ? $"You now have {roleText} access to \"{name}\"."
                            : $"{granter} gave you {roleText} access to \"{name}\".";
                        return NotificationService.Dispatch(
                            hub,
                            recipient: recipient,
                            mainNodePath: recipient,
                            title: $"You've been given access to {name}",
                            message: message,
                            type: NotificationType.AccessGranted,
                            targetNodePath: grantedNodePath,
                            createdBy: assignmentNode.CreatedBy,
                            icon: "/static/NodeTypeIcons/shield.svg",
                            emailCtaLabel: $"Open {name}",
                            // First-contact hint — this recipient may have never signed in. Passed
                            // explicitly (not defaulted in NotificationService) so it never leaks onto
                            // notifications aimed at already-signed-in users.
                            emailFooterNote: "New to Memex? Sign in with this email address to open it.");
                    });
            });
        });
    }

    /// <summary>
    /// Pure decision: should the created <paramref name="assignmentNode"/> raise an access-granted
    /// notification, and to whom / for what? Returns <c>true</c> only for an actual grant (a
    /// non-denied role) that is NOT a self-grant (creator == subject) and carries a target node.
    /// Does not resolve whether the subject is a user (that needs a read). Pure + unit-testable.
    /// </summary>
    internal static bool TryResolveGrant(
        MeshNode assignmentNode, System.Text.Json.JsonSerializerOptions options,
        out string recipient, out string grantedNodePath, out string roleText)
    {
        recipient = grantedNodePath = roleText = "";
        var assignment = assignmentNode.ContentAs<AccessAssignment>(options);
        if (assignment is null || string.IsNullOrEmpty(assignment.AccessObject))
            return false;

        // Only actual grants (a non-denied role) — never notify about a denial.
        var grantedRoles = (assignment.Roles ?? [])
            .Where(r => !r.Denied && !string.IsNullOrEmpty(r.Role))
            .Select(r => r.Role)
            .ToList();
        if (grantedRoles.Count == 0)
            return false;

        // Suppress self-grants (e.g. the space creator's own Admin assignment) — not noise-worthy.
        if (string.Equals(assignmentNode.CreatedBy, assignment.AccessObject, StringComparison.Ordinal))
            return false;

        if (string.IsNullOrEmpty(assignmentNode.MainNode))
            return false;

        recipient = assignment.AccessObject;
        grantedNodePath = assignmentNode.MainNode;
        roleText = string.Join(", ", grantedRoles);
        return true;
    }

    /// <summary>
    /// A human display name for the granter, or <c>null</c> when none can be shown (so the message
    /// omits the "granted by" clause rather than printing a raw ObjectId). Prefers the granter node's
    /// display <c>Name</c>, then the <see cref="User"/>'s <c>FullName</c>/<c>Email</c>; anything that
    /// merely echoes the <paramref name="granterId"/> ObjectId is treated as "no name".
    /// </summary>
    internal static string? ResolveGranterName(
        MeshNode? granterNode, string? granterId, System.Text.Json.JsonSerializerOptions options)
    {
        if (granterNode is null)
            return null;
        var id = granterId?.Trim();
        // Trim BEFORE comparing so a stored name that is the ObjectId with incidental whitespace is
        // still treated as "just the id" (never leak the raw ObjectId to the recipient).
        string? Usable(string? s)
        {
            var t = s?.Trim();
            return !string.IsNullOrEmpty(t) && !string.Equals(t, id, StringComparison.Ordinal) ? t : null;
        }

        if (Usable(granterNode.Name) is { } nodeName)
            return nodeName;
        var user = granterNode.ContentAs<User>(options);
        if (Usable(user?.FullName) is { } fullName)
            return fullName;
        if (Usable(user?.Email) is { } email)
            return email;
        return null;
    }

    private IObservable<T> AsSystem<T>(Func<IObservable<T>> factory)
        => Observable.Using(accessService.ImpersonateAsSystem, _ => Observable.Defer(factory));

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => subscription?.Dispose();
}
