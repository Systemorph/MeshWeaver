using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshWeaver.AI;

/// <summary>
/// Resolves a BARE <see cref="IChatClient"/> for the mesh's DEFAULT language model — the lowest-Order
/// <c>LanguageModel</c> whose credentials resolve — WITHOUT an agent and without mutating any shared
/// factory state. It is the headless counterpart to the chat composer's model selection: background
/// jobs that need a one-shot model call (e.g. the content-indexing image describer) resolve their
/// client here instead of standing up an agent.
///
/// <para>Client selection mirrors <c>AgentChatClient.GetFactoryForModel</c>: the default model id comes
/// from <see cref="ChatClientCredentialResolver.ResolveDefaultModelId"/>, and the serving factory is the
/// lowest-Order non-persistent <see cref="IChatClientFactory"/> that <see cref="IChatClientFactory.Supports"/>
/// it. Returns <c>null</c> (never throws) when no model is configured or none resolves — callers treat a
/// null client as "no model available" and degrade gracefully.</para>
/// </summary>
public sealed class DefaultChatClientProvider
{
    private readonly IReadOnlyList<IChatClientFactory> factories;
    private readonly ChatClientCredentialResolver resolver;
    private readonly ILogger<DefaultChatClientProvider> logger;

    /// <summary>Initializes the provider with every registered chat-client factory and the credential resolver.</summary>
    public DefaultChatClientProvider(
        IEnumerable<IChatClientFactory> factories,
        ChatClientCredentialResolver resolver,
        ILogger<DefaultChatClientProvider>? logger = null)
    {
        this.factories = (factories ?? throw new ArgumentNullException(nameof(factories))).ToList();
        this.resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        this.logger = logger ?? NullLogger<DefaultChatClientProvider>.Instance;
    }

    /// <summary>
    /// Creates a bare <see cref="IChatClient"/> for the default resolvable model, or <c>null</c> when
    /// no model is configured / none resolves / the serving factory can't build a bare client. Never throws.
    /// </summary>
    public IChatClient? TryCreate()
    {
        var modelId = resolver.ResolveDefaultModelId();
        if (string.IsNullOrEmpty(modelId))
        {
            logger.LogDebug("No default model resolves; returning null chat client.");
            return null;
        }

        // Only a factory that actually SUPPORTS the model — never an arbitrary fallback, which would
        // route the id to an incompatible provider. When none supports it, return null so the caller
        // degrades to no-description rather than calling the wrong provider.
        var factory = factories
            .Where(f => !f.IsPersistent)
            .OrderBy(f => f.Order)
            .FirstOrDefault(f => f.Supports(modelId));
        if (factory is null)
        {
            logger.LogDebug("No non-persistent chat-client factory supports default model {ModelId}.", modelId);
            return null;
        }

        try
        {
            return factory.CreateChatClient(modelId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to create a bare chat client for default model {ModelId} via factory {Factory}.",
                modelId, factory.Name);
            return null;
        }
    }
}
