using System.Collections.Immutable;
using System.Text.Json;
using Json.Pointer;
using MeshWeaver.Messaging;

namespace MeshWeaver.Data;

/// <summary>
/// Provides methods and constants for working with layout area references.
/// </summary>
public record LayoutAreaReference : WorkspaceReference<EntityStore>
{
    public LayoutAreaReference(string area)
    {
        Area = area;
        parameters = new(ParseParameters);
    }

    private IReadOnlyDictionary<string, string?> ParseParameters()
    {
        if (Id is null)
            return ImmutableDictionary<string, string?>.Empty;

        var parts = Id.ToString()!.Split('?').Skip(1).SelectMany(x => x.Split('&', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (parts.Length == 0)
            return ImmutableDictionary<string, string?>.Empty;

        return parts.Select(p =>
            {
                var split = p.Split('=');
                if (split.Length == 1)
                    return new KeyValuePair<string, string?>(Uri.UnescapeDataString(split[0]), null);
                if (split.Length == 2)
                    return new KeyValuePair<string, string?>(Uri.UnescapeDataString(split[0]),
                        Uri.UnescapeDataString(split[1]));
                throw new InvalidOperationException($"Invalid parameter format: {p}");
            })
            .ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Name of the layout area.
    /// </summary>
    public string Area { get; init; }
    /// <summary>
    /// Id of the layout area. Can contain optional parameters after a ? in URL format.
    /// </summary>
    public object? Id { get; init; }

    /// <summary>
    /// Can specify a separate layout.
    /// </summary>
    public string? Layout { get; init; }

    /// <summary>
    /// Constant for the data area.
    /// </summary>
    public const string Data = "data";

    /// <summary>
    /// The constant string for areas.
    /// </summary>
    public const string Areas = "areas";

    /// <summary>
    /// Gets the data pointer for the specified ID and extra segments.
    /// </summary>
    /// <param name="id">The ID for the data pointer.</param>
    /// <param name="extraSegments">The extra segments for the data pointer.</param>
    /// <returns>A string representing the data pointer.</returns>
    public static string GetDataPointer(string id, params string?[] extraSegments) =>
        JsonPointer.Create(
            new[] { Data, Encode(id) }
            .Concat(extraSegments)
            .Select(x => (PointerSegment)x!)
            .ToArray()
        )
        .ToString();

    public static string Encode(string id)
        => JsonSerializer.Serialize(id);

    /// <summary>
    /// Gets the control pointer for the specified area.
    /// </summary>
    /// <param name="area">The area for the control pointer.</param>
    /// <returns>A string representing the control pointer.</returns>
    public static string GetControlPointer(string area) =>
        JsonPointer.Create(Areas, JsonSerializer.Serialize(area)).ToString();


    /// <summary>
    /// Converts the layout area reference to an application href.
    /// </summary>
    /// <param name="address">The address for the href.</param>
    /// <returns>A string representing the application href.</returns>
    public string ToHref(object address)
    {
        var ret = $"{address}/{WorkspaceReference.Encode(Area)}";
        if (Id?.ToString() is { } s)
            ret = $"{ret}/{WorkspaceReference.Encode(s)}";
        return ret;
    }
    /// <summary>
    /// Converts the layout area reference to an application href.
    /// </summary>
    /// <param name="address">The address for the href.</param>
    /// <returns>A string representing the application href.</returns>
    public string ToHref(Address address)
    {
        var ret = $"{address}/{WorkspaceReference.Encode(Area)}";
        if (Id?.ToString() is { } s)
            ret = $"{ret}/{WorkspaceReference.Encode(s)}";
        return ret;
    }

    public string ToHref(string addressType, string addressId)
    {
        var ret = $"area/{addressType}/{addressId}/{WorkspaceReference.Encode(Area)}";
        if (Id?.ToString() is { } s)
            ret = $"{ret}/{WorkspaceReference.Encode(s)}";
        return ret;

    }

    private readonly Lazy<IReadOnlyDictionary<string, string?>> parameters = new();
    public string? GetParameterValue(string name)
    {
        return parameters.Value.GetValueOrDefault(name);
    }

    public bool HasParameter(string name) => parameters.Value.ContainsKey(name);

    // Override the generated Equals and GetHashCode to exclude the parameters field
    public virtual bool Equals(LayoutAreaReference? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return string.Equals(Area, other.Area, StringComparison.Ordinal) &&
               Equals(Id, other.Id) &&
               string.Equals(Layout, other.Layout, StringComparison.Ordinal);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(
            Area ?? string.Empty,
            Id ?? string.Empty,
            Layout ?? string.Empty
        );
    }
}
