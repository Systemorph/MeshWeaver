using System.Linq;
using MeshWeaver.Mesh;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The ratchet behind the recurring "Skill partition was never imported" bug: every built-in AI
/// <see cref="IStaticRepoSource"/> defined in <c>MeshWeaver.AI</c> MUST be in the
/// <see cref="AiContentSources.AddBuiltInAiContentSources"/> bundle. Adding a new AI content type
/// (a fifth partition) without bundling it fails this test — so it can never again be silently left
/// un-imported by an incomplete per-partition list.
/// </summary>
public class AiContentSourcesTest
{
    [Fact]
    public void AddBuiltInAiContentSources_bundles_every_AI_static_repo_source()
    {
        // Reflect over EVERY concrete IStaticRepoSource in the MeshWeaver.AI assembly.
        var allAiSources = typeof(AiContentSources).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true }
                        && typeof(IStaticRepoSource).IsAssignableFrom(t))
            .ToHashSet();
        allAiSources.Should().NotBeEmpty("MeshWeaver.AI defines built-in static-repo content sources");

        var registered = new ServiceCollection()
            .AddBuiltInAiContentSources()
            .Where(sd => sd.ServiceType == typeof(IStaticRepoSource))
            .Select(sd => sd.ImplementationType!)
            .ToHashSet();

        var unbundled = allAiSources.Except(registered).OrderBy(t => t.Name).ToList();
        unbundled.Should().BeEmpty(
            "AddBuiltInAiContentSources must register EVERY IStaticRepoSource in MeshWeaver.AI — " +
            "bundling them is what stops a new AI partition (Skill, …) from being silently un-imported");
    }

    [Fact]
    public void ContentPartitions_cover_the_four_AI_content_partitions()
    {
        AiContentSources.ContentPartitions.OrderBy(p => p, System.StringComparer.Ordinal)
            .Should().Equal("Agent", "Harness", "Provider", "Skill");
    }
}
