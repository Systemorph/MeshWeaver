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
/// <para><b>And the first cure repeated the mistake one level in.</b> #3211 named the anchor
/// explicitly per arm, but the from-source arm still READ it out of the module's publish output,
/// on the ground that "the platform ProjectReferences are real, so MeshWeaver.Compiler.dll IS
/// beside the module". That was measured green — against the two modules the lane then built,
/// both of which reach the compiler through MeshWeaver.Graph. It is a property of the reference
/// graph, not of the arm. The hour core's compose set grew to four (#3290, 2026-09-04),
/// MeshWeaver.Maps and MeshWeaver.Payments.Stripe were red on their first run and core CD stopped
/// delivering. n=2 with 100% agreement is not evidence when the population is chosen by whoever
/// edits a list. The arm now BUILDS the anchor from the platform source the call pins (#3293).</para>
///
/// <para><b>Why these assertions and not a green run.</b> The three properties below are invisible
/// in a green log: a pack that omits the anchor still packs, an inspection that does not read the
/// field still passes, and a publish step that does not check still POSTs. The refusal must also sit
/// on the step that actually publishes — the inspection runs on the BUILD leg only, while the reuse
/// leg hands over an artifact an earlier run packed, so a guard bound to the inspection alone would
/// pass while pre-#3211 bytes went to the registry.</para>
///
/// <para>🚨 <b>And every one of those is CONFIG-level</b> — the lane's text, the anchor's location.
/// None can see the run END UP stating a different identity per module, which is the SILENT half
/// (#3310): the bundle packs, the manifest carries a well-formed 32-hex value, and every non-blank
/// assertion downstream passes. That is asserted on the OUTCOME instead, in
/// <c>node-repo-pack-verify.py</c>'s <c>identity_agreement</c> over the receipts <c>verify</c>
/// already collects; the last test here is the ratchet on the evidence trail that check depends on,
/// not the check itself.</para>
/// </summary>
public class ModuleIdentityPublishGuard
{
    private const string Lane = ".github/workflows/node-repo-module-pack.yml";
    private const string KeyScript = ".github/scripts/module-build-key.py";
    private const string Verifier = ".github/scripts/node-repo-pack-verify.py";

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
        Assert.Contains("--graph-dll \"$anchor\"", pack, StringComparison.Ordinal);

        // 🚨 THE FROM-SOURCE ARM STATES THE IDENTITY; IT DOES NOT BUILD ONE.
        //
        // Three cures were tried on this arm and all three were the same mistake — deriving the
        // platform's identity from a DLL sitting in a per-MODULE job:
        //   #3211 read it out of $PACKDIR (present only when the module transitively
        //         ProjectReferences the compiler — a property of two reference graphs, written
        //         down as a property of the arm, and 100% of a population of two agreed);
        //   #3293 built it from the platform tree (correct location, still per job);
        //   #3306 removed the `-p:Version` that polluted that build (correct property, still per job).
        // Run 33950389008 (2026-09-05, four entries, ONE commit, every leg `plan: BUILD`) still
        // stated three identities: Maps and Payments.Stripe agreed on 0051e721… having compiled the
        // compiler ONCE, while Markdown.Collaboration (6553ea37…) and AI (395909c2…) each compiled
        // it TWICE and each got its own. The split is exactly the reference graph: those two reach
        // the compiler transitively, so the MODULE build compiles it first, into the same output
        // path, under the module's own global properties — and the anchor build that follows keeps
        // those bytes. No property removed from the ANCHOR build can fix pollution that arrives
        // from the MODULE build upstream.
        Assert.Contains("identity=(--framework-mvid \"g$sha\")", pack, StringComparison.Ordinal);
        Assert.Contains("rev-parse HEAD", pack, StringComparison.Ordinal);

        // 🚨 THE RATCHET THAT REPLACES ALL THREE. The arm must not compile the platform's compiler
        // at all: a per-job build is the mechanism every previous cure left in place, and it packs
        // GREEN while handing #3154's comparison a value no consumer can match. If a fourth cure
        // ever reaches for `dotnet build …MeshWeaver.Compiler.csproj` here, this fails.
        Assert.DoesNotContain("MeshWeaver.Compiler.csproj", pack, StringComparison.Ordinal);
        Assert.DoesNotContain("anchordir=", pack, StringComparison.Ordinal);

        // The older ratchet stands: scavenging the anchor out of the module's own publish output
        // reads as reasonable every time. It must not come back either.
        Assert.DoesNotContain("anchor=\"$PACKDIR/MeshWeaver.Compiler.dll\"", pack, StringComparison.Ordinal);

        // 🚨 AND `g<sha>` IS THE TOKEN A CONSUMER ACTUALLY READS. A portal image is built with
        // -p:CIRun=true, so AddCommitHashMetadata stamps
        // AssemblyMetadata("MeshWeaverFrameworkIdentity", "g<sha>") and
        // FrameworkIdentity.ReadIdentity PREFERS that stamp over the MVID. A bundle stating a
        // 32-hex MVID could therefore never equal what a consumer reports — not "usually differs",
        // never equal, by construction — so #3154's (version, identity) comparison could only ever
        // answer "identity could not be checked". Determinism alone would not have fixed that;
        // stating the commit does.
        Assert.Contains("MeshWeaverFrameworkIdentity", File.ReadAllText(Path.Combine(
            SourceScan.FindRepoRoot(), "src", "MeshWeaver.Plugin.Build", "FrameworkIdentity.cs")),
            StringComparison.Ordinal);

        // 🚨 An unreadable platform commit ends the arm RED — it never falls back to a derived or
        // empty identity. The branch picks WHERE the identity comes from, never whether to have one.
        Assert.Contains("if [ ! -f \"$anchor\" ]; then", pack, StringComparison.Ordinal);
        Assert.Contains("::error::no identity anchor for $MODULE", pack, StringComparison.Ordinal);
        Assert.Contains("cannot read the platform commit", pack, StringComparison.Ordinal);
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
    /// 🚨 THE OUTCOME-LEVEL HALF (#3310). Every assertion above reads the lane's TEXT or the
    /// anchor's LOCATION, so none of them can see a run END UP stating a different identity per
    /// module — the silent shape: the bundle packs, the manifest carries a well-formed 32-hex
    /// value, and every non-blank assertion downstream passes. The one check that sees it is an
    /// agreement assertion over the whole wave, and it needs the value on the RECEIPT.
    ///
    /// <para>This test is the ratchet on that evidence trail, not the check itself — the check is
    /// <c>identity_agreement</c> in the verifier, whose <c>--self-test</c> runs on every lane run
    /// and mutates a green run into both failures. What is asserted here is that the trail cannot
    /// be quietly cut: the receipt step reads the identity off the BYTES (so a reuse leg is
    /// accounted for exactly like a build leg — the inspection above runs on the build leg alone),
    /// refuses to write a receipt that states none, and the verifier still refuses BOTH a
    /// disagreement AND an absent value. An absent identity reading as "agrees" would be the same
    /// mistake as a skipped gate reading as a passed one.</para>
    /// </summary>
    [Fact]
    public void TheReceiptCarriesTheStatedIdentity_AndVerifyRefusesBothDisagreementAndSilence()
    {
        var pack = JobBody("pack");

        // Off the BYTES this leg produced — the build leg's packed bundle or the artifact the
        // reuse leg downloaded and sha256-verified — never from a step output. The packer's exit
        // code, the inspection's tick and the value in the artifact are three different claims,
        // and only the third is what a consumer compares against.
        var receipt = pack[pack.IndexOf("name: Drop the receipt", StringComparison.Ordinal)..];
        Assert.Contains("BUNDLE: ${{ steps.bundle.outputs.path || steps.reused.outputs.path }}",
            receipt, StringComparison.Ordinal);
        Assert.Contains("unzip -p \"$BUNDLE\" meshweaver/manifest.json", receipt, StringComparison.Ordinal);
        Assert.Contains("jq -r '.frameworkMvid // \"\"' | tr -d '[:space:]'", receipt, StringComparison.Ordinal);
        // A receipt that cannot state the identity is REFUSED, not written blank: a blank field
        // would make "this lane's bundles agree" vacuously true for that module, which is the very
        // reading this whole change removes.
        Assert.Contains("states no framework identity", receipt, StringComparison.Ordinal);
        Assert.Contains("frameworkIdentity:$fi", receipt, StringComparison.Ordinal);

        // And the verifier that reads it still asks BOTH questions. `verify` is the lane's one
        // stable context (`All selected bundles built`) — the context a repo's branch protection
        // requires — so this is where the outcome becomes falsifiable.
        var verifier = File.ReadAllText(Path.Combine(FindRepoRoot(), Verifier));
        Assert.Contains("def identity_agreement(", verifier, StringComparison.Ordinal);
        Assert.Contains("IDENTITY_FIELD = \"frameworkIdentity\"", verifier, StringComparison.Ordinal);
        Assert.Contains("DIFFERENT framework identities", verifier, StringComparison.Ordinal);
        Assert.Contains("state NO framework identity", verifier, StringComparison.Ordinal);
        // 🚨 UNCONDITIONAL in main(): no flag to forget, no input to test. A check reached only
        // when a caller remembers to pass something is the skip-trapdoor in another costume.
        Assert.Contains("i_errors, i_notes = identity_agreement(own_receipts(receipts, args.lane, declared_set))",
            verifier, StringComparison.Ordinal);
        Assert.Contains("if i_errors:", verifier, StringComparison.Ordinal);
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
