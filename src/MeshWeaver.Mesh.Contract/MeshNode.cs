using MeshWeaver.Application;

namespace MeshWeaver.Mesh.Contract
{
    [GenerateSerializer]
    public record MeshNode(
        string Id, 
        string Name, 
        string BasePath, 
        string AssemblyLocation
        )
    {
        public const string MeshIn = nameof(MeshIn);
        public string ThumbNail { get; init; }
        public string StreamProvider { get; init; } = StreamProviders.Mesh;

        public string Namespace { get; init; } = MeshNode.MeshIn;
        public string ContentPath { get; init; } = "wwwroot";
        public string ArticlePath { get; init; } = "articles";
        public string AddressType { get; init; } = typeof(ApplicationAddress).FullName;
    }
}
