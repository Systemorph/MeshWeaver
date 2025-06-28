using MeshWeaver.AI.Application;
using MeshWeaver.Mesh;

[assembly: AgentsApplication]

namespace MeshWeaver.AI.Application;

/// <summary>
/// Mesh node attribute for the Agents application
/// </summary>
public class AgentsApplicationAttribute : MeshNodeAttribute
{
    public override IEnumerable<MeshNode> Nodes =>
    [
        CreateFromHubConfiguration(
            ApplicationAddress.Agents,
            "Agents Application",
            config => config.ConfigureAgentsApplication()
        )
    ];
}
