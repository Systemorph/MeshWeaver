using System.Collections.Immutable;
using MeshWeaver.Mesh.Security;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 <b>The deployment configuration as an AUTHORIZING PRINCIPAL</b> for the unattended default
/// install — policy <c>deployment-config-authorizes-named-commercial</c>.
///
/// <para><b>Why the configuration may authorize a commercial install.</b> A package named EXACTLY
/// in <see cref="PluginCatalogOptions.InstallByDefault"/> (<c>Source/Package</c>) is named in the
/// deployment record of that instance, and that record changes only through a reviewed pull request
/// in the deployment repository that the auto-merge App merges — a merge there IS the release. So
/// the decision "this instance comes up with that package" was taken by people, reviewed, and is on
/// record; it is the same kind of decision a governed <c>package.provision</c> activity records,
/// taken one step earlier. Refusing it at boot for want of a person clicking Install made the
/// reviewed configuration a dead letter on exactly the instances it was written for.</para>
///
/// <para><b>What it does NOT authorize.</b></para>
/// <list type="bullet">
///   <item>A package covered only by a WHOLE-SOURCE pattern (<c>Plugins/*</c>). Nobody named it; a
///     wildcard is a seed for a fresh deployment, not a decision about a commercial product. It is
///     still held when newly listed and still skipped when commercial.</item>
///   <item>Anybody's ACCESS. Installing a package grants nobody anything: the content stays dark
///     until the plan / a subscription admits a user (<c>Store:InstancePlan</c>, the Store's
///     <c>PluginGate</c>). No sweep, no bulk grant, no notification.</item>
///   <item>Licence acceptance. A configuration cannot accept terms on a person's behalf
///     (<see cref="LicenseAcceptanceGate"/> refuses it), so a package whose licence requires
///     acceptance is still skipped with that reason.</item>
///   <item>Another instance. The principal names THIS instance
///     (<c>deployment-config:{PluginCatalog:InstanceId}</c>); a principal naming any other is
///     refused.</item>
/// </list>
///
/// <para><b>Verified, never trusted by its string.</b> <see cref="PackageEntitlement.Authorize"/>
/// asks <see cref="WhyNot"/>: the package must be named exactly in the configuration this process
/// BOOTED with, or be a requirement (direct or transitive) of a package that is, as computed by
/// this boot's default-install selection (<see cref="Admit"/>). So the stamped principal is
/// re-verified by every unattended update, and a package the configuration stops naming stops
/// being authorized by it at the next boot — which is when a changed deployment record takes
/// effect anyway.</para>
///
/// <para><b>The trust boundary is the constructor.</b> The exact entries and the instance id are
/// captured ONCE, into private immutable state, from the options the host bound from its
/// deployment configuration. The public, mutable <see cref="PluginCatalogOptions"/> object is never
/// consulted again, so code running in the process cannot widen the authority by appending to its
/// list; and only the catalog itself can publish the requirement closure (<see cref="Admit"/> is
/// internal).</para>
/// </summary>
public sealed class DeploymentConfigAuthority
{
    /// <summary>The entries that name ONE package exactly, as captured at construction.</summary>
    private readonly ImmutableList<PluginGrantEntry> namingEntries;

    /// <summary>
    /// Captures what the deployment configuration names, once.
    /// </summary>
    /// <param name="options">The catalog options as bound from the deployment configuration; null = nothing named.</param>
    public DeploymentConfigAuthority(PluginCatalogOptions? options)
    {
        options ??= new PluginCatalogOptions();
        Principal = PackageEntitlement.DeploymentConfigPrincipal(InstanceName(options));
        namingEntries = ExactEntries(options.InstallByDefault);
    }

    /// <summary>What the principal is called when the instance has no configured id.</summary>
    public const string UnnamedInstance = "unnamed-instance";

    /// <summary>Requirements of an explicitly named package, admitted by this boot's selection:
    /// package id → the named package that requires it.</summary>
    /// <remarks>An immutable snapshot, published with <see cref="Volatile"/>: the boot installer
    /// writes it and the update / reconcile pipelines read it on other scheduler threads.</remarks>
    private ImmutableDictionary<string, string> requiredBy =
        ImmutableDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>
    /// The principal the default install acts as — <c>deployment-config:{instance}</c>. Logged at
    /// every install it authorizes, stamped on the install record
    /// (<see cref="PackageManifest.AuthorizedBy"/>) and on the default-install ledger.
    /// </summary>
    public string Principal { get; }

    /// <summary>The instance a deployment-config principal names: <see cref="PluginCatalogOptions.InstanceId"/>,
    /// or <see cref="UnnamedInstance"/>. Pure.</summary>
    /// <param name="options">The catalog options.</param>
    /// <returns>The instance name.</returns>
    public static string InstanceName(PluginCatalogOptions options) =>
        string.IsNullOrWhiteSpace(options.InstanceId) ? UnnamedInstance : options.InstanceId.Trim();

    /// <summary>
    /// The <see cref="PluginCatalogOptions.InstallByDefault"/> entry that names
    /// <paramref name="package"/> EXACTLY — <c>Source/Package</c>, never a whole-source
    /// <c>Source/*</c> — or null when none does. Pure.
    /// </summary>
    /// <param name="installByDefault">The configured patterns.</param>
    /// <param name="package">The package.</param>
    /// <returns>The naming entry as written, or null.</returns>
    public static string? NamingEntry(IEnumerable<string>? installByDefault, PackageManifest package)
    {
        ArgumentNullException.ThrowIfNull(package);
        return NamingEntry(ExactEntries(installByDefault), package);
    }

    private static string? NamingEntry(ImmutableList<PluginGrantEntry> exact, PackageManifest package) =>
        exact.FirstOrDefault(e => e.Matches(package.Source ?? "", package.Id))?.ToString();

    /// <summary>The configured patterns that name ONE package (<c>Source/Package</c>) — a
    /// whole-source <c>Source/*</c> and a malformed entry are dropped. Pure.</summary>
    private static ImmutableList<PluginGrantEntry> ExactEntries(IEnumerable<string>? installByDefault) =>
        (installByDefault ?? [])
            .Select(PluginGrantEntry.TryParse)
            .Where(e => e is not null && e.PackageId != PluginGrantEntry.AllPackages)
            .Select(e => e!)
            .ToImmutableList();

    /// <summary>
    /// Records the requirement closure of the explicitly named packages this boot's selection
    /// computed (<see cref="InstanceAutoRegistrationService.ConfigAuthorizedRequirements"/>) —
    /// REPLACING the previous one, so a requirement the configuration stopped reaching stops being
    /// authorized. 🚨 INTERNAL: only the catalog's own selection may publish it — a public setter
    /// would let any code in the process add a commercial package to the authorized set.
    /// </summary>
    /// <param name="closure">Package id → the named package that requires it.</param>
    internal void Admit(ImmutableDictionary<string, string> closure) =>
        Volatile.Write(ref requiredBy, closure.WithComparers(StringComparer.Ordinal));

    /// <summary>
    /// Why the configuration does NOT authorize <paramref name="package"/>, or null when it does —
    /// see <see cref="Because"/> for what it says when it does.
    /// </summary>
    /// <param name="principal">The principal handed to the gate.</param>
    /// <param name="package">The package being installed or updated.</param>
    /// <returns>The reason, or null.</returns>
    public string? WhyNot(string principal, PackageManifest package)
    {
        ArgumentNullException.ThrowIfNull(package);
        var expected = Principal;
        if (!string.Equals(principal.Trim(), expected, StringComparison.Ordinal))
            return $"'{principal}' names another instance's configuration — this instance's is '{expected}'";
        return Because(package) is null
            ? $"'{package.Id}' (source '{package.Source}') is neither named exactly in "
              + "PluginCatalog:InstallByDefault — a whole-source pattern such as 'Plugins/*' names "
              + "nothing — nor required by a package that is"
            : null;
    }

    /// <summary>
    /// Why the configuration authorizes <paramref name="package"/> — the sentence the log and the
    /// ledger carry — or null when it does not.
    /// </summary>
    /// <param name="package">The package.</param>
    /// <returns>The reason, or null.</returns>
    public string? Because(PackageManifest package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (NamingEntry(namingEntries, package) is { } entry)
            return $"named in PluginCatalog:InstallByDefault as '{entry}'";
        return Volatile.Read(ref requiredBy).TryGetValue(package.Id, out var root)
            ? $"required by '{root}', which PluginCatalog:InstallByDefault names"
            : null;
    }
}
