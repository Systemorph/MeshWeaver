using System.Collections.Immutable;
using System.Text.Json;

namespace MeshWeaver.PluginTester;

/// <summary>
/// The platform, AS DELIVERED IN THIS CONTAINER, as a build input — the reference half of
/// <c>mw-plugin-test build-project</c>.
///
/// <para>A module does not run against the platform's SOURCE and it does not run against a NuGet
/// feed: it is loaded into the platform IMAGE and bound by the assemblies in there (maintainer,
/// 2026-08-30: <i>"it must be built against references in mesh container"</i>, <i>"the platform
/// builds dll completely without any external dotnet kit or nuget"</i>). So the reference set is
/// the assemblies on disk beside this process, and the versions are the ones the image's own
/// <c>.deps.json</c> records — what SHIPPED, not what a source tree would resolve today.</para>
///
/// <para>This is the C# port of <c>MeshWeaver.Plugins/scripts/container-refs.py</c>. That script
/// extracts an image and emits MSBuild files for a <c>dotnet build</c>; this type runs INSIDE the
/// container and reads <c>/app</c> directly, so there is no extraction, no props file and no
/// restore between the assemblies and the compilation.</para>
///
/// <para>🚨 <b>Every read FAILS CLOSED.</b> An unreadable <c>/app</c>, a missing or ambiguous
/// <c>.deps.json</c>, a package with no version, platform assemblies that disagree on their binding
/// identity — each stops the run RED. A reference set assembled from a partial read produces a
/// build that is green against a reference set nobody can name, which is the failure the whole
/// design exists to end.</para>
///
/// <para>🚨 <b>A package is matched by the ASSEMBLY FILE on disk, never by its id alone.</b> A
/// package can ship an assembly under a different name, or none at all (a metapackage), and
/// treating a <c>PackageReference</c> as satisfied when its assembly is not actually there is how a
/// build loses a type it needs.</para>
/// </summary>
public sealed class ContainerReferenceSet
{
    /// <summary>Raised for every fail-closed condition; the message names what was missing.</summary>
    public sealed class UnreadableContainerException(string message) : Exception(message);

    /// <summary>Where the platform's own assemblies live in every MeshWeaver image.</summary>
    public const string DefaultAppDirectory = "/app";

    private ContainerReferenceSet(
        string appDirectory,
        ImmutableDictionary<string, string> packageVersions,
        ImmutableDictionary<string, ImmutableArray<string>> packageAssemblies,
        ImmutableDictionary<string, ImmutableArray<string>> packageDependencies,
        ImmutableDictionary<string, string> assembliesByName,
        ImmutableHashSet<string> frameworkAssemblyNames,
        string platformAssemblyVersion)
    {
        AppDirectory = appDirectory;
        PackageVersions = packageVersions;
        PackageAssemblies = packageAssemblies;
        PackageDependencies = packageDependencies;
        AssembliesByName = assembliesByName;
        FrameworkAssemblyNames = frameworkAssemblyNames;
        PlatformAssemblyVersion = platformAssemblyVersion;
    }

    /// <summary>The directory the reference set was read from.</summary>
    public string AppDirectory { get; }

    /// <summary>Package id → the version the image was built with.</summary>
    public ImmutableDictionary<string, string> PackageVersions { get; }

    /// <summary>Package id → the assembly simple names it contributes, per the image's deps.json.</summary>
    public ImmutableDictionary<string, ImmutableArray<string>> PackageAssemblies { get; }

    /// <summary>Package id → the package ids it depends on, per the image's deps.json. The
    /// record the private-closure walk follows (<see cref="PrivateClosure"/>) — never a guess.</summary>
    public ImmutableDictionary<string, ImmutableArray<string>> PackageDependencies { get; }

    /// <summary>Assembly simple name → the file backing it (case-insensitive).</summary>
    public ImmutableDictionary<string, string> AssembliesByName { get; }

    /// <summary>
    /// The assembly simple names the container's SHARED FRAMEWORKS supply
    /// (<c>Microsoft.NETCore.App</c>, <c>Microsoft.AspNetCore.App</c>, …).
    ///
    /// <para>🚨 This — and ONLY this — is what a module bundle may leave out of its own closure.
    /// The shared framework travels with every host that can load the module at all, so an
    /// assembly it supplies is a genuine platform guarantee. <c>/app</c> is NOT: it is one
    /// PORTAL's composition, and a portal that happens to carry a module compiled in also carries
    /// that module's private package dependencies. Reading <c>/app</c> as a guarantee is what
    /// dropped <c>Microsoft.Agents.AI</c> out of the AI bundle and made every consumer that is not
    /// that exact image fail with <c>ReflectionTypeLoadException</c>
    /// (MeshWeaver.Plugins#1043).</para>
    /// </summary>
    public ImmutableHashSet<string> FrameworkAssemblyNames { get; }

    /// <summary>The one <c>AssemblyVersion</c> every <c>MeshWeaver.*</c> assembly in the image carries.</summary>
    public string PlatformAssemblyVersion { get; }

    /// <summary>Every assembly file in the reference set, ordered by name.</summary>
    public ImmutableArray<string> AssemblyPaths =>
        [.. AssembliesByName.Values.OrderBy(p => p, StringComparer.Ordinal)];

    /// <summary>
    /// Reads the reference set out of the running container.
    /// </summary>
    /// <param name="appDirectory">Where the platform assemblies are; <c>/app</c> in every image.</param>
    /// <param name="trustedPlatformAssemblies">The process's TPA list — the shared framework plus
    /// this app's own closure. Defaults to the running process's; injectable so the fail-closed
    /// behaviour is testable without a container.</param>
    /// <param name="sharedFrameworksRoot">The PLATFORM image's <c>shared/</c> frameworks root
    /// (<c>&lt;dotnet root&gt;/shared</c>, one directory per framework, one per version below it).
    /// 🚨 Pass it whenever the app directory comes from a DIFFERENT image than the one this
    /// process runs in: the module lands in the PLATFORM's runtime, whose ASP.NET Core shared
    /// framework supplies assemblies (FileSystemGlobbing, Components.Web…) that a console-image
    /// builder's own runtime does not have — measured on MeshWeaver.Plugins#1032, where every
    /// container entry redded on Microsoft.Extensions.FileSystemGlobbing, a framework-provided
    /// (NU1510-prunable) assembly the portal runtime carries and the tester image does not. When
    /// null, the RUNNING runtime's shared root is used, which is correct only in-place.</param>
    /// <returns>The reference set.</returns>
    /// <exception cref="UnreadableContainerException">Anything that would leave the set partial.</exception>
    public static ContainerReferenceSet Read(
        string? appDirectory = null, string? trustedPlatformAssemblies = null,
        string? sharedFrameworksRoot = null)
    {
        var app = Path.GetFullPath(appDirectory ?? DefaultAppDirectory);
        if (!Directory.Exists(app))
            throw new UnreadableContainerException(
                $"'{app}' does not exist, so there is no container to build against. This verb only "
                + "runs inside a MeshWeaver image — from outside, pass --image and let the CLI run it "
                + "in one.");

        var onDisk = Directory.GetFiles(app, "*.dll");
        if (onDisk.Length == 0)
            throw new UnreadableContainerException(
                $"'{app}' holds no assemblies. A reference set of nothing compiles nothing, so this "
                + "is a failure rather than an empty build.");

        var deps = DepsFile(app);
        var (packageVersions, packageAssemblies, packageDependencies, platformVersion) = ReadDeps(deps);

        // The reference set is the union of three things the container supplies, in increasing
        // priority: the SHARED FRAMEWORKS installed in it, the assemblies this process was
        // launched with, and the assemblies on disk in /app.
        var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. The shared frameworks. 🚨 Not reachable through TPA alone: TPA is the BUILDER's
        //    closure, and the builder is not a web app, so Microsoft.AspNetCore.App's assemblies
        //    (Components, Components.Web, SignalR, …) are absent from it — while the portal the
        //    module is loaded into runs on exactly that framework. Measured: 15 of 51 Plugins
        //    modules reported Microsoft.AspNetCore.Components.Web "not supplied" against an image
        //    that ships it. This is the C# form of Directory.PlatformRefs.targets'
        //    <FrameworkReference Include="Microsoft.AspNetCore.App" />.
        var frameworkNames = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in SharedFrameworkAssemblies(sharedFrameworksRoot))
        {
            var simple = Path.GetFileNameWithoutExtension(path);
            frameworkNames.Add(simple);
            byName.TryAdd(simple, path);
        }

        // 2. This process's own closure.
        var tpa = trustedPlatformAssemblies
            ?? AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? string.Empty;
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!File.Exists(path)) continue;
            byName[Path.GetFileNameWithoutExtension(path)] = path;
        }

        // 3. /app wins: it is what the image ships, and a module dropped in after publish is on
        //    no other list.
        foreach (var path in onDisk)
            byName[Path.GetFileNameWithoutExtension(path)] = path;

        return new ContainerReferenceSet(
            app,
            packageVersions,
            packageAssemblies,
            packageDependencies,
            byName.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase),
            frameworkNames.ToImmutable(),
            platformVersion);
    }

    /// <summary>
    /// Every assembly of every shared framework installed in this container — the highest version
    /// of each (<c>Microsoft.NETCore.App</c>, <c>Microsoft.AspNetCore.App</c>, …).
    ///
    /// <para>Located from the RUNNING runtime directory rather than from a path guess: the layout
    /// is <c>&lt;root&gt;/shared/&lt;Framework&gt;/&lt;version&gt;/</c>, so the runtime directory's
    /// grandparent is the <c>shared</c> root. Returns nothing when that shape does not hold — a
    /// self-contained deployment has no shared root, and TPA already carries everything there.</para>
    /// </summary>
    private static IEnumerable<string> SharedFrameworkAssemblies(string? explicitRoot = null)
    {
        string? sharedRoot;
        if (explicitRoot is { Length: > 0 })
        {
            // The PLATFORM's frameworks, handed over explicitly (see Read). A named root that is
            // not there is a hard failure — silently falling back to the builder's own runtime
            // would resurrect exactly the false "additional library" verdicts this parameter
            // exists to kill.
            sharedRoot = Path.GetFullPath(explicitRoot);
            if (!Directory.Exists(sharedRoot))
                throw new UnreadableContainerException(
                    $"--shared-frameworks '{sharedRoot}' does not exist. It must be the platform "
                    + "image's <dotnet root>/shared directory; without it, framework-provided "
                    + "assemblies read as missing packages.");
        }
        else
        {
            var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
            var frameworkDirectory = Path.GetDirectoryName(runtime.TrimEnd(Path.DirectorySeparatorChar, '/'));
            sharedRoot = frameworkDirectory is null ? null : Path.GetDirectoryName(frameworkDirectory);
            // The name check only guards the DERIVED shape (a self-contained deployment has no
            // shared root and TPA already carries everything); an explicit root was validated above
            // and may be mounted under any name.
            if (sharedRoot is null
                || !string.Equals(Path.GetFileName(sharedRoot), "shared", StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(sharedRoot))
                yield break;
        }

        foreach (var framework in Directory.GetDirectories(sharedRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            // Highest version wins, compared as a Version so 10.0.10 sorts above 10.0.9.
            var newest = Directory.GetDirectories(framework)
                .Select(d => (Directory: d, Version: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
                .Where(x => x.Version is not null)
                .OrderByDescending(x => x.Version)
                .Select(x => x.Directory)
                .FirstOrDefault();
            if (newest is null) continue;
            foreach (var dll in Directory.GetFiles(newest, "*.dll"))
                yield return dll;
        }
    }

    /// <summary>The host's <c>.deps.json</c> — the one file that records what the image was BUILT with.</summary>
    private static string DepsFile(string app)
    {
        var candidates = Directory.GetFiles(app, "*.deps.json").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        if (candidates.Length != 1)
            throw new UnreadableContainerException(
                $"expected exactly one *.deps.json in '{app}', found {candidates.Length}"
                + (candidates.Length == 0 ? "" : ": " + string.Join(", ", candidates.Select(Path.GetFileName)))
                + ". Without it the package versions and the binding identity cannot be read, and a "
                + "reference set nobody can name is not a reference set.");
        return candidates[0];
    }

    private static (ImmutableDictionary<string, string> Versions,
                    ImmutableDictionary<string, ImmutableArray<string>> Assemblies,
                    ImmutableDictionary<string, ImmutableArray<string>> Dependencies,
                    string PlatformAssemblyVersion)
        ReadDeps(string path)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            throw new UnreadableContainerException($"{Path.GetFileName(path)} is not readable JSON — {ex.Message}");
        }

        using (doc)
        {
            var versions = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("libraries", out var libraries))
                foreach (var library in libraries.EnumerateObject())
                {
                    if (!library.Value.TryGetProperty("type", out var type)
                        || type.GetString() != "package")
                        continue;
                    var slash = library.Name.IndexOf('/');
                    if (slash <= 0 || slash == library.Name.Length - 1)
                        throw new UnreadableContainerException(
                            $"{Path.GetFileName(path)} carries a package entry this builder cannot "
                            + $"parse: '{library.Name}'.");
                    versions[library.Name[..slash]] = library.Name[(slash + 1)..];
                }
            if (versions.Count == 0)
                throw new UnreadableContainerException(
                    $"{Path.GetFileName(path)} lists no packages — a build against it would resolve nothing.");

            if (!doc.RootElement.TryGetProperty("targets", out var targets)
                || targets.ValueKind != JsonValueKind.Object)
                throw new UnreadableContainerException(
                    $"{Path.GetFileName(path)} has no targets section, so no assembly version can be read.");

            JsonElement runtimeTarget = default;
            foreach (var target in targets.EnumerateObject())
                runtimeTarget = target.Value;

            var assemblies = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(
                StringComparer.OrdinalIgnoreCase);
            var dependencies = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(
                StringComparer.OrdinalIgnoreCase);
            var bindingIdentities = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var entry in runtimeTarget.EnumerateObject())
            {
                var entrySlash = entry.Name.IndexOf('/');
                var entryId = entrySlash > 0 ? entry.Name[..entrySlash] : entry.Name;
                // The dependency edges are read for EVERY node, runtime assets or not: a
                // metapackage carries no runtime of its own and is exactly the node a closure walk
                // has to pass THROUGH rather than stop at.
                if (entry.Value.TryGetProperty("dependencies", out var edges)
                    && edges.ValueKind == JsonValueKind.Object)
                    dependencies[entryId] = [.. edges.EnumerateObject().Select(d => d.Name)];
                if (!entry.Value.TryGetProperty("runtime", out var runtime)
                    || runtime.ValueKind != JsonValueKind.Object)
                    continue;
                var id = entryId;
                var names = ImmutableArray.CreateBuilder<string>();
                foreach (var file in runtime.EnumerateObject())
                {
                    var simple = Path.GetFileNameWithoutExtension(file.Name.Replace('\\', '/').Split('/')[^1]);
                    names.Add(simple);
                    if (!simple.StartsWith("MeshWeaver.", StringComparison.Ordinal)) continue;
                    if (file.Value.TryGetProperty("assemblyVersion", out var av)
                        && av.GetString() is { Length: > 0 } version)
                        bindingIdentities.Add(version);
                }
                if (names.Count > 0)
                    assemblies[id] = names.ToImmutable();
            }

            if (bindingIdentities.Count != 1)
                // 🚨 MeshWeaver#143's failure, caught in the image instead of at run time: a coherent
                // build has ONE binding identity across every platform assembly.
                throw new UnreadableContainerException(
                    "the image's MeshWeaver assemblies do not agree on a binding identity ("
                    + (bindingIdentities.Count == 0 ? "none found" : string.Join(", ", bindingIdentities))
                    + "). That is the drift the AssemblyVersion contract exists to prevent; refusing "
                    + "to emit a reference set.");

            return (versions.ToImmutable(), assemblies.ToImmutable(), dependencies.ToImmutable(),
                bindingIdentities.Single());
        }
    }

    /// <summary>What resolving one <c>PackageReference</c> against the container concluded.</summary>
    /// <param name="Id">The package id, as the project declared it.</param>
    /// <param name="Version">The version the image was built with, when the image knows the package.</param>
    /// <param name="AssemblyPaths">The files that satisfy it, empty when it is not supplied.</param>
    /// <param name="Supplied">Whether the container supplies the package's assemblies.</param>
    public sealed record PackageResolution(
        string Id, string? Version, ImmutableArray<string> AssemblyPaths, bool Supplied);

    /// <summary>
    /// Resolves a <c>PackageReference</c> against the container.
    ///
    /// <para>Matched by the ASSEMBLY FILE: the image's <c>.deps.json</c> says which assemblies the
    /// package contributes, and the package is supplied only when those files are in the reference
    /// set. A package the deps.json does not name falls back to <c>&lt;id&gt;.dll</c>, which is the
    /// same rule <c>container-refs.py</c>'s <c>supplied_packages</c> applies.</para>
    ///
    /// <para>A package with NO assembly here is an <b>additional library</b> — additional to the
    /// platform — and this mode cannot supply it. It is reported, never skipped.</para>
    /// </summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>The resolution.</returns>
    public PackageResolution Resolve(string packageId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        var version = PackageVersions.GetValueOrDefault(packageId);
        var contributed = PackageAssemblies.TryGetValue(packageId, out var names) ? names : [packageId];
        var files = contributed
            .Where(AssembliesByName.ContainsKey)
            .Select(n => AssembliesByName[n])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new PackageResolution(packageId, version, files, !files.IsEmpty);
    }

    /// <summary>
    /// The file backing an assembly simple name, or null when the container does not carry it.
    /// This is how a <c>ProjectReference</c> that points outside the source tree is satisfied: the
    /// project it names is already IN the image, as an assembly.
    /// </summary>
    /// <param name="assemblyName">The assembly's simple name.</param>
    /// <returns>The file path, or null.</returns>
    public string? FindAssembly(string assemblyName) =>
        AssembliesByName.GetValueOrDefault(assemblyName);

    /// <summary>
    /// Whether the container's SHARED FRAMEWORK supplies this assembly — the one thing a module
    /// bundle may leave out of its own closure, because it travels with every host that can load
    /// the module at all. Deliberately NOT "is it in <c>/app</c>": see
    /// <see cref="FrameworkAssemblyNames"/>.
    /// </summary>
    /// <param name="assemblyName">The assembly's simple name.</param>
    /// <returns>True when a shared framework in this container carries it.</returns>
    public bool IsFrameworkSupplied(string assemblyName) =>
        FrameworkAssemblyNames.Contains(assemblyName);

    /// <summary>
    /// The assembly simple names a package contributes, per the image's own deps.json — falling
    /// back to <c>&lt;id&gt;</c> for a package the image never resolved, which is the same rule
    /// <see cref="Resolve"/> applies.
    /// </summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>The simple names.</returns>
    public ImmutableArray<string> AssembliesOf(string packageId) =>
        PackageAssemblies.TryGetValue(packageId, out var names) ? names : [packageId];

    /// <summary>The package ids a package depends on, per the image's deps.json.</summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>The dependency ids, empty when the image does not know the package.</returns>
    public ImmutableArray<string> DependenciesOf(string packageId) =>
        PackageDependencies.TryGetValue(packageId, out var deps) ? deps : [];
}
