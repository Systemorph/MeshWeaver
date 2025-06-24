namespace MeshWeaver.AI.Bedrock;

/// <summary>
/// Configuration for AWS Bedrock service credentials and settings
/// </summary>
public class BedrockConfiguration
{
    /// <summary>
    /// The AWS region where Bedrock service is available
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Available Bedrock model IDs (e.g., "anthropic.claude-3-sonnet-20240229-v1:0")
    /// </summary>
    public string[] Models { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Access key to access AWS Bedrock service
    /// </summary>
    public string? AccessKey { get; set; }

    /// <summary>
    /// Secret access key to access AWS Bedrock service
    /// </summary>
    public string? SecretAccessKey { get; set; }
}
