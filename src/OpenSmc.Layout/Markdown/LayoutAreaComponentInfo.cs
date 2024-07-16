using System.Collections.Immutable;
using Markdig.Parsers;
using Markdig.Syntax;

namespace OpenSmc.Layout.Markdown;

public class LayoutAreaComponentInfo(string area, BlockParser blockParser)
    : ContainerBlock(blockParser)
{
    public ImmutableDictionary<string, object> Options { get; set; }

    public string Area => area;
    public string DivId { get; } = Guid.NewGuid().ToString();

    public string SourceReference { get; set; }
    public object Address { get; set; }
    public object Id { get; set; }

    public SourceInfo Source { get; set; }

    public DisplayMode DisplayMode { get; set; }
    public LayoutAreaReference Reference =>
        new (Area) { Id = Id, Options = Options };
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
