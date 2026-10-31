using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Messaging;

/// <summary>
/// Configures an outgoing post: the sender, target, correlation/request id and arbitrary
/// metadata applied to a message as it is dispatched into the mesh. Immutable record — each
/// <c>With…</c> method returns a new instance with the change applied.
/// </summary>
/// <param name="Sender">The address the message is sent from; also used as the identity when impersonating as a hub.
/// Owned by the posting hub: <c>MessageHub.Post</c> constructs these options with its own address and replaces any
/// other value a caller writes here (logged as <c>[ForeignSender]</c>; Doc/Architecture/SenderIsThePostingHub).</param>
public record PostOptions(Address Sender)
{
    /// <summary>
    /// Well-known property key under which a request's correlation id is stored, used to
    /// match a response delivery back to the request that triggered it.
    /// </summary>
    public const string RequestId = nameof(RequestId);

    /// <summary>
    /// Well-known property key stamped (value <c>true</c>) on a response delivery that
    /// <c>MessageHub.HandleCallbacks</c> dispatched to a live <c>hub.Observe</c> callback.
    /// Distinguishes an AWAITED response — already surfaced to its caller via
    /// <c>OnNext</c>/<c>OnError</c> — from an un-awaited one (fire-and-forget post, or a
    /// response whose observer is gone). Consumers like the portal's
    /// <c>DeliveryFailure</c>-to-modal reporter use it to skip failures the call site
    /// already handled.
    /// </summary>
    public const string CallbackDispatched = nameof(CallbackDispatched);

    /// <summary>
    /// Well-known property key a hub stamps on every request it AWAITS (<c>hub.Observe</c>): it
    /// understands <see cref="RequestProgress"/>, so a handler may report signs of life that
    /// restart the request's deadline. Its absence — a caller on an image that predates the type —
    /// means a progress report would be read as the reply, so none is sent
    /// (<see cref="MessageHubExtensions.ReportRequestProgress"/>).
    /// </summary>
    public const string AcceptsProgress = nameof(AcceptsProgress);

    /// <summary>
    /// Returns a copy of these options stamped <see cref="AcceptsProgress"/>.
    /// </summary>
    /// <returns>A new <see cref="PostOptions"/> declaring that the sender understands progress.</returns>
    public PostOptions AcceptingProgress() => WithProperty(AcceptsProgress, "true");

    /// <summary>
    /// Well-known property key a hub stamps on a request it awaits as a STREAM
    /// (<c>MessageHubExtensions.ObserveStreamed</c>): it understands response PARTS, so the handler
    /// may answer with any number of parts (<see cref="ResponsePart"/>) before the one terminal
    /// reply. Its absence — a caller that awaits a single reply, or an image that predates streamed
    /// responses — means a part would be read as the reply, so the handler answers ONCE.
    /// </summary>
    public const string AcceptsResponseParts = nameof(AcceptsResponseParts);

    /// <summary>
    /// Well-known property key on a response PART: its zero-based sequence number, as an invariant
    /// integer string. A part is correlated exactly as the reply is (<see cref="RequestId"/>), never
    /// consumes the awaiting callback, and restarts the request's silence deadline. The awaiting hub
    /// re-orders parts by this number, because a transport may deliver them out of order.
    /// </summary>
    public const string ResponsePart = nameof(ResponsePart);

    /// <summary>
    /// Well-known property key on the TERMINAL reply of a streamed response: how many parts were
    /// sent before it, as an invariant integer string. The awaiting hub completes the stream only
    /// once that many parts have been delivered, so a part that overtook nothing and arrives after
    /// the reply is still delivered in order.
    /// </summary>
    public const string ResponsePartCount = nameof(ResponsePartCount);

    /// <summary>
    /// Returns a copy of these options stamped <see cref="AcceptsResponseParts"/>.
    /// </summary>
    /// <returns>A new <see cref="PostOptions"/> declaring that the sender understands response parts.</returns>
    public PostOptions AcceptingResponseParts() => WithProperty(AcceptsResponseParts, "true");

    /// <summary>
    /// Well-known property key a PRODUCER stamps on a request it re-asks on a transient
    /// <c>ShuttingDown</c> answer: it observes the reply and, on the refusal an activation gives
    /// while it goes down, sends the request again to the address's next activation.
    ///
    /// <para>The per-delivery form of <c>[ReaskedOnShutdown]</c>. That attribute is a claim about
    /// EVERY producer of a type, so it cannot be put on a type with several producers of which only
    /// some re-ask: <c>GetDataRequest</c> is sent by the node point read, which re-asks, and by the
    /// content-collection reads and the MCP <c>get</c>, which do not (MeshWeaver#5718). This key is
    /// the claim of ONE producer about ONE request. It is read in one place, the discard report of
    /// a hub disposed while the delivery was parked behind its initialization gates
    /// (<c>MessageService</c>, event 7301): a delivery that carries it is teardown-normal (Debug),
    /// not an Error, exactly as a marked type is.</para>
    /// </summary>
    public const string ReasksOnShutdown = nameof(ReasksOnShutdown);

    /// <summary>
    /// Returns a copy of these options stamped <see cref="ReasksOnShutdown"/>. Stamp it only from a
    /// producer that really asks the next activation on a <c>ShuttingDown</c> answer.
    /// </summary>
    /// <returns>A new <see cref="PostOptions"/> declaring that this sender re-asks on shutdown.</returns>
    public PostOptions ReaskingOnShutdown() => WithProperty(ReasksOnShutdown, "true");

    internal Address Target { get; init; } = null!;

    /// <summary>
    /// Arbitrary key/value metadata to attach to the outgoing message. Backed by an immutable
    /// dictionary; assigning copies the supplied values.
    /// </summary>
    public IReadOnlyDictionary<string, object> Properties
    {
        get => PropertiesInternal;
        init => PropertiesInternal = value.ToImmutableDictionary();
    }

    private ImmutableDictionary<string, object> PropertiesInternal { get; init; } =
        ImmutableDictionary<string, object>.Empty;

    /// <summary>
    /// Returns a copy of these options with the property <paramref name="key"/> set to
    /// <paramref name="value"/> (added or overwritten).
    /// </summary>
    /// <param name="key">The property key.</param>
    /// <param name="value">The property value.</param>
    /// <returns>A new <see cref="PostOptions"/> carrying the updated property.</returns>
    public PostOptions WithProperty(string key, object value) => this with { PropertiesInternal = PropertiesInternal.SetItem(key, value) };
    /// <summary>
    /// Returns a copy of these options targeting <paramref name="targetAddress"/>.
    /// </summary>
    /// <param name="targetAddress">The address the message should be delivered to.</param>
    /// <returns>A new <see cref="PostOptions"/> addressed to <paramref name="targetAddress"/>.</returns>
    public PostOptions WithTarget(Address targetAddress) => this with { Target = targetAddress };

    /// <summary>
    /// Configures these options to post a response to <paramref name="requestDelivery"/>: targets
    /// the request's sender, carries the request's properties stamped with its id under
    /// <see cref="RequestId"/>, and auto-propagates the request's <c>AccessContext</c> so the
    /// response is attributed to the original caller rather than the responding hub.
    /// </summary>
    /// <param name="requestDelivery">The request delivery this response answers.</param>
    /// <returns>A new <see cref="PostOptions"/> configured to reply to <paramref name="requestDelivery"/>.</returns>
    public PostOptions ResponseFor(IMessageDelivery requestDelivery) =>
        this with
        {
            Target = requestDelivery.Sender,
            PropertiesInternal = CorrelatedReplyProperties(requestDelivery),
            // 🚨 Auto-propagate the request's AccessContext to the response.
            // A response IS attributed to the caller who made the request —
            // not to the hub that's now generating it. Without this, every
            // handler that posts via `o.ResponseFor(request)` had to manually
            // re-stamp the user identity, and most didn't — prod logs (Loki)
            // (2026-05-21) showed dozens of "hub=Thread, message=GetDataResponse
            // posted with no AccessContext" warnings + PostPipeline fail-closed
            // dropping them. The request's AccessContext is the authoritative
            // signal of "who is this for" — copy it forward.
            ImpersonateContext = ImpersonateContext ?? requestDelivery.AccessContext,
        };

    /// <summary>
    /// Returns a copy of these options carrying <paramref name="requestDelivery"/>'s properties
    /// with its id stamped under <see cref="RequestId"/>, correlating the outgoing message to that
    /// request without otherwise retargeting or propagating its access context.
    /// </summary>
    /// <param name="requestDelivery">The request delivery whose id is used for correlation.</param>
    /// <returns>A new <see cref="PostOptions"/> carrying the request id correlation.</returns>
    public PostOptions WithRequestIdFrom(IMessageDelivery requestDelivery) => this with
    {
        PropertiesInternal = CorrelatedReplyProperties(requestDelivery)
    };

    /// <summary>
    /// The property bag of a message correlated to <paramref name="requestDelivery"/>: the
    /// request's properties, its id under <see cref="RequestId"/>, and WITHOUT
    /// <see cref="ReasksOnShutdown"/>. That key is one producer's claim about its own request — that
    /// it asks the next activation again on a <c>ShuttingDown</c> answer. A reply has no such
    /// producer, so carrying the claim forward would let a teardown discard of the reply report as
    /// teardown-normal when nobody re-asks for it (MeshWeaver#6717).
    /// </summary>
    private static ImmutableDictionary<string, object> CorrelatedReplyProperties(IMessageDelivery requestDelivery)
        => requestDelivery.Properties.ToImmutableDictionary()
            .Remove(ReasksOnShutdown)
            .SetItem(RequestId, requestDelivery.Id);


    /// <summary>
    /// Returns a copy of these options with all entries from <paramref name="properties"/> merged
    /// into the property bag (existing keys overwritten).
    /// </summary>
    /// <param name="properties">The properties to add or overwrite.</param>
    /// <returns>A new <see cref="PostOptions"/> carrying the merged properties.</returns>
    public PostOptions WithProperties(IReadOnlyDictionary<string, object> properties)
    {
        return this with { PropertiesInternal = PropertiesInternal.AddRange(properties) };
    }

    /// <summary>
    /// Pre-computed AccessContext for this message. Set by ImpersonateAsHub().
    /// When non-null, the post pipeline uses this instead of the current user's context.
    /// </summary>
    internal AccessContext? ImpersonateContext { get; init; }

    /// <summary>
    /// Stamps a pre-captured AccessContext on this message.
    /// The post pipeline will use this identity instead of reading AsyncLocal.
    /// Use when posting from outside hub context (ContinueWith, background tasks).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public PostOptions WithAccessContext(AccessContext context)
        => this with
        {
            ImpersonateContext = context,
            ImpersonationRequestedBy = Assembly.GetCallingAssembly(),
            ImpersonationSurface = nameof(WithAccessContext),
        };

    /// <summary>
    /// The assembly that asked for <see cref="ImpersonateContext"/> — read where the delivery is
    /// posted (<see cref="MessageHub"/>), which checks it with <see cref="InMeshImpersonationGuard"/>.
    /// Null when the framework set the context itself (a response stamped with its request's user).
    /// </summary>
    internal Assembly? ImpersonationRequestedBy { get; init; }

    /// <summary>
    /// Which setter stamped <see cref="ImpersonateContext"/> — <see cref="WithAccessContext"/> or
    /// <see cref="ImpersonateAsHub()"/> — so the guard's log names the surface that was really
    /// called. Set together with <see cref="ImpersonationRequestedBy"/>.
    /// </summary>
    internal string? ImpersonationSurface { get; init; }

    /// <summary>
    /// Instructs the post pipeline to use the hub's own address as the identity
    /// for this message, instead of the current user's context.
    /// The hub address comes from the Sender property.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public PostOptions ImpersonateAsHub() => ImpersonateAsHub(Sender, Assembly.GetCallingAssembly());

    /// <summary>
    /// Instructs the post pipeline to use the specified hub address as the identity
    /// for this message. Use this overload when posting from a hosted sub-hub
    /// (e.g. a SynchronizationStream hub) but you want the workspace hub's address
    /// as the identity.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public PostOptions ImpersonateAsHub(Address hubAddress) => ImpersonateAsHub(hubAddress, Assembly.GetCallingAssembly());

    private PostOptions ImpersonateAsHub(Address hubAddress, Assembly requestedBy) => this with
    {
        ImpersonateContext = new AccessContext
        {
            ObjectId = hubAddress.ToFullString(),
            Name = hubAddress.ToString(),
            IsHub = true
        },
        ImpersonationRequestedBy = requestedBy,
        ImpersonationSurface = nameof(ImpersonateAsHub),
    };

    /// <summary>
    /// 🚨 The ONE sanctioned way a post leaves a hub under another hub's address (#6553): a parent
    /// CARRYING a delivery its hosted hub posted but can no longer send itself (a correlated reply or
    /// a release posted during that hub's teardown). Only this assembly can set it.
    ///
    /// <para>Every other post is stamped with the address of the hub that actually posts it
    /// (<see cref="MessageHub.Post{TMessage}"/>): a <see cref="Sender"/> the caller wrote is
    /// overwritten, so a hub guard that decides by sender — the stamped-field guard admitting only
    /// the owning hub — cannot be passed by code that merely names another hub's address.</para>
    /// </summary>
    internal bool CarriedForHostedHub { get; init; }

    /// <summary>Marks these options as carried by a parent for one of its hosted hubs (see
    /// <see cref="CarriedForHostedHub"/>).</summary>
    internal PostOptions CarriedByParent() => this with { CarriedForHostedHub = true };

    internal string MessageId { get; init; } = Guid.NewGuid().AsString();
    /// <summary>
    /// Returns a copy of these options with an explicit message id, overriding the
    /// auto-generated one. Useful for deduplication or correlating a known id.
    /// </summary>
    /// <param name="messageId">The message id to assign; must not be null.</param>
    /// <returns>A new <see cref="PostOptions"/> with the specified message id.</returns>
    public PostOptions WithMessageId(string messageId)
        => this with { MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId)) };
}
