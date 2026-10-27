using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.Compiler;

/// <summary>
/// What each metadata reference of a compile IS, for the dependency edges
/// (<c>Doc/Architecture/DependencyGraph</c>): an installed MODULE, a PACKAGE a <c>#r "nuget:"</c>
/// directive (or a module-libraries shelf) supplied, or — everything else — the platform host's
/// own set. Told apart by WHICH FILE a reference is, never by its name: a directory host's
/// platform references are fresh instances, and an image-bound module may sit in the baseline.
/// Within the platform set the <c>MeshWeaver.*</c> assemblies are <see cref="Mesh.DependencyEdgeKinds.Platform"/>
/// edges and the rest (the BCL, third-party host libraries) are not recorded.
/// </summary>
/// <param name="Modules">The references that are installed modules.</param>
/// <param name="Packages">The references a package supplied, each mapped to its package id.</param>
public sealed record DependencyReferenceClasses(
    IReadOnlySet<MetadataReference> Modules,
    IReadOnlyDictionary<MetadataReference, string> Packages)
{
    /// <summary>The assembly-name prefix of the MeshWeaver platform. A platform-baseline reference
    /// whose simple name is <see cref="PlatformAssemblyName"/> or starts with this is a
    /// <see cref="Mesh.DependencyEdgeKinds.Platform"/> edge.</summary>
    public const string PlatformAssemblyPrefix = "MeshWeaver.";

    /// <summary>The bare platform assembly name (no dotted suffix).</summary>
    public const string PlatformAssemblyName = "MeshWeaver";

    /// <summary>No module and no package: every reference is the platform host's.</summary>
    public static DependencyReferenceClasses None { get; } = new(
        ImmutableHashSet<MetadataReference>.Empty, ImmutableDictionary<MetadataReference, string>.Empty);

    /// <summary>Whether an assembly simple name is a MeshWeaver platform assembly name.</summary>
    /// <param name="assemblyName">The assembly's simple name.</param>
    public static bool IsPlatformName(string assemblyName) =>
        string.Equals(assemblyName, PlatformAssemblyName, StringComparison.Ordinal)
        || assemblyName.StartsWith(PlatformAssemblyPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Classifies <paramref name="references"/> by file: those whose file is one of
    /// <paramref name="moduleLocations"/> are modules, those whose file is one of
    /// <paramref name="packageLocations"/> are packages (a module location wins when a file is
    /// named by both).
    /// </summary>
    /// <param name="references">The compile's metadata references.</param>
    /// <param name="moduleLocations">The installed module assemblies' file paths.</param>
    /// <param name="packageLocations">The package-supplied assemblies' file paths (a
    /// <c>#r "nuget:"</c> resolution, a shelf package).</param>
    public static DependencyReferenceClasses Of(
        IEnumerable<MetadataReference> references,
        IEnumerable<string?> moduleLocations,
        IEnumerable<string?> packageLocations)
    {
        ArgumentNullException.ThrowIfNull(references);
        var all = references.ToList();
        var modules = DependencyEdgeExtraction.ModuleReferences(all, moduleLocations ?? []);
        var packageFiles = (packageLocations ?? [])
            .Where(l => !string.IsNullOrEmpty(l))
            .Select(l => Path.GetFullPath(l!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var packages = packageFiles.Count == 0
            ? ImmutableDictionary<MetadataReference, string>.Empty
            : all
                .Where(r => !modules.Contains(r)
                            && r is PortableExecutableReference { FilePath: { Length: > 0 } file }
                            && packageFiles.Contains(Path.GetFullPath(file)))
                .Distinct()
                .ToImmutableDictionary(r => r, r => PackageIdOf(((PortableExecutableReference)r).FilePath!));
        return new DependencyReferenceClasses(modules, packages);
    }

    /// <summary>
    /// Classifies <paramref name="references"/> with the package of each package file GIVEN by the
    /// caller — for a source whose files carry no package in their path (a flat module-libraries
    /// shelf, where only its deps.json knows which package owns which assembly).
    /// </summary>
    /// <param name="references">The compile's metadata references.</param>
    /// <param name="moduleLocations">The installed module assemblies' file paths.</param>
    /// <param name="packageIdByFile">Package file path → the package that owns it.</param>
    public static DependencyReferenceClasses Of(
        IEnumerable<MetadataReference> references,
        IEnumerable<string?> moduleLocations,
        IReadOnlyDictionary<string, string> packageIdByFile)
    {
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(packageIdByFile);
        var all = references.ToList();
        var modules = DependencyEdgeExtraction.ModuleReferences(all, moduleLocations ?? []);
        var byFile = packageIdByFile.ToImmutableDictionary(
            kv => Path.GetFullPath(kv.Key), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        var packages = all
            .Where(r => !modules.Contains(r)
                        && r is PortableExecutableReference { FilePath: { Length: > 0 } file }
                        && byFile.ContainsKey(Path.GetFullPath(file)))
            .Distinct()
            .ToImmutableDictionary(r => r, r => byFile[Path.GetFullPath(((PortableExecutableReference)r).FilePath!)]);
        return new DependencyReferenceClasses(modules, packages);
    }

    /// <summary>
    /// The package id a resolved package file belongs to, read off the NuGet global-packages layout
    /// <c>{root}/{id}/{version}/(lib|ref|runtimes)/…/{file}.dll</c>; the file's simple name when the
    /// path does not have that shape (a flat shelf), so the edge still names something real.
    /// </summary>
    /// <param name="path">The package assembly's path.</param>
    public static string PackageIdOf(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = segments.Length - 2; i >= 2; i--)
            if (segments[i] is "lib" or "ref" or "runtimes"
                && segments[i - 1].Length > 0 && char.IsDigit(segments[i - 1][0]))
                return segments[i - 2];
        return Path.GetFileNameWithoutExtension(path);
    }
}
