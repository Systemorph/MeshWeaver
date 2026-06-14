#nullable enable

using MeshWeaver.AI.Parsing;
using MeshWeaver.Messaging;

namespace MeshWeaver.AI.Commands;

/// <summary>
/// The execution context handed to a chat command's handler (<see cref="IChatCommand.Execute"/>).
/// A command is a HANDLER — like a button's click action — that runs <b>in the thread</b> when the
/// user executes it. The context gives the handler everything it needs to run its workflow WITHOUT
/// knowing anything about the GUI:
/// <list type="bullet">
///   <item><b>thread access</b> — <see cref="Hub"/> (resolve the workspace/services + write nodes),
///     <see cref="ThreadPath"/>, <see cref="ComposerPath"/> (the <c>ThreadComposer</c> the selections
///     persist on), and <see cref="ContextPath"/> (the navigation context);</item>
///   <item><b>GUI callbacks</b> — the handler TRIGGERS these to inject GUI (e.g. pop the node selector
///     via <see cref="ShowNodePicker"/>) without referencing any Blazor type. The host (the chat view)
///     wires the implementations; a command in any module composes them freely.</item>
/// </list>
///
/// <para>This is the "emancipate commands from the GUI" surface: a module ships an
/// <see cref="IChatCommand"/> (or, for the common pick case, a <see cref="MeshNodePickCommand"/> /
/// a <c>nodeType:Command</c> node) and it works in the chat with NO change to the chat view. The
/// callbacks are optional (null in a headless/test host), so handlers null-guard them and stay
/// unit-testable without a mesh or a GUI. See <c>Doc/AI/ChatCommands.md</c>.</para>
/// </summary>
public record CommandContext
{
    /// <summary>The parsed command (name + arguments) the user executed.</summary>
    public required ParsedCommand ParsedCommand { get; init; }

    /// <summary>
    /// The chat hub — the handler resolves the workspace / services from it and writes nodes
    /// (e.g. <c>Hub.GetMeshNodeStream(ComposerPath).Update(...)</c>). Optional so commands are
    /// unit-testable without a mesh.
    /// </summary>
    public IMessageHub? Hub { get; init; }

    /// <summary>The current navigation context path (used to scope node-pick queries). Optional.</summary>
    public string? ContextPath { get; init; }

    /// <summary>
    /// The thread this command runs in (null for the out-of-thread composer / new-thread case).
    /// </summary>
    public string? ThreadPath { get; init; }

    /// <summary>
    /// The <c>ThreadComposer</c> node path the selections (agent / model / harness / …) persist on.
    /// The standard place a command saves its picked value — the status row + the next submission
    /// read it back. <see cref="ShowNodePicker"/>'s default host writes the selected node PATH onto
    /// the <see cref="NodePickerRequest.ComposerField"/> of this node.
    /// </summary>
    public string? ComposerPath { get; init; }

    /// <summary>Registry of all commands (for the <c>/help</c> command).</summary>
    public ChatCommandRegistry? CommandRegistry { get; init; }

    /// <summary>
    /// GUI callback: pop the generic mesh-node selector for <see cref="NodePickerRequest.Query"/>;
    /// on selection the host writes the chosen node's PATH onto the composer field. A command TRIGGERS
    /// this to inject the picker GUI without referencing any Blazor type. Null in a headless host.
    /// </summary>
    public Action<NodePickerRequest>? ShowNodePicker { get; init; }

    /// <summary>
    /// GUI callback: surface a one-line status / error / help message under the chat input. Null in a
    /// headless host. The bool argument styles it as an error.
    /// </summary>
    public Action<string, bool>? ShowStatus { get; init; }
}

/// <summary>
/// A request from a command's handler to the host to render the generic node selector: list the mesh
/// nodes matching <see cref="Query"/> (ordering + eligibility are the query's concern — e.g.
/// <c>... sort:order</c>), and on selection write the chosen node's PATH onto the composer field
/// <see cref="ComposerField"/> (a camelCase <c>ThreadComposer</c> property — <c>harness</c>,
/// <c>agentName</c>, <c>modelName</c>). When <see cref="SearchTerm"/> is non-null the host pre-filters
/// to it and auto-selects an exact match (so <c>/model gpt-4o</c> switches without a click).
/// </summary>
public record NodePickerRequest(string Query, string ComposerField, string Title, string? SearchTerm = null);
