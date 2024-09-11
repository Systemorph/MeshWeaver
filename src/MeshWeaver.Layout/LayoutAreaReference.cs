using System.Text.Json;
using Json.Pointer;
using MeshWeaver.Data;

namespace MeshWeaver.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<EntityStore>
{
    public object Id { get; init; }
    public string QueryString { get; init; }
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



    public string ToAppHref(object address)
    {
        var ret = $"app/{address}/{LayoutExtensions.Encode(Area)}";
        if (Id?.ToString() is { } s)
            ret = $"{ret}/{LayoutExtensions.Encode(s)}";
        if (QueryString is not null)
        {
            ret += $"?{QueryString}";
        }
        return ret;
    }



}
