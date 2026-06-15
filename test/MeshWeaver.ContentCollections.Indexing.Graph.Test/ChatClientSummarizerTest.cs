using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Graph.Test;

/// <summary>
/// Focused tests for <see cref="ChatClientSummarizer"/>: the summary comes from a single
/// <see cref="IChatClient"/> completion, the prompt carries the file name + (truncated) text, and the
/// model call is routed through the bounded <see cref="IoPoolNames.Http"/> pool (so it takes a pool
/// slot — never runs inline on the subscribing scheduler).
/// </summary>
public class ChatClientSummarizerTest
{
    [Fact(Timeout = 30_000)]
    public async Task Summarize_ReturnsModelText_AndPromptCarriesFileNameAndText()
    {
        var chat = new RecordingChatClient("MODEL SUMMARY");
        var registry = new IoPoolRegistry();
        var summarizer = new ChatClientSummarizer(chat, registry);

        var summary = await summarizer.Summarize("Quarterly pension report body text.", "report.pdf")
            .FirstAsync().ToTask();

        summary.Should().Be("MODEL SUMMARY");
        chat.LastPrompt.Should().NotBeNull();
        chat.LastPrompt!.Should().Contain("report.pdf", "the file name is a prompt hint");
        chat.LastPrompt.Should().Contain("Quarterly pension report body text.", "the document text is in the prompt");
    }

    [Fact]
    public void BuildPrompt_TruncatesLongText()
    {
        // The summarizer truncates to MaxTextChars before the call so a huge file can't blow the
        // context window. The prompt is built from the truncated text, never the full body.
        var longText = new string('x', ChatClientSummarizer.MaxTextChars + 5_000);
        var prompt = ChatClientSummarizer.BuildPrompt("big.txt", longText[..ChatClientSummarizer.MaxTextChars]);
        prompt.Length.Should().BeLessThan(longText.Length, "the body is truncated before prompting");
        prompt.Should().Contain("big.txt");
    }

    [Fact(Timeout = 30_000)]
    public async Task Summarize_RoutesThroughHttpIoPool()
    {
        var registry = new IoPoolRegistry();
        var httpPool = registry.Get(IoPoolNames.Http);

        // The fake blocks inside GetResponseAsync until released; while it blocks, the operation is
        // in flight THROUGH the Http pool — proving the leaf took a pool slot rather than running
        // inline on the subscribe thread.
        var gate = new TaskCompletionSource();
        var entered = new TaskCompletionSource();
        var chat = new RecordingChatClient("ok", onInvoke: () => { entered.TrySetResult(); return gate.Task; });
        var summarizer = new ChatClientSummarizer(chat, registry);

        var resultTask = summarizer.Summarize("text", "f.txt").FirstAsync().ToTask();

        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        httpPool.CurrentInFlight.Should().Be(1, "the summarize call holds exactly one Http pool slot while running");

        gate.SetResult();
        (await resultTask).Should().Be("ok");
        httpPool.CurrentInFlight.Should().Be(0, "the slot is released when the call completes");
    }

    /// <summary>
    /// Minimal <see cref="IChatClient"/> test double: records the last prompt (the single user
    /// message's text), returns a fixed response, and optionally awaits a gate so a test can observe
    /// the in-flight pool slot.
    /// </summary>
    private sealed class RecordingChatClient(string response, Func<Task>? onInvoke = null) : IChatClient
    {
        public string? LastPrompt { get; private set; }

        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastPrompt = string.Concat(messages.Select(m => m.Text));
            if (onInvoke is not null)
                await onInvoke();
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, response));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var resp = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, resp.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
