using System.Reactive.Linq;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.AI;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// The framework <see cref="ISummarizer"/>: a single <see cref="IChatClient"/> chat-completion that
/// produces a short natural-language summary of a document's extracted text. The model call is ONE
/// I/O-pool leaf (the <see cref="IoPoolNames.Http"/> pool) — it takes its own slot, runs off the
/// hub/grain scheduler, and the result bridges back into the reactive contract. The orchestration in
/// <see cref="ContentIndexingService"/> holds no slot while it runs.
///
/// <para>No <c>async</c>/<c>await</c> escapes this type: the awaitable <see cref="IChatClient"/> call
/// is sealed inside <c>ioPool.Invoke(ct =&gt; ...)</c>, and the public surface is
/// <see cref="IObservable{T}"/>. The text is truncated to a sane budget before the call so a large
/// file doesn't blow the model's context window (summarizing the head is sufficient for a title-card
/// summary, and the chunk-embed branch indexes the whole file for retrieval).</para>
/// </summary>
public sealed class ChatClientSummarizer : ISummarizer
{
    /// <summary>
    /// Max characters of extracted text fed to the model. A summary is a title-card, not a full
    /// re-read — the head of the document carries the gist, and the full text is already chunked +
    /// embedded for retrieval. Keeps the prompt well within typical context budgets.
    /// </summary>
    public const int MaxTextChars = 12_000;

    private readonly IChatClient _chatClient;
    private readonly IIoPool _httpPool;

    /// <summary>
    /// Resolves the bounded HTTP I/O pool (<see cref="IoPoolNames.Http"/>) from the mesh-scoped
    /// <see cref="IoPoolRegistry"/> so the model call shares the same outbound-network gate as every
    /// other HTTP leaf.
    /// </summary>
    public ChatClientSummarizer(IChatClient chatClient, IoPoolRegistry ioPoolRegistry)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _httpPool = (ioPoolRegistry ?? throw new ArgumentNullException(nameof(ioPoolRegistry)))
            .Get(IoPoolNames.Http);
    }

    /// <inheritdoc />
    public IObservable<string> Summarize(string text, string fileName)
    {
        var prompt = BuildPrompt(fileName, Truncate(text));
        // The model round-trip is the single IIoPool leaf — cold, bounded, off-scheduler.
        return _httpPool.Invoke(ct => _chatClient.GetResponseAsync(prompt, options: null, ct))
            .Select(response => response.Text ?? string.Empty);
    }

    private static string Truncate(string text) =>
        string.IsNullOrEmpty(text) || text.Length <= MaxTextChars ? text : text[..MaxTextChars];

    /// <summary>
    /// Builds the summarization prompt. <paramref name="fileName"/> is a title/format hint; the body
    /// asks for a short, plain-language summary suitable as a document card.
    /// </summary>
    internal static string BuildPrompt(string fileName, string text) =>
        $"""
         Summarize the following document in 2-4 concise sentences. Capture what it is about and the
         key points. Write plain prose — no preamble, no markdown headings, no bullet list.

         File name: {fileName}

         Document text:
         {text}
         """;
}
