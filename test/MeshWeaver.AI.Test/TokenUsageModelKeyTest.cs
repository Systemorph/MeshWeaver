#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Pins the model-identifier normalization behind the per-model usage satellite
/// (<c>{thread}/_Usage/{modelKey}</c>).
///
/// <para><b>The defect.</b> <see cref="TokenUsageNodeType.RecordUsage"/> stored whatever identifier
/// its callers handed it — <c>actualModel ?? effectiveModel ?? request.ModelName</c> — and
/// <c>ThreadComposer.ModelName</c> is BY DESIGN a node PATH (its <c>[MeshNode]</c> picker persists
/// the catalogue node's path). So when a provider reported no serving model and no resolved model
/// was available, a PATH became the satellite's key and its stored <c>Model</c> dimension.</para>
///
/// <para>Measured on memex.systemorph.com (2026-08-26): the same DeepSeek model appeared under four
/// spellings — <c>deepseek/deepseek-v4-pro</c> (catalogue id), <c>DeepSeek-V4-Pro</c> (the provider's
/// display name), <c>DeepSeek-V3-0324</c>, and <c>_Provider/Anthropic/DeepSeek-V3-0324</c> — a path,
/// filed under the WRONG provider. 10% of all recorded input tokens keyed to identifiers no catalogue
/// lookup can price, and one model's usage split across rows.</para>
///
/// <para><b>Scope.</b> Normalization strips the registry prefix so a path keys identically to the
/// catalogue id it denotes. Reconciling a provider's DISPLAY NAME with the catalogue id is a
/// catalogue-alias lookup, deliberately out of scope here — pinned below as documented behaviour so
/// the boundary is explicit rather than accidental.</para>
/// </summary>
public class TokenUsageModelKeyTest
{
    [Fact]
    public void ARegistryPath_IsReducedToTheCatalogueId()
    {
        // The catalogue node id is everything after {Registry}/{Provider}/.
        Assert.Equal("anthropic/claude-opus-5",
            TokenUsageNodeType.NormalizeModelId("Provider/OpenRouter/anthropic/claude-opus-5"));
        Assert.Equal("auto", TokenUsageNodeType.NormalizeModelId("Provider/Auto/auto"));
    }

    [Fact]
    public void TheLegacyUnderscoreRegistry_IsReducedTheSameWay()
    {
        // The exact value observed in production.
        Assert.Equal("DeepSeek-V3-0324",
            TokenUsageNodeType.NormalizeModelId("_Provider/Anthropic/DeepSeek-V3-0324"));
    }

    [Fact]
    public void APathAndItsCatalogueId_ProduceTheSameSatelliteKey()
    {
        // The regression that matters: both spellings must accumulate onto ONE satellite.
        Assert.Equal(
            TokenUsageNodeType.SatelliteKey("anthropic/claude-opus-5"),
            TokenUsageNodeType.SatelliteKey("Provider/OpenRouter/anthropic/claude-opus-5"));
    }

    [Fact]
    public void APlainCatalogueId_IsUntouched()
    {
        Assert.Equal("anthropic/claude-opus-5",
            TokenUsageNodeType.NormalizeModelId("anthropic/claude-opus-5"));
        Assert.Equal("deepseek/deepseek-v4-pro",
            TokenUsageNodeType.NormalizeModelId("deepseek/deepseek-v4-pro"));
    }

    [Fact]
    public void AProviderDisplayName_IsLeftAlone_TheDocumentedBoundary()
    {
        // Mapping "DeepSeek-V4-Pro" onto "deepseek/deepseek-v4-pro" needs a catalogue alias lookup,
        // NOT string surgery — guessing here would merge genuinely distinct models.
        Assert.Equal("DeepSeek-V4-Pro", TokenUsageNodeType.NormalizeModelId("DeepSeek-V4-Pro"));
        Assert.Equal("gpt-5.2", TokenUsageNodeType.NormalizeModelId("gpt-5.2"));
    }

    [Fact]
    public void OnlyTheRegistryPrefix_IsStripped_NeverAnIdThatMerelyResemblesOne()
    {
        // A two-segment id is a catalogue id (vendor/model), not a path — stripping it would
        // destroy the identifier.
        Assert.Equal("providers/some-model",
            TokenUsageNodeType.NormalizeModelId("providers/some-model"));
        Assert.Equal("x-ai/grok-4.6", TokenUsageNodeType.NormalizeModelId("x-ai/grok-4.6"));
    }

    [Fact]
    public void AbsentOrBlank_StaysTheUnknownSentinel()
    {
        Assert.Equal("(unknown)", TokenUsageNodeType.NormalizeModelId(null));
        Assert.Equal("(unknown)", TokenUsageNodeType.NormalizeModelId("   "));
    }

    [Fact]
    public void SurroundingWhitespaceAndSlashes_DoNotForkTheKey()
    {
        Assert.Equal("anthropic/claude-opus-5",
            TokenUsageNodeType.NormalizeModelId("  /Provider/OpenRouter/anthropic/claude-opus-5/ "));
        Assert.Equal(
            TokenUsageNodeType.SatelliteKey("anthropic/claude-opus-5"),
            TokenUsageNodeType.SatelliteKey(" anthropic/claude-opus-5 "));
    }

    [Fact]
    public void TheKeyStaysAPathSafeSlug()
    {
        // The key is a node id — no slashes, no dots; the existing scheme maps every
        // non-alphanumeric to '_' and must keep doing so.
        Assert.Equal("anthropic_claude_opus_5",
            TokenUsageNodeType.SatelliteKey("anthropic/claude-opus-5"));
        Assert.Equal("z_ai_glm_5_3", TokenUsageNodeType.SatelliteKey("z-ai/glm-5.3"));
    }
}
