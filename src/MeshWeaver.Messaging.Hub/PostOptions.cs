using System.Collections.Immutable;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Messaging;

/// <summary>
/// Configures an outgoing post: the sender, target, correlation/request id and arbitrary
/// metadata applied to a message as it is dispatched into the mesh. Immutable record — each
/// <c>With…</c> method returns a new instance with the change applied.
/// </summary>
/// <param name="Sender">The address the message is sent from; also used as the identity when impersonating as a hub.</param>
public record PostOptions(Address Sender)
{
    /// <summary>
    /// Well-known property key under which a request's correlation id is stored, used to
    /// match a response delivery back to the request that triggered it.
    /// </summary>
    public const string RequestId = nameof(RequestId);
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
            PropertiesInternal = requestDelivery.Properties.ToImmutableDictionary().SetItem(RequestId, requestDelivery.Id),
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
        PropertiesInternal = requestDelivery.Properties.ToImmutableDictionary()
                                                                                                              .SetItem(RequestId, requestDelivery.Id)
    };


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
    public PostOptions WithAccessContext(AccessContext context)
        => this with { ImpersonateContext = context };

    /// <summary>
    /// Instructs the post pipeline to use the hub's own address as the identity
    /// for this message, instead of the current user's context.
    /// The hub address comes from the Sender property.
    /// </summary>
    public PostOptions ImpersonateAsHub() => ImpersonateAsHub(Sender);

    /// <summary>
    /// Instructs the post pipeline to use the specified hub address as the identity
    /// for this message. Use this overload when posting from a hosted sub-hub
    /// (e.g. a SynchronizationStream hub) but you want the workspace hub's address
    /// as the identity.
    /// </summary>
    public PostOptions ImpersonateAsHub(Address hubAddress) => this with
    {
        ImpersonateContext = new AccessContext
        {
            ObjectId = hubAddress.ToFullString(),
            Name = hubAddress.ToString(),
            IsHub = true
        }
    };

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
