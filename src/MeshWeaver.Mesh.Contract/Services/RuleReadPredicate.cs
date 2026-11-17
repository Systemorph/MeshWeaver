using System.Collections.Immutable;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh.Services;

/// <summary>
/// WHO a <see cref="RuleReadPredicate"/> opens rows to. An OPEN vocabulary of string constants
/// (policy <c>open-vocabulary-string-constants</c>): a value no resolver knows opens NOTHING — it is
/// never read as a default subject — and is logged by name (<see cref="RuleReadPredicates.Resolve(IMessageHub, string?)"/>).
/// </summary>
public static class RuleReadSubject
{
    /// <summary>A global administrator, by the one canonical predicate <c>hub.IsGlobalAdmin(userId)</c>.</summary>
    public const string GlobalAdmin = "GlobalAdmin";

    /// <summary>
    /// The real user whose id a TOP-LEVEL string field of the row's content holds
    /// (<see cref="RuleReadPredicate.CallerField"/>) — e.g. a feedback record's <c>submittedBy</c>.
    /// </summary>
    public const string ContentFieldIsCaller = "ContentFieldIsCaller";

    /// <summary>
    /// The real user who holds <see cref="RuleReadPredicate.HeldPermission"/> on the row's OWN path, by the
    /// permission fold — e.g. a sync config readable to whoever holds Read on it, which an
    /// <see cref="INodeTypeExclusiveRead"/> rule must declare to keep its ordinary readers listing it.
    /// Applied by the store (the SQL fold over the row's path); never bound for a consumer that does not
    /// declare it supported (<see cref="RuleReadPredicates.BindSupporting"/>).
    /// </summary>
    public const string PermissionOnPath = "PermissionOnPath";

    /// <summary>
    /// The real user who holds <see cref="RuleReadPredicate.HeldPermission"/> on the row's PARTITION ROOT
    /// (the path's first segment), by the permission fold — e.g. a storage binding, readable to whoever
    /// administers (holds Update on) its partition. As <see cref="PermissionOnPath"/>, opt-in per consumer.
    /// </summary>
    public const string PermissionOnPartitionRoot = "PermissionOnPartitionRoot";

    /// <summary>
    /// The subjects every predicate consumer applies: a consumer that does not name the subjects it
    /// supports is bound only these (<see cref="RuleReadPredicates.Resolve(IMessageHub, string?)"/>).
    /// </summary>
    public static readonly ImmutableHashSet<string> Baseline =
        ImmutableHashSet.Create(StringComparer.Ordinal, GlobalAdmin, ContentFieldIsCaller);

    /// <summary>Every subject this assembly defines — what a consumer that applies all of them declares.</summary>
    public static readonly ImmutableHashSet<string> All =
        Baseline.Union([PermissionOnPath, PermissionOnPartitionRoot]);
}

/// <summary>
/// A TYPED, data-only description of a set of rows an <see cref="INodeTypeAccessRule"/> grants Read on,
/// so that a QUERY — not only a point read — returns them (Doc/Architecture/AccessControl →
/// "Rule read predicates").
///
/// <para><b>Why data and not code.</b> A rule decides a point read with <see cref="INodeTypeAccessRule.HasAccess"/>,
/// arbitrary reactive C#. A listing on Postgres is filtered inside SQL, which cannot call it, so a rule's
/// grant used to hold for <c>get</c> and vanish from <c>search</c>. A predicate is the part of the grant
/// that BOTH executors can apply: the SQL generator ORs it into the row filter, and the in-memory
/// executor (which runs <see cref="INodeTypeAccessRule.HasAccess"/> per row) needs nothing more.</para>
///
/// <para>🚨 <b>The contract: predicate ⇒ HasAccess.</b> Every row a predicate matches for a caller
/// MUST be one the rule's <see cref="INodeTypeAccessRule.HasAccess"/> grants Read on to that caller. A
/// predicate may say LESS than the rule (a listing then shows fewer rows than a point read admits — the
/// safe direction), never more. Each declaring rule pins this with a property test.</para>
///
/// <para>A row matches when ALL of: its node type equals <see cref="NodeType"/>; its path is strictly
/// below <see cref="PathPrefix"/> (when set); its partition ROOT exists and its type is not one of
/// <see cref="ExcludedPartitionRootTypes"/> (when any); and the caller is the <see cref="Subject"/>.</para>
/// </summary>
public sealed record RuleReadPredicate
{
    /// <summary>The node type the rows carry — the type the declaring rule governs Read for.</summary>
    public required string NodeType { get; init; }

    /// <summary>
    /// Optional: the row's path must START WITH this (ordinal) and be longer than it — e.g.
    /// <c>Feedback/_Submissions/</c>. Its first segment is the partition the predicate OPENS to a
    /// fan-out (<see cref="OpenedPartition"/>). Null: any path, and the predicate opens no partition by
    /// itself — it applies inside the partitions the caller's grants already reach.
    /// </summary>
    public string? PathPrefix { get; init; }

    /// <summary>
    /// Optional: the row's partition root (the node at the path's first segment) must EXIST and its node
    /// type must not be one of these — e.g. <c>User</c>/<c>VUser</c> to keep a person's home private. An
    /// absent root never matches (fail closed, as a rule that reads an unreadable root as private).
    /// </summary>
    public ImmutableArray<string> ExcludedPartitionRootTypes { get; init; } = [];

    /// <summary>Who the rows open to — one of <see cref="RuleReadSubject"/>.</summary>
    public required string Subject { get; init; }

    /// <summary>
    /// For <see cref="RuleReadSubject.ContentFieldIsCaller"/>: the TOP-LEVEL content field (as stored,
    /// e.g. <c>submittedBy</c>) whose string value must equal the caller's id, ordinally.
    /// </summary>
    public string? CallerField { get; init; }

    /// <summary>
    /// For <see cref="RuleReadSubject.PermissionOnPath"/> and <see cref="RuleReadSubject.PermissionOnPartitionRoot"/>:
    /// the ONE permission (a single flag) the caller must hold. A missing or combined value opens nothing.
    /// </summary>
    public Permission? HeldPermission { get; init; }

    /// <summary>The partition a fan-out must read for this predicate: <see cref="PathPrefix"/>'s first
    /// segment, or null when there is no prefix.</summary>
    public string? OpenedPartition => string.IsNullOrEmpty(PathPrefix)
        ? null
        : PathPrefix.Split('/', 2)[0] is { Length: > 0 } first ? first : null;
}

/// <summary>
/// A <see cref="RuleReadPredicate"/> resolved for ONE caller: the subject decided (a global admin is one,
/// or is not) and, for a content-field subject, the caller's id bound. What a SQL generator or an
/// in-memory filter applies. Never contains a predicate whose subject the caller does not satisfy.
/// </summary>
/// <param name="NodeType">The row's node type.</param>
/// <param name="PathPrefix">Strict path prefix, or null.</param>
/// <param name="ExcludedPartitionRootTypes">Partition root types that close the row.</param>
/// <param name="CallerField">Content field that must hold <paramref name="CallerId"/>, or null when the
/// subject was decided outright.</param>
/// <param name="CallerId">The caller the predicate was resolved for.</param>
public sealed record ResolvedReadPredicate(
    string NodeType,
    string? PathPrefix,
    ImmutableArray<string> ExcludedPartitionRootTypes,
    string? CallerField,
    string CallerId)
{
    /// <summary>See <see cref="RuleReadPredicate.OpenedPartition"/>.</summary>
    public string? OpenedPartition => string.IsNullOrEmpty(PathPrefix)
        ? null
        : PathPrefix.Split('/', 2)[0] is { Length: > 0 } first ? first : null;

    /// <summary>
    /// The single permission <see cref="CallerId"/> must hold, by the permission fold, on the row's path
    /// (or its partition root when <see cref="HeldOnPartitionRoot"/>) — or null when the predicate
    /// carries no permission term. Set only for a consumer that declared the subject supported.
    /// </summary>
    public Permission? HeldPermission { get; init; }

    /// <summary>Whether <see cref="HeldPermission"/> is tested on the row's partition root (its path's first
    /// segment) rather than on the row's own path.</summary>
    public bool HeldOnPartitionRoot { get; init; }

    /// <summary>
    /// Whether <paramref name="node"/> matches — the same test the SQL clause makes. Pure. A predicate
    /// with a <see cref="HeldPermission"/> term never matches here: that term needs the permission fold,
    /// which the overload taking <c>holdsPermission</c> is handed.
    /// </summary>
    /// <param name="node">The row.</param>
    /// <param name="partitionRootType">The node type of the row's partition root, or null when it does
    /// not exist (only consulted when <see cref="ExcludedPartitionRootTypes"/> is non-empty).</param>
    /// <param name="options">Serializer options for a typed content object.</param>
    public bool Matches(MeshNode node, string? partitionRootType, JsonSerializerOptions options)
        => Matches(node, partitionRootType, options, holdsPermission: null);

    /// <summary>
    /// As <see cref="Matches(MeshNode, string?, JsonSerializerOptions)"/>, with the permission fold for a
    /// <see cref="HeldPermission"/> term: <paramref name="holdsPermission"/> answers whether
    /// <see cref="CallerId"/> holds the permission on the given path. Null fails such a term CLOSED.
    /// </summary>
    /// <param name="node">The row.</param>
    /// <param name="partitionRootType">See the other overload.</param>
    /// <param name="options">Serializer options for a typed content object.</param>
    /// <param name="holdsPermission">(path, permission) → whether the caller holds it there.</param>
    public bool Matches(MeshNode node, string? partitionRootType, JsonSerializerOptions options,
        Func<string, Permission, bool>? holdsPermission)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (HeldPermission is { } held)
        {
            var target = HeldOnPartitionRoot ? (node.Path ?? "").Trim('/').Split('/', 2)[0] : node.Path ?? "";
            if (holdsPermission is null || string.IsNullOrEmpty(target) || !holdsPermission(target, held))
                return false;
        }
        return MatchesWithoutPermission(node, partitionRootType, options);
    }

    private bool MatchesWithoutPermission(MeshNode node, string? partitionRootType, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!string.Equals(node.NodeType, NodeType, StringComparison.Ordinal))
            return false;
        var path = node.Path ?? string.Empty;
        if (!string.IsNullOrEmpty(PathPrefix)
            && !(path.StartsWith(PathPrefix, StringComparison.Ordinal) && path.Length > PathPrefix.Length))
            return false;
        if (!ExcludedPartitionRootTypes.IsDefaultOrEmpty
            && (partitionRootType is null || ExcludedPartitionRootTypes.Contains(partitionRootType, StringComparer.Ordinal)))
            return false;
        return CallerField is null
               || string.Equals(ContentString(node.Content, CallerField, options), CallerId, StringComparison.Ordinal);
    }

    /// <summary>The string value of the top-level <paramref name="field"/> (exact name, as stored), or
    /// null. Pure.</summary>
    private static string? ContentString(object? content, string field, JsonSerializerOptions options)
    {
        if (content is null)
            return null;
        var json = content is JsonElement element
            ? element
            : JsonSerializer.SerializeToElement(content, content.GetType(), options);
        return json.ValueKind == JsonValueKind.Object
               && json.TryGetProperty(field, out var value)
               && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}

/// <summary>
/// Declared by an <see cref="INodeTypeAccessRule"/> whose Read grant reaches beyond the permission fold:
/// the rows it opens, as <see cref="RuleReadPredicate"/>s, so queries return what point reads admit.
/// A COMPANION interface — an access rule that does not implement it declares no predicate and is
/// unchanged. A predicate is honoured only while its declaring rule is the one that GOVERNS Read for the
/// predicate's node type (<see cref="NodeTypeAccessRuleSet.ReadPredicates"/>).
/// </summary>
public interface INodeTypeReadPredicates
{
    /// <summary>The rows this rule opens to Read. Must satisfy predicate ⇒ HasAccess (see
    /// <see cref="RuleReadPredicate"/>).</summary>
    IReadOnlyList<RuleReadPredicate> ReadPredicates { get; }
}

/// <summary>
/// Declared by an <see cref="INodeTypeAccessRule"/> whose Read decision REPLACES the permission fold in a
/// LISTING too: a grant on the row's path never lists a row of this rule's node type; only the rule's own
/// <see cref="INodeTypeReadPredicates"/> do (Doc/Architecture/AccessControl → "Rule read predicates").
///
/// <para><b>Why it exists.</b> A rule replaces the fold on a point read. Most rules only WIDEN it or
/// delegate to it, so a query that applies the fold plus the rule's predicates lists no more than a
/// point read admits. A rule that NARROWS — it refuses a caller who holds a Read grant on the node, as
/// a feed readable by its owner alone does — would be bypassed by such a query: everyone holding Read on
/// the partition would list rows the rule refuses them on a point read. A rule of that kind implements
/// this interface, and a query executor that filters inside its store (Postgres) then excludes the type
/// from the fold's arm of its row filter.</para>
///
/// <para>A marker: the declaration IS the type test, made on the governing rule under its lease
/// (<see cref="NodeTypeAccessRuleSet.ExclusiveReadNodeTypes"/>). Pair it with
/// <see cref="INodeTypeReadPredicates"/> to keep the rows the rule admits listable; without predicates
/// the type is listed to nobody but System.</para>
/// </summary>
public interface INodeTypeExclusiveRead;

/// <summary>
/// What a query that filters inside its store applies of the node types' access rules for ONE caller
/// (Doc/Architecture/AccessControl → "Rule read predicates").
/// </summary>
/// <param name="Predicates">The rows rules open to the caller (OR'd into the row filter).</param>
/// <param name="ExclusiveNodeTypes">Node types whose governing Read rule replaces the fold in a listing
/// (<see cref="INodeTypeExclusiveRead"/>): the fold's arm of the row filter never admits them, whoever the
/// caller is. Caller-independent; resolved even when <paramref name="Predicates"/> could not be.</param>
public sealed record RuleReadScope(
    IReadOnlyList<ResolvedReadPredicate> Predicates,
    IReadOnlyList<string> ExclusiveNodeTypes)
{
    /// <summary>No predicate and no exclusive type: the row filter is the fold alone.</summary>
    public static RuleReadScope Empty { get; } = new([], []);

    /// <summary>True when the scope changes nothing in a row filter.</summary>
    public bool IsEmpty => Predicates.Count == 0 && ExclusiveNodeTypes.Count == 0;
}

/// <summary>Resolution of the mesh's declared <see cref="RuleReadPredicate"/>s for one caller.</summary>
public static class RuleReadPredicates
{
    /// <summary>
    /// The node types whose governing Read rule replaces the fold in a listing
    /// (<see cref="NodeTypeAccessRuleSet.ExclusiveReadNodeTypes"/>). Synchronous and caller-independent;
    /// empty when the mesh registers no rule set.
    /// </summary>
    /// <param name="hub">The mesh hub (its services carry the rule set).</param>
    public static ImmutableArray<string> ExclusiveNodeTypes(IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return hub.ServiceProvider.GetService<NodeTypeAccessRuleSet>()?.ExclusiveReadNodeTypes()
               ?? ImmutableArray<string>.Empty;
    }

    /// <summary>
    /// The caller's <see cref="RuleReadScope"/>: <see cref="Resolve(IMessageHub, string?)"/>'s predicates and
    /// <see cref="ExclusiveNodeTypes"/>. Emits exactly once.
    /// </summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="userId">The reader.</param>
    public static IObservable<RuleReadScope> ResolveScope(IMessageHub hub, string? userId)
        => ResolveScopeSupporting(hub, userId, RuleReadSubject.Baseline);

    /// <summary>
    /// As <see cref="ResolveScope(IMessageHub, string?)"/>, for a consumer that applies the subjects in
    /// <paramref name="supportedSubjects"/> — a store whose row filter can evaluate
    /// <see cref="RuleReadSubject.PermissionOnPath"/> / <see cref="RuleReadSubject.PermissionOnPartitionRoot"/>
    /// names them here. A predicate whose subject the consumer does not support opens NOTHING for it.
    ///
    /// <para>🚨 Why opt-in: a resolved predicate a consumer cannot fully apply would be applied WITHOUT the
    /// term it does not know, i.e. to every caller. The core rules and the store that filters by them
    /// version independently, so an older store must see such a predicate as absent (fail closed), never as
    /// its node-type term alone.</para>
    /// </summary>
    /// <param name="hub">The mesh hub.</param>
    /// <param name="userId">The reader.</param>
    /// <param name="supportedSubjects">The <see cref="RuleReadSubject"/> values the consumer applies.</param>
    public static IObservable<RuleReadScope> ResolveScopeSupporting(
        IMessageHub hub, string? userId, IReadOnlySet<string> supportedSubjects)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var exclusive = ExclusiveNodeTypes(hub);
        return ResolveSupporting(hub, userId, supportedSubjects)
            .Select(predicates => new RuleReadScope(predicates, exclusive));
    }

    /// <summary>
    /// The predicates the mesh's governing Read rules declare, resolved for <paramref name="userId"/>:
    /// empty for anyone who is not a real user (anonymous, public, System — which reads unfiltered
    /// anyway — or a hub principal); a <see cref="RuleReadSubject.GlobalAdmin"/> predicate only when
    /// <c>hub.IsGlobalAdmin(userId)</c>; a <see cref="RuleReadSubject.ContentFieldIsCaller"/> predicate
    /// bound to the caller. An unknown subject resolves to nothing and is logged. Emits exactly once.
    /// </summary>
    /// <param name="hub">The mesh hub (its services carry the rule set).</param>
    /// <param name="userId">The reader.</param>
    public static IObservable<ImmutableArray<ResolvedReadPredicate>> Resolve(IMessageHub hub, string? userId)
        => ResolveSupporting(hub, userId, RuleReadSubject.Baseline);

    /// <summary>
    /// As <see cref="Resolve(IMessageHub, string?)"/>, binding only the subjects the consumer applies
    /// (<see cref="ResolveScopeSupporting"/>).
    /// </summary>
    /// <param name="hub">The mesh hub (its services carry the rule set).</param>
    /// <param name="userId">The reader.</param>
    /// <param name="supportedSubjects">The <see cref="RuleReadSubject"/> values the consumer applies.</param>
    public static IObservable<ImmutableArray<ResolvedReadPredicate>> ResolveSupporting(
        IMessageHub hub, string? userId, IReadOnlySet<string> supportedSubjects)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(supportedSubjects);
        var declared = hub.ServiceProvider.GetService<NodeTypeAccessRuleSet>()?.ReadPredicates()
                       ?? ImmutableArray<RuleReadPredicate>.Empty;
        if (declared.IsEmpty || userId is not { Length: > 0 } caller || !IsRealUser(caller))
            return Observable.Return(ImmutableArray<ResolvedReadPredicate>.Empty);
        var needsAdmin = declared.Any(p => p.Subject == RuleReadSubject.GlobalAdmin);
        return (needsAdmin ? hub.IsGlobalAdmin(caller).Take(1).DefaultIfEmpty(false) : Observable.Return(false))
            .Select(isAdmin => BindSupporting(declared, caller, isAdmin, supportedSubjects, hub));
    }

    /// <summary>
    /// <paramref name="declared"/> bound to <paramref name="caller"/>, given whether the caller is a
    /// global admin. Pure apart from logging an unknown subject.
    /// </summary>
    /// <param name="declared">The declared predicates.</param>
    /// <param name="caller">A real user id.</param>
    /// <param name="isGlobalAdmin">Whether the caller is a global admin.</param>
    /// <param name="hub">For logging an unknown subject; optional.</param>
    public static ImmutableArray<ResolvedReadPredicate> Bind(
        IEnumerable<RuleReadPredicate> declared, string caller, bool isGlobalAdmin, IMessageHub? hub = null)
        => BindSupporting(declared, caller, isGlobalAdmin, RuleReadSubject.Baseline, hub);

    /// <summary>
    /// As <see cref="Bind"/>, binding only the subjects in <paramref name="supportedSubjects"/>; a predicate
    /// whose subject the consumer does not apply opens nothing (see <see cref="ResolveScopeSupporting"/>).
    /// A <see cref="RuleReadSubject.PermissionOnPath"/> / <see cref="RuleReadSubject.PermissionOnPartitionRoot"/>
    /// predicate is bound with its <see cref="RuleReadPredicate.HeldPermission"/>, which must be ONE flag.
    /// </summary>
    /// <param name="declared">The declared predicates.</param>
    /// <param name="caller">A real user id.</param>
    /// <param name="isGlobalAdmin">Whether the caller is a global admin.</param>
    /// <param name="supportedSubjects">The <see cref="RuleReadSubject"/> values the consumer applies.</param>
    /// <param name="hub">For logging an unknown subject; optional.</param>
    public static ImmutableArray<ResolvedReadPredicate> BindSupporting(
        IEnumerable<RuleReadPredicate> declared, string caller, bool isGlobalAdmin,
        IReadOnlySet<string> supportedSubjects, IMessageHub? hub = null)
    {
        ArgumentNullException.ThrowIfNull(supportedSubjects);
        var builder = ImmutableArray.CreateBuilder<ResolvedReadPredicate>();
        foreach (var p in declared)
        {
            // A subject this assembly KNOWS but the consumer does not apply is skipped silently: that is
            // the expected state of an older store. A subject NOBODY knows is not skipped here — it falls
            // through to the switch's default, which logs it by name (and opens nothing).
            if (RuleReadSubject.All.Contains(p.Subject) && !supportedSubjects.Contains(p.Subject))
                continue;
            switch (p.Subject)
            {
                case RuleReadSubject.GlobalAdmin:
                    if (isGlobalAdmin)
                        builder.Add(new(p.NodeType, p.PathPrefix, p.ExcludedPartitionRootTypes, null, caller));
                    break;
                case RuleReadSubject.ContentFieldIsCaller when !string.IsNullOrEmpty(p.CallerField):
                    builder.Add(new(p.NodeType, p.PathPrefix, p.ExcludedPartitionRootTypes, p.CallerField, caller));
                    break;
                case RuleReadSubject.PermissionOnPath or RuleReadSubject.PermissionOnPartitionRoot
                    when p.HeldPermission is { } held && IsSingleFlag(held):
                    builder.Add(new(p.NodeType, p.PathPrefix, p.ExcludedPartitionRootTypes, null, caller)
                    {
                        HeldPermission = held,
                        HeldOnPartitionRoot = p.Subject == RuleReadSubject.PermissionOnPartitionRoot,
                    });
                    break;
                default:
                    hub?.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(typeof(RuleReadPredicates))
                        .LogWarning(
                            "Rule read predicate for node type {NodeType} names subject '{Subject}' (field '{Field}') that no resolver knows — it opens nothing",
                            p.NodeType, p.Subject, p.CallerField);
                    break;
            }
        }
        return builder.ToImmutable();
    }

    private static bool IsSingleFlag(Permission permission)
        => permission != Permission.None && System.Numerics.BitOperations.IsPow2((uint)permission);

    private static bool IsRealUser(string? userId) =>
        userId is { Length: > 0 }
        && userId != WellKnownUsers.Anonymous
        && userId != WellKnownUsers.Public
        && userId != WellKnownUsers.System
        && !AccessService.LooksLikeHubPrincipal(userId);
}
