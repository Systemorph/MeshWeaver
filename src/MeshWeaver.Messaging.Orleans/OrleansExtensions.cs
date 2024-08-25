using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Orleans;

public static class OrleansExtensions
{
    public const string Storage = "storage";



}

public record OrleansAddress : IAddressWithId
{
    public string Id { get; set; } = Guid.NewGuid().AsString();

    public override string ToString()
        => Id;
}
