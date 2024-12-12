using System.ComponentModel.DataAnnotations;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh
{
    [GenerateSerializer]
    public record MeshNode(
        string AddressType,
        string AddressId, 
        string Name, 
        string PackageName
        )
    {
        [Key] public string Key { get; init; } = $"{AddressType}/{AddressId}";

        public const string MeshIn = nameof(MeshIn);
        public string ThumbNail { get; init; }
        public string StreamProvider { get; init; } = StreamProviders.Mesh;

        public string Namespace { get; init; } = MeshNode.MeshIn;
        public string ContentPath { get; init; } = "wwwroot";
        public string ArticlePath { get; init; } = "articles";

        public string AssemblyLocation { get; init; }

        public Func<IServiceProvider, string, IMessageHub> HubFactory { get; init; }
    }
}
