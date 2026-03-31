namespace MeshWeaver.AI.AzureOpenAI;

/// <summary>
/// Configuration for AI service credentials
/// </summary>
public class AzureOpenAIConfiguration
{
    /// <summary>
    /// The Azure OpenAI endpoint URL
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The API key for authentication
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Available models/deployment names
    /// </summary>
    public string[] Models { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Display order in model dropdown (lower = first)
    /// </summary>
    public int Order { get; set; } = 0;
}
