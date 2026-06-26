using System.ComponentModel.DataAnnotations;
using MeshWeaver.AI.Connect;
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

    /// <summary>Short description shown in the harness picker; <c>null</c> when none.</summary>
    public string? Description { get; init; }

    /// <summary>Icon for the picker (URL or inline SVG); <c>null</c> when none.</summary>
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

    /// <summary>
    /// The slash-commands this harness OWNS. When this harness is the active one in the chat, these
    /// drive BOTH the slash-command autocomplete (the harness is the authority for its command list)
    /// AND dispatch — a non-MeshWeaver harness routes its own commands (e.g. <c>/login</c>,
    /// <c>/logout</c>) to itself instead of MeshWeaver's <c>/agent</c>/<c>/model</c> node-pickers.
    /// Empty for the MeshWeaver harness (it keeps the node-pick commands). Default: none.
    /// </summary>
    IReadOnlyList<HarnessCommand> Commands => [];

    /// <summary>
    /// The Connect provider this harness authenticates per-user (Claude Code / GitHub Copilot), or
    /// <c>null</c> for MeshWeaver (no per-user CLI login). The chat drives <see cref="HarnessCommandKind.Connect"/>
    /// / <see cref="HarnessCommandKind.Disconnect"/> commands against this provider.
    /// </summary>
    ConnectProvider? AuthProvider => null;
}

/// <summary>
/// A slash-command a <see cref="IHarness"/> owns. Pure data: the harness DECLARES its commands (so
/// the chat autocomplete lists them) and the chat view EXECUTES the <see cref="Kind"/> (Connect /
/// Disconnect) against the harness's <see cref="IHarness.AuthProvider"/>. Extensible by adding kinds.
/// </summary>
public sealed record HarnessCommand(string Name, string Description, HarnessCommandKind Kind)
{
    /// <summary>Usage syntax for help / the autocomplete detail (e.g. <c>/login</c>).</summary>
    public string Usage => $"/{Name}";
}

/// <summary>What a <see cref="HarnessCommand"/> does when run in the chat.</summary>
public enum HarnessCommandKind
{
    /// <summary>Log in / (re)authenticate this harness's subscription — drives the Connect flow inline.</summary>
    Connect,

    /// <summary>Log out — forget this harness's stored per-user subscription token.</summary>
    Disconnect,
}

/// <summary>Inputs a harness needs to build its chat client for one round.</summary>
public sealed record HarnessExecutionContext(
    IMessageHub Hub,
    AgentConfiguration? Agent,
    string? ModelName);
