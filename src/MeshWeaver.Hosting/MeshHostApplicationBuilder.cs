﻿using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Hosting;

namespace MeshWeaver.Hosting;

public record MeshHostApplicationBuilder : MeshBuilder
{
    public MeshHostApplicationBuilder(IHostApplicationBuilder Host, object address) : base(x => x.Invoke(Host.Services), address)
    {
        this.Host = Host;
        Host.ConfigureContainer(new MessageHubServiceProviderFactory(BuildHub));
    }

    public IHostApplicationBuilder Host { get; }
}
public record MeshHostBuilder : MeshBuilder
{
    public MeshHostBuilder(IHostBuilder Host, object address) : base(c => Host.ConfigureServices(services => c(services)), address)
    {
        this.Host = Host;
        Host.UseServiceProviderFactory(new MessageHubServiceProviderFactory(BuildHub));
    }


    public IHostBuilder Host { get; }
}