namespace Aspire.Hosting;

/// <summary>
/// Options for <see cref="MemexHostingExtensions.AddMemex"/>. Sensible defaults give a
/// single-node, Azure-free self-host out of the box; override for HA, Azure, or to gate
/// AI capabilities. Every value maps 1:1 to a portal config key, so the same surface flows
/// through Docker Compose <c>.env</c>, Kubernetes config, and ACA / ARM container env.
/// </summary>
public sealed class MemexOptions
{
    /// <summary>Container registry + namespace holding the Memex images. Default GHCR / Systemorph.</summary>
    public string ImageRegistry { get; set; } = "ghcr.io/systemorph";

    /// <summary>Image tag applied to all Memex images (portal, migration). Default <c>latest</c>.</summary>
    public string ImageTag { get; set; } = "latest";

    /// <summary>
    /// Use the <c>memex-portal-ai</c> image (co-hosted Claude Code + GitHub Copilot CLIs baked in)
    /// rather than the lean <c>memex-portal</c>. Default <c>true</c>. The runtime
    /// <see cref="Anthropic"/>/<see cref="ClaudeCode"/>/<see cref="Copilot"/> flags still gate
    /// whether those providers are actually registered.
    /// </summary>
    public bool IncludeAiClis { get; set; } = true;

    /// <summary>
    /// Object-storage / NodeType cache / NuGet cache / DataProtection backend:
    /// <c>Filesystem</c> (default, mounted volumes) or <c>Azure</c> (blob). Mesh data always lives in Postgres.
    /// </summary>
    public string Backend { get; set; } = "Filesystem";

    /// <summary>
    /// Orleans clustering: <c>Localhost</c> (single node, default), <c>AdoNet</c> (HA, Postgres-backed),
    /// or <c>AzureTables</c>.
    /// </summary>
    public string OrleansClustering { get; set; } = "Localhost";

    /// <summary>Encryption master key for provider credentials (<c>Ai:KeyProtection:MasterKey</c>). Required for production.</summary>
    public string? MasterKey { get; set; }

    /// <summary>The portal's externally reachable base URL; the co-hosted CLIs connect back to <c>{BaseUrl}/mcp</c>. Defaults to the portal's own endpoint.</summary>
    public string? BaseUrl { get; set; }

    // Deploy-time capability flags. null = leave the portal default (on); set false to disable explicitly.
    public bool? Anthropic { get; set; }
    public bool? AzureFoundry { get; set; }
    public bool? AzureOpenAI { get; set; }
    public bool? OpenAI { get; set; }
    public bool? ClaudeCode { get; set; }
    public bool? Copilot { get; set; }

    internal IEnumerable<KeyValuePair<string, string>> FeatureEnvironment()
    {
        if (Anthropic is { } an) yield return new("Features__Ai__Providers__Anthropic", an ? "true" : "false");
        if (AzureFoundry is { } af) yield return new("Features__Ai__Providers__AzureFoundry", af ? "true" : "false");
        if (AzureOpenAI is { } ao) yield return new("Features__Ai__Providers__AzureOpenAI", ao ? "true" : "false");
        if (OpenAI is { } op) yield return new("Features__Ai__Providers__OpenAI", op ? "true" : "false");
        if (ClaudeCode is { } cc) yield return new("Features__Ai__Clis__ClaudeCode", cc ? "true" : "false");
        if (Copilot is { } co) yield return new("Features__Ai__Clis__Copilot", co ? "true" : "false");
    }
}
