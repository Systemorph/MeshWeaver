namespace MeshWeaver.Mesh.Contract;

public record MeshWeaverConnection
{
    public string AddressType { get; init; }
    public string Id { get; init; }
    public ConnectionStatus Status { get; init; }
}

public enum ConnectionStatus
{
    Connected,
    Disconnected
}
