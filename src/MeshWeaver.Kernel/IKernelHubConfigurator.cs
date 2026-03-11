using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Kernel;

/// <summary>
/// Abstracts the kernel hub configuration (handlers, services, etc.)
/// so KernelNodeType in Graph can reference it without depending on Kernel.Hub.
/// Implemented by KernelContainer in MeshWeaver.Kernel.Hub.
/// Registered via AddKernel().
/// </summary>
public interface IKernelHubConfigurator
{
    MessageHubConfiguration Configure(MessageHubConfiguration config);
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
}
