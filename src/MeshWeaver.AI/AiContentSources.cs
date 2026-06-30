using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.AI;

/// <summary>
/// The built-in AI content partitions and their static-repo import sources, treated as ONE bundle.
/// AI content — Agents, model Providers, Harnesses, Skills — is seeded from these sources on boot.
/// Bundling them is the cure for the recurring "Skill partition was never imported" bug: there is no
/// per-partition allow-list to hand-maintain, so a new built-in AI content type can't be silently
/// left un-imported by forgetting to name its partition. This is the single source of truth shared by
/// <c>AddAI</c>'s serve-from-DB gating and the portal's static-repo import wiring.
///
/// <para>Pinned by <c>AiContentSourcesTest</c>: every <see cref="IStaticRepoSource"/> defined in
/// <c>MeshWeaver.AI</c> MUST be in <see cref="AddBuiltInAiContentSources"/> — adding a fifth source
/// without bundling it fails the test.</para>
/// </summary>
public static class AiContentSources
{
    /// <summary>
    /// The partitions whose content is the built-in AI catalog: <c>Agent</c>, <c>Provider</c>,
    /// <c>Harness</c>, <c>Skill</c>. Served from the DB (and imported) as a UNIT — never partially.
    /// </summary>
    public static readonly IReadOnlySet<string> ContentPartitions =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Agent",                             // AgentStaticRepoSource.Partition
            ModelProviderNodeType.RootNamespace, // "Provider"
            HarnessNodeType.RootNamespace,       // "Harness"
            SkillNodeType.RootNamespace,         // "Skill"
        };

    /// <summary>
    /// Registers EVERY built-in AI <see cref="IStaticRepoSource"/> (Agent, Model/Provider, Harness,
    /// Skill) as one bundle. The portal calls this whenever AI content is served from the DB, so the
    /// import set can never drift from <see cref="ContentPartitions"/> or silently drop a partition.
    /// </summary>
    public static IServiceCollection AddBuiltInAiContentSources(this IServiceCollection services)
    {
        services.AddSingleton<IStaticRepoSource, AgentStaticRepoSource>();
        services.AddSingleton<IStaticRepoSource, ModelStaticRepoSource>();
        services.AddSingleton<IStaticRepoSource, HarnessStaticRepoSource>();
        services.AddSingleton<IStaticRepoSource, SkillStaticRepoSource>();
        return services;
    }
}
