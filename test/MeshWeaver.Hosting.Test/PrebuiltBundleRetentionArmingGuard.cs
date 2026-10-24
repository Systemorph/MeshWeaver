using System;
using MeshWeaver.Hosting;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// #3963: deletion in the prebuilt-bundle store is NEVER armed by omission.
///
/// <para><b>The defect this pins.</b> <see cref="PrebuiltBundleRetention.Delete"/> defaults to
/// <c>true</c>, so an instance that mounts <c>PreWarm:PrebuiltBundleRoot</c> and says nothing else
/// sweeps ARMED. Worse, the knob that would disarm it was unreachable: the portal ConfigMap
/// template enumerates every key it renders EXPLICITLY — there is no catch-all range over
/// <c>.Values.config</c> — so a values file, a deployment overlay or a Deployment record's
/// <c>extraPortalConfig</c> setting <c>PreWarm__PrebuiltBundleRetention__Delete</c> reached no
/// container, with nothing erroring anywhere. The same silent-drop shape cost the CI-bake lane
/// months of inertness (#1660 WS3) and twelve OpenRouter model keys (#2203); here the omission does
/// not merely disable a feature, it DELETES.</para>
///
/// <para><b>The invariant, stated so it survives either fix.</b> Deletion must be off unless
/// somebody wrote it down. That holds if the CODE default is already <c>false</c>, or if the chart
/// binds the key with an explicit <c>default "false"</c>. It does NOT hold when the code default is
/// <c>true</c> and the chart is silent — which is exactly the state this guard was written for. So
/// the guard reads the live default off the type rather than assuming it, and only then demands the
/// chart line.</para>
///
/// <para>🚨 The guard follows <see cref="PrebuiltBundleRetentionExtensions.DeleteConfigKey"/>, not a
/// copied string: renaming the config key moves the guard's subject with it instead of leaving a
/// check that passes having verified nothing.</para>
///
/// <para>The CHART fact (<c>DeletionIsNeverArmedByOmission</c>) moved to MeshWeaver.Plugins (src/Memex.Hosts.Test/Chart/), with the deployment chart it reads — the platform repository carries no deployment knowledge.
/// See Doc/Architecture/PrebuiltBundleRetention.</para>
/// </summary>
public class PrebuiltBundleRetentionArmingGuard
{
    /// <summary>
    /// The report NAMES ITS DENOMINATOR, so a sweep that examined nothing cannot read as a sweep
    /// that found nothing. This is the same distinction the rest of the fleet gets wrong: "0
    /// collectable over 482 identities" and "0 collectable because the root was empty" are the same
    /// number and opposite facts. Asserted here on the shipped format string, so a later reword that
    /// dropped the count would be caught even though every rule test would still pass.
    /// </summary>
    [Fact]
    public void TheSummaryFormat_LeadsWithTheCountExamined_NotWithTheCountCollected()
    {
        var empty = new PrebuiltBundleSweepPlan(
            "s-live", "3.1.0-ci.9000", [], [], System.Collections.Immutable.ImmutableDictionary<string, string>.Empty,
            [], null, []);

        Assert.StartsWith("0 identity directory(ies)", empty.Summary, StringComparison.Ordinal);
        Assert.Contains("collectable=0", empty.Summary, StringComparison.Ordinal);
    }
}
