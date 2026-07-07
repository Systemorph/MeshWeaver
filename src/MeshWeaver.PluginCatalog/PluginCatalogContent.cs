namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The content of a <c>PluginCatalog</c> node — points the catalog browse view at a source git
/// repository and ref. The catalog lists that repo's installable folders (folders carrying a
/// <c>package.json</c>) at <see cref="SourceRef"/> and offers Install / Update per package.
/// </summary>
public record PluginCatalogContent
{
    /// <summary>Local path to the source git repository (the plugins repo checkout). A GitHub-URL
    /// source can be added behind the same catalog later.</summary>
    public string? SourceRepoPath { get; init; }

    /// <summary>The git ref (commit SHA or branch) to browse/install from. Defaults to <c>HEAD</c>.</summary>
    public string SourceRef { get; init; } = "HEAD";

    /// <summary>Optional subdirectory within the repo that holds the package folders (e.g.
    /// <c>"catalog"</c>). When empty, package folders are read from the repo root.</summary>
    public string? SourceSubdir { get; init; }

    /// <summary>Optional markdown intro shown above the package list.</summary>
    public string? Description { get; init; }
}
