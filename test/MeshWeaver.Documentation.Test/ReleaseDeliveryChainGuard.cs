using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MeshWeaver.GitSync;
using MeshWeaver.Graph.Configuration;
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
/// <para>🚨 <b>The failure this file exists for.</b> <c>FrameworkBroadcast:Subscribers</c> — the
/// only source of the subscriber set that exists — was rendered by <b>no chart in either repo</b>
/// for the whole life of the feature, so no deployment could set it. Every broadcast ran against an
/// empty set and logged it at <c>Information</c> as normal. Nothing was red, because a key that no
/// chart renders cannot be distinguished from a key a deployment chose to leave empty. The
/// companion defect was prose: <c>main-cd.yml</c> and the CD contract both said the set "lives in
/// the Hosting fleet registry on memex", and no such registry was ever built — so every reader who
/// went looking for the seam was sent to a mechanism that does not exist.</para>
///
/// <para><see cref="PlatformReleaseNotifyGuard"/> pins the SENDER (CD's notify leg cannot skip or
/// downgrade a failed delivery). This file pins the RECEIVER's provisioning: each joint's key must
/// be reachable by a deploy, and the key each guard entry names must still be the key the code
/// reads — which is why the entries reference the readers' constants instead of restating
/// them.</para>
/// </summary>
public class ReleaseDeliveryChainGuard
{
    private const string ConfigMap = "deploy/helm/templates/memex-portal/config.yaml";
    private const string ValuesAks = "deploy/aks/values.aks.yaml";
    private const string Workflow = ".github/workflows/main-cd.yml";
    private const string ContractDoc =
        "src/MeshWeaver.Documentation/Data/Architecture/ContinuousDeliveryContract.md";

    /// <summary>
    /// One joint of the delivery chain: the configuration key that switches it on, in the
    /// double-underscore environment form a deployment actually sets, plus what silently does not
    /// happen while the key cannot be set at all.
    /// </summary>
    /// <param name="Joint">The joint's name, for the failure message.</param>
    /// <param name="EnvKey">The env-var form the ConfigMap must render and a values file must declare.</param>
    /// <param name="Consequence">What is silently lost when no deployment can set the key.</param>
    private sealed record ChainKey(string Joint, string EnvKey, string Consequence);

    /// <summary>
    /// The chain's keys, each derived from the CONSTANT its reader owns — never a literal. A
    /// literal would restate the key, and the guard would then go green through exactly the rename
    /// it is here to catch (AGENTS.md: a renamed config key is a silent deletion).
    /// </summary>
    private static IEnumerable<ChainKey> ChainKeys()
    {
        // Joint 1 — CD's signed POST is accepted at all. The inbox is fail-closed: a target
        // missing from this allowlist answers 404, byte-identical to a wrong URL.
        yield return new ChainKey(
            "the webhook inbox accepts the release event",
            EnvForm(WebhookInbox.TargetsConfigSection) + "__0",
            "CD's notify-platform-update 404s on every promoted build and no release event ever "
            + "reaches the mesh");

        // Joint 2 — the verified release fans out. FrameworkBroadcastOptions owns the env-key
        // prefix precisely so this entry cannot drift from what the broadcaster reads.
        yield return new ChainKey(
            "the release wave has subscribers",
            FrameworkBroadcastOptions.SubscribersEnvKeyPrefix + "0",
            "every broadcast dispatches to an empty set — 0 dispatched, 0 failed, logged as the "
            + "normal state of a non-control mesh — so no satellite ever re-bakes promptly");
    }

    /// <summary>
    /// 🚨 THE CONTROL. A key the code READS and no chart RENDERS cannot be set by any deployment,
    /// and its feature is therefore permanently off — while reading exactly like a feature a
    /// deployment left off on purpose. Both ends are asserted: the ConfigMap must name the key
    /// (the Deployment's only env path is <c>envFrom</c> on it, and it carries no catch-all range),
    /// and a values file must declare the slot so an overlay that sets it has something to override.
    /// </summary>
    [Fact]
    public void EveryConfigKeyTheReleaseChainReads_IsRenderedByTheChart()
    {
        var root = SourceScan.FindRepoRoot();
        var configMap = File.ReadAllText(Path.Combine(root, ConfigMap.Replace('/', Path.DirectorySeparatorChar)));
        var values = File.ReadAllText(Path.Combine(root, ValuesAks.Replace('/', Path.DirectorySeparatorChar)));

        var broken = ChainKeys()
            .Select(k => (k, inConfigMap: RendersKey(configMap, k.EnvKey), inValues: DeclaresKey(values, k.EnvKey)))
            .Where(x => !x.inConfigMap || !x.inValues)
            .ToList();

        Assert.True(broken.Count == 0,
            "These release-delivery-chain keys cannot be set by ANY deployment — the code reads "
            + "them and the chart does not render them, so the joint they switch on is "
            + "permanently off and indistinguishable from one a deployment turned off:\n"
            + string.Join("\n", broken.Select(x =>
                $"  • {x.k.EnvKey} ({x.k.Joint}) — "
                + (x.inConfigMap ? "" : $"not rendered by {ConfigMap}; ")
                + (x.inValues ? "" : $"no slot declared in {ValuesAks}; ")
                + $"consequence: {x.k.Consequence}")));
    }

    /// <summary>
    /// 🚨 THE COMPANION DEFECT — prose naming a mechanism that does not exist. `main-cd.yml` and the
    /// CD contract are the two places a reader goes to ask "who is subscribed?", and for the whole
    /// life of the feature both answered "the Hosting fleet registry on memex" — a registry with no
    /// node type, no reader and no writer in any repo. That sentence is why the empty subscriber set
    /// survived two rounds of investigation into why nothing was delivered. Both must name the seam
    /// that actually exists, so the next reader lands on something they can set.
    /// </summary>
    [Fact]
    public void TheDocumentedSubscriberSource_IsTheOneThatExists()
    {
        var root = SourceScan.FindRepoRoot();
        var section = FrameworkBroadcastOptions.ConfigSection;

        foreach (var file in new[] { Workflow, ContractDoc })
        {
            var text = File.ReadAllText(Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar)));
            Assert.True(text.Contains(section, StringComparison.Ordinal),
                $"{file} explains where the release wave's subscribers come from and never names "
                + $"'{section}' — the only source that exists. It used to name a Hosting fleet "
                + "registry instead; that registry was never built, and pointing readers at it is "
                + "what kept #2235's empty subscriber set invisible through two investigations.");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>A configuration path (<c>A:B</c>) in the env-var form a container receives.</summary>
    private static string EnvForm(string configPath) => configPath.Replace(":", "__", StringComparison.Ordinal);

    /// <summary>
    /// The ConfigMap renders the key when it appears as a mapping key whose value interpolates the
    /// SAME name out of <c>config.memex_portal</c>. Checking both halves is deliberate: a line that
    /// names the key but interpolates a different one is the #1925 shape wearing the right label.
    /// </summary>
    private static bool RendersKey(string configMap, string key) =>
        configMap.Split('\n').Any(l =>
        {
            var t = l.Trim();
            return !t.StartsWith('#')
                   && t.StartsWith(key + ":", StringComparison.Ordinal)
                   && t.Contains("config.memex_portal." + key, StringComparison.Ordinal);
        });

    /// <summary>A values file declares the slot when it carries the key as a mapping key.</summary>
    private static bool DeclaresKey(string values, string key) =>
        values.Split('\n').Any(l =>
        {
            var t = l.Trim();
            return !t.StartsWith('#') && t.StartsWith(key + ":", StringComparison.Ordinal);
        });
}
