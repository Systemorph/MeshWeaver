using MeshWeaver.Application;
using MeshWeaver.Demo.ViewModel;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
[assembly: DemoMeshNode]

namespace MeshWeaver.Demo.ViewModel
{
    public class DemoMeshNodeAttribute : MeshNodeAttribute
    {
        public override IMessageHub Create(IServiceProvider serviceProvider, object address)
            =>             serviceProvider.CreateMessageHub(
                address,
                application =>
                    application
                        .AddDemoViewModels()
            );

        public override MeshNode Node =>
            GetMeshNode(new ApplicationAddress("MeshWeaver", "Demo"), typeof(DemoMeshNodeAttribute).Assembly.Location);
    }
}
