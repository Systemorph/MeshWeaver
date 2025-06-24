namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Configuration for Azure AI Foundry services
/// </summary>
public class AzureAIFoundryConfiguration
{
    /// <summary>
    /// The Azure AI Foundry project endpoint URL
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The API Key
    /// </summary>
    public string? ApiKey { get; set; }


    /// <summary>
    /// Available models for different purposes
    /// </summary>
    public string[] Models { get; set; } = Array.Empty<string>();

}
