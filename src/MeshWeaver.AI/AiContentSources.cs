using System.Collections.Immutable;
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
/// <c>MeshWeaver.AI</c> MUST be in <see cref="AddBuiltInAiContentSources"/> — adding a source
/// without bundling it fails the test.</para>
/// </summary>
public static class AiContentSources
{
    /// <summary>
    /// The partitions whose content is the built-in AI catalog: <c>Agent</c>, <c>Provider</c>,
    /// <c>Harness</c>, <c>Skill</c>. Served from the DB as a UNIT — never partially.
    ///
    /// <para>🚨 <c>Agent</c> is still listed even though nothing imports it any more, and that is
    /// LOAD-BEARING. Membership here decides who SERVES the partition: a listed partition is served
    /// by Postgres and its read-only in-memory <c>StaticNodePartitionStorageProvider</c> is not
    /// registered. Drop <c>Agent</c> and the in-memory provider comes back and SHADOWS the DB nodes
    /// the Agent plugin installs — the agents silently revert to whatever the binary embeds.
    /// "Served from the DB" and "imported by this binary" are now two different things.</para>
    /// </summary>
    public static readonly ImmutableHashSet<string> ContentPartitions =
        ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            "Agent",                             // served from the DB; filled by the Agent PLUGIN
            ModelProviderNodeType.RootNamespace, // "Provider"
            HarnessNodeType.RootNamespace,       // "Harness"
            SkillNodeType.RootNamespace);        // "Skill"

    /// <summary>
    /// Registers every built-in AI <see cref="IStaticRepoSource"/> that this binary still ships
    /// (Model/Provider, Harness, Skill) as one bundle. The portal calls this whenever AI content is
    /// served from the DB, so the import set can never silently drop a partition.
    ///
    /// <para>There is no Agent source: the built-in agents moved to the <c>Agent</c> plugin in
    /// <c>MeshWeaver.Plugins</c> (pre-installed, so every installation gets them). The framework
    /// keeps <see cref="BuiltInAgentProvider"/> only as the in-memory offline path for a mesh that
    /// does NOT serve Agent from the DB — monolith, tests, MAUI.</para>
    /// </summary>
    public static IServiceCollection AddBuiltInAiContentSources(this IServiceCollection services)
    {
        services.AddSingleton<IStaticRepoSource, ModelStaticRepoSource>();
        services.AddSingleton<IStaticRepoSource, HarnessStaticRepoSource>();
        services.AddSingleton<IStaticRepoSource, SkillStaticRepoSource>();
        return services;
    }
}
