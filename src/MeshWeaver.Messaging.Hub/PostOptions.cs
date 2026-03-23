using System.Collections.Immutable;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Messaging;

public record PostOptions(Address Sender)
{
    public const string RequestId = nameof(RequestId);
    internal Address Target { get; init; } = null!;

    public IReadOnlyDictionary<string, object> Properties
    {
        get => PropertiesInternal;
        init => PropertiesInternal = value.ToImmutableDictionary();
    }

    private ImmutableDictionary<string, object> PropertiesInternal { get; init; } =
        ImmutableDictionary<string, object>.Empty;

    public PostOptions WithProperty(string key, object value) => this with { PropertiesInternal = PropertiesInternal.SetItem(key, value) };
    public PostOptions WithTarget(Address targetAddress) => this with { Target = targetAddress };

    public PostOptions ResponseFor(IMessageDelivery requestDelivery) =>
        this with
        {
            Target = requestDelivery.Sender,
            PropertiesInternal = requestDelivery.Properties.ToImmutableDictionary().SetItem(RequestId, requestDelivery.Id)
        };

    public PostOptions WithRequestIdFrom(IMessageDelivery requestDelivery) => this with
    {
        PropertiesInternal = requestDelivery.Properties.ToImmutableDictionary()
                                                                                                              .SetItem(RequestId, requestDelivery.Id)
    };


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
            Name = hubAddress.ToString()
        }
    };

    internal string MessageId { get; init; } = Guid.NewGuid().AsString();
    public PostOptions WithMessageId(string messageId)
        => this with { MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId)) };
}
