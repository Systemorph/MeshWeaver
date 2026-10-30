using System.Collections.Immutable;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The scopes an API token (<c>mw_…</c>) can be limited to — an OPEN vocabulary of string
/// constants (policy <c>open-vocabulary-string-constants</c>), never an enum.
///
/// <para><b>A scope is a CAP, never a grant.</b> A token that carries scopes can do at most what
/// those scopes allow, and within that still only what its owner's live grants allow. A token
/// with NO scopes (<c>null</c> or empty — every token minted before scopes existed) is unscoped
/// and behaves exactly as before: the owner's full authority over the API surface.</para>
///
/// <para><b>The set is open.</b> These constants are the platform's starting set. A module that
/// owns a surface declares its own scope value and checks it at its own enforcement site with
/// <see cref="Allows"/> / <see cref="Refusal"/> — no registration and no change to core. A value
/// that no site claims grants nothing anywhere: a token carrying only <c>"bogus"</c> is refused
/// everywhere, and every refusal NAMES the scopes the token carries. An unknown value is never
/// read as "all", and is never rejected merely for not being one of these constants (that would
/// re-close the set).</para>
///
/// <para>Two kinds of scope, enforced at two kinds of seam:</para>
/// <list type="bullet">
/// <item><b>Node scopes</b> (<see cref="Read"/>, <see cref="Write"/>) cap the node permissions
/// the access layer computes — the permission fold, a node type's own access rule, and the RLS
/// validator all apply <see cref="PermissionCap(IReadOnlyCollection{string})"/>, so a scoped token is capped on EVERY surface
/// that reads or writes nodes (MCP tools, REST, a transport connection, a layout area).</item>
/// <item><b>Surface scopes</b> (<see cref="Mcp"/>, <see cref="Api"/>, <see cref="Relay"/>,
/// <see cref="Tokens"/>) are checked by the site that authenticates or serves that surface.</item>
/// </list>
///
/// <para>See Doc/Architecture/AccessControl → "Scoped API tokens".</para>
/// </summary>
public static class ApiTokenScope
{
    /// <summary>Read nodes (and export what may be read). Caps node permissions to Read.</summary>
    public const string Read = "read";

    /// <summary>Create, update and delete nodes, comment, start threads and execute — the node
    /// writes. Does not imply <see cref="Read"/>.</summary>
    public const string Write = "write";

    /// <summary>Call the MCP endpoint at all. Which nodes the tools then reach is capped by the
    /// node scopes.</summary>
    public const string Mcp = "mcp";

    /// <summary>Call the portal's REST endpoints (everything under the API surface that is not
    /// MCP). Which nodes they then reach is capped by the node scopes.</summary>
    public const string Api = "api";

    /// <summary>Authenticate a transport connection (SignalR / gRPC), claim the device-relay
    /// address of the token's OWN owner and answer the requests routed to it. Grants no node
    /// access by itself.</summary>
    public const string Relay = "relay";

    /// <summary>Mint, change or revoke API tokens. A scoped token holding it may still only mint
    /// tokens whose scopes are a non-empty subset of its own (see <see cref="MintRefusal"/>).</summary>
    public const string Tokens = "tokens";

    /// <summary>
    /// The claim type that carries one scope on a principal built from a token (one claim per
    /// scope). NOT a scope value. A site reads it only off an identity of the API-token
    /// authentication scheme, so no other sign-in can assert a scope — and a principal that carries
    /// none of these claims is unscoped.
    /// </summary>
    public const string ScopeClaimType = "mw_scope";

    /// <summary>
    /// The node type of an API-token record (and of its index rows) — the records the
    /// <see cref="Tokens"/> rule protects. NOT a scope value.
    /// </summary>
    public const string TokenRecordNodeType = "ApiToken";

    /// <summary>
    /// Whether <paramref name="nodeType"/> names an API-token record. Case-insensitive, because node
    /// type resolution is: a record written as <c>apitoken</c> is still a token record.
    /// </summary>
    /// <param name="nodeType">A node's type.</param>
    public static bool IsTokenRecordType(string? nodeType)
        => string.Equals(nodeType, TokenRecordNodeType, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The node permissions each of the platform's NODE scopes allows. Immutable constant lookup;
    /// a value absent here contributes nothing to <see cref="PermissionCap(IReadOnlyCollection{string})"/> — never everything.
    /// <see cref="Permission.Api"/> rides along with both, because the API-token clamp demands it
    /// of every token operation and the cap must not undo the clamp's own answer.
    /// <see cref="Permission.Sync"/> and <see cref="Permission.Compile"/> are reachable by no
    /// scope: a scoped token never carries the privileged grants.
    /// </summary>
    private static readonly ImmutableDictionary<string, Permission> NodePermissions =
        ImmutableDictionary.CreateRange(StringComparer.Ordinal, new[]
        {
            KeyValuePair.Create(Read, Permission.Read | Permission.Export | Permission.Api),
            KeyValuePair.Create(Write, Permission.Create | Permission.Update | Permission.Delete
                                       | Permission.Comment | Permission.Thread | Permission.Execute
                                       | Permission.Api),
        });

    /// <summary>Whether <paramref name="scopes"/> limits a token at all: non-null and non-empty.</summary>
    /// <param name="scopes">A token's scope list.</param>
    public static bool IsScoped(IReadOnlyCollection<string>? scopes) => scopes is { Count: > 0 };

    /// <summary>Whether <paramref name="context"/> carries a scope cap (see <see cref="AccessContext.Scopes"/>).</summary>
    /// <param name="context">The caller's context; null is unscoped.</param>
    public static bool IsScoped(AccessContext? context) => IsScoped(context?.Scopes);

    /// <summary>
    /// The node permissions a scoped caller may hold at most, or <c>null</c> when the caller is
    /// unscoped (no cap). The union of what each carried NODE scope allows; scopes that claim no
    /// node permission (surface scopes, and values no site claims) contribute nothing.
    /// </summary>
    /// <param name="scopes">A token's scope list.</param>
    /// <returns>The cap, or null for no cap.</returns>
    public static Permission? PermissionCap(IReadOnlyCollection<string>? scopes)
    {
        if (!IsScoped(scopes))
            return null;
        var cap = Permission.None;
        foreach (var scope in scopes!)
            if (scope is not null && NodePermissions.TryGetValue(scope, out var allowed))
                cap |= allowed;
        return cap;
    }

    /// <summary><see cref="PermissionCap(IReadOnlyCollection{string})"/> of the context's scopes.</summary>
    /// <param name="context">The caller's context; null is unscoped.</param>
    public static Permission? PermissionCap(AccessContext? context) => PermissionCap(context?.Scopes);

    /// <summary>
    /// <paramref name="permissions"/> capped by the caller's scopes — unchanged for an unscoped
    /// caller.
    /// </summary>
    /// <param name="context">The caller's context; null is unscoped.</param>
    /// <param name="permissions">The permissions computed without the cap.</param>
    public static Permission Cap(AccessContext? context, Permission permissions)
        => PermissionCap(context) is { } cap ? permissions & cap : permissions;

    /// <summary>
    /// Whether the scope cap lets <paramref name="context"/> hold <paramref name="permission"/>
    /// (every flag of it). Always true for an unscoped caller and for <see cref="Permission.None"/>.
    /// </summary>
    /// <param name="context">The caller's context; null is unscoped.</param>
    /// <param name="permission">The permission demanded.</param>
    public static bool AllowsPermission(AccessContext? context, Permission permission)
        => permission == Permission.None
           || PermissionCap(context) is not { } cap
           || (cap & permission) == permission;

    /// <summary>The node scope that would allow <paramref name="permission"/> (for naming it in a
    /// refusal), or <c>null</c> when no platform scope does.</summary>
    /// <param name="permission">The permission demanded.</param>
    public static string? ScopeFor(Permission permission)
    {
        foreach (var (scope, allowed) in NodePermissions)
            if (permission != Permission.None && (allowed & permission) == permission)
                return scope;
        return null;
    }

    /// <summary>
    /// Whether <paramref name="context"/> may use the surface guarded by <paramref name="scope"/>:
    /// always for an unscoped caller, otherwise only when the token carries exactly that value.
    /// </summary>
    /// <param name="context">The caller's context; null is unscoped.</param>
    /// <param name="scope">The scope the surface requires — the site's OWN value.</param>
    public static bool Allows(AccessContext? context, string scope)
        => !IsScoped(context) || context!.Scopes!.Contains(scope, StringComparer.Ordinal);

    /// <summary>
    /// <c>null</c> when <paramref name="context"/> may use the surface guarded by
    /// <paramref name="scope"/>; otherwise the refusal, naming the scope required and every scope
    /// the token carries. Machine-facing (an HTTP body, a log line) — a user-facing seam localizes
    /// its own message.
    /// </summary>
    /// <param name="context">The caller's context; null is unscoped.</param>
    /// <param name="scope">The scope the surface requires.</param>
    public static string? Refusal(AccessContext? context, string scope)
        => Allows(context, scope) ? null : Describe(scope, context!.Scopes!);

    /// <summary>The refusal text for a scoped token that lacks <paramref name="required"/>.</summary>
    /// <param name="required">The scope required.</param>
    /// <param name="carried">The scopes the token carries.</param>
    public static string Describe(string required, IReadOnlyCollection<string> carried)
        => $"This API token is limited to the scopes [{Format(carried)}]; this needs the scope '{required}'.";

    /// <summary>The carried scopes as a stable, comma-separated list (for messages).</summary>
    /// <param name="scopes">The scopes.</param>
    public static string Format(IReadOnlyCollection<string>? scopes)
        => scopes is null ? string.Empty : string.Join(", ", scopes.Select(s => $"'{s}'"));

    /// <summary>
    /// Whether <paramref name="minter"/> may write an API token carrying
    /// <paramref name="requested"/>: <c>null</c> when it may, otherwise the refusal. An unscoped
    /// minter is unrestricted (today's behaviour). A scoped minter needs <see cref="Tokens"/>, and
    /// the token it writes must itself be scoped, to a subset of the minter's scopes — so a capped
    /// token can never mint, or rewrite itself into, a wider one.
    /// </summary>
    /// <param name="minter">The context writing the token record.</param>
    /// <param name="requested">The scopes on the token record being written.</param>
    public static string? MintRefusal(AccessContext? minter, IReadOnlyCollection<string>? requested)
        => MintScopeRefusal(minter, requested);

    /// <summary>
    /// <see cref="MintRefusal"/> for a whole token
    /// record: a scoped minter may write only a token for ITS OWN principal (review of #7585) — never
    /// one naming another person, System or any other infrastructure identity (which the fold and RLS
    /// admit before any cap), and never a service credential. Otherwise a narrow-scoped mint would
    /// authenticate as whoever the record names. An unscoped minter is unrestricted, as before.
    /// </summary>
    /// <param name="minter">The context writing the token record.</param>
    /// <param name="requested">The scopes on the token record being written.</param>
    /// <param name="owner">The principal the record names (<c>ApiToken.UserId</c>).</param>
    /// <param name="serviceIdentityPath">The record's service identity path, if any.</param>
    public static string? RecordMintRefusal(AccessContext? minter, IReadOnlyCollection<string>? requested,
        string? owner, string? serviceIdentityPath)
    {
        if (IsScoped(minter)
            && (!string.Equals(owner, minter!.ObjectId, StringComparison.Ordinal)
                || string.IsNullOrEmpty(minter.ObjectId)
                || !string.IsNullOrEmpty(serviceIdentityPath)))
            return $"This API token is limited to the scopes [{Format(minter.Scopes)}]; it can write a token only "
                   + $"for its own principal '{minter.ObjectId}', never for '{owner}'"
                   + (string.IsNullOrEmpty(serviceIdentityPath) ? "." : " or a service identity.");
        return MintScopeRefusal(minter, requested);
    }

    private static string? MintScopeRefusal(AccessContext? minter, IReadOnlyCollection<string>? requested)
    {
        if (!IsScoped(minter))
            return null;
        if (Refusal(minter, Tokens) is { } refusal)
            return refusal;
        if (!IsScoped(requested))
            return $"This API token is limited to the scopes [{Format(minter!.Scopes)}]; it cannot write an unscoped token.";
        var wider = requested!.Where(s => !minter!.Scopes!.Contains(s, StringComparer.Ordinal)).ToArray();
        return wider.Length == 0
            ? null
            : $"This API token is limited to the scopes [{Format(minter!.Scopes)}]; it cannot write a token carrying [{Format(wider)}].";
    }
}
