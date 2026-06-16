using System.ComponentModel.DataAnnotations;
using MeshWeaver.Messaging;
using Microsoft.Extensions.AI;

namespace MeshWeaver.AI;

/// <summary>
/// A chat execution <b>harness</b> — the top-level choice of HOW a round runs. A
/// harness is <i>not</i> a model provider: it decides which execution library drives
/// the round. Three for now:
/// <list type="bullet">
///   <item><b>MeshWeaver</b> — the native agent + model system (provider factories).
///     Lives in <c>MeshWeaver.AI</c>. Surfaces agent + model selection.</item>
///   <item><b>Claude Code</b> — the <c>claude</c> CLI via the Claude Agent SDK. Lives
///     in <c>MeshWeaver.AI.ClaudeCode</c>.</item>
///   <item><b>GitHub Copilot</b> — the Copilot CLI. Lives in <c>MeshWeaver.AI.Copilot</c>.</item>
/// </list>
/// Stored as a MeshNode with <c>nodeType="Harness"</c> and <c>Content=Harness</c>.
/// </summary>
public record Harness
{
    /// <summary>Stable id — matches <see cref="ThreadComposer.Harness"/> and the <see cref="Harnesses"/> constants.</summary>
    [Key]
    public required string Id { get; init; }

    /// <summary>Friendly name for the picker (defaults to <see cref="Id"/>).</summary>
    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    public string? Icon { get; init; }

    /// <summary>Display order in the harness picker (lower first).</summary>
    public int Order { get; init; }

    /// <summary>Whether this harness is the default selection on a new thread.</summary>
    public bool IsDefault { get; init; }

    /// <summary>
    /// True when this harness surfaces agent + model selection (MeshWeaver). The CLI
    /// harnesses run their own agent loop, so they hide the agent/model pickers.
    /// </summary>
    public bool SupportsAgentSelection { get; init; }
}

/// <summary>
/// Runtime contract for a harness. Each harness lives in its own assembly and uses
/// its own library to run a round. Registered in DI (one per assembly);
/// <see cref="BuiltInHarnessProvider"/> projects each into a catalog
/// <see cref="Harness"/> node so the picker and routing share one source of truth.
/// </summary>
public interface IHarness
{
    /// <summary>Stable id, matches <see cref="Harness.Id"/> and <see cref="ThreadComposer.Harness"/>.</summary>
    string Id { get; }

    /// <summary>The catalog definition surfaced as a node and in the picker.</summary>
    Harness Definition { get; }

    /// <summary>
    /// Creates the <see cref="IChatClient"/> that runs a round under this harness, or
    /// <c>null</c> to fall through to the default MeshWeaver agent/model path. The CLI
    /// harnesses return their own library's client (e.g. the <c>claude</c> CLI) — so
    /// they never touch the model-provider factory chain.
    /// </summary>
    IChatClient? CreateChatClient(HarnessExecutionContext context);
}

/// <summary>Inputs a harness needs to build its chat client for one round.</summary>
public sealed record HarnessExecutionContext(
    IMessageHub Hub,
    AgentConfiguration? Agent,
    string? ModelName);
