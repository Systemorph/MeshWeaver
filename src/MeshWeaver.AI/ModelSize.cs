namespace MeshWeaver.AI;

/// <summary>
/// Abstract SIZE of a language model — the label an agent asks for instead of naming a concrete
/// model id. The label lives on the model NODE (<see cref="ModelDefinition.Size"/>), so a
/// deployment expresses "which model is our large one" as data rather than as
/// <c>ModelTier:*</c> configuration keys that every new environment has to get right.
///
/// <para>Sizes are a preference, never a requirement: <b>an environment does not have to populate
/// all of them.</b> A label with no model behind it falls through to the deployment default
/// (see <c>ChatClientCredentialResolver.ResolveDefaultModelId</c>), so labelling a single model is
/// a valid — and common — setup.</para>
/// </summary>
public enum ModelSize
{
    /// <summary>Cheapest and fastest. Classification, titles, summaries, routing decisions —
    /// latency and cost dominate, and the work does not involve code.</summary>
    S = 0,

    /// <summary>Fast general-purpose. Ordinary chat and agent rounds; must still be able to write
    /// correct code, or it does not belong at this label.</summary>
    M = 1,

    /// <summary>The capable default. Complex reasoning, multi-step agent work, code that matters.</summary>
    L = 2,

    /// <summary>The strongest available. Reserved for the hardest asks — slowest and most expensive,
    /// so it is chosen deliberately, never as a fallback.</summary>
    XL = 3,
}

/// <summary>
/// Parsing for <see cref="ModelSize"/>, including the legacy tier vocabulary
/// (<c>utility</c> / <c>light</c> / <c>standard</c> / <c>heavy</c>) that
/// <see cref="AgentConfiguration.ModelTier"/> and the <c>ModelTier:*</c> config section speak.
/// Both spellings resolve to the same label so existing agents keep working unchanged.
/// </summary>
public static class ModelSizes
{
    /// <summary>
    /// Parses a size label or a legacy tier name. Returns <c>null</c> for null/empty/unknown input —
    /// an unrecognised label is a MISS, which falls through to the default, never an exception.
    /// </summary>
    /// <param name="value">"S"/"M"/"L"/"XL", or "utility"/"light"/"standard"/"heavy".</param>
    /// <returns>The parsed <see cref="ModelSize"/>, or <c>null</c> when it does not name one.</returns>
    public static ModelSize? Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "s" or "small" or "utility" => ModelSize.S,
        "m" or "medium" or "light" => ModelSize.M,
        "l" or "large" or "standard" => ModelSize.L,
        "xl" or "xlarge" or "heavy" => ModelSize.XL,
        _ => null,
    };
}
