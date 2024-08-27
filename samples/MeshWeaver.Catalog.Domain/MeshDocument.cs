namespace MeshWeaver.Catalog.Domain;

public record MeshDocument(string Id, string Title, string MeshNodeId)
{
    public string Author { get; init; }
    public string Description { get; init; }
    public string Thumbnail { get; init; }
    public DateTime Created { get; init; }
    public IEnumerable<string> Tags { get; init; }
    public int Views { get; init; }
    public int Likes { get; init; }
}

public record MeshNodeFollower(string MeshNodeId, string UserId, DateTime FollowedOn);

public record UserFollower(string UserId, string FollowerId, DateTime FollowedOn);

public record MeshDocumentViews(string MeshDocumentId, string UserId, DateTime ViewedOn);

public record MeshDocumentLike(string MeshDocumentId, string UserId, DateTime LikedOn);

