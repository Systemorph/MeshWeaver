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

    /// <summary>
    /// 🚨 RETIRED by #3648 — nothing produces this any more. It meant "the bundle's declared
    /// <c>minMeshVersion</c> floor exceeds the running platform, skipped", and on 2026-09-07 that
    /// skip is what held every production portal on the morning build: the comparator ranks
    /// <c>ci &lt; rc</c>, so an <c>rc</c> floor could never be satisfied by any <c>ci</c> build. The
    /// floor is advisory now (<see cref="Land"/> carries the sentence in its reason) and the link
    /// probe at landing decides. The member stays so a verdict serialized by an older build keeps
    /// its ordinal and a caller compiled against it keeps compiling.
    /// </summary>
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

    /// <summary>
    /// The entry is in FALLBACK — its newest landed generation could not be loaded on this
    /// platform (<see cref="ModuleActivationEntry.UnloadableFrameworkMvid"/>, the boot's link
    /// probe; #3649 keeps the previous generation running) — and the registry still serves that
    /// same build (or states no identity, so a different one cannot be seen). Nothing travels; the
    /// entry is re-examined on every reconcile and lands the moment a build for this platform
    /// appears (#3650, rule R3 of <c>Doc/Architecture/ModuleAdoptionPolicy</c>). Distinct from
    /// <see cref="SkipUpToDate"/> on purpose: "already landed" is the sentence that hid every
    /// identity defect on this lane, and a deployment running its previous generation is not up
    /// to date. Appended, never inserted: the members before it keep their ordinals.
    /// </summary>
    SkipUnloadable,
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
/// <para>🚨 <b>No platform gate here at all (#3648).</b> This decision used to skip a bundle whose
/// declared <c>minMeshVersion</c> floor exceeded the running platform. That string comparison held
/// every production portal on 2026-09-07 (see <see cref="ModulePlatformFloor"/>), so the floor is
/// ADVISORY: it is worded into the <see cref="ModuleUpdateAction.Land"/> reason and logged, and
/// whether the bytes can load is MEASURED where the bytes are — the link probe in
/// <see cref="ModuleLandingService"/> at placement, and <c>MeshBuilder.InstallAssemblies</c> at
/// boot. MVID equality was never the gate either: modules are ordinary .NET assemblies binding by
/// simple name, and a bundle built against an OLDER platform LANDS (the ex-post Store install
/// across platform versions the lane exists for).</para>
///
/// <para>🚨 <b>The framework identity decides whether there is anything NEW to land</b>
/// (Plugins#931 consumer half). A module's published version encodes its CONTENT only, so a rebuild
/// of the same source against a new platform republishes under the SAME version — and a consumer
/// holding the old bytes answered "already landed" and skipped an update it needed. Measured in
/// Plugins#723: after a platform identity flip the updater went quiet with no new
/// <c>MeshWeaver.AI.OpenAI</c> build because its version had not moved, the pre-flip build then
/// crash-looped in DI on the new platform (<c>OpenAICompatibleModelSync</c> could not resolve
/// <c>ProviderModelLister</c>, whose registration had moved), and the fleet was held on an old
/// image. So <see cref="ModuleUpdateAction.SkipUpToDate"/> means <i>this content against this
/// framework</i>, never <i>this content</i>. An identity difference makes a bundle NEWER, never
/// UNINSTALLABLE.</para>
///
/// <para>🚨 <b>An entry in FALLBACK is re-examined against the generation that could not load</b>
/// (#3650, rule R3 of <c>Doc/Architecture/ModuleAdoptionPolicy</c>). When the boot's link probe
/// refuses the newest landed generation and the previous one keeps running (#3649), the entry
/// carries the refused build's identity in
/// <see cref="ModuleActivationEntry.UnloadableFrameworkMvid"/>. The same-version branch then asks
/// the only question that matters to such a deployment — does the registry serve a DIFFERENT build
/// of this version than the one that would not load? — and answers <see cref="ModuleUpdateAction.Land"/>
/// when it does (a build for this platform appeared) and
/// <see cref="ModuleUpdateAction.SkipUnloadable"/> when it does not. Without this the fallback was
/// permanent until the next boot happened to follow a publication, which is the gap the policy
/// names.</para>
/// </summary>
public static class ModuleUpdateDecision
{
    /// <summary>
    /// Decides for one module-declaring installed package.
    ///
    /// <para>Order matters and is deliberate: no-bundle first (a registry that serves nothing for
    /// this package has nothing to decide about), the uninstalled check before up-to-date (a
    /// disabled entry may still carry the served version, and "up to date" would misname the
    /// operator's choice), and the policy LAST — so a policy skip is only ever reported when an
    /// update genuinely would have landed. There is no floor step (#3648): a bundle whose declared
    /// floor exceeds the running platform proceeds to <see cref="ModuleUpdateAction.Land"/> with the
    /// advisory in its reason, and the landing's link probe decides.</para>
    /// </summary>
    /// <param name="bundleVersion">The version the registry's bundle index serves for this package,
    /// or null when it lists no bundle.</param>
    /// <param name="bundleMinMeshVersion">The bundle's declared platform floor, as the index
    /// surfaces it. Null = none declared. ADVISORY (#3648): it is worded into the verdict's reason
    /// and never decides it.</param>
    /// <param name="platformGate">Words the advisory — returns the sentence naming both versions
    /// when the declared floor exceeds the running platform, or null when it does not; production
    /// passes <see cref="ModulePlatformFloor.DeclineReason(string?)"/> so there is never a second
    /// wording of the floor. 🚨 Its answer NEVER changes the action (#3648): the parameter stays
    /// so callers compiled against the previous platform keep binding, and so the log line can say
    /// "declares platform ≥ X; running Y" without a second comparison.</param>
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
    /// <param name="bundleFrameworkMvid">
    /// 🚨 The framework identity the registry states the SERVED module bytes were built against —
    /// the producer's value, as the bundle index advertises it per bundle
    /// (<c>PluginBundleClient.BundleRef.FrameworkMvid</c>), compared against
    /// <see cref="ModuleActivationEntry.FrameworkMvid"/> on the landed entry. This is the input
    /// that makes "already landed" mean <i>this content against this framework</i>
    /// (Plugins#931/#723 — see the type doc).
    ///
    /// <para><b>The rule: a STATED identity that differs from the landed one LANDS.</b> Ordinal
    /// equality, and "the entry recorded none" counts as differing — so an entry written before the
    /// identity was recorded heals by landing once, exactly the shape
    /// <see cref="ModuleActivationEntry.Version"/> already documents for a pre-field entry.</para>
    ///
    /// <para>🚨 <b>The two sides are deliberately NOT symmetric, and the asymmetry is what keeps
    /// this from looping.</b> Landing can cure an unknown on the LANDED side — it writes back the
    /// identity that was compared, so the next reconcile has two known values. It can never cure an
    /// unknown on the SERVED side: a registry that states no identity will state none next time
    /// either, so answering Land there would re-download every module on every reconcile, forever,
    /// on every deployment pointed at a pre-#931 registry. That is the loop, and it is not a price
    /// worth paying for a comparison that would still have nothing to compare.</para>
    ///
    /// <para>So an unstated served identity SKIPS — and the verdict SAYS the identity could not be
    /// checked rather than implying the two agree. It is absence of evidence, not evidence of
    /// agreement, and the place to remove that blind spot is where it is created: part 3 of the
    /// agreed shape makes a bundle that cannot state what it was built against unpublishable, so
    /// the served identity stops being optional at the source instead of being guessed at here.</para>
    ///
    /// <para>Defaulting to null is therefore the LEGACY shape (the answer this decision gave before
    /// Plugins#931), not a fail-safe. Production passes it — <c>PluginBundleClient.AdoptModule</c>
    /// off <c>BundleRef.FrameworkMvid</c> — and any new caller must.</para>
    /// </param>
    public static ModuleUpdateVerdict Decide(
        string? bundleVersion,
        string? bundleMinMeshVersion,
        Func<string?, string?> platformGate,
        ModuleActivationEntry? landed,
        string? policyDecline,
        Func<ModuleActivationEntry, bool> landedBytesPresent,
        string? bundleFrameworkMvid = null)
    {
        if (string.IsNullOrWhiteSpace(bundleVersion))
            return new(ModuleUpdateAction.SkipNoBundle,
                "the registry lists no bundle for this package");

        // 🚨 #3648 — the floor is ADVISORY. A step here used to answer SkipPlatformBelowFloor when
        // the declared floor exceeded the running platform; on 2026-09-07 that string comparison
        // (ci < rc < clean) declined every candidate on every production portal for a day while
        // the measured link probe would have loaded each of them. The sentence still rides the
        // Land reason so the log says what the module claims; nothing branches on it.
        var advisory = platformGate(bundleMinMeshVersion);

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

        // Already landed at the served version: nothing travels — PROVIDED the bytes are there AND
        // they are the bytes for the framework the registry is serving now. A version is a claim
        // about the disk, not the disk; and it is a claim about CONTENT, not about the artifact.
        //
        // 🚨 #2417: this branch used to end at the version. That single missing question is what
        // made the whole lane SELF-SEALING. A recorded version with no assembly behind it answered
        // "up to date" forever, on every deployment and every reconcile, while the package was in
        // fact half-installed. Note the ordering: SkipUninstalled above still wins, because a
        // disabled entry with no bytes is a completed uninstall, not damage to repair.
        //
        // 🚨 Plugins#931/#723: and it used to end at the BYTES. A module rebuilt against a new
        // platform republishes under the same version, so version equality answered "up to date"
        // for an artifact this deployment does not hold — the updater went quiet and the fleet
        // could not converge. The identity is checked AFTER the presence probe on purpose: an
        // absent assembly is the more actionable diagnosis, and both answers are Land anyway.
        if (landedComparison == 0)
        {
            if (!landedBytesPresent(landed!))
                return new(ModuleUpdateAction.Land,
                    $"version {bundleVersion} is recorded as landed but its assembly is ABSENT — "
                    + "the landing never completed or its bytes were lost; re-landing (no restart "
                    + "would have fixed it, and nothing else would ever have looked again)"
                    + WithAdvisory(advisory));

            var landedMvid = string.IsNullOrWhiteSpace(landed!.FrameworkMvid)
                ? null : landed.FrameworkMvid;
            var servedMvid = string.IsNullOrWhiteSpace(bundleFrameworkMvid)
                ? null : bundleFrameworkMvid;
            var unloadableMvid = string.IsNullOrWhiteSpace(landed.UnloadableFrameworkMvid)
                ? null : landed.UnloadableFrameworkMvid;

            // 🚨 #3650 — an entry in FALLBACK is re-examined against the UNLOADABLE generation's
            // identity, not the landed one. The boot could not load the newest generation and runs
            // the previous one (#3649); what this deployment is waiting for is a build of this
            // version for THIS platform. The registry serving the same unloadable build again is
            // nothing new — a download would only re-land bytes the next boot would park again —
            // while ANY other stated identity is a build that appeared, and rule R3 says it lands
            // now, not at the next boot. Before the ordinary identity comparison on purpose: that
            // one compares against FrameworkMvid, whose meaning in fallback is the fallback's
            // business, and in either reading it would answer "already landed" for the one
            // build that gets the module off its previous generation.
            if (unloadableMvid is not null)
            {
                if (servedMvid is null)
                    return new(ModuleUpdateAction.SkipUnloadable,
                        $"version {bundleVersion} is landed but its newest generation (built against "
                        + $"framework {unloadableMvid}) could not be loaded on this platform — the "
                        + "previous generation is running; the registry states no framework identity "
                        + "for what it serves, so a build for this platform cannot be seen from here");
                if (string.Equals(servedMvid, unloadableMvid, StringComparison.Ordinal))
                    return new(ModuleUpdateAction.SkipUnloadable,
                        $"version {bundleVersion} is landed but its newest generation (built against "
                        + $"framework {unloadableMvid}) could not be loaded on this platform — the "
                        + "previous generation is running, and the registry still serves that same "
                        + "build; a build for this platform lands as soon as the registry serves one");
                return new(ModuleUpdateAction.Land,
                    $"version {bundleVersion}'s newest landed generation (built against framework "
                    + $"{unloadableMvid}) could not be loaded on this platform and the previous "
                    + $"generation is running; the registry now serves that version built against "
                    + $"{servedMvid} — a build for this platform appeared; landing it"
                    + WithAdvisory(advisory));
            }

            // 🚨 A STATED served identity is the only evidence a rebuild happened; an unstated one
            // is absence of evidence, and landing could never turn it into evidence — the registry
            // would say nothing next time too. Hence the asymmetry: unknown on the LANDED side
            // lands once and heals (the landing writes the identity back), unknown on the SERVED
            // side skips and SAYS SO. See the parameter doc.
            if (servedMvid is not null && !string.Equals(landedMvid, servedMvid, StringComparison.Ordinal))
                // 🚨 The reason NAMES which half differed. "version X is already landed" hiding a
                // framework mismatch is the exact shape of the bug being fixed here: a verdict
                // that reads as agreement while the two sides describe different artifacts.
                return new(ModuleUpdateAction.Land,
                    $"version {bundleVersion} is landed, but built against framework "
                    + $"{landedMvid ?? "(unrecorded)"} while the registry serves that same version "
                    + $"built against {servedMvid} — same content, different platform build; "
                    + "re-landing so this deployment holds the artifact for the framework it is "
                    + "being served" + WithAdvisory(advisory));

            return new(ModuleUpdateAction.SkipUpToDate,
                servedMvid is null
                    // The registry states no framework identity for these bytes. Not agreement —
                    // no answer. Said out loud, because a bare "already landed" is exactly the
                    // sentence that made the defect unreadable in the logs it was printing to.
                    ? $"version {bundleVersion} is already landed; the registry states no framework "
                      + "identity for it, so a rebuild against a new platform cannot be seen from "
                      + "here"
                    : $"version {bundleVersion} is already landed, built against framework "
                      + $"{servedMvid}");
        }

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
            (landed is null
                ? $"never landed here — landing {bundleVersion}"
                : $"landing {bundleVersion} (current: {landed.Version ?? "unknown"})")
            + WithAdvisory(advisory));
    }

    /// <summary>The floor's sentence as a suffix on a Land reason, or nothing when the running
    /// platform satisfies the declared floor (or none is declared).</summary>
    private static string WithAdvisory(string? advisory) =>
        advisory is null ? string.Empty : $"; {advisory}";
}
