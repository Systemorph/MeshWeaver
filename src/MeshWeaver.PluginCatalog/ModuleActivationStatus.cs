using System.Collections.Immutable;
using System.Reflection;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// One module that has LANDED on the deployment's volume but is not LOADED in this process — the
/// unit of the restart-as-activation signal (#1979), carrying enough to point a viewer back at the
/// install that produced it.
/// </summary>
/// <param name="Name">The module's assembly simple name, as the activation entry records it.</param>
/// <param name="PackagePath">The mesh path of the install record that landed it, when the store
/// lane wrote one — the back-pointer a package card matches on. Null for an entry with no
/// recorded origin.</param>
/// <param name="Version">The package version the landed bundle was served at, when recorded.</param>
public sealed record PendingModuleActivation(string Name, string? PackagePath, string? Version);

/// <summary>
/// Derives, per PROCESS, which activated modules are not running here yet.
///
/// <para>🚨 <b>Why not just read <see cref="ModuleActivationList.PendingRestart"/>.</b> That flag is
/// a single deployment-wide boolean that the NEXT boot clears — and on a multi-replica deployment
/// the pod that clears it is not the pod that is missing the module. Replica A lands a module and
/// sets the flag; replica B restarts for an unrelated reason, applies the list and resets it; A is
/// still serving WITHOUT the module while every surface reads "nothing pending". The flag answers
/// "did something change since some boot", which is not the question a buyer or an operator is
/// asking. Comparing the persisted list against what THIS process actually loaded answers it
/// exactly, per pod, and needs no extra state to stay true.</para>
///
/// <para>🚨 <b>"Loaded" is a NAME <i>and</i> a GENERATION (#3395).</b> The rule above used to ask
/// only whether an assembly of that simple name was loaded here, which answers the INSTALL case
/// and misses the UPDATE case entirely — and update is what a deployment does continuously
/// (<c>RegistryUpdateReconciler</c> lands a fresh generation, moves
/// <see cref="ModuleActivationEntry.Directory"/>, and running pods keep the generation they pinned
/// at THEIR boot). A replica hours behind therefore reported "no module activation pending" and
/// <c>/health</c> reported it Healthy, so nothing anywhere could see that the replicas of one
/// deployment were running DIFFERENT module sets.</para>
///
/// <para>Measured on memex-cloud 2026-09-06: three pods of one Deployment, one image
/// (<c>3.0.0-rc9.ci.7693</c>), booted 11:33 / 11:41 / 12:51 around a landing wave at 12:18–12:27 —
/// <b>39 of 40 pinned module generations differed</b> between the first two and the third, the
/// sidecar named the newest, and both stale pods answered <c>/health</c> → <c>Healthy</c>. Two
/// NodeType compiles 16 minutes apart landed on two of those pods and stamped two different module
/// fingerprints into the ONE shared compile record, which is how a NodeType that was healthy
/// becomes failed with no source change (#3395).</para>
///
/// <para>Pure and total: the caller supplies the list, the loaded set and what each loaded module's
/// generation is, so the rule is testable with no filesystem, no reflection and no host.</para>
/// </summary>
public static class ModuleActivationStatus
{
    /// <summary>
    /// The enabled entries in <paramref name="activation"/> whose assembly is not among
    /// <paramref name="loadedAssemblyNames"/> — and which a restart would actually load.
    ///
    /// <para>A DISABLED entry is never pending: it is the record of an uninstall, and its module
    /// being absent from this process is the outcome, not a to-do. (An uninstall that has not taken
    /// effect yet — the module still loaded — is deliberately not reported either: nothing is
    /// missing from the user's point of view, and reporting it would put an alarming "restart
    /// required" on a package that has just been removed.)</para>
    ///
    /// <para>🚨 A HELD entry — one whose recorded platform floor <paramref name="platformGate"/>
    /// refuses — is not pending either (2026-08-22). "Pending" is a promise: a restart activates this.
    /// For a held entry that promise is false — boot applies the SAME gate and skips it — so
    /// reporting it would put a permanent "restart required" on the surface that no restart can
    /// ever clear (a registry SHELVES modules for platforms newer than itself, and the hold lasts
    /// until a platform update; the update is itself a restart, at which point the entry loads and
    /// leaves this question entirely). The gate is a parameter for the same reason boot's is:
    /// production passes <see cref="ModulePlatformFloor.DeclineReason(string?)"/>, and there is
    /// never a second notion of the module platform requirement.</para>
    ///
    /// <para>🚨 And an entry whose LANDED BYTES ARE GONE is not pending either — it is
    /// <see cref="Unresolvable(ModuleActivationList, IReadOnlySet{string},
    /// IReadOnlyDictionary{string, string}, Func{string, string}, Func{ModuleActivationEntry, bool})"/>
    /// (#2093). Same reason, sharper: "pending" promises that a restart
    /// activates this, and boot skips an entry whose DLL is missing exactly as loudly as a held
    /// one. Reporting it as pending is a promise every restart breaks and none of them clears —
    /// and it is the state that took <c>/mcp</c> down for a pod's whole lifetime while every
    /// surface said "restart required". The two must render differently because the ACTIONS
    /// differ: wait for the restart, versus re-install the package.</para>
    /// </summary>
    /// <param name="activation">The persisted activation list.</param>
    /// <param name="loadedAssemblyNames">Assembly SIMPLE names loaded in this process.</param>
    /// <param name="platformGate">Returns WHY a recorded platform FLOOR is not satisfied by the
    /// running platform, or null when it is (an absent floor is always satisfied).</param>
    /// <param name="landedDllExists">Whether the entry's landed DLL is actually on the volume —
    /// production passes <see cref="ModuleActivationBoot.LandedModuleDllExists"/>, the SAME check
    /// boot gates on, so this report can never promise a restart boot would not honour.</param>
    public static ImmutableList<PendingModuleActivation> NotYetLoaded(
        ModuleActivationList activation,
        IReadOnlySet<string> loadedAssemblyNames,
        Func<string?, string?> platformGate,
        Func<ModuleActivationEntry, bool> landedDllExists)
        => NotYetLoaded(activation, loadedAssemblyNames,
            ImmutableDictionary<string, string>.Empty, platformGate, landedDllExists);

    /// <summary>
    /// As the four-argument overload, plus what GENERATION each module actually loaded from in this
    /// process (#3395) — so an entry whose <see cref="ModuleActivationEntry.Directory"/> has moved
    /// under a still-loaded name is pending, which is the case an auto-updating deployment is in
    /// almost all the time.
    ///
    /// <para>🚨 <b>An overload, deliberately, not an extra parameter on the existing method</b> —
    /// the same rule <c>RequiredModuleStatus.Classify</c> states: adding a parameter replaces the
    /// signature, so a host compiled against the previous platform gets a
    /// <see cref="MissingMethodException"/> at runtime. The four-argument form stays and forwards
    /// an EMPTY map, which is exactly its old behaviour.</para>
    ///
    /// <para>🚨 <b>Absence of a generation is never a mismatch.</b> A name missing from
    /// <paramref name="loadedModuleGenerations"/> means this process cannot say where that module
    /// was loaded from — not that it is stale. Claiming a mismatch there would print a "restart
    /// required" no restart can clear, the exact false promise the held-entry and missing-bytes
    /// rules above exist to prevent. Nor is an entry with no recorded
    /// <see cref="ModuleActivationEntry.Directory"/> (the legacy fixed <c>modules/&lt;name&gt;/</c>
    /// folder) ever stale: it names no generation to compare against.</para>
    /// </summary>
    /// <param name="activation">The persisted activation list.</param>
    /// <param name="loadedAssemblyNames">Assembly SIMPLE names loaded in this process.</param>
    /// <param name="loadedModuleGenerations">Module simple name → the generation DIRECTORY LEAF
    /// (<c>&lt;name&gt;@&lt;id&gt;</c>) this process loaded it from — production passes
    /// <see cref="LoadedModuleGenerations()"/>. A name absent from the map is "unknown", never
    /// "stale".</param>
    /// <param name="platformGate">The one platform floor gate.</param>
    /// <param name="landedDllExists">Whether the entry's landed DLL is on the volume.</param>
    public static ImmutableList<PendingModuleActivation> NotYetLoaded(
        ModuleActivationList activation,
        IReadOnlySet<string> loadedAssemblyNames,
        IReadOnlyDictionary<string, string> loadedModuleGenerations,
        Func<string?, string?> platformGate,
        Func<ModuleActivationEntry, bool> landedDllExists)
    {
        ArgumentNullException.ThrowIfNull(landedDllExists);
        return AwaitingLoad(activation, loadedAssemblyNames, loadedModuleGenerations, platformGate)
            .Where(landedDllExists)
            .Select(Describe)
            .ToImmutableList();
    }

    /// <summary>
    /// The enabled, floor-satisfied entries that are not loaded here AND whose landed DLL is not on
    /// the volume — activated modules a restart will NOT bring up (#2093).
    ///
    /// <para>This is the state behind an endpoint module that 404s for a pod's whole lifetime: the
    /// activation record says the module is on, so every NodeType-facing surface treats it as
    /// installed, while its assembly was never host-loaded and so contributed no routes. It has
    /// exactly one remedy — re-install the package — and the whole defect was that nothing
    /// anywhere said so.</para>
    /// </summary>
    /// <param name="activation">The persisted activation list.</param>
    /// <param name="loadedAssemblyNames">Assembly SIMPLE names loaded in this process.</param>
    /// <param name="platformGate">The one platform floor gate.</param>
    /// <param name="landedDllExists">Whether the entry's landed DLL is on the volume.</param>
    public static ImmutableList<PendingModuleActivation> Unresolvable(
        ModuleActivationList activation,
        IReadOnlySet<string> loadedAssemblyNames,
        Func<string?, string?> platformGate,
        Func<ModuleActivationEntry, bool> landedDllExists)
        => Unresolvable(activation, loadedAssemblyNames,
            ImmutableDictionary<string, string>.Empty, platformGate, landedDllExists);

    /// <summary>
    /// As the four-argument overload, with the generation map of #3395 — so a module whose
    /// ACTIVATED generation directory is gone from the volume while an OLDER one is still loaded
    /// here reports as unresolvable (re-install) rather than pending (wait for a restart), which is
    /// the same distinction this pair has always drawn, now visible for an update as well as an
    /// install.
    /// </summary>
    /// <param name="activation">The persisted activation list.</param>
    /// <param name="loadedAssemblyNames">Assembly SIMPLE names loaded in this process.</param>
    /// <param name="loadedModuleGenerations">Module simple name → loaded generation directory leaf;
    /// an absent name is "unknown", never "stale".</param>
    /// <param name="platformGate">The one platform floor gate.</param>
    /// <param name="landedDllExists">Whether the entry's landed DLL is on the volume.</param>
    public static ImmutableList<PendingModuleActivation> Unresolvable(
        ModuleActivationList activation,
        IReadOnlySet<string> loadedAssemblyNames,
        IReadOnlyDictionary<string, string> loadedModuleGenerations,
        Func<string?, string?> platformGate,
        Func<ModuleActivationEntry, bool> landedDllExists)
    {
        ArgumentNullException.ThrowIfNull(landedDllExists);
        return AwaitingLoad(activation, loadedAssemblyNames, loadedModuleGenerations, platformGate)
            .Where(entry => !landedDllExists(entry))
            .Select(Describe)
            .ToImmutableList();
    }

    private static PendingModuleActivation Describe(ModuleActivationEntry entry) =>
        new(entry.Name, entry.PackagePath, entry.Version);

    private static IEnumerable<ModuleActivationEntry> AwaitingLoad(
        ModuleActivationList activation,
        IReadOnlySet<string> loadedAssemblyNames,
        IReadOnlyDictionary<string, string> loadedModuleGenerations,
        Func<string?, string?> platformGate)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(loadedAssemblyNames);
        ArgumentNullException.ThrowIfNull(loadedModuleGenerations);
        ArgumentNullException.ThrowIfNull(platformGate);

        return activation.Entries
            .Where(entry => entry.Enabled
                && !string.IsNullOrWhiteSpace(entry.Name)
                && platformGate(entry.MinMeshVersion) is null
                && (!loadedAssemblyNames.Contains(entry.Name)
                    || RunsAnOlderGeneration(entry, loadedModuleGenerations)));
    }

    /// <summary>
    /// Whether this process holds a DIFFERENT generation of <paramref name="entry"/> than the one
    /// the activation record activates (#3395) — the update half of "not loaded here".
    ///
    /// <para>True only on positive evidence: the entry names a generation, this process knows which
    /// generation it loaded that module from, and the two differ. Unknown is never a mismatch — see
    /// the note on the five-argument <see cref="NotYetLoaded(ModuleActivationList, IReadOnlySet{string},
    /// IReadOnlyDictionary{string, string}, Func{string, string}, Func{ModuleActivationEntry, bool})"/>.</para>
    /// </summary>
    private static bool RunsAnOlderGeneration(
        ModuleActivationEntry entry,
        IReadOnlyDictionary<string, string> loadedModuleGenerations) =>
        !string.IsNullOrWhiteSpace(entry.Directory)
        && loadedModuleGenerations.TryGetValue(entry.Name, out var loaded)
        && !string.IsNullOrWhiteSpace(loaded)
        && !string.Equals(loaded, entry.Directory, StringComparison.Ordinal);

    /// <summary>
    /// Whether the install record at <paramref name="packagePath"/> landed a module that is not
    /// loaded here — the per-package question the Store's install step asks so it can say
    /// "restart required to finish activating this" instead of a bare "installed".
    ///
    /// <para>A blank path matches nothing. It is not a wildcard: a package whose card has no path
    /// to match on must not inherit some other package's pending restart.</para>
    /// </summary>
    public static bool IsPendingForPackage(
        IEnumerable<PendingModuleActivation> pending, string? packagePath) =>
        !string.IsNullOrWhiteSpace(packagePath)
        && pending.Any(p => string.Equals(
            p.PackagePath?.Trim('/'), packagePath.Trim('/'), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The simple names of every assembly loaded into <paramref name="domain"/>. The set the
    /// derivation above is asked against on a live host.
    /// </summary>
    public static IReadOnlySet<string> LoadedAssemblyNames(AppDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        return domain.GetAssemblies()
            .Select(a => a.GetName().Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Convenience for the live process.</summary>
    public static IReadOnlySet<string> LoadedAssemblyNames() =>
        LoadedAssemblyNames(AppDomain.CurrentDomain);

    /// <summary>
    /// Which GENERATION directory each loaded assembly came from in <paramref name="domain"/>:
    /// simple name → the leaf of its containing directory (#3395). That leaf IS the generation
    /// identity — landing writes <c>modules/&lt;name&gt;@&lt;id&gt;/</c> and
    /// <c>ModuleGenerationPin</c> copies the directory WITH its leaf into process-local storage, so
    /// a pinned module's location ends <c>…/&lt;name&gt;@&lt;id&gt;/&lt;name&gt;.dll</c> exactly as
    /// the shared one does. Compared ordinally against
    /// <see cref="ModuleActivationEntry.Directory"/>, which records the same string.
    ///
    /// <para>🚨 <b>Only unambiguous evidence is recorded.</b> An assembly with no location (loaded
    /// from bytes — every NodeType build is) contributes nothing, and a simple name whose loaded
    /// copies disagree on a leaf is DROPPED rather than guessed: the map's contract is "this is
    /// where that module was loaded from", and a name absent from it means "unknown", which the
    /// derivation treats as not-stale. Under-reporting a pending activation costs a signal;
    /// over-reporting one prints a restart prompt no restart can clear.</para>
    /// </summary>
    /// <param name="domain">The app domain to read.</param>
    public static IReadOnlyDictionary<string, string> LoadedModuleGenerations(AppDomain domain)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var generations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assembly in domain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (string.IsNullOrEmpty(name) || ambiguous.Contains(name))
                continue;
            var leaf = GenerationLeafOf(assembly);
            if (leaf is null)
                continue;
            if (generations.TryGetValue(name, out var known))
            {
                if (!string.Equals(known, leaf, StringComparison.Ordinal))
                {
                    generations.Remove(name);
                    ambiguous.Add(name);
                }
                continue;
            }
            generations[name] = leaf;
        }
        return generations;
    }

    /// <summary>Convenience for the live process.</summary>
    public static IReadOnlyDictionary<string, string> LoadedModuleGenerations() =>
        LoadedModuleGenerations(AppDomain.CurrentDomain);

    /// <summary>The containing directory's leaf, or null when the assembly has no readable
    /// on-disk location (loaded from bytes, or a single-file bundle).</summary>
    private static string? GenerationLeafOf(Assembly assembly)
    {
        string? location;
        try
        {
            location = assembly.Location;
        }
        catch (NotSupportedException)
        {
            // A dynamic assembly refuses the property outright — "unknown", like an empty location.
            return null;
        }
        if (string.IsNullOrEmpty(location))
            return null;
        var directory = Path.GetDirectoryName(location);
        return string.IsNullOrEmpty(directory) ? null : Path.GetFileName(directory);
    }

    /// <summary>
    /// One human-readable line naming what is pending — shared by every surface so an operator and
    /// a buyer are never told different numbers.
    /// </summary>
    /// <param name="pending">The pending activations.</param>
    /// <param name="maxNamed">How many are named before the line truncates.</param>
    public static string Describe(IReadOnlyCollection<PendingModuleActivation> pending, int maxNamed = 10)
    {
        ArgumentNullException.ThrowIfNull(pending);
        if (pending.Count == 0)
            return "no module activation pending";

        var names = pending
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return $"{names.Length} module(s) are landed but not yet loaded in this process — "
            + "a restart activates them: "
            + Name(names, maxNamed);
    }

    /// <summary>
    /// One human-readable line naming the ACTIVATED modules a restart will not fix — the other
    /// half of the report, kept separate because the remedy is different (#2093).
    /// </summary>
    /// <param name="unresolvable">The activated entries whose landed bytes are absent.</param>
    /// <param name="maxNamed">How many are named before the line truncates.</param>
    public static string DescribeUnresolvable(
        IReadOnlyCollection<PendingModuleActivation> unresolvable, int maxNamed = 10)
    {
        ArgumentNullException.ThrowIfNull(unresolvable);
        if (unresolvable.Count == 0)
            return "no module activation is unresolvable";

        var names = unresolvable
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return $"{names.Length} module(s) are ACTIVATED but their landed assemblies are absent — "
            + "a restart will NOT load them and anything they contribute (endpoints included) "
            + "stays missing; re-install the package: "
            + Name(names, maxNamed);
    }

    private static string Name(string[] names, int maxNamed) =>
        string.Join(", ", names.Take(Math.Max(1, maxNamed)))
        + (names.Length > maxNamed ? $", …(+{names.Length - maxNamed})" : string.Empty);
}
