using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// Request to create a comment on a text range.
/// </summary>
public record CreateCommentRequest : IRequest<CreateCommentResponse>
{
    /// <summary>
    /// The document to add the comment to.
    /// </summary>
    public string DocumentId { get; init; } = string.Empty;

    /// <summary>
    /// The selected text to comment on.
    /// </summary>
    public string SelectedText { get; init; } = string.Empty;

    /// <summary>
    /// First few words of the selection — used for fuzzy matching in markdown source.
    /// </summary>
    public string StartFragment { get; init; } = string.Empty;

    /// <summary>
    /// Last few words of the selection — used to find the end position in markdown source.
    /// </summary>
    public string EndFragment { get; init; } = string.Empty;

    /// <summary>
    /// The comment text.
    /// </summary>
    public string CommentText { get; init; } = string.Empty;

    /// <summary>
    /// The author of the comment.
    /// </summary>
    public string Author { get; init; } = string.Empty;
}

/// <summary>
/// Response after creating a comment.
/// </summary>
public record CreateCommentResponse
{
    /// <summary>
    /// Whether the comment was created successfully.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Identifier of the newly created comment, when successful.
    /// </summary>
    public string? CommentId { get; init; }

    /// <summary>
    /// Identifier of the marker embedded in the markdown content anchoring the comment.
    /// </summary>
    public string? MarkerId { get; init; }

    /// <summary>
    /// Error message if the comment could not be created.
    /// </summary>
    public string? Error { get; init; }
}

// NOTE: CreateSuggestedEditRequest / CreateSuggestedEditResponse are gone. A suggested edit is a
// DOCUMENT MUTATION, and every mutation goes through workspace.GetMeshNodeStream(path).Update(...) —
// not a bespoke request/response verb. The agent tool (CollaborationPlugin.SuggestEdit) writes the
// edit straight onto the document, and the version history records who changed what, when; the
// tracked-change view is projected from that history (ChangeProjection) instead of stored.
//
// NOTE: the Resolve/Delete-comment and Accept/Reject-change request records that used to live here
// had NO handler anywhere — posting them hung the caller to the timeout. They are gone; resolve,
// delete, accept and reject are node operations on the satellite / document (see the Collaboration
// plugin's ChangeActions and the CollaborativeMarkdownView), not message verbs.
