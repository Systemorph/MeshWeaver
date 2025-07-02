#nullable enable
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MeshWeaver.Messaging;

namespace MeshWeaver.Mesh;

public record SystemLog(
    string Service,
    string ServiceId,
    string Level,
    DateTimeOffset Timestamp,
    string Message,
    string? Exception,
    IReadOnlyDictionary<string, object>? Properties
)
{
    public long Id { get; init; }
}
public record MessageLog(
    string Service,
    string ServiceId,
    DateTimeOffset Timestamp,
    string Address,
    string MessageId,
    IReadOnlyDictionary<string, object>? Message,
    string? Sender,
    string? Target,
    string? State,
    IReadOnlyDictionary<string, object>? AccessContext,
    IReadOnlyDictionary<string, object>? Properties)
{
    public long Id { get; init; }
}
public record MeshNode(
    string AddressType,
    string? AddressId,
    string Name
)
{
    [Key] public string Key { get; init; } = AddressId == null ? AddressType : $"{AddressType}/{AddressId}";
    public const string MeshIn = nameof(MeshIn);
    public string? ThumbNail { get; init; }
    public string? StreamProvider { get; init; }
    public string? Namespace { get; init; }
    public string? AssemblyLocation { get; init; }
    [JsonIgnore]
    public Func<MessageHubConfiguration, MessageHubConfiguration>? HubConfiguration { get; init; }
    public string? StartupScript
    {
        get; init;
    }
    public RoutingType RoutingType { get; init; }
    public InstantiationType InstantiationType { get; set; }

    public virtual Address CreateAddress(string addressType, string addressId)
        => MeshExtensions.MapAddress(addressType, addressId);
}

public enum InstantiationType
{
    HubConfiguration,
    Script
}
