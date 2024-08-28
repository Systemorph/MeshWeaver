using MeshWeaver.Catalog.Domain;

namespace MeshWeaver.Catalog.Layout;

public record CatalogItemData()
{
    public CatalogItemData(MeshDocument document, MeshNode node, User user) : this()
    {
        Title = document.Title;
        Description = document.Description;
        Thumbnail = document.Thumbnail;
        Created = document.Created;
        Tags = document.Tags;
        Views = document.Views;
        Likes = document.Likes;
        Author = user.Name;
        AuthorAvatar = user.Avatar;
        NodeName = node.Name;
    }

    public string Title { get; init; }
    public string Description { get; init; }
    public string Thumbnail { get; init; }
    public DateTime Created { get; init; }
    public IEnumerable<string> Tags { get; init; }
    public int Views { get; init; }
    public int Likes { get; init; }
    public string Author { get; init; }
    public string AuthorAvatar { get; init; }
    public string NodeName { get; init; }
    public string DocumentUrl { get; init; }
}
