namespace MeshWeaver.AI;

/// <summary>
/// Content of the per-user <b>ThreadComposer</b> singleton node at
/// <c>{userHome}/_Thread/ThreadComposer</c> — the persisted state of the out-of-thread
/// chat composer (the "new chat" box). Carries the in-progress message text plus the
/// three composer comboboxes (harness / agent / model) and any reference attachments,
/// so the user's draft + selection survive a reload server-side (no browser storage).
///
/// <para><b>Used ONLY outside a thread.</b> Inside a thread the composer's selection
/// lives on the thread node itself (<see cref="Thread.SelectedHarness"/> /
/// <see cref="Thread.SelectedAgentName"/> / <see cref="Thread.SelectedModelName"/>) —
/// see <c>ThreadChatView.PersistSelection</c>. A dedicated record (rather than reusing
/// <see cref="Thread"/>) keeps the input box's content to exactly the fields it owns,
/// instead of the dozens of execution/message fields on a conversation thread.</para>
/// </summary>
public record ThreadComposer
{
    /// <summary>The in-progress composer text — the message currently being typed.</summary>
    public string? MessageContent { get; init; }

    /// <summary>Selected harness (one of <see cref="Harnesses"/>) — combobox 1.</summary>
    public string? Harness { get; init; }

    /// <summary>Selected agent name — combobox 2.</summary>
    public string? AgentName { get; init; }

    /// <summary>Selected model name — combobox 3.</summary>
    public string? ModelName { get; init; }

    /// <summary>Paths attached as @-references / context chips on the next message.</summary>
    public IReadOnlyList<string>? Attachments { get; init; }

    /// <summary>The navigation context path the next thread should carry.</summary>
    public string? ContextPath { get; init; }
}
