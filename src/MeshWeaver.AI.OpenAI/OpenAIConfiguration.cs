namespace MeshWeaver.AI.OpenAI;

/// <summary>
/// Configuration for direct OpenAI (<c>api.openai.com</c>) credentials —
/// bring-your-own personal OpenAI key. Distinct from
/// <see cref="AzureOpenAIConfiguration"/>, which targets an Azure-hosted
/// OpenAI deployment. Bound from the <c>OpenAI:</c> config section; per-user
/// keys override via a <c>ModelProvider</c> node (Provider = "OpenAI").
/// </summary>
public class OpenAIConfiguration
{
    /// <summary>
    /// Optional endpoint override. <c>null</c> uses the SDK default
    /// (<c>https://api.openai.com</c>). Set for an OpenAI-compatible gateway.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>The OpenAI API key (<c>sk-...</c>).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Available model ids (e.g. <c>gpt-4o</c>, <c>gpt-4o-mini</c>).</summary>
    public string[] Models { get; set; } = Array.Empty<string>();

    /// <summary>Display order in the model dropdown (lower = first).</summary>
    public int Order { get; set; } = 0;
}
