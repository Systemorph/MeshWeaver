using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<EntityStore>
{
    public const string CollectionName = "LayoutArea";
    public object Options { get; init; }
}

