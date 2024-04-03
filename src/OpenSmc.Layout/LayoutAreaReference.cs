using System.Collections.Immutable;
using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<LayoutAreaCollection>
{
    public object Options { get; init; }
}

public record LayoutAreaCollection(LayoutAreaReference Reference)
{
    public ImmutableDictionary<string, UiControl> Areas { get; init; } = ImmutableDictionary<string, UiControl>.Empty;
}