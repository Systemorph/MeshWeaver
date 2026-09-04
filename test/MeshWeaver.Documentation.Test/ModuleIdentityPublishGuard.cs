#pragma warning disable CS1591

using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace MeshWeaver.Documentation.Test;

/// <summary>
/// Governance guard for the PUBLISH-SIDE refusal that makes a bundle stating no framework identity
/// unpublishable (MeshWeaver#3211 — part 3 of Plugins#931;
/// <c>Doc/Architecture/ModuleBuildArchitecture</c> → "A bundle states what it was built against").
///
/// <para><b>What went wrong, measured.</b> #3154 landed the consumer half:
/// <c>ModuleUpdateDecision.Decide</c> compares <b>(version, framework identity)</b> before answering
/// <c>SkipUpToDate</c>, because a module's version encodes CONTENT only — a rebuild of unchanged
/// source against a new platform republishes under the SAME version, and without the identity the
/// consumer cannot tell that rebuild from a no-op (Plugins#723: a portal held on an old image while
/// the updater went quiet). It shipped into a fleet where NOTHING stated an identity: on
/// MeshWeaver.Plugins run 33773265959 (2026-09-03) all 34 bundles packed
/// <c>built-against MVID (unrecorded)</c>, sdk and container alike, because the packer probes for
/// <c>MeshWeaver.Compiler.dll</c> BESIDE the module and on both paths the platform is the pinned
/// IMAGE. So the comparison had nothing to compare, everywhere.</para>
///
/// <para><b>Why these assertions and not a green run.</b> The three properties below are invisible
/// in a green log: a pack that omits the anchor still packs, an inspection that does not read the
/// field still passes, and a publish step that does not check still POSTs. The refusal must also sit
/// on the step that actually publishes — the inspection runs on the BUILD leg only, while the reuse
/// leg hands over an artifact an earlier run packed, so a guard bound to the inspection alone would
/// pass while pre-#3211 bytes went to the registry.</para>
/// </summary>
public class ModuleIdentityPublishGuard
{
    private const string Lane = ".github/workflows/node-repo-module-pack.yml";
    private const string KeyScript = ".github/scripts/module-build-key.py";

    [Fact]
    public void ThePackStep_NamesTheIdentityAnchor_AndFailsRedWhenThereIsNone()
    {
        var pack = JobBody("pack");

        // The anchor is the SAME reference set the build bound against, and WHERE that is moves
        // with the platform: the pinned image's extracted /app when one is pinned (`platform-refs`
        // — which the sdk build passes as MeshWeaverRefs and the container build compiles inside),
        // the PACK TOOL's publish output when the platform was built from source. It is never a
        // second opinion, and never left to the packer's default probe, which is exactly the probe
        // that found nothing on all 34 of the fleet's bundles.
        Assert.Contains("anchor=\"$REFS/MeshWeaver.Compiler.dll\"", pack, StringComparison.Ordinal);
        Assert.Contains(
            "anchor=\"$RUNNER_TEMP/pack-tool/MeshWeaver.Compiler.dll\"", pack, StringComparison.Ordinal);
        Assert.Contains("--graph-dll \"$anchor\"", pack, StringComparison.Ordinal);

        // 🚨 #3176 — NEVER the module's own publish output. `$PACKDIR` is the directory being
        // packed, and a MeshWeaver.Compiler.dll in there is either absent (the module's closure does
        // not reach it: MeshWeaver.Maps and MeshWeaver.Payments.Stripe packed RED on every core CD
        // run of 2026-09-04) or a rebuild under that module's -p:Version (so one platform produced
        // two identities in run 33874892203). The identity is a property of the PLATFORM, so it may
        // not be read out of a per-module directory.
        Assert.DoesNotContain(
            "anchor=\"$PACKDIR/MeshWeaver.Compiler.dll\"", pack, StringComparison.Ordinal);

        // 🚨 BOTH arms end RED when there is no anchor — the branch picks WHERE to look, never
        // whether to check. A bundle whose identity is a guess is worse than a pack that stops.
        Assert.Contains("if [ ! -f \"$anchor\" ]; then", pack, StringComparison.Ordinal);
        Assert.Contains("::error::no identity anchor for $MODULE", pack, StringComparison.Ordinal);
        Assert.DoesNotContain("--graph-dll \"${anchor:-", pack, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInspection_ReadsTheIdentityBackOffTheBytes()
    {
        var pack = JobBody("pack");
        // Read off the packed manifest, not inferred from the packer's exit code — the same rule
        // every other assertion in that step follows.
        Assert.Contains("(.frameworkMvid // \"\") | test(\"^\\\\S+$\")", pack, StringComparison.Ordinal);
        Assert.Contains("the bundle states no framework identity", pack, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePublishStep_RefusesAnUnstatedIdentity_BeforeItPosts()
    {
        var pack = JobBody("pack");

        var read = pack.IndexOf("unzip -p \"$BUNDLE\" meshweaver/manifest.json", StringComparison.Ordinal);
        var refuse = pack.IndexOf("REFUSING to publish $MODULE@$VERSION", StringComparison.Ordinal);
        var post = pack.IndexOf("-X POST \"$REGISTRY/api/plugins/bundles/$PACKAGE", StringComparison.Ordinal);

        Assert.True(read >= 0,
            "the publish step must read the manifest out of the bytes it is about to POST — the "
            + "inspection above runs on the build leg only, and the reuse leg publishes an artifact "
            + "an earlier run packed");
        Assert.True(refuse >= 0, "the publish step must REFUSE an unstated framework identity by name");
        Assert.True(post >= 0, "the publish step must still POST to the registry");
        Assert.True(read < refuse && refuse < post,
            "the refusal must sit between reading the manifest and the POST — a check after the "
            + "hand-over is a check on bytes the registry already shelved");

        // Blank reads as unstated, on the lane exactly as it does in ModuleUpdateDecision: the
        // value is stripped of whitespace before the emptiness test, so " " cannot publish.
        Assert.Contains("jq -r '.frameworkMvid // \"\"' | tr -d '[:space:]'", pack, StringComparison.Ordinal);
        Assert.Contains("if [ -z \"$identity\" ]; then", pack, StringComparison.Ordinal);

        // An unreadable manifest is a refusal too — "could not check" must never render as "checked".
        Assert.Contains("refusing to publish bytes whose manifest this lane could not check", pack, StringComparison.Ordinal);
    }

    /// <summary>
    /// 🚨 #3176 — the source-built arm's anchor is the pack tool's publish output, which carries
    /// MeshWeaver.Compiler.dll only because MeshWeaver.Plugin.Build references MeshWeaver.Graph.
    /// That is a load-bearing property of a reference graph this lane does not own, so `prepare`
    /// ASSERTS it — and asserts it UNCONDITIONALLY, because the tool is restored from a cache keyed
    /// on `platform-ref`: a check guarded by the cache-miss condition would let a warm cache restore
    /// a tool without the anchor and skip the very check that would have caught it. That is a gate
    /// testing its own input, and GitHub paints the skip the same colour as a pass.
    /// </summary>
    [Fact]
    public void ThePrepareJob_AssertsTheAnchorInTheToolOutput_OnACacheHitToo()
    {
        var prepare = JobBody("prepare");

        var stepIndex = prepare.IndexOf(
            "- name: The tool output carries the platform identity anchor", StringComparison.Ordinal);
        Assert.True(stepIndex >= 0,
            "prepare must assert that the published module-pack tool carries MeshWeaver.Compiler.dll "
            + "— the source-built arm names it as the identity anchor");

        // Everything from that step's `- name:` up to the next step at the same indentation.
        var rest = prepare[stepIndex..];
        var next = rest.IndexOf("\n      - name:", StringComparison.Ordinal);
        var step = next >= 0 ? rest[..next] : rest;

        Assert.Contains("$RUNNER_TEMP/pack-tool/MeshWeaver.Compiler.dll", step, StringComparison.Ordinal);
        Assert.Contains("::error::the module-pack tool published no MeshWeaver.Compiler.dll", step, StringComparison.Ordinal);
        Assert.DoesNotContain("if:", step);
        Assert.DoesNotContain("continue-on-error", step);
    }

    /// <summary>
    /// The pack step now passes a flag that changes the bytes it writes for the SAME source (the
    /// manifest gained <c>frameworkMvid</c>), and the ledger's content address must therefore have
    /// moved — otherwise a later run REUSES a pre-#3211 bundle, whose publish the step above then
    /// refuses. The recipe version is the lever the script itself documents for exactly this.
    /// </summary>
    [Fact]
    public void TheLedgerRecipeVersion_MovedWithTheBytesTheLanePacks()
    {
        var key = File.ReadAllText(Path.Combine(FindRepoRoot(), KeyScript));
        var recipe = Regex.Match(key, @"^RECIPE_VERSION = ""(?<v>[^""]+)""", RegexOptions.Multiline);
        Assert.True(recipe.Success, $"{KeyScript} must declare RECIPE_VERSION");
        Assert.NotEqual("1", recipe.Groups["v"].Value);
    }

    private static string JobBody(string job)
    {
        var text = File.ReadAllText(Path.Combine(FindRepoRoot(), Lane));
        var match = Regex.Match(text, @"\n  " + Regex.Escape(job) + @":\n(?<body>(?:(?:    .*|  #.*)\n|\n)+?)(?=  [a-z][a-z-]*:\n|\z)");
        Assert.True(match.Success, $"{Lane} must have a `{job}` job");
        return string.Join('\n', match.Groups["body"].Value.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MeshWeaver.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not locate the repository root (MeshWeaver.slnx) above the test bin");
        return dir!.FullName;
    }
}
