using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.ContentCollections.Indexing;
using MeshWeaver.Mesh.Threading;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshWeaver.ContentCollections.Indexing.Graph;

/// <summary>
/// The framework <see cref="IImageDescriber"/>: a single MULTIMODAL <see cref="IChatClient"/> call that
/// captions an image for alt-text and search. The image bytes are sent as a <see cref="DataContent"/>
/// part alongside a text prompt; the model round-trip is ONE <see cref="IoPoolNames.Http"/> I/O-pool
/// leaf — it takes its own slot, runs off the hub/grain scheduler, and bridges back into the reactive
/// contract. No <c>async</c>/<c>await</c> escapes the public surface.
///
/// <para>The chat client is resolved LAZILY per call through the supplied factory delegate
/// (typically the mesh's <c>DefaultChatClientProvider</c>) rather than captured at construction: the
/// default model may not be resolvable until the mesh is warm, and it can change. When no vision model
/// is available — or the call fails — <see cref="Describe"/> emits an EMPTY string, so the file simply
/// indexes as no-text (exactly the behavior before a describer was wired) instead of failing the
/// indexing activity.</para>
/// </summary>
public sealed class ChatClientImageDescriber : IImageDescriber
{
    private static readonly ImmutableHashSet<string> Extensions = ImmutableHashSet.Create(
        StringComparer.OrdinalIgnoreCase, ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tiff", ".tif");

    private static readonly ImmutableDictionary<string, string> MediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".bmp"] = "image/bmp",
            [".tiff"] = "image/tiff",
            [".tif"] = "image/tiff"
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    private readonly Func<IChatClient?> chatClientFactory;
    private readonly IIoPool httpPool;
    private readonly ILogger<ChatClientImageDescriber> logger;

    /// <summary>The image extensions this describer handles.</summary>
    public IReadOnlyCollection<string> SupportedExtensions => Extensions;

    /// <summary>
    /// Creates the describer. <paramref name="chatClientFactory"/> supplies a multimodal chat client
    /// on demand (may return <c>null</c> when no default model resolves); the model round-trip runs on
    /// the shared <see cref="IoPoolNames.Http"/> outbound-network gate.
    /// </summary>
    public ChatClientImageDescriber(
        Func<IChatClient?> chatClientFactory,
        IoPoolRegistry ioPoolRegistry,
        ILogger<ChatClientImageDescriber>? logger = null)
    {
        this.chatClientFactory = chatClientFactory ?? throw new ArgumentNullException(nameof(chatClientFactory));
        httpPool = (ioPoolRegistry ?? throw new ArgumentNullException(nameof(ioPoolRegistry))).Get(IoPoolNames.Http);
        this.logger = logger ?? NullLogger<ChatClientImageDescriber>.Instance;
    }

    /// <inheritdoc />
    public IObservable<string> Describe(byte[] imageBytes, string fileName)
    {
        var ext = System.IO.Path.GetExtension(fileName).ToLowerInvariant();
        var mediaType = MediaTypes.GetValueOrDefault(ext, "image/png");

        // The multimodal round-trip is the single IIoPool leaf — cold, bounded, off-scheduler.
        return httpPool.Invoke(async ct =>
        {
            IChatClient? client;
            try
            {
                client = chatClientFactory();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Resolving a vision chat client failed for image '{FileName}'; indexing it without a description.",
                    fileName);
                return string.Empty;
            }

            if (client is null)
            {
                // Debug, not Information: with no vision model configured this fires for EVERY image.
                logger.LogDebug(
                    "No vision chat client available; image '{FileName}' indexed without a description.", fileName);
                return string.Empty;
            }

            var message = new ChatMessage(ChatRole.User, new List<AIContent>
            {
                new TextContent(BuildPrompt(fileName)),
                new DataContent(imageBytes, mediaType)
            });

            try
            {
                var response = await client.GetResponseAsync([message], options: null, ct).ConfigureAwait(false);
                return response.Text ?? string.Empty;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Vision describe failed for image '{FileName}'; indexing it without a description.", fileName);
                return string.Empty;
            }
        });
    }

    /// <summary>Builds the vision prompt. <paramref name="fileName"/> is a name/format hint.</summary>
    internal static string BuildPrompt(string fileName) =>
        $"""
         Describe this image in 2-4 concise sentences for use as searchable alt-text. Capture the
         subject, any visible text, and notable details. Write plain prose — no preamble, no markdown.

         File name: {fileName}
         """;
}
