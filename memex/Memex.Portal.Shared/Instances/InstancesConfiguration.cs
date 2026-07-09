using MeshWeaver.Mesh;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Memex.Portal.Shared.Instances;

/// <summary>
/// Wires the platform-admin Instances feature: binds <see cref="InstancesOptions"/> from the
/// <c>Instances:*</c> configuration section and registers the live cluster-query service. The
/// admin-gated GUI itself is added as a Settings tab via
/// <see cref="InstancesAdminLayoutArea.AddInstancesAdminSettingsTab"/>.
/// </summary>
public static class InstancesConfiguration
{
    /// <summary>Registers <see cref="InstancesOptions"/> (from config) and
    /// <see cref="IClusterInstanceService"/>. The k8s query service uses X.509/TLS APIs that are
    /// <c>[UnsupportedOSPlatform("browser")]</c>, so it is skipped on a Blazor WASM host (matching
    /// <c>AddSelfUpdate</c>); off-cluster it simply reports it cannot query.</summary>
    public static TBuilder AddInstancesAdmin<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
    {
        builder.ConfigureServices(services =>
        {
            services.AddSingleton(sp =>
                sp.GetService<IConfiguration>()?.GetSection("Instances").Get<InstancesOptions>()
                ?? new InstancesOptions());
            if (!OperatingSystem.IsBrowser())
                services.AddSingleton<IClusterInstanceService, KubernetesInstanceService>();
            return services;
        });
        return builder;
    }
}
