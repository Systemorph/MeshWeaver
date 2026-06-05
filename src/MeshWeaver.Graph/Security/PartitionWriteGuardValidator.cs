using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph.Security;

/// <summary>
/// Structural write guard that enforces two partition invariants <b>independently of
/// per-node permissions</b> — it runs alongside <see cref="RlsNodeValidator"/> in the
/// create/update validation chain and a rejection here blocks the operation even when
/// RLS would have granted it (validators compose with AND semantics: the first failure
/// wins).
///
/// <list type="number">
///   <item><b>System-managed mirror partitions are middleware-only.</b> The <c>User</c>
///     and <c>Auth</c> partitions are the auth-lookup mirror (User / Group / Role / VUser /
///     ApiToken rows, copied there by the V27 <c>mirror_access_object_to_auth_schema</c>
///     trigger and by onboarding under <c>ImpersonateAsSystem</c>). No interactive user —
///     not even a platform admin — writes content into them. Without this guard a path
///     like <c>User/{user}/SomeNodeType</c> slips through the legacy <c>User/{user}/…</c>
///     passthrough in <c>UserNodeType.UserAccessRule</c> and lands a content node in the
///     mirror schema (the <c>ReinsuranceContractCheck</c> incident).</item>
///   <item><b>No implicit space creation ("no partition, no write").</b> A normal user
///     write whose top-level partition does not already exist is rejected, rather than
///     silently provisioning a brand-new Postgres schema on first touch. Spaces are
///     created <i>explicitly</i> via the <c>Space</c> node type (which provisions the
///     partition under System before the root write); writing never creates a space as a
///     side effect.</item>
/// </list>
///
/// <para><b>Exemptions</b> (all legitimate paths that must keep working):
/// <list type="bullet">
///   <item><see cref="WellKnownUsers.System"/> — middleware, onboarding, eager partition
///     provisioning. This is exactly the identity that writes the mirror and creates new
///     partition schemas; it bypasses both rules.</item>
///   <item>The caller's <b>own</b> partition (<c>{userId}/…</c>) — always exists after
///     onboarding.</item>
///   <item>Creating a <c>Space</c> — the explicit partition-creation path, owned by
///     <c>SpaceTopLevelValidator</c> which provisions the schema first.</item>
///   <item>Global-satellite namespaces (names starting with <c>_</c>, e.g. <c>_Access</c>,
///     <c>_Activity</c>) — framework partitions whose schema name differs from the
///     namespace, so the existence probe can't resolve them; they are never
///     "implicitly created spaces".</item>
/// </list></para>
///
/// <para><b>Fails open on rule 2.</b> Existence is probed via
/// <see cref="IPartitionStorageProvider.PartitionExists"/>, which emits <c>null</c>
/// when it cannot tell. Each provider only knows its own store, so the probes are combined
/// as a global OR: <b>any</b> provider answering <c>true</c> allows the write, and the guard
/// rejects for non-existence ONLY when <b>every</b> provider definitively answers <c>false</c>
/// (the partition is confirmed absent in all stores). A mix of <c>false</c> and <c>null</c> —
/// e.g. Postgres says "not my schema" while the filesystem provider that actually owns the
/// partition can't tell — is indeterminate and allows the write, so a non-owning provider can
/// never veto a partition another provider owns, and a probe hiccup can never wedge legitimate
/// writes to a real space. Rule 1 (the mirror block) is pure path+identity logic and always
/// enforced.</para>
/// </summary>
public sealed class PartitionWriteGuardValidator : INodeValidator
{
    private readonly IMessageHub _hub;
    private readonly ILogger<PartitionWriteGuardValidator> _logger;

    /// <summary>
    /// Top-level partitions written ONLY by the middleware / DB mirror trigger. Immutable
    /// constant lookup (never mutated at runtime) — case-insensitive so <c>user</c>,
    /// <c>User</c>, <c>auth</c>, <c>Auth</c> all match.
    /// </summary>
    private static readonly ImmutableHashSet<string> ReservedMirrorPartitions =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "User", "Auth");

    public PartitionWriteGuardValidator(
        IMessageHub hub,
        ILogger<PartitionWriteGuardValidator> logger)
    {
        _hub = hub;
        _logger = logger;
    }

    /// <summary>Create + Update only — reads and deletes don't provision or write content.</summary>
    public IReadOnlyCollection<NodeOperation> SupportedOperations =>
        [NodeOperation.Create, NodeOperation.Update];

    public IObservable<NodeValidationResult> Validate(NodeValidationContext context)
    {
        var userId = GetUserId(context);

        // System identity = middleware / onboarding / Space provisioning. This is the
        // path that legitimately writes the mirror AND creates new partition schemas, so
        // it bypasses both rules.
        if (string.Equals(userId, WellKnownUsers.System, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(NodeValidationResult.Valid());

        var partition = GetFirstSegment(context.Node.Path);
        if (string.IsNullOrEmpty(partition))
            return Observable.Return(NodeValidationResult.Valid());

        // Rule 1 — system-managed mirror is middleware-only. Applies to Create AND Update
        // (don't let a user edit a mirror row either) and ignores per-node permissions.
        //
        // Transitional exception: the threading / comment subsystems still emit own-scope
        // SATELLITE nodes under the legacy shape `User/{user}/_Thread|_Comment|_Notification/…`
        // (UserNodeType.UserAccessRule has a matching legacy passthrough; autocomplete maps
        // these back to the user scope). Those carry a `_`-prefixed satellite segment right
        // after the username — let them fall through to RLS (which still gates by permission)
        // so we don't break the un-migrated thread/comment flow. Everything else in the mirror
        // — standalone content nodes (the `User/rsalzmann/ReinsuranceContractCheck` incident)
        // and bare mirror-row writes — is blocked; that content belongs in the user's own
        // partition (`{username}/…`) or an explicitly-created Space.
        if (ReservedMirrorPartitions.Contains(partition))
        {
            var segments = context.Node.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var isLegacyOwnScopeSatellite = segments.Length >= 3 && segments[2].StartsWith('_');
            if (isLegacyOwnScopeSatellite)
                // Transitional thread/comment satellite — allow, RLS still gates by permission.
                // Return here: the mirror's schema name differs from the namespace (V27 renamed
                // `user`→`auth`), so it must NOT reach the Rule-2 existence probe below.
                return Observable.Return(NodeValidationResult.Valid());

            _logger.LogWarning(
                "PartitionWriteGuard: blocked {Operation} by {User} into system mirror partition '{Partition}' at {Path}",
                context.Operation, userId ?? "(anonymous)", partition, context.Node.Path);
            return Observable.Return(NodeValidationResult.Invalid(
                $"The '{partition}' partition is a system-managed lookup mirror (users, groups, roles, " +
                $"tokens) written only by the platform middleware. Cannot {context.Operation} '{context.Node.Path}' " +
                $"there. Put your content in your own space ('{userId}/…') or a Space you create.",
                NodeRejectionReason.Unauthorized));
        }

        // Rule 2 (no implicit creation) is about provisioning a NEW partition — only a
        // Create can do that. Updates always target an existing node, so skip.
        if (context.Operation != NodeOperation.Create)
            return Observable.Return(NodeValidationResult.Valid());

        // Creating a Space IS the explicit partition-creation path — SpaceTopLevelValidator
        // provisions the schema under System before the root write. Defer to it.
        if (string.Equals(context.Node.NodeType, "Space", StringComparison.OrdinalIgnoreCase))
            return Observable.Return(NodeValidationResult.Valid());

        // The caller's own partition always exists post-onboarding.
        if (!string.IsNullOrEmpty(userId)
            && string.Equals(partition, userId, StringComparison.OrdinalIgnoreCase))
            return Observable.Return(NodeValidationResult.Valid());

        // Global-satellite namespaces (_Access, _Activity, _UserActivity, _Thread, …) are
        // framework partitions whose schema name differs from the namespace, so the probe
        // can't resolve them — and they are never implicitly-created spaces. Exempt.
        if (partition.StartsWith('_'))
            return Observable.Return(NodeValidationResult.Valid());

        var providers = _hub.ServiceProvider.GetServices<IPartitionStorageProvider>().ToList();
        if (providers.Count == 0)
            return Observable.Return(NodeValidationResult.Valid());

        // Probe every provider. Reject ONLY on a definitive negative (some provider says the
        // partition does not exist and none says it does). Indeterminate / errored probes
        // (null) fall through to allow — a probe hiccup must never block a write to a space
        // that really exists.
        var probes = providers
            .Select(p => p.PartitionExists(partition)
                .Take(1)
                .Timeout(TimeSpan.FromSeconds(5))
                .Catch<bool?, Exception>(ex =>
                {
                    _logger.LogDebug(ex,
                        "PartitionWriteGuard: existence probe failed for '{Partition}' via {Provider}; treating as indeterminate",
                        partition, p.Name);
                    return Observable.Return<bool?>(null);
                }))
            .ToList();

        return Observable.CombineLatest(probes)
            .Take(1)
            .Select(results =>
            {
                // Existence is a global OR across providers: each provider only knows its OWN
                // store, so a single `false` means "not in MY store", NOT "doesn't exist
                // anywhere". A non-owning provider must never veto a partition another provider
                // owns — e.g. the Postgres provider returns `false` for the filesystem-backed
                // ACME partition, while the FileSystem provider (which actually has it) can't
                // give a definitive answer and returns `null`. So:
                //   • any `true`            → the partition exists somewhere   → allow
                //   • EVERY provider `false`→ confirmed absent in every store  → reject
                //   • anything else (some `null`, none `true`) → indeterminate → allow (fail open)
                if (results.Any(r => r == true))
                    return NodeValidationResult.Valid();

                if (results.Count > 0 && results.All(r => r == false))
                {
                    _logger.LogWarning(
                        "PartitionWriteGuard: blocked implicit creation of partition '{Partition}' by {User} at {Path}",
                        partition, userId ?? "(anonymous)", context.Node.Path);
                    return NodeValidationResult.Invalid(
                        $"Space '{partition}' does not exist. Writing never creates a space implicitly " +
                        $"(\"no partition, no write\") — create the space explicitly first, then write into it. " +
                        $"Cannot create '{context.Node.Path}'.",
                        NodeRejectionReason.InvalidPath);
                }

                // Mixed (some provider unsure) or all indeterminate → allow.
                return NodeValidationResult.Valid();
            });
    }

    /// <summary>
    /// First path segment (the top-level partition). Mirrors
    /// <c>PostgreSqlPartitionStorageProvider.GetFirstSegment</c>.
    /// </summary>
    private static string? GetFirstSegment(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = path.Trim('/');
        if (normalized.Length == 0) return null;
        var slash = normalized.IndexOf('/');
        return slash < 0 ? normalized : normalized[..slash];
    }

    /// <summary>
    /// Identity for the operation — explicit request identity first
    /// (CreatedBy/UpdatedBy), then the authenticated session context. Same precedence as
    /// <see cref="RlsNodeValidator"/> so the System bypass agrees across validators.
    /// </summary>
    private static string? GetUserId(NodeValidationContext context)
    {
        var requestUserId = context.Request switch
        {
            CreateNodeRequest createReq => createReq.CreatedBy,
            _ => null
        };
        if (!string.IsNullOrEmpty(requestUserId))
            return requestUserId;

        return string.IsNullOrEmpty(context.AccessContext?.ObjectId)
            ? null
            : context.AccessContext.ObjectId;
    }
}
