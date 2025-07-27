using MeshWeaver.Messaging;
using MeshWeaver.ShortGuid;

namespace MeshWeaver.Data;

public record ActivityAddress(string? Id = null) : Address(TypeName, Id ?? Guid.NewGuid().AsString() ?? string.Empty)
{
    public const string TypeName = "activity";
}
