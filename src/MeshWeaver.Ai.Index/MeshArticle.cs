using System.ComponentModel.DataAnnotations;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace MeshWeaver.Ai.Index
{
    public record MeshArticle(
        [property: Key][property: SimpleField(IsKey = true)] string Url,
        [property: SearchableField(IsFilterable = true, IsSortable = true)] string Name,
        [property: SearchableField(IsFilterable = true, IsSortable = true)] string Description,
        string Thumbnail,
        [property: SearchableField(IsFilterable = true, IsSortable = true)] DateTime Published,
        [property: SearchableField(IsFilterable = true, IsSortable = true)] IReadOnlyCollection<string> Authors,
        [property: SearchableField(IsFilterable = true, IsSortable = true, IsFacetable = true, AnalyzerName = LexicalAnalyzerName.Values.StandardLucene)]
        IReadOnlyCollection<string> Tags
    )
    {
        [property: SearchableField(IsFilterable = true, IsSortable = true)] 
        public int Views { get; init; }
        [property: SearchableField(IsFilterable = true, IsSortable = true)] 
        public int Likes { get; init; }
        [VectorSearchField(VectorSearchDimensions = 1536, VectorSearchProfileName = "vector-profile")]
        public float[] VectorRepresentation { get; init; }
    }
}
