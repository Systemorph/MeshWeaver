using MeshWeaver.PluginCatalog;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>"Already landed" must mean THIS CONTENT AGAINST THIS FRAMEWORK</b> — the consumer half of
/// Systemorph/MeshWeaver.Plugins#931, pinned pure (no registry, no filesystem, no mesh).
///
/// <para><b>The defect.</b> A module's published version encodes its CONTENT only. Rebuild the same
/// source against a new platform and it republishes under the same version — so
/// <see cref="ModuleUpdateDecision"/>, which compared the version alone, answered
/// <see cref="ModuleUpdateAction.SkipUpToDate"/> for an artifact the deployment does not hold, and
/// nothing ever looked again.</para>
///
/// <para><b>It is not hypothetical.</b> Plugins#723: after a platform identity flip, core CD baked
/// all 53 packages, the portals' updater landed ~12 rebuilt modules whose versions had moved — and
/// then went quiet with no new <c>MeshWeaver.AI.OpenAI</c> build, because OpenAI's version had not.
/// Rolling the image anyway crash-looped deterministically (the pre-flip build cannot resolve
/// <c>ProviderModelLister</c> on the new platform, whose registration had moved) and the fleet was
/// held on an old image. The updater "went quiet" for exactly the reason pinned below.</para>
///
/// <para><b>What the producer half writes.</b> The registry records the framework identity of the
/// module bytes when their owning repo's CI publishes them (<c>ModulePublish.Accepted</c> →
/// <c>ModuleLandingService.ShelveModule</c>) and advertises it per bundle on the index
/// (<c>PluginBundleClient.BundleRef.FrameworkMvid</c>); the consumer records what it landed on
/// <see cref="ModuleActivationEntry.FrameworkMvid"/>. These tests compare exactly those two.</para>
///
/// <para>🚨 The FLOOR is untouched and is still never MVID equality — a differing identity makes a
/// bundle NEWER, never UNINSTALLABLE. <c>ModuleUpdateDecisionTest</c> (MeshWeaver.Plugins) keeps
/// the floor, policy and ordering rules; this fixture is only about the identity half.</para>
/// </summary>
public class ModuleUpdateIdentityTest
{
    private const string Running = "3.2.0";

    /// <summary>The build these bytes came from — the flip's "before".</summary>
    private const string OldFramework = "s2b488317c0de0000c0de0000c0de0000";

    /// <summary>The build the platform flipped TO — same source, different bytes.</summary>
    private const string NewFramework = "se0f09bc8f00d0000f00d0000f00d0000";

    /// <summary>The production gate's shape, bound to a fixed running platform version.</summary>
    private static string? Gate(string? floor) => ModulePlatformFloor.DeclineReason(floor, Running);

    private static ModuleActivationEntry Landed(
        string? version, string? frameworkMvid, bool enabled = true) => new()
        {
            Name = "MeshWeaver.AI.OpenAI",
            FrameworkMvid = frameworkMvid,
            Version = version,
            Enabled = enabled,
        };

    private static bool BytesPresent(ModuleActivationEntry _) => true;

    private static bool BytesGone(ModuleActivationEntry _) => false;

    private static ModuleUpdateVerdict Decide(
        string? bundleVersion,
        ModuleActivationEntry? landed,
        string? bundleFrameworkMvid,
        Func<ModuleActivationEntry, bool>? bytes = null) =>
        ModuleUpdateDecision.Decide(
            bundleVersion, bundleMinMeshVersion: "3.0.0", Gate, landed, policyDecline: null,
            bytes ?? BytesPresent, bundleFrameworkMvid);

    /// <summary>The no-op that must stay a no-op: same content, same framework, bytes on disk.</summary>
    [Fact]
    public void SameVersion_SameIdentity_Skips()
    {
        var verdict = Decide("1.1.0", Landed("1.1.0", OldFramework), OldFramework);

        Assert.Equal(ModuleUpdateAction.SkipUpToDate, verdict.Action);
        Assert.Contains("1.1.0", verdict.Reason);
        // The reason states the framework it agreed on — a verdict that merely says "already
        // landed" is what hid the mismatch in the first place.
        Assert.Contains(OldFramework, verdict.Reason);
    }

    /// <summary>
    /// 🚨 <b>#723, reproduced.</b> The registry serves the SAME version built against a DIFFERENT
    /// platform: the bytes on this deployment are the pre-flip build, and skipping is what left the
    /// fleet unable to converge. This is the one case the whole change exists for.
    /// </summary>
    [Fact]
    public void SameVersion_DifferentIdentity_Lands()
    {
        var verdict = Decide("1.1.0", Landed("1.1.0", OldFramework), NewFramework);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
        // 🚨 The reason must NAME WHICH HALF DIFFERED. "version 1.1.0 is already landed" concealing
        // a framework mismatch is the shape of the original bug; a reason that names both
        // identities is what makes the next incident readable from one log line.
        Assert.Contains(OldFramework, verdict.Reason);
        Assert.Contains(NewFramework, verdict.Reason);
        Assert.Contains("1.1.0", verdict.Reason);
    }

    /// <summary>
    /// The convergence proof for the case above: landing records the identity that was compared, so
    /// the very next reconcile agrees. Without this the fix would trade a silent skip for an
    /// unbounded re-download.
    /// </summary>
    [Fact]
    public void AfterLanding_TheNextReconcileSkips()
    {
        var landed = Landed("1.1.0", OldFramework);
        Assert.Equal(ModuleUpdateAction.Land, Decide("1.1.0", landed, NewFramework).Action);

        // What PluginBundleClient.LandFromBundle writes: the ADVERTISED identity, not the archive's
        // requested-lane stamp. That is the whole reason the two sides can ever come to agree.
        var afterLanding = landed with { FrameworkMvid = NewFramework };

        Assert.Equal(
            ModuleUpdateAction.SkipUpToDate, Decide("1.1.0", afterLanding, NewFramework).Action);
    }

    /// <summary>
    /// An entry written before the identity was recorded. Unknown is NOT a match — landing once
    /// records it and the reconcile after that is stable, which is the same shape
    /// <see cref="ModuleActivationEntry.Version"/> already has for a pre-field entry.
    /// </summary>
    [Fact]
    public void SameVersion_LandedIdentityUnknown_Lands()
    {
        var verdict = Decide("1.1.0", Landed("1.1.0", frameworkMvid: null), NewFramework);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
        Assert.Contains("unrecorded", verdict.Reason);
        Assert.Contains(NewFramework, verdict.Reason);
    }

    /// <summary>
    /// 🚨 <b>The asymmetry, and the reason it is not symmetric.</b> A registry that states no
    /// identity offers no evidence a rebuild happened — and landing could never turn that into
    /// evidence, because the registry would state nothing next time either. Answering Land here
    /// would re-download every module on every reconcile, forever, on every deployment pointed at a
    /// pre-#931 registry: the infinite loop, bought for a comparison that still has nothing to
    /// compare. So it skips — and the verdict SAYS the identity could not be checked, because a
    /// bare "already landed" is the sentence that hid the defect. The blind spot is closed where it
    /// is created (part 3 of the agreed shape: a bundle that cannot state what it was built against
    /// is not publishable), never by churning consumers.
    /// </summary>
    [Fact]
    public void SameVersion_ServedIdentityUnknown_SkipsAndSaysItCouldNotBeChecked()
    {
        var verdict = Decide("1.1.0", Landed("1.1.0", OldFramework), bundleFrameworkMvid: null);

        Assert.Equal(ModuleUpdateAction.SkipUpToDate, verdict.Action);
        Assert.Contains("states no framework identity", verdict.Reason);
    }

    /// <summary>Neither side states one: the same answer, for the same reason.</summary>
    [Fact]
    public void SameVersion_NeitherSideStatesAnIdentity_SkipsButSaysSo()
    {
        var verdict = Decide(
            "1.1.0", Landed("1.1.0", frameworkMvid: null), bundleFrameworkMvid: null);

        Assert.Equal(ModuleUpdateAction.SkipUpToDate, verdict.Action);
        Assert.Contains("states no framework identity", verdict.Reason);
    }

    /// <summary>
    /// An empty or blank string is a recorded nothing, not an identity — on EITHER side it must
    /// read exactly like an absent one. A producer that writes "" would otherwise turn the gate
    /// back off while looking populated, and a landed "" would land on every reconcile.
    /// </summary>
    [Theory]
    [InlineData("", null, ModuleUpdateAction.SkipUpToDate)]
    [InlineData(null, "", ModuleUpdateAction.SkipUpToDate)]
    [InlineData("   ", "  ", ModuleUpdateAction.SkipUpToDate)]
    [InlineData(OldFramework, "   ", ModuleUpdateAction.SkipUpToDate)]
    [InlineData("", NewFramework, ModuleUpdateAction.Land)]
    [InlineData("  ", NewFramework, ModuleUpdateAction.Land)]
    public void BlankIdentitiesReadAsUnknown_OnBothSides(
        string? landedMvid, string? servedMvid, ModuleUpdateAction expected)
        => Assert.Equal(expected, Decide("1.1.0", Landed("1.1.0", landedMvid), servedMvid).Action);

    /// <summary>A version that moved lands regardless of identity — the ordinary update path is
    /// untouched by this change.</summary>
    [Fact]
    public void ANewerVersion_LandsWhateverTheIdentitySays()
    {
        Assert.Equal(
            ModuleUpdateAction.Land,
            Decide("1.2.0", Landed("1.1.0", OldFramework), OldFramework).Action);
        Assert.Equal(
            ModuleUpdateAction.Land,
            Decide("1.2.0", Landed("1.1.0", OldFramework), NewFramework).Action);
    }

    /// <summary>
    /// 🚨 An identity difference must never become a DOWNGRADE. The registry serving an older
    /// version is an operator situation, and a differing framework does not make rolling back
    /// unattended any more this lane's call than it was before.
    /// </summary>
    [Fact]
    public void AnOlderServedVersion_IsStillNeverRolledBack()
    {
        var verdict = Decide("1.0.0", Landed("1.1.0", OldFramework), NewFramework);

        Assert.Equal(ModuleUpdateAction.SkipOlder, verdict.Action);
    }

    /// <summary>
    /// Ordering: the ABSENT-assembly diagnosis (#2417) wins over the identity one. Both answer
    /// Land, so nothing is lost — but "its assembly is ABSENT" is the actionable sentence, and a
    /// half-installed module must not be reported as a framework mismatch.
    /// </summary>
    [Fact]
    public void AbsentBytes_AreReportedAsAbsent_NotAsAnIdentityMismatch()
    {
        var verdict = Decide("1.1.0", Landed("1.1.0", OldFramework), NewFramework, BytesGone);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
        Assert.Contains("ABSENT", verdict.Reason);
    }

    /// <summary>
    /// A deliberately uninstalled module stays uninstalled. The identity check sits INSIDE the
    /// same-version branch, below the uninstall check, so an operator's choice is never overturned
    /// by a platform rebuild.
    /// </summary>
    [Fact]
    public void AnUninstalledModule_IsNotReinstalledByAnIdentityDifference()
    {
        var verdict = Decide(
            "1.1.0", Landed("1.1.0", OldFramework, enabled: false), NewFramework);

        Assert.Equal(ModuleUpdateAction.SkipUninstalled, verdict.Action);
    }

    /// <summary>
    /// A FIRST landing is unaffected: there is no recorded identity to disagree with, and the
    /// never-landed branch already lands.
    /// </summary>
    [Fact]
    public void ANeverLandedModule_LandsAsBefore()
    {
        var verdict = Decide("1.1.0", landed: null, NewFramework);

        Assert.Equal(ModuleUpdateAction.Land, verdict.Action);
        Assert.Contains("never landed here", verdict.Reason);
    }

    /// <summary>
    /// 🚨 The omitted argument is the LEGACY shape, stated on purpose so nobody reads the default
    /// as a safety net. Omitting it is indistinguishable from a registry that states no identity,
    /// which is exactly the pre-#931 answer — so every caller must pass it, and the one production
    /// caller (<c>PluginBundleClient.AdoptModule</c>, off <c>BundleRef.FrameworkMvid</c>) does.
    /// The parameter is optional rather than required only because the six-argument form is called
    /// from a suite in another repository (<c>ModuleUpdateDecisionTest</c>, MeshWeaver.Plugins) and
    /// a platform change must not red a satellite's whole CI to land.
    /// </summary>
    [Fact]
    public void OmittingTheIdentityArgument_IsTheLegacyAnswer_NotASafetyNet()
    {
        var legacy = ModuleUpdateDecision.Decide(
            "1.1.0", "3.0.0", Gate, Landed("1.1.0", OldFramework), policyDecline: null,
            BytesPresent);

        Assert.Equal(ModuleUpdateAction.SkipUpToDate, legacy.Action);
        Assert.Equal(
            Decide("1.1.0", Landed("1.1.0", OldFramework), bundleFrameworkMvid: null).Reason,
            legacy.Reason);
    }
}
