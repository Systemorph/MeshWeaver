namespace MeshWeaver.Messaging;


public interface IHostedAddress
{
    object Host { get; init; }
}

public record HostedAddress(object Address, object Host);

