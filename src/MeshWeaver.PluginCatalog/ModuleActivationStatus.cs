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
/// <para>Pure and total: the caller supplies both the list and the loaded set, so the rule is
/// testable with no filesystem, no reflection and no host.</para>
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
    /// <see cref="Unresolvable"/> (#2093). Same reason, sharper: "pending" promises that a restart
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
    {
        ArgumentNullException.ThrowIfNull(landedDllExists);
        return AwaitingLoad(activation, loadedAssemblyNames, platformGate)
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
    {
        ArgumentNullException.ThrowIfNull(landedDllExists);
        return AwaitingLoad(activation, loadedAssemblyNames, platformGate)
            .Where(entry => !landedDllExists(entry))
            .Select(Describe)
            .ToImmutableList();
    }

    private static PendingModuleActivation Describe(ModuleActivationEntry entry) =>
        new(entry.Name, entry.PackagePath, entry.Version);

    private static IEnumerable<ModuleActivationEntry> AwaitingLoad(
        ModuleActivationList activation,
        IReadOnlySet<string> loadedAssemblyNames,
        Func<string?, string?> platformGate)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(loadedAssemblyNames);
        ArgumentNullException.ThrowIfNull(platformGate);

        return activation.Entries
            .Where(entry => entry.Enabled
                && !string.IsNullOrWhiteSpace(entry.Name)
                && !loadedAssemblyNames.Contains(entry.Name)
                && platformGate(entry.MinMeshVersion) is null);
    }

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
