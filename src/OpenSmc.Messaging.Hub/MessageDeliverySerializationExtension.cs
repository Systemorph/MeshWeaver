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

}