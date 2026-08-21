using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Issue #1728 — a runtime-loaded module could not ship NATIVE assets.
///
/// <para>A module is loaded with <c>Assembly.LoadFrom</c>, which never consults the module's own
/// <c>deps.json</c>, so nothing probed <c>modules/&lt;Name&gt;/runtimes/&lt;rid&gt;/native/</c> and the publish
/// lane deleted the tree as unreachable dead weight. That is what keeps SkiaSharp (and therefore
/// <c>OgCardRenderer</c>) inside the portal image closure.</para>
///
/// <para>These pin the resolver that closes it. The last test is the one that matters: a REAL
/// assembly, loaded from a REAL <c>modules/&lt;Name&gt;/</c> folder, resolving a REAL native library that
/// exists ONLY under that folder's RID tree — through the runtime's own P/Invoke resolution path
/// (<see cref="NativeLibrary.Load(string, Assembly, DllImportSearchPath?)"/>), not through a helper
/// the test calls directly. Against <c>main</c> that throws <see cref="DllNotFoundException"/>.</para>
/// </summary>
public class ModuleNativeAssetTest
{
    [Fact]
    public void ModuleDirectory_IsRecognisedOnlyDirectlyUnderModules()
    {
        var app = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Assert.True(ModuleNativeAssets.IsModuleDirectory(Path.Combine(app, "modules", "MeshWeaver.OgCard")));
        // A trailing separator must not change the verdict — Path.GetDirectoryName would otherwise
        // return the folder itself and the parent test would read "modules" as the module name.
        Assert.True(ModuleNativeAssets.IsModuleDirectory(
            Path.Combine(app, "modules", "MeshWeaver.OgCard") + Path.DirectorySeparatorChar));

        // The app root itself, a look-alike folder, and the modules root are all NOT modules.
        Assert.False(ModuleNativeAssets.IsModuleDirectory(app));
        Assert.False(ModuleNativeAssets.IsModuleDirectory(Path.Combine(app, "notmodules", "X")));
        Assert.False(ModuleNativeAssets.IsModuleDirectory(Path.Combine(app, "modules")));
        Assert.False(ModuleNativeAssets.IsModuleDirectory(null));
        Assert.False(ModuleNativeAssets.IsModuleDirectory(string.Empty));

        // An assembly that did NOT come from a module folder yields no module directory, so the
        // hook returns immediately for every platform assembly on every failed native load.
        Assert.Null(ModuleNativeAssets.ModuleDirectoryOf(typeof(ModuleNativeAssetTest).Assembly));
    }

    [Fact]
    public void RuntimeIdentifierCandidates_AddThePortableForm_AndNeverCrossTheLibcBoundary()
    {
        Assert.Equal(["linux-x64"], ModuleNativeAssets.RuntimeIdentifierCandidates("linux-x64"));

        // A versioned RID (what older runtimes reported) falls back to the portable form native
        // packages actually publish.
        Assert.Equal(["osx.14-arm64", "osx-arm64"],
            ModuleNativeAssets.RuntimeIdentifierCandidates("osx.14-arm64"));

        // 🚨 musl must NEVER fall back to glibc: the binaries are not interchangeable, so a graph
        // walk between them would load something that cannot run — a crash at the first P/Invoke
        // instead of a clean "not found" that leaves the runtime's own message intact.
        Assert.Equal(["linux-musl-x64"], ModuleNativeAssets.RuntimeIdentifierCandidates("linux-musl-x64"));

        Assert.Empty(ModuleNativeAssets.RuntimeIdentifierCandidates(""));
    }

    [Fact]
    public void FileNameCandidates_CoverTheDecoratedFormsWithoutDoublingAnExtension()
    {
        var extension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib" : ".so";

        // SkiaSharp declares `libSkiaSharp` and ships `libSkiaSharp.so` — both halves are needed.
        var candidates = ModuleNativeAssets.FileNameCandidates("libSkiaSharp");
        Assert.Equal("libSkiaSharp", candidates[0]);
        Assert.Contains("libSkiaSharp" + extension, candidates);

        // A name that already carries the extension is never decorated with a second one.
        Assert.DoesNotContain(
            ModuleNativeAssets.FileNameCandidates("libSkiaSharp" + extension),
            n => n.EndsWith(extension + extension, StringComparison.Ordinal));

        Assert.Empty(ModuleNativeAssets.FileNameCandidates(" "));
    }

    [Fact]
    public void CandidatePaths_ProbeTheRidTreeBeforeTheFlatFolder()
    {
        var module = Path.Combine(AppContext.BaseDirectory, "modules", "Probe");
        var paths = ModuleNativeAssets.CandidatePaths(module, "libSkiaSharp", "linux-x64");

        var ridTree = Path.Combine(module, "runtimes", "linux-x64", "native");
        var firstFlat = paths.ToList().FindIndex(p => Path.GetDirectoryName(p) == module);
        var lastRid = paths.ToList().FindLastIndex(p => Path.GetDirectoryName(p) == ridTree);

        Assert.True(lastRid >= 0, "the RID tree must be probed");
        Assert.True(firstFlat >= 0, "flat placement must remain supported");
        // The portable publish lays the tree out; flat placement is the landing step's option, so
        // it must not shadow a RID-correct payload.
        Assert.True(lastRid < firstFlat, "the RID tree must be probed BEFORE the flat folder");
    }

    /// <summary>
    /// The end-to-end claim: an assembly in <c>modules/&lt;Name&gt;/</c> resolves a native library that
    /// exists ONLY under that module's <c>runtimes/&lt;rid&gt;/native/</c>, through the runtime's own
    /// P/Invoke resolution. The library is a genuine one copied out of the shared framework under a
    /// new name, so nothing on the machine can resolve it by any other route — if it loads, it
    /// loaded from the module folder.
    /// </summary>
    [Fact]
    public void AnAssemblyInAModuleFolder_ResolvesANativeLibraryFromItsOwnRidTree()
    {
        ModuleNativeAssets.EnsureRegistered();

        var source = FindShippedNativeLibrary();
        var extension = Path.GetExtension(source);
        var probeName = $"meshweaver_probe_{Guid.NewGuid():N}";
        var moduleName = $"NativeProbe{Guid.NewGuid():N}";
        var moduleDirectory = Path.Combine(AppContext.BaseDirectory, "modules", moduleName);
        var nativeDirectory = Path.Combine(
            moduleDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        Directory.CreateDirectory(nativeDirectory);

        try
        {
            var prefix = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? string.Empty : "lib";
            File.Copy(source, Path.Combine(nativeDirectory, prefix + probeName + extension));

            var assemblyPath = Path.Combine(moduleDirectory, moduleName + ".dll");
            EmitProbeAssembly(moduleName, assemblyPath);
            var moduleAssembly = Assembly.LoadFrom(assemblyPath);

            // Sanity: the assembly really is the one in the module folder (LoadFrom returns an
            // already-loaded assembly on an identity match, which would make this prove nothing).
            Assert.Equal(moduleDirectory.TrimEnd(Path.DirectorySeparatorChar),
                ModuleNativeAssets.ModuleDirectoryOf(moduleAssembly)?.TrimEnd(Path.DirectorySeparatorChar));

            // Nothing outside the module folder can resolve this name: an assembly loaded from the
            // app root must still fail, or the test would pass on the machine's own search path.
            Assert.Throws<DllNotFoundException>(
                () => NativeLibrary.Load(probeName, typeof(ModuleNativeAssetTest).Assembly, null));

            // THE claim — this is what throws DllNotFoundException against main.
            var handle = NativeLibrary.Load(probeName, moduleAssembly, null);
            Assert.NotEqual(IntPtr.Zero, handle);
            NativeLibrary.Free(handle);
        }
        finally
        {
            // The emitted assembly stays loaded (non-collectible), so the DLL may be locked on
            // Windows; the directory tree is per-test and disposable either way.
            try { Directory.Delete(moduleDirectory, recursive: true); } catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A real, loadable native library from the shared framework — the payload the module folder
    /// will carry under a new name. Asserted rather than skipped: a test that quietly does nothing
    /// when it cannot find its fixture is not a test (AGENTS.md).
    /// </summary>
    private static string FindShippedNativeLibrary()
    {
        var runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var extension = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? ".dylib" : ".so";
        var candidates = Directory
            .EnumerateFiles(runtimeDirectory, "*Native*" + extension)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        foreach (var candidate in candidates)
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                NativeLibrary.Free(handle);
                return candidate;
            }
        Assert.Fail(
            $"no loadable native library found in '{runtimeDirectory}' "
            + $"(looked for *Native*{extension}; {candidates.Count} candidate(s))");
        return string.Empty;
    }

    /// <summary>
    /// The smallest real assembly that can be loaded from a path: emitted to disk so its
    /// <see cref="Assembly.Location"/> is the module folder, which is what the resolver reads.
    /// </summary>
    private static void EmitProbeAssembly(string name, string path)
    {
        var builder = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        builder.DefineDynamicModule(name)
            .DefineType("Probe", TypeAttributes.Public | TypeAttributes.Class)
            .CreateType();
        builder.Save(path);
    }
}
