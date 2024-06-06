using System.Text.Json;
using System.Text.Json.Nodes;
using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<JsonElement>
{
    public object Options { get; init; }
    public const string Data = nameof(Data);
    public const string Areas = nameof(Areas);
}

