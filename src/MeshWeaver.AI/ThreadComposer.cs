using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.AI;

/// <summary>
/// Content of the per-user <b>ThreadComposer</b> singleton node at
/// <c>{userHome}/_Thread/ThreadComposer</c> — the persisted, 100% data-bound state of the chat
/// composer (the "new chat" box). The composer renders this record through the framework
/// <c>Edit</c> macro: the property attributes below decide the controls (message editor +
/// agent/model/harness <c>MeshNodePicker</c>s). The Send button (<see cref="ThreadComposerView"/>)
/// submits the message via <see cref="HubThreadExtensions.StartThread"/>.
///
/// <para><b>Used outside a thread</b> (the new-chat composer). Inside a thread the selection lives on
/// the thread node (<see cref="Thread.SelectedHarness"/> / <see cref="Thread.SelectedAgentName"/> /
/// <see cref="Thread.SelectedModelName"/>).</para>
/// </summary>
public record ThreadComposer
{
    /// <summary>The in-progress composer text — the message currently being typed.</summary>
    [Description("Message")]
    [UiControl<TextAreaControl>]
    public string? MessageContent { get; init; }

    /// <summary>Selected harness node path (a <c>nodeType:Harness</c> catalog node).</summary>
    [Description("Harness")]
    [MeshNode("namespace:Harness nodeType:Harness")]
    public string? Harness { get; init; }

    /// <summary>Selected agent node path (a <c>nodeType:Agent</c> node).</summary>
    [Description("Agent")]
    [MeshNode("namespace:Agent nodeType:Agent")]
    public string? AgentName { get; init; }

    /// <summary>Selected model node path (a <c>nodeType:LanguageModel</c> node).</summary>
    [Description("Model")]
    [MeshNode("namespace:_Provider nodeType:LanguageModel scope:descendants")]
    public string? ModelName { get; init; }

    /// <summary>Paths attached as @-references / context chips on the next message.</summary>
    [Editable(false)]
    public IReadOnlyList<string>? Attachments { get; init; }

    /// <summary>The navigation context path the next thread should carry.</summary>
    [Editable(false)]
    public string? ContextPath { get; init; }
}
