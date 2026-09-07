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

    /// <summary>
    /// The LEGACY escape hatch, for an environment whose SecretProviderClass is HAND-MADE and is
    /// therefore attached by NAME rather than declared. These three values keys are the whole of
    /// it, and they are a set: the env source, the volume behind it, and the mount that makes the
    /// driver write the volume's Secret.
    /// </summary>
    private const string ExtraEnvFrom = ".Values.extraEnvFrom";
    private const string ExtraVolumes = ".Values.extraVolumes";
    private const string ExtraVolumeMounts = ".Values.extraVolumeMounts";

    /// <summary>Where those keys are declared — asserted so a rename cannot silence this guard.</summary>
    private const string Values = "deploy/helm/values.yaml";

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
    /// 🚨 <b>Every workload that carries the chart's Key Vault secret set carries the LEGACY
    /// ESCAPE HATCH too</b> (Systemorph/MeshWeaver#3595).
    ///
    /// <para><b>What went wrong, and why the guard above could not see it.</b> The fact above is
    /// keyed entirely on <c>$class.*</c> — the classes the CHART owns. An environment whose
    /// SecretProviderClass is HAND-MADE owns no class: it attaches the SPC's synced Secret BY NAME
    /// through <c>.Values.extraEnvFrom</c>, the shape <c>values.yaml</c> calls "the memex-cloud
    /// AI-keys shape". A hand-made class carries neither marker, so the freshness guard is blind
    /// to it BY CONSTRUCTION and cannot go red on it. Measured on <c>origin/main</c>
    /// <c>fbf9e9be1</c>: <c>grep -rn extraEnvFrom deploy/</c> found exactly ONE rendering site in
    /// the whole chart — the portal Deployment. The migration Job rendered none of it.</para>
    ///
    /// <para><b>And that is a DIFFERENT defect from #3548, with a different remedy.</b> #3548 was
    /// STALENESS: the Job had the value, from before the rotation. This is ABSENCE: for a key
    /// delivered through the escape hatch the Job had no value at all, so
    /// <c>MeshNodeEmbeddingBackfill</c> took the not-configured path, logged-and-skipped every row,
    /// and the Job still reported <c>Database migration completed</c>. Same green run, same silent
    /// outcome, different mechanism.</para>
    ///
    /// <para><b>The invariant.</b> The two workloads must carry the SAME secret set, whichever
    /// mechanism delivers it. So every pod-bearing template that reads the chart's own classes
    /// must render the escape hatch as well — the parity is what stops the next workload from
    /// getting half the environment's secrets, which is a state that produces a green run and an
    /// empty index rather than a failure.</para>
    /// </summary>
    [Fact]
    public void EveryWorkloadThatCarriesTheChartsKeyVaultSecrets_AlsoRendersTheLegacyEscapeHatch()
    {
        var root = FindRepoRoot();

        AssertTheEscapeHatchKeysStillExist(root);

        var readers = PodBearingTemplatesReadingTheChartsClasses(root);

        var missingHatch = readers
            .Where(t => !t.Body.Contains(ExtraEnvFrom, StringComparison.Ordinal))
            .Select(t => Path.GetRelativePath(root, t.Path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.True(missingHatch.Length == 0,
            "these workloads carry the chart-owned Key Vault secrets but render NONE of "
            + $"`{ExtraEnvFrom}`: {string.Join(", ", missingHatch)}. An environment whose "
            + "SecretProviderClass is hand-made delivers its keys ONLY through that escape hatch "
            + "(values.yaml calls it the memex-cloud AI-keys shape), so such a workload receives "
            + "part of the environment's secrets and no signal that the rest is missing — the "
            + "embedding backfill then logs-and-skips every row and the Job still reports "
            + "`Database migration completed` (#3595). Render the same "
            + $"`{ExtraEnvFrom}` block the portal Deployment does, and its "
            + $"`{ExtraVolumes}` / `{ExtraVolumeMounts}` twins with it.");
    }

    /// <summary>
    /// 🚨 <b>The escape hatch's own freshness half: a workload that READS
    /// <c>.Values.extraEnvFrom</c> also renders the volume and the mount behind it.</b>
    ///
    /// <para>This is #3548's argument applied to the class the chart does not own, and it is why
    /// the three values keys are a SET rather than three options. The CSI driver materialises and
    /// rotates a synced Secret for the pods that MOUNT its SecretProviderClass; a pod that only
    /// names the Secret in <c>envFrom</c> free-rides on some other pod's mount and resolves
    /// <c>envFrom</c> once, at container start. Rendering the env source without its volume would
    /// therefore hand the new workload exactly the stale credential #3548 measured — 1,260 × HTTP
    /// 401 behind a green migration — with the guard above unable to see it, because none of it
    /// goes through <c>$class.*</c>.</para>
    ///
    /// <para>Asserted in the same direction as its sibling and for the same reason: rendering
    /// MORE of the hatch can only be safe, rendering less is the defect.</para>
    /// </summary>
    [Fact]
    public void EveryWorkloadThatRendersTheEscapeHatchEnvFrom_AlsoRendersItsVolumeAndMount()
    {
        var root = FindRepoRoot();

        AssertTheEscapeHatchKeysStillExist(root);

        var templates = PodBearingTemplates(root);

        var hatchReaders = templates
            .Where(t => t.Body.Contains(ExtraEnvFrom, StringComparison.Ordinal))
            .ToArray();

        // 🚨 The denominator. Two workloads render the hatch today — the portal Deployment and the
        // migration Job. If that reaches zero the escape hatch was removed from the chart while
        // values.yaml still declares it (the assertion above proves it does), which is a values key
        // consumed by nothing — the exact class of defect chart-gate exists for. Never a pass.
        Assert.True(hatchReaders.Length >= 2,
            $"expected at least two pod-bearing templates to render `{ExtraEnvFrom}` (the portal "
            + $"Deployment and the migration Job); found {hatchReaders.Length}. `{Values}` still "
            + "declares the key, so either a workload stopped rendering it — which is #3595 "
            + "reopening — or the hatch was retired and this guard must move with it.");

        var freeRiders = hatchReaders
            .Where(t => !t.Body.Contains(ExtraVolumes, StringComparison.Ordinal)
                        || !t.Body.Contains(ExtraVolumeMounts, StringComparison.Ordinal))
            .Select(t => Path.GetRelativePath(root, t.Path))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.True(freeRiders.Length == 0,
            $"these workloads read `{ExtraEnvFrom}` but do not render "
            + $"`{ExtraVolumes}` / `{ExtraVolumeMounts}`: {string.Join(", ", freeRiders)}. The env "
            + "source names a Secret the Secrets Store CSI driver writes only for the pods that "
            + "MOUNT its SecretProviderClass, and envFrom is resolved once at container start — so "
            + "without the volume the workload free-rides on another pod's mount and reads a value "
            + "that is stale or absent, green and silent (#3548, #3595). The three keys are a set: "
            + "render all three or none.");
    }

    /// <summary>
    /// The escape hatch's keys must still be DECLARED in values.yaml. Without this a rename there
    /// would leave both facts above matching nothing in every template and passing — a guard whose
    /// subject moved and whose roots did not, which AGENTS.md names as the skip-trapdoor shape.
    /// </summary>
    private static void AssertTheEscapeHatchKeysStillExist(string root)
    {
        var values = Path.Combine(root, Values);
        Assert.True(File.Exists(values),
            $"{Values} is gone — the escape-hatch keys this guard keys on are declared there. If "
            + "the values file moved, move the guard with it rather than letting it match nothing.");

        var body = File.ReadAllText(values);
        var undeclared = new[] { "extraEnvFrom", "extraVolumes", "extraVolumeMounts" }
            .Where(k => !Regex.IsMatch(body, $@"^{Regex.Escape(k)}\s*:", RegexOptions.Multiline))
            .ToArray();

        Assert.True(undeclared.Length == 0,
            $"{Values} no longer declares: {string.Join(", ", undeclared)}. These three keys are "
            + "the legacy escape hatch and this guard is keyed on their names, so a rename here "
            + "makes both facts match nothing and pass having checked no template. Rename them in "
            + "the guard in the same change — or, if the hatch was retired because every "
            + "environment moved onto `keyVaultSecretClasses`, delete this guard and say so.");
    }

    /// <summary>Pod-bearing templates that read the chart's OWN Key Vault classes.</summary>
    private static (string Path, string Body)[] PodBearingTemplatesReadingTheChartsClasses(string root)
    {
        var readers = PodBearingTemplates(root)
            .Where(t => t.Body.Contains(ReadsSyncedSecret, StringComparison.Ordinal))
            .ToArray();

        // The same denominator the first fact asserts, for the same reason: zero readers means the
        // wiring was rewritten, which is a human question and never a pass.
        Assert.True(readers.Length >= 2,
            $"expected at least two pod-bearing templates to read '{ReadsSyncedSecret}' (the portal "
            + $"Deployment and the migration Job); found {readers.Length}. Either the Key Vault "
            + "wiring was rewritten (move this guard with it) or a reader was lost.");

        return readers;
    }

    /// <summary>Every template in the chart that declares a pod, comments stripped.</summary>
    private static (string Path, string Body)[] PodBearingTemplates(string root)
    {
        var dir = Path.Combine(root, Templates);

        var templates = Directory.EnumerateFiles(dir, "*.yaml", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToArray();

        Assert.True(templates.Length > 0,
            $"{Templates} contains no .yaml templates — this guard would pass having examined "
            + "nothing. Point it at wherever the chart moved.");

        return templates
            .Select(f => (Path: f, Body: ExecutableLinesOf(File.ReadAllText(f))))
            .Where(t => PodBearingKind.IsMatch(t.Body))
            .ToArray();
    }

    /// <summary>
    /// Strips Helm comment BLOCKS (<c>{{- /* … */}}</c>, which span many lines here), whole-line
    /// YAML comments, and TRAILING YAML comments — for the same reason
    /// <see cref="MigrationWorkloadModelGuard"/> strips comments: these templates explain their own
    /// history at length, and a guard that matched the prose would pass on the comment describing
    /// the fix rather than on the fix.
    ///
    /// <para>🚨 The trailing form matters as much as the leading one (Copilot review): a
    /// line-prefix filter leaves <c>name: "x"  # {{ $class.mountPath }}</c> in the scan, so a
    /// template could satisfy the mount half from a COMMENT while its real mount was gone — the
    /// guard passing on its own documentation, which is the failure it exists to prevent.</para>
    ///
    /// <para>The trailing rule is deliberately naive about quoting: it cuts from the first
    /// whitespace-preceded <c>#</c>, so a <c>#</c> inside a quoted value is cut too. That errs
    /// toward removing text, and removing text can only make this guard REFUSE, never accept — a
    /// false red is loud and fixable, a false green is the thing being guarded against. The chart's
    /// marker lines (<c>mountPath: "{{ $class.mountPath }}"</c>, the CSI driver name) carry no
    /// <c>#</c> at all, so nothing real is at risk today.</para>
    /// </summary>
    private static string ExecutableLinesOf(string yaml)
    {
        var withoutHelmComments = Regex.Replace(
            yaml, @"\{\{-?\s*/\*.*?\*/\s*-?\}\}", string.Empty, RegexOptions.Singleline);
        return string.Join("\n", withoutHelmComments.Split('\n')
            .Where(line => !line.TrimStart().StartsWith('#'))
            .Select(StripTrailingComment));
    }

    /// <summary>Cuts a line at its first whitespace-preceded <c>#</c> — YAML's comment rule.</summary>
    private static string StripTrailingComment(string line)
    {
        var hash = Regex.Match(line, @"(?<=^|\s)#");
        return hash.Success ? line[..hash.Index] : line;
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
