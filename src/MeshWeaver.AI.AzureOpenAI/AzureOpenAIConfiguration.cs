namespace MeshWeaver.AI.AzureOpenAI;

/// <summary>
/// Configuration for AI service credentials
/// </summary>
public class AzureOpenAIConfiguration
{
    /// <summary>
    /// The Azure OpenAI endpoint URL
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// The API key for authentication
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Available models/deployment names
    /// </summary>
    public string[] Models { get; set; } = Array.Empty<string>();
}
