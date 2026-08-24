using System.IO;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What a REGISTRY instance can serve as a module bundle (#1664 Slice C, the serving half): the
/// files under its own <c>modules/&lt;name&gt;/</c> — the very bytes this deployment loads and runs,
/// which is the same philosophy the NodeType bundle lane established ("the inputs ARE the storage").
/// A module is servable exactly when its bytes are here and it was not uninstalled: entry DLL
/// present, activation entry (if any) enabled. The MVID a landing recorded is diagnostic and never
/// withholds a serve — modules bind by simple name.
///
/// <para>🚨 <b>The serving side applies NO platform-floor gate of its own (2026-08-22)</b> — the shelf
/// deliberately carries modules for platforms NEWER than the instance serving them. The old rule
/// ("a registry must never fan out a module it could not load itself") read as caution and was a
/// deadlock: modules extracted from the platform image declared a floor above the registry's own
/// version, the publish path refused to carry them, and the registry could not update to that
/// version because its <c>Modules:Required</c> gate held the rollout for exactly those absent
/// modules. The floor is the CONSUMER's gate, applied against the CONSUMER's platform — it rides
/// the bundle index and the bundle manifest, and every consumer checks it three times
/// (<c>ModuleUpdateDecision.Decide</c> before any download, <c>PluginBundleClient.LandFromBundle</c>
/// against the manifest, <see cref="ModuleLandingService"/> at placement) — so serving above-floor
/// bytes costs a below-floor consumer zero bytes and can never land where they would not load. A
/// serve-side floor re-check would be a SECOND notion of the same gate, wrong for the warehouse
/// role by construction.</para>
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
    /// <returns>Absolute file paths (entry DLL first) or the decline reason.</returns>
    public static (IReadOnlyList<string> Files, string? DeclineReason) Collect(
        string baseDirectory,
        string moduleName,
        ModuleActivationList activation)
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
        // 🚨 Deliberately NO floor check on the entry here — a HELD landing (floor above this
        // instance's platform, ShelveModule) and a landing the platform rolled back below are
        // both SERVED: their recorded floor rides the index and the manifest, and the consumer's
        // own gate is what decides loadability THERE. See the type doc for why a serve-side floor
        // was the deadlock.

        // The entry's GENERATION directory is the newest landed content — the one resolution
        // rule (ModuleDirectoryFor), shared with boot. Serving follows the pointer immediately,
        // so consumers fetch what was PUBLISHED even while this process still runs an older
        // generation it loaded at ITS boot (or, for a held landing, none at all).
        var folder = ModuleLandingService.ModuleDirectoryFor(baseDirectory, moduleName, entry);
        var entryDll = Path.Combine(folder, moduleName + ".dll");
        if (!File.Exists(entryDll))
            // Covers the missing folder, the transitional publish state (a module still riding the
            // app closure prunes its modules/ folder empty), and a lost volume alike: no entry DLL,
            // no module bundle — the package still serves its content and NodeType assemblies.
            return ([], $"{Path.GetFileName(folder)}/{moduleName}.dll does not exist on this instance");

        // Entry DLL first, the rest of the closure (dlls + symbols) in stable order. Top level
        // only — a module directory is flat by construction (ModuleLandingService writes file
        // names, and the publish target lays closures out flat).
        var files = new List<string> { entryDll };
        files.AddRange(Directory.EnumerateFiles(folder)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".dll" or ".pdb")
            .Where(f => !string.Equals(f, entryDll, StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));

        return (files, null);
    }
}
