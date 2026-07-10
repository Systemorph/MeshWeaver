#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

// Assertions (.Should()) come from the in-house MeshWeaver.Reactive.Assertions,
// wired as a global using in test/Directory.Build.props — no per-file import.
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins <see cref="ModelProviderIcons"/>: every AI model resolves to its maker's brand logo
/// (so the picker no longer shows an identical sparkle for every model), the model id wins over
/// a gateway provider name (a <c>claude-*</c> served through AzureFoundry still reads as Anthropic),
/// and an unknown model/provider returns <c>null</c> so the caller falls back to the neutral icon.
/// </summary>
public class ModelProviderIconsTest
{
    [Theory]
    [InlineData("claude-sonnet-4-20250514", "Anthropic", "anthropic")]
    [InlineData("gpt-4o-mini", "OpenAI", "openai")]
    [InlineData("chatgpt-4o-latest", "OpenAI", "openai")]
    [InlineData("gemini-2.5-pro", "Google", "google")]
    [InlineData("gemma-3-27b", "Google", "google")]
    [InlineData("mistral-large-latest", "Mistral", "mistral")]
    [InlineData("mixtral-8x7b", "Mistral", "mistral")]
    [InlineData("codestral-latest", "Mistral", "mistral")]
    [InlineData("llama-3.3-70b", "Meta", "meta")]
    [InlineData("deepseek-chat", "DeepSeek", "deepseek")]
    [InlineData("grok-4", "xAI", "xai")]
    [InlineData("qwen-2.5-72b", "Qwen", "qwen")]
    [InlineData("sonar-pro", "Perplexity", "perplexity")]
    public void ModelId_resolves_to_its_brand(string modelId, string provider, string expectedIcon)
        => ModelProviderIcons.ForModel(provider, modelId)
            .Should().Be($"/static/NodeTypeIcons/{expectedIcon}.svg");

    [Fact]
    public void ModelId_wins_over_a_gateway_provider_name()
        // AzureFoundry / OpenRouter serve many makers — the model id must decide the logo.
        => ModelProviderIcons.ForModel("AzureFoundry", "claude-sonnet-4-20250514")
            .Should().Be("/static/NodeTypeIcons/anthropic.svg");

    [Fact]
    public void OpenRouter_prefixed_ids_resolve_to_the_maker()
        => ModelProviderIcons.ForModel("OpenRouter", "meta-llama/llama-3.3-70b-instruct")
            .Should().Be("/static/NodeTypeIcons/meta.svg");

    [Theory]
    [InlineData("Anthropic", "anthropic")]
    [InlineData("OpenAI", "openai")]
    [InlineData("AzureOpenAI", "openai")]   // OpenAI models hosted on Azure → the OpenAI mark
    [InlineData("AzureFoundry", "azure")]   // generic Azure gateway → the Azure mark
    [InlineData("GitHubCopilot", "githubcopilot")]
    public void Provider_name_resolves_when_no_model_id(string provider, string expectedIcon)
        => ModelProviderIcons.ForProvider(provider)
            .Should().Be($"/static/NodeTypeIcons/{expectedIcon}.svg");

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("SomeCustomProvider", "totally-unknown-model")]
    public void Unknown_returns_null_for_neutral_fallback(string? provider, string? modelId)
        => ModelProviderIcons.ForModel(provider, modelId).Should().BeNull();
}
