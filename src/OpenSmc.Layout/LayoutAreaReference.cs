using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<JsonElement>
{
    public object Options { get; init; }
    public const string Data = "data";
    public const string Areas = "areas";

    public static string GetDataPointer(string id) => $"/{Data}/{id.Replace("/", "~1")}";
    public static string GetControlPointer(string area) => $"/{Areas}/{area}";
}

