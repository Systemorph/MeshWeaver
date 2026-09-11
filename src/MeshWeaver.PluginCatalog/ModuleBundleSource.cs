using System.IO;
using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// What a REGISTRY instance can serve as a module bundle (#1664 Slice C, the serving half): the
/// files under its own <c>modules/&lt;name&gt;/</c> — the very bytes this deployment loads and runs,
/// which is the same philosophy the NodeType bundle lane established ("the inputs ARE the storage").
/// A module is servable exactly when its bytes are here and it was not uninstalled: entry DLL
/// present, activation entry (if any) enabled. The ordinary call follows the activation head; the
/// versioned call may also follow its retained fallback generation, so warehouse stock stays
/// downloadable without becoming the runtime head (#3996). The MVID a landing recorded is
/// diagnostic and never withholds a serve — modules bind by simple name.
///
/// <para>🚨 <b>The serving side applies NO platform-floor gate of its own (2026-08-22)</b> — the shelf
/// deliberately carries modules for platforms NEWER than the instance serving them. The old rule
/// ("a registry must never fan out a module it could not load itself") read as caution and was a
/// deadlock: modules extracted from the platform image declared a floor above the registry's own
/// version, the publish path refused to carry them, and the registry could not update to that
/// version because its <c>Modules:Required</c> gate held the rollout for exactly those absent
/// modules. Loadability is the CONSUMER's question, measured against the CONSUMER's platform by
/// the link probe in <see cref="ModuleLandingService"/> at placement (#3538) — so serving bytes
/// this instance cannot load can never land where they would not load. The declared floor rides
/// the bundle index and the bundle manifest as an ADVISORY (#3648): the consumer logs what the
/// module claims and lands what links. A serve-side check of either would be a SECOND notion of
/// the same question, wrong for the warehouse role by construction.</para>
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
    /// <returns>Absolute closure paths (entry DLL first), the module's STATIC WEB ASSETS as
    /// (module-relative path, absolute path) pairs, or the decline reason.</returns>
    public static (IReadOnlyList<string> Files,
        IReadOnlyList<(string RelativePath, string FullPath)> Assets,
        string? DeclineReason) Collect(
        string baseDirectory,
        string moduleName,
        ModuleActivationList activation) =>
        CollectVersion(baseDirectory, moduleName, activation, version: null);

    /// <summary>
    /// Resolves one published VERSION of a module. The null version keeps the ordinary activation
    /// behaviour and serves the head. A stated version may select either the head or its retained
    /// previous generation (#3996), which is what makes older warehouse stock addressable without
    /// moving activation backwards.
    /// </summary>
    public static (IReadOnlyList<string> Files,
        IReadOnlyList<(string RelativePath, string FullPath)> Assets,
        string? DeclineReason) CollectVersion(
        string baseDirectory,
        string moduleName,
        ModuleActivationList activation,
        string? version)
    {
        if (string.IsNullOrWhiteSpace(moduleName)
            || moduleName is "." or ".."
            || moduleName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || moduleName.Contains('/') || moduleName.Contains('\\'))
            return ([], [], $"'{moduleName}' is not a valid module name");

        var entry = ResolveEntry(activation, moduleName, version);
        if (!string.IsNullOrWhiteSpace(version) && entry is null)
            return ([], [], $"module '{moduleName}' has no retained generation at version {version}");
        if (entry is { Enabled: false })
            return ([], [], $"module '{moduleName}' is uninstalled on this instance");
        // 🚨 Deliberately NO floor check on the entry here — a HELD landing (floor above this
        // instance's platform, ShelveModule) and a landing the platform rolled back below are
        // both SERVED: their recorded floor rides the index and the manifest, and the consumer's
        // own gate is what decides loadability THERE. See the type doc for why a serve-side floor
        // was the deadlock.

        // The selected entry's GENERATION directory is the requested landed content — the one
        // resolution rule (ModuleDirectoryFor), shared with boot. The ordinary null-version call
        // follows the head immediately; a versioned bundle route may follow its retained fallback.
        // Consumers therefore fetch exactly what the index named even while this process still
        // runs an older generation it loaded at ITS boot (or, for a held landing, none at all).
        var folder = ModuleLandingService.ModuleDirectoryFor(baseDirectory, moduleName, entry);
        var entryDll = Path.Combine(folder, moduleName + ".dll");
        if (!File.Exists(entryDll))
            // Covers the missing folder, the transitional publish state (a module still riding the
            // app closure prunes its modules/ folder empty), and a lost volume alike: no entry DLL,
            // no module bundle — the package still serves its content and NodeType assemblies.
            return ([], [], $"{Path.GetFileName(folder)}/{moduleName}.dll does not exist on this instance");

        // Entry DLL first, the rest of the CLOSURE (dlls + symbols) in stable order. Top level
        // only — a module's assemblies are flat by construction (ModuleLandingService writes file
        // names, and the publish target lays closures out flat). 🚨 That flatness is why the
        // assets below need their own walk: they are the one part of a landed module that is NOT
        // flat, and a top-level-only listing silently omits every one of them.
        var files = new List<string> { entryDll };
        files.AddRange(Directory.EnumerateFiles(folder)
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".dll" or ".pdb")
            .Where(f => !string.Equals(f, entryDll, StringComparison.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase));

        // 🚨 THE ASSETS MUST ROUND-TRIP, or the registry re-creates the very defect it was just
        // fixed for. A consumer lands what this bundle carries and nothing else, so a pack whose
        // wwwroot the SERVE path drops renders unstyled downstream even though the shelf holds it
        // — the publish-side fix (#2221) only makes the registry's own copy complete. Assets keep
        // their RELATIVE path: a component asks for _content/<pack>/Components/x.razor.js, so a
        // flattened asset 404s exactly like a missing one.
        var assetRoot = Path.Combine(folder, "wwwroot");
        var assets = Directory.Exists(assetRoot)
            ? Directory.EnumerateFiles(assetRoot, "*", SearchOption.AllDirectories)
                .Select(f => (
                    RelativePath: "wwwroot/" + Path.GetRelativePath(assetRoot, f)
                        .Replace(Path.DirectorySeparatorChar, '/'),
                    FullPath: f))
                .OrderBy(a => a.RelativePath, StringComparer.Ordinal)
                .ToList()
            : [];

        return (files, assets, null);
    }

    /// <summary>The activation entry whose bytes represent <paramref name="version"/>: the head
    /// for a null or matching version, the retained previous generation for its matching version,
    /// or null when this deployment retains neither. Version equality uses the canonical NuGet
    /// comparer, so 1.2 and 1.2.0 cannot disagree between index and download.</summary>
    public static ModuleActivationEntry? ResolveEntry(
        ModuleActivationList activation, string moduleName, string? version)
    {
        var head = activation.Entries.FirstOrDefault(e =>
            string.Equals(e.Name, moduleName, StringComparison.OrdinalIgnoreCase));
        if (head is null || !head.Enabled || string.IsNullOrWhiteSpace(version))
            return head;
        if (!string.IsNullOrWhiteSpace(head.Version)
            && NuGetVersionComparer.Instance.Compare(version, head.Version) == 0)
            return head;
        var previous = ModuleActivationBoot.PreviousGeneration(head);
        return previous is not null
               && !string.IsNullOrWhiteSpace(previous.Version)
               && NuGetVersionComparer.Instance.Compare(version, previous.Version) == 0
            ? previous
            : null;
    }
}
