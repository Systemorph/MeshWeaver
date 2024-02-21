using OpenSmc.Serialization;

namespace OpenSmc.Messaging;

public static class MessageDeliverySerializationExtension
{
    public static IMessageDelivery SerializeDelivery(this ISerializationService serializationService, IMessageDelivery delivery)
    {
        try
        {
            var rawJson = serializationService.Serialize(delivery.Message);
            return delivery.WithMessage(rawJson);
        }
        catch (Exception e)
        {
            return delivery.Failed($"Error serializing: \n{e}");
        }
    }

    public static IMessageDelivery DeserializeDelivery(this ISerializationService serializationService, IMessageDelivery delivery)
    {
        if (delivery.Message is not RawJson rawJson)
            return delivery;

        var deserializedMessage = serializationService.Deserialize(rawJson.Content);
        return delivery.WithMessage(deserializedMessage);
    }
}