using System;
using System.IO;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for the release-broadcast chain (<c>#2235</c>) — CD's promoted build →
/// the control instance's webhook inbox → HMAC verify → one <c>repository_dispatch</c> per
/// subscriber. Every joint of that chain is switched on by a CONFIGURATION KEY, and every one of
/// them is silent when it is off: an unlisted inbox target answers 404 (identical to a wrong URL),
/// an empty subscriber set reports <c>0 dispatched, 0 failed</c> (identical to a mesh that is not
/// the control instance).
///
/// <para>🚨 <b>The failure this file exists for.</b> <c>FrameworkBroadcast:Subscribers</c> — then
/// the only source of the subscriber set — was rendered by <b>no chart in either repo</b> for the
/// whole life of the feature, so no deployment could set it. Every broadcast ran against an empty
/// set and logged it at <c>Information</c> as normal. Nothing was red, because a key that no chart
/// renders cannot be distinguished from a key a deployment chose to leave empty. The companion
/// defect was prose: <c>main-cd.yml</c> and the CD contract both said the set "lives in the Hosting
/// fleet registry on memex", and no such registry was ever built — so every reader who went looking
/// for the seam was sent to a mechanism that does not exist. (Since 2026-09-03 the subscriber set is
/// no longer a key at all — it is derived from the <c>Hosting/Deployment</c> records — so that joint
/// left this ledger; the remaining joints are still configuration and still silent when off.)</para>
///
/// <para><see cref="PlatformReleaseNotifyGuard"/> pins the SENDER (CD's notify leg cannot skip or
/// downgrade a failed delivery). This file pins the RECEIVER's provisioning: each joint's key must
/// be reachable by a deploy, and the key each guard entry names must still be the key the code
/// reads — which is why the entries reference the readers' constants instead of restating
/// them.</para>
///
/// <para>The ledger's CHART fact (<c>EveryConfigKeyTheReleaseChainReads_IsRenderedByTheChart</c>)
/// moved to MeshWeaver.Deploy (test/MeshWeaver.Deploy.ChartGuards/), with the deployment chart it reads — the platform repository carries no deployment knowledge.</para>
/// </summary>
public class ReleaseDeliveryChainGuard
{
    private const string Workflow = ".github/workflows/main-cd.yml";
    private const string ContractDoc =
        "src/MeshWeaver.Documentation/Data/Architecture/ContinuousDeliveryContract.md";

    /// <summary>
    /// 🚨 THE COMPANION DEFECT — prose naming a mechanism that does not exist. `main-cd.yml` and the
    /// CD contract are the two places a reader goes to ask "who is subscribed?", and for the whole
    /// life of the feature both answered "the Hosting fleet registry on memex" — a registry with no
    /// node type, no reader and no writer in any repo. That sentence is why the empty subscriber set
    /// survived two rounds of investigation into why nothing was delivered. Both must name the source
    /// that actually exists — since 2026-09-03 the <c>Hosting/Deployment</c> records' registry-source
    /// mounts (<c>pluginRepos[].isRegistrySource</c>), read from the mesh by the Hosting module's
    /// inbox watcher — so the next reader lands on something they can set (a record, not a key).
    /// </summary>
    [Fact]
    public void TheDocumentedSubscriberSource_IsTheOneThatExists()
    {
        var root = RepoPaths.Root;

        foreach (var file in new[] { Workflow, ContractDoc })
        {
            var text = File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(
                text.Contains("Hosting/Deployment", StringComparison.Ordinal)
                && text.Contains("isRegistrySource", StringComparison.Ordinal),
                $"{file} explains where the release wave's subscribers come from and never names the "
                + "source that exists — the Hosting/Deployment records' registry-source mounts "
                + "(pluginRepos[].isRegistrySource). It used to name a Hosting fleet registry (never "
                + "built) and then a FrameworkBroadcast__Subscribers__N config list (retired); pointing "
                + "readers at a source that does not exist is what kept #2235's empty subscriber set "
                + "invisible through two investigations.");
        }
    }

    // 🚨 THE THIRD JOINT — the platform-build webhook secret, provisioned through a Key Vault
    // SecretProviderClass rather than the ConfigMap — was pinned here against the example
    // environment's template. That template moved to Systemorph/Memex
    // (deployments/aks/example/) with the core-slimming carve, and its guard moved with it:
    // scripts/check-example-secret-template.py, in that repository's required Build (Release).
}
