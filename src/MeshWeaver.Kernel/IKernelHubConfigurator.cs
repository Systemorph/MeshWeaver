using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Kernel;

/// <summary>
/// Abstracts the kernel hub configuration (handlers, services, etc.)
/// so KernelNodeType in Graph can reference it without depending on Kernel.Hub.
/// Implemented by KernelContainer in MeshWeaver.Kernel.Hub.
/// Registered via KernelNodeType.AddKernel().
/// </summary>
public interface IKernelHubConfigurator
{
    MessageHubConfiguration Configure(MessageHubConfiguration config);

    /// <summary>
    /// Configures a lightweight kernel sub-hub (no mesh types, no routing, no persistence).
    /// Used for hosted kernel hubs created directly by Blazor views.
    /// </summary>
    MessageHubConfiguration ConfigureSubHub(MessageHubConfiguration config) => Configure(config);
}

public static class KernelHubConfigurationExtensions
{
    /// <summary>
    /// Adds kernel hub handlers and services to the configuration.
    /// Resolves IKernelHubConfigurator from DI (registered by AddKernel()).
    /// </summary>
    public static MessageHubConfiguration AddKernelHandlers(this MessageHubConfiguration config)
    {
        var configurator = config.ParentHub?.ServiceProvider.GetService<IKernelHubConfigurator>();
        return configurator?.Configure(config) ?? config;
    }

    /// <summary>
    /// Adds lightweight kernel sub-hub handlers (no mesh types, no routing).
    /// Used for hosted hubs created directly by Blazor views.
    /// </summary>
    public static MessageHubConfiguration AddKernelSubHubHandlers(this MessageHubConfiguration config)
    {
        var configurator = config.ParentHub?.ServiceProvider.GetService<IKernelHubConfigurator>();
        return configurator?.ConfigureSubHub(config) ?? config;
    }
}
