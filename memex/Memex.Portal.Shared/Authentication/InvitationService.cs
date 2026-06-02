using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace Memex.Portal.Shared.Authentication;

/// <summary>
/// Reads and writes <see cref="Invitation"/> nodes for invitation-only onboarding. Invitations
/// live in the always-present <b>Admin</b> partition at <c>Admin/Invitation/{slug}</c> and are
/// made globally queryable by the path-less <c>nodeType:Invitation → Admin</c> routing rule
/// registered in <see cref="InvitationNodeType"/>.
///
/// <para><b>100% reactive — IObservable&lt;T&gt; end-to-end</b> (like <see cref="UserOnboardingService"/>).
/// Every method returns a cold observable; callers Subscribe. All writes wrap in
/// <c>Observable.Using(() =&gt; accessService.ImpersonateAsSystem(), …)</c> because the caller — the
/// onboarding user (no identity yet) or an admin who lacks rights on the Admin partition — cannot
/// write there directly. This is the same infrastructure-write pattern as
/// <see cref="UserOnboardingService.CreateUser"/>.</para>
/// </summary>
public sealed class InvitationService(
    IMeshService meshService,
    AccessService accessService,
    ILogger<InvitationService>? logger = null)
{
    /// <summary>Hard cap on the invitation lookup; matches OnboardingMiddleware's user lookup.</summary>
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Reactive lookup of an outstanding (<see cref="InvitationStatus.Pending"/>) invitation for
    /// <paramref name="email"/> via the canonical synced query (<c>workspace.GetQuery</c>, runs as
    /// System and routes to the Admin partition). Emits the matching node, or <c>null</c> when no
    /// pending invitation exists (or on timeout). Shape mirrors
    /// <c>OnboardingMiddleware.FindUserByEmail</c>.
    /// </summary>
    public IObservable<MeshNode?> FindPendingInvitation(IWorkspace workspace, string email)
    {
        var jsonOptions = workspace.Hub.JsonSerializerOptions;
        return workspace.GetQuery(
                $"invite:byEmail:{email}",
                $"nodeType:{InvitationNodeType.NodeType} content.email:{email}")
            .Take(1)
            .Select(items => (MeshNode?)items
                .FirstOrDefault(n => TryGetInvitation(n, jsonOptions) is { Status: InvitationStatus.Pending }))
            .Timeout(LookupTimeout, Observable.Defer(() =>
            {
                logger?.LogWarning(
                    "FindPendingInvitation({Email}): no snapshot within {Timeout} — treating as not invited",
                    email, LookupTimeout);
                return Observable.Return<MeshNode?>(null);
            }));
    }

    /// <summary>
    /// Creates (or overwrites, since the slug is derived from the email) a Pending invitation for
    /// <paramref name="email"/>. Returns a cold observable emitting the created node; subscribe to drive.
    /// </summary>
    public IObservable<MeshNode> CreateInvitation(string email, string? invitedBy, string? note)
    {
        var trimmed = email.Trim();
        var node = new MeshNode(Slugify(trimmed), InvitationNodeType.Namespace)
        {
            Name = trimmed,
            NodeType = InvitationNodeType.NodeType,
            State = MeshNodeState.Active,
            Content = new Invitation
            {
                Email = trimmed,
                InvitedBy = invitedBy,
                Note = string.IsNullOrWhiteSpace(note) ? null : note!.Trim(),
                Status = InvitationStatus.Pending,
            },
        };

        return Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.CreateNode(node)
                .Do(__ => logger?.LogInformation(
                    "Invitation: created for {Email} at {Path} (by {InvitedBy})",
                    trimmed, node.Path, invitedBy ?? "(unknown)")));
    }

    /// <summary>
    /// Flips an invitation to <see cref="InvitationStatus.Accepted"/> (called on successful
    /// onboarding). The caller passes the <paramref name="current"/> content it already extracted
    /// so all other fields (Id, InvitedBy, InvitedAt, Note) are preserved.
    /// </summary>
    public IObservable<MeshNode> MarkAccepted(MeshNode node, Invitation current) =>
        WriteStatus(node, current with
        {
            Status = InvitationStatus.Accepted,
            AcceptedAt = DateTimeOffset.UtcNow,
        });

    /// <summary>Flips an invitation to <see cref="InvitationStatus.Revoked"/> (admin withdraws it).</summary>
    public IObservable<MeshNode> Revoke(MeshNode node, Invitation current) =>
        WriteStatus(node, current with { Status = InvitationStatus.Revoked });

    private IObservable<MeshNode> WriteStatus(MeshNode node, Invitation updated) =>
        Observable.Using(
            () => accessService.ImpersonateAsSystem(),
            _ => meshService.UpdateNode(node with { Content = updated })
                .Do(__ => logger?.LogInformation("Invitation: {Path} → {Status}", node.Path, updated.Status)));

    /// <summary>
    /// Extracts the <see cref="Invitation"/> from a node's <c>Content</c>, deserializing from
    /// <see cref="JsonElement"/> when the synced query returned the raw JSON shape. Returns
    /// <c>null</c> when the content is absent or not an invitation.
    /// </summary>
    public static Invitation? TryGetInvitation(MeshNode node, JsonSerializerOptions? options) =>
        node.Content switch
        {
            Invitation inv => inv,
            JsonElement je => Deserialize(je, options),
            _ => null,
        };

    private static Invitation? Deserialize(JsonElement je, JsonSerializerOptions? options)
    {
        try { return JsonSerializer.Deserialize<Invitation>(je.GetRawText(), options); }
        catch { return null; }
    }

    private static string Slugify(string email) =>
        new(email.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
