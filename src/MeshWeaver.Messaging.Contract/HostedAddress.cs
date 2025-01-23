namespace MeshWeaver.Messaging;


public record HostedAddress(Address Address, Address Host): Address(Host.Type, Host.Id)
{
    public const string TypeName = "hosted";
}

