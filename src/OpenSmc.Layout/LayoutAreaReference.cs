using System.Text.Json;
using Json.Pointer;
using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<EntityStore>
{
    public object Id { get; init; }
    public string Layout { get; init; }

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



    public string ToHref(object address)
    {
        var ret = $"{address}/{Encode(Area)}";
        if (Id?.ToString() is { } s)
            ret = $"{ret}/{Encode(s)}";
        return ret;
    }

    public static object Encode(object value) => value is string s ? s.Replace(".", "%9Y") : value;
    public static object Decode(object value) => value is string s ? s.Replace("%9Y", ".") : value;
}
