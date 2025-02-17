namespace MeshWeaver.Messaging;


public record HostedAddress(Address Address, Address Host): Address(Host.Type, $"{Host.Id}/{Address}")
{
    public const string TypeName = "hosted";
}

