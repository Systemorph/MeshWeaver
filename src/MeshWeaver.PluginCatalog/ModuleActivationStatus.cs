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
    /// <paramref name="loadedAssemblyNames"/>.
    ///
    /// <para>A DISABLED entry is never pending: it is the record of an uninstall, and its module
    /// being absent from this process is the outcome, not a to-do. (An uninstall that has not taken
    /// effect yet — the module still loaded — is deliberately not reported either: nothing is
    /// missing from the user's point of view, and reporting it would put an alarming "restart
    /// required" on a package that has just been removed.)</para>
    /// </summary>
    /// <param name="activation">The persisted activation list.</param>
    /// <param name="loadedAssemblyNames">Assembly SIMPLE names loaded in this process.</param>
    public static ImmutableList<PendingModuleActivation> NotYetLoaded(
        ModuleActivationList activation,
        IReadOnlySet<string> loadedAssemblyNames)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(loadedAssemblyNames);

        return activation.Entries
            .Where(entry => entry.Enabled
                && !string.IsNullOrWhiteSpace(entry.Name)
                && !loadedAssemblyNames.Contains(entry.Name))
            .Select(entry => new PendingModuleActivation(entry.Name, entry.PackagePath, entry.Version))
            .ToImmutableList();
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
            + string.Join(", ", names.Take(Math.Max(1, maxNamed)))
            + (names.Length > maxNamed ? $", …(+{names.Length - maxNamed})" : string.Empty);
    }
}
