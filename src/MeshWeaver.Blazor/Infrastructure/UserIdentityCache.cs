using System.Collections.Concurrent;
using System.Reactive;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Blazor.Infrastructure;

/// <summary>
/// The outcome of ONE email → mesh-user lookup, keeping "this user has no node" apart from
/// "the index cannot answer yet" (issue #974).
///
/// <para>A cold cache and an unknown user used to be the same <c>null</c>. That is the trap #637
/// opened: "user unknown" is exactly the input that drives onboarding and provisioning, so a
/// storage stall or a not-yet-hydrated index could route an already-onboarded user into sign-up.
/// The cache is the only party that knows whether it has ever received a snapshot, so it is the
/// only place that can tell the two apart.</para>
/// </summary>
/// <param name="Node">The mesh <c>User</c> node, or <c>null</c> when unknown/unavailable.</param>
/// <param name="UnavailableReason">Why no verdict was reached, or <c>null</c> when the index
/// answered (found OR definitively unknown).</param>
public readonly record struct UserIdentityLookup(MeshNode? Node, string? UnavailableReason)
{
    /// <summary>True when the index reached NO verdict — never evidence the user is unknown.</summary>
    public bool IsUnavailable => UnavailableReason is not null;

    /// <summary>The index answered and found the user's node.</summary>
    public static UserIdentityLookup Found(MeshNode node) => new(node, null);

    /// <summary>The index answered: no mesh <c>User</c> node carries this email. Definitive.</summary>
    public static UserIdentityLookup Unknown { get; } = new(null, null);

    /// <summary>The index could not answer — report unavailable, never "unknown user".</summary>
    public static UserIdentityLookup Unavailable(string reason) => new(null, reason);

    /// <summary>
    /// The whole decision, as a PURE function of what the index knows — extracted so the
    /// cold-cache boundary is unit-testable without a mesh, a hub or a query subscription (the
    /// same reason <c>AreaErrorClassifier</c> is dependency-free). The cold leg is the one that
    /// matters and the one a live-mesh test cannot pin: by the time a test can observe a real
    /// cache, it has usually already hydrated.
    /// </summary>
    /// <param name="hit">The node found in the index, or <c>null</c> on a miss.</param>
    /// <param name="hydrated">Whether the first snapshot has been applied. A miss is only
    /// authoritative once this is true.</param>
    /// <param name="subscriptionFailure">Non-null once the backing query has faulted; such an
    /// index will never hydrate, so it can never honestly answer "unknown".</param>
    public static UserIdentityLookup Classify(MeshNode? hit, bool hydrated, string? subscriptionFailure)
    {
        if (hit is not null)
            return Found(hit);
        // The failure outranks hydration: a subscription that faulted AFTER its first snapshot has
        // both set, and "the index is broken" is the more useful and more honest answer — its
        // contents are now arbitrarily stale, so a miss is no longer evidence of anything.
        if (subscriptionFailure is not null)
            return Unavailable(subscriptionFailure);
        return hydrated
            ? Unknown
            : Unavailable("the mesh user index has not received its first snapshot yet");
    }

    /// <summary>
    /// Turns an <b>Unavailable</b> lookup back into a PENDING QUESTION instead of a verdict: emits
    /// the first <b>determinate</b> answer (Found or Unknown) and nothing before it.
    ///
    /// <para>🚨 This is the whole fix for the latched-degraded-identity defect. "The index cannot
    /// answer yet" is not a result to cache — and the thing being waited for is ALREADY an
    /// observable event, because the index is filled by a live <c>nodeType:User</c> query
    /// subscription. So the unavailable leg does not need a timer, a poll, a retry loop or a
    /// <c>Subject</c>: it falls through to the very same emission the success path rides
    /// (<see cref="UserIdentityCache.IndexChanged"/>) and re-asks the same
    /// <see cref="Classify"/> question. Same chain, one extra lap.</para>
    ///
    /// <para><b>The <c>StartWith</c> closes the sample-then-subscribe race.</b> A caller reaches
    /// here BECAUSE its own <see cref="UserIdentityCache.Lookup"/> came back unavailable; between
    /// that call and this subscription the snapshot may already have landed, and its emission would
    /// be gone. Re-asking at subscribe time (inside
    /// <see cref="Observable.Defer{TResult}(Func{IObservable{TResult}})"/>, so it is
    /// per-subscription and not captured once) means the window cannot swallow the answer.</para>
    ///
    /// <para><b><c>Take(1)</c> is sanctioned here</b> and is not the live-view mistake: this
    /// restores a ONE-SHOT resolution that the success path also performs exactly once. It is not
    /// feeding a data-bound view, and once the index is determinate the degraded state is over —
    /// any later refinement is an explicit write by whoever made it (onboarding calls
    /// <c>CircuitAccessHandler.UpdateUserContext</c>), never this chain firing again.</para>
    ///
    /// <para><b>Unknown terminates it too</b> — deliberately. "This email has no mesh User node" IS
    /// an answer, and the caller's seeded fallback is the correct response to it; continuing to
    /// listen would leave a subscription open for every never-onboarded visitor.</para>
    ///
    /// <para>Pure and dependency-free for the same reason <see cref="Classify"/> is: the temporal
    /// behaviour is testable with a virtual-time scheduler, no mesh, hub or query subscription
    /// required — and the unavailable window is exactly the window a live-mesh test cannot hold
    /// open.</para>
    /// </summary>
    /// <param name="indexChanged">Fires once per applied index snapshot/delta — for the real cache,
    /// <see cref="UserIdentityCache.IndexChanged"/>, which emits only AFTER the change is applied.</param>
    /// <param name="lookup">Re-asks the classified question. Called at subscribe time and once per
    /// <paramref name="indexChanged"/> emission.</param>
    public static IObservable<UserIdentityLookup> UntilDetermined(
        IObservable<Unit> indexChanged,
        Func<UserIdentityLookup> lookup)
    {
        ArgumentNullException.ThrowIfNull(indexChanged);
        ArgumentNullException.ThrowIfNull(lookup);
        return Observable
            .Defer(() => indexChanged.Select(_ => lookup()).StartWith(lookup()))
            .Where(l => !l.IsUnavailable)
            .Take(1);
    }
}

/// <summary>
/// Hot, in-process index of mesh <c>User</c> nodes by email. Subscribes to
/// <see cref="IMeshService.Query{T}"/> at construction and maintains a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> snapshot so the user-context
/// middleware can resolve <c>email → mesh user</c> synchronously without ever
/// awaiting on a hub-touching observable (which would deadlock).
///
/// <para>🚨 The index is EVENTUALLY populated, so a miss is ambiguous until the first snapshot
/// lands. <see cref="Lookup"/> reports that ambiguity instead of hiding it (issue #974); see
/// <see cref="UserIdentityLookup"/> for why a cold cache must never read as "user unknown".</para>
/// </summary>
public sealed class UserIdentityCache : IDisposable
{
    private readonly ConcurrentDictionary<string, MeshNode> _byEmail =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable _subscription;
    private readonly IDisposable _faultWatch;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly ILogger<UserIdentityCache> _logger;

    /// <summary>
    /// Fires once per index snapshot/delta, AFTER it has been applied to <see cref="_byEmail"/>.
    ///
    /// <para>This is the observable event that makes an UNAVAILABLE lookup recoverable without any
    /// new machinery: the index is filled by a live query subscription, so "ask again when the
    /// index learns something" is already an event this object receives. Callers compose it via
    /// <see cref="UserIdentityLookup.UntilDetermined"/> rather than re-subscribing the query.</para>
    ///
    /// <para>🚨 <c>Apply</c> runs INSIDE the multicast source (a <c>Select</c> ahead of
    /// <see cref="Observable.Publish{TSource}(IObservable{TSource})"/>), not in a subscriber. That
    /// is what guarantees every observer of this stream sees an index that ALREADY contains the
    /// change it is being told about — an ordering that a second <c>Subscribe</c> alongside the
    /// cache's own could not promise. It also keeps the upstream at exactly ONE subscription no
    /// matter how many circuits are waiting.</para>
    /// </summary>
    public IObservable<Unit> IndexChanged { get; }

    /// <summary>
    /// Set once the first <c>Initial</c>/<c>Reset</c> snapshot has been applied. Until then the
    /// index is EMPTY BUT NOT AUTHORITATIVE, and a miss means "ask again", not "no such user".
    /// Instance field, written from the query subscription and read from request threads —
    /// <see cref="Volatile"/> so the flip is visible without a lock.
    /// </summary>
    private bool _hydrated;

    /// <summary>
    /// Set when the backing query subscription FAILS. A faulted index never hydrates, so every
    /// later lookup is unavailable rather than silently, permanently "unknown" — which would make
    /// a dead subscription look exactly like an empty user directory.
    /// </summary>
    private string? _subscriptionFailure;

    /// <summary>
    /// Subscribes to the live <c>nodeType:User</c> query and builds the email-to-node index.
    /// The index is ready as soon as the first query emission arrives.
    /// </summary>
    /// <param name="mesh">Mesh service used to subscribe to the synced user-node query.</param>
    /// <param name="hub">Hub whose JSON serializer options are used when deserializing node content.</param>
    /// <param name="logger">Logger for subscription failures and deserialization warnings.</param>
    public UserIdentityCache(IMeshService mesh, IMessageHub hub, ILogger<UserIdentityCache> logger)
    {
        _jsonOptions = hub.JsonSerializerOptions;
        _logger = logger;
        // Post-v10: each User node lives at the root of its own partition
        // (path = ObjectId, namespace = ""). The legacy `namespace:User` filter
        // matched only the old `User/{id}` layout and missed every per-user
        // partition — so email→User resolution returned null and Index.razor
        // routed to the raw email ("No node found at 'rbuergi@systemorph.com'").
        // Drop the namespace constraint and fan out across user partitions.
        // Apply sits INSIDE the multicast source so the index is updated before ANY observer is
        // notified, and so the upstream query keeps exactly one subscription however many waiters
        // (see IndexChanged). Publish + Connect, not Publish().RefCount(): the cache's connection
        // is unconditional and lives for the object's lifetime, so the index keeps filling even
        // while nobody is waiting on it — a RefCount that dropped to zero would tear the query down.
        var applied = mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery("nodeType:User"))
            .Select(change =>
            {
                Apply(change);
                return Unit.Default;
            })
            .Publish();
        IndexChanged = applied;
        _faultWatch = applied.Subscribe(
            _ => { },
            ex =>
            {
                // Record the fault so lookups report UNAVAILABLE rather than answering
                // "unknown user" forever off an index that will never fill (#974). Still
                // logged — this is a classification, not a swallow.
                Volatile.Write(ref _subscriptionFailure,
                    $"user index subscription failed: {ex.GetType().Name}: {ex.Message}");
                _logger.LogWarning(ex, "UserIdentityCache subscription failed");
            });
        // Connect LAST: the fault watcher is already attached, and Publish's subject replays its
        // terminal notification to late subscribers, so a waiter that arrives after a failure is
        // told the index is dead instead of waiting on it forever.
        _subscription = applied.Connect();
    }

    private void Apply(QueryResultChange<MeshNode> change)
    {
        switch (change.ChangeType)
        {
            case QueryChangeType.Initial:
            case QueryChangeType.Reset:
                _byEmail.Clear();
                foreach (var node in change.Items)
                    if (TryGetEmail(node, out var email))
                        _byEmail[email] = node;
                // The index is authoritative from here on: a miss now genuinely means "no mesh
                // User node carries this email". Set LAST, after the snapshot is applied, so a
                // concurrent reader never sees "hydrated" over a half-built index.
                Volatile.Write(ref _hydrated, true);
                break;
            case QueryChangeType.Added:
            case QueryChangeType.Updated:
                foreach (var node in change.Items)
                    if (TryGetEmail(node, out var email))
                        _byEmail[email] = node;
                break;
            case QueryChangeType.Removed:
                foreach (var node in change.Items)
                    if (TryGetEmail(node, out var email))
                        _byEmail.TryRemove(email, out _);
                break;
        }
    }

    /// <summary>
    /// Synchronous, CLASSIFIED lookup (issue #974): the mesh <c>User</c> node for
    /// <paramref name="email"/>, a definitive "no such user", or "the index cannot answer".
    ///
    /// <para>The three legs, and why each is what it is:
    /// <list type="bullet">
    ///   <item><description>A hit is <b>Found</b>.</description></item>
    ///   <item><description>A miss on a HYDRATED index is <b>Unknown</b> — the snapshot has landed,
    ///     so the absence is a fact about the directory.</description></item>
    ///   <item><description>A miss BEFORE the first snapshot, or after the subscription faulted, is
    ///     <b>Unavailable</b>. The index is empty because it has not filled, not because the user
    ///     does not exist, and "user unknown" is precisely the input that drives onboarding and
    ///     provisioning.</description></item>
    /// </list></para>
    ///
    /// <para>An empty email is <b>Unknown</b>, not unavailable: there is nothing to look up, which
    /// is a fact about the input rather than a degradation.</para>
    /// </summary>
    public UserIdentityLookup Lookup(string email)
    {
        if (string.IsNullOrEmpty(email))
            return UserIdentityLookup.Unknown;

        _byEmail.TryGetValue(email, out var node);
        return UserIdentityLookup.Classify(
            node,
            Volatile.Read(ref _hydrated),
            Volatile.Read(ref _subscriptionFailure));
    }

    /// <summary>
    /// Presence-only lookup for callers that genuinely cannot act on the distinction — they have a
    /// correct fallback for BOTH "unknown" and "cannot answer" and take the same branch either way.
    ///
    /// <para>🚨 Do NOT use this to decide anything a user is told or that provisions/onboards
    /// them: it re-collapses the tri-state that <see cref="Lookup"/> exists to preserve. Call
    /// <see cref="Lookup"/> and branch on <see cref="UserIdentityLookup.IsUnavailable"/> instead.</para>
    /// </summary>
    public MeshNode? TryGetByEmail(string email) => Lookup(email).Node;

    private bool TryGetEmail(MeshNode node, out string email)
    {
        email = "";
        if (node.Content is null) return false;
        try
        {
            string? value = null;
            if (node.Content is JsonElement je)
            {
                if (je.ValueKind == JsonValueKind.Object
                    && je.TryGetProperty("email", out var emailProp)
                    && emailProp.ValueKind == JsonValueKind.String)
                    value = emailProp.GetString();
            }
            else
            {
                var prop = node.Content.GetType().GetProperty("Email")
                           ?? node.Content.GetType().GetProperty("email");
                value = prop?.GetValue(node.Content) as string;
            }
            if (!string.IsNullOrEmpty(value))
            {
                email = value;
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UserIdentityCache failed to extract email for {Path}", node.Path);
        }
        return false;
    }

    /// <summary>
    /// Waits for the index to reach a VERDICT for <paramref name="email"/> — Found or a definitive
    /// Unknown — and emits it exactly once. Nothing is emitted while the answer is merely
    /// unavailable; see <see cref="UserIdentityLookup.UntilDetermined"/> for why that is the whole
    /// point and why it needs no timer or retry loop.
    /// </summary>
    /// <param name="email">The email whose mesh <c>User</c> node is being resolved.</param>
    public IObservable<UserIdentityLookup> WhenDetermined(string email) =>
        UserIdentityLookup.UntilDetermined(IndexChanged, () => Lookup(email));

    /// <summary>Disposes the underlying query subscription, stopping further cache updates.</summary>
    public void Dispose()
    {
        _subscription.Dispose();
        _faultWatch.Dispose();
    }
}
