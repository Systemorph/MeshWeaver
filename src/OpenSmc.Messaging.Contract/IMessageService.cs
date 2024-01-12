using System.Collections.Generic;
using System.Collections.Immutable;
using Systemorph.Messaging;

namespace OpenSmc.Messaging;

public record PostOptions(object Sender)
{
    public const string RequestId = nameof(RequestId);
    internal object Target { get; init; }
    public ImmutableDictionary<string, object> Properties { get; init; } = ImmutableDictionary<string, object>.Empty;

    public PostOptions WithProperty(string key, object value) => this with { Properties = Properties.SetItem(key, value)};
    public PostOptions WithTarget(object targetAddress) => this with { Target = targetAddress };

    public PostOptions ResponseFor(IMessageDelivery requestDelivery) =>
        requestDelivery == null ? this : 
        this with
        {
            Target = requestDelivery.Sender,
            Properties = requestDelivery.Properties
                                        .SetItem(RequestId, requestDelivery.Id)
        };

    public PostOptions WithRequestIdFrom(IMessageDelivery requestDelivery) => this with
                                                                              {
                                                                                  Properties = requestDelivery.Properties
                                                                                                              .SetItem(RequestId, requestDelivery.Id)
                                                                              };


    public PostOptions WithProperties(IDictionary<string, object> properties)
    {
        return this with { Properties = Properties.AddRange(properties) };
    }


}
