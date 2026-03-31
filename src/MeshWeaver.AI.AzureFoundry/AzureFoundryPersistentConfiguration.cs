namespace MeshWeaver.AI.AzureFoundry;

/// <summary>
/// Configuration for Azure AI Foundry persistent agent services.
/// Persistent agents maintain server-side conversation history,
/// so each message only needs to be sent once.
/// </summary>
public class AzureFoundryPersistentConfiguration
{
    /// <summary>
    /// The Azure AI Foundry project endpoint URL.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// The API key for authentication. When null, DefaultAzureCredential is used.
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

    /// <summary>
    /// Use DefaultAzureCredential instead of API key.
    /// </summary>
    public bool UseManagedIdentity { get; set; }
}
