using MeshWeaver.Catalog.Domain;

namespace MeshWeaver.Catalog.Layout;

public record CatalogItemData()
{
    public CatalogItemData(MeshDocument Document, MeshNode Node, User User) : this()
    {
        Title = Document.Title;
        Description = Document.Description;
        Thumbnail = Document.Thumbnail;
        Created = Document.Created;
        Tags = Document.Tags;
        Views = Document.Views;
        Likes = Document.Likes;
        Author = User.Name;
        Avatar = User.Avatar;
    }

    public string Title { get; init; }
    public string Description { get; init; }
    public string Thumbnail { get; init; }
    public DateTime Created { get; init; }
    public IEnumerable<string> Tags { get; init; }
    public int Views { get; init; }
    public int Likes { get; init; }
    public string Author { get; init; }
    public string Avatar { get; init; }
}
