using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the view-pack module lane: every optional GUI pack the portal ships carries a
/// <see cref="MeshNodeProviderAttribute"/> whose hub configurations register its views, so
/// listing the DLL under <c>Modules:Assemblies</c> is the complete activation — the portal has
/// no compiled registration call any more (the flags it replaced are gone with it).
/// </summary>
public class ViewPackModuleTest
{
    public static IEnumerable<object[]> ViewPackAssemblies() =>
    [
        [typeof(MeshWeaver.Blazor.Radzen.RadzenViewPackModuleAttribute).Assembly],
        [typeof(MeshWeaver.Blazor.Analysis.AnalysisViewPackModuleAttribute).Assembly],
        [typeof(MeshWeaver.Blazor.GoogleMaps.GoogleMapsViewPackModuleAttribute).Assembly],
    ];

    [Theory]
    [MemberData(nameof(ViewPackAssemblies))]
    public void EveryViewPack_CarriesAModuleAttribute_WithHubRegistrations(Assembly pack)
    {
        var attributes = pack.GetCustomAttributes<MeshNodeProviderAttribute>().ToList();
        Assert.NotEmpty(attributes);
        Assert.Contains(attributes, a => a.HubConfigurations.Any());
    }

    [Fact]
    public void InstallAssemblies_OverAllThreePacks_FoldsEveryRegistration()
    {
        var serviceConfigs = new List<System.Func<IServiceCollection, IServiceCollection>>();
        var builder = new MeshBuilder(configure => serviceConfigs.Add(configure), AddressExtensions.CreateMeshAddress());

        builder.InstallAssemblies(ViewPackAssemblies()
            .Select(row => ((Assembly)row[0]).Location)
            .ToArray());

        var services = serviceConfigs.Aggregate(
            (IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        // The Radzen DI twin arrived through the module lane — the same fold the portal's
        // Modules:Assemblies boot uses (Assembly.LoadFrom + attribute discovery included).
        Assert.Contains(services, d => d.ServiceType.Namespace?.StartsWith("Radzen") == true);
        // And the GoogleMaps options binding landed.
        Assert.Contains(services, d =>
            d.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<MeshWeaver.Maps.GoogleMapsConfiguration>));
    }

    [Fact]
    public void RadzenModule_RegistersItsDiTwin()
    {
        var services = typeof(MeshWeaver.Blazor.Radzen.RadzenViewPackModuleAttribute).Assembly
            .GetCustomAttributes<MeshNodeProviderAttribute>()
            .SelectMany(a => a.Nodes)
            .SelectMany(n => n.GlobalServiceConfigurations)
            .Aggregate((IServiceCollection)new ServiceCollection(), (collection, configure) => configure(collection));

        // AddRadzenServices registers at least one Radzen-namespaced service; the exact set is
        // Radzen's own concern — the pin here is that the DI twin rides the module.
        Assert.Contains(services, d => d.ServiceType.Namespace?.StartsWith("Radzen") == true);
    }
}
