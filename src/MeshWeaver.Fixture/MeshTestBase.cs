using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace MeshWeaver.Fixture
{
    public class MeshTestBase : TestBase
    {
        [Inject] protected IRoutingService RoutingService { get; set; }
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
        protected virtual async Task<IMessageHub> GetClient(object address = null)
        {
            address ??= new ClientAddress();
            var ret = MeshHub.ServiceProvider.CreateMessageHub(address, ConfigureClient);
            await RoutingService.RegisterHubAsync(ret);
            return ret;
        }
    }
}
