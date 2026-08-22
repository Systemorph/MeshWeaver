#pragma warning disable CS1591

using System.Collections.Generic;
using Memex.Portal.Shared.SelfUpdate;
using MeshWeaver.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>"Cannot verify" and "verified as nothing to verify" are different states, and only the
/// first may hold.</b>
///
/// <para>The release-availability gate (#1754) fails closed, and the first cut of its
/// unwired-gate hold drew that rule one state too wide: it held on ANY host with no gate
/// registered, including deployments the gate was never going to protect. Caught by
/// <c>SelfUpdateRollFloorTest</c>, whose three roll-expecting cases went red — and the serious one
/// was <c>NeverRolled_IsNotHeld</c>, because holding there means a first roll can never
/// happen.</para>
///
/// <para><see cref="ReleaseAvailabilityService.NotApplicableReason"/> is the line between them,
/// and it is decided from CONFIGURATION alone — which is what lets a caller with no instance of
/// the service reach the same answer a registered one would. These pin that rule; the behavioural
/// halves live in <c>SelfUpdateAvailabilityGateTest</c>.</para>
/// </summary>
public class ReleaseGateApplicabilityTest
{
    private static IConfiguration With(string? bundleRoot) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ShippedPrebuiltBundles.PublishedRootConfigKey] = bundleRoot,
        }).Build();

    /// <summary>
    /// A deployment that consumes CI bakes has something an unwired gate has failed to verify —
    /// so it is unverifiable, and the hold stands.
    /// </summary>
    [Fact]
    public void ADeploymentThatConsumesBakes_IsInScopeOfTheGate()
    {
        Assert.Null(ReleaseAvailabilityService.NotApplicableReason(With("/data/prebuilt-bundles")));
    }

    /// <summary>
    /// A deployment with no bundle root already compiles its content at every boot, so a REGISTERED
    /// gate answers NotEnforced for it. Its absence is therefore the same answer reached from
    /// configuration, not an unanswered question — and holding on it would freeze an environment
    /// the gate could never have protected.
    /// </summary>
    [Fact]
    public void ADeploymentThatConsumesNoBakes_HasNothingToEnforce_AndSaysWhy()
    {
        var reason = ReleaseAvailabilityService.NotApplicableReason(With(null));

        Assert.NotNull(reason);
        // The reason must NAME the key, or an operator cannot tell "not applicable" from "broken".
        Assert.Contains(ShippedPrebuiltBundles.PublishedRootConfigKey, reason);
    }

    /// <summary>
    /// Blank is absent. An empty <c>PreWarm__PrebuiltBundleRoot=</c> is a deployment that meant to
    /// unset the key, never a request to gate against the process's working directory.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankBundleRoot_ReadsAsAbsent(string blank)
    {
        Assert.NotNull(ReleaseAvailabilityService.NotApplicableReason(With(blank)));
    }

    /// <summary>
    /// No configuration at all reads as "nothing configured" rather than throwing — the caller is a
    /// poller tick, and an exception there would kill the tick that was supposed to decide.
    /// </summary>
    [Fact]
    public void NoConfigurationAtAll_IsNotApplicable_NeverAThrow()
    {
        Assert.NotNull(ReleaseAvailabilityService.NotApplicableReason(null));
    }
}
