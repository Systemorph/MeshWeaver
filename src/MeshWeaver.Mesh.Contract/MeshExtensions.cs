using MeshWeaver.Domain;
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
        { HostedAddress.TypeName, typeof(HostedAddress)},
        { PortalAddress.TypeName, typeof(PortalAddress)},
    };


    public static MessageHubConfiguration AddMeshTypes(this MessageHubConfiguration config)
    {
        MeshAddressTypes.ForEach(kvp => config.TypeRegistry.WithType(kvp.Value, kvp.Key));
        config.TypeRegistry.WithTypes(typeof(PingRequest), typeof(PingResponse), typeof(MeshNode));
        return config;
    }


    public static Address MapAddress(this ITypeRegistry typeRegistry, string addressType, string id)
    {
        var type = typeRegistry.GetType(addressType);
        if (type != null)
            return (Address)Activator.CreateInstance(type, id)!;
        return new Address(addressType, id);
    }


}
