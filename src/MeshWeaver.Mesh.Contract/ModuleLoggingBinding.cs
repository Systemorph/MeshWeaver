#nullable enable
using MeshWeaver.ServiceProvider;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Mesh;

/// <summary>
/// Binds the module diagnostics to the root's logging when the generic host starts. A root built by
/// <see cref="ServiceProviderExtensions.SetupModules"/> has already done this; a host on plain Microsoft DI never
/// runs that path, and without this binding its module diagnostics would be held and never written
/// (MeshWeaver.Plugins#4964). The host starts services once the container is built, so the logger factory is not
/// under construction here, and a second binding changes nothing.
/// </summary>
/// <param name="services">The root container.</param>
internal sealed class ModuleLoggingBinding(IServiceProvider services) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        services.GetService<ModuleInstanceOwners>()?.UseLoggerFactory(services.GetService<ILoggerFactory>());
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
