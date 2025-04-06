namespace MeshWeaver.Messaging;

public record AccessContext
{
    public string ObjectId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Email { get; init; } = string.Empty;
    public IReadOnlyCollection<string> Roles { get; init; } = [];
}
