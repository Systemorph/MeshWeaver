namespace MeshWeaver.AI;

/// <summary>
/// Request to resubmit a user message in a thread.
/// The handler truncates ThreadMessages from the given message onwards,
/// then posts a new SubmitMessageRequest with the provided text.
/// </summary>
public record ResubmitMessageRequest
{
    public required string ThreadPath { get; init; }
    public required string MessageId { get; init; }
    public required string UserMessageText { get; init; }
}

/// <summary>
/// Request to delete a message and all subsequent messages from a thread.
/// The handler truncates ThreadMessages from the given message onwards.
/// </summary>
public record DeleteFromMessageRequest
{
    public required string ThreadPath { get; init; }
    public required string MessageId { get; init; }
}

/// <summary>
/// Request to edit a user message in a thread.
/// The handler truncates ThreadMessages from the given message onwards,
/// creates a new EditingPrompt message node with the original text,
/// and appends it to ThreadMessages so the user can edit and resubmit.
/// </summary>
public record EditMessageRequest
{
    public required string ThreadPath { get; init; }
    public required string MessageId { get; init; }
    public required string MessageText { get; init; }
}
