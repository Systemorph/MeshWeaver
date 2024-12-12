using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh
{
    public static class MeshExtensions
    {

        public static readonly IReadOnlyDictionary<string, Type> MeshAddressTypes = new Dictionary<string, Type>()
        {
            { MeshAddress.TypeName, typeof(MeshAddress) },
            { ApplicationAddress.TypeName, typeof(ApplicationAddress) },
            { KernelAddress.TypeName, typeof(KernelAddress) },
            { NotebookAddress.TypeName, typeof(NotebookAddress) },
            { SignalRClientAddress.TypeName, typeof(SignalRClientAddress) },
            { UiAddress.TypeName, typeof(UiAddress) }
        };


        public static MessageHubConfiguration AddMeshTypes(this MessageHubConfiguration config)
        {
            MeshAddressTypes.ForEach(kvp => config.TypeRegistry.WithType(kvp.Value, kvp.Key));
            config.TypeRegistry.WithTypes(typeof(PingRequest), typeof(PingResponse));
            return config;
        }


        public static object MapAddress(string addressType, string id)
            => addressType switch
            {
                ApplicationAddress.TypeName => new ApplicationAddress(id),
                KernelAddress.TypeName => new KernelAddress { Id = id },
                NotebookAddress.TypeName => new NotebookAddress(id),
                UiAddress.TypeName => new UiAddress { Id = id },
                MeshAddress.TypeName => new MeshAddress(),
                _ => throw new NotSupportedException($"Address type '{addressType}' is not supported.")
            };

    }
}
