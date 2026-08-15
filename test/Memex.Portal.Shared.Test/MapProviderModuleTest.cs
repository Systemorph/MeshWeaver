using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the map-provider module lane: each provider pack (OpenStreetMap, Apple MapKit) carries a
/// <see cref="MeshNodeProviderAttribute"/> whose hub configurations register its renderer for the
/// provider-neutral <c>MapControl</c>, so a deployment swaps providers purely by which DLL its
/// <c>Modules:Assemblies</c> lists.
/// </summary>
public class MapProviderModuleTest
{
    public static IEnumerable<object[]> ProviderAssemblies() =>
    [
        [typeof(MeshWeaver.Blazor.OpenStreetMap.OpenStreetMapViewPackModuleAttribute).Assembly],
        [typeof(MeshWeaver.Blazor.AppleMaps.AppleMapsViewPackModuleAttribute).Assembly],
    ];

    [Theory]
    [MemberData(nameof(ProviderAssemblies))]
    public void EveryProviderPack_CarriesAModuleAttribute_WithHubRegistrations(Assembly pack)
    {
        var attributes = pack.GetCustomAttributes<MeshNodeProviderAttribute>().ToList();
        Assert.NotEmpty(attributes);
        Assert.Contains(attributes, a => a.HubConfigurations.Any());
    }

    [Fact]
    public void AppleModule_BindsItsTokenOptions_ThroughTheInstallFold()
    {
        var serviceConfigs = new List<Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());

        builder.InstallAssemblies(
            typeof(MeshWeaver.Blazor.AppleMaps.AppleMapsViewPackModuleAttribute).Assembly.Location);

        var services = serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        Assert.Contains(services, d =>
            d.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<MeshWeaver.Blazor.AppleMaps.AppleMapsConfiguration>));
    }
}
