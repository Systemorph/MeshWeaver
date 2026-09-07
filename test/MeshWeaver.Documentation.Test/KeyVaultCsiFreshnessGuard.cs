using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// 🚨 <b>A workload that READS a CSI-synced Key Vault Secret must MOUNT the SecretProviderClass
/// that feeds it</b> (Systemorph/MeshWeaver#3548).
///
/// <para><b>What went wrong.</b> The Secrets Store CSI driver materialises and rotates the synced
/// Kubernetes Secret <i>for the pods that mount the SPC volume</i>. A pod that only lists the
/// synced Secret in <c>envFrom</c> is a FREE RIDER: it reads whatever some other pod's mount last
/// wrote, and <c>envFrom</c> is resolved once, at container start. The migration Job was exactly
/// that — it mounted nothing, started the instant <c>helm upgrade</c> applied, and the portal pods
/// that own the rotation had not rolled yet. So on the 2026-09-07 <c>memex</c> release, the deploy
/// that repointed <c>Embedding__ApiKey</c> from the Azure object to the OpenRouter one ran the
/// embedding backfill with the PREVIOUS key: 1,260 × HTTP 401, <c>1274 upserted (0 embedded)</c>,
/// and <c>Database migration completed</c> — a GREEN job that authenticated with a stale
/// credential and embedded nothing. A portal pod that started minutes later held the same key name
/// and got 200s, so the vault was right and only the Job's copy was stale.</para>
///
/// <para><b>Why a guard and not a comment.</b> That is this repo's central failure mode — a check
/// that cannot fail. The backfill logs-and-skips per row rather than throwing, so the Job's exit
/// code says nothing about whether it had a working key, and no amount of reading the logs
/// afterwards makes the deploy safe. The cure is structural: a CSI mount is set up before ANY
/// container in the pod starts and fetches from the vault at that moment, so a mounting pod's
/// <c>envFrom</c> resolves against a freshly-written Secret BY CONSTRUCTION. This guard is what
/// stops the next workload from taking the free ride.</para>
///
/// <para>The check is over the chart's <b>own</b> Key Vault plumbing — the
/// <c>memex.keyVaultClasses</c> helper, the single place the SPC name, the synced Secret, the
/// volume name and the mount path are resolved together so those four can never disagree. Only
/// templates that declare a POD are examined: <c>secretproviderclass.yaml</c> legitimately names
/// <c>syncedSecret</c> (it is the declaration) and mounts nothing.</para>
/// </summary>
public class KeyVaultCsiFreshnessGuard
{
    private const string Templates = "deploy/helm/templates";

    /// <summary>The envFrom half: this template reads a class's synced Secret.</summary>
    private const string ReadsSyncedSecret = "$class.syncedSecret";

    /// <summary>The mount half: the CSI volume, and the mount that makes the driver fetch.</summary>
    private const string CsiDriver = "secrets-store.csi.k8s.io";
    private const string MountsClassPath = "$class.mountPath";

    /// <summary>Kinds that carry a pod template — the only ones that can mount anything.</summary>
    private static readonly Regex PodBearingKind =
        new(@"^kind:\s*""?(Deployment|Job|CronJob|StatefulSet|DaemonSet|ReplicaSet|Pod)""?",
            RegexOptions.Multiline);

    [Fact]
    public void EveryWorkloadThatReadsAKeyVaultSyncedSecret_AlsoMountsItsCsiVolume()
    {
        var root = FindRepoRoot();
        var dir = Path.Combine(root, Templates);

        var templates = Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        // 🚨 The guard asserts its own denominator. A rename of the templates directory, or of the
        // helper the whole scheme is keyed on, would otherwise leave this passing having examined
        // nothing — the skip-trapdoor shape AGENTS.md forbids.
        Assert.True(templates.Length > 0,
            $"{Templates} contains no .yaml templates — this guard would pass having checked "
            + "nothing. Point it at wherever the chart moved.");

        var declaringHelper = Path.Combine(root, Templates, "memex-portal", "_keyvault.tpl");
        Assert.True(File.Exists(declaringHelper),
            $"{Path.GetRelativePath(root, declaringHelper)} is gone — the '{ReadsSyncedSecret}' "
            + "marker this guard keys on comes from the memex.keyVaultClasses helper. If the helper "
            + "moved, move the guard with it rather than letting it match nothing.");

        var readers = templates
            .Select(f => (Path: f, Body: ExecutableLinesOf(File.ReadAllText(f))))
            .Where(t => PodBearingKind.IsMatch(t.Body)
                        && t.Body.Contains(ReadsSyncedSecret, StringComparison.Ordinal))
            .ToArray();

        // Two workloads read the classes today: the portal Deployment and the migration Job. If
        // that count reaches zero the wiring was rewritten — a human question, never a pass.
        Assert.True(readers.Length >= 2,
            $"expected at least two pod-bearing templates to read '{ReadsSyncedSecret}' (the portal "
            + $"Deployment and the migration Job); found {readers.Length}. Either the Key Vault "
            + "wiring was rewritten (move this guard with it) or a reader was lost.");

        var freeRiders = readers
            .Where(t => !t.Body.Contains(CsiDriver, StringComparison.Ordinal)
                        || !t.Body.Contains(MountsClassPath, StringComparison.Ordinal))
            .Select(t => Path.GetRelativePath(root, t.Path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.True(freeRiders.Length == 0,
            "these workloads read a Key Vault CSI-synced Secret through envFrom but do not mount "
            + $"the SecretProviderClass volume that feeds it: {string.Join(", ", freeRiders)}. The "
            + "driver only fetches and rotates for the pods that MOUNT the class, and envFrom is "
            + "resolved once at container start — so such a pod silently reads whatever another "
            + "pod's mount last wrote. On a deploy that CHANGES an SPC mapping that value is the "
            + "PREVIOUS one, and the workload runs green against a stale credential (#3548: 1,260 × "
            + "HTTP 401 and a completed migration). Add the volume + volumeMount from the same "
            + "`memex.keyVaultClasses` block that renders the envFrom.");
    }

    /// <summary>
    /// Strips YAML comments AND whole Helm comment blocks (<c>{{- /* … */}}</c>) before probing,
    /// for the same reason <see cref="MigrationWorkloadModelGuard"/> strips comments: these
    /// templates explain their own history at length, and a guard that matched the prose would pass
    /// on the comment describing the fix rather than on the fix. The Helm blocks span many lines,
    /// so a line-prefix filter is not enough here.
    /// </summary>
    private static string ExecutableLinesOf(string yaml)
    {
        var withoutHelmComments = Regex.Replace(
            yaml, @"\{\{-?\s*/\*.*?\*/\s*-?\}\}", string.Empty, RegexOptions.Singleline);
        return string.Join("\n", withoutHelmComments.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#')));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException(
                "Could not locate the repo root (MeshWeaver.slnx) from " + AppContext.BaseDirectory);
    }
}
