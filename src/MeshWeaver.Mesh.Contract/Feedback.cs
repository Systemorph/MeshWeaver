using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using MeshWeaver.Layout;

namespace MeshWeaver.Mesh;

/// <summary>
/// Triage status of a piece of <see cref="Feedback"/>.
/// </summary>
public enum FeedbackStatus
{
    /// <summary>Just submitted, not yet looked at.</summary>
    New,

    /// <summary>Acknowledged and being worked on / categorised.</summary>
    Triaged,

    /// <summary>Closed — addressed, answered, or dismissed.</summary>
    Resolved
}

/// <summary>
/// A single piece of user feedback, filed by the <c>/feedback</c> skill into the dedicated
/// <c>Feedback</c> space (one node per submission). The skill captures WHERE the user was
/// (<see cref="Location"/> — the app context node path) and WHO they are
/// (<see cref="SubmittedBy"/> / <see cref="SubmittedByName"/>) at submission time, so a reviewer
/// can reproduce the context without asking. Content-type of <c>nodeType = "Feedback"</c> nodes.
/// </summary>
public record Feedback
{
    /// <summary>Unique identifier for the feedback entry.</summary>
    [Browsable(false)]
    [Key]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>The feedback itself (markdown supported).</summary>
    [Markdown(EditorHeight = "150px")]
    public string Text { get; init; } = string.Empty;

    /// <summary>
    /// Where the user was when they gave the feedback — the app context node PATH taken from the
    /// "Current Application Context" the agent receives each round (e.g. <c>ACME/Reports/Q3</c>).
    /// Lets a reviewer jump straight to the page the feedback is about. Empty if given with no context.
    /// </summary>
    public string? Location { get; init; }

    /// <summary>
    /// Identity (ObjectId / email) of the user who submitted the feedback. Mirrors
    /// <see cref="MeshNode.CreatedBy"/>; kept on the content so it travels with an export.
    /// </summary>
    [Browsable(false)]
    public string? SubmittedBy { get; init; }

    /// <summary>Display name of the submitter (for showing on the review card without a User lookup).</summary>
    public string? SubmittedByName { get; init; }

    /// <summary>Optional free-text category the submitter (or triager) assigns — e.g. "bug", "idea", "praise".</summary>
    public string? Category { get; init; }

    /// <summary>When the feedback was submitted.</summary>
    [Browsable(false)]
    public DateTimeOffset SubmittedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Triage status — starts <see cref="FeedbackStatus.New"/>; reviewers advance it.</summary>
    public FeedbackStatus Status { get; init; } = FeedbackStatus.New;
}
