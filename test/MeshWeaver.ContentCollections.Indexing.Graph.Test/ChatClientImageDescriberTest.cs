using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.CompilerServices;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.AI;
using Xunit;

namespace MeshWeaver.ContentCollections.Indexing.Graph.Test;

/// <summary>
/// Focused tests for <see cref="ChatClientImageDescriber"/>: the description comes from a single
/// MULTIMODAL <see cref="IChatClient"/> completion whose message carries the image bytes as a
/// <see cref="DataContent"/> part (correct media type) plus a text prompt; the client is resolved
/// lazily and a null client (or a failing call) degrades to an empty description rather than throwing.
/// </summary>
public class ChatClientImageDescriberTest
{
    private static readonly byte[] SampleBytes = [0x89, 0x50, 0x4E, 0x47, 0x01, 0x02, 0x03];

    [Fact(Timeout = 30_000)]
    public async Task Describe_SendsImageAsDataContent_AndReturnsModelText()
    {
        var chat = new RecordingChatClient("A bar chart of quarterly revenue.");
        var describer = new ChatClientImageDescriber(() => chat, new IoPoolRegistry());

        var description = await describer.Describe(SampleBytes, "chart.jpg").FirstAsync().ToTask();

        description.Should().Be("A bar chart of quarterly revenue.");
        chat.LastPrompt.Should().Contain("chart.jpg", "the file name is a prompt hint");
        chat.LastImage.Should().NotBeNull("the image is sent as a DataContent part");
        chat.LastImage!.MediaType.Should().Be("image/jpeg", ".jpg maps to image/jpeg");
        chat.LastImage.Data.ToArray().Should().Equal(SampleBytes);
    }

    [Fact]
    public async Task Describe_NoClient_ReturnsEmpty()
    {
        var describer = new ChatClientImageDescriber(() => null, new IoPoolRegistry());

        var description = await describer.Describe(SampleBytes, "chart.png").FirstAsync().ToTask();

        description.Should().BeEmpty("no vision model available → the image indexes as no-text");
    }

    [Fact]
    public async Task Describe_ClientThrows_ReturnsEmpty()
    {
        var chat = new RecordingChatClient("unused", onInvoke: () => throw new InvalidOperationException("boom"));
        var describer = new ChatClientImageDescriber(() => chat, new IoPoolRegistry());

        var description = await describer.Describe(SampleBytes, "chart.png").FirstAsync().ToTask();

        description.Should().BeEmpty("a failed describe call must not fail the indexing activity");
    }

    [Fact]
    public void SupportedExtensions_CoverCommonRasterImages()
    {
        var describer = new ChatClientImageDescriber(() => null, new IoPoolRegistry());
        describer.SupportedExtensions.Should().Contain([".png", ".jpg", ".jpeg", ".gif", ".webp"]);
    }

    /// <summary>
    /// Minimal multimodal <see cref="IChatClient"/> test double: records the last text prompt and the
    /// last image <see cref="DataContent"/> part, returns a fixed response, and optionally runs a hook
    /// (e.g. to throw) inside the call.
    /// </summary>
    private sealed class RecordingChatClient(string response, Action? onInvoke = null) : IChatClient
    {
        public string? LastPrompt { get; private set; }
        public DataContent? LastImage { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var all = messages.SelectMany(m => m.Contents).ToList();
            LastPrompt = string.Concat(all.OfType<TextContent>().Select(t => t.Text));
            LastImage = all.OfType<DataContent>().FirstOrDefault(d => d.MediaType?.StartsWith("image/") == true);
            onInvoke?.Invoke();
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, response)));
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
