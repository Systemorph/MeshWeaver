namespace MeshWeaver.Mesh.Contract
{
    [GenerateSerializer]
    public record MeshNode(string Id, string Name, string BasePath, string AssemblyLocation, string ContentPath, string ThumbNail = null, string StreamProvider = StreamProviders.Mesh, string Namespace = MeshNode.MeshIn)
    {
        public const string MeshIn = nameof(MeshIn);
    }
}
