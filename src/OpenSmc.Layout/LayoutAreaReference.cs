using System.Collections.Immutable;
using System.Text.Json;
using Json.Pointer;
using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<EntityStore>
{
    public object Id { get; init; }
    public IReadOnlyDictionary<string, object> Options { get; init; } = ImmutableDictionary<string, object>.Empty;
    public bool RenderLayout { get; init; }

    public const string Data = "data";
    public const string Areas = "areas";
    public const string Properties = "properties";

    public static string GetDataPointer(string id, params string[] extraSegments) =>
        JsonPointer.Create(
            new[] { Data, JsonSerializer.Serialize(id) }
            .Concat(extraSegments)
            .Select(PointerSegment.Create)
            .ToArray()
        )
        .ToString();

    public static string GetControlPointer(string area) =>
        JsonPointer.Create(Areas, JsonSerializer.Serialize(area)).ToString();
    public static string GetPropertiesPointer(string id) =>
        JsonPointer.Create(Properties, id).ToString();

    public virtual bool Equals(LayoutAreaReference other)
    {
        if (other is null)
            return false;
        return Equals(Area, other.Area) && Equals(Id, other.Id) && Options.SequenceEqual(other.Options);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Area, Id, Options);
    }
}
