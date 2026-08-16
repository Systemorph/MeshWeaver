using System.IO;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What a REGISTRY instance can serve as a module bundle (#1664 Slice C, the serving half): the
/// files under its own <c>modules/&lt;name&gt;/</c> — the very bytes this deployment loads and runs,
/// which is the same philosophy the NodeType bundle lane established ("the inputs ARE the storage").
/// A module laid out by the image's publish (`MeshModulesPublish.targets`) was compiled with the
/// image and therefore matches the RUNNING framework MVID by construction; a module the registry
/// itself store-landed carries its recorded MVID in the activation sidecar, and stale bytes are
/// refused here so a registry can never fan out assemblies it could not load itself.
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
    /// <param name="frameworkGate">Returns WHY a recorded framework identity may not load in THIS
    /// process, or null when it may — production passes
    /// <c>PrebuiltAssemblySeeder.DeclineReason</c>.</param>
    /// <returns>Absolute file paths (entry DLL first) or the decline reason.</returns>
    public static (IReadOnlyList<string> Files, string? DeclineReason) Collect(
        string baseDirectory,
        string moduleName,
        ModuleActivationList activation,
        Func<string?, string?> frameworkGate)
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
        if (entry is not null && frameworkGate(entry.FrameworkMvid) is { } stale)
            // The landed bytes did not survive this instance's own image roll — they are exactly
            // the ABI-stale assemblies the boot union skips, and serving them would hand a consumer
            // bytes stamped with a framework NEITHER side runs.
            return ([], $"module '{moduleName}' is landed for a stale framework here: {stale}");

        var folder = Path.Combine(baseDirectory, "modules", moduleName);
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
