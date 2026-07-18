using System;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Read-time redaction of the PII on <see cref="User"/> nodes. User nodes are world-readable
/// (<c>WithPublicRead</c>), so their content is served to every authenticated caller. The email
/// address is PII: it must be visible ONLY to the subject (the user themselves) and to global
/// admins — never to arbitrary authenticated readers (GitHub issue #471, RC1).
/// <para>
/// This is a READ PROJECTION, not a storage change. The stored node keeps the real email, so the
/// System-identity email→userId login lookup (<c>UserContextMiddleware.TryLoadMeshUser</c>, which
/// queries <c>content.email</c> under <c>ImpersonateAsHub</c> and never routes through this helper)
/// keeps resolving. The display <b>name</b> is never redacted — only the email.
/// </para>
/// <para>
/// There is deliberately no single per-caller content-projection seam in the mesh: a single-node
/// read via <c>GetMeshNodeStream</c> is a SHARED subscription — the owning hub broadcasts ONE node
/// value to every subscriber on a silo, so it cannot project per-reader. Redaction is therefore
/// applied at the read surfaces that carry the caller's identity and actually expose the email:
/// the MCP/agent single-node read (<c>MeshOperations.Get</c>) and the GUI visitor profile
/// (<c>UserActivityLayoutAreas.BuildVisitorProfile</c>). Search/enumeration returns only a
/// {path,name,nodeType,version,lastModified} projection and never carries the email.
/// </para>
/// </summary>
public static class UserPiiRedaction
{
    /// <summary>
    /// Returns <paramref name="node"/> with <see cref="User.Email"/> nulled when it is a User node
    /// carrying an email; a no-op for non-User nodes or an already-null email. Pure — never mutates
    /// the input (returns a <c>with</c>-copy), so the stored/queried node is untouched.
    /// </summary>
    public static MeshNode RedactEmail(MeshNode node, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!string.Equals(node.NodeType, UserNodeType.NodeType, StringComparison.Ordinal))
            return node;
        var user = node.ContentAs<User>(options);
        if (user is null || user.Email is null)
            return node;
        return node with { Content = user with { Email = null } };
    }

    /// <summary>
    /// Per-reader projection: emits <paramref name="node"/> unchanged when the reader is the subject
    /// of the User node or a global admin; otherwise emits it with the email redacted. Non-User
    /// nodes pass through unchanged. Single emission, reactive (no <c>await</c>).
    /// </summary>
    public static IObservable<MeshNode> RedactEmailForReader(
        IMessageHub hub, MeshNode node, AccessContext? viewer)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(node);

        var options = hub.JsonSerializerOptions;
        if (!string.Equals(node.NodeType, UserNodeType.NodeType, StringComparison.Ordinal))
            return Observable.Return(node);

        // The subject sees their own email (same owner predicate the home page uses).
        var ownerId = OwnerIdOf(node.Path);
        if (UserActivityLayoutAreas.IsViewerOwner(viewer, ownerId))
            return Observable.Return(node);

        // Anonymous / virtual readers never see it.
        var viewerId = viewer?.ObjectId;
        if (string.IsNullOrEmpty(viewerId) || viewer?.IsVirtual == true)
            return Observable.Return(RedactEmail(node, options));

        // Global admins (Permission.All at the Admin scope) see it; everyone else gets it redacted.
        // Fail-secure: a slow/faulted admin probe redacts rather than leaks.
        var redacted = RedactEmail(node, options);
        return hub.IsGlobalAdmin(viewerId)
            .Take(1)
            .Select(isAdmin => isAdmin ? node : redacted)
            .Timeout(TimeSpan.FromSeconds(5), Observable.Return(redacted))
            .Catch<MeshNode, Exception>(_ => Observable.Return(redacted));
    }

    /// <summary>Subject id from a User node path (<c>"User/Alice" → "Alice"</c>, else verbatim).</summary>
    private static string OwnerIdOf(string? path)
        => string.IsNullOrEmpty(path)
            ? string.Empty
            : path.StartsWith("User/", StringComparison.OrdinalIgnoreCase)
                ? path["User/".Length..]
                : path;
}
