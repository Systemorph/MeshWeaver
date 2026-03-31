namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Configuration for Azure AI Foundry services (supports OpenAI, Claude, and other models)
/// </summary>
public class AzureFoundryConfiguration
{
    /// <summary>
    /// The Azure AI Foundry endpoint URL.
    /// Format: https://resource.services.ai.azure.com/models
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The API Key
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Available models (e.g., gpt-4o, gpt-4o-mini)
    /// </summary>
    public string[] Models { get; set; } = [];

    /// <summary>
    /// Display order in model dropdown (lower = first)
    /// </summary>
    public int Order { get; set; } = 0;
}
