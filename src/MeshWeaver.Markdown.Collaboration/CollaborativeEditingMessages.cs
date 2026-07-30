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

/// <summary>
/// Request to create a suggested edit (track change).
/// </summary>
public record CreateSuggestedEditRequest : IRequest<CreateSuggestedEditResponse>
{
    /// <summary>
    /// The document to add the suggestion to.
    /// </summary>
    public string DocumentId { get; init; } = string.Empty;

    /// <summary>
    /// Character position where the edit applies.
    /// </summary>
    public int Position { get; init; }

    /// <summary>
    /// Text to insert (for insertions and replacements).
    /// </summary>
    public string? InsertedText { get; init; }

    /// <summary>
    /// Text to delete (for deletions and replacements).
    /// </summary>
    public string? DeletedText { get; init; }

    /// <summary>
    /// The author suggesting the edit.
    /// </summary>
    public string Author { get; init; } = string.Empty;
}

/// <summary>
/// Response after creating a suggested edit.
/// </summary>
public record CreateSuggestedEditResponse(bool Success, string? ChangeId, string? Error);

// NOTE: the Resolve/Delete-comment and Accept/Reject-change request records that used to live here
// had NO handler anywhere — posting them hung the caller to the timeout. They are gone; resolve,
// delete, accept and reject are node operations on the satellite / document (see the Collaboration
// plugin's ChangeActions and the CollaborativeMarkdownView), not message verbs.
