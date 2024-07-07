using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Pointer;
using Microsoft.Extensions.Primitives;
using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<EntityStore>
{
    public string Id { get; set; }
    public Dictionary<string, StringValues> Options { get; init; }
    public const string Data = "data";
    public const string Areas = "areas";
    
    public static string GetDataPointer(string id) => JsonPointer.Create(Data, JsonSerializer.Serialize(id)).ToString();

    public static string GetControlPointer(string area) => JsonPointer.Create(Areas, JsonSerializer.Serialize(area)).ToString();

}
