using System.Collections.Immutable;

namespace MeshWeaver.AI;

/// <summary>
/// Resolves a brand icon for an AI model or model provider so the picker shows a
/// recognizable logo instead of the generic sparkle. Icons are the self-contained
/// coloured-tile SVGs shipped under <c>src/MeshWeaver.Graph/Icons</c> (served at
/// <c>/static/NodeTypeIcons/*.svg</c>).
///
/// <para>The brand is inferred by substring match against the MODEL ID first, then
/// the provider name — so a gateway provider (AzureFoundry, OpenRouter) that serves
/// many brands still resolves each model to its true maker (a <c>claude-*</c> served
/// through Azure gets the Anthropic mark). An unknown model/provider returns
/// <c>null</c> so the caller can fall back to the neutral sparkle.</para>
/// </summary>
public static class ModelProviderIcons
{
    private const string Base = "/static/NodeTypeIcons/";

    /// <summary>
    /// Brand tokens, checked in order via <see cref="string.Contains(string)"/> against a
    /// lower-cased id/name. Order matters only where several tokens could match the same
    /// string: the first match wins, so brand-specific tokens (<c>mixtral</c>, <c>codestral</c>,
    /// <c>llama</c>) precede broader ones (<c>mistral</c>, <c>meta</c>). Each icon file exists
    /// under <c>MeshWeaver.Graph/Icons</c>.
    /// </summary>
    private static readonly ImmutableArray<(string Token, string Icon)> Brands =
    [
        ("claude", "anthropic"),
        ("anthropic", "anthropic"),
        ("gemini", "google"),
        ("gemma", "google"),
        ("deepseek", "deepseek"),
        ("mixtral", "mistral"),
        ("codestral", "mistral"),
        ("ministral", "mistral"),
        ("magistral", "mistral"),
        ("devstral", "mistral"),
        ("mistral", "mistral"),
        ("llama", "meta"),
        ("grok", "xai"),
        ("qwen", "qwen"),
        ("qwq", "qwen"),
        ("copilot", "githubcopilot"),
        ("openrouter", "openrouter"),
        ("perplexity", "perplexity"),
        ("sonar", "perplexity"),
        ("ollama", "ollama"),
        ("gpt", "openai"),
        ("chatgpt", "openai"),
        ("davinci", "openai"),
        ("dall-e", "openai"),
        ("openai", "openai"),
        ("meta", "meta"),
        ("xai", "xai"),
        ("azure", "azure"),
        ("microsoft", "azure"),
    ];

    /// <summary>
    /// Resolves the icon path for a model, preferring the brand implied by
    /// <paramref name="modelId"/> and falling back to <paramref name="provider"/>.
    /// Returns <c>null</c> when no brand matches.
    /// </summary>
    public static string? ForModel(string? provider, string? modelId)
    {
        var icon = Match(modelId) ?? Match(provider);
        return icon is null ? null : Base + icon + ".svg";
    }

    /// <summary>
    /// Resolves the icon path for a provider by its name. Returns <c>null</c> when no
    /// brand matches.
    /// </summary>
    public static string? ForProvider(string? provider)
    {
        var icon = Match(provider);
        return icon is null ? null : Base + icon + ".svg";
    }

    private static string? Match(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var v = value.ToLowerInvariant();
        foreach (var (token, icon) in Brands)
            if (v.Contains(token))
                return icon;
        return null;
    }
}
