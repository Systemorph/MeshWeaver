using System.ComponentModel;
using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI.Plugins;

/// <summary>
/// Creates the <c>load_skill</c> tool — reads a <c>nodeType:Skill</c> node by path and returns its
/// instructions (the SKILL.md body), so the agent can INJECT a skill's how-to on demand. Skills are
/// found with <c>search nodeType:Skill</c>; <c>load_skill</c> injects the one that fits the task. Reads
/// the authoritative single-node stream (never the eventually-consistent query index).
/// </summary>
public static class SkillTool
{
    /// <summary>Creates the <c>load_skill</c> AITool bound to the given hub + chat (for sub-thread launch).</summary>
    public static AITool Create(IMessageHub hub, IAgentChat chat)
    {
        Task<string> LoadSkill(
            [Description("The full mesh path of the nodeType:Skill node to load (e.g. a path returned by `search nodeType:Skill`).")]
            string skillPath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(skillPath))
                return Task.FromResult("Provide the skill's path — find skills with `search nodeType:Skill`.");

            // Authoritative single-node read (GetMeshNodeStream, not QueryAsync — the query index lags).
            //
            // 🚨 The three terminals go through ToolTask.Bridge. Before #1956 this was a bare
            // TaskCompletionSource with a 2-arg Subscribe, so the EMPTY-completion terminal was
            // missing: a .Where(...)-filtered stream that completes without ever passing the filter
            // (MeshNodeStreamCache completes its per-path subjects on eviction and on dispose)
            // reached neither arm, and the task stayed pending forever. Timeout does not cover that
            // — it passes an empty OnCompleted straight through. Nor was the token observed, so
            // Stop could not end a load_skill parked on a hub that never activates.
            return ToolTask.Bridge(
                hub.GetMeshNodeStream(skillPath.Trim())
                    .Where(n => n is not null)
                    .Take(1)
                    .Timeout(TimeSpan.FromSeconds(10)),
                cancellationToken,
                node => Answer(node!),
                ex => $"Could not load skill '{skillPath}': {ex.Message}",
                // An absent skill node is a CORRECT outcome, and the honest answer is that it is
                // absent — the agent can then search for the right path instead of waiting.
                () => $"Skill '{skillPath}' was not found — find skills with `search nodeType:Skill`.");

            string Answer(MeshNode node)
            {
                var def = DefinitionOf(node, hub.JsonSerializerOptions);
                var instructions = def?.Instructions;
                if (string.IsNullOrWhiteSpace(instructions))
                    return $"Skill '{skillPath}' has no instructions to load.";

                // LaunchesSubThread: run the skill in its OWN sub-thread to keep the work out of
                // the main context, via the generic StartThread launcher (mainNode = this thread).
                var execCtx = chat.ExecutionContext;
                if (def!.LaunchesSubThread && execCtx is not null)
                {
                    hub.StartThread(
                        execCtx.ContextPath ?? execCtx.ThreadPath,
                        instructions!,
                        contextPath: execCtx.ContextPath,
                        createdBy: execCtx.UserAccessContext?.ObjectId,
                        mainNode: execCtx.ThreadPath);
                    return $"Launched skill '{node.Name ?? skillPath}' in a sub-thread to run in isolation. " +
                           "Its result appears inline when it completes — continue with other work.";
                }

                return instructions!;
            }
        }

        return AIFunctionFactory.Create(
            LoadSkill,
            name: "load_skill",
            description: "Loads a skill (a nodeType:Skill node) by path and returns its instructions — the " +
                         "how-to for a specific operation. Find skills with `search nodeType:Skill`, then load the " +
                         "one that fits the task. Load a skill only when a request matches it, and read each skill " +
                         "only once — if you have already loaded it this conversation, do not re-load it.");
    }

    private static SkillDefinition? DefinitionOf(MeshNode node, JsonSerializerOptions json) => node.Content switch
    {
        SkillDefinition s => s,
        JsonElement je => TryDeserialize(je, json),
        _ => null
    };

    private static SkillDefinition? TryDeserialize(JsonElement je, JsonSerializerOptions json)
    {
        try { return JsonSerializer.Deserialize<SkillDefinition>(je.GetRawText(), json); }
        catch { return null; }
    }
}
