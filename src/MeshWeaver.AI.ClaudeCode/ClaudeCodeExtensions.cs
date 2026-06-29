using System.Collections.Immutable;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MeshWeaver.AI.ClaudeCode;

/// <summary>
/// Extension methods for adding Claude Code (Claude Agent SDK) services.
/// </summary>
public static class ClaudeCodeExtensions
{
    /// <summary>
    /// Claude Code is a <b>harness</b>, not a model provider — so this no longer
    /// registers a language-model catalog source. The harness surfaces as a
    /// <c>Harness</c> catalog node (see <see cref="HarnessNodeType"/>) and is wired
    /// via <see cref="AddClaudeCode(IServiceCollection, Action{ClaudeCodeConfiguration})"/>.
    /// Retained as a no-op so existing builder chains keep compiling.
    /// </summary>
    public static TBuilder AddClaudeCode<TBuilder>(this TBuilder builder)
        where TBuilder : MeshBuilder
        => builder;

    /// <summary>
    /// Registers Claude Code as a <b>harness</b> (NOT a model provider): the
    /// <see cref="ClaudeCodeHarness"/> runs the <c>claude</c> CLI directly via the
    /// Claude Agent SDK. <see cref="HarnessNodeType.AddHarnessType"/> projects it into
    /// a catalog node; <see cref="ThreadExecution"/> dispatches the round to it.
    /// Requires Claude Code CLI >= 2.0.0 (npm install -g @anthropic-ai/claude-code).
    /// </summary>
    public static IServiceCollection AddClaudeCode(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarness, ClaudeCodeHarness>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessRuntimeInfo, ClaudeCodeRuntimeProbe>());
        return services;
    }

    /// <summary>
    /// Registers the Claude Code harness with a configuration action that binds
    /// <see cref="ClaudeCodeConfiguration"/>. See <see cref="AddClaudeCode(IServiceCollection)"/>.
    /// </summary>
    public static IServiceCollection AddClaudeCode(
        this IServiceCollection services,
        Action<ClaudeCodeConfiguration> configure)
    {
        services.Configure(configure);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarness, ClaudeCodeHarness>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHarnessRuntimeInfo, ClaudeCodeRuntimeProbe>());
        return services;
    }
}
