using System.Collections.Immutable;
using OpenSmc.Data;

namespace OpenSmc.Layout;

public record LayoutAreaReference(string Area) : WorkspaceReference<LayoutAreaCollection>
{
    public object Options { get; init; }
}


/// <summary>
/// If you are interested in a particular control, use collection.areas['area1/subarea']
/// </summary>
/// <param name="Reference"></param>
public record LayoutAreaCollection(LayoutAreaReference Reference)
{
    public ImmutableDictionary<string, UiControl> Areas { get; init; } = ImmutableDictionary<string, UiControl>.Empty;
}