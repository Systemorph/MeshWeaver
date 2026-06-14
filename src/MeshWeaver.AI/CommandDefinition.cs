using MeshWeaver.AI.Commands;

namespace MeshWeaver.AI;

/// <summary>
/// Content of a <c>nodeType:Command</c> mesh node — the DECLARATIVE spec for a "pick a mesh node
/// by query, write its path to a composer field" slash-command. The slash word is the node's
/// <c>Id</c> and the help text is the node's <c>Description</c> (node metadata, not duplicated
/// here — see <see cref="CommandInfo"/> for the resolved shape the picker consumes).
///
/// <para>This is the data form of <see cref="MeshNodePickCommand"/>: a command can be a tiny C#
/// subclass OR — preferably — a Command mesh node, so a Space or a NodeType can ship its own
/// commands with no code, discoverable through namespace inheritance. See
/// <c>Doc/AI/ChatCommands.md</c>.</para>
/// </summary>
public record CommandDefinition
{
    /// <summary>The mesh query whose nodes the picker lists (e.g. <c>namespace:Agent nodeType:Agent</c>).</summary>
    public required string Query { get; init; }

    /// <summary>The camelCase <c>ThreadComposer</c> field the selected node PATH is written to.</summary>
    public required string ComposerField { get; init; }

    /// <summary>Picker title (e.g. "Choose a model").</summary>
    public required string Title { get; init; }
}

/// <summary>
/// A resolved command for the chat input — its slash word (<see cref="Id"/>, from the Command
/// node's id), help text, and the pick spec. Projected from a <c>nodeType:Command</c> node by
/// <c>CommandNodeType.ProjectCommands</c>; the autocomplete lists these and execution turns the
/// matching one into a <see cref="NodePickerRequest"/>.
/// </summary>
public record CommandInfo
{
    /// <summary>The slash word (e.g. <c>model</c> for <c>/model</c>) — the Command node's id.</summary>
    public required string Id { get; init; }

    /// <summary>Help text shown in autocomplete.</summary>
    public string? Description { get; init; }

    /// <summary>The pick spec.</summary>
    public required CommandDefinition Definition { get; init; }

    /// <summary>Builds the picker request the host opens, carrying the typed argument as the search term.</summary>
    public NodePickerRequest ToPickerRequest(string? searchTerm) =>
        new(Definition.Query, Definition.ComposerField, Definition.Title, searchTerm);
}
