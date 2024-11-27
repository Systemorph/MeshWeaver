using MeshWeaver.Application;
using MeshWeaver.Demo.ViewModel;
using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
[assembly: DemoMeshNode]

namespace MeshWeaver.Demo.ViewModel
{
    public class DemoMeshNodeAttribute : MeshNodeAttribute
    {
        private static readonly ApplicationAddress Address = new("Demo");

        public override IMessageHub Create(IServiceProvider serviceProvider, MeshNode node)
            => CreateIf(node.Matches(Address), () => serviceProvider.CreateMessageHub(
                    Address,
                    application =>
                        application
                            .AddDemoViewModels()
                )
            );



        public override IEnumerable<MeshNode> Nodes =>
            [MeshExtensions.GetMeshNode(Address, typeof(DemoMeshNodeAttribute).Assembly.Location)];
    }
}
