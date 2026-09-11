namespace MeshWeaver.GitSync;

/// <summary>
/// Repository-level overrides for the workflow whose green verdict GitSync may publish as a
/// build completion. Bound from <c>GitHub:ContentWorkflows</c>. The declaration belongs to a
/// repository, never to one Space: every Space targeting the same repository must trust the same
/// content evidence.
/// </summary>
public sealed record GitHubContentWorkflowOptions
{
    /// <summary>The configuration section containing <see cref="Repositories"/>.</summary>
    public const string ConfigSection = "GitHub:ContentWorkflows";

    /// <summary>Overrides for repositories whose content CI does not use the conventional
    /// <c>.github/workflows/ci.yml</c> path.</summary>
    public List<GitHubContentWorkflow> Repositories { get; } = [];
}

/// <summary>One repository and the stable Git workflow path that proves its content.</summary>
public sealed record GitHubContentWorkflow
{
    /// <summary>Repository identity as <c>owner/repo</c> or its GitHub URL.</summary>
    public string Repository { get; init; } = "";

    /// <summary>Case-sensitive Git path, for example <c>.github/workflows/content.yml</c>.</summary>
    public string Path { get; init; } = "";
}
