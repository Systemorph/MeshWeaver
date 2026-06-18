namespace MeshWeaver.AI;

/// <summary>
/// Content shape for <c>nodeType:Model</c> mesh nodes — the
/// bring-your-own-model surface. Mirrors the role
/// <see cref="AgentConfiguration"/> plays for <c>nodeType:Agent</c>: a
/// mesh node carries everything <see cref="IChatClientFactory"/> needs to
/// instantiate a chat client (endpoint, model id, auth reference). Discovery
/// happens via the same workspace synced query that loads agents
/// (<c>nodeType:Agent|Model</c>).
///
/// <para>Auth is handled by an <see cref="ApiKeySecretRef"/> — a path or
/// key into a secret store rather than the literal credential — so a Model
/// node is safe to read with the same RLS that gates other content. The
/// factory selected by <see cref="Provider"/> is responsible for
/// resolving the secret at request time.</para>
/// </summary>
public record ModelDefinition
{
    /// <summary>
    /// Stable model identifier — what the underlying API expects in the
    /// <c>model</c> field of a chat-completions request. Must match the
    /// value the chosen factory accepts (e.g. <c>gpt-4o-mini</c>,
    /// <c>claude-sonnet-4-20250514</c>). Used as the dictionary key in the
    /// chat client and as the value of <c>currentModelName</c>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Human-readable name shown in the model picker. Defaults to
    /// <see cref="Id"/> via <see cref="ToModelInfo(int)"/> when not set.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Factory category — picks which <see cref="IChatClientFactory"/>
    /// instance handles the model. Free-form string matched against
    /// <see cref="IChatClientFactory.Name"/> /
    /// <see cref="IChatClientFactory.Supports"/>; common values:
    /// <c>OpenAI</c>, <c>AzureOpenAI</c>, <c>Anthropic</c>,
    /// <c>AzureFoundry</c>, <c>GitHubCopilot</c>.
    /// </summary>
    public required string Provider { get; init; }

    /// <summary>
    /// Optional endpoint override — for self-hosted / OpenAI-compatible
    /// gateways. Null means "use the factory's default endpoint".
    /// </summary>
    public string? Endpoint { get; init; }

    /// <summary>
    /// Reference to the API key in the host's secret store (e.g. a config
    /// path like <c>OpenAI:ApiKey</c>, an Azure Key Vault secret name, or
    /// an environment variable). The literal credential is never stored
    /// in the node content — only the lookup key.
    /// </summary>
    public string? ApiKeySecretRef { get; init; }

    /// <summary>
    /// Path of the <c>nodeType:ModelProvider</c> node that owns this
    /// model's credentials — e.g. <c>Model/Anthropic</c> for built-in
    /// catalog entries, or <c>{userId}/Model/Anthropic</c> for
    /// user-authored BYO models. The chat-client factory's
    /// <see cref="ChatClientCredentialResolver"/> follows this reference
    /// to read <see cref="ModelProviderConfiguration.ApiKey"/> /
    /// <see cref="ModelProviderConfiguration.Endpoint"/>. Null on legacy
    /// catalog rollouts that stamp <see cref="ApiKeySecretRef"/> /
    /// <see cref="Endpoint"/> directly on the model node.
    /// </summary>
    public string? ProviderRef { get; init; }

    /// <summary>
    /// Optional description shown in the picker.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Display order in the picker. Lower sorts first; falls back to
    /// alphabetical within the same order.
    /// </summary>
    public int Order { get; init; }

    /// <summary>
    /// Price charged per ONE MILLION input (prompt) tokens, in
    /// <see cref="Currency"/>. Used by the token-cost summaries to turn a
    /// thread's / space's recorded token usage into a monetary cost
    /// (cost = tokens / 1_000_000 × price). Null means "unknown / not priced"
    /// — the summaries then fall back to <see cref="ModelPricing"/> defaults,
    /// and show the tokens without a cost if neither is set.
    /// </summary>
    [System.ComponentModel.Description("Input price per 1M tokens")]
    public decimal? InputPricePerMillionTokens { get; init; }

    /// <summary>
    /// Price charged per ONE MILLION output (completion) tokens, in
    /// <see cref="Currency"/>. See <see cref="InputPricePerMillionTokens"/>.
    /// </summary>
    [System.ComponentModel.Description("Output price per 1M tokens")]
    public decimal? OutputPricePerMillionTokens { get; init; }

    /// <summary>
    /// ISO currency code the per-million prices are denominated in (e.g.
    /// <c>USD</c>, <c>EUR</c>, <c>CHF</c>). Null defaults to <c>USD</c> in the
    /// cost summaries.
    /// </summary>
    [System.ComponentModel.Description("Currency (e.g. USD)")]
    public string? Currency { get; init; }

    /// <summary>
    /// Projects this definition into the lighter <see cref="ModelInfo"/>
    /// shape consumed by the chat picker.
    /// </summary>
    public ModelInfo ToModelInfo(int factoryOrder = 0) => new()
    {
        Name = Id,
        Provider = Provider,
        Order = Order != 0 ? Order : factoryOrder
    };
}
