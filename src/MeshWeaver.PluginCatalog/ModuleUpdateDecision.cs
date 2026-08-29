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
    /// <param name="landedBytesPresent">
    /// 🚨 Whether the entry's landed assembly is actually ON DISK — production passes
    /// <see cref="ModuleActivationBoot.LandedModuleDllExists"/> bound to the module root, the same
    /// resolution rule boot itself uses, so there is never a second notion of "landed".
    ///
    /// <para>Required, not optional-defaulting-to-true, and #2417 is why. Until this parameter
    /// existed the "already landed" branch below was decided from the entry's recorded VERSION
    /// alone — a string in a sidecar — and the bytes were never stat'd. That made every way a
    /// landed binary can go missing PERMANENT and SILENT on every deployment: a recreated
    /// <c>Modules:Root</c> volume, a half-completed landing, a generation directory collected out
    /// from under its entry. The record said "module X at version V", the disk said nothing, and
    /// no reconcile would ever look again. A default of <c>true</c> would keep exactly that
    /// answer as the one a careless caller gets.</para>
    ///
    /// <para>Three other surfaces already diagnose this state and all three say "re-install"
    /// (<see cref="ModuleActivationStatus"/>, <c>RequiredModuleStatus.StoreReason</c>,
    /// <see cref="ModuleActivationBoot"/>'s skip reason). Nothing on the install path acted on it.
    /// This is that action.</para>
    /// </param>
    public static ModuleUpdateVerdict Decide(
        string? bundleVersion,
        string? bundleMinMeshVersion,
        Func<string?, string?> platformGate,
        ModuleActivationEntry? landed,
        string? policyDecline,
        Func<ModuleActivationEntry, bool> landedBytesPresent)
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

        // Both version checks go through the ONE comparer, so "equal" and "older" cannot disagree
        // about what a version string means: manifests legitimately carry two-part versions, and
        // string equality would read a landed "1.2.0" against a served "1.2" as an update — a
        // re-land on every reconcile, forever (Copilot catch on the ordinal-equality draft).
        var landedComparison = landed is { Version.Length: > 0 }
            ? NuGetVersionComparer.Instance.Compare(bundleVersion, landed.Version)
            : (int?)null;

        // Already landed at the served version: nothing travels — PROVIDED the bytes are there.
        // The version alone still keys the no-op (landed bytes stay loadable across platform
        // builds, simple-name binding, so there is no "re-land the same version for a new build"
        // case) — but a version is a claim about the disk, not the disk.
        //
        // 🚨 #2417: this branch used to end here, and that single missing question is what made
        // the whole lane SELF-SEALING. A recorded version with no assembly behind it answered
        // "up to date" forever, on every deployment and every reconcile, while the package was in
        // fact half-installed. Note the ordering: SkipUninstalled above still wins, because a
        // disabled entry with no bytes is a completed uninstall, not damage to repair.
        if (landedComparison == 0)
            return landedBytesPresent(landed!)
                ? new(ModuleUpdateAction.SkipUpToDate, $"version {bundleVersion} is already landed")
                : new(ModuleUpdateAction.Land,
                    $"version {bundleVersion} is recorded as landed but its assembly is ABSENT — "
                    + "the landing never completed or its bytes were lost; re-landing (no restart "
                    + "would have fixed it, and nothing else would ever have looked again)");

        // Never roll BACK unattended: a registry serving an older version than what is landed is a
        // deliberate operator situation (a pinned rollback, a lagging registry), and silently
        // downgrading a running deployment is not this lane's call.
        if (landedComparison < 0)
            return new(ModuleUpdateAction.SkipOlder,
                $"the registry serves {bundleVersion} but {landed!.Version} is landed — never "
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
