namespace MeshWeaver.Hosting.PostgreSql;

/// <summary>
/// Configuration options for the Azure Foundry embedding provider.
/// Bind from "Embedding" config section.
/// </summary>
public class EmbeddingOptions
{
    public string? Endpoint { get; set; }
    public string? ApiKey { get; set; }
    public string Model { get; set; } = "cohere-embed-v-4-0";
    public int Dimensions { get; set; } = 1024;
}
