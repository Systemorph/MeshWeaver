using System.Collections.Immutable;

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
        requestDelivery == null ? this : 
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
}
