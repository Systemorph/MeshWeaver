using Markdig.Parsers;
using Markdig.Syntax;
using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown;

public class LayoutAreaComponentInfo : ContainerBlock
{

    public LayoutAreaComponentInfo(string url, object defaultAddress, BlockParser blockParser) : base(blockParser)
    {
        var parts = url.Split('/');
        Address = $"{{parts[0]}}/{parts[1]}";
        Area = parts[2];
        if (parts.Length == 3)
        {
            var optionalSplit = Area.Split('?');
            if (optionalSplit.Length > 1)
            {
                Area = optionalSplit[0];
                Id = optionalSplit[1];
            }
        }
        else if(parts.Length == 4)
        {
            Id = parts[3];
        }
        else
            Id = string.Join('/', parts.Skip(3));

    }

    public string Area { get; }

    public object Address { get;  }
    public object Id { get;}

    public LayoutAreaReference Reference =>
        new (Area) { Id = Id };
}

public record SourceInfo(string Type, string Reference, string Address);

