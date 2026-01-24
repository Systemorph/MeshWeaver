using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.AI.Streaming;

/// <summary>
/// Manages a streaming chat session with support for cancellation and new message injection.
/// Allows users to cancel current streaming and send new messages immediately.
/// </summary>
public class StreamingChatSession : IDisposable
{
    private readonly IAgentChat _agentChat;
    private readonly ILogger<StreamingChatSession>? _logger;
    private CancellationTokenSource? _currentStreamCts;
    private readonly object _streamLock = new();
    private bool _isStreaming;
    private bool _disposed;

    /// <summary>
    /// Event raised when streaming starts.
    /// </summary>
    public event Action? StreamingStarted;

    /// <summary>
    /// Event raised when streaming ends (completed or cancelled).
    /// </summary>
    public event Action<StreamingEndReason>? StreamingEnded;

    /// <summary>
    /// Event raised when a partial response is saved due to cancellation.
    /// </summary>
    public event Action<string>? PartialResponseSaved;

    public StreamingChatSession(IAgentChat agentChat, ILogger<StreamingChatSession>? logger = null)
    {
        _agentChat = agentChat;
        _logger = logger;
    }

    /// <summary>
    /// Gets whether a streaming operation is currently in progress.
    /// </summary>
    public bool IsStreaming
    {
        get
        {
            lock (_streamLock)
            {
                return _isStreaming;
            }
        }
    }

    /// <summary>
    /// Cancels the current streaming operation if one is in progress.
    /// </summary>
    /// <returns>True if a stream was cancelled, false if no stream was active.</returns>
    public bool CancelCurrentStream()
    {
        lock (_streamLock)
        {
            if (_currentStreamCts != null && !_currentStreamCts.IsCancellationRequested)
            {
                _logger?.LogInformation("Cancelling current stream");
                _currentStreamCts.Cancel();
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Gets a streaming response, automatically cancelling any existing stream.
    /// </summary>
    /// <param name="messages">The messages to send.</param>
    /// <param name="cancellationToken">External cancellation token.</param>
    /// <returns>Async enumerable of chat response updates.</returns>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Cancel any existing stream
        CancelCurrentStream();

        // Create new cancellation token source
        CancellationTokenSource streamCts;
        lock (_streamLock)
        {
            _currentStreamCts?.Dispose();
            _currentStreamCts = new CancellationTokenSource();
            streamCts = _currentStreamCts;
            _isStreaming = true;
        }

        // Link with external cancellation token
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(streamCts.Token, cancellationToken);

        _logger?.LogDebug("Starting new streaming session");
        StreamingStarted?.Invoke();

        var partialResponse = new StringBuilder();
        var endReason = StreamingEndReason.Completed;
        Exception? caughtException = null;

        // Get the enumerator outside of try-catch so we can yield
        var enumerator = _agentChat.GetStreamingResponseAsync(messages, linkedCts.Token).GetAsyncEnumerator(linkedCts.Token);

        try
        {
            while (true)
            {
                ChatResponseUpdate update;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                        break;
                    update = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    _logger?.LogInformation("Stream cancelled");
                    endReason = StreamingEndReason.Cancelled;

                    // Save partial response if we have content
                    if (partialResponse.Length > 0)
                    {
                        var partial = partialResponse.ToString();
                        _logger?.LogDebug("Saving partial response: {Length} chars", partial.Length);
                        PartialResponseSaved?.Invoke(partial);
                    }
                    break;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Stream error");
                    endReason = StreamingEndReason.Error;
                    caughtException = ex;
                    break;
                }

                // Accumulate text for potential partial save
                if (!string.IsNullOrEmpty(update.Text))
                {
                    partialResponse.Append(update.Text);
                }

                yield return update;
            }
        }
        finally
        {
            await enumerator.DisposeAsync();

            lock (_streamLock)
            {
                _isStreaming = false;
            }

            StreamingEnded?.Invoke(endReason);
            _logger?.LogDebug("Streaming session ended: {Reason}", endReason);
        }

        if (caughtException != null)
        {
            throw caughtException;
        }
    }

    /// <summary>
    /// Sends a new message, cancelling any existing stream first.
    /// This allows users to interrupt the current response and send a new query.
    /// </summary>
    /// <param name="messages">The messages to send.</param>
    /// <param name="cancellationToken">External cancellation token.</param>
    /// <returns>Async enumerable of chat response updates.</returns>
    public async IAsyncEnumerable<ChatResponseUpdate> InterruptAndSendAsync(
        IReadOnlyCollection<ChatMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // This is essentially the same as GetStreamingResponseAsync since it already
        // cancels any existing stream before starting a new one
        await foreach (var update in GetStreamingResponseAsync(messages, cancellationToken))
        {
            yield return update;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_streamLock)
        {
            _currentStreamCts?.Cancel();
            _currentStreamCts?.Dispose();
            _currentStreamCts = null;
        }
    }
}

/// <summary>
/// Reason why streaming ended.
/// </summary>
public enum StreamingEndReason
{
    /// <summary>
    /// Stream completed successfully.
    /// </summary>
    Completed,

    /// <summary>
    /// Stream was cancelled by user or new message.
    /// </summary>
    Cancelled,

    /// <summary>
    /// Stream ended due to an error.
    /// </summary>
    Error
}
