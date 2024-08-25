using MeshWeaver.ShortGuid;

namespace MeshWeaver.Messaging;


public static class StreamProviders
{
    public const string SMS = nameof(SMS);
    
}



public record MeshNode(string Id, string BaseDirectory, string AssemblyLocation, string HubConfiguration)
{
    public const string MessageIn = nameof(MessageIn);
    public string Thumbnail { get; init; }
    public string StreamProvider { get; init; } = StreamProviders.SMS;
    public string Namespace { get; init; } = MessageIn;
}


public record ArticleEntry(string Name, string Description, string Image, string Url)
{
    public string Id { get; init; } = Guid.NewGuid().AsString();
}

