using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Pointer;
using Microsoft.Extensions.Primitives;
using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<EntityStore>
{
    public object Id { get; init; }
    public IReadOnlyDictionary<string, StringValues> Options { get; init; }
    public const string Data = "data";
    public const string Areas = "areas";

    public static string GetDataPointer(string id) =>
        JsonPointer.Create(Data, JsonSerializer.Serialize(id)).ToString();

    public static string GetControlPointer(string area) =>
        JsonPointer.Create(Areas, JsonSerializer.Serialize(area)).ToString();

    public virtual bool Equals(LayoutAreaReference other)
    {
        if (other is null)
            return false;
        return Id.Equals(other.Id) && Options.SequenceEqual(other.Options);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Area, Id, Options);
    }
}
