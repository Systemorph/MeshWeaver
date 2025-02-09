using System.ComponentModel.DataAnnotations;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

public record MeshNode(
    string AddressType,
    string AddressId, 
    string Name
)
{
    [Key] public string Key { get; init; } = AddressId == null ? AddressType : $"{AddressType}/{AddressId}";
    public const string MeshIn = nameof(MeshIn);
    public string ThumbNail { get; init; }
    public string StreamProvider { get; init; }
    public string Namespace { get; init; }
    public string AssemblyLocation { get; init; }
    public Func<MessageHubConfiguration, MessageHubConfiguration> HubConfiguration { get; init; }
    public string StartupScript { get; init; }
    public RoutingType RoutingType { get; init; }

    public virtual Address CreateAddress(string addressType, string addressId)
        => MeshExtensions.MapAddress(addressType, addressId);
}
