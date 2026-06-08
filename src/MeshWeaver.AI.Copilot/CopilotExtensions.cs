using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI.Copilot;

/// <summary>
/// Extension methods for adding GitHub Copilot services.
/// </summary>
public static class CopilotExtensions
{
    /// <summary>
    /// GitHub Copilot is a <b>harness</b>, not a model provider — so this no longer
    /// registers a language-model catalog source. The harness surfaces as a
    /// <c>Harness</c> catalog node (see <see cref="HarnessNodeType"/>) and is wired
    /// via <see cref="AddCopilot(IServiceCollection, Action{CopilotConfiguration})"/>.
    /// Retained as a no-op so existing builder chains keep compiling.
    /// </summary>
    public static TBuilder AddCopilot<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
        => builder;

    /// <summary>
    /// Registers GitHub Copilot as a <b>harness</b>: the <see cref="CopilotHarness"/>
    /// runs the Copilot CLI directly. The live model catalog is still registered (the
    /// CLI reports its models). See <see cref="HarnessNodeType"/> + <see cref="ThreadExecution"/>.
    /// </summary>
    public static IServiceCollection AddCopilot(this IServiceCollection services)
    {
        services.TryAddSingleton<CopilotModelCatalog>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarness, CopilotHarness>());
        return services;
    }

    /// <summary>
    /// Registers the GitHub Copilot harness with a configuration action that binds
    /// <see cref="CopilotConfiguration"/>. See <see cref="AddCopilot(IServiceCollection)"/>.
    /// </summary>
    public static IServiceCollection AddCopilot(
        this IServiceCollection services,
        Action<CopilotConfiguration> configure)
    {
        services.Configure(configure);
        services.TryAddSingleton<CopilotModelCatalog>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarness, CopilotHarness>());
        return services;
    }
}
