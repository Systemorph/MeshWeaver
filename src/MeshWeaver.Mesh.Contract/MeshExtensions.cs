using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

public static class MeshExtensions
{

    public static readonly IReadOnlyDictionary<string, Type> MeshAddressTypes = new Dictionary<string, Type>()
    {
        { typeof(Address).FullName!, typeof(Address)},
        { MeshAddress.TypeName, typeof(MeshAddress) },
        { ApplicationAddress.TypeName, typeof(ApplicationAddress) },
        { KernelAddress.TypeName, typeof(KernelAddress) },
        { NotebookAddress.TypeName, typeof(NotebookAddress) },
        { SignalRAddress.TypeName, typeof(SignalRAddress) },
        { UiAddress.TypeName, typeof(UiAddress) },
        { ArticlesAddress.TypeName, typeof(ArticlesAddress) },
        { HostedAddress.TypeName, typeof(HostedAddress)}
    };


    public static MessageHubConfiguration AddMeshTypes(this MessageHubConfiguration config)
    {
        MeshAddressTypes.ForEach(kvp => config.TypeRegistry.WithType(kvp.Value, kvp.Key));
        config.TypeRegistry.WithTypes(typeof(PingRequest), typeof(PingResponse), typeof(MeshNode));
        return config;
    }


    public static Address MapAddress(string addressType, string id)
        => addressType switch
        {
            ApplicationAddress.TypeName => new ApplicationAddress(id),
            KernelAddress.TypeName => new KernelAddress(id),
            NotebookAddress.TypeName => new NotebookAddress(id),
            UiAddress.TypeName => new UiAddress(id),
            MeshAddress.TypeName => new MeshAddress(id),
            _ => throw new NotSupportedException($"Address type '{addressType}' is not supported.")
        };

}
