using MeshWeaver.CreativeCloud.Domain;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

[assembly: CreativeCloudApplication]

namespace MeshWeaver.CreativeCloud.Domain;

/// <summary>
/// Mesh node attribute for the CreativeCloud content portal application.
/// </summary>
public class CreativeCloudApplicationAttribute : MeshNodeAttribute
{
    /// <summary>
    /// Address of the CreativeCloud application.
    /// </summary>
    public static readonly ApplicationAddress Address = new("CreativeCloud");

    /// <summary>
    /// Mesh catalog entry.
    /// </summary>
    public override IEnumerable<MeshNode> Nodes =>
    [
        CreateFromHubConfiguration(
            Address,
            nameof(CreativeCloud),
            CreativeCloudApplicationExtensions.ConfigureCreativeCloudApplication
        )
    ];

    /// <summary>
    /// Node factories for entity detail hubs (story, post, video, event, person, arch).
    /// Each entity type gets its own hub with Overview, Workflow, Dependencies views.
    /// </summary>
    public override IEnumerable<Func<Address, MeshNode?>> NodeFactories =>
    [
        CreateEntityNodeFactory(StoryAddress.TypeName, CreativeCloudApplicationExtensions.ConfigureStoryHub),
        CreateEntityNodeFactory(PostAddress.TypeName, CreativeCloudApplicationExtensions.ConfigurePostHub),
        CreateEntityNodeFactory(VideoAddress.TypeName, CreativeCloudApplicationExtensions.ConfigureVideoHub),
        CreateEntityNodeFactory(EventAddress.TypeName, CreativeCloudApplicationExtensions.ConfigureEventHub),
        CreateEntityNodeFactory(PersonAddress.TypeName, CreativeCloudApplicationExtensions.ConfigurePersonHub),
        CreateEntityNodeFactory(ArchAddress.TypeName, CreativeCloudApplicationExtensions.ConfigureArchHub),
    ];

    /// <summary>
    /// Address types for entity detail hubs.
    /// </summary>
    public override IEnumerable<KeyValuePair<string, Type>> AddressTypes =>
    [
        new(PersonAddress.TypeName, typeof(PersonAddress)),
        new(ArchAddress.TypeName, typeof(ArchAddress)),
        new(StoryAddress.TypeName, typeof(StoryAddress)),
        new(VideoAddress.TypeName, typeof(VideoAddress)),
        new(EventAddress.TypeName, typeof(EventAddress)),
        new(PostAddress.TypeName, typeof(PostAddress)),
    ];

    private static Func<Address, MeshNode?> CreateEntityNodeFactory(
        string typeName,
        Func<MessageHubConfiguration, MessageHubConfiguration> hubConfig) =>
        address => address.Type == typeName
            ? new MeshNode(address.Type, address.Id, address.ToString())
            {
                HubConfiguration = hubConfig
            }
            : null;
}
