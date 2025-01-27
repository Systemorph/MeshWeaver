using System.Collections.Immutable;

namespace MeshWeaver.Layout;

public record LayoutAreaDefinition(string Area)
{
    public string Description { get; set; }
    public LayoutAreaDefinition WithDescription(string description) => 
        this with { Description = description };

    public ImmutableList<string> CRefs { get; init; } = [];
    public LayoutAreaDefinition WithReferences(params IEnumerable<string> reference) => 
        this with{CRefs = CRefs.AddRange(reference.Where(x => x != null))};
}
