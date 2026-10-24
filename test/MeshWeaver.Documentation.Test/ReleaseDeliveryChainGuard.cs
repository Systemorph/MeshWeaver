using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

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
    private const string SecretTemplate = "deploy/aks/envs/example/secretproviderclass.yaml";

    /// <summary>
    /// The shared HMAC's env key. A literal, unlike every other key here, and the exception is
    /// worth naming rather than hiding: the constant that reads it
    /// (<c>PlatformBuildInboxWatcher.SecretConfigKey</c>) lives in MeshWeaver.Plugins, so this
    /// repository cannot reference it. What this repository DOES own is the template every
    /// environment's SecretProviderClass is copied from — so that is what is pinned.
    /// </summary>
    private const string WebhookSecretEnvKey = "Hosting__PlatformWebhookSecret";

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
        var root = SourceScan.FindRepoRoot();

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

    /// <summary>
    /// 🚨 THE THIRD JOINT, which is provisioned through the OTHER path — a Key Vault secret mounted
    /// by the CSI driver, not a ConfigMap entry — and is therefore invisible to the ledger above. It
    /// is the one joint CD structurally cannot see: the inbox stores the delivery and answers 2xx
    /// whatever the secret is, and the watcher then drops an unverifiable delivery in silence. Every
    /// environment's SecretProviderClass is copied from this template, so an entry quietly lost here
    /// propagates into the next environment stood up, and its only symptom is a release wave that
    /// never starts.
    /// </summary>
    [Fact]
    public void TheSharedHmacIsProvisionedByTheSecretTemplate()
    {
        var root = SourceScan.FindRepoRoot();
        var template = File.ReadAllText(
            Path.Combine(root, SecretTemplate.Replace('/', Path.DirectorySeparatorChar)));

        // Both halves: the Key Vault object must be FETCHED, and it must be PROJECTED onto the env
        // key the watcher reads. Either alone is a secret that is mounted nowhere or a key that
        // names nothing — and both render as a perfectly healthy pod.
        //
        // 🚨 Two ways this assertion was written wrong before the reverts were actually RUN, and
        // both left it permanently green:
        //   * `Contains` on a key name is satisfied by any LONGER key starting with it, so
        //     `key: Hosting__PlatformWebhookSecretRENAMED` passed the projection check. Hence
        //     whole-line matching.
        //   * the SAME `objectName:` line appears in BOTH blocks, so searching the whole document
        //     let the projection block satisfy the fetch check — deleting the Key Vault fetch
        //     outright still passed. Hence each half is searched in its OWN section.
        // A guard that cannot be shown failing is not a guard.
        var fetch = SectionBefore(template, "secretObjects:");
        var projection = SectionFrom(template, "secretObjects:");

        Assert.True(HasLine(fetch, "objectName: Hosting-PlatformWebhookSecret"),
            $"{SecretTemplate} no longer fetches the platform-build webhook secret from Key Vault. "
            + "Every environment's SecretProviderClass is copied from this template; without the "
            + "secret the Hosting watcher drops every release delivery as unverifiable while CD's "
            + "POST still answers 2xx, so the wave stops with nothing red anywhere (#2235).");
        Assert.True(HasLine(projection, "key: " + WebhookSecretEnvKey),
            $"{SecretTemplate} fetches the platform-build webhook secret but no longer projects it "
            + $"onto {WebhookSecretEnvKey}, so it reaches the container under no name the watcher "
            + "reads — a mounted secret and an unset one are indistinguishable from inside the pod.");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Everything BEFORE the <paramref name="marker"/> line — the CSI driver's Key Vault
    /// fetch list, which is a different question from what the fetched objects are projected onto.</summary>
    private static string SectionBefore(string text, string marker)
    {
        var i = text.IndexOf("\n  " + marker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{marker}' is gone from {SecretTemplate} — the template's shape changed "
                            + "and this guard is no longer reading what it claims to read.");
        return text[..i];
    }

    /// <summary>Everything from the <paramref name="marker"/> line on — the env projections.</summary>
    private static string SectionFrom(string text, string marker)
    {
        var i = text.IndexOf("\n  " + marker, StringComparison.Ordinal);
        Assert.True(i >= 0, $"'{marker}' is gone from {SecretTemplate}.");
        return text[i..];
    }

    /// <summary>
    /// The text carries <paramref name="line"/> as a COMPLETE line (whitespace and any YAML list
    /// dash aside). Whole-line, because <c>Contains</c> on a key name is satisfied by any longer
    /// key that starts with it — which is precisely the rename these assertions exist to catch.
    /// </summary>
    private static bool HasLine(string text, string line) =>
        text.Split('\n').Any(l => l.Trim().TrimStart('-').Trim() == line);
}
