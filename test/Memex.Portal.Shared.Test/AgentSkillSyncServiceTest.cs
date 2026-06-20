using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using Memex.Portal.Shared.Skills;
using MeshWeaver.AI;
using MeshWeaver.Mesh;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Unit tests for the agent/skill→file sync core (<see cref="AgentSkillSyncService.Project"/> +
/// <see cref="AgentSkillSyncService.Reconcile"/>) — the pure projection/reconcile logic, no mesh.
/// </summary>
public class AgentSkillSyncServiceTest
{
    private static readonly JsonSerializerOptions Json = new();

    [Fact]
    public void Project_HandlesAgentsAndSkills_SkipsUtilityAndInstructionless()
    {
        var nodes = new[]
        {
            Agent("Assistant", "The default assistant", "You are the assistant."),
            Agent("ThreadNamer", "names threads", "Name the thread."),  // utility generator → skipped
            Agent("NoInstr", "no instructions", null),                  // no instructions → skipped
            Skill("Translate", "Translate text", "Translate the input."), // first-class Skill node
        };

        var desired = AgentSkillSyncService.Project(nodes, Json);

        Assert.Equal(new[] { "assistant", "translate" }, desired.Keys.OrderBy(k => k).ToArray());
        Assert.Contains("name: translate", desired["translate"]);
        Assert.Contains("description: Translate text", desired["translate"]);
        Assert.Contains("Translate the input.", desired["translate"]);
    }

    [Fact]
    public void Reconcile_WritesBaseInstructions_SkillsLayout_AndPrunesStale()
    {
        var dir = Path.Combine(Path.GetTempPath(), "skillsync-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = ImmutableDictionary<string, string>.Empty
                .Add("assistant", "---\nname: assistant\ndescription: a\n---\n\nbody1")
                .Add("coder", "---\nname: coder\ndescription: c\n---\n\nbody2");
            AgentSkillSyncService.Reconcile(dir, first, default);

            var skillsRoot = Path.Combine(dir, ".claude", "skills");
            Assert.True(File.Exists(Path.Combine(skillsRoot, "assistant", "SKILL.md")));
            Assert.Contains("body2", File.ReadAllText(Path.Combine(skillsRoot, "coder", "SKILL.md")));

            // Base instructions written for both CLIs, referencing the meshweaver MCP server.
            Assert.True(File.Exists(Path.Combine(dir, "AGENTS.md")));
            Assert.True(File.Exists(Path.Combine(dir, "CLAUDE.md")));
            Assert.Contains("meshweaver", File.ReadAllText(Path.Combine(dir, "AGENTS.md")));

            // Second reconcile drops "coder" → its skill folder is pruned; "assistant" remains.
            var second = ImmutableDictionary<string, string>.Empty
                .Add("assistant", "---\nname: assistant\ndescription: a\n---\n\nbody1");

            // A foreign folder WITHOUT a SKILL.md must never be deleted by the prune.
            var foreign = Path.Combine(skillsRoot, "not-a-skill");
            Directory.CreateDirectory(foreign);

            AgentSkillSyncService.Reconcile(dir, second, default);

            Assert.True(Directory.Exists(Path.Combine(skillsRoot, "assistant")));
            Assert.False(Directory.Exists(Path.Combine(skillsRoot, "coder")));   // stale skill pruned
            Assert.True(Directory.Exists(foreign));                              // not a skill folder → kept
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static MeshNode Agent(string id, string description, string? instructions) =>
        new(id, "Agent")
        {
            NodeType = "Agent",
            Name = id,
            Description = description,
            Content = new AgentConfiguration { Id = id, Description = description, Instructions = instructions }
        };

    private static MeshNode Skill(string id, string description, string? instructions) =>
        new(id, "Skill")
        {
            NodeType = "Skill",
            Name = id,
            Description = description,
            Content = new SkillDefinition { Instructions = instructions }
        };
}
