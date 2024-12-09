using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh
{
    public static class MeshExtensions
    {
        public static MeshNode GetMeshNode(object address, string location)
            => GetMeshNode(address.GetType().FullName, address.ToString(), location);

        public static MeshNode GetMeshNode(string addressType, string id, string location)
        {
            var basePathLength = location.LastIndexOf(Path.DirectorySeparatorChar);
            return new(ApplicationAddress.TypeName, id, "Mesh Weaver Overview",
                location.Substring(0, basePathLength),
                location.Substring(basePathLength + 1))
            {
                AddressType = addressType,

            };
        }

        public static readonly IReadOnlyDictionary<string, Type> MeshAddressTypes = new Dictionary<string, Type>()
        {
            { MeshAddress.TypeName, typeof(MeshAddress) },
            { ApplicationAddress.TypeName, typeof(ApplicationAddress) },
            { NotebookAddress.TypeName, typeof(NotebookAddress) },
            { SignalRClientAddress.TypeName, typeof(SignalRClientAddress) },
            { UiAddress.TypeName, typeof(UiAddress) }
        };


        public static MessageHubConfiguration AddDefaultAddressTypes(this MessageHubConfiguration config)
        {
            MeshAddressTypes.ForEach(kvp => config.TypeRegistry.WithType(kvp.Value, kvp.Key));
            return config;
        }


    }
}
