using System.Collections.Immutable;

namespace MeshWeaver.PluginCatalog;

/// <summary>
/// 🚨 THE predicate behind both release gates (#1754 deployment, #1755 build): <i>given a target
/// release, and given a set of packages, is every one of them AVAILABLE for that release?</i>
///
/// <para>It is stated once, here, and consumed three ways — the portals' self-update poll, CD's
/// own post-promote assertion, and the CI build gate — because a rule that only one caller
/// honours is not a rule. Everything about it is PURE: the caller supplies the target and an
/// <see cref="ReleaseArtifacts">observation</see> of what the artifact stores actually hold, and
/// this class decides. That is what lets an in-process service, an HTTP endpoint and a CI step
/// reach the identical verdict without three copies of the reasoning.</para>
///
/// <para><b>"Available" has exactly two forms</b>, and they are gated differently on purpose:</para>
/// <list type="number">
/// <item><description><b>Content package</b> — a published, SEALED bake under the TARGET release's
/// framework identity (<c>prebuilt-bundles/&lt;identity&gt;/&lt;source&gt;/</c>, with its
/// <c>_complete</c> sentinel written strictly last). Absent ⇒ the instance Roslyn-compiles that
/// content at boot, which is the regression (#1347 → #1660) the bake lane exists to prevent, and
/// a type that fails to compile parks its hub for the whole activation budget. The identity match
/// is EXACT — the strict-MVID rule of <c>PrebuiltAssemblySeeder.DeclineReason</c> — and it is
/// expressed here as "the bundle is sealed under the target's identity", never re-derived.</description></item>
/// <item><description><b>Compiled module</b> — a build whose <c>MinMeshVersion</c> FLOOR is
/// satisfied by the target version. Modules bind by simple name and their contract is API
/// compatibility, so the gate is the semver floor
/// (<see cref="ModulePlatformFloor.DeclineReason(string?, string?)"/>), never MVID equality —
/// MVID has been diagnostic-only for modules since #1664. Applying the bundle rule here would
/// forbid every ex-post Store install across platform versions.</description></item>
/// </list>
///
/// <para>🚨 <b>It fails SAFE.</b> "Cannot determine" is NOT "clear to proceed". When the target's
/// framework identity cannot be resolved, or the artifact store could not be read, every package
/// answers <see cref="PackageAvailabilityKind.Indeterminate"/> and the verdict is NOT updatable —
/// with a reason that says so in those words. An availability failure is never dressed up as a
/// compatibility verdict: <see cref="PackageAvailabilityKind.Indeterminate"/> and
/// <see cref="PackageAvailabilityKind.ContentBakeMissing"/> are different answers to different
/// questions, and a caller that cannot tell them apart cannot tell an outage from an incompatible
/// release.</para>
/// </summary>
public static class ReleaseAvailability
{
    /// <summary>
    /// Is <paramref name="target"/> a release every one of <paramref name="packages"/> can survive,
    /// given <paramref name="artifacts"/>? Pure, total, and never throws — the answer for an
    /// unreadable observation is a NOT-updatable verdict whose reasons name the unreadability.
    /// </summary>
    /// <param name="target">The candidate release: its version, and the framework identity its
    /// image resolves (null/blank ⇒ indeterminate).</param>
    /// <param name="packages">What must survive the roll — an environment's installed set for the
    /// deployment gate, a repo's declared upstreams for the build gate.</param>
    /// <param name="artifacts">What the artifact stores were observed to hold for that target.</param>
    public static UpdatabilityVerdict IsUpdatable(
        ReleaseTarget target,
        IEnumerable<RequiredPackage> packages,
        ReleaseArtifacts artifacts)
    {
        var required = packages?.ToImmutableArray() ?? [];

        // The observation itself is unusable: say THAT, once, about every package. Answering
        // "content bake missing" here would report an outage as an incompatibility — the exact
        // conflation #1754 forbids.
        var blocked = IndeterminateReason(target, artifacts);
        if (blocked is not null)
            return new UpdatabilityVerdict(
                false,
                [.. required.Select(p => new PackageAvailability(
                    p.Name, PackageAvailabilityKind.Indeterminate, blocked))],
                blocked);

        var verdicts = required
            .Select(p => Evaluate(p, target, artifacts))
            .ToImmutableArray();

        var blockers = verdicts.Where(v => !v.IsAvailable).ToImmutableArray();
        return new UpdatabilityVerdict(
            blockers.Length == 0,
            verdicts,
            blockers.Length == 0
                ? null
                : string.Join("; ", blockers.Select(b => $"{b.Package}: {b.Reason}")));
    }

    /// <summary>
    /// Why the whole observation is unusable, or null when it can be reasoned about. Kept separate
    /// from the per-package rules so "we could not look" can never be mistaken for "we looked and
    /// it is not there".
    /// </summary>
    private static string? IndeterminateReason(ReleaseTarget target, ReleaseArtifacts artifacts)
    {
        if (artifacts.ReadFailure is { Length: > 0 } failure)
            return $"the artifact catalogue for release {Describe(target)} could not be read "
                   + $"({failure}) — cannot determine availability, which is not clearance to proceed";
        if (string.IsNullOrWhiteSpace(target.Version))
            return "no target release version was given — cannot determine availability, which is "
                   + "not clearance to proceed";
        if (string.IsNullOrWhiteSpace(target.FrameworkIdentity))
            return $"the framework identity of release {target.Version} is not resolvable — no "
                   + "content bake has been published for it, so nothing can be shown adoptable; "
                   + "cannot determine availability, which is not clearance to proceed";
        return null;
    }

    private static PackageAvailability Evaluate(
        RequiredPackage package, ReleaseTarget target, ReleaseArtifacts artifacts)
    {
        // Modules first: a floor that EXCEEDS the target is a definite incompatibility, and saying
        // so is more useful than "its bake is missing" even when both hold.
        if (ModulePlatformFloor.DeclineReason(package.MinMeshVersion, target.Version) is { } floor)
            return new PackageAvailability(
                package.Name, PackageAvailabilityKind.ModuleFloorExceedsTarget, floor);

        if (package.HasContent && !artifacts.SealedBundles.Contains(package.BundleName))
            return new PackageAvailability(
                package.Name,
                PackageAvailabilityKind.ContentBakeMissing,
                $"no sealed content bake for framework identity {target.FrameworkIdentity} — the "
                + $"bundle '{package.BundleName}' is not published for release {target.Version}, so "
                + "this instance would recompile it at boot");

        return new PackageAvailability(package.Name, PackageAvailabilityKind.Available, null);
    }

    private static string Describe(ReleaseTarget target) =>
        string.IsNullOrWhiteSpace(target.Version) ? "<unspecified>" : target.Version;
}

/// <summary>
/// The candidate release a gate is asked about: the platform version being rolled to, and the
/// framework build identity the image resolves. Both are needed — the version gates module
/// floors, the identity gates content bakes — and neither substitutes for the other.
/// </summary>
/// <param name="Version">The platform version tag, e.g. <c>3.0.0-rc4.ci.4049</c>.</param>
/// <param name="FrameworkIdentity">The framework build identity (<c>s&lt;hash&gt;</c> /
/// <c>g&lt;sha&gt;</c>) that release's image resolves, or null when it could not be resolved.</param>
public sealed record ReleaseTarget(string? Version, string? FrameworkIdentity);

/// <summary>
/// One thing that must survive the roll. The deployment gate builds these from an environment's
/// install records; the build gate builds them from a repo's declared upstreams.
/// </summary>
/// <param name="Name">How the package is named to a human in the refusal.</param>
/// <param name="BundleName">The bake bundle's base name — the package id the bake writes as
/// <c>&lt;id&gt;.zip</c> and lists in the <c>_complete</c> sentinel.</param>
/// <param name="MinMeshVersion">The compiled module's declared platform floor, or null for a
/// content-only package — and null too when the caller has decided the floor is not a REGRESSION
/// here. 🚨 SemVer puts <c>3.0.0-rc4.ci.4049</c> below <c>3.0.0</c>, so a floor judged absolutely
/// can be unmet on the platform an environment already runs; holding on that would freeze it on
/// every release forever. The caller passes a floor only when the RUNNING platform satisfies it,
/// which makes the gate a regression check and leaves it firing exactly where it should — on a
/// rollback below a module's floor.</param>
/// <param name="HasContent">Whether the package ships NodeType content that must be baked. False
/// for a module-only package, whose whole gate is the floor.</param>
public sealed record RequiredPackage(
    string Name, string BundleName, string? MinMeshVersion = null, bool HasContent = true);

/// <summary>
/// What the artifact stores were OBSERVED to hold for one target release. Deliberately a value:
/// the observation is made by whoever can reach the store (a mounted bundle root, the registry
/// index, a CI storage probe) and the rules above never do IO, so every caller reasons identically.
/// </summary>
/// <param name="SealedBundles">Bundle base names published under the target's framework identity in
/// a SEALED source directory — a <c>_complete</c> sentinel present and every bundle it lists
/// actually there. An unsealed or torn directory contributes nothing, exactly as the boot seeder
/// treats it.</param>
/// <param name="ReadFailure">Why the observation could not be made, or null when it was made.
/// Non-null forces every verdict to <see cref="PackageAvailabilityKind.Indeterminate"/>.</param>
public sealed record ReleaseArtifacts(
    ImmutableHashSet<string> SealedBundles,
    string? ReadFailure = null)
{
    /// <summary>An observation that failed — the fail-safe constructor.</summary>
    public static ReleaseArtifacts Unreadable(string reason) =>
        new(ImmutableHashSet.Create<string>(StringComparer.OrdinalIgnoreCase), reason);

    /// <summary>An observation of the given sealed bundle names, case-insensitively matched (the
    /// bake writes file names, and the stores this runs against are not all case-sensitive).</summary>
    public static ReleaseArtifacts Of(IEnumerable<string> sealedBundles) =>
        new(
            ImmutableHashSet.CreateRange(
                StringComparer.OrdinalIgnoreCase,
                sealedBundles.Select(StripBundleExtension)),
            null);

    /// <summary>The sentinel lists file names (<c>Store.zip</c>); packages are named by id
    /// (<c>Store</c>). One place converts, so no caller has to remember which side it holds.</summary>
    private static string StripBundleExtension(string name) =>
        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}

/// <summary>Why one package can or cannot survive the target release.</summary>
public enum PackageAvailabilityKind
{
    /// <summary>The package has a usable artifact for the target release.</summary>
    Available,

    /// <summary>No sealed content bake exists under the target's framework identity, so the
    /// instance would Roslyn-compile this package's NodeTypes at boot.</summary>
    ContentBakeMissing,

    /// <summary>The compiled module declares a platform floor the target release does not
    /// satisfy — a definite incompatibility, not an absence.</summary>
    ModuleFloorExceedsTarget,

    /// <summary>🚨 Availability could NOT be determined. Never "clear to proceed", and never to be
    /// reported as an incompatibility: this is the answer when the catalogue is unreachable or the
    /// target release has no resolvable framework identity.</summary>
    Indeterminate,

    /// <summary>
    /// The COMBO gate ran this module's content inside the candidate image and it did not survive
    /// — it failed to install, to compile, to render, or its Tests area went red
    /// (<see cref="ComboVerdictKind.Red"/>). A definite incompatibility, like
    /// <see cref="ModuleFloorExceedsTarget"/> and unlike <see cref="Indeterminate"/>: the gate
    /// looked, and the answer is about the release rather than about our ability to see it.
    ///
    /// <para>🚨 Appended, never inserted: every member before it keeps its ordinal, so a verdict
    /// serialized by an older build still deserializes correctly.</para>
    /// </summary>
    ComboVerificationFailed,
}

/// <summary>One package's answer.</summary>
/// <param name="Package">The package, as named to a human.</param>
/// <param name="Kind">The verdict.</param>
/// <param name="Reason">Why, in one sentence — null only when available.</param>
public sealed record PackageAvailability(string Package, PackageAvailabilityKind Kind, string? Reason)
{
    /// <summary>Whether this package clears the gate.</summary>
    public bool IsAvailable => Kind == PackageAvailabilityKind.Available;
}

/// <summary>
/// The gate's answer: whether the roll may proceed, every package's reason, and a one-line summary
/// for the refusal a human reads. <see cref="IsUpdatable"/> false with a null
/// <see cref="HoldReason"/> is impossible by construction — a refusal always says why.
/// </summary>
/// <param name="IsUpdatable">True only when every package is available.</param>
/// <param name="Packages">Every package's verdict, including the available ones (a gate that
/// reports only failures cannot show that it looked at anything).</param>
/// <param name="HoldReason">The joined reasons, or null when updatable.</param>
/// <param name="NotEnforcedReason">Set only when the gate does not APPLY to this deployment at all
/// — see <see cref="NotEnforced"/>. Never a way to pass a gate that does apply.</param>
public sealed record UpdatabilityVerdict(
    bool IsUpdatable,
    ImmutableArray<PackageAvailability> Packages,
    string? HoldReason,
    string? NotEnforcedReason = null)
{
    /// <summary>
    /// 🚨 The gate does not APPLY here — the deployment consumes no CI bakes at all (no
    /// <c>PreWarm:PrebuiltBundleRoot</c>), so it already compiles its content at every boot and
    /// holding the update could only freeze it forever, which is the outage this gate exists to
    /// avoid rather than to cause.
    ///
    /// <para>This is deliberately NOT the same thing as passing. It is updatable with a stated
    /// reason the caller must LOG and SURFACE, so "nothing is gating this environment" is visible
    /// rather than inferred from a green tick. It is the one applicability answer; every other
    /// unknown is <see cref="PackageAvailabilityKind.Indeterminate"/>, which HOLDS.</para>
    /// </summary>
    public static UpdatabilityVerdict NotEnforced(string reason) =>
        new(true, [], null, reason);

    /// <summary>
    /// 🚨 The gate could not RUN — it is not wired into this host at all.
    ///
    /// <para>This is a HOLD, and it is deliberately not <see cref="NotEnforced"/>. That answer
    /// exists for the one stated applicability exemption (a deployment consuming no CI bakes);
    /// reusing it for a wiring failure is the trap this repo has been bitten by repeatedly — an
    /// <c>if: vars.X != ''</c> that skips green, a health check reporting Healthy while the bake
    /// is <c>NotStarted</c>. A gate that cannot run must never look like a gate that passed, and
    /// "the service is not registered" is the purest possible case of cannot-run: there is no
    /// verdict at all, only the absence of one.</para>
    ///
    /// <para>It reports as <see cref="IsIndeterminate"/> — an availability failure to FIX, never
    /// a compatibility verdict about the release — so the surfaces that already distinguish the
    /// two do so here without changing.</para>
    /// </summary>
    public static UpdatabilityVerdict Unavailable(string reason) =>
        new(false,
            [new PackageAvailability("(gate)", PackageAvailabilityKind.Indeterminate, reason)],
            reason);

    /// <summary>The packages that block the roll.</summary>
    public IEnumerable<PackageAvailability> Blockers => Packages.Where(p => !p.IsAvailable);

    /// <summary>
    /// True when the hold is an "I could not look" rather than "I looked and it is incompatible".
    /// Callers surface this differently on purpose: an unreachable catalogue is an availability
    /// incident to fix, an incompatible package is a release to re-bake.
    /// </summary>
    public bool IsIndeterminate =>
        Packages.Any(p => p.Kind == PackageAvailabilityKind.Indeterminate);
}
