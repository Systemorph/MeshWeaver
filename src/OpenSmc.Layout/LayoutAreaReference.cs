using System.Collections.Immutable;
using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<LayoutAreaCollection>
{
    public object Options { get; init; }
}

public record LayoutAreaCollection(ImmutableDictionary<LayoutAreaReference, UiControl> Instances) ;