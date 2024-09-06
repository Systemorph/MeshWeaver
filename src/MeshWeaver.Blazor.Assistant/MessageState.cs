using MeshWeaver.Assistant;

namespace MeshWeaver.Blazor.Assistant;

public record MessageState
{
    public string Text { get; set; }

    public bool IsAssistant { get; init; }

    public IAsyncEnumerable<ChatReplyChunk> ResponseItems { get; init; }

    public CancellationToken CancellationToken { get; init; }

    public Exception Exception { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text) && Exception is null;
}
