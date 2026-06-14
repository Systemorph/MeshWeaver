#nullable enable

namespace MeshWeaver.AI.Commands;

/// <summary>
/// Reusable base for the very common "pick a mesh node by query, write its path to a composer
/// field" slash command. A concrete command declares only its <see cref="Name"/>, the mesh
/// <see cref="Query"/> whose nodes the picker lists, the composer <see cref="ComposerField"/> the
/// selected path is written to, and a <see cref="Title"/>. Everything else is inherited:
/// no argument → show the picker; an argument → pre-filter + auto-select an exact match.
///
/// <para>A module adds such a command with a tiny subclass plus one DI registration —
/// <c>services.TryAddEnumerable(ServiceDescriptor.Singleton&lt;IChatCommand, MyPickCommand&gt;())</c> —
/// and it immediately appears in the slash-command autocomplete and works in the chat input, with
/// NO changes to <see cref="CommandContext"/> or the chat view. See <c>Doc/AI/ChatCommands.md</c>
/// for an executable example.</para>
/// </summary>
public abstract class MeshNodePickCommand : IChatCommand
{
    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <summary>The mesh query whose nodes the picker lists (e.g. <c>namespace:Agent nodeType:Agent</c>).</summary>
    protected abstract string Query { get; }

    /// <summary>
    /// The camelCase <c>ThreadComposer</c> field the selected node PATH is written to
    /// (e.g. <c>harness</c>, <c>agentName</c>, <c>modelName</c>).
    /// </summary>
    protected abstract string ComposerField { get; }

    /// <summary>Picker title (e.g. "Choose a model").</summary>
    protected abstract string Title { get; }

    /// <inheritdoc />
    public virtual string Usage => $"/{Name} [name]";

    /// <inheritdoc />
    public void Execute(CommandContext context)
    {
        var raw = context.ParsedCommand.RawArguments.Trim();
        // Normalise the argument to a bare name: "@agent/Worker" / "Agent/Worker" → "Worker".
        var term = raw.Length == 0
            ? null
            : raw.Contains('/') ? raw[(raw.LastIndexOf('/') + 1)..].Trim() : raw;

        // Trigger the host's node-selector GUI; on selection it writes the chosen node PATH onto the
        // composer's ComposerField. The picker (and its default-to-first) order/eligibility come from
        // the Query (e.g. `... sort:order`) — never replicated in the GUI.
        context.ShowNodePicker?.Invoke(new NodePickerRequest(Query, ComposerField, Title, term));
    }
}
