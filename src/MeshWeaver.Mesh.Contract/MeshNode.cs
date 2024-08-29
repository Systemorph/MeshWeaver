namespace MeshWeaver.Mesh.Contract
{
    [GenerateSerializer]
    public record MeshNode(string Id, string Name, string BasePath, string AssemblyLocation, string ContentPath)
    {
        public const string MessageIn = nameof(MessageIn);
        public string Thumbnail { get; init; }
        public string StreamProvider { get; init; } = StreamProviders.Memory;
        public string Namespace { get; init; } = MessageIn;
    }
}
