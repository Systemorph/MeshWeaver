using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Mesh.Features;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.GitSync;

/// <summary>
/// The package a sync source imports, and the flag that declared it retired.
/// </summary>
/// <param name="Flag">The feature flag declared off (<c>Features:Flags:{Flag}:Enabled=false</c>).</param>
/// <param name="Pattern">The flag's package pattern that covers the source, verbatim
/// (<c>Plugins/Hosting</c>, <c>Plugins/*</c>).</param>
/// <param name="SourceName">The package source the repository is configured as.</param>
/// <param name="PackageId">The package id the source was matched as.</param>
internal sealed record RetiredSyncSource(string Flag, string Pattern, string SourceName, string PackageId);

/// <summary>
/// 🚨 <b>A Space whose package the instance declares RETIRED is not imported</b> (MeshWeaver#8020;
/// <c>Doc/Architecture/GitHubSync</c> → "A retired package is not synced").
///
/// <para>A feature flag declared off with an explicit <c>Enabled=false</c> retires the packages it
/// names (<see cref="FeatureComposition.Retired"/>, policy <c>declared-not-derived</c>;
/// <c>Doc/Architecture/EnvironmentComposition</c> → "A disabled flag retires what is installed").
/// Retiring the package does not remove its Space or its <c>_GitSync</c> source — the data is
/// retained, and an uninstall may be refused outright (a partition above the uninstall's
/// verification bound). Before this gate GitSync therefore kept IMPORTING the retired package on
/// every push of its repository: measured on memex-cloud, <c>Hosting</c> grew 26,436 → 26,576 nodes
/// in 7.6 h while its record declared <c>features.fleetops: {enabled:false, packages:[Plugins/Hosting,
/// Plugins/BuildServer]}</c>.</para>
///
/// <para><b>Which package a source IS.</b> The package source is the configured one that names the
/// source's repository — <c>PluginCatalog:Sources:N</c> (its <c>Name</c>, else its <c>RepoPath</c>),
/// the legacy single source (<c>registry</c>) when that list names none, or an instance-local source
/// (<see cref="InstancePackageSources.NameOf"/>) — the same naming install records use. The package
/// id is the Space (the partition a package is installed into) or the last segment of the source's
/// subdirectory (the folder the package lives in, which an install whose target partition differs
/// from its id still names). A repository no source names matches nothing: a source-scoped pattern
/// fails closed, as every other flag lane does.</para>
///
/// <para><b>Read live.</b> The flags are read on every decision (<see cref="IFeatureFlags.Composition"/>
/// replays the current value and re-emits on a configuration reload), so a flag turned back on
/// resumes the sync on the next push or reconcile pass — nothing to recycle.</para>
///
/// <para><b>Said once per transition.</b> One Information line names the flag when a source is first
/// found retired, and one when it resumes; an unchanged state writes nothing. The memory of what was
/// said is an instance field on a mesh-scoped singleton, never static.</para>
/// </summary>
internal sealed class RetiredPackageSyncGate(
    IFeatureFlags? flags = null,
    IConfiguration? configuration = null,
    InstancePackageSourceCatalog? instanceSources = null,
    ILogger<RetiredPackageSyncGate>? logger = null)
{
    /// <summary>The name the legacy single configured source is given (<c>PackageSources.FromConfiguration</c>).</summary>
    internal const string LegacySourceName = "registry";

    /// <summary>Config path → the flag last announced as retiring it.</summary>
    private readonly ConcurrentDictionary<string, string> announced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The retired patterns as the environment declares them NOW, as a classifier over one sync
    /// source: <c>(configPath, config) → the retirement, or null</c>. Never faults: an unreadable
    /// flag surface retires nothing (an absent contributor is never a removal).
    /// </summary>
    public IObservable<Func<string, GitHubSyncConfig?, RetiredSyncSource?>> Classifier()
        => (flags?.Composition.Take(1) ?? Observable.Return(FeatureComposition.Empty))
            .DefaultIfEmpty(FeatureComposition.Empty)
            .Catch((Exception ex) =>
            {
                logger?.LogWarning(ex,
                    "[GitSync] the feature flags could not be read — no sync source is treated as retired on this reading.");
                return Observable.Return(FeatureComposition.Empty);
            })
            .Select(composition =>
            {
                var retired = composition.Retired;
                return (Func<string, GitHubSyncConfig?, RetiredSyncSource?>)((configPath, config) =>
                {
                    var hit = retired.Count == 0 || config is null
                        ? null
                        : CoveredBy(SpaceOf(configPath), IsPrimary(configPath), config, retired, SourceNameOf);
                    Announce(configPath, hit);
                    return hit;
                });
            });

    /// <summary>
    /// THE rule: the declared-off pattern covering the package a sync source imports, or null. Pure.
    ///
    /// <para>🚨 <b>The package is the SOURCE's, never merely the Space's</b> (review on #8053). The
    /// source must be read from a repository a package source names, and its package id is the last
    /// folder of its subdirectory. The Space's own name counts as the package id only for the Space's
    /// PRIMARY source (<c>{space}/_GitSync</c>) — the one a package install writes, whose Space is the
    /// package's target partition. An ADDITIONAL source of the same Space
    /// (<c>{space}/_GitSync/{sourceId}</c>) mirrors some other folder or repository, and retiring the
    /// Space's package says nothing about it, so it keeps syncing.</para>
    /// </summary>
    /// <param name="space">The Space the source imports into.</param>
    /// <param name="primary">Whether this is the Space's primary source (<c>{space}/_GitSync</c>).</param>
    /// <param name="config">The source.</param>
    /// <param name="retired">The declared-off patterns (<see cref="FeatureComposition.Retired"/>).</param>
    /// <param name="sourceNameOf">Repository URL → the package source name it is configured as, or null.</param>
    internal static RetiredSyncSource? CoveredBy(
        string space, bool primary, GitHubSyncConfig config, IReadOnlyList<FeaturePackage> retired,
        Func<string?, string?> sourceNameOf)
    {
        if (retired.Count == 0 || sourceNameOf(config.RepositoryUrl) is not { Length: > 0 } sourceName)
            return null;
        var candidates = new List<string>();
        if (LastSegment(config.Subdirectory) is { } folder)
            candidates.Add(folder);
        if (primary && !string.IsNullOrWhiteSpace(space) && !candidates.Contains(space, StringComparer.Ordinal))
            candidates.Add(space);
        foreach (var pattern in retired)
        {
            if (PluginGrantEntry.TryParse(pattern.Package) is not { } entry)
                continue;
            foreach (var packageId in candidates)
                if (entry.Matches(sourceName, packageId))
                    return new RetiredSyncSource(pattern.Flag, pattern.Package, sourceName, packageId);
        }
        return null;
    }

    /// <summary>
    /// The package source name a repository is configured as — the configured list first (its
    /// <c>Name</c>, else its <c>RepoPath</c>), the legacy single source when that list names no
    /// repository, then an instance-local source — or null when no source names it.
    /// </summary>
    internal string? SourceNameOf(string? repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
            return null;
        if (configuration is not null)
        {
            var listed = configuration.GetSection(GitSourceCredentials.SourcesSection).GetChildren()
                .Select(s => (RepoPath: s["RepoPath"]?.Trim(), Name: s["Name"]?.Trim()))
                .Where(s => s.RepoPath is { Length: > 0 })
                .ToList();
            foreach (var (repoPath, name) in listed)
                if (repoPath is { Length: > 0 } path && SameRepository(path, repositoryUrl))
                    return name is { Length: > 0 } declared ? declared : path;
            if (listed.Count == 0
                && SameRepository(configuration[GitSourceCredentials.LegacySourceRepoPathKey], repositoryUrl))
                return LegacySourceName;
        }
        return instanceSources?.For(repositoryUrl) is { } local ? InstancePackageSources.NameOf(local) : null;
    }

    /// <summary>Whether two spellings name one repository: equal once normalised, or the same
    /// owner/repo once parsed.</summary>
    internal static bool SameRepository(string? a, string? b)
    {
        if (GitSourceCredentials.NormalizeRepository(a) is not { } left
            || GitSourceCredentials.NormalizeRepository(b) is not { } right)
            return false;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            return true;
        return GitHubRepoIdentityResolver.Parse(left) is { } x
               && GitHubRepoIdentityResolver.Parse(right) is { } y
               && string.Equals(x.ToString(), y.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether a config path is a Space's PRIMARY source — it ENDS in <c>_GitSync</c>.</summary>
    internal static bool IsPrimary(string configPath)
        => configPath.Split('/') is { Length: > 1 } parts
           && string.Equals(parts[^1], GitHubSyncService.ConfigId, StringComparison.Ordinal);

    /// <summary>The census phrase a skipped source is named with — the same words in every trigger's
    /// census line, so a reader can grep one sentence across the push, reconcile and seal passes.</summary>
    internal static string SkipPhrase(RetiredSyncSource retired)
        => $"package {retired.SourceName}/{retired.PackageId} retired by feature flag '{retired.Flag}'";

    /// <summary>The Space a config path (<c>{space}/_GitSync[/{sourceId}]</c>) configures.</summary>
    private static string SpaceOf(string configPath)
    {
        var parts = configPath.Split('/');
        var idx = Array.IndexOf(parts, GitHubSyncService.ConfigId);
        return idx <= 0 ? parts[0] : string.Join('/', parts[..idx]);
    }

    private static string? LastSegment(string? subdirectory)
        => (subdirectory ?? "").Trim().Trim('/') is { Length: > 0 } trimmed ? trimmed.Split('/')[^1] : null;

    /// <summary>One Information line per transition — retired (naming the flag) and resumed.</summary>
    private void Announce(string configPath, RetiredSyncSource? hit)
    {
        if (hit is not null)
        {
            if (announced.TryGetValue(configPath, out var flag) && string.Equals(flag, hit.Flag, StringComparison.OrdinalIgnoreCase))
                return;
            announced[configPath] = hit.Flag;
            logger?.LogInformation(
                "[GitSync] {Config} is not synced: its package {Source}/{Package} is retired by feature flag "
                + "'{Flag}' (Features:Flags:{Flag}:Enabled=false, pattern '{Pattern}'). Nothing is fetched or "
                + "imported for it while the flag is off; turning the flag on resumes it on the next push or "
                + "reconcile pass.",
                configPath, hit.SourceName, hit.PackageId, hit.Flag, hit.Flag, hit.Pattern);
            return;
        }
        if (announced.TryRemove(configPath, out var previous))
            logger?.LogInformation(
                "[GitSync] {Config} syncs again: feature flag '{Flag}' no longer retires its package.",
                configPath, previous);
    }
}
