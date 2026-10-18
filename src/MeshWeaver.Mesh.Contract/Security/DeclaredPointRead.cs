using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// Decides a node type's DECLARED point read (<see cref="PointReadDeclaration"/>): whether a caller
/// may read the ONE node at a path because the node's type declares it and the node names the
/// caller's audience (MeshWeaver#4306).
///
/// <para><b>Consulted only for a point read, and only after the standard check DENIED.</b> The
/// seams that serve a single node at its own path ask it on a denial: the RLS node validator for a
/// read whose context says it is a point read (<see cref="NodeValidationContext.IsPointRead"/>,
/// set by the node hub's own data read), the <c>[RequiresPermission]</c> delivery gate for a Read
/// on the hub's own path, the node-stream cache's read gate, and
/// <see cref="NodeTypeAccessRuleGate.CheckFor"/>. Its answer replaces the denial only when it
/// ADMITS. Listings and queries never ask it — a query's row filter leaves
/// <see cref="NodeValidationContext.IsPointRead"/> false, and the Postgres permission table knows
/// nothing of it — so a node admitted this way is invisible to every listing. It is not a grant row
/// and it never enters the permission fold.</para>
///
/// <para><b>Fail closed.</b> No node, a node the platform did not issue, a type the platform does
/// not own, no declaration, a revoked or expired node, an unmatched subject, an anonymous caller on
/// an instance that denies anonymous access, and every fault all answer <c>false</c>.</para>
/// </summary>
public static class DeclaredPointRead
{
    /// <summary>The <c>NodeTypeDefinition</c> property that carries the declaration.</summary>
    public const string DeclarationProperty = "pointRead";

    /// <summary>
    /// Whether <paramref name="caller"/> may point-read the node at <paramref name="path"/> through
    /// its type's declared point read. The node, its type and the type's ownership are read here,
    /// from storage — never taken from a caller-supplied context. Cold; for a group subject it stays
    /// live on the group's memberships, so a seam takes the first verdict and a page may follow it.
    /// </summary>
    /// <param name="hub">The hub asking.</param>
    /// <param name="path">The exact path being read.</param>
    /// <param name="caller">The caller's context; null or virtual is the anonymous caller.</param>
    public static IObservable<bool> Admits(IMessageHub hub, string path, AccessContext? caller)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return Observable.Defer(() =>
        {
            var userId = SubjectOf(caller);
            if (string.IsNullOrEmpty(path) || AnonymousAccess.Refuses(hub.ServiceProvider, userId, caller))
                return Observable.Return(false);
            var options = hub.JsonSerializerOptions;
            return hub.ReadStoredNode(path)
                .Take(1)
                .SelectMany(node => node is null || !IsPlatformIssued(node) || string.IsNullOrEmpty(node.NodeType)
                    ? Observable.Return(false)
                    : DeclarationOf(hub, node.NodeType!)
                        .SelectMany(declaration =>
                            declaration is not null
                            && AdmittedSubject(declaration, ContentOf(node, options), DateTimeOffset.UtcNow) is { } subject
                                ? SubjectAdmits(hub, subject, userId, caller)
                                : Observable.Return(false)))
                .DefaultIfEmpty(false)
                .Catch((Exception ex) =>
                {
                    hub.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(DeclaredPointRead))
                        .LogWarning(ex, "The declared point read of {Path} for {User} could not be decided — refused", path, userId);
                    return Observable.Return(false);
                });
        });
    }

    /// <summary>
    /// The live Read verdict of a POINT read of <paramref name="path"/> for <paramref name="userId"/>:
    /// the standard permission check, or — when that denies — the declared point read. For a page
    /// that must follow whether its viewer may still read the node it shows. Never use it to filter
    /// a listing: a listing is not a point read.
    /// </summary>
    /// <param name="hub">The hub asking.</param>
    /// <param name="path">The exact path being read.</param>
    /// <param name="userId">The viewer.</param>
    public static IObservable<bool> ObservePointRead(IMessageHub hub, string path, string userId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var caller = new AccessContext { ObjectId = userId };
        return hub.CheckPermission(path, userId, Permission.Read)
            .Select(granted => granted ? Observable.Return(true) : Admits(hub, path, caller))
            .Switch()
            .DistinctUntilChanged();
    }

    /// <summary>
    /// The subject <paramref name="declaration"/> admits on a node whose content is
    /// <paramref name="content"/>, at <paramref name="now"/>; null when it admits nobody (no subject,
    /// revoked, no expiry where one is declared, or expired). Pure.
    /// </summary>
    /// <param name="declaration">The type's declaration.</param>
    /// <param name="content">The node's content as JSON.</param>
    /// <param name="now">The instant to judge the expiry at.</param>
    public static string? AdmittedSubject(PointReadDeclaration declaration, JsonElement? content, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (content is not { ValueKind: JsonValueKind.Object } body || string.IsNullOrWhiteSpace(declaration.SubjectProperty))
            return null;
        if (!string.IsNullOrWhiteSpace(declaration.RevokedProperty)
            && body.TryGetProperty(declaration.RevokedProperty!, out var revoked)
            && revoked.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            return null;
        if (!string.IsNullOrWhiteSpace(declaration.ExpiresProperty)
            && !(body.TryGetProperty(declaration.ExpiresProperty!, out var expires)
                 && expires.ValueKind == JsonValueKind.String
                 && DateTimeOffset.TryParse(expires.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                     System.Globalization.DateTimeStyles.None, out var at)
                 && now < at))
            return null;
        return body.TryGetProperty(declaration.SubjectProperty, out var subject)
               && subject.ValueKind == JsonValueKind.String
               && subject.GetString()?.Trim() is { Length: > 0 } named
            ? named
            : null;
    }

    /// <summary>
    /// Whether <paramref name="subject"/> names <paramref name="userId"/>: <see cref="WellKnownUsers.Anonymous"/>
    /// names every caller, <see cref="WellKnownUsers.Public"/> every signed-in one, a user id that user,
    /// and any other value a group — matched through the same transitive membership expansion the
    /// permission fold uses. Live for a group.
    /// </summary>
    private static IObservable<bool> SubjectAdmits(IMessageHub hub, string subject, string userId, AccessContext? caller)
    {
        if (string.Equals(subject, WellKnownUsers.Anonymous, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(true);
        var signedIn = WellKnownUsers.IsAuthenticated(userId) && caller?.IsVirtual != true;
        if (string.Equals(subject, WellKnownUsers.Public, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(signedIn);
        if (!signedIn)
            return Observable.Return(false);
        if (string.Equals(subject, userId, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(true);
        return PermissionEvaluator.ObserveUserGroups(hub, userId)
            .Select(groups => groups.Contains(subject))
            .DistinctUntilChanged();
    }

    /// <summary>
    /// The declaration of <paramref name="nodeType"/>, read only from a type the platform OWNS: the
    /// host-declared configuration node, or a stored type node in a system-owned partition (one-way
    /// <c>_GitSync</c>). A host-declared type is authoritative — a stored node at the same path cannot
    /// replace its declaration. Null for any other type, and when no declaration is present.
    /// </summary>
    private static IObservable<PointReadDeclaration?> DeclarationOf(IMessageHub hub, string nodeType)
    {
        var options = hub.JsonSerializerOptions;
        if (hub.ServiceProvider.FindServedStaticNode(nodeType) is { } declared)
            return Observable.Return(Parse(declared, options));
        var partition = nodeType.Split('/', 2)[0];
        if (partition.Length == 0)
            return Observable.Return<PointReadDeclaration?>(null);
        return hub.ReadStoredNode(AccessAssignmentGuard.SyncConfigPath(partition))
            .Take(1)
            .DefaultIfEmpty(null)
            .SelectMany(sync => AccessAssignmentGuard.IsSystemOwned(sync, options)
                ? hub.ReadStoredNode(nodeType).Take(1).DefaultIfEmpty(null).Select(type => type is null ? null : Parse(type, options))
                : Observable.Return<PointReadDeclaration?>(null));
    }

    private static PointReadDeclaration? Parse(MeshNode typeNode, JsonSerializerOptions options)
        => ContentOf(typeNode, options) is { ValueKind: JsonValueKind.Object } content
           && content.TryGetProperty(DeclarationProperty, out var declaration)
           && declaration.ValueKind == JsonValueKind.Object
            ? declaration.Deserialize<PointReadDeclaration>(options)
            : null;

    /// <summary>Whether the node's last write was made by the platform's System identity — the only issuer a declared point read admits. Pure.</summary>
    /// <param name="node">The stored node.</param>
    public static bool IsPlatformIssued(MeshNode node)
        => string.Equals(node.LastModifiedBy?.Trim(), WellKnownUsers.System, StringComparison.OrdinalIgnoreCase);

    private static JsonElement? ContentOf(MeshNode node, JsonSerializerOptions options)
    {
        try
        {
            return node.Content switch
            {
                null => null,
                JsonElement element => element,
                var typed => JsonSerializer.SerializeToElement(typed, typed.GetType(), options),
            };
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    private static string SubjectOf(AccessContext? caller)
        => caller is null || caller.IsVirtual || string.IsNullOrEmpty(caller.ObjectId)
            ? WellKnownUsers.Anonymous
            : caller.ObjectId;
}
