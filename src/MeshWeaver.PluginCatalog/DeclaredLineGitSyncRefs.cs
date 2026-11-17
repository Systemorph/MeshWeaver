using MeshWeaver.GitSync;
using MeshWeaver.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 GitSync's per-line branch (<see cref="IGitSyncLineRefs"/>) read from the SAME declaration the
/// registry serves each line by: the record's <c>pluginRepos[].lineRefs</c>, rendered as
/// <c>PluginCatalog:LineRefs:{source}:{major}</c> beside <c>PluginCatalog:Sources:N</c>, and this
/// instance's line as <see cref="PlatformLine.Of"/> reads it (policy <c>major-lines-run-separately</c>,
/// Doc/Architecture/PluginRegistryServesEachLine → "GitSync syncs each line's ref").
///
/// <para>Measured 2026-11-17 on memex.meshweaver.cloud (3.1.13482): every <c>_GitSync</c> of
/// MeshWeaver.Plugins and MeshWeaver.SocialMedia stored <c>branch: main</c>, the record declared
/// <c>lineRefs {"3":"main3"}</c> for every one of those repositories, the registry honoured it — and
/// GitSync imported the v4 tree from <c>main</c> anyway, because nothing on the sync side read it.</para>
///
/// <para>The sources are read through <see cref="PackageSources.FromConfiguration"/>, the one reader
/// the registry, the default install and discovery share, so GitSync can never name a different
/// repository or ref set than they do. The LINE is re-read on every call, so a roll that changes the
/// record's <c>updatePattern</c> moves every declared source to the new line's branch with no edit to
/// any <c>_GitSync</c>.</para>
/// </summary>
/// <param name="configuration">The instance configuration (the line is read from it on every call).</param>
/// <param name="sources">The configured package sources.</param>
public sealed class DeclaredLineGitSyncRefs(
    IConfiguration configuration, Func<IReadOnlyList<ConfiguredPackageSource>> sources) : IGitSyncLineRefs
{
    private readonly Lazy<IReadOnlyList<ConfiguredPackageSource>> declared = new(sources);

    /// <summary>The reader the portal registers: the configured sources of <paramref name="services"/>'s
    /// hub, read once on first use. A container with no configuration declares nothing, so every source
    /// keeps its stored branch.</summary>
    /// <param name="services">The container (its <see cref="IMessageHub"/> and <see cref="IConfiguration"/>).</param>
    public static DeclaredLineGitSyncRefs Create(IServiceProvider services)
    {
        var configuration = services.GetService<IConfiguration>() ?? new ConfigurationBuilder().Build();
        return new DeclaredLineGitSyncRefs(configuration,
            () => PackageSources.FromConfiguration(services.GetRequiredService<IMessageHub>(), configuration));
    }

    /// <inheritdoc />
    public string? LineBranch(string repositoryUrl, string storedBranch)
    {
        if (GitSourceCredentials.NormalizeRepository(repositoryUrl) is not { } repo)
            return null;
        var line = PlatformLine.Of(configuration);
        foreach (var source in declared.Value)
        {
            if (source.LineRefs.Count == 0
                || !string.Equals(GitSourceCredentials.NormalizeRepository(source.RepoPath), repo,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (GitSyncLine.Resolve(storedBranch, source.ConfiguredRef, source.LineRefs, line) is { } branch)
                return branch;
        }
        return null;
    }
}
