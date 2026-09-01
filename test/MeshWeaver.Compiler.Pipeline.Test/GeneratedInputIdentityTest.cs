#pragma warning disable CS1591

using System.Collections.Immutable;
using MeshWeaver.Compiler;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins the CONTENT KEY (#1707 slice 4) — the hash of the FULLY GENERATED compilation input.
///
/// <para>The key exists to replace a PROXY. <c>FrameworkBuildIdentity.FullMvidAssemblies</c> folds
/// the toolchain's whole implementation MVID into the framework identity because a generator body
/// change reshapes what Roslyn is fed with no API change — correct, and so coarse that a body-only
/// commit anywhere in a 16-assembly closure rebakes every NodeType on every deployment (#1976). So
/// the key has to DISCRIMINATE, and these are the four claims that make it usable:</para>
///
/// <list type="number">
/// <item><description>identical generated input ⇒ identical key, in one process and across
/// processes/hosts/architectures (the golden vector);</description></item>
/// <item><description>a GENERATOR BODY change that alters the generated input ⇒ the key MOVES
/// (this is the case the full-MVID rule exists for — the load-bearing one);</description></item>
/// <item><description>a body-only change that does NOT alter the generated input ⇒ the key does
/// NOT move (this is the win);</description></item>
/// <item><description>a REFERENCE-SURFACE change ⇒ the key MOVES.</description></item>
/// </list>
///
/// <para>An unstable key is worse than the proxy it replaces: it would invalidate everything on
/// every build and nobody would know why. Hence the golden vectors — a literal a DIFFERENT process
/// on a DIFFERENT architecture has to reproduce, which is exactly what CI does on every run.</para>
/// </summary>
public class GeneratedInputIdentityTest
{
    // Fixed, injected environment: the algorithm is pinned, not this machine's Roslyn build.
    private const string Compiler = "Microsoft.CodeAnalysis.CSharp/4.14.0.0/4.14.0-test";
    private const string Options = "options/v1\nCSharpParseOptions.LanguageVersion=Preview\n";

    private const string GeneratedSource =
        "// Auto-generated from MeshNode: Demo/Thing\n"
        + "// Generated at: 2026-08-21T09:14:02.1234567+00:00\n"
        + "using System;\n"
        + "[assembly: MeshWeaver.Graph.Generated.DemoThingMeshNodeProvider]\n"
        + "public record ThingContent(string Name);\n";

    private static ImmutableSortedDictionary<string, string> Pairs(
        params (string Key, string Value)[] pairs)
    {
        var builder = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in pairs)
            builder[key] = value;
        return builder.ToImmutable();
    }

    private static string Digest(
        string source, string options = Options, params (string Key, string Value)[] generators) =>
        GeneratedInputIdentity.OfGeneratedInput(
            "DynamicNode_Demo_Thing", source, options, Compiler, Pairs(generators));

    // ── 1. identical generated input ⇒ identical key ─────────────────────────────────────────

    [Fact]
    public void IdenticalGeneratedInput_HashesIdentically_WithinOneProcess()
    {
        Digest(GeneratedSource).Should().Be(Digest(GeneratedSource));
        GeneratedInputIdentity.Combine(Digest(GeneratedSource), Pairs(("MeshWeaver.Layout", "ref:a")))
            .Should().Be(GeneratedInputIdentity.Combine(
                Digest(GeneratedSource), Pairs(("MeshWeaver.Layout", "ref:a"))));
    }

    /// <summary>
    /// 🚨 THE CROSS-PROCESS / CROSS-HOST / CROSS-ARCHITECTURE claim, expressed the only way a test
    /// can make it: a LITERAL. Every CI run on every runner recomputes these from the same inputs
    /// in a different process — an implementation that picked up ANY ambient state (culture, line
    /// endings, enumeration order, a clock, a hash seed) fails here rather than silently
    /// invalidating every build in production.
    ///
    /// <para>A change to these literals is a change to the KEY SPACE: every stamped record stops
    /// matching and every NodeType rebuilds once. That is legitimate when the algorithm genuinely
    /// changes — bump the <c>generated-input/v1</c> / <c>content-key/v1</c> document versions with
    /// it and say so in the PR. It is never legitimate as a way to make a failing test pass.</para>
    /// </summary>
    [Fact]
    public void TheKey_IsAPinnedFunctionOfItsInputs_AcrossProcessesAndHosts()
    {
        var digest = Digest(GeneratedSource);
        digest.Should().Be("gb7fb3927697f90ef616dc94be3d91869");

        GeneratedInputIdentity.Combine(digest, Pairs(
                ("MeshWeaver.Layout", "ref:aaa"), ("MeshWeaver.Mesh.Contract", "ref:bbb")))
            .Should().Be("i01e1f2f47df6303c72c00a1f6857eae7");
    }

    [Fact]
    public void LineEndings_AreNormalized_SoAWindowsBuildAndALinuxBuildAgree()
    {
        // The skeleton is built with StringBuilder.AppendLine — Environment.NewLine — so the SAME
        // node generates CRLF on a Windows dev box and LF in a pod. Without the fold nothing would
        // ever share a build across hosts.
        Digest(GeneratedSource.Replace("\n", "\r\n", StringComparison.Ordinal))
            .Should().Be(Digest(GeneratedSource));
        Digest("\uFEFF" + GeneratedSource).Should().Be(Digest(GeneratedSource));
    }

    [Fact]
    public void TheGeneratorsWallClockHeader_IsNormalizedOut()
    {
        // DynamicMeshNodeAttributeGenerator stamps DateTimeOffset.UtcNow into the generated text.
        // Left in, the input is never twice the same and NO content key over it could ever hit.
        var later = GeneratedSource.Replace(
            "2026-08-21T09:14:02.1234567+00:00", "2027-01-02T23:59:59.9999999+00:00",
            StringComparison.Ordinal);
        later.Should().NotBe(GeneratedSource);
        Digest(later).Should().Be(Digest(GeneratedSource));

        // …and ONLY that line: the same timestamp text inside user code is content, and moves it.
        var inUserCode = GeneratedSource.Replace(
            "public record ThingContent(string Name);",
            "public const string Stamp = \"// Generated at: 2026-08-21T09:14:02.1234567+00:00\";",
            StringComparison.Ordinal);
        Digest(inUserCode).Should().NotBe(Digest(GeneratedSource));
    }

    /// <summary>
    /// 🚨 THE SECOND WALL CLOCK, and the one that is not obvious. The skeleton emits the NodeType
    /// node's own <c>LastModified</c> into the provider's node, and <c>PackageInstaller.BulkSave</c>
    /// stamps that field with <c>DateTimeOffset.UtcNow</c> on EVERY import — so the same repo
    /// content imported twice generates different text. A CI bake and a portal import the same
    /// commit at different moments by construction, so a key that discriminated on it could never
    /// match a bundle against the input a portal regenerates.
    ///
    /// <para>Caught by <c>BakeEquivalenceTest</c>: with only the source order fixed, the mesh
    /// producer still emitted a different key on every run (<c>iaa2ed0ef…</c>, <c>ib704fb17…</c>)
    /// while the compiler producer sat on <c>i770851b4…</c>.</para>
    /// </summary>
    [Fact]
    public void TheNodeTimestampStamp_IsNormalizedOut_ButOnlyInItsGeneratedShape()
    {
        const string WithStamp = "        [\n            new MeshNode(\"Demo/Thing\")\n"
            + "            {\n"
            + "                LastModified = DateTimeOffset.Parse(\"2026-08-21T09:14:02.1234567+00:00\"),\n"
            + "                HubConfiguration = ConfigureHub\n            }\n        ];\n";
        var reimported = WithStamp.Replace(
            "2026-08-21T09:14:02.1234567+00:00", "2027-03-04T11:22:33.4455667+00:00",
            StringComparison.Ordinal);
        reimported.Should().NotBe(WithStamp);

        Digest(GeneratedSource + reimported).Should().Be(Digest(GeneratedSource + WithStamp));

        // …and it is anchored to the generator's emitted SHAPE: a user-code line that merely
        // mentions the member, or a different call shape, is content and still moves the key.
        Digest(GeneratedSource + "public string LastModified = \"2026-01-01\";\n")
            .Should().NotBe(Digest(GeneratedSource + "public string LastModified = \"2027-01-01\";\n"));
    }

    // ── 2. a generator body change that alters the generated input ⇒ the key MOVES ───────────

    /// <summary>
    /// The load-bearing case. A body-only edit to the skeleton generator — the #1802 shape, where
    /// <c>GenerateGlobalUsingsSource</c>/<c>GenerateAttributeSource</c> started emitting a
    /// different import scope — changes the emitted bytes with NO API change anywhere. This is
    /// exactly what surface hashing cannot see and what <c>FullMvidAssemblies</c> exists for.
    /// </summary>
    [Fact]
    public void AGeneratorBodyChange_ThatAltersTheGeneratedText_MovesTheKey()
    {
        var afterGeneratorChange = GeneratedSource.Replace(
            "using System;\n", "using System;\nusing System.Linq;\n", StringComparison.Ordinal);

        Digest(afterGeneratorChange).Should().NotBe(Digest(GeneratedSource));
    }

    /// <summary>
    /// The other half of the same case: a SOURCE GENERATOR whose body changed emits different
    /// source into the compilation while the text handed to Roslyn is byte-identical. Nothing in
    /// the generated text can see that, so the generators' own identities are part of the key.
    /// </summary>
    [Fact]
    public void AGeneratorAssemblyBodyChange_MovesTheKey_EvenWhenTheTextIsIdentical()
    {
        var before = Digest(GeneratedSource, Options, ("Scope.Generator.dll", "mvid:1111"));
        var after = Digest(GeneratedSource, Options, ("Scope.Generator.dll", "mvid:2222"));

        after.Should().NotBe(before);
        // …and adding a generator is a change too — an empty set must not hash like a populated one.
        Digest(GeneratedSource).Should().NotBe(before);
    }

    /// <summary>
    /// A body-only change to <c>EmitPipeline.CreateCompilationOptions</c> (flipping the
    /// optimization level, say) rewrites every emitted assembly while the generated text is
    /// unchanged — so the option set is in the key, and it is REFLECTED rather than hand-listed so
    /// a newly-set option cannot sit silently outside it.
    /// </summary>
    [Fact]
    public void AnOptionChange_MovesTheKey()
    {
        Digest(GeneratedSource, Options + "CSharpCompilationOptions.OptimizationLevel=Release\n")
            .Should().NotBe(Digest(GeneratedSource));
    }

    [Fact]
    public void RenderOptions_IsSortedOrdinalAndInvariant_AndNamesEveryPublicProperty()
    {
        var rendered = GeneratedInputIdentity.RenderOptions(
            Microsoft.CodeAnalysis.CSharp.CSharpParseOptions.Default);
        var lines = rendered.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        lines.Should().NotBeEmpty();
        lines.Should().Equal(lines.OrderBy(l => l, StringComparer.Ordinal));
        lines.Should().Contain(l => l.StartsWith("CSharpParseOptions.LanguageVersion=", StringComparison.Ordinal));
        // Reflected, so a Roslyn upgrade that ADDS an option joins the key automatically.
        lines.Should().HaveCountGreaterThan(5);
    }

    // ── 3. a body-only change that does NOT alter the generated input ⇒ the key does NOT move ──

    /// <summary>
    /// 🚨 THE WIN, and the claim the whole slice is for. Two compiles of the SAME content by two
    /// toolchains whose implementation MVIDs differ — the situation after any body-only commit to
    /// the 16-assembly toolchain closure — produce the SAME content key. The <c>!toolchain</c>
    /// proxy moves and the record records that it moved; the CONTENT key does not, because the
    /// generated input did not.
    /// </summary>
    [Fact]
    public void AToolchainBodyChange_ThatDoesNotAlterTheGeneratedInput_LeavesTheKeyPut()
    {
        Func<string, string?> ids = name => name == "MeshWeaver.Layout" ? "ref:aaa" : null;
        var digest = Digest(GeneratedSource);

        var before = CompiledDependencies.Compute(
            ["MeshWeaver.Layout"], ids, "mvid:toolchain-BEFORE", digest);
        var after = CompiledDependencies.Compute(
            ["MeshWeaver.Layout"], ids, "mvid:toolchain-AFTER", digest);

        before[CompiledDependencies.ToolchainKey]
            .Should().NotBe(after[CompiledDependencies.ToolchainKey]);
        after[CompiledDependencies.ContentKey]
            .Should().Be(before[CompiledDependencies.ContentKey]);
    }

    /// <summary>
    /// The record's content key is folded over the ASSEMBLY entries only — never over the reserved
    /// <c>!</c> entries. Folding the toolchain id in would make the key move on every toolchain
    /// commit, i.e. reproduce the proxy it replaces.
    /// </summary>
    [Fact]
    public void TheRecordsContentKey_IsFoldedOverTheAssemblyEntriesOnly()
    {
        Func<string, string?> ids = name => name == "MeshWeaver.Layout" ? "ref:aaa" : null;
        var digest = Digest(GeneratedSource);
        var record = CompiledDependencies.Compute(
            ["MeshWeaver.Layout"], ids, "mvid:toolchain-1", digest);

        record[CompiledDependencies.ContentKey].Should().Be(
            GeneratedInputIdentity.Combine(digest, Pairs(("MeshWeaver.Layout", "ref:aaa"))));
    }

    [Fact]
    public void NoDigest_MeansNoContentKey_AndValidationIsUnchanged()
    {
        Func<string, string?> ids = _ => "ref:aaa";
        var record = CompiledDependencies.Compute(["MeshWeaver.Layout"], ids, "mvid:t");

        record.Should().NotContainKey(CompiledDependencies.ContentKey);
        CompiledDependencies.FindMismatch(record, ids, "mvid:t").Should().BeNull();
    }

    [Fact]
    public void AStampedContentKey_IsSkippedWithoutALiveOne_AndDecisiveWithOne()
    {
        Func<string, string?> ids = _ => "ref:aaa";
        var record = CompiledDependencies.Compute(
            ["MeshWeaver.Layout"], ids, "mvid:t", Digest(GeneratedSource));
        var stamped = record[CompiledDependencies.ContentKey];

        // No live key (every metadata-only caller): skipped — the toolchain entry still governs.
        CompiledDependencies.FindMismatch(record, ids, "mvid:t").Should().BeNull();
        // A caller that REGENERATED the input gets an exact verdict, both ways.
        CompiledDependencies.FindMismatch(record, ids, "mvid:t", stamped).Should().BeNull();
        CompiledDependencies.FindMismatch(record, ids, "mvid:t", "i-something-else")
            .Should().Contain(CompiledDependencies.ContentKey);
    }

    // ── 4. a reference-surface change ⇒ the key MOVES ────────────────────────────────────────

    [Fact]
    public void AReferenceSurfaceChange_MovesTheKey()
    {
        var digest = Digest(GeneratedSource);

        var before = GeneratedInputIdentity.Combine(digest, Pairs(
            ("MeshWeaver.Layout", "ref:aaa"), ("MeshWeaver.Mesh.Contract", "ref:bbb")));
        var afterSurfaceChange = GeneratedInputIdentity.Combine(digest, Pairs(
            ("MeshWeaver.Layout", "ref:aaa"), ("MeshWeaver.Mesh.Contract", "ref:CHANGED")));
        var afterNewReference = GeneratedInputIdentity.Combine(digest, Pairs(
            ("MeshWeaver.Layout", "ref:aaa"), ("MeshWeaver.Mesh.Contract", "ref:bbb"),
            ("Some.Module", "mvid:ccc")));

        afterSurfaceChange.Should().NotBe(before);
        afterNewReference.Should().NotBe(before);
    }

    [Fact]
    public void ReferenceOrder_DoesNotMoveTheKey()
    {
        var digest = Digest(GeneratedSource);
        var ascending = new[] { ("A.Ref", "ref:1"), ("B.Ref", "ref:2") };
        var descending = new[] { ("B.Ref", "ref:2"), ("A.Ref", "ref:1") };

        GeneratedInputIdentity.Combine(digest, Pairs(descending))
            .Should().Be(GeneratedInputIdentity.Combine(digest, Pairs(ascending)));
    }

    // ── the stage-1/stage-2 split ────────────────────────────────────────────────────────────

    [Fact]
    public void TheTwoStages_AreDistinguishableByPrefix()
    {
        var digest = Digest(GeneratedSource);
        digest.Should().StartWith(GeneratedInputIdentity.GeneratedInputPrefix);
        GeneratedInputIdentity.Combine(digest, Pairs())
            .Should().StartWith(GeneratedInputIdentity.ContentKeyPrefix);
    }

    [Fact]
    public void AnUnreadableGeneratorAssembly_IsRecordedAsAbsent_NotSkipped()
    {
        var identities = GeneratedInputIdentity.AssemblyFileIdentities(
            [Path.Combine(Path.GetTempPath(), "definitely-not-here-4d1f.dll")]);

        identities.Should().ContainKey("definitely-not-here-4d1f.dll")
            .WhoseValue.Should().Be(GeneratedInputIdentity.AbsentId);
    }

    // ── why the full-MVID rule could NOT be deleted with this slice ─────────────────────────

    /// <summary>
    /// 🚨 THE BLOCKER, pinned so it is checkable rather than an assertion in a PR body.
    ///
    /// <para>#1707 slice 4 says the content key lets the full-MVID rule "be deleted entirely". It
    /// does not — not on its own — and this test is why. The content key answers "were these bytes
    /// produced from THIS input" exactly; what it cannot do is answer it CHEAPLY, because
    /// evaluating it means regenerating the compile input (source discovery, includes, skeleton).
    /// Every consumer that decides "rebuild or not" today is metadata-only, so it has no live value
    /// to compare against and <see cref="CompiledDependencies.FindMismatch"/> correctly skips the
    /// entry.</para>
    ///
    /// <para>So with <c>FullMvidAssemblies</c> emptied, a body-only toolchain commit that DOES
    /// change generated input — the #1802 global-usings fix, the <c>AnchorIncludePath</c> fix,
    /// both real — moves NOTHING that any check looks at: the surface identity is unchanged
    /// (no API moved), the toolchain entry is a constant, and the content key is skipped. The type
    /// keeps bytes compiled from input that no longer exists, and the failure mode is a
    /// <c>TypeLoadException</c> inside an ALC at activation with no diagnostic.</para>
    ///
    /// <para><b>What unblocks it</b> is a re-evaluation lane, not a better key: something that
    /// REGENERATES the input on a toolchain change and compares. The toolchain MVID then stops
    /// being the invalidation unit and becomes the trigger for that comparison — which is the
    /// demotion #1976 actually wants, and which this key is the precondition for.</para>
    ///
    /// <para>🚨 <b>That lane now EXISTS</b> (#1976 —
    /// <see cref="ContentKeyReevaluation"/>,
    /// <c>NodeTypeCompilationHelpers.ReevaluateStaleBuild</c>), and the last two assertions below
    /// exercise it: the same record a metadata-only caller validates forever is judged EXACTLY by
    /// a caller that regenerated. What still blocks the DELETION of the rule is not the decision —
    /// it is that a build's bytes are addressed under a store key carrying the framework tag, so
    /// carrying one across a framework generation is a cross-generation assembly load (the
    /// 2026-06-20 wedge) and a maintainer scope call. The lane therefore acts only where the bytes
    /// are already addressable under the live tag. Until that call is made, this pin stays.</para>
    /// </summary>
    [Fact]
    public void DeletingTheFullMvidRule_WouldLeaveNothingWatchingTheToolchain()
    {
        // Identical API surfaces; only the toolchain's IMPLEMENTATION moved — a body-only commit.
        var surfaces = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MeshWeaver.Compiler"] = "surface-unchanged",
        };

        // WITH the rule, the global identity moves and every stale-build check fires.
        FrameworkBuildIdentity.ComputeSurfaceIdentity(surfaces, _ => "mvidBEFORE", ["MeshWeaver.Compiler"])
            .Should().NotBe(FrameworkBuildIdentity.ComputeSurfaceIdentity(
                surfaces, _ => "mvidAFTER", ["MeshWeaver.Compiler"]));

        // WITHOUT it, the identity is the same value — nothing re-drives anything.
        FrameworkBuildIdentity.ComputeSurfaceIdentity(surfaces, _ => "mvidBEFORE", [])
            .Should().Be(FrameworkBuildIdentity.ComputeSurfaceIdentity(surfaces, _ => "mvidAFTER", []));

        // …and the per-type record cannot cover for it: the toolchain entry has degenerated to a
        // constant, and the content key is skipped because no metadata-only caller can compute a
        // live one. A stamped record therefore validates forever across a toolchain change.
        Func<string, string?> ids = _ => "ref:unchanged";
        const string ToolchainAfterDeletion = "mvid:constant";
        var record = CompiledDependencies.Compute(
            ["MeshWeaver.Layout"], ids, ToolchainAfterDeletion, Digest(GeneratedSource));

        CompiledDependencies.FindMismatch(record, ids, ToolchainAfterDeletion).Should().BeNull();
        // The SAME record, judged by a caller that regenerated, is decisive — which is the lane
        // that has to exist before the rule can go.
        var afterAToolchainChangeThatDidAlterTheInput = GeneratedInputIdentity.Combine(
            Digest(GeneratedSource + "using System.Text;\n"),
            Pairs(("MeshWeaver.Layout", "ref:unchanged")));
        CompiledDependencies.FindMismatch(
                record, ids, ToolchainAfterDeletion, afterAToolchainChangeThatDidAlterTheInput)
            .Should().Contain(CompiledDependencies.ContentKey);

        // The lane's own verdicts over the same staged world: a regenerated input that MOVED is a
        // rebuild, and one that did not is carried forward — the demotion, positively.
        ContentKeyReevaluation.Reevaluate(
                record, ids, ToolchainAfterDeletion,
                Digest(GeneratedSource + "using System.Text;\n"))
            .Verdict.Should().Be(ReevaluationVerdict.Rebuild);
        ContentKeyReevaluation.Reevaluate(
                record, ids, "mvid:a-DIFFERENT-toolchain", Digest(GeneratedSource))
            .Verdict.Should().Be(ReevaluationVerdict.CarryForward);
    }

    /// <summary>
    /// 🚨 A file NAME can be ambiguous — NuGet can resolve two different assemblies to the same
    /// file name (two packages, or two TFM folders), and <c>RunSourceGenerators</c> loads BOTH.
    /// Overwriting on collision would drop one from the key, so the key would claim "same input"
    /// for a genuinely different effective generator set — the UNDER-invalidating direction — and
    /// which one survived would depend on enumeration order, reintroducing the order-dependence
    /// this type exists to remove.
    /// </summary>
    [Fact]
    public void ACollidingGeneratorFileName_AggregatesEveryIdentity_OrderIndependently()
    {
        var dir = Directory.CreateTempSubdirectory("mw-gen-collide-");
        try
        {
            // Two DIFFERENT unreadable-but-present files would both resolve `absent`, which cannot
            // show aggregation — so use the real toolchain assemblies, which have distinct MVIDs,
            // copied under ONE shared file name in two directories.
            var real = typeof(GeneratedInputIdentity).Assembly.Location;
            var other = typeof(Xunit.FactAttribute).Assembly.Location;
            var a = Path.Combine(dir.FullName, "a"); Directory.CreateDirectory(a);
            var b = Path.Combine(dir.FullName, "b"); Directory.CreateDirectory(b);
            var first = Path.Combine(a, "Gen.dll");
            var second = Path.Combine(b, "Gen.dll");
            File.Copy(real, first);
            File.Copy(other, second);

            var ascending = GeneratedInputIdentity.AssemblyFileIdentities([first, second]);
            var descending = GeneratedInputIdentity.AssemblyFileIdentities([second, first]);

            ascending.Keys.Should().Equal("Gen.dll");
            // BOTH identities are represented, and the order they arrived in does not matter.
            ascending["Gen.dll"].Should().Contain(GeneratedInputIdentity.IdentitySeparator);
            descending["Gen.dll"].Should().Be(ascending["Gen.dll"]);

            // …and dropping one of the two MUST move the key — that is the whole point.
            GeneratedInputIdentity.AssemblyFileIdentities([first])["Gen.dll"]
                .Should().NotBe(ascending["Gen.dll"]);

            // The SAME assembly reached by two paths is ONE input, so it collapses to one id.
            var duplicated = GeneratedInputIdentity.AssemblyFileIdentities([first, first]);
            duplicated["Gen.dll"].Should().NotContain(GeneratedInputIdentity.IdentitySeparator);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void AssemblyFileIdentities_AreKeyedByFileName_NotByHostPath()
    {
        // A key carrying a host path could never match between a bake container and a portal pod.
        GeneratedInputIdentity.AssemblyFileIdentities(["/a/b/Gen.dll"])
            .Keys.Should().Equal("Gen.dll");
    }
}
