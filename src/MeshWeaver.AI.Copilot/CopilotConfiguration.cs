namespace MeshWeaver.AI.Copilot;

/// <summary>
/// Configuration for GitHub Copilot SDK integration.
/// </summary>
public class CopilotConfiguration
{
    /// <summary>
    /// Optional path to the Copilot CLI executable.
    /// If not specified, the CLI is expected to be in PATH.
    /// </summary>
    public string? CliPath { get; set; }

    /// <summary>
    /// Optional URL to connect to an existing Copilot server.
    /// If specified, a new server will not be started.
    /// </summary>
    public string? CliUrl { get; set; }

    /// <summary>
    /// Optional port for the Copilot server.
    /// </summary>
    public int? Port { get; set; }

    /// <summary>
    /// Available models through Copilot.
    /// </summary>
    public string[] Models { get; set; } = [];

    /// <summary>
    /// Display order in model dropdown (lower = first).
    /// </summary>
    public int DisplayOrder { get; set; } = 10;

    /// <summary>
    /// Whether to enable streaming responses.
    /// </summary>
    public bool EnableStreaming { get; set; } = true;

    /// <summary>
    /// Session timeout in milliseconds.
    /// </summary>
    public int SessionTimeoutMs { get; set; } = 30000;
}
