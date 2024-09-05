using MeshWeaver.Application;
using MeshWeaver.Documentation;
using MeshWeaver.Layout;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using MeshWeaver.Overview;

[assembly:MeshWeaverOverview]

namespace MeshWeaver.Overview;

public class MeshWeaverOverviewAttribute : MeshNodeAttribute
{
    public override IMessageHub Create(IServiceProvider serviceProvider, object address)
        => serviceProvider.CreateMessageHub(address, configuration => configuration
            .AddLayout(layout => layout)
            .AddDocumentation());

    public override MeshNode Node =>
        GetMeshNode(new ApplicationAddress("MeshWeaver", "Overview"), typeof(MeshWeaverOverviewAttribute).Assembly.Location);

}

