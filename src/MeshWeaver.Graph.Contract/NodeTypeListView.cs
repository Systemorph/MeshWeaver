using System.Text.Json.Serialization;
using MeshWeaver.Layout;

namespace MeshWeaver.Graph.Configuration;

/// <summary>The presentation a NodeType declares for a list of its instances.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NodeTypeListMode>))]
public enum NodeTypeListMode
{
    /// <summary>Thumbnail cards — the default when a type declares nothing.</summary>
    Cards,
    /// <summary>One row per instance: icon, name and description.</summary>
    List,
    /// <summary>A data grid: one row per instance, one column per <see cref="NodeTypeListView.Columns"/> entry,
    /// a header that sorts in the store.</summary>
    Table,
}

/// <summary>
/// How a NodeType's instances are LISTED — declared once on the type
/// (<see cref="NodeTypeDefinition.ListView"/>) and honoured by every children catalog that lists
/// them: the <c>Search</c> area of a node whose children are all of this type, the type's own
/// instance catalog, and this type's section of a catalog grouped by type.
///
/// <para>Authored as data on the NodeType node:</para>
/// <code>
/// "listView": {
///   "mode": "Table",
///   "columns": [ { "title": "Model", "field": "content.model" },
///                { "title": "Finished", "field": "content.finishedAt", "format": "yyyy-MM-dd HH:mm" } ],
///   "pageSize": 50,
///   "sort": "content.finishedAt-desc"
/// }
/// </code>
/// <para>Nothing declared (or <see cref="NodeTypeListMode.Cards"/>) keeps the cards. A URL that
/// asks for a shape explicitly (<c>?groupBy=</c>) wins over the declaration.</para>
/// </summary>
public record NodeTypeListView
{
    /// <summary>The presentation: cards (default), list rows or a table.</summary>
    public NodeTypeListMode Mode { get; init; } = NodeTypeListMode.Cards;

    /// <summary>The table's columns, left to right (<see cref="MeshSearchColumn"/>: a header title and
    /// the query selector its cells read — <c>name</c>, <c>lastModified</c>, <c>content.model</c>).
    /// Ignored by the other modes; a table without columns shows one Name column.</summary>
    public IReadOnlyList<MeshSearchColumn>? Columns { get; init; }

    /// <summary>Hits per store-side page; null keeps the catalog's default page size. A
    /// <c>?pageSize=</c> in the URL wins.</summary>
    public int? PageSize { get; init; }

    /// <summary>The default order, as a query sort selector (<c>content.finishedAt-desc</c>,
    /// <c>name</c>); applied in the store. A header click replaces it. Null keeps the catalog's order.</summary>
    public string? Sort { get; init; }

    /// <summary>The search control's render mode for <see cref="Mode"/>.</summary>
    [JsonIgnore]
    public MeshSearchRenderMode RenderMode => Mode switch
    {
        NodeTypeListMode.List => MeshSearchRenderMode.List,
        NodeTypeListMode.Table => MeshSearchRenderMode.Table,
        _ => MeshSearchRenderMode.Flat,
    };

    /// <summary>Whether the declaration changes anything — cards are what a catalog shows anyway.</summary>
    [JsonIgnore]
    public bool IsDeclared => Mode != NodeTypeListMode.Cards;
}
