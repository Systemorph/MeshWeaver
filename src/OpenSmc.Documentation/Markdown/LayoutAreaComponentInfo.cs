using System.Collections.Immutable;
using Microsoft.Extensions.Primitives;
using OpenSmc.Layout;

namespace OpenSmc.Documentation.Markdown;

public record LayoutAreaComponentInfo(string Area)
{
    public ImmutableDictionary<string,StringValues> Options { get; init; }

    public string SourceReference { get; init; }
    public object Address { get; init; }
    public object Id { get; init; }

    public SourceInfo Source { get; init; }

    public DisplayMode DisplayMode { get; init; }
    public LayoutAreaReference Reference => new LayoutAreaReference(Area) { Id = Id, Options = Options };
}

public record SourceInfo(string Type, string Reference, string Address);

public enum DisplayMode
{
    ViewWithSourceMenu,
    ViewOnly,
    SourceOnly,
    SourceThenView,
    ViewThenSource
}
