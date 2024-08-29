using MeshWeaver.ShortGuid;

namespace MeshWeaver.Messaging;


public static class StreamProviders
{
    public const string Memory = nameof(Memory);
    
}



public record MeshNode(string Id, string Name, string BasePath, string AssemblyLocation, string ContentPath)
{
    public const string MessageIn = nameof(MessageIn);
    public string Thumbnail { get; init; }
    public string StreamProvider { get; init; } = StreamProviders.Memory;
    public string Namespace { get; init; } = MessageIn;
}


public record ArticleEntry(string Name, string Description, string Image, string Url)
{
    public string Id { get; init; } = Guid.NewGuid().AsString();
}

