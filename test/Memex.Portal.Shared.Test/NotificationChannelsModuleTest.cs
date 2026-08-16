using System;
using System.Collections.Generic;
using System.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Notifications.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the notification delivery-channels module lane: installing the assembly via
/// <see cref="MeshBuilder.InstallAssemblies"/> (the <c>Modules:Assemblies</c> path) applies the
/// SAME <c>AddNotificationChannels()</c> a compiled-in host calls — registering the
/// <c>NotificationRule</c>/<c>NotificationChannel</c> node types AND the triage watcher (as a
/// singleton with an <see cref="IHostedService"/> forward, so it actually STARTS). Listing the DLL
/// is the complete activation; delisting removes the types from create contexts and stops triage.
/// </summary>
public class NotificationChannelsModuleTest
{
    [Fact]
    public void InstallingTheAssembly_RegistersTheTriageWatcherAndTheNodeTypes()
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());

        builder.InstallAssemblies(typeof(NotificationChannelsModuleAttribute).Assembly.Location);

        var services = serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        // The watcher: a mesh-scoped singleton plus an IHostedService FORWARD that starts it —
        // never a second IHostedService CONSTRUCTION of the type, which would mean two triage
        // pipelines double-escalating.
        Assert.Contains(services, d =>
            d.ServiceType == typeof(NotificationTriageService) && d.Lifetime == ServiceLifetime.Singleton);
        // Exactly ONE hosted-service registration, and it is a singleton FACTORY forward (never a
        // second type-based construction, which would run two triage pipelines double-escalating).
        var hosted = Assert.Single(services, d => d.ServiceType == typeof(IHostedService));
        Assert.Equal(ServiceLifetime.Singleton, hosted.Lifetime);
        Assert.NotNull(hosted.ImplementationFactory);
        Assert.Null(hosted.ImplementationType);

        // The node types ride the same install: the static-node provider registered by the
        // builder must now carry both definitions.
        var provider = services
            .Where(d => d.ServiceType == typeof(IStaticNodeProvider))
            .Select(d => d.ImplementationInstance)
            .OfType<IStaticNodeProvider>()
            .Single();
        var nodeTypes = provider.GetStaticNodes().Select(n => n.Id).ToList();
        Assert.Contains(NotificationRuleNodeType.NodeType, nodeTypes);
        Assert.Contains(NotificationChannelNodeType.NodeType, nodeTypes);
    }
}
