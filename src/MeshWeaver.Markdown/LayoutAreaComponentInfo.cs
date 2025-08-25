using Markdig.Parsers;
using Markdig.Syntax;
using MeshWeaver.Data;

namespace MeshWeaver.Markdown;

public class LayoutAreaComponentInfo : ContainerBlock
{

    public LayoutAreaComponentInfo(string url, BlockParser blockParser) : base(blockParser)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL cannot be null or empty", nameof(url));
            
        var parts = url.Split('/');
        if (parts.Length < 3)
            throw new ArgumentException($"Invalid URL format '{url}'. Expected format: 'addressType/addressId/area' or 'addressType/addressId/area/areaId'", nameof(url));
            
        Address = $"{parts[0]}/{parts[1]}";
        Area = parts[2];
        
        if (string.IsNullOrWhiteSpace(Address.ToString()))
            throw new ArgumentException($"Invalid address in URL '{url}'", nameof(url));
        if (string.IsNullOrWhiteSpace(Area))
            throw new ArgumentException($"Invalid area in URL '{url}'", nameof(url));
            
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

    public LayoutAreaComponentInfo(string address, string area, string? id, BlockParser blockParser) : base(blockParser)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("Address cannot be null or empty", nameof(address));
        if (string.IsNullOrWhiteSpace(area))
            throw new ArgumentException("Area cannot be null or empty", nameof(area));
            
        Address = address;
        Area = area;
        Id = id;
    }

    public string Area { get; }

    public object Address { get;  }
    public object? Id { get;}

    public LayoutAreaReference Reference =>
        new (Area) { Id = Id };
}

public record SourceInfo(string Type, string Reference, string Address);

