using System.Text.Json;
using Json.Pointer;

namespace MeshWeaver.Data;

/// <summary>
/// Provides methods and constants for working with layout area references.
/// </summary>
public record LayoutAreaReference(string Area) : WorkspaceReference<EntityStore>
{
    public object Id { get; init; }
    public string Layout { get; init; }

    public const string Data = "data";

    /// <summary>
    /// The constant string for areas.
    /// </summary>
    public const string Areas = "areas";
    /// <summary>
    /// The constant string for properties.
    /// </summary>
    public const string Properties = "properties";

    /// <summary>
    /// Gets the data pointer for the specified ID and extra segments.
    /// </summary>
    /// <param name="id">The ID for the data pointer.</param>
    /// <param name="extraSegments">The extra segments for the data pointer.</param>
    /// <returns>A string representing the data pointer.</returns>
    public static string GetDataPointer(string id, params string[] extraSegments) =>
        JsonPointer.Create(
            new[] { Data, EncodeId(id) }
            .Concat(extraSegments)
            .Select(x => (PointerSegment)x)
            .ToArray()
        )
        .ToString();

    public static string EncodeId(string id)
        => JsonSerializer.Serialize(id);

    /// <summary>
    /// Gets the control pointer for the specified area.
    /// </summary>
    /// <param name="area">The area for the control pointer.</param>
    /// <returns>A string representing the control pointer.</returns>
    public static string GetControlPointer(string area) =>
        JsonPointer.Create(Areas, JsonSerializer.Serialize(area)).ToString();

    /// <summary>
    /// Gets the properties pointer for the specified ID.
    /// </summary>
    /// <param name="id">The ID for the properties pointer.</param>
    /// <returns>A string representing the properties pointer.</returns>
    public static string GetPropertiesPointer(string id) =>
        JsonPointer.Create(Properties, id).ToString();

    /// <summary>
    /// Converts the layout area reference to an application href.
    /// </summary>
    /// <param name="address">The address for the href.</param>
    /// <returns>A string representing the application href.</returns>
    public string ToAppHref(object address)
    {
        var ret = $"app/{address}/{WorkspaceReference.Encode(Area)}";
        if (Id?.ToString() is { } s)
            ret = $"{ret}/{WorkspaceReference.Encode(s)}";
        return ret;
    }
}
