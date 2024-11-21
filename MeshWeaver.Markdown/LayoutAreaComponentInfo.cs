using Markdig.Parsers;
using Markdig.Syntax;

namespace MeshWeaver.Layout.Markdown;

public class LayoutAreaComponentInfo(string area, BlockParser blockParser)
    : ContainerBlock(blockParser)
{

    public string Area => area;
    public string DivId { get; set; } = Guid.NewGuid().ToString();

    public string Layout { get; set; }
    public string Address { get; set; }
    public object Id { get; set; }

    public LayoutAreaReference Reference =>
        new (Area) { Id = Id, Layout = Layout };
}

public record SourceInfo(string Type, string Reference, string Address);

