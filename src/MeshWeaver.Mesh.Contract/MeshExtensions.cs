namespace MeshWeaver.Mesh
{
    public static class MeshExtensions
    {
        public static MeshNode GetMeshNode(object address, string location)
            => GetMeshNode(address.GetType().FullName, address.ToString(), location);

        public static MeshNode GetMeshNode(string addressType, string id, string location)
        {
            var basePathLength = location.LastIndexOf(Path.DirectorySeparatorChar);
            return new(typeof(ApplicationAddress).FullName, id, "Mesh Weaver Overview",
                location.Substring(0, basePathLength),
                location.Substring(basePathLength + 1))
            {
                AddressType = addressType,

            };
        }

        public static readonly Type[] MeshAddressTypes =
            [typeof(ApplicationAddress), typeof(NotebookAddress), typeof(SignalRClientAddress), typeof(UiAddress)];

    }
}
