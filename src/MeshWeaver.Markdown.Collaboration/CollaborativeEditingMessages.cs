using MeshWeaver.Data;
using MeshWeaver.Messaging;

namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// Request to apply a text edit operation to a collaborative document.
/// </summary>
public record ApplyTextEditRequest : IRequest<ApplyTextEditResponse>
{
    /// <summary>
    /// The operation to apply.
    /// </summary>
    public TextOperation Operation { get; init; } = null!;

    /// <summary>
    /// Client identifier for deduplication.
    /// </summary>
    public string ClientId { get; init; } = string.Empty;
}

/// <summary>
/// Response after applying a text edit operation.
/// </summary>
public record ApplyTextEditResponse
{
    /// <summary>
    /// Whether the operation was successfully applied.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// The new document version after the operation.
    /// </summary>
    public long NewVersion { get; init; }

    /// <summary>
    /// The operation as transformed by the server (may differ from original if concurrent edits occurred).
    /// </summary>
    public TextOperation? TransformedOperation { get; init; }

    /// <summary>
    /// Error message if the operation failed.
    /// </summary>
    public string? Error { get; init; }

    /// <summary>
    /// Operations from other users that the client needs to catch up on.
    /// </summary>
    public IReadOnlyList<TextOperation> PendingOperations { get; init; } = [];
}

/// <summary>
/// Event broadcast when a document changes.
/// </summary>
public record TextChangedEvent : StreamMessage
{
    public TextChangedEvent(string streamId) : base(streamId) { }

    /// <summary>
    /// The operation that was applied.
    /// </summary>
    public TextOperation Operation { get; init; } = null!;

    /// <summary>
    /// The document version after this operation.
    /// </summary>
    public long Version { get; init; }

    /// <summary>
    /// Who made the change.
    /// </summary>
    public string ChangedBy { get; init; } = string.Empty;
}

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
    /// Character position where the selection starts.
    /// </summary>
    public int SelectionStart { get; init; }

    /// <summary>
    /// Character position where the selection ends.
    /// </summary>
    public int SelectionEnd { get; init; }

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
    public bool Success { get; init; }
    public string? CommentId { get; init; }
    public string? MarkerId { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// Request to resolve (close) a comment.
/// </summary>
public record ResolveCommentRequest(string CommentId, string ResolvedBy) : IRequest<ResolveCommentResponse>;

/// <summary>
/// Response after resolving a comment.
/// </summary>
public record ResolveCommentResponse(bool Success, string? Error);

/// <summary>
/// Request to delete a comment.
/// </summary>
public record DeleteCommentRequest(string CommentId) : IRequest<DeleteCommentResponse>;

/// <summary>
/// Response after deleting a comment.
/// </summary>
public record DeleteCommentResponse(bool Success, string? Error);

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

/// <summary>
/// Request to accept a tracked change.
/// </summary>
public record AcceptChangeRequest(string ChangeId, string ReviewedBy) : IRequest<AcceptChangeResponse>;

/// <summary>
/// Response after accepting a change.
/// </summary>
public record AcceptChangeResponse(bool Success, string? Error);

/// <summary>
/// Request to reject a tracked change.
/// </summary>
public record RejectChangeRequest(string ChangeId, string ReviewedBy) : IRequest<RejectChangeResponse>;

/// <summary>
/// Response after rejecting a change.
/// </summary>
public record RejectChangeResponse(bool Success, string? Error);
