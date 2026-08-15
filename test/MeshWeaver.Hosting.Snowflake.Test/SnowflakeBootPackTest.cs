using System.Linq;
using System.Reflection;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Snowflake.Test;

/// <summary>
/// Pins the boot-pack contract: the Snowflake assembly carries a
/// <see cref="MeshNodeProviderAttribute"/> whose global service configurations register the
/// keyed storage factory — so a portal with NO compiled reference resolves
/// <c>Graph:Storage:Type = Snowflake</c> purely from <c>Modules:Assemblies</c>.
/// </summary>
public class SnowflakeBootPackTest
{
    [Fact]
    public void Assembly_CarriesTheBootAttribute_RegisteringTheKeyedFactory()
    {
        var attributes = typeof(SnowflakeStorageAdapterFactory).Assembly
            .GetCustomAttributes<MeshNodeProviderAttribute>()
            .ToList();
        Assert.NotEmpty(attributes);

        var services = new ServiceCollection();
        foreach (var configure in attributes
                     .SelectMany(a => a.Nodes)
                     .SelectMany(n => n.GlobalServiceConfigurations))
            configure(services);

        Assert.Contains(services, d =>
            d.ServiceType == typeof(IStorageAdapterFactory)
            && Equals(d.ServiceKey, SnowflakeStorageAdapterFactory.StorageType)
            && d.KeyedImplementationType == typeof(SnowflakeStorageAdapterFactory));
    }
}
