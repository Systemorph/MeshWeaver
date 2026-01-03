using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace MeshWeaver.Markdown.Collaboration;

/// <summary>
/// Coordinates collaborative editing for markdown documents.
/// Maintains document state and applies OT transformations for concurrent edits.
/// </summary>
public class CollaborativeEditingCoordinator
{
    private readonly ConcurrentDictionary<string, DocumentEditState> _documents = new();
    private readonly TextOperationTransformer _transformer = new();

    /// <summary>
    /// Applies an operation to a document with OT transformation.
    /// </summary>
    /// <param name="documentId">The document identifier.</param>
    /// <param name="operation">The operation to apply.</param>
    /// <param name="currentContent">The current document content (used for initialization).</param>
    /// <returns>The result including the transformed operation and new version.</returns>
    public ApplyTextEditResponse ApplyOperation(
        string documentId,
        TextOperation operation,
        string currentContent)
    {
        var state = _documents.GetOrAdd(documentId, id => new DocumentEditState
        {
            DocumentId = id,
            Content = currentContent,
            Version = 0
        });

        lock (state)
        {
            try
            {
                // Transform the operation against any operations that came after its base version
                var transformedOp = operation;
                var pendingOps = state.PendingOperations
                    .Where(o => o.BaseVersion > operation.BaseVersion)
                    .ToList();

                foreach (var pendingOp in pendingOps)
                {
                    transformedOp = _transformer.Transform(pendingOp, transformedOp);
                }

                // Apply the transformed operation to the document
                var newContent = _transformer.ApplyOperation(state.Content, transformedOp);
                state.Content = newContent;
                state.Version++;
                state.LastModified = DateTimeOffset.UtcNow;

                // Store the operation for future transformations
                var storedOp = transformedOp with { BaseVersion = state.Version };
                state.PendingOperations.Add(storedOp);

                // Trim old operations (keep last 100 for late-joining clients)
                if (state.PendingOperations.Count > 100)
                {
                    state.PendingOperations.RemoveRange(0, state.PendingOperations.Count - 100);
                }

                // Update vector clock
                if (!string.IsNullOrEmpty(operation.UserId))
                {
                    var userClock = state.VectorClock.GetValueOrDefault(operation.UserId, 0);
                    state.VectorClock = state.VectorClock.SetItem(operation.UserId, userClock + 1);
                }

                return new ApplyTextEditResponse
                {
                    Success = true,
                    NewVersion = state.Version,
                    TransformedOperation = storedOp
                };
            }
            catch (Exception ex)
            {
                return new ApplyTextEditResponse
                {
                    Success = false,
                    Error = ex.Message
                };
            }
        }
    }

    /// <summary>
    /// Gets the current state of a document.
    /// </summary>
    public MarkdownDocumentState? GetDocumentState(string documentId)
    {
        if (!_documents.TryGetValue(documentId, out var state))
            return null;

        return new MarkdownDocumentState
        {
            DocumentId = documentId,
            Version = state.Version,
            VectorClock = state.VectorClock,
            LastModified = state.LastModified
        };
    }

    /// <summary>
    /// Gets the current content of a document.
    /// </summary>
    public string? GetDocumentContent(string documentId)
    {
        return _documents.TryGetValue(documentId, out var state) ? state.Content : null;
    }

    /// <summary>
    /// Gets operations that occurred after a specific version (for syncing).
    /// </summary>
    public IReadOnlyList<TextOperation> GetOperationsSince(string documentId, long sinceVersion)
    {
        if (!_documents.TryGetValue(documentId, out var state))
            return [];

        return state.PendingOperations
            .Where(o => o.BaseVersion > sinceVersion)
            .ToList();
    }

    /// <summary>
    /// Initializes or resets a document with the given content.
    /// </summary>
    public void InitializeDocument(string documentId, string content)
    {
        _documents[documentId] = new DocumentEditState
        {
            DocumentId = documentId,
            Content = content,
            Version = 0,
            LastModified = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Registers an active editing session for presence awareness.
    /// </summary>
    public void RegisterSession(string documentId, EditingSession session)
    {
        if (!_documents.TryGetValue(documentId, out var state))
            return;

        lock (state)
        {
            // Remove existing session for the same user
            var sessions = state.ActiveSessions
                .Where(s => s.SessionId != session.SessionId)
                .ToImmutableList();

            state.ActiveSessions = sessions.Add(session);
        }
    }

    /// <summary>
    /// Updates an editing session's cursor position.
    /// </summary>
    public void UpdateSessionCursor(string documentId, string sessionId, int cursorPosition, int? selectionStart = null, int? selectionEnd = null)
    {
        if (!_documents.TryGetValue(documentId, out var state))
            return;

        lock (state)
        {
            var session = state.ActiveSessions.FirstOrDefault(s => s.SessionId == sessionId);
            if (session != null)
            {
                var updatedSession = session with
                {
                    CursorPosition = cursorPosition,
                    SelectionStart = selectionStart,
                    SelectionEnd = selectionEnd,
                    LastActivity = DateTimeOffset.UtcNow
                };

                state.ActiveSessions = state.ActiveSessions
                    .Replace(session, updatedSession);
            }
        }
    }

    /// <summary>
    /// Removes an editing session.
    /// </summary>
    public void UnregisterSession(string documentId, string sessionId)
    {
        if (!_documents.TryGetValue(documentId, out var state))
            return;

        lock (state)
        {
            state.ActiveSessions = state.ActiveSessions
                .Where(s => s.SessionId != sessionId)
                .ToImmutableList();
        }
    }

    /// <summary>
    /// Gets active sessions for a document.
    /// </summary>
    public IReadOnlyList<EditingSession> GetActiveSessions(string documentId)
    {
        if (!_documents.TryGetValue(documentId, out var state))
            return [];

        return state.ActiveSessions;
    }

    /// <summary>
    /// Cleans up stale sessions (inactive for more than timeout).
    /// </summary>
    public void CleanupStaleSessions(TimeSpan timeout)
    {
        var cutoff = DateTimeOffset.UtcNow - timeout;

        foreach (var state in _documents.Values)
        {
            lock (state)
            {
                state.ActiveSessions = state.ActiveSessions
                    .Where(s => s.LastActivity > cutoff)
                    .ToImmutableList();
            }
        }
    }

    /// <summary>
    /// Removes a document from the coordinator.
    /// </summary>
    public bool RemoveDocument(string documentId)
    {
        return _documents.TryRemove(documentId, out _);
    }
}

/// <summary>
/// Internal state for a collaboratively edited document.
/// </summary>
internal class DocumentEditState
{
    public string DocumentId { get; init; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public long Version { get; set; }
    public List<TextOperation> PendingOperations { get; } = [];
    public ImmutableDictionary<string, long> VectorClock { get; set; } = ImmutableDictionary<string, long>.Empty;
    public ImmutableList<EditingSession> ActiveSessions { get; set; } = ImmutableList<EditingSession>.Empty;
    public DateTimeOffset LastModified { get; set; } = DateTimeOffset.UtcNow;
}
