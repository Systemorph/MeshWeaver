using Memex.Portal.Shared.Skills;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Unit tests for the skill workspace base-instructions
/// (<see cref="AgentSkillSyncService.BaseInstructions"/>). Skills are NOT materialised to disk — they are
/// mesh nodes read on demand — so the only thing the sync writes is <c>AGENTS.md</c>, whose guidance this
/// pins: the <c>meshweaver</c> MCP server, vector search, and finding skills via <c>search nodeType:Skill</c>.
/// </summary>
public class AgentSkillSyncServiceTest
{
    [Fact]
    public void BaseInstructions_PointToMcp_VectorSearch_AndOnDemandSkillDiscovery()
    {
        var content = AgentSkillSyncService.BaseInstructions();

        Assert.Contains("meshweaver", content);             // the MCP server the CLIs use
        Assert.Contains("vector-indexed", content);         // everything is vector-indexed
        Assert.Contains("search nodeType:Skill", content);  // skills are found on demand
        // Skills live in the mesh, NOT materialised to a local .claude/skills tree.
        Assert.DoesNotContain(".claude/skills", content);
    }
}
