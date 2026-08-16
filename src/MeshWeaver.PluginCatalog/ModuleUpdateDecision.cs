using MeshWeaver.Plugin.Packaging;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// The platform's gate on UNATTENDED module landing — the seam through which the module lane rides
/// the EXISTING update-policy surface instead of growing a knob of its own (#1664).
///
/// <para>The memex portals implement this over the admin-editable <c>Admin/UpdatePolicy</c> node
/// (Continuous / Stable / None — the same single policy that governs the platform image roll):
/// <c>Continuous</c>, the platform default, allows unattended module landing; <c>Stable</c> and
/// <c>None</c> decline it, because a deployment that pins its image has chosen to take updates
/// deliberately, and its modules must not run ahead of that choice. A host that registers NO
/// implementation is the platform default: unattended landing is ALLOWED.</para>
///
/// <para>The gate covers only the RECONCILER's unattended lane. An explicit install (the Store's
/// Provision funnel, the default-install seed) lands its module regardless — the operator asked
/// for the package, and the module is part of what they asked for.</para>
/// </summary>
public interface IModuleUpdatePolicy
{
    /// <summary>Why unattended module landing is currently declined, or null when it is allowed.
    /// Cold; emits exactly once per subscription.</summary>
    IObservable<string?> DeclineUnattendedLanding();
}

/// <summary>What the module-update reconcile decided for one installed module package.</summary>
public enum ModuleUpdateAction
{
    /// <summary>Fetch the bundle and land it (restart-as-activation).</summary>
    Land,

    /// <summary>The landed module already matches the registry's bundle — nothing travels.</summary>
    SkipUpToDate,

    /// <summary>The bundle's declared <c>minMeshVersion</c> FLOOR exceeds the running platform —
    /// skipped silently-with-log; the bundle becomes installable after the platform updates.</summary>
    SkipPlatformBelowFloor,

    /// <summary>The deployment's update policy declines unattended landing (Stable/None).</summary>
    SkipPolicy,

    /// <summary>The registry serves no bundle for this package.</summary>
    SkipNoBundle,

    /// <summary>The registry's bundle is OLDER than what is landed — never rolled back
    /// unattended.</summary>
    SkipOlder,

    /// <summary>The module was deliberately uninstalled here (activation entry disabled) — the
    /// reconcile must not fight the operator.</summary>
    SkipUninstalled,
}

/// <summary>One reconcile verdict: the action and the human reason behind it.</summary>
/// <param name="Action">What to do.</param>
/// <param name="Reason">Why — one phrase for the log line.</param>
public sealed record ModuleUpdateVerdict(ModuleUpdateAction Action, string? Reason = null);

/// <summary>
/// THE decision "does this installed module package's bundle land, and why not otherwise" — pure,
/// so the reconciler's behaviour is pinnable without a registry, a filesystem, or a mesh
/// (#1664 Slice C). Every input is a fact the caller already holds; nothing here fetches.
///
/// <para><b>The platform gate is a semver FLOOR, never MVID equality.</b> Modules are ordinary
/// .NET assemblies binding by simple name — their contract is API compatibility, which
/// <c>minMeshVersion</c> expresses. A bundle built against an OLDER platform whose floor this
/// deployment satisfies LANDS (the ex-post Store install across platform versions the lane exists
/// for); MVID equality is bake semantics and stays with the NodeType assembly lane.</para>
/// </summary>
public static class ModuleUpdateDecision
{
    /// <summary>
    /// Decides for one module-declaring installed package.
    ///
    /// <para>Order matters and is deliberate: no-bundle before the floor gate (a registry that
    /// serves nothing for this package declares no floor to check), the floor gate before
    /// everything stateful (an uninstallable bundle makes every other question moot — and the
    /// skip is silent-with-log, becoming relevant when the platform updates), the uninstalled
    /// check before up-to-date (a disabled entry may still carry the served version, and "up to
    /// date" would misname the operator's choice), and the policy LAST — so a policy skip is only
    /// ever reported when an update genuinely would have landed.</para>
    /// </summary>
    /// <param name="bundleVersion">The version the registry's bundle index serves for this package,
    /// or null when it lists no bundle.</param>
    /// <param name="bundleMinMeshVersion">The bundle's declared platform floor, as the index
    /// surfaces it. Null = no constraint.</param>
    /// <param name="platformGate">Returns WHY a declared floor is not satisfied by the running
    /// platform, or null when it is — production passes
    /// <see cref="ModulePlatformFloor.DeclineReason(string?)"/> so there is never a second notion
    /// of the module platform requirement.</param>
    /// <param name="landed">This deployment's activation entry for the module, or null when it was
    /// never landed (which includes "installed before the module lane existed" — those heal by
    /// landing).</param>
    /// <param name="policyDecline">Why the deployment's update policy declines unattended landing,
    /// or null when it allows it (<see cref="IModuleUpdatePolicy"/>; null when no policy is
    /// registered — the platform default is auto-update).</param>
    public static ModuleUpdateVerdict Decide(
        string? bundleVersion,
        string? bundleMinMeshVersion,
        Func<string?, string?> platformGate,
        ModuleActivationEntry? landed,
        string? policyDecline)
    {
        if (string.IsNullOrWhiteSpace(bundleVersion))
            return new(ModuleUpdateAction.SkipNoBundle,
                "the registry lists no bundle for this package");

        if (platformGate(bundleMinMeshVersion) is { } belowFloor)
            return new(ModuleUpdateAction.SkipPlatformBelowFloor, belowFloor);

        if (landed is { Enabled: false })
            return new(ModuleUpdateAction.SkipUninstalled,
                "the module was uninstalled on this deployment; an unattended update must not "
                + "re-install it");

        // Already landed at the served version: nothing travels. The version alone keys this —
        // landed bytes stay loadable across platform builds (simple-name binding), so there is no
        // "re-land the same version for a new build" case.
        if (landed is { } current
            && string.Equals(current.Version, bundleVersion, StringComparison.OrdinalIgnoreCase))
            return new(ModuleUpdateAction.SkipUpToDate,
                $"version {bundleVersion} is already landed");

        // Never roll BACK unattended: a registry serving an older version than what is landed is a
        // deliberate operator situation (a pinned rollback, a lagging registry), and silently
        // downgrading a running deployment is not this lane's call.
        if (landed is { Version.Length: > 0 } newer
            && NuGetVersionComparer.Instance.Compare(bundleVersion, newer.Version) < 0)
            return new(ModuleUpdateAction.SkipOlder,
                $"the registry serves {bundleVersion} but {newer.Version} is landed — never "
                + "rolled back unattended");

        // 🚨 The policy gates UPGRADES ONLY — an existing landing moving to a different version.
        // A FIRST landing (landed == null) is deliberately EXEMPT: it COMPLETES an install the
        // operator's own surfaces already sanctioned (a Provision click, the default-install
        // config), and gating it would ship a package whose binary half never arrives.
        if (policyDecline is not null && landed is not null)
            return new(ModuleUpdateAction.SkipPolicy, policyDecline);

        return new(ModuleUpdateAction.Land,
            landed is null
                ? $"never landed here — landing {bundleVersion}"
                : $"landing {bundleVersion} (current: {landed.Version ?? "unknown"})");
    }
}
