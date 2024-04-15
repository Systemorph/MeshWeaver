using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<WorkspaceState>
{
    public object Options { get; init; }
}

