using System.Collections.Immutable;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// One package this installation HAS INSTALLED, whose install record declares a compiled module,
/// and which the registry DID NOT OFFER at the ref a successful feed read was made against.
///
/// <para>Its content moves — a consumer's own git source keeps landing new files and the install
/// record advances — while its module bytes stay on whatever generation the deployment was first
/// seeded with, because the module funnel only ever considers packages the registry serves.</para>
/// </summary>
/// <param name="PackageId">The install record's id, which is the package id.</param>
/// <param name="Name">The package's display name, when the record carries one.</param>
/// <param name="Module">The module assembly the record declares
/// (<see cref="PackageManifest.Module"/>) — the bytes that are not being delivered.</param>
/// <param name="InstalledModuleVersion">The content identity the record claims is installed. It is
/// what makes the shape legible: the record says 1.5 while the loaded assembly is whatever the
/// last delivered generation was.</param>
public sealed record UndeliveredModule(
    string PackageId,
    string? Name,
    string? Module,
    string? InstalledModuleVersion)
{
    /// <summary>One line for a log or a ledger reader.</summary>
    public string Describe() =>
        $"{PackageId}"
        + (string.IsNullOrWhiteSpace(Module) ? "" : $" (module {Module})");
}

/// <summary>
/// 🚨 <b>Whether a consumer's installed modules can be DELIVERED at all — the question the module
/// funnel never asked (Systemorph/MeshWeaver.Plugins#1584).</b>
///
/// <para><b>The defect.</b> <see cref="RegistryUpdateReconciler"/>'s module lane iterates the
/// packages the REGISTRY SERVES, intersects them with this installation's install records, and
/// adopts what is newer. A package installed HERE that the registry does NOT serve is therefore
/// not "up to date" and not "failed" — it is <i>absent from the loop</i>, and absence produced no
/// log line, no ledger entry and no card state anywhere. Measured on memex 2026-09-10: the
/// registry offered 45 manifests and <c>Mail</c> was not among them (its
/// <c>index.json</c> declares <c>tier: personal</c>, the instance's catalog grant covers the
/// baseline plan), so <c>MeshWeaver.Mail.MicrosoftGraph</c> loaded an assembly written eleven days
/// earlier while the install record read <c>version 1.5.0</c>, installed an hour ago, and every
/// dashboard read "installed, up to date".</para>
///
/// <para><b>Absence of an offer is EVIDENCE, once the feed read succeeded.</b> That is the whole
/// reason this can be stated at all, and the reason it is computed from a completed read rather
/// than from a timeout: a registry that answered its feed in full and did not list a package has
/// declined it — the entitlement anchor's own vocabulary (see
/// <see cref="EntitlementDecision"/>) — and a decline the consumer cannot see is indistinguishable
/// from delivery. A read that FAILED says nothing, and the caller must record "not determined"
/// rather than an empty list; the two are separate states on
/// <see cref="RegistryReconcileEntry.UndeliveredModules"/> for exactly that reason.</para>
///
/// <para><b>What this does NOT claim.</b> It is not an entitlement verdict — a registry may omit a
/// package for any reason, and the consumer cannot tell "your grant does not cover it" from "this
/// registry never carried it". It is not a fault either: nothing here is broken, the package is
/// simply not delivered from this registry, and the remedy (grant the plan, tier the package,
/// point at a registry that carries it) lives with whoever runs the registry. And it says nothing
/// about a package the registry DOES offer whose bundle is missing or built for another framework
/// identity — that lands in <see cref="PluginBundleClient.AdoptModule"/>, which logs its own
/// verdict.</para>
///
/// <para>Pure and total: the caller supplies both sides, so the rule is testable with no registry,
/// no mesh and no host.</para>
/// </summary>
public static class ModuleDelivery
{
    /// <summary>
    /// The install records that declare a module and whose package the registry did not offer.
    ///
    /// <para>A record with no <see cref="PackageManifest.Module"/> is content-only: its whole
    /// delivery is the content lane, which the reconcile already covers, so it is never reported
    /// here. Matching is ORDINAL-IGNORE-CASE on the package id, the same comparison every other
    /// install-record lookup on this path uses.</para>
    /// </summary>
    /// <param name="installedRecords">This installation's install records
    /// (<c>Plugins/*</c>). Nulls — an unreadable record's content — are skipped.</param>
    /// <param name="servedPackages">The packages the registry answered its feed with. An EMPTY
    /// feed from a successful read means the registry offers this instance nothing, and every
    /// installed module package is then undelivered, which is the honest answer.</param>
    public static ImmutableList<UndeliveredModule> NotOffered(
        IEnumerable<PackageManifest?>? installedRecords,
        IEnumerable<PackageManifest?>? servedPackages)
    {
        var offered = (servedPackages ?? [])
            .Where(pkg => pkg is not null && !string.IsNullOrWhiteSpace(pkg.Id))
            .Select(pkg => pkg!.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (installedRecords ?? [])
            .Where(record => record is not null
                && !string.IsNullOrWhiteSpace(record.Id)
                && !string.IsNullOrWhiteSpace(record.Module)
                && !offered.Contains(record.Id))
            .Select(record => new UndeliveredModule(
                record!.Id, record.Name, record.Module, record.ModuleVersion))
            .OrderBy(u => u.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();
    }

    /// <summary>
    /// The packages NO configured registry offers — the consumer-wide verdict a surface renders as
    /// <b>not delivered</b>, read off the ledger <see cref="RegistryUpdateReconciler"/> owns.
    ///
    /// <para>🚨 <b>Only DETERMINED entries vote.</b> An entry whose
    /// <see cref="RegistryReconcileEntry.UndeliveredModules"/> is null never read its feed
    /// successfully in this process, so it has nothing to say — counting it as "offers nothing"
    /// would turn one unreachable registry into a mesh-wide "not delivered" alarm, and counting it
    /// as "offers everything" would silence a real one. With no determined entry at all the answer
    /// is EMPTY: nothing is known, which is not the same as nothing being undelivered, and the
    /// caller distinguishes them with <see cref="AnyRegistryDetermined"/>.</para>
    ///
    /// <para>An intersection, not a union: several registries are alternatives, so a package one of
    /// them serves is delivered, whatever the others do or do not carry.</para>
    /// </summary>
    /// <param name="ledger">The reconcile ledger, or null.</param>
    public static ImmutableList<UndeliveredModule> NotDeliveredByAnyRegistry(
        RegistryReconcileLedger? ledger)
    {
        var determined = (ledger?.Registries ?? [])
            .Where(entry => entry.UndeliveredModules is not null)
            .ToList();
        if (determined.Count == 0)
            return [];

        var undeliveredEverywhere = determined
            .Select(entry => entry.UndeliveredModules!
                .Select(u => u.PackageId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase))
            .Aggregate((left, right) =>
            {
                left.IntersectWith(right);
                return left;
            });

        return determined[0].UndeliveredModules!
            .Where(u => undeliveredEverywhere.Contains(u.PackageId))
            .OrderBy(u => u.PackageId, StringComparer.OrdinalIgnoreCase)
            .ToImmutableList();
    }

    /// <summary>Whether ANY configured registry answered its feed in this process — the denominator
    /// behind <see cref="NotDeliveredByAnyRegistry"/>, so an empty result can be read as "nothing
    /// undelivered" rather than as "nothing known".</summary>
    /// <param name="ledger">The reconcile ledger, or null.</param>
    public static bool AnyRegistryDetermined(RegistryReconcileLedger? ledger) =>
        (ledger?.Registries ?? []).Any(entry => entry.UndeliveredModules is not null);

    /// <summary>
    /// The line a reconcile logs when a registry declined installed module packages — naming the
    /// packages, what it means for them, and where the remedy lives. One line, never one per
    /// package: this is true on every boot for as long as the grant stands.
    /// </summary>
    /// <param name="undelivered">What the registry did not offer.</param>
    /// <param name="registryName">The registry's display name.</param>
    /// <param name="maxNamed">How many packages are named before the line truncates.</param>
    public static string Describe(
        IReadOnlyCollection<UndeliveredModule> undelivered, string registryName, int maxNamed = 10)
    {
        ArgumentNullException.ThrowIfNull(undelivered);
        if (undelivered.Count == 0)
            return $"{registryName} offers every installed module package of this installation";

        var named = undelivered
            .OrderBy(u => u.PackageId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(1, maxNamed))
            .Select(u => u.Describe())
            .ToArray();

        return $"{registryName} does NOT offer {undelivered.Count} installed package(s) that "
            + "declare a module, so their module bytes are NOT DELIVERED here and can never "
            + "advance from this registry — the content lane keeps moving and the install record "
            + "keeps advancing, which is what makes them read as installed and up to date: "
            + string.Join(", ", named)
            + (undelivered.Count > named.Length ? $", …(+{undelivered.Count - named.Length})" : "")
            + ". Check the instance's catalog grant on the registry, or the packages' tier.";
    }
}
