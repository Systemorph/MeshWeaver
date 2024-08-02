using System.Collections.Immutable;
using System.Text.Json;
using Json.Pointer;
using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<EntityStore>
{
    public object Id { get; init; }
    public IReadOnlyDictionary<string, object> Options { get; init; } = ImmutableDictionary<string, object>.Empty;
    public string Layout { get; init; }

    public const string Data = "data";
    public const string Areas = "areas";
    public const string Properties = "properties";

    public static string GetDataPointer(string id) =>
        JsonPointer.Create(Data, JsonSerializer.Serialize(id)).ToString();

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

    public string ToHref(object address)
    {
        var ret = $"{address}/{Uri.EscapeDataString(Area)}";
        if (Id?.ToString() is { } s)
            ret = $"{ret}/{Uri.EscapeDataString(s)}";
        if (Options.Any())
            ret = $"ret?{string.Join('&',
                Options
                    .Select(x => $"{x.Key}={Uri.EscapeDataString(x.Value?.ToString() ?? "")}"))}";
        return ret;
    }


}
