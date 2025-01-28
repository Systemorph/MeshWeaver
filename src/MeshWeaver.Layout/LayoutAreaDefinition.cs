using System.Collections.Immutable;
using MeshWeaver.Utils;

namespace MeshWeaver.Layout;

public record LayoutAreaDefinition(string Area, string Url)
{
    public string Title { get; init; } = Area.Wordify();

    public LayoutAreaDefinition WithTitle(string title)
        => this with { Title = title };

    public string ImageUrl { get; init; } = "LayoutAreaDefinition.png";
    public LayoutAreaDefinition WithImageUrl(string imageUrl) =>
        this with { ImageUrl = imageUrl };

    public string Description { get; set; }
    public LayoutAreaDefinition WithDescription(string description) => 
        this with { Description = description };

    public ImmutableList<string> CRefs { get; init; } = [];
    public LayoutAreaDefinition WithReferences(params IEnumerable<string> reference) => 
        this with{CRefs = CRefs.AddRange(reference.Where(x => x != null))};

    public string Category { get; init; }
    public LayoutAreaDefinition WithCategory(string category)
        => this with { Category = category };
}
