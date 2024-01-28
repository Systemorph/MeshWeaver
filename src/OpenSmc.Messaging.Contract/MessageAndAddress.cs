namespace OpenSmc.Messaging
{
    public record MessageAndAddress(object Message, object Address);

    public record ViewRequest(object Message, object Address, object Area) : MessageAndAddress(Message, Address);
}
