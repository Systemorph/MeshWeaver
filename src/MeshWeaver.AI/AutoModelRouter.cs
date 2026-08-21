using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

/// <summary>
/// The Auto router's SELECTION step (#1951) — the pure half: builds the routing prompt and parses
/// the engine's answer. The orchestration (when to run, which engine serves the call, how a choice
/// takes effect) lives in <c>AgentChatClient.ApplyAutoRouterSelectionAsync</c>; everything HERE is
/// deterministic and unit-testable without a portal or a model.
///
/// <para><b>Two-stage Auto, by design.</b> The synchronous dispatch in
/// <c>AgentChatClient.ApplyStaleModelFallback</c> stays the FLOOR: it parks an Auto round on the
/// selected agent's declared tier (or the deployment default) with no model call, so a round can
/// always run — the original "a router nobody can predict is worse than a fixed default" stance.
/// This step then REFINES that floor with one cheap, bounded model call over the round's actual
/// content (maintainer direction, 2026-08-20: Auto "should trigger an agent to determine the most
/// appropriate model for the thread"). Predictability is preserved by construction: any failure —
/// timeout, unparseable answer, an id outside the candidate list, an engine with no bare chat
/// client — keeps the floor, and the choice is logged with the model's own stated reason.</para>
/// </summary>
public static class AutoModelRouter
{
    /// <summary>One candidate the router may pick: the model's wire id plus the display facts the
    /// prompt names it by.</summary>
    /// <param name="Id">The model's registered id — what the round runs on when picked.</param>
    /// <param name="Provider">The serving provider/factory label.</param>
    /// <param name="Label">Human label from the model node, when one exists.</param>
    public readonly record struct RouterCandidate(string Id, string Provider, string? Label);

    /// <summary>The router's answer wire shape — what the engine is instructed to reply.</summary>
    private sealed record RouterChoice(string? Model, string? Reason);

    /// <summary>Cap on the task text embedded in the prompt — routing needs the shape of the ask,
    /// never the whole document; an unbounded paste would make the routing call cost more than the
    /// round it routes.</summary>
    public const int MaxTaskChars = 2000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Builds the routing conversation: a system message defining the contract (pick ONE candidate
    /// id, answer strict JSON) and a user message carrying the candidates and the task.
    /// </summary>
    public static IReadOnlyList<ChatMessage> BuildMessages(
        IReadOnlyList<RouterCandidate> candidates, string taskText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Candidate models:");
        foreach (var c in candidates)
        {
            sb.Append("- ").Append(c.Id).Append(" (provider: ").Append(c.Provider);
            if (!string.IsNullOrWhiteSpace(c.Label) && !string.Equals(c.Label, c.Id, StringComparison.OrdinalIgnoreCase))
                sb.Append(", label: ").Append(c.Label);
            sb.AppendLine(")");
        }
        sb.AppendLine();
        sb.AppendLine("Task:");
        var task = taskText.Length <= MaxTaskChars ? taskText : taskText[..MaxTaskChars];
        sb.AppendLine(task);

        return
        [
            new ChatMessage(ChatRole.System,
                "You route a user task to the most appropriate language model. "
                + "Prefer a stronger model for complex reasoning, coding, or long multi-step work, "
                + "and a faster or cheaper model for short, simple, or conversational tasks. "
                + "Pick exactly one id from the candidate list. "
                + "Reply with ONLY a JSON object of the shape "
                + "{\"model\": \"<candidate id>\", \"reason\": \"<one short sentence>\"} — "
                + "no prose, no code fences."),
            new ChatMessage(ChatRole.User, sb.ToString()),
        ];
    }

    /// <summary>
    /// Parses the engine's answer into a validated choice. Tolerates code fences and prose around
    /// the JSON object (models add both, whatever the instructions say), but the picked id must
    /// match a candidate EXACTLY (case-insensitive) — an invented or partial id is a refusal, never
    /// a fuzzy match, because the caller falls back to a model that certainly works.
    /// </summary>
    public static bool TryParseChoice(
        string? responseText,
        IReadOnlyCollection<string> candidateIds,
        out string chosenId,
        out string reason)
    {
        chosenId = "";
        reason = "";
        if (string.IsNullOrWhiteSpace(responseText))
            return false;

        // The first balanced-looking JSON object in the text: from the first '{' to the last '}'.
        // Good enough for a reply that is "a JSON object, possibly fenced/prefixed" — anything more
        // broken than that reads as a refusal and keeps the floor.
        var start = responseText.IndexOf('{');
        var end = responseText.LastIndexOf('}');
        if (start < 0 || end <= start)
            return false;

        RouterChoice? choice;
        try
        {
            choice = JsonSerializer.Deserialize<RouterChoice>(
                responseText[start..(end + 1)], Json);
        }
        catch (JsonException)
        {
            return false;
        }

        if (choice?.Model is not { Length: > 0 } picked)
            return false;

        var match = candidateIds.FirstOrDefault(id =>
            string.Equals(id, picked.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match is null)
            return false;

        chosenId = match;
        reason = choice.Reason?.Trim() ?? "";
        return true;
    }

    /// <summary>
    /// The candidate set for one round: every loaded non-router model whose credentials actually
    /// resolve, de-duplicated by id, in catalog order. Pure — both inputs are snapshots.
    /// </summary>
    public static ImmutableList<RouterCandidate> UsableCandidates(
        IReadOnlyList<ModelInfo> loadedModels, Func<string, bool> hasUsableCredential)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = ImmutableList.CreateBuilder<RouterCandidate>();
        foreach (var model in loadedModels.OrderBy(m => m.Order))
        {
            if (model.IsRouter || string.IsNullOrWhiteSpace(model.Name))
                continue;
            if (!seen.Add(model.Name))
                continue;
            if (!hasUsableCredential(model.Name))
                continue;
            result.Add(new RouterCandidate(model.Name, model.Provider, model.Label));
        }
        return result.ToImmutable();
    }
}
