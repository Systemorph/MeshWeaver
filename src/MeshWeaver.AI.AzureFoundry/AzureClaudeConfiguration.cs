namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Configuration for Azure AI Foundry Claude/Anthropic services
/// </summary>
public class AzureClaudeConfiguration
{
    /// <summary>
    /// The Azure AI Foundry endpoint URL for Claude.
    /// Format: https://resource.services.ai.azure.com/anthropic/v1/messages
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The API Key
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Available Claude models (e.g., claude-sonnet-4-20250514, claude-haiku-4-5)
    /// </summary>
    public string[] Models { get; set; } = [];

    /// <summary>
    /// Display order in model dropdown (lower = first)
    /// </summary>
    public int DisplayOrder { get; set; } = 0;
}
