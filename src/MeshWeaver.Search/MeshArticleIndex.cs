using System.ComponentModel.DataAnnotations;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;

namespace MeshWeaver.Search;

public class MeshArticleIndex
{
    [property: SimpleField(IsFilterable = true)]
    public string Url { get; set; }
    [property: SearchableField(IsFilterable = true, IsSortable = true)] public string Name { get; set; }
    [property: SearchableField(IsFilterable = true, IsSortable = true)] public string Description { get; set; }
    public string Thumbnail { get; set; }
    [property: SimpleField(IsFilterable = true, IsSortable = true)] public DateTime Published { get; set; }
    [property: SearchableField(IsFilterable = true)] public List<string> Authors { get; set; }
    [property: SearchableField(IsFilterable = true, IsFacetable = true, AnalyzerName = LexicalAnalyzerName.Values.StandardLucene)]
    public List<string> Tags { get; set; }
        [property: Key]
        [property: SimpleField(IsKey = true)]
        public string Path { get; set; }
    [property: SimpleField(IsFilterable = true, IsSortable = true)]
    public int Views { get; set; }
    [property: SimpleField(IsFilterable = true, IsSortable = true)]
    public int Likes { get; set; }
    [VectorSearchField(VectorSearchDimensions = 1536, VectorSearchProfileName = "vector-profile")]
    public float[] VectorRepresentation { get; set; }

}
