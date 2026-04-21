using System.Collections.Concurrent;
using System.Collections.Immutable;
using NuGet.Common;
using MsLogging = Microsoft.Extensions.Logging;
using NuGet.Configuration;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Resolver;
using NuGet.Versioning;

namespace MeshWeaver.NuGet;

/// <summary>
/// Resolves NuGet packages to assembly paths using pure NuGet client libraries.
/// No MSBuild, no dotnet CLI, no SDK — safe to run on a runtime-only container.
/// </summary>
public sealed class NuGetAssemblyResolver(
    MsLogging.ILogger<NuGetAssemblyResolver> logger,
    INuGetPackageCache? packageCache = null) : INuGetAssemblyResolver
{
    private static readonly NuGetFramework DefaultFramework = NuGetFramework.Parse("net10.0");

    private readonly ConcurrentDictionary<string, Task<ResolvedPackageSet>> _cache = new();
    private readonly SourceCacheContext _sourceCache = new();
    private readonly ISettings _settings = Settings.LoadDefaultSettings(root: null);
    private readonly NuGetLogger _nugetLogger = new(logger);
    private readonly INuGetPackageCache _packageCache = packageCache ?? NullNuGetPackageCache.Instance;

    public Task<ResolvedPackageSet> ResolveAsync(
        IReadOnlyCollection<NuGetPackageReference> requested,
        NuGetFramework? targetFramework = null,
        CancellationToken ct = default)
    {
        if (requested.Count == 0) return Task.FromResult(ResolvedPackageSet.Empty);

        var framework = targetFramework ?? DefaultFramework;
        var key = BuildCacheKey(requested, framework);
        return _cache.GetOrAdd(key, _ => ResolveCoreAsync(requested, framework, ct));
    }

    private async Task<ResolvedPackageSet> ResolveCoreAsync(
        IReadOnlyCollection<NuGetPackageReference> requested,
        NuGetFramework framework,
        CancellationToken ct)
    {
        var packagesRoot = SettingsUtility.GetGlobalPackagesFolder(_settings);
        var providers = Repository.Provider.GetCoreV3();
        var packageSourceProvider = new PackageSourceProvider(_settings);
        var sources = packageSourceProvider.LoadPackageSources()
            .Where(s => s.IsEnabled)
            .Select(s => new SourceRepository(s, providers))
            .ToArray();

        if (sources.Length == 0)
        {
            sources = [new SourceRepository(new PackageSource("https://api.nuget.org/v3/index.json"), providers)];
        }

        var available = new HashSet<SourcePackageDependencyInfo>(PackageIdentityComparer.Default);
        var targets = new List<PackageIdentity>();

        foreach (var req in requested)
        {
            var identity = await ResolveIdentityAsync(req, sources, framework, ct);
            targets.Add(identity);
            await WalkDependenciesAsync(identity, sources, framework, available, ct);
        }

        var resolverContext = new PackageResolverContext(
            dependencyBehavior: DependencyBehavior.Lowest,
            targetIds: targets.Select(t => t.Id),
            requiredPackageIds: Enumerable.Empty<string>(),
            packagesConfig: Enumerable.Empty<global::NuGet.Packaging.PackageReference>(),
            preferredVersions: targets,
            availablePackages: available,
            packageSources: sources.Select(s => s.PackageSource),
            log: _nugetLogger);

        var resolver = new PackageResolver();
        var resolved = resolver.Resolve(resolverContext, ct).ToList();

        var assemblyPaths = ImmutableArray.CreateBuilder<string>();
        var probing = ImmutableArray.CreateBuilder<string>();
        var versions = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in resolved)
        {
            var info = available.First(a =>
                string.Equals(a.Id, pkg.Id, StringComparison.OrdinalIgnoreCase) && a.Version == pkg.Version);
            var installedPath = await EnsureInstalledAsync(info, packagesRoot, ct);
            versions[pkg.Id] = pkg.Version.ToNormalizedString();

            var reader = new PackageFolderReader(installedPath);
            var libItems = (await reader.GetLibItemsAsync(ct)).ToList();
            if (libItems.Count == 0) continue;

            var bestMatch = new FrameworkReducer().GetNearest(framework, libItems.Select(li => li.TargetFramework));
            var chosen = libItems.FirstOrDefault(li => li.TargetFramework == bestMatch);
            if (chosen is null) continue;

            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var relPath in chosen.Items)
            {
                if (!relPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                var full = Path.Combine(installedPath, relPath);
                if (!File.Exists(full)) continue;
                assemblyPaths.Add(full);
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir)) dirs.Add(dir);
            }
            foreach (var d in dirs) probing.Add(d);
        }

        return new ResolvedPackageSet(
            assemblyPaths.ToImmutable(),
            probing.Distinct(StringComparer.OrdinalIgnoreCase).ToImmutableArray(),
            versions.ToImmutable());
    }

    private async Task<PackageIdentity> ResolveIdentityAsync(
        NuGetPackageReference req, SourceRepository[] sources, NuGetFramework framework, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(req.VersionRange) && NuGetVersion.TryParse(req.VersionRange, out var pinned))
            return new PackageIdentity(req.Id, pinned);

        var range = string.IsNullOrEmpty(req.VersionRange)
            ? VersionRange.All
            : VersionRange.Parse(req.VersionRange);

        foreach (var src in sources)
        {
            var finder = await src.GetResourceAsync<FindPackageByIdResource>(ct);
            var versions = await finder.GetAllVersionsAsync(req.Id, _sourceCache, _nugetLogger, ct);
            var match = versions?.Where(v => range.Satisfies(v)).OrderByDescending(v => v).FirstOrDefault();
            if (match is not null) return new PackageIdentity(req.Id, match);
        }
        throw new InvalidOperationException($"No NuGet package '{req.Id}' matching '{req.VersionRange ?? "*"}' found on configured sources.");
    }

    private async Task WalkDependenciesAsync(
        PackageIdentity root, SourceRepository[] sources, NuGetFramework framework,
        HashSet<SourcePackageDependencyInfo> collected, CancellationToken ct)
    {
        var queue = new Queue<PackageIdentity>();
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (collected.Any(c => PackageIdentityComparer.Default.Equals(c, current))) continue;

            SourcePackageDependencyInfo? info = null;
            foreach (var src in sources)
            {
                var resource = await src.GetResourceAsync<DependencyInfoResource>(ct);
                info = await resource.ResolvePackage(current, framework, _sourceCache, _nugetLogger, ct);
                if (info is not null) break;
            }
            if (info is null) continue;

            collected.Add(info);
            foreach (var dep in info.Dependencies)
            {
                var depIdentity = new PackageIdentity(dep.Id, dep.VersionRange.MinVersion);
                if (!collected.Any(c => PackageIdentityComparer.Default.Equals(c, depIdentity)))
                    queue.Enqueue(depIdentity);
            }
        }
    }

    private async Task<string> EnsureInstalledAsync(SourcePackageDependencyInfo info, string packagesRoot, CancellationToken ct)
    {
        var versionString = info.Version.ToNormalizedString();
        var installedPath = Path.Combine(packagesRoot, info.Id.ToLowerInvariant(), versionString);
        if (Directory.Exists(installedPath) && Directory.EnumerateFileSystemEntries(installedPath).Any())
            return installedPath;

        // Try to hydrate from the persistent cache (e.g., Azure Blob) before hitting the feed.
        Directory.CreateDirectory(installedPath);
        if (await _packageCache.TryHydrateAsync(info.Id, versionString, installedPath, ct))
        {
            MsLogging.LoggerExtensions.LogInformation(logger,
                "Hydrated NuGet package {Id} {Version} from persistent cache", info.Id, versionString);
            return installedPath;
        }

        var downloadResource = await info.Source.GetResourceAsync<DownloadResource>(ct);
        var downloadContext = new PackageDownloadContext(_sourceCache);
        using var result = await downloadResource.GetDownloadResourceResultAsync(
            new PackageIdentity(info.Id, info.Version), downloadContext, packagesRoot, _nugetLogger, ct);

        if (result.Status != DownloadResourceResultStatus.Available && result.Status != DownloadResourceResultStatus.AvailableWithoutStream)
            throw new InvalidOperationException($"Failed to download {info.Id} {info.Version}: {result.Status}");

        result.PackageStream.Seek(0, SeekOrigin.Begin);
        await PackageExtractor.ExtractPackageAsync(
            source: info.Source.PackageSource.Source,
            packageStream: result.PackageStream,
            packagePathResolver: new PackagePathResolver(packagesRoot),
            packageExtractionContext: new PackageExtractionContext(
                PackageSaveMode.Defaultv3,
                XmlDocFileSaveMode.Skip,
                ClientPolicyContext.GetClientPolicy(_settings, _nugetLogger),
                _nugetLogger),
            token: ct);

        // Fire-and-forget cache save so we don't block the compile path on a slow upload.
        _ = Task.Run(async () =>
        {
            try
            {
                await _packageCache.SaveAsync(info.Id, versionString, installedPath, CancellationToken.None);
            }
            catch (Exception ex)
            {
                MsLogging.LoggerExtensions.LogWarning(logger, ex,
                    "Failed to save NuGet package {Id} {Version} to persistent cache", info.Id, versionString);
            }
        }, CancellationToken.None);

        return installedPath;
    }

    private static string BuildCacheKey(IReadOnlyCollection<NuGetPackageReference> requested, NuGetFramework framework)
    {
        var parts = requested
            .OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .Select(r => $"{r.Id}|{r.VersionRange ?? "*"}");
        return $"{framework.DotNetFrameworkName}::{string.Join(";", parts)}";
    }

    private sealed class NuGetLogger(MsLogging.ILogger logger) : LoggerBase
    {
        public override void Log(ILogMessage message)
        {
            var level = message.Level switch
            {
                LogLevel.Debug => MsLogging.LogLevel.Debug,
                LogLevel.Verbose => MsLogging.LogLevel.Trace,
                LogLevel.Information => MsLogging.LogLevel.Information,
                LogLevel.Minimal => MsLogging.LogLevel.Information,
                LogLevel.Warning => MsLogging.LogLevel.Warning,
                LogLevel.Error => MsLogging.LogLevel.Error,
                _ => MsLogging.LogLevel.Debug,
            };
            MsLogging.LoggerExtensions.Log(logger, level, "{Message}", message.Message);
        }

        public override Task LogAsync(ILogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }
    }
}
