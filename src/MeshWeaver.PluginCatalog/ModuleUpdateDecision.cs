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

    /// <summary>The registry serves bundles for a DIFFERENT framework MVID — skipped
    /// silently-with-log; the bundle becomes relevant after the next image roll.</summary>
    SkipForeignFramework,

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
/// </summary>
public static class ModuleUpdateDecision
{
    /// <summary>
    /// Decides for one module-declaring installed package.
    ///
    /// <para>Order matters and is deliberate: no-bundle before the framework gate (a registry that
    /// serves nothing for this package says nothing about frameworks), the framework gate before
    /// everything stateful (a foreign-MVID index makes every other question moot — and the skip is
    /// silent-with-log, becoming relevant only after the next image roll), the uninstalled check
    /// before up-to-date (a disabled entry may still carry the served version, and "up to date"
    /// would misname the operator's choice), and the policy LAST — so a policy skip is only ever
    /// reported when an update genuinely would have landed.</para>
    /// </summary>
    /// <param name="bundleVersion">The version the registry's bundle index serves for this package,
    /// or null when it lists no bundle.</param>
    /// <param name="registryFrameworkMvid">The framework MVID the registry's index advertises.</param>
    /// <param name="frameworkGate">Returns WHY a framework identity may not load here, or null when
    /// it may — production passes <c>PrebuiltAssemblySeeder.DeclineReason</c> so there is never a
    /// second notion of framework identity.</param>
    /// <param name="landed">This deployment's activation entry for the module, or null when it was
    /// never landed (which includes "installed before the module lane existed" — those heal by
    /// landing).</param>
    /// <param name="policyDecline">Why the deployment's update policy declines unattended landing,
    /// or null when it allows it (<see cref="IModuleUpdatePolicy"/>; null when no policy is
    /// registered — the platform default is auto-update).</param>
    public static ModuleUpdateVerdict Decide(
        string? bundleVersion,
        string? registryFrameworkMvid,
        Func<string?, string?> frameworkGate,
        ModuleActivationEntry? landed,
        string? policyDecline)
    {
        if (string.IsNullOrWhiteSpace(bundleVersion))
            return new(ModuleUpdateAction.SkipNoBundle,
                "the registry lists no bundle for this package");

        if (frameworkGate(registryFrameworkMvid) is { } foreign)
            return new(ModuleUpdateAction.SkipForeignFramework, foreign);

        if (landed is { Enabled: false })
            return new(ModuleUpdateAction.SkipUninstalled,
                "the module was uninstalled on this deployment; an unattended update must not "
                + "re-install it");

        // Already landed, still loadable against the RUNNING framework, and at the served version:
        // nothing travels. A landed entry whose recorded MVID the gate refuses is NOT up to date —
        // its bytes stopped loading at the last image roll, and re-landing current ones is exactly
        // the heal this lane exists for.
        if (landed is { } current
            && frameworkGate(current.FrameworkMvid) is null
            && string.Equals(current.Version, bundleVersion, StringComparison.OrdinalIgnoreCase))
            return new(ModuleUpdateAction.SkipUpToDate,
                $"version {bundleVersion} is already landed for the running framework");

        // Never roll BACK unattended: a registry serving an older version than what is landed is a
        // deliberate operator situation (a pinned rollback, a lagging registry), and silently
        // downgrading a running deployment is not this lane's call. Only comparable when the landed
        // bytes still load here — stale-MVID bytes are dead weight whatever their version says.
        if (landed is { Version.Length: > 0 } newer
            && frameworkGate(newer.FrameworkMvid) is null
            && NuGetVersionComparer.Instance.Compare(bundleVersion, newer.Version) < 0)
            return new(ModuleUpdateAction.SkipOlder,
                $"the registry serves {bundleVersion} but {newer.Version} is landed — never "
                + "rolled back unattended");

        // 🚨 The policy gates UPGRADES ONLY — a loadable landing moving to a different version.
        // Two landings are deliberately EXEMPT, because gating them turns "no unattended updates"
        // into "the module breaks":
        //   • a FIRST landing (landed == null) COMPLETES an install the operator's own surfaces
        //     already sanctioned (a Provision click, the default-install config) — withholding it
        //     ships a package whose binary half never arrives;
        //   • a stale-MVID RE-LAND heals an image roll the operator chose: the old bytes stopped
        //     loading with that roll, and refusing the heal leaves the module dead until someone
        //     notices. Keeping what was installed WORKING is not an update.
        if (policyDecline is not null
            && landed is { } upgraded
            && frameworkGate(upgraded.FrameworkMvid) is null)
            return new(ModuleUpdateAction.SkipPolicy, policyDecline);

        return new(ModuleUpdateAction.Land,
            landed is null
                ? $"never landed here — landing {bundleVersion}"
                : $"landing {bundleVersion} (current: {landed.Version ?? "unknown"}"
                  + (frameworkGate(landed.FrameworkMvid) is null ? ")" : ", framework-stale)"));
    }
}
