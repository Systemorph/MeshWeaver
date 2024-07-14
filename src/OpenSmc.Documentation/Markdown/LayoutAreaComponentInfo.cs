using System.Collections.Immutable;
using Markdig.Parsers;
using Markdig.Syntax;
using Microsoft.Extensions.Primitives;
using OpenSmc.Layout;

namespace OpenSmc.Documentation.Markdown;

public class LayoutAreaComponentInfo(string Area, BlockParser blockParser)
    : ContainerBlock(blockParser)
{
    public ImmutableDictionary<string, StringValues> Options { get; set; }

    public string SourceReference { get; set; }
    public object Address { get; set; }
    public object Id { get; set; }

    public SourceInfo Source { get; set; }

    public DisplayMode DisplayMode { get; set; }
    public LayoutAreaReference Reference =>
        new LayoutAreaReference(Area) { Id = Id, Options = Options };
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
