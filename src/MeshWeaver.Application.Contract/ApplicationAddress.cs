
using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Application;

// domain name + env pair uniquely identifies the domain
// TODO V10: add "string ClientId" (05.06.2024, Alexander Kravets)
public record ApplicationAddress(string Name, string Environment)
{
    public override string ToString()
        => $"app/{Name}/{Environment}";
}
public record UiAddress : IAddressWithId
{
    public string Id { get; init; } = Guid.NewGuid().AsString();
}

