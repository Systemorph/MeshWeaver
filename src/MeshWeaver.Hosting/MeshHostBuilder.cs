using MeshWeaver.Mesh.Contract;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting
{
    public record MeshHostBuilder : MeshBuilder
    {
        public MeshHostBuilder(IHostApplicationBuilder Host, object address) : base(Host.Services, address)
        {
            this.Host = Host;
            Host.ConfigureContainer(new MessageHubServiceProviderFactory(BuildHub));
        }

        public IHostApplicationBuilder Host { get; }
    }
}
