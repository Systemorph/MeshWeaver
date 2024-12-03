using MeshWeaver.Application;
using MeshWeaver.Domain.Layout.Documentation;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using MeshWeaver.Overview;

[assembly: MeshWeaverOverview]

namespace MeshWeaver.Overview;

public class MeshWeaverOverviewAttribute : MeshNodeAttribute
{
    private static readonly ApplicationAddress Address = new ApplicationAddress("MeshWeaver");
    public override IMessageHub Create(IServiceProvider serviceProvider, MeshNode node)
        => CreateIf(node.Matches(Address), () => serviceProvider.CreateMessageHub(Address, configuration => configuration
            .AddLayout(layout => layout)
            .AddDocumentation()));

    public override IEnumerable<MeshNode> Nodes =>
        [MeshExtensions.GetMeshNode(Address, typeof(MeshWeaverOverviewAttribute).Assembly.Location)];

}

