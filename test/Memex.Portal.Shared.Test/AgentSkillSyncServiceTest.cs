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
/// Unit tests for the agent→skill sync core (<see cref="AgentSkillSyncService.Project"/> +
/// <see cref="AgentSkillSyncService.Reconcile"/>) — the pure projection/reconcile logic, no mesh.
/// </summary>
public class AgentSkillSyncServiceTest
{
    private static readonly JsonSerializerOptions Json = new();

    [Fact]
    public void Project_WritesConversationalAgents_SkipsUtilityAndInstructionless()
    {
        var nodes = new[]
        {
            Agent("Assistant", "The default assistant", "You are the assistant."),
            Agent("ThreadNamer", "names threads", "Name the thread."),  // utility generator → skipped
            Agent("NoInstr", "no instructions", null),                  // no instructions → skipped
            Agent("Coder", "Writes code", "You are a coder."),
        };

        var desired = AgentSkillSyncService.Project(nodes, Json);

        Assert.Equal(new[] { "assistant", "coder" }, desired.Keys.OrderBy(k => k).ToArray());
        Assert.Contains("name: assistant", desired["assistant"]);
        Assert.Contains("description: The default assistant", desired["assistant"]);
        Assert.Contains("You are the assistant.", desired["assistant"]);
    }

    [Fact]
    public void Reconcile_Writes_Updates_AndPrunesStale_WithoutTouchingForeignFolders()
    {
        var dir = Path.Combine(Path.GetTempPath(), "skillsync-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = ImmutableDictionary<string, string>.Empty
                .Add("assistant", "---\nname: assistant\ndescription: a\n---\n\nbody1")
                .Add("coder", "---\nname: coder\ndescription: c\n---\n\nbody2");
            AgentSkillSyncService.Reconcile(dir, first, default);

            Assert.True(File.Exists(Path.Combine(dir, "assistant", "SKILL.md")));
            Assert.Contains("body2", File.ReadAllText(Path.Combine(dir, "coder", "SKILL.md")));

            // Second reconcile drops "coder" → its folder is pruned; "assistant" remains.
            var second = ImmutableDictionary<string, string>.Empty
                .Add("assistant", "---\nname: assistant\ndescription: a\n---\n\nbody1");

            // A foreign folder WITHOUT a SKILL.md must never be deleted by the prune.
            var foreign = Path.Combine(dir, "not-a-skill");
            Directory.CreateDirectory(foreign);

            AgentSkillSyncService.Reconcile(dir, second, default);

            Assert.True(Directory.Exists(Path.Combine(dir, "assistant")));
            Assert.False(Directory.Exists(Path.Combine(dir, "coder")));   // stale skill folder pruned
            Assert.True(Directory.Exists(foreign));                       // not a skill folder → kept
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
            Content = new AgentConfiguration { Id = id, Description = description, Instructions = instructions }
        };
}
