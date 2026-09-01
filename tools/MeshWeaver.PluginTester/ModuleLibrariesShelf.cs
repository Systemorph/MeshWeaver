using System.Collections.Immutable;
using System.Text.Json;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The curated ADDITIONAL-libraries shelf (<c>module-libs/</c> beside the builder — the publish of
/// <c>tools/MeshWeaver.ModuleLibraries</c>, staged into the tester image by CD like
/// <c>razor-generators/</c> and <c>sdk-generators/</c>). A container build resolves an
/// otherwise-unsupplied <c>PackageReference</c> here, and the package's assemblies RIDE the bundle
/// together with their transitive shelf closure — every ride derived from the shelf's own
/// <c>deps.json</c>, never guessed (the 2026-08-19/20 outage was a guessed closure, and the lane's
/// no-extra-refs stance exists so that cannot recur).
///
/// <para>🚨 <b>The container still wins — for the COMPILE.</b> An assembly the platform image
/// supplies (its <c>/app</c>, its shared frameworks) is referenced from there, not from the shelf:
/// two metadata references to one simple name is the CS0433 family.</para>
///
/// <para>🚨 <b>It does NOT decide what rides.</b> That is
/// <see cref="PrivateClosure"/>'s job, and it deliberately does not ask whether <c>/app</c> has
/// the file: the reference image is a PORTAL, so a module compiled into it puts its own private
/// dependencies in <c>/app</c>, and treating that as a platform guarantee is what emptied the
/// <c>MeshWeaver.AI</c> bundle's closure (MeshWeaver.Plugins#1043).
/// <see cref="Resolution.RideFiles"/> answers the narrower question "what would ride from the
/// shelf alone" and is no longer the bundle's ride set.</para>
/// </summary>
public sealed class ModuleLibrariesShelf
{
    /// <summary>The directory, beside the builder, that carries the shelf.</summary>
    public const string DirectoryName = "module-libs";

    private readonly ImmutableDictionary<string, ImmutableArray<string>> _assembliesByPackage;
    private readonly ImmutableDictionary<string, ImmutableArray<string>> _dependenciesByPackage;
    private readonly ImmutableDictionary<string, string> _versionsByPackage;
    private readonly ImmutableDictionary<string, string> _filesByName;

    private ModuleLibrariesShelf(
        string directory,
        ImmutableDictionary<string, ImmutableArray<string>> assembliesByPackage,
        ImmutableDictionary<string, ImmutableArray<string>> dependenciesByPackage,
        ImmutableDictionary<string, string> versionsByPackage,
        ImmutableDictionary<string, string> filesByName)
    {
        Directory = directory;
        _assembliesByPackage = assembliesByPackage;
        _dependenciesByPackage = dependenciesByPackage;
        _versionsByPackage = versionsByPackage;
        _filesByName = filesByName;
    }

    /// <summary>Where the shelf was read from.</summary>
    public string Directory { get; }

    /// <summary>The packages the shelf carries, for the build log.</summary>
    public int PackageCount => _assembliesByPackage.Count;

    /// <summary>
    /// Locates the shelf: <c>module-libs/</c> beside the builder first (the in-image layout), then
    /// under the reference container's directory. Null when no shelf ships — a dev build without
    /// the staging; a shelf-needing module then fails by name exactly as before.
    /// </summary>
    public static ModuleLibrariesShelf? Locate(string appDirectory)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, DirectoryName),
                     Path.Combine(appDirectory, DirectoryName),
                 })
        {
            var full = Path.GetFullPath(candidate);
            if (System.IO.Directory.Exists(full)
                && System.IO.Directory.GetFiles(full, "*.deps.json").Length == 1)
                return Read(full);
        }
        return null;
    }

    /// <summary>
    /// Reads a shelf directory: exactly one <c>*.deps.json</c> (the closure record), assemblies on
    /// disk beside it. A shelf whose record is unreadable is a FAILURE — a shelf that silently
    /// resolves nothing turns every consumer into the unresolved-package refusal with a lie
    /// attached ("the shelf has no such package" when really the shelf could not be read).
    /// </summary>
    public static ModuleLibrariesShelf Read(string directory)
    {
        var full = Path.GetFullPath(directory);
        var depsFiles = System.IO.Directory.GetFiles(full, "*.deps.json");
        if (depsFiles.Length != 1)
            throw new InvalidOperationException(
                $"'{full}' holds {depsFiles.Length} *.deps.json files — the shelf needs exactly one: "
                + "it IS the closure record every ride is derived from.");

        using var document = JsonDocument.Parse(File.ReadAllText(depsFiles[0]));
        var assemblies = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);
        var dependencies = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase);
        var versions = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!document.RootElement.TryGetProperty("targets", out var targets))
            throw new InvalidOperationException($"'{depsFiles[0]}' has no 'targets' — not a deps.json.");
        foreach (var target in targets.EnumerateObject())
        {
            foreach (var library in target.Value.EnumerateObject())
            {
                var slash = library.Name.IndexOf('/');
                if (slash <= 0) continue;
                var id = library.Name[..slash];
                var version = library.Name[(slash + 1)..];
                versions[id] = version;

                if (library.Value.TryGetProperty("runtime", out var runtime))
                {
                    var names = runtime.EnumerateObject()
                        .Select(r => Path.GetFileNameWithoutExtension(r.Name))
                        .Where(n => n.Length > 0)
                        .ToImmutableArray();
                    if (!names.IsEmpty)
                        assemblies[id] = names;
                }
                if (library.Value.TryGetProperty("dependencies", out var deps))
                    dependencies[id] = [.. deps.EnumerateObject().Select(d => d.Name)];
            }
        }

        var files = System.IO.Directory.GetFiles(full, "*.dll")
            .ToImmutableDictionary(
                p => Path.GetFileNameWithoutExtension(p),
                p => p,
                StringComparer.OrdinalIgnoreCase);

        return new ModuleLibrariesShelf(
            full, assemblies.ToImmutable(), dependencies.ToImmutable(),
            versions.ToImmutable(), files);
    }

    /// <summary>Whether the shelf carries this package at all (its deps record names it).</summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>True when the shelf's deps.json has a node for it.</returns>
    public bool Knows(string packageId) => _assembliesByPackage.ContainsKey(packageId);

    /// <summary>The assembly simple names this package contributes, per the shelf's deps record.</summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>The simple names, empty when the shelf does not carry the package.</returns>
    public ImmutableArray<string> AssembliesOf(string packageId) =>
        _assembliesByPackage.TryGetValue(packageId, out var names) ? names : [];

    /// <summary>The package ids this package depends on, per the shelf's deps record.</summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>The dependency ids, empty when the shelf does not carry the package.</returns>
    public ImmutableArray<string> DependenciesOf(string packageId) =>
        _dependenciesByPackage.TryGetValue(packageId, out var deps) ? deps : [];

    /// <summary>The shelf's pinned version for a package, or null.</summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>The version string, or null.</returns>
    public string? VersionOf(string packageId) => _versionsByPackage.GetValueOrDefault(packageId);

    /// <summary>The file on the shelf backing an assembly simple name, or null.</summary>
    /// <param name="assemblyName">The assembly's simple name.</param>
    /// <returns>The path, or null.</returns>
    public string? FileFor(string assemblyName) => _filesByName.GetValueOrDefault(assemblyName);

    /// <summary>One shelf resolution: the package's own assemblies plus its transitive ride set.</summary>
    /// <param name="PackageId">The package.</param>
    /// <param name="Version">The shelf's pinned version — the same central pin the SDK path used.</param>
    /// <param name="ReferenceFiles">The compile references: the package's deps-recorded transitive
    /// shelf closure, minus what the container supplies (those are already referenced from
    /// <c>/app</c>). The SDK hands a consumer its package's TRANSITIVE compile surface — a
    /// <c>PackageReference Microsoft.Graph</c> lets code <c>using Microsoft.Kiota…</c> — and a
    /// shelf that offered only the package's own assemblies re-created exactly that gap
    /// (Plugins#1032, 2026-08-31: Mail.MicrosoftGraph rode Kiota at runtime but could not compile
    /// against it).</param>
    /// <param name="RideFiles">Every file that must travel with the bundle: the package's
    /// assemblies plus its transitive shelf dependencies, MINUS anything the container already
    /// supplies (a duplicate beside the platform's copy is the binding trap, not a convenience).</param>
    public sealed record Resolution(
        string PackageId, string Version,
        ImmutableArray<string> ReferenceFiles,
        ImmutableArray<string> RideFiles);

    /// <summary>
    /// Resolves one package from the shelf, or null when the shelf does not carry it.
    /// </summary>
    /// <param name="packageId">The package id.</param>
    /// <param name="suppliedByContainer">Filter: an assembly simple name the landing image already
    /// supplies (its /app, its shared frameworks) — those never ride.</param>
    public Resolution? Resolve(string packageId, Func<string, bool> suppliedByContainer)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentNullException.ThrowIfNull(suppliedByContainer);
        if (!_assembliesByPackage.TryGetValue(packageId, out var own))
            return null;

        // The package must put at least one of its OWN assemblies on the shelf to resolve at all —
        // "the shelf carries it" means the package, not merely some transitive of another entry.
        if (!own.Any(_filesByName.ContainsKey))
            return null;

        // The transitive shelf closure: packages reachable from this one whose assemblies are on
        // the shelf and NOT supplied by the landing image. Walked over the deps.json graph — the
        // record, not a guess.
        var rides = ImmutableArray.CreateBuilder<string>();
        var seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(packageId);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!seenPackages.Add(current)) continue;
            if (_assembliesByPackage.TryGetValue(current, out var names))
                foreach (var name in names)
                {
                    if (suppliedByContainer(name)) continue;
                    if (!_filesByName.TryGetValue(name, out var file)) continue;
                    if (seenFiles.Add(name))
                        rides.Add(file);
                }
            if (_dependenciesByPackage.TryGetValue(current, out var next))
                foreach (var dependency in next)
                    pending.Push(dependency);
        }

        // Compile references ARE the ride closure: same record, same container-wins filter. The
        // container-supplied names are already referenced from /app — offering the shelf's copy
        // beside them would be the same-identity duplicate the ride filter exists to prevent.
        var closure = rides.ToImmutable();
        return new Resolution(
            packageId,
            _versionsByPackage.GetValueOrDefault(packageId, "unknown"),
            closure,
            closure);
    }
}
