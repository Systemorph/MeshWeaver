using MeshWeaver.AI;
using Memex.Portal.Shared.Skills;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Unit tests for the skill workspace instructions
/// (<see cref="AgentSkillSyncService.BaseInstructions"/> + <see cref="AgentSkillSyncService.RenderSkillCatalog"/>).
/// Skill BODIES are NOT materialised to disk — they are mesh nodes read on demand — so what the sync
/// writes is <c>AGENTS.md</c>: the base "mesh is via the <c>meshweaver</c> MCP server / vector search /
/// find skills with <c>search nodeType:Skill</c>" guidance, PLUS a LISTING (name + description + load
/// path) of the up-front-advertised platform instruction skills.
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

    [Fact]
    public void RenderSkillCatalog_ListsAdvertisedInstructionSkills_WithDescriptionAndLoadPath()
    {
        var skills = new[]
        {
            // Advertised instruction skill — listed.
            Skill("storm", "/storm", "Diagnose and cure portal storms", "Skill/storm",
                new SkillDefinition { Instructions = "## Storm\n...", AutoMount = true }),
            // Behaviour-only skill (a Pick action, no body) — NOT a CLI how-to, excluded.
            Skill("agent", "/agent", "Switch the agent", "Skill/agent",
                new SkillDefinition { Action = new SkillAction { Kind = SkillActionKind.Pick }, AutoMount = true }),
            // Instruction skill but opted OUT of up-front advertising — excluded.
            Skill("secret", "/secret", "Loaded on demand only", "Skill/secret",
                new SkillDefinition { Instructions = "body", AutoMount = false }),
        };

        var catalog = AgentSkillSyncService.RenderSkillCatalog(skills);

        Assert.Contains("/storm", catalog);
        Assert.Contains("Diagnose and cure portal storms", catalog);
        Assert.Contains("get Skill/storm", catalog);     // load-on-demand path
        Assert.DoesNotContain("/agent", catalog);        // behaviour-only, not listed
        Assert.DoesNotContain("/secret", catalog);       // AutoMount=false, not listed
    }

    [Fact]
    public void RenderSkillCatalog_NoAdvertisedSkills_ReturnsEmpty()
    {
        var onlyBehaviour = new[]
        {
            Skill("model", "/model", "Switch the model", "Skill/model",
                new SkillDefinition { Action = new SkillAction { Kind = SkillActionKind.Pick }, AutoMount = true }),
        };

        Assert.Equal(string.Empty, AgentSkillSyncService.RenderSkillCatalog(onlyBehaviour));
    }

    private static SkillInfo Skill(string id, string name, string description, string path, SkillDefinition def)
        => new() { Id = id, Name = name, Description = description, Path = path, Definition = def };
}
