using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace MeshWeaver.Mesh;

/// <summary>
/// Native-asset resolution for RUNTIME-LOADED MODULES (issue #1728).
///
/// <para><b>The gap this closes.</b> A module ships as <c>modules/&lt;Name&gt;/</c> beside the app and
/// is loaded with <see cref="Assembly.LoadFrom(string)"/>, which never consults the module's own
/// <c>deps.json</c> — the runtime's fallback probe is the module's FLAT folder only. So a
/// <c>runtimes/&lt;rid&gt;/native/</c> tree under a module folder was unreachable BY CONSTRUCTION, which
/// is why <c>MeshModulesPublish.targets</c> deleted it outright. That made "a module cannot ship
/// non-IL assets" a hard capability gap: it is what keeps the SkiaSharp share-card renderer
/// (<c>OgCardRenderer</c>, 5–15 MB of <c>libSkiaSharp.*</c> per RID) inside the portal image closure
/// rather than in <c>MeshWeaver.OgCard</c>.</para>
///
/// <para><b>Why resolution at LOAD time, not placement at PUBLISH time.</b> Every module MSBuild
/// invocation strips RID globals by design (#1675/#1676 — a flipped module is outside the host's
/// restore graph, so a RID flowing in from CD's per-arch publish fails <c>NETSDK1047</c>; the
/// 2026-08-16 CD outage). A module publish is therefore ALWAYS portable, so the RID is not known
/// when the bits are laid out and natives always land under <c>runtimes/</c>. Only the host knows
/// its own RID, and it knows it here.</para>
///
/// <para><b>No state, by construction.</b> The hook derives everything from its arguments: the
/// requesting assembly's own location IS the module folder, so there is no registry of module
/// directories to keep, invalidate, or leak across meshes (AGENTS.md — no static collections).
/// <see cref="AssemblyLoadContext.ResolvingUnmanagedDll"/> fires ONLY after the runtime's own
/// probing has already failed, so this can never shadow a resolution that works today.</para>
/// </summary>
public static class ModuleNativeAssets
{
    /// <summary>The folder name the modules lane publishes into, beside the app.</summary>
    public const string ModulesFolderName = "modules";

    /// <summary>
    /// Subscribes the process-wide resolver exactly once. A <c>static readonly</c> initialised by
    /// the type initialiser: run-once, never written afterwards, and holding no collection — the
    /// shape AGENTS.md permits. The subscription's scope genuinely IS the process, because
    /// <see cref="AssemblyLoadContext.Default"/> is; a mesh-scoped singleton could not unsubscribe
    /// a native library that is already loaded anyway.
    /// </summary>
    private static readonly bool Registered = Register();

    private static bool Register()
    {
        AssemblyLoadContext.Default.ResolvingUnmanagedDll += ResolveForModule;
        return true;
    }

    /// <summary>
    /// Ensures the resolver is subscribed. Idempotent — the work happens in the type initialiser,
    /// so calling this from every <c>InstallAssemblies</c> stacks nothing.
    /// </summary>
    public static void EnsureRegistered() => _ = Registered;

    private static IntPtr ResolveForModule(Assembly requesting, string libraryName)
    {
        var moduleDirectory = ModuleDirectoryOf(requesting);
        if (moduleDirectory is null)
            return IntPtr.Zero;

        foreach (var candidate in CandidatePaths(moduleDirectory, libraryName, RuntimeInformation.RuntimeIdentifier))
            if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                return handle;

        return IntPtr.Zero;
    }

    /// <summary>
    /// The module folder an assembly was loaded from, or null when it did not come from one.
    ///
    /// <para>The test is structural and deliberately narrow: the assembly sits DIRECTLY in
    /// <c>…/modules/&lt;Name&gt;/</c>. That is exactly what both layout lanes produce — the module's own
    /// DLL and its pruned private closure land flat in that folder — and it is what makes a
    /// dependency such as <c>SkiaSharp.dll</c> (which declares the P/Invokes, not the module
    /// assembly) resolve its natives too. Anything deeper, or an assembly with no location
    /// (dynamic, single-file, collectible), is not a module and is left to the runtime.</para>
    /// </summary>
    /// <param name="assembly">The assembly whose native dependency failed to resolve.</param>
    /// <returns>The absolute module folder, or <c>null</c>.</returns>
    public static string? ModuleDirectoryOf(Assembly assembly)
    {
        var location = assembly.Location;
        if (string.IsNullOrEmpty(location))
            return null;
        var directory = Path.GetDirectoryName(location);
        return IsModuleDirectory(directory) ? directory : null;
    }

    /// <summary>
    /// Whether a directory is a module folder — i.e. its parent is named <c>modules</c>.
    /// </summary>
    /// <param name="directory">The candidate directory.</param>
    /// <returns><c>true</c> when the directory is <c>…/modules/&lt;Name&gt;</c>.</returns>
    public static bool IsModuleDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
            return false;
        var parent = Path.GetFileName(Path.GetDirectoryName(directory.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        return string.Equals(parent, ModulesFolderName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Every path this resolver will try for <paramref name="libraryName"/>, in order — the pure
    /// half, so the ordering and the RID policy are unit-testable without loading anything.
    ///
    /// <para>RID candidates are the running RID and, when it carries an OS VERSION
    /// (<c>osx.14-arm64</c> — the shape older runtimes reported), the portable form
    /// (<c>osx-arm64</c>) that native packages actually publish. 🚨 There is deliberately NO wider
    /// fallback: <c>linux-musl-x64</c> and <c>linux-x64</c> are different C libraries, so a graph
    /// walk between them would load a binary that cannot run — a crash at first P/Invoke rather
    /// than a clean "not found" that leaves the runtime's own error message intact.</para>
    ///
    /// <para>Flat placement (the library directly in the module folder) is tried last, so the
    /// landing step's option of laying a single RID's payload flat keeps working without the tree.</para>
    /// </summary>
    /// <param name="moduleDirectory">The module folder (<c>…/modules/&lt;Name&gt;</c>).</param>
    /// <param name="libraryName">The library name as the P/Invoke declared it.</param>
    /// <param name="runtimeIdentifier">The running RID.</param>
    /// <returns>Candidate absolute paths, most specific first.</returns>
    public static IReadOnlyList<string> CandidatePaths(
        string moduleDirectory, string libraryName, string runtimeIdentifier)
    {
        var names = FileNameCandidates(libraryName);
        var paths = new List<string>();
        foreach (var rid in RuntimeIdentifierCandidates(runtimeIdentifier))
        foreach (var name in names)
            paths.Add(Path.Combine(moduleDirectory, "runtimes", rid, "native", name));
        foreach (var name in names)
            paths.Add(Path.Combine(moduleDirectory, name));
        return paths;
    }

    /// <summary>
    /// The RIDs to probe, most specific first: the running one, plus its OS-version-stripped
    /// portable form when they differ.
    /// </summary>
    /// <param name="runtimeIdentifier">The running RID.</param>
    /// <returns>The RID probe order.</returns>
    public static IReadOnlyList<string> RuntimeIdentifierCandidates(string runtimeIdentifier)
    {
        if (string.IsNullOrWhiteSpace(runtimeIdentifier))
            return [];
        var portable = PortableForm(runtimeIdentifier);
        return string.Equals(portable, runtimeIdentifier, StringComparison.OrdinalIgnoreCase)
            ? [runtimeIdentifier]
            : [runtimeIdentifier, portable];
    }

    private static string PortableForm(string runtimeIdentifier)
    {
        // `osx.14-arm64` → `osx-arm64`; `linux-musl-x64` (no dot) is returned unchanged.
        var dash = runtimeIdentifier.IndexOf('-');
        var head = dash < 0 ? runtimeIdentifier : runtimeIdentifier[..dash];
        var dot = head.IndexOf('.');
        if (dot < 0)
            return runtimeIdentifier;
        return dash < 0 ? head[..dot] : string.Concat(head.AsSpan(0, dot), runtimeIdentifier.AsSpan(dash));
    }

    /// <summary>
    /// The file names a P/Invoke name can take on this platform, most specific first: the name as
    /// written (a declaration may already carry its extension), then the platform-decorated forms
    /// the runtime itself tries — <c>&lt;name&gt;.so</c>/<c>lib&lt;name&gt;.so</c> and their macOS and
    /// Windows equivalents. <c>SkiaSharp</c> declares <c>libSkiaSharp</c> and ships
    /// <c>libSkiaSharp.so</c>, so both halves matter.
    /// </summary>
    /// <param name="libraryName">The library name as the P/Invoke declared it.</param>
    /// <returns>Candidate file names, most specific first.</returns>
    public static IReadOnlyList<string> FileNameCandidates(string libraryName)
    {
        if (string.IsNullOrWhiteSpace(libraryName))
            return [];
        var (prefix, extension) = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? (string.Empty, ".dll")
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? ("lib", ".dylib")
                : ("lib", ".so");

        var names = new List<string> { libraryName };
        void Add(string candidate)
        {
            if (!names.Contains(candidate, StringComparer.Ordinal))
                names.Add(candidate);
        }

        var hasExtension = libraryName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
        if (!hasExtension)
            Add(libraryName + extension);
        if (prefix.Length > 0 && !libraryName.StartsWith(prefix, StringComparison.Ordinal))
            Add(prefix + libraryName + (hasExtension ? string.Empty : extension));
        return names;
    }
}
