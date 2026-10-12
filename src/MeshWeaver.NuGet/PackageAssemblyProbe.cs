using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.NuGet;

/// <summary>
/// Runtime binding of NuGet package assemblies for a load context that serves code compiled
/// against <c>#r "nuget:…"</c> packages (a NodeType's collectible context, a kernel session).
/// Three rules, in this order:
/// <list type="number">
///   <item><b>Host unification</b> — a dependency the HOST already ships (its trusted-platform list
///     or an assembly already loaded in <see cref="AssemblyLoadContext.Default"/>) at an
///     equal-or-newer version binds to the host's copy. A package that pins System.Text.Json 8 or
///     Microsoft.Extensions.DependencyInjection.Abstractions 8 must not load a second, older copy
///     beside the host's 10: the two copies' types are different types, and every
///     <c>JsonSerializerOptions</c> or <c>IServiceProvider</c> crossing the boundary breaks.</item>
///   <item><b>Probing</b> — otherwise the package's own copy, from the managed probing directories
///     (which hold the RID-specific runtime group, see <see cref="RuntimeAssetSelector"/>).</item>
///   <item><b>Native</b> — <c>DllImport</c>s resolve from the <c>runtimes/&lt;rid&gt;/native/</c>
///     directories among the probing directories.</item>
/// </list>
/// Holds no state: every answer is computed from the arguments and the running process.
/// </summary>
public static class PackageAssemblyProbe
{
    /// <summary>
    /// True for a probing directory that holds native libraries (<c>…/runtimes/&lt;rid&gt;/native</c>);
    /// those are never probed for managed assemblies.
    /// </summary>
    public static bool IsNativeDirectory(string directory)
        => string.Equals(
            Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            "native", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The identity of <paramref name="simpleName"/> the host serves — an assembly already loaded in
    /// the default context, else the trusted-platform-assembly list — or <c>null</c> when the host
    /// does not ship it. For an application the TPA list is built from its <c>.deps.json</c>, so it
    /// names every dependency the host ships (Azure.Core, Microsoft.Extensions.*) whether or not it
    /// has been loaded yet.
    /// </summary>
    public static AssemblyName? HostIdentityOf(string simpleName)
    {
        foreach (var loaded in AssemblyLoadContext.Default.Assemblies)
        {
            var n = loaded.GetName();
            if (string.Equals(n.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                return n;
        }
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is not string tpa) return null;
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.Equals(Path.GetFileNameWithoutExtension(path), simpleName, StringComparison.OrdinalIgnoreCase))
                continue;
            try { return AssemblyName.GetAssemblyName(path); }
            catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>The version of <paramref name="simpleName"/> the host serves, or <c>null</c>.</summary>
    public static Version? HostVersionOf(string simpleName) => HostIdentityOf(simpleName)?.Version;

    /// <summary>
    /// True when <paramref name="host"/> is the SAME assembly identity as <paramref name="requested"/>
    /// apart from the version: same culture, and — when the reference is strong-named — the same
    /// public key token. A same-named assembly of another publisher is a different assembly.
    /// </summary>
    public static bool SameIdentityModuloVersion(AssemblyName requested, AssemblyName host)
    {
        if (!string.Equals(requested.CultureName ?? string.Empty, host.CultureName ?? string.Empty,
                StringComparison.OrdinalIgnoreCase))
            return false;
        var wanted = requested.GetPublicKeyToken() ?? [];
        var have = host.GetPublicKeyToken() ?? [];
        return wanted.Length == 0 || wanted.AsSpan().SequenceEqual(have);
    }

    /// <summary>
    /// Binds <paramref name="requested"/> to the host's copy when the host ships the same identity
    /// at an equal-or-newer version; <c>null</c> when it does not (the caller probes the package
    /// next). A host copy OLDER than requested is not unified — the package's own copy is the only
    /// one that satisfies the reference — and that is logged as a warning, since its types then exist
    /// twice.
    /// </summary>
    public static Assembly? TryUnifyWithHost(AssemblyName requested, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(requested.Name)) return null;
        var hostName = HostIdentityOf(requested.Name);
        if (hostName?.Version is not { } host) return null;
        if (!SameIdentityModuloVersion(requested, hostName))
        {
            logger?.LogDebug(
                "NuGet dependency {Assembly} differs from the host's in culture or public key; not unified",
                requested.FullName);
            return null;
        }
        if (requested.Version is { } wanted && host < wanted)
        {
            logger?.LogWarning(
                "NuGet dependency {Assembly} {Requested} is newer than the host's {Host}: loading the package's copy "
                + "in isolation — its types are distinct from the host's",
                requested.Name, wanted, host);
            return null;
        }
        try
        {
            // Already loaded by the host: that very Assembly (it may have been loaded by path, which
            // a by-name bind could not find again). Otherwise the default context's TPA bind.
            var bound = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a =>
                    string.Equals(a.GetName().Name, requested.Name, StringComparison.OrdinalIgnoreCase))
                ?? AssemblyLoadContext.Default.LoadFromAssemblyName(hostName);
            logger?.LogInformation(
                "Unified NuGet dependency {Assembly} {Requested} with the host's {Host}",
                requested.Name, requested.Version?.ToString() ?? "(any)", bound.GetName().Version);
            return bound;
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            logger?.LogDebug(e, "Host unification of {Assembly} failed; probing the package", requested.Name);
            return null;
        }
    }

    /// <summary>
    /// Every <c>{dir}/{name}.dll</c> among the MANAGED <paramref name="probingDirectories"/>
    /// (native directories skipped), in probing order — a caller tries the next when one fails to
    /// load. A satellite reference (<paramref name="cultureName"/> set, e.g.
    /// <c>Humanizer.resources, Culture=de</c>) is looked up in the culture subfolder,
    /// <c>{dir}/{culture}/{name}.dll</c>, where packages ship their resources.
    /// </summary>
    public static IEnumerable<string> FindManagedCandidates(
        string simpleName, IEnumerable<string> probingDirectories, string? cultureName = null)
        => probingDirectories
            .Where(dir => !IsNativeDirectory(dir))
            .Select(dir => string.IsNullOrEmpty(cultureName)
                ? Path.Combine(dir, simpleName + ".dll")
                : Path.Combine(dir, cultureName, simpleName + ".dll"))
            .Where(File.Exists);

    /// <summary>The first of <see cref="FindManagedCandidates"/>, or <c>null</c>.</summary>
    public static string? FindManaged(string simpleName, IEnumerable<string> probingDirectories)
        => FindManagedCandidates(simpleName, probingDirectories).FirstOrDefault();

    /// <summary>
    /// Host unification, then the package probe: the assembly <paramref name="context"/> should
    /// bind for <paramref name="requested"/>, or <c>null</c> to fall through to the context's own
    /// fallback.
    /// </summary>
    public static Assembly? Resolve(
        AssemblyLoadContext context, AssemblyName requested, IReadOnlyCollection<string> probingDirectories,
        ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(requested.Name) || probingDirectories.Count == 0) return null;
        var candidates = FindManagedCandidates(requested.Name, probingDirectories, requested.CultureName).ToImmutableArray();
        // Only a name the PACKAGES supply is this probe's business; anything else keeps the
        // context's own resolution order untouched.
        if (candidates.IsEmpty) return null;
        if (TryUnifyWithHost(requested, logger) is { } host) return host;
        foreach (var path in candidates)
        {
            try
            {
                return context.LoadFromAssemblyPath(path);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Probing load failed for {Candidate}; trying the next", path);
            }
        }
        return null;
    }

    /// <summary>
    /// The candidate file names for a <c>DllImport</c> of <paramref name="name"/> on the running OS:
    /// the name as given, then with the platform's prefix/extension (<c>libX.so</c>, <c>libX.dylib</c>,
    /// <c>X.dll</c>).
    /// </summary>
    public static IEnumerable<string> NativeFileNames(string name)
    {
        yield return name;
        if (OperatingSystem.IsWindows())
        {
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) yield return name + ".dll";
            yield break;
        }
        var ext = OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst() ? ".dylib" : ".so";
        var hasExt = name.EndsWith(ext, StringComparison.OrdinalIgnoreCase);
        if (!hasExt) yield return name + ext;
        if (!name.StartsWith("lib", StringComparison.Ordinal))
        {
            yield return "lib" + name;
            if (!hasExt) yield return "lib" + name + ext;
        }
    }

    /// <summary>
    /// Every native library file for <paramref name="name"/> among the native probing directories, in
    /// probing order and, within a directory, in <see cref="NativeFileNames"/> order.
    /// </summary>
    public static IEnumerable<string> FindNativeCandidates(string name, IEnumerable<string> probingDirectories)
        => probingDirectories
            .Where(IsNativeDirectory)
            .SelectMany(dir => NativeFileNames(name).Select(file => Path.Combine(dir, file)))
            .Where(File.Exists);

    /// <summary>
    /// The probing directories a native import from <paramref name="requestingAssemblyLocation"/>
    /// may search, for a context that accumulates several resolutions (a kernel session's
    /// submissions): only the FIRST closure — one per <see cref="ResolvedPackageSet.ProbingDirectories"/>
    /// — whose managed directories contain the requesting assembly. A native library that a
    /// different resolution brought (another version under the same import name) is never offered.
    /// Empty when the assembly came from none of them.
    /// </summary>
    public static ImmutableArray<string> NativeSearchDirectoriesFor(
        string? requestingAssemblyLocation, IEnumerable<ImmutableArray<string>> closures)
    {
        if (string.IsNullOrEmpty(requestingAssemblyLocation)) return [];
        var origin = Path.GetDirectoryName(requestingAssemblyLocation);
        if (string.IsNullOrEmpty(origin)) return [];
        // The FIRST closure that holds it, and only that one: the default context binds a managed
        // path once, to whichever resolution asked first, so that resolution owns the assembly — and
        // its native dependencies with it. Closures overlap routinely (each carries its whole
        // transitive graph); merging them would offer one resolution's native version to another.
        return closures.FirstOrDefault(closure => closure.Contains(origin, StringComparer.OrdinalIgnoreCase)) is
            { IsDefaultOrEmpty: false } owner
            ? owner
            : [];
    }

    /// <summary>The first of <see cref="FindNativeCandidates"/>, or <c>null</c>.</summary>
    public static string? FindNative(string name, IEnumerable<string> probingDirectories)
        => FindNativeCandidates(name, probingDirectories).FirstOrDefault();

    /// <summary>
    /// Loads the native library <paramref name="name"/> from the package native directories, trying
    /// each candidate until one loads; <see cref="IntPtr.Zero"/> when none does (the runtime's
    /// default search then applies).
    /// </summary>
    /// <param name="name">The <c>DllImport</c> library name.</param>
    /// <param name="probingDirectories">The probing directories; only the native ones are read.</param>
    /// <param name="logger">Receives a warning per candidate that failed to load.</param>
    /// <param name="load">How to load a path. A COLLECTIBLE load context passes its own
    /// <c>LoadUnmanagedDllFromPath</c>, so the handle belongs to the context and is released with
    /// it; the default, <see cref="NativeLibrary.Load(string)"/>, yields a process-owned handle —
    /// right only for the default context.</param>
    public static IntPtr ResolveUnmanaged(
        string name, IEnumerable<string> probingDirectories, ILogger? logger = null, Func<string, IntPtr>? load = null)
    {
        load ??= NativeLibrary.Load;
        foreach (var path in FindNativeCandidates(name, probingDirectories))
        {
            try
            {
                var handle = load(path);
                if (handle != IntPtr.Zero) return handle;
            }
            catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
            {
                logger?.LogWarning(ex, "Native library {Library} from {Path} failed to load; trying the next", name, path);
            }
        }
        return IntPtr.Zero;
    }
}
