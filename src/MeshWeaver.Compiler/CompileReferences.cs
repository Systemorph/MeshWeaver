using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;

namespace MeshWeaver.Compiler;

/// <summary>
/// The metadata-reference set a dynamic NodeType compile is built against — the process-wide TPA
/// baseline plus this mesh's installed-module composition. Which assemblies Roslyn can see is
/// part of the compile's input, so the construction lives inside the toolchain identity boundary
/// (#1707); MeshWeaver.Graph resolves the module list from the mesh's service tree and threads it
/// in.
/// </summary>
public static class CompileReferences
{
    /// <summary>
    /// The process-wide default reference list — TPA assemblies plus a few well-known additions,
    /// built once at type load (eager static init: a plain field read on every compile, no Lazy
    /// dispatch, no synchronization). 🚨 NOT a constant lookup, whatever the collection type says:
    /// a <c>PortableExecutableReference</c> owns lazily mmap'd IDisposable metadata and Roslyn
    /// caches symbol tables against the instance — this is the process-wide CACHE tracked by #890,
    /// allowlisted by name in <c>NoStaticCollectionsTest.AllowedRoslynReferenceHolders</c>
    /// (migration held in #1438 pending the emit canary's verdict). An explicit field, not an
    /// auto-property, so the allowlist names <c>_default</c> rather than a compiler-generated
    /// backing field.
    /// </summary>
    public static IReadOnlyList<MetadataReference> Default => _default;

    private static readonly IReadOnlyList<MetadataReference> _default = GetDefaultReferences();

    /// <summary>
    /// Builds the process-wide MetadataReference list — TPA assemblies plus a few
    /// well-known additions. Uses <see cref="MetadataReference.CreateFromFile(string, MetadataReferenceProperties, DocumentationProvider)"/>
    /// (mmap, lazy read) — Roslyn typically reads only a small fraction of each
    /// assembly's metadata, so the upfront cost is tiny. An earlier attempt at
    /// <see cref="MetadataReference.CreateFromStream(System.IO.Stream, MetadataReferenceProperties, DocumentationProvider, string?)"/>
    /// to avoid finalizer pressure ended up reading the whole DLL into managed
    /// memory eagerly — net 10%+ slower in the autocomplete-test CPU profile,
    /// since most of those bytes were never touched. The file-handle finalizer
    /// pressure those references add is also tiny in practice (the static
    /// property holds them for the process lifetime; finalizers only run at shutdown).
    /// </summary>
    private static List<MetadataReference> GetDefaultReferences()
    {
        var references = new List<MetadataReference>();

        var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (trustedAssemblies != null)
        {
            foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    continue;
                try
                {
                    references.Add(HostReference(path));
                }
                catch
                {
                    // Skip assemblies that can't be loaded
                }
            }
        }

        // Three well-known additions in case TPA didn't include them. Dedup
        // against TPA-derived set by Display path so we don't double-load.
        var seen = new HashSet<string>(
            references.Select(r => r.Display ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in new[]
        {
            typeof(object).Assembly,                                           // System.Runtime
            typeof(System.ComponentModel.DataAnnotations.KeyAttribute).Assembly, // DataAnnotations
            typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute).Assembly, // System.Text.Json
        })
        {
            if (!string.IsNullOrEmpty(assembly.Location)
                && File.Exists(assembly.Location)
                && seen.Add(assembly.Location))
            {
                try
                {
                    references.Add(MetadataReference.CreateFromFile(assembly.Location));
                }
                catch
                {
                    // Skip if it can't be loaded
                }
            }
        }

        return references;
    }

    /// <summary>
    /// The metadata reference a compile uses for the host assembly at <paramref name="path"/>:
    /// the file itself — unless this toolchain carries the package's REFERENCE assembly for the
    /// same identity (simple name, version, public key token), which is then compiled against in
    /// its place. A package can keep members in its implementation for binary compatibility that
    /// its reference assembly hides from every compile (System.Interactive's
    /// <c>EnumerableEx.MaxBy</c>, which made the BCL's <c>Enumerable.MaxBy</c> ambiguous — CS0121);
    /// <c>src/</c> never sees them, and in-mesh C# must not either. The reference keeps
    /// <paramref name="path"/> as its file path, so it names the same host assembly. A host
    /// carrying a different build of the package gets its own file — the embedded surface
    /// describes only the identity it was built from.
    /// </summary>
    /// <param name="path">A host (TPA or container) assembly path.</param>
    public static MetadataReference HostReference(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        using var surface = typeof(CompileReferences).Assembly.GetManifestResourceStream(
            CompileSurfacePrefix + Path.GetFileName(path));
        if (surface is null)
            return MetadataReference.CreateFromFile(path);
        var image = new byte[surface.Length];
        surface.ReadExactly(image);
        return SameIdentity(image, path)
            ? MetadataReference.CreateFromImage(image, filePath: path)
            : MetadataReference.CreateFromFile(path);
    }

    private const string CompileSurfacePrefix = "MeshWeaver.Compiler.CompileSurface.";

    private static bool SameIdentity(byte[] referenceImage, string hostPath)
    {
        try
        {
            using var pe = new System.Reflection.PortableExecutable.PEReader(
                System.Collections.Immutable.ImmutableArray.Create(referenceImage));
            var surface = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe)
                .GetAssemblyDefinition().GetAssemblyName();
            var host = System.Reflection.AssemblyName.GetAssemblyName(hostPath);
            return string.Equals(surface.Name, host.Name, StringComparison.OrdinalIgnoreCase)
                && surface.Version == host.Version
                && (surface.GetPublicKeyToken() ?? []).AsSpan().SequenceEqual(host.GetPublicKeyToken() ?? []);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 🚨 <b>ONE ASSEMBLY PER NAME — the copy that will RUN.</b> Composes <paramref name="host"/>
    /// with a <c>#r "nuget:…"</c> resolution, which is the package's whole transitive closure and
    /// routinely carries assemblies the host ships too: Microsoft.Data.SqlClient 6.1.1 brings
    /// System.Text.Json 9 and Microsoft.Extensions.DependencyInjection.Abstractions 9 beside the
    /// shared framework's 10. Appended as-is, Roslyn sees two <c>JsonElement</c>s (CS0433) and two
    /// <c>GetRequiredService&lt;T&gt;</c>s (CS0121), and the NodeType never compiles (an enterprise
    /// client portal, 2026-10-15).
    ///
    /// <para>For a package assembly whose simple name the host set already holds, the compile takes
    /// exactly the copy the NodeType load context will BIND at run time
    /// (<see cref="MeshWeaver.NuGet.PackageAssemblyProbe.TryUnifyWithHost"/>), so compile and run
    /// cannot disagree:</para>
    /// <list type="bullet">
    ///   <item><b>Same identity, host equal or newer</b> → the host's reference; the package copy is
    ///     dropped (it unifies with the host at run time).</item>
    ///   <item><b>Same identity, package newer</b> → the package's copy REPLACES the host's reference
    ///     (the load context refuses an older host and loads the package's copy in isolation, so code
    ///     needing the newer API compiles against it).</item>
    ///   <item><b>Different identity</b> (another publisher's public key, another culture) → both are
    ///     kept: a same-named assembly of another publisher is a different assembly, at compile time
    ///     as at run time.</item>
    /// </list>
    /// <para>A package name appearing twice in the closure keeps its first path; a name the host does
    /// not hold is added. Pure and order-preserving, so the bake and the runtime compose the same set
    /// for the same host. Applied to the METADATA REFERENCES only: generator candidates and the
    /// stage-1 content key (<c>GeneratedInputDigestOf</c>) keep the full resolution.</para>
    /// </summary>
    /// <param name="host">The reference set the compile is built against (TPA or container set, plus
    /// modules) — WITHOUT any package closure.</param>
    /// <param name="packageAssemblyPaths">The resolved package closure's compile assemblies.</param>
    /// <param name="createReference">Makes a reference for a package path (default
    /// <see cref="MetadataReference.CreateFromFile(string, MetadataReferenceProperties, DocumentationProvider)"/>);
    /// a <c>null</c> answer skips the path.</param>
    public static IReadOnlyList<MetadataReference> WithPackageAssemblies(
        IReadOnlyList<MetadataReference> host,
        IEnumerable<string> packageAssemblyPaths,
        Func<string, MetadataReference?>? createReference = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(packageAssemblyPaths);
        createReference ??= path => MetadataReference.CreateFromFile(path);

        var hostIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < host.Count; i++)
            if (SimpleNameOf(host[i]) is { } name)
                hostIndex.TryAdd(name, i);

        var decided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var replaced = new Dictionary<int, MetadataReference>();
        var added = new List<MetadataReference>();
        foreach (var path in packageAssemblyPaths)
        {
            if (string.IsNullOrEmpty(path)) continue;
            var name = Path.GetFileNameWithoutExtension(path);
            if (!decided.Add(name)) continue;
            var choice = hostIndex.TryGetValue(name, out var index)
                ? Choose(host[index], path)
                : PackageChoice.Add;
            if (choice == PackageChoice.HostWins || createReference(path) is not { } reference)
                continue;
            if (choice == PackageChoice.ReplaceHost)
                replaced[index] = reference;
            else
                added.Add(reference);
        }

        if (replaced.Count == 0 && added.Count == 0)
            return host;
        var composed = new List<MetadataReference>(host.Count + added.Count);
        for (var i = 0; i < host.Count; i++)
            composed.Add(replaced.TryGetValue(i, out var swap) ? swap : host[i]);
        composed.AddRange(added);
        return composed;
    }

    private enum PackageChoice { Add, HostWins, ReplaceHost }

    /// <summary>The run-time binding rule (<c>PackageAssemblyProbe.TryUnifyWithHost</c>) applied to a
    /// name both sides hold. An identity that cannot be read on either side keeps the host's.</summary>
    private static PackageChoice Choose(MetadataReference hostReference, string packagePath)
    {
        var hostPath = hostReference is PortableExecutableReference { FilePath: { Length: > 0 } file }
            ? file
            : null;
        if (hostPath is null || AssemblyNameOf(hostPath) is not { } host || AssemblyNameOf(packagePath) is not { } package)
            return PackageChoice.HostWins;
        if (!MeshWeaver.NuGet.PackageAssemblyProbe.SameIdentityModuloVersion(package, host)
            || !MeshWeaver.NuGet.PackageAssemblyProbe.SameIdentityModuloVersion(host, package))
            return PackageChoice.Add;
        return package.Version is { } wanted && host.Version is { } have && have < wanted
            ? PackageChoice.ReplaceHost
            : PackageChoice.HostWins;
    }

    private static System.Reflection.AssemblyName? AssemblyNameOf(string path)
    {
        try
        {
            return System.Reflection.AssemblyName.GetAssemblyName(path);
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException
                                      or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The assembly simple name a host reference provides: its file name without extension (a
    /// TPA, module or NuGet assembly is named after its assembly, which is also how the run-time
    /// probe finds it — <c>{dir}/{name}.dll</c>); <c>null</c> for an in-memory reference.
    /// </summary>
    private static string? SimpleNameOf(MetadataReference reference)
    {
        var path = reference is PortableExecutableReference { FilePath: { Length: > 0 } file }
            ? file
            : reference.Display;
        return string.IsNullOrEmpty(path) ? null : Path.GetFileNameWithoutExtension(path);
    }

    /// <summary>
    /// Composes <see cref="Default"/> with THIS mesh's installed module assemblies
    /// (<c>Modules:Assemblies</c> → <see cref="InstalledModuleAssembly"/>, design #1644). A module
    /// published into <c>modules/&lt;name&gt;/</c> is not in <c>TRUSTED_PLATFORM_ASSEMBLIES</c>,
    /// so in-mesh consumers (e.g. a scope class using a map control) would silently lose it
    /// without this. Deduped by path — a module still riding the app closure appears in both
    /// sets. Returns <see cref="Default"/> itself for a zero-module mesh.
    /// </summary>
    public static IReadOnlyList<MetadataReference> ComposeWithModules(
        IReadOnlyList<InstalledModuleAssembly> modules)
    {
        if (modules.Count == 0)
            return Default;
        var seen = new HashSet<string>(
            Default.Select(r => r.Display ?? string.Empty),
            StringComparer.OrdinalIgnoreCase);
        var composed = new List<MetadataReference>(Default);
        foreach (var module in modules)
        {
            var location = module.Assembly.Location;
            if (!string.IsNullOrEmpty(location) && File.Exists(location) && seen.Add(location))
                composed.Add(MetadataReference.CreateFromFile(location));
        }
        return composed;
    }
}
