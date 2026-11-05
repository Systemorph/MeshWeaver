using System.Collections.Immutable;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Mesh.Security;

/// <summary>
/// The LOCK on one person's account: while it is in force (<see cref="IsInForce"/>) the person
/// signs in to nothing, every token they hold and every session they have open stops being admitted
/// at its next request, every agent run on their behalf is refused at its next permission check, and
/// they hold no effective permission anywhere — not on their own home partition either. Their data is
/// left exactly as it is, so <see cref="AccountLocks.Unlock"/> gives the account back unchanged.
///
/// <para><b>Why a record in the Admin partition, not a field on the <c>User</c> node.</b> The User
/// node is the root of the person's OWN partition, where they hold Admin — a lock stored there would
/// sit inside the reach of the person it locks out, and a platform admin has no standing access to
/// it (a global admin is not a data superuser). The Admin partition is the one partition only a
/// global admin can write; invitations, service identities and declared grant rules live there for
/// the same reason. The record is one node per person at <see cref="PathOf"/>, kept after an
/// unlock (<see cref="UnlockedAt"/>), so the version history of that one node is the audit trail:
/// who locked, when, why, and who unlocked.</para>
///
/// <para><b>Where it is enforced.</b> The permission fold (<c>PermissionEvaluator</c>) answers
/// <see cref="Permission.None"/> for a locked subject — every hub request, node read, write,
/// delivery and agent tool call passes that fold; the node-type access rules
/// (<c>NodeTypeAccessRuleGate</c>) refuse a locked subject before running a rule; the query boundary
/// (<c>MeshService</c>) answers a locked viewer's query with nothing, which is what keeps the
/// SQL-filtered providers in line; and the portal's request middleware refuses a locked person's
/// sign-in and bearer token with a 403. All four read the SAME live list
/// (<see cref="AccountLocks.ObserveAll(IMessageHub)"/>). See <c>Doc/Architecture/AccountLockout</c>.</para>
/// </summary>
public record AccountLock
{
    /// <summary>The node type of an account lock record.</summary>
    public const string NodeTypeName = "AccountLock";

    /// <summary>Where account locks live: the Admin partition, out of reach of everyone but a global admin.</summary>
    public const string Namespace = "Admin/_AccountLock";

    /// <summary>The path of the lock record for <paramref name="userId"/>.</summary>
    /// <param name="userId">The person's user id (their partition key, e.g. <c>jdoe</c>).</param>
    /// <returns><c>Admin/_AccountLock/{userId}</c>.</returns>
    public static string PathOf(string userId) => $"{Namespace}/{userId}";

    /// <summary>The locked person's user id — the mesh <c>User</c> node id, never an email.</summary>
    public string UserId { get; init; } = "";

    /// <summary>When the lock was put in force.</summary>
    public DateTimeOffset? LockedAt { get; init; }

    /// <summary>The global admin who locked the account.</summary>
    public string? LockedBy { get; init; }

    /// <summary>Why the account is locked — recorded for whoever reviews the Admin partition.</summary>
    public string? Reason { get; init; }

    /// <summary>When the lock was lifted; <c>null</c> while it is in force.</summary>
    public DateTimeOffset? UnlockedAt { get; init; }

    /// <summary>The global admin who lifted the lock.</summary>
    public string? UnlockedBy { get; init; }

    /// <summary>The governed activity that wrote the record, when one did.</summary>
    [Browsable(false)]
    public string? GovernedBy { get; init; }

    /// <summary>True while the lock is in force: it names a person, has been set, and has not been lifted.</summary>
    public bool IsInForce => !string.IsNullOrWhiteSpace(UserId) && LockedAt is not null && UnlockedAt is null;
}

/// <summary>
/// Reading and writing <see cref="AccountLock"/> records — the ONE definition of "is this person
/// locked", shared by the permission fold, the node-type rule gate, the query boundary and the
/// portal's admission, and the two verbs a platform administrator uses.
///
/// <para>🚨 <b>Authorize as the caller, execute as System</b> (policy
/// <c>authorize-as-caller-execute-as-system</c>): <see cref="Lock"/> and <see cref="Unlock"/> take
/// the caller EXPLICITLY, check that the caller is a global admin
/// (<see cref="HubPermissionExtensions.IsGlobalAdmin(IMessageHub,string)"/>), and only then write
/// the record as System.</para>
/// </summary>
public static class AccountLocks
{
    /// <summary>The cache id of the live lock list — one shared subscription per process.</summary>
    public const string QueryId = SecurityQueries.QueryIdPrefix + "account-locks";

    /// <summary>
    /// The query the lock list is read with — anchored on <see cref="AccountLock.Namespace"/>, so it
    /// is served by the Admin partition's schema alone and never fans out.
    /// </summary>
    public static string Query
        => SecurityQueries.Enumeration($"path:{AccountLock.Namespace} scope:descendants "
            + $"nodeType:{AccountLock.NodeTypeName} {SecurityQueries.ContentProjection}");

    /// <summary>
    /// Whether <paramref name="userId"/> can be locked at all: a person's id, never a reserved
    /// principal (System, Anonymous, Public, a hub), a service principal (those are revoked, see
    /// <see cref="ServiceIdentity.IsRevoked"/>), an email or a path.
    /// </summary>
    /// <param name="userId">The candidate id.</param>
    /// <returns>True for an id a lock can name.</returns>
    public static bool IsLockable(string? userId)
        => !string.IsNullOrWhiteSpace(userId)
           && !RequestIdentity.IsReservedPrincipal(userId)
           && !ServiceIdentity.IsServiceObjectId(userId)
           && userId.IndexOf('@') < 0
           && userId.IndexOf('/') < 0
           && string.Equals(userId.Trim(), userId, StringComparison.Ordinal);

    /// <summary>
    /// How account ids are compared everywhere a lock is looked up: case-INSENSITIVELY, the way home
    /// ownership (<c>RlsNodeValidator</c>'s own-scope shortcut) and the user directory identify an
    /// account. A lock written as <c>JDOE</c> therefore applies to the account <c>jdoe</c>.
    /// </summary>
    public static StringComparer IdComparer => StringComparer.OrdinalIgnoreCase;

    /// <summary>
    /// Every lock record among <paramref name="nodes"/>, by user id — typed or raw content, only
    /// records that sit directly in <see cref="AccountLock.Namespace"/> and whose node id is the user
    /// id they name (a record placed anywhere else, or naming someone other than its own id, is
    /// inert). Lifted locks are kept: the map is the whole register, <see cref="AccountLock.IsInForce"/>
    /// says which apply. Keyed by <see cref="IdComparer"/>; when two records differ only in casing,
    /// the one IN FORCE wins, so a stale lifted twin can never hide a lock.
    /// </summary>
    /// <param name="nodes">The candidate nodes.</param>
    /// <param name="options">Serializer options for raw content.</param>
    /// <returns>The register.</returns>
    public static ImmutableDictionary<string, AccountLock> Read(IEnumerable<MeshNode> nodes, JsonSerializerOptions options)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, AccountLock>(IdComparer);
        foreach (var node in nodes)
        {
            if (!string.Equals(node.NodeType, AccountLock.NodeTypeName, StringComparison.Ordinal)
                || !string.Equals(node.Namespace, AccountLock.Namespace, StringComparison.Ordinal))
                continue;
            var record = node.Content.As<AccountLock>(options, what: AccountLock.Namespace);
            if (record is null || !IdComparer.Equals(record.UserId, node.Id))
                continue;
            if (builder.TryGetValue(record.UserId, out var twin) && twin.IsInForce && !record.IsInForce)
                continue;
            builder[record.UserId] = record;
        }
        return builder.ToImmutable();
    }

    /// <summary>
    /// The live lock register — re-emitted whenever a record is written. Read as System through the
    /// process-wide node-stream cache, like every other <c>$security-*</c> input, so the fold and
    /// every admission seam share one upstream subscription.
    /// </summary>
    /// <param name="hub">The hub whose node-stream cache serves the register.</param>
    /// <returns>The register, live.</returns>
    public static IObservable<ImmutableDictionary<string, AccountLock>> ObserveAll(IMessageHub hub)
    {
        ArgumentNullException.ThrowIfNull(hub);
        return ObserveAll(hub.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>(), hub.JsonSerializerOptions);
    }

    /// <summary>
    /// <see cref="ObserveAll(IMessageHub)"/> over an already-resolved cache — the overload the
    /// permission fold uses, so a later emission never resolves from a disposed hub scope (#2679).
    /// </summary>
    internal static IObservable<ImmutableDictionary<string, AccountLock>> ObserveAll(
        IMeshNodeStreamCache cache, JsonSerializerOptions options)
        => cache.GetQuery(QueryId, options, Query).Select(nodes => Read(nodes, options));

    /// <summary>
    /// Whether the lock APPLIES to <paramref name="userId"/>, live: a lock is in force on them and they
    /// do not hold platform administration. A lock never removes an administrator — whatever wrote the
    /// record — which is what keeps a lock from ever leaving an instance without one (the permission
    /// fold applies the same rule, <c>PermissionEvaluator.ObserveEffectiveLock</c>). An id that cannot
    /// be locked (<see cref="IsLockable"/>) answers <c>false</c> at once, without opening the read.
    ///
    /// <para>🚨 <b>Not seeded.</b> A lock SUBTRACTS everything, so an optimistic <c>false</c> before
    /// the register has answered would be an admission window — the monotonicity rule of the fold
    /// (<c>Doc/Architecture/AccessControl</c> → "The convergence contract"). A register fault faults
    /// this stream.</para>
    /// </summary>
    /// <param name="hub">The hub whose node-stream cache serves the register.</param>
    /// <param name="userId">The subject.</param>
    /// <returns>True while a lock applies to <paramref name="userId"/>.</returns>
    public static IObservable<bool> ObserveLocked(IMessageHub hub, string? userId)
        => !IsLockable(userId)
            ? Observable.Return(false)
            : ObserveAll(hub).Select(locks => IsLockedIn(locks, userId!)).DistinctUntilChanged()
                // The administrator question only while a record is in force — an unlocked subject pays nothing.
                .Select(inForce => inForce
                    ? IsAdministrator(hub, userId!).Select(isAdmin => !isAdmin)
                    : Observable.Return(false))
                .Switch()
                .DistinctUntilChanged();

    /// <summary>
    /// Every account a lock APPLIES to (<see cref="ObserveLocked(IMessageHub,string?)"/>), as one live
    /// set compared with <see cref="IdComparer"/> — what <see cref="AccountLockCatalog"/> keeps as its
    /// synchronous snapshot. Faults when the register's read faults.
    /// </summary>
    /// <param name="hub">The hub whose node-stream cache serves the register.</param>
    /// <returns>The locked accounts, live.</returns>
    public static IObservable<ImmutableHashSet<string>> ObserveEffectivelyLocked(IMessageHub hub)
        => EffectivelyLocked(hub, ObserveAll(hub));

    /// <summary>
    /// The accounts a lock APPLIES to, from <paramref name="register"/> — the in-force records less
    /// every platform administrator. Shared by <see cref="ObserveEffectivelyLocked"/> and
    /// <see cref="AccountLockCatalog"/>'s standing platform-only read.
    /// </summary>
    internal static IObservable<ImmutableHashSet<string>> EffectivelyLocked(
        IMessageHub hub, IObservable<ImmutableDictionary<string, AccountLock>> register)
        => register
            .Select(locks => locks.Values.Where(l => l.IsInForce).Select(l => l.UserId).ToArray())
            .Select(inForce => inForce.Length == 0
                ? Observable.Return(ImmutableHashSet.Create<string>(IdComparer))
                : inForce.Select(id => IsAdministrator(hub, id).Select(isAdmin => (Id: id, IsAdmin: isAdmin)))
                    .CombineLatest()
                    .Select(verdicts => verdicts.Where(v => !v.IsAdmin).Select(v => v.Id).ToImmutableHashSet(IdComparer)))
            .Switch();

    /// <summary>
    /// Whether <paramref name="userId"/> holds platform administration — the exemption question —
    /// asked as a CONTAINED System read with the subject explicit. It is a fact about the subject, never
    /// about whoever happens to be ambient: under a caller's scoped API token the fold would cap the
    /// answer by THAT token's scope and could make an administrator read as locked.
    /// </summary>
    private static IObservable<bool> IsAdministrator(IMessageHub hub, string userId)
        => hub.ServiceProvider.GetService<AccessService>().RunAsSystem(() => hub.IsGlobalAdmin(userId));

    /// <summary>
    /// Whether a lock RECORD is in force on <paramref name="userId"/>, live — without the administrator
    /// exemption, which the fold applies itself (it cannot ask <c>IsGlobalAdmin</c>, which IS the fold).
    /// </summary>
    internal static IObservable<bool> ObserveRecordInForce(
        IMeshNodeStreamCache cache, JsonSerializerOptions options, string? userId)
        => !IsLockable(userId)
            ? Observable.Return(false)
            : ObserveAll(cache, options).Select(locks => IsLockedIn(locks, userId!)).DistinctUntilChanged();

    /// <summary>Whether <paramref name="locks"/> holds a lock in force on <paramref name="userId"/>,
    /// compared with <see cref="IdComparer"/> whatever comparer the dictionary carries.</summary>
    /// <param name="locks">The register.</param>
    /// <param name="userId">The subject.</param>
    /// <returns>True when locked.</returns>
    public static bool IsLockedIn(IReadOnlyDictionary<string, AccountLock> locks, string userId)
        => RecordOf(locks, userId) is { IsInForce: true };

    /// <summary>The record naming <paramref name="userId"/> (by <see cref="IdComparer"/>), or null.</summary>
    /// <param name="locks">The register.</param>
    /// <param name="userId">The subject.</param>
    /// <returns>The record, lifted or in force, or null when there is none.</returns>
    public static AccountLock? RecordOf(IReadOnlyDictionary<string, AccountLock> locks, string userId)
    {
        if (locks.TryGetValue(userId, out var exact))
            return exact;
        AccountLock? found = null;
        foreach (var (key, record) in locks)
            if (IdComparer.Equals(key, userId) && (found is null || record.IsInForce))
                found = record;
        return found;
    }

    /// <summary>
    /// Locks <paramref name="userId"/>'s account. Authorize as the caller — <paramref name="caller"/>
    /// must be a global admin — and refuse, by name, a target that cannot be locked: a reserved or
    /// service principal, the caller themselves, or anyone who still holds platform administration
    /// (their <c>Admin/_Access</c> grant is removed by another administrator first, so a lock can
    /// never leave an instance without a human administrator). Then write the record as System.
    /// </summary>
    /// <param name="hub">The hub to act on (its mesh service and access service are used).</param>
    /// <param name="caller">The person asking, captured at the verb's entry.</param>
    /// <param name="userId">The person to lock out.</param>
    /// <param name="reason">Why — recorded on the lock.</param>
    /// <returns>The written record; an <see cref="UnauthorizedAccessException"/> or
    /// <see cref="InvalidOperationException"/> naming the refusal.</returns>
    public static IObservable<AccountLock> Lock(IMessageHub hub, AccessContext? caller, string userId, string? reason)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var target = userId?.Trim() ?? "";
        var callerId = caller?.ObjectId;
        var locale = caller?.Locale;
        if (!IsLockable(target))
            return Observable.Throw<AccountLock>(new InvalidOperationException(
                LocalizationCatalog.Get("accountLock.refused.notAPerson", locale, target)));
        if (IdComparer.Equals(callerId, target))
            return Observable.Throw<AccountLock>(new InvalidOperationException(
                LocalizationCatalog.Get("accountLock.refused.self", locale)));

        return AuthorizeGlobalAdmin(hub, caller)
            .SelectMany(_ => hub.IsGlobalAdmin(target).TakeDecisionOutsideGate())
            .SelectMany(targetIsAdmin => targetIsAdmin
                ? Observable.Throw<ImmutableDictionary<string, AccountLock>>(new InvalidOperationException(
                    LocalizationCatalog.Get("accountLock.refused.administrator", locale, target)))
                // The register only DISCOVERS an existing record's casing, so a re-lock lands on it
                // rather than beside it; it decides nothing about the lock itself.
                : ObserveAll(hub).TakeDecisionOutsideGate())
            .SelectMany(locks =>
            {
                var id = RecordOf(locks, target)?.UserId ?? target;
                var record = new AccountLock
                {
                    UserId = id,
                    LockedAt = DateTimeOffset.UtcNow,
                    LockedBy = callerId,
                    Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
                };
                var node = new MeshNode(id, AccountLock.Namespace)
                {
                    NodeType = AccountLock.NodeTypeName,
                    Name = id,
                    Content = record,
                };
                var mesh = hub.ServiceProvider.GetRequiredService<IMeshService>();
                return hub.ServiceProvider.GetService<AccessService>()
                    .RunAsSystem(() => mesh.CreateOrUpdateNode(node))
                    .Select(_ => record);
            });
    }

    /// <summary>
    /// Lifts the lock on <paramref name="userId"/>: authorize as the caller (a global admin), then
    /// stamp <see cref="AccountLock.UnlockedAt"/> on the record as System. The record is kept, so its
    /// history still says who locked and who unlocked.
    ///
    /// <para>The register only DISCOVERS whether a record exists (a point read of a path that may not
    /// exist is never issued); whether the lock is still in force is decided INSIDE the update, on the
    /// owner's authoritative node — so a lift that another replica already wrote is not stamped twice,
    /// and a lock the register has not caught up with yet is still lifted. An account with no record,
    /// or whose record is no longer in force, is answered with an <see cref="InvalidOperationException"/>
    /// and nothing is written.</para>
    /// </summary>
    /// <param name="hub">The hub to act on.</param>
    /// <param name="caller">The person asking, captured at the verb's entry.</param>
    /// <param name="userId">The person whose lock is lifted.</param>
    /// <returns>The node as written.</returns>
    public static IObservable<MeshNode> Unlock(IMessageHub hub, AccessContext? caller, string userId)
    {
        ArgumentNullException.ThrowIfNull(hub);
        var target = userId?.Trim() ?? "";
        var callerId = caller?.ObjectId;
        var locale = caller?.Locale;
        var notLocked = new InvalidOperationException(
            LocalizationCatalog.Get("accountLock.refused.notLocked", locale, target));
        return AuthorizeGlobalAdmin(hub, caller)
            .SelectMany(_ => ObserveAll(hub).TakeDecisionOutsideGate())
            .SelectMany(locks =>
            {
                if (RecordOf(locks, target) is not { } existing)
                    return Observable.Throw<MeshNode>(notLocked);
                var now = DateTimeOffset.UtcNow;
                var options = hub.JsonSerializerOptions;
                // Typed Update: a record that is present but unreadable FAULTS (never a defaulted
                // record written over it); one that is absent or already lifted is left as it is.
                return hub.ServiceProvider.GetService<AccessService>()
                    .RunAsSystem(() => hub.GetMeshNodeStream(AccountLock.PathOf(existing.UserId))
                        .Update<AccountLock>((node, record) =>
                        {
                            if (record is not { IsInForce: true })
                                return node;
                            return node with { Content = record with { UnlockedAt = now, UnlockedBy = callerId } };
                        }))
                    // Lifted by THIS write exactly when the committed record carries this write's stamp.
                    .SelectMany(node => node.ContentAs<AccountLock>(options) is { } written
                                        && written.UnlockedAt is { } at
                                        && Math.Abs((at - now).TotalMilliseconds) < 1
                                        && string.Equals(written.UnlockedBy, callerId, StringComparison.Ordinal)
                        ? Observable.Return(node)
                        : Observable.Throw<MeshNode>(notLocked));
            });
    }

    private static IObservable<bool> AuthorizeGlobalAdmin(IMessageHub hub, AccessContext? caller)
    {
        var callerId = caller?.ObjectId;
        var locale = caller?.Locale;
        return string.IsNullOrEmpty(callerId)
            ? Observable.Throw<bool>(new UnauthorizedAccessException(
                LocalizationCatalog.Get("accountLock.refused.noCaller", locale)))
            : hub.IsGlobalAdmin(callerId)
                .TakeDecisionOutsideGate()
                .SelectMany(isAdmin => isAdmin
                    ? Observable.Return(true)
                    : Observable.Throw<bool>(new UnauthorizedAccessException(
                        LocalizationCatalog.Get("accountLock.refused.notAdmin", locale, callerId))));
    }
}
