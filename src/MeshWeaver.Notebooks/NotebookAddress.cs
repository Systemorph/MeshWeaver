using MeshWeaver.ShortGuid;

namespace MeshWeaver.Notebooks;

public record NotebookAddress()
{
    public NotebookAddress(string id) : this()
    {
        Id = id;
    }
    
    public string Id { get; init; } = Guid.NewGuid().AsString();
}