using System.ComponentModel.DataAnnotations;
using MeshWeaver.Application;

namespace MeshWeaver.Mesh.Contract
{
    [GenerateSerializer]
    public record MeshNode(
        string AddressType,
        string Id, 
        string Name, 
        string BasePath, 
        string AssemblyLocation
        )
    {
        [Key] public string Key { get; init; } = $"{AddressType}/{Id}";

        public const string MeshIn = nameof(MeshIn);
        public string ThumbNail { get; init; }
        public string StreamProvider { get; init; } = StreamProviders.Mesh;

        public string Namespace { get; init; } = MeshNode.MeshIn;
        public string ContentPath { get; init; } = "wwwroot";
        public string ArticlePath { get; init; } = "articles";

        public bool Matches(object address)
        {
            if (address == null)
                return false;
            return address.GetType().FullName == AddressType && Id == address.ToString();
        }
    }
}
