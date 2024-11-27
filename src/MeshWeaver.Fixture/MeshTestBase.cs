using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace MeshWeaver.Fixture
{
    public class MeshTestBase : TestBase
    {
        protected record MeshAddress;

        protected record ClientAddress;
        protected readonly MeshAddress Address = new();
        protected virtual MeshBuilder CreateBuilder() => new(Services, Address);

        protected MeshTestBase(ITestOutputHelper output)
            : base(output)
        {
            var builder = CreateBuilder();
            meshHub = new(() => builder.BuildHub(ServiceProvider));
        }


        private readonly Lazy<IMessageHub> meshHub; 
        protected IMessageHub MeshHub => meshHub.Value;


        protected virtual MessageHubConfiguration ConfigureClient(MessageHubConfiguration config)
            => config;
        protected virtual IMessageHub GetClient()
        {
            var ret = MeshHub.ServiceProvider.CreateMessageHub(new ClientAddress(), ConfigureClient);
            ServiceProvider.GetRequiredService<IRoutingService>().RegisterHubAsync(ret);
            return ret;
        }
    }
}
