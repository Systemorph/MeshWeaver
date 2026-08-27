namespace MeshWeaver.PluginCatalog;

/// <summary>
/// Pins a store-landed module generation to PROCESS-LOCAL storage before it is loaded (#2509).
///
/// <para>🚨 <b>Why loading from the shared volume is not safe for a running process.</b> A landed
/// generation lives under the deployment's shared <c>modules/</c> tree (<c>/data</c>, mounted by
/// every replica). The process loads the module's ENTRY assembly at boot, but its dependency DLLs
/// load LAZILY — first use, hours later. In between, the generation can legitimately stop being
/// referenced: an auto-update lands a NEWER generation and moves the activation pointer, this pod
/// keeps running the old one until its restart, and any SIBLING pod's boot GC then reclaims the
/// old directory — correctly, by the sidecar's lights. The first lazy load after that is
/// <c>FileNotFoundException: Could not load file or assembly 'OpenAI'</c> with nothing connecting
/// it to a GC pass on another pod two hours earlier — the 2026-08-27 outage, verbatim. Roslyn
/// content compiles (<c>CompileReferences.ComposeWithModules</c>) re-read module files by path the
/// same way and had the same exposure.</para>
///
/// <para>The root cause is storage lifetime: resources with PROCESS lifetime were being served
/// from a directory with REFERENCE-SET lifetime. The fix is to give the loaded bytes process-local
/// storage — each boot copies the generation directory it is about to load into a per-process
/// folder under the OS temp path and loads from there. No lease files, no cross-replica
/// coordination, no GC coupling: the shared tree stays a transport, and reclaiming it can no
/// longer reach into a running process. The copy is a few MB per module, on storage whose
/// lifecycle already matches the process (a container's tmp dies with the pod).</para>
///
/// <para>🚨 <b>The pin is protection, not a gate.</b> A boot that cannot copy (temp full,
/// read-only tmp) warns loudly and falls back to the shared path — exactly today's behavior. A
/// portal that will not start cannot be given the fix for what is wrong with it.</para>
/// </summary>
public static class ModuleGenerationPin
{
    /// <summary>Where pinned copies live by default: under the OS temp path, whose lifecycle
    /// matches the process's environment (a container's tmp dies with the pod; a dev machine's is
    /// OS-managed).</summary>
    public static string DefaultPinRoot => Path.Combine(Path.GetTempPath(), "meshweaver-pinned-modules");

    /// <summary>
    /// Copies the generation directory <paramref name="entry"/> points at into a process-local
    /// folder and returns the entry DLL path INSIDE the copy — the path boot hands to
    /// <c>MeshBuilder.InstallAssemblies</c>. Dependency DLLs, static assets, everything in the
    /// generation travels: lazy loads and Roslyn metadata reads must all resolve from the pinned
    /// copy, or the protection has a hole exactly where the outage was.
    ///
    /// <para>On ANY failure it warns through <paramref name="onWarn"/> and returns the SHARED
    /// landed path (<see cref="ModuleActivationBoot.LandedDllPath"/>) — the pre-#2509 behavior,
    /// degraded but bootable.</para>
    /// </summary>
    /// <param name="moduleRoot">The deployment root the shared <c>modules/</c> tree lives under —
    /// the same root <see cref="ModuleLandingService"/> writes.</param>
    /// <param name="entry">The activation entry naming the generation to pin.</param>
    /// <param name="pinRoot">Test seam for the process-local root; production uses
    /// <see cref="DefaultPinRoot"/>.</param>
    /// <param name="onWarn">The loud channel for a failed pin — pre-DI boot passes stderr.</param>
    public static string PinnedLoadPath(
        string moduleRoot,
        ModuleActivationEntry entry,
        string? pinRoot = null,
        Action<string>? onWarn = null)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var sharedDirectory = ModuleLandingService.ModuleDirectoryFor(moduleRoot, entry.Name, entry);
        var sharedDll = ModuleActivationBoot.LandedDllPath(moduleRoot, entry);
        try
        {
            var leaf = Path.GetFileName(sharedDirectory);
            // Unique per process AND per call: two hosts on one machine (a dev portal beside a
            // test run) or two boots in one long-lived process must never share or overwrite a
            // pinned tree an earlier load is still using.
            var pinnedDirectory = Path.Combine(
                pinRoot ?? DefaultPinRoot,
                $"{Environment.ProcessId}-{Guid.NewGuid():N}"[..(Environment.ProcessId.ToString().Length + 9)],
                leaf);
            CopyTree(sharedDirectory, pinnedDirectory);
            var pinnedDll = Path.Combine(pinnedDirectory, entry.Name + ".dll");
            if (!File.Exists(pinnedDll))
                throw new FileNotFoundException(
                    $"the copied generation has no entry DLL at '{pinnedDll}' — the shared "
                    + "directory is incomplete", pinnedDll);
            return pinnedDll;
        }
        catch (Exception ex)
        {
            onWarn?.Invoke(
                $"module '{entry.Name}': could not pin generation '{entry.Directory ?? entry.Name}' "
                + $"to process-local storage ({ex.GetType().Name}: {ex.Message}) — loading from the "
                + "shared modules/ tree instead. A GC pass on another replica can reclaim that "
                + "directory while this process still lazily loads from it (#2509); restart to "
                + "re-attempt the pin.");
            return sharedDll;
        }
    }

    private static void CopyTree(string source, string destination)
    {
        // Reparse points (symlinks/junctions) are never followed: a landed generation is plain
        // files by construction, so a link inside one is corruption or crafting, and following it
        // would pull arbitrary filesystem content into the pin (huge copies, unintended reads).
        // Parent directories are created per file, so no directory pass traverses links either.
        Directory.CreateDirectory(destination);
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };
        foreach (var file in Directory.EnumerateFiles(source, "*", options))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }
}
