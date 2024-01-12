namespace OpenSmc.Messaging;

public interface IHostedAddress
{
    object Host { get; }
}

public interface IHostedAddressSettable : IHostedAddress
{
    object SetHost(object hostAddress);
}