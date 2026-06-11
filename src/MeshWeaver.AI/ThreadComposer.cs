using System.Collections.Immutable;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Domain;
using MeshWeaver.Layout;

namespace MeshWeaver.AI;

/// <summary>
/// The persisted, 100% data-bound state of a chat composer (the chat input box):
/// draft text + the selected harness / agent / model (as picked node PATHS) + attachments.
///
/// <para><b>Two homes, one record:</b></para>
/// <list type="bullet">
///   <item><description><b>Out of a thread</b> — content of the per-user singleton node at
///   <c>{userHome}/_Thread/ThreadComposer</c> (the "new chat" box). Submitting copies this
///   record onto the created thread (<see cref="Thread.Composer"/>), empties the draft, and
///   stamps <see cref="OpenThreadPath"/> so the side panel navigates to the new thread.
///   </description></item>
///   <item><description><b>Inside a thread</b> — embedded on the thread content as
///   <see cref="Thread.Composer"/> (NOT a separate node, so reads can never hit a missing
///   satellite). <c>hub.SubmitComposer</c> drains <see cref="MessageContent"/> into
///   <see cref="Thread.PendingUserMessages"/> and empties the composer in ONE atomic
///   <c>stream.Update</c>.</description></item>
/// </list>
///
/// <para>The record renders through the framework <c>Edit</c> macro: the property attributes
/// below decide the controls (message editor + harness/agent/model <c>MeshNodePicker</c>s).
/// See <see cref="ThreadComposerView"/>.</para>
/// </summary>
public record ThreadComposer
{
    /// <summary>The in-progress composer text — the message currently being typed.</summary>
    [Description("Message")]
    [UiControl<TextAreaControl>]
    public string? MessageContent { get; init; }

    /// <summary>Selected harness node path (a <c>nodeType:Harness</c> catalog node).</summary>
    [Description("Harness")]
    [MeshNode("namespace:Harness nodeType:Harness",
        Layout = MeshNodePickerLayout.Thin, Open = MeshNodePickerOpenDirection.Up, DefaultToFirst = true)]
    public string? Harness { get; init; }

    /// <summary>Selected agent node path (a <c>nodeType:Agent</c> node).</summary>
    [Description("Agent")]
    [MeshNode("namespace:Agent nodeType:Agent",
        Layout = MeshNodePickerLayout.Thin, Open = MeshNodePickerOpenDirection.Up, DefaultToFirst = true)]
    public string? AgentName { get; init; }

    /// <summary>Selected model node path (a <c>nodeType:LanguageModel</c> node).</summary>
    [Description("Model")]
    [MeshNode("namespace:_Provider nodeType:LanguageModel scope:descendants",
        Layout = MeshNodePickerLayout.Thin, Open = MeshNodePickerOpenDirection.Up, DefaultToFirst = true)]
    public string? ModelName { get; init; }

    /// <summary>Paths attached as @-references / context chips on the next message.</summary>
    [Editable(false)]
    public ImmutableList<string>? Attachments { get; init; }

    /// <summary>
    /// The navigation context path the next thread should carry. Written by the side panel
    /// whenever the navigation context changes (out-of-thread composer only); Send creates the
    /// thread under <c>{MainNodeOf(ContextPath)}/_Thread/{speakingId}</c>.
    /// </summary>
    [Editable(false)]
    public string? ContextPath { get; init; }

    /// <summary>
    /// Path of the thread the composer's last Send created — the data-bound "navigate here"
    /// signal. Stamped by the Send click in the SAME composer write that empties the draft;
    /// the side panel observes the composer node, opens the thread, and clears this field.
    /// Exists because the Send click runs on the composer node's server hub, which cannot
    /// reach circuit services — navigation flows through data, like everything else.
    /// </summary>
    [Editable(false)]
    public string? OpenThreadPath { get; init; }

    /// <summary>
    /// Value-based equality including <see cref="Attachments"/> by SEQUENCE. The synthesized
    /// record equality compares list members by reference, so every deserialized server echo of
    /// a non-null attachment list would look "changed" — defeating the echo-dedup guards in the
    /// composer binding/auto-save and turning them into a write loop.
    /// </summary>
    public virtual bool Equals(ThreadComposer? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return MessageContent == other.MessageContent
               && Harness == other.Harness
               && AgentName == other.AgentName
               && ModelName == other.ModelName
               && ContextPath == other.ContextPath
               && OpenThreadPath == other.OpenThreadPath
               && (Attachments ?? []).SequenceEqual(other.Attachments ?? []);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(MessageContent);
        hash.Add(Harness);
        hash.Add(AgentName);
        hash.Add(ModelName);
        hash.Add(ContextPath);
        hash.Add(OpenThreadPath);
        foreach (var attachment in Attachments ?? [])
            hash.Add(attachment);
        return hash.ToHashCode();
    }
}
