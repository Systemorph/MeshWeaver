#pragma warning disable CS1591

using System.Linq;
using MeshWeaver.AI;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// The built-in Agent/Skill content now lives in the repo section <c>content/ai</c> (edited in the
/// mesh, synced back to the repo) but is STILL embedded — via <c>LinkBase="Data"</c> — as the offline
/// fallback the providers read when the on-disk section can't be found. If the embedding regressed
/// (wrong resource names / the section not embedded), the fallback would silently yield no agents or
/// skills. This pins that the fallback resources ship under the exact names the providers read.
/// </summary>
public class AiContentSectionTest
{
    [Fact]
    public void EmbeddedFallback_ShipsAllAgentAndSkillResources_UnderTheDataNames()
    {
        var names = typeof(BuiltInAgentProvider).Assembly.GetManifestResourceNames();

        var agents = names
            .Where(n => n.StartsWith("MeshWeaver.AI.Data.Agent.", System.StringComparison.Ordinal)
                        && n.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase))
            .ToList();
        var skills = names
            .Where(n => n.StartsWith("MeshWeaver.AI.Data.Skill.", System.StringComparison.Ordinal)
                        && n.EndsWith(".md", System.StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(agents.Count >= 13, $"embedded agent fallback resources: {agents.Count}");
        Assert.True(skills.Count >= 15, $"embedded skill fallback resources: {skills.Count}");
        // Spot-check the exact names the providers' fallback path reads.
        Assert.Contains("MeshWeaver.AI.Data.Agent.Assistant.md", names);
        Assert.Contains("MeshWeaver.AI.Data.Skill.agent.md", names);
    }
}
