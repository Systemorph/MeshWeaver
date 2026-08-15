using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// Pins the boot-pack contract: installing the observability engine assembly via
/// <see cref="MeshBuilder.InstallAssemblies"/> (the <c>Modules:Assemblies</c> path) applies the
/// SAME <c>AddLogWatch()</c> a compiled-in portal calls, and registers the
/// <see cref="ILogIncidentIngest"/> contract seam the portal's compiled HTTP surface resolves.
/// </summary>
public class ObservabilityBootPackTest
{
    [Fact]
    public void InstallingTheAssembly_AppliesAddLogWatch()
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());

        builder.InstallAssemblies(typeof(ObservabilityProviderAttribute).Assembly.Location);

        var services = serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        Assert.Contains(services, d => d.ServiceType == typeof(LogIncidentIngestService));
        Assert.Contains(services, d => d.ServiceType == typeof(ILogIncidentIngest));
        Assert.Contains(services, d => d.ServiceType == typeof(LogIncidentControlPlane));
    }

    [Fact]
    public void TheContractSeam_ForwardsToTheConcreteIngestSingleton()
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());
        builder.InstallAssemblies(typeof(ObservabilityProviderAttribute).Assembly.Location);

        var services = serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        // The seam must be a singleton FACTORY forward (one ingest pipeline, resolved off the
        // concrete registration), never a second construction of the service.
        var seam = Assert.Single(services, d => d.ServiceType == typeof(ILogIncidentIngest));
        Assert.Equal(ServiceLifetime.Singleton, seam.Lifetime);
        Assert.NotNull(seam.ImplementationFactory);
        Assert.Null(seam.ImplementationType);
    }
}
