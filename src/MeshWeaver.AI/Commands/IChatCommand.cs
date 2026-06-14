#nullable enable

namespace MeshWeaver.AI.Commands;

/// <summary>
/// A chat slash-command. A command is a HANDLER — like a button's click action — invoked via
/// <see cref="Execute"/> when the user runs it in the chat. The handler runs its workflow against the
/// thread (<see cref="CommandContext"/>) and TRIGGERS GUI callbacks on the context to inject any UI it
/// needs (e.g. <see cref="CommandContext.ShowNodePicker"/>), so the command knows nothing about Blazor
/// and a module can ship one without touching the chat view. For the common "pick a node, save it on
/// the composer" case use <see cref="MeshNodePickCommand"/> (or a <c>nodeType:Command</c> node).
///
/// <para>🚨 Synchronous + reactive — NO <c>async</c>/<c>Task</c> (this runs in the Blazor view). A
/// handler that needs IO composes <c>IObservable</c> and Subscribes; see
/// <c>Doc/Architecture/AsynchronousCalls.md</c>.</para>
/// </summary>
public interface IChatCommand
{
    /// <summary>The command name (without the <c>/</c> prefix). Must be lowercase.</summary>
    string Name { get; }

    /// <summary>Short description of the command for help text + autocomplete.</summary>
    string Description { get; }

    /// <summary>Usage syntax for the command (e.g., <c>/agent [name]</c>).</summary>
    string Usage => $"/{Name}";

    /// <summary>Optional aliases for the command.</summary>
    IReadOnlyList<string> Aliases => Array.Empty<string>();

    /// <summary>
    /// Runs the command's workflow. Triggers GUI callbacks on <paramref name="context"/> to inject
    /// any UI (e.g. <see cref="CommandContext.ShowNodePicker"/>) and/or writes thread state via
    /// <see cref="CommandContext.Hub"/>. Synchronous — compose + Subscribe for any IO, never await.
    /// </summary>
    void Execute(CommandContext context);
}
