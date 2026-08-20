using System.IO;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What a REGISTRY instance can serve as a module bundle (#1664 Slice C, the serving half): the
/// files under its own <c>modules/&lt;name&gt;/</c> — the very bytes this deployment loads and runs,
/// which is the same philosophy the NodeType bundle lane established ("the inputs ARE the storage").
/// A module is servable exactly when this deployment's own boot would load it: not uninstalled,
/// its declared platform FLOOR satisfied here, its entry DLL present. The MVID a landing recorded
/// is diagnostic and never withholds a serve — modules bind by simple name, and a consumer's own
/// floor gate (against the floor this serve surfaces) is what protects IT.
///
/// <para>Pure decision + one directory listing — no mesh, no HTTP — so the serve rules are
/// pinnable with a temp directory.</para>
/// </summary>
public static class ModuleBundleSource
{
    /// <summary>
    /// The module files this deployment may serve for <paramref name="moduleName"/>, or a decline
    /// reason. Exactly one of the two is meaningful: a non-null <c>DeclineReason</c> means the
    /// bundle carries no module section (which a consumer treats as "nothing to land").
    /// </summary>
    /// <param name="baseDirectory">The deployment root the <c>modules/</c> tree lives under.</param>
    /// <param name="moduleName">The module's entry-assembly name without extension.</param>
    /// <param name="activation">The deployment's activation sidecar list (empty for a module that
    /// ships with the image — image modules have no sidecar entry).</param>
    /// <param name="platformGate">Returns WHY a recorded platform FLOOR is not satisfied by THIS
    /// process, or null when it is (an absent floor is always satisfied) — production passes
    /// <see cref="ModulePlatformFloor.DeclineReason(string?)"/>, the same gate boot applies, so a
    /// registry never serves a landing its own boot skips.</param>
    /// <returns>Absolute file paths (entry DLL first) or the decline reason.</returns>
    public static (IReadOnlyList<string> Files, string? DeclineReason) Collect(
        string baseDirectory,
        string moduleName,
        ModuleActivationList activation,
        Func<string?, string?> platformGate)
    {
        if (string.IsNullOrWhiteSpace(moduleName)
            || moduleName is "." or ".."
            || moduleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || moduleName.Contains('/') || moduleName.Contains('\\'))
            return ([], $"'{moduleName}' is not a valid module name");

        var entry = activation.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, moduleName, StringComparison.OrdinalIgnoreCase));
        if (entry is { Enabled: false })
            return ([], $"module '{moduleName}' is uninstalled on this instance");
        if (entry is not null && platformGate(entry.MinMeshVersion) is { } unsatisfied)
            // The landed module's declared floor is not satisfied HERE (the platform rolled back
            // below it) — these are exactly the bytes this instance's own boot union skips, and a
            // registry must never fan out a module it could not load itself.
            return ([], $"module '{moduleName}' is not loadable on this instance: {unsatisfied}");

        var folder = Path.Combine(baseDirectory, "modules", moduleName);
        // A DEFERRED re-land (the running instance had the module loaded, so the swap parked at
        // .pending-<name> until the next boot) is the NEWEST landed content — serve it: consumers
        // must fetch what was published, not what this process happens to still be running.
        var pending = ModuleLandingService.PendingPathFor(baseDirectory, moduleName);
        if (Directory.Exists(pending)
            && File.Exists(Path.Combine(pending, moduleName + ".dll")))
            folder = pending;
        var entryDll = Path.Combine(folder, moduleName + ".dll");
        if (!File.Exists(entryDll))
            // Covers the missing folder, the transitional publish state (a module still riding the
            // app closure prunes its modules/ folder empty), and a lost volume alike: no entry DLL,
            // no module bundle — the package still serves its content and NodeType assemblies.
            return ([], $"modules/{moduleName}/{moduleName}.dll does not exist on this instance");

        // Entry DLL first, the rest of the closure (dlls + symbols) in stable order. Top level
        // only — the modules/<name>/ layout is flat by construction (ModuleLandingService writes
        // file names, and the publish target lays closures out flat).
        var files = new List<string> { entryDll };
        files.AddRange(Directory.EnumerateFiles(folder)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".dll" or ".pdb")
            .Where(f => !string.Equals(f, entryDll, StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));

        return (files, null);
    }
}
