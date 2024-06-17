using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<EntityStore>
{
    public object Options { get; init; }
    public const string Data = "data";
    public const string Areas = "areas";

    public static string GetDataPointer(string id) => $"/{Data}/{JsonSerializer.Serialize(id).Replace("/", "~1")}";
    public static string GetControlPointer(string area) => $"/{Areas}/{JsonSerializer.Serialize(area).Replace("/", "~1")}";
}

