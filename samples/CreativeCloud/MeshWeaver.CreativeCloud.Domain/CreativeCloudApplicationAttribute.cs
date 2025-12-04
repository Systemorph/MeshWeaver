using MeshWeaver.CreativeCloud.Domain;
using MeshWeaver.Mesh;

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
}
