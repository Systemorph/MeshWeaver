using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 THE DEFECT (#2948): an <c>@@</c> include pulls a Code node that NO source query matches, so
/// it is inside the emitted assembly and was outside <see cref="NodeTypeSourceFingerprint"/>.
/// Editing an included-only snippet therefore moved neither
/// <c>NodeTypeDefinition.CurrentSourceFingerprint</c> nor the producer's
/// <c>AdoptedSourceFingerprint</c>, and a prebuilt assembly baked BEFORE that edit still adopted as
/// <see cref="BuildProvenance.AdoptedVerified"/>.
///
/// <para><b>Why that is worse than no check.</b> <c>AdoptedUnverified</c> says "nobody established
/// where these bytes came from" and an operator reads it as the warning it is.
/// <c>AdoptedVerified</c> is an ASSERTION that the shipped bytes match the source this mesh holds.
/// Standing over source that was never hashed, it is a false verification — the same shape as the
/// #2813 incident it was built to prevent, one layer in.</para>
///
/// <para><b>What is NOT the fix.</b> Widening the refusal. <c>AdoptedUnverified</c> is a legacy
/// state that must keep adopting (refusing it recompiles everything on every install and, on a
/// <c>Modules:RequirePrebuilt</c> mesh, parks every legacy-bundle type). And a read that could not
/// be COMPLETED must not shorten the closure either — that reads exactly like a stale bundle and
/// refuses a good adoption, which is the outage direction. Both are pinned below.</para>
///
/// <para>The include reader is in-memory here, so every chain completes on subscribe: these are
/// pins on the DECISION, with no hub, no mesh and no timing.
/// <c>BakeEquivalenceTest</c> is what pins the two real producers to the same value.</para>
/// </summary>
public class SourceFingerprintIncludeClosureTest
{
    private const string TypePath = "Widget/Thing";

    private static MeshNode Code(string path, string code) =>
        new(path[(path.LastIndexOf('/') + 1)..], path[..path.LastIndexOf('/')])
        {
            NodeType = "Code",
            State = MeshNodeState.Active,
            Content = new CodeConfiguration { Code = code, Language = "csharp" },
        };

    /// <summary>
    /// The mesh half, in memory. Mirrors <c>SourceFingerprintIncludeReader</c>'s contract exactly:
    /// the ANCHORED path first, the authored path only as a fallback, reporting the path that
    /// produced the node so nested includes anchor there — and a path listed in
    /// <paramref name="unreadable"/> FAULTS rather than answering "absent", which is the one
    /// distinction the whole inconclusive rule rests on.
    /// </summary>
    private static Func<string, string?, IObservable<(MeshNode? Node, string Path)>> ReaderOver(
        IReadOnlyDictionary<string, string> nodes,
        IReadOnlySet<string>? unreadable = null)
        => (anchored, authored) =>
        {
            foreach (var candidate in authored is null ? [anchored] : new[] { anchored, authored })
            {
                if (unreadable?.Contains(candidate) == true)
                    return Observable.Throw<(MeshNode? Node, string Path)>(
                        new SourceIncludeUnavailableException(candidate));
                if (nodes.TryGetValue(candidate, out var code))
                    return Observable.Return<(MeshNode? Node, string Path)>(
                        (Code(candidate, code), candidate));
            }
            return Observable.Return<(MeshNode? Node, string Path)>((null, anchored));
        };

    /// <summary>Subscribes the (synchronous, in-memory) chain and returns the one value it
    /// produced. No <c>.Wait()</c>: the reader completes on subscribe, so a plain
    /// <c>Subscribe</c> is both sufficient and non-blocking.</summary>
    private static string FingerprintOf(
        IReadOnlyList<MeshNode> sources,
        IReadOnlyDictionary<string, string> mesh,
        IReadOnlySet<string>? unreadable = null)
    {
        string? captured = null;
        Exception? failure = null;
        NodeTypeSourceFingerprint
            .Compute(sources, TypePath, ReaderOver(mesh, unreadable))
            .Subscribe(value => captured = value, ex => failure = ex);
        if (failure is not null)
            throw failure;
        captured.Should().NotBeNull("the include walk must produce a value or fault, never neither");
        return captured!;
    }

    private static Exception FingerprintFailureOf(
        IReadOnlyList<MeshNode> sources,
        IReadOnlyDictionary<string, string> mesh,
        IReadOnlySet<string> unreadable)
    {
        Exception? failure = null;
        NodeTypeSourceFingerprint
            .Compute(sources, TypePath, ReaderOver(mesh, unreadable))
            .Subscribe(_ => { }, ex => failure = ex);
        failure.Should().NotBeNull("an unreadable include must FAULT, never answer");
        return failure!;
    }

    // ── The defect ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE ONE THAT WAS FAILING. The snippet is reachable ONLY through the <c>@@</c> directive —
    /// no source query matches <c>Widget/Snippets/Greeting</c>, which is exactly the shape
    /// <c>BakeEquivalenceTest</c> pins — so before #2948 both fingerprints were identical and an
    /// edit to it was invisible.
    /// </summary>
    [Fact]
    public void EditingAnIncludedOnlySnippet_MovesTheFingerprint()
    {
        var sources = new[]
        {
            Code($"{TypePath}/Source/Thing", "public record Thing;\n\n@@Widget/Snippets/Greeting"),
        };

        var before = FingerprintOf(sources, new Dictionary<string, string>
        {
            ["Widget/Snippets/Greeting"] = "public static class Greeting { public static string Hello() => \"hi\"; }",
        });
        var after = FingerprintOf(sources, new Dictionary<string, string>
        {
            ["Widget/Snippets/Greeting"] = "public static class Greeting { public static string Hello() => \"HELLO\"; }",
        });

        after.Should().NotBe(before,
            "the snippet is INSIDE the emitted bytes, so a change to it must move the hash — "
            + "otherwise an assembly compiled before the edit still claims AdoptedVerified against "
            + "source it was never built from");
    }

    /// <summary>
    /// 🚨 …AND THE CONTROL. A fingerprint that moved for any reason would pass the test above while
    /// producing endless phantom staleness — a recompile on every activation, and on a
    /// RequirePrebuilt mesh a permanent refusal. Nothing the compile does not consume may reach it.
    /// </summary>
    [Fact]
    public void AnEditToANodeTheCompileNeverSees_LeavesTheFingerprintAlone()
    {
        var sources = new[]
        {
            Code($"{TypePath}/Source/Thing", "public record Thing;\n\n@@Widget/Snippets/Greeting"),
        };
        const string greeting = "public static class Greeting { }";

        var before = FingerprintOf(sources, new Dictionary<string, string>
        {
            ["Widget/Snippets/Greeting"] = greeting,
            ["Widget/Snippets/Unused"] = "public static class Unused { }",
        });
        var after = FingerprintOf(sources, new Dictionary<string, string>
        {
            ["Widget/Snippets/Greeting"] = greeting,
            ["Widget/Snippets/Unused"] = "public static class Unused { public static int X => 1; }",
        });

        after.Should().Be(before,
            "a node nothing includes and no query matches is not compile input — letting it move "
            + "the hash would refuse every good bundle on the next unrelated edit");
    }

    // ── The walk: nesting, cycles, order ──────────────────────────────────────────────────────

    /// <summary>An include may itself include. The closure is TRANSITIVE, so a change two hops
    /// down still moves the hash.</summary>
    [Fact]
    public void ANestedIncludeIsCovered_Transitively()
    {
        var sources = new[] { Code($"{TypePath}/Source/Thing", "public record Thing;\n\n@@Widget/Snippets/Outer") };
        var mesh = new Dictionary<string, string>
        {
            ["Widget/Snippets/Outer"] = "public static class Outer { }\n\n@@Widget/Snippets/Inner",
            ["Widget/Snippets/Inner"] = "public static class Inner { public static int V => 1; }",
        };

        var before = FingerprintOf(sources, mesh);

        var edited = new Dictionary<string, string>(mesh)
        {
            ["Widget/Snippets/Inner"] = "public static class Inner { public static int V => 2; }",
        };
        FingerprintOf(sources, edited).Should().NotBe(before,
            "the innermost snippet is in the bytes just as much as the outermost one");
    }

    /// <summary>
    /// 🚨 A cycle must TERMINATE, and terminate at a stable value. <c>A → B → A</c> is not
    /// hypothetical — mount-relative includes inside one subtree reference each other, and the walk
    /// runs on the hub-activation path where a non-terminating traversal is an outage, not a slow
    /// test.
    /// </summary>
    [Fact]
    public void ACyclicIncludeTerminates_AndIsStable()
    {
        var sources = new[] { Code($"{TypePath}/Source/Thing", "public record Thing;\n\n@@Widget/Snippets/A") };
        var mesh = new Dictionary<string, string>
        {
            ["Widget/Snippets/A"] = "public static class A { }\n\n@@Widget/Snippets/B",
            ["Widget/Snippets/B"] = "public static class B { }\n\n@@Widget/Snippets/A",
        };

        var first = FingerprintOf(sources, mesh);
        FingerprintOf(sources, mesh).Should().Be(first,
            "a walk that terminates must terminate at the SAME place every time");

        // …and the cycle did not swallow either member: editing EITHER one still moves the hash.
        FingerprintOf(sources, new Dictionary<string, string>(mesh)
        {
            ["Widget/Snippets/B"] = "public static class B { public static int V => 1; }\n\n@@Widget/Snippets/A",
        }).Should().NotBe(first, "B is in the closure — the cycle brake stops the walk, not the coverage");
    }

    /// <summary>A self-include is the one-hop cycle, and the walk must not recurse into it.</summary>
    [Fact]
    public void ASelfIncludeTerminates()
    {
        var sources = new[] { Code($"{TypePath}/Source/Thing", "public record Thing;\n\n@@Widget/Snippets/Self") };
        var mesh = new Dictionary<string, string>
        {
            ["Widget/Snippets/Self"] = "public static class Self { }\n\n@@Widget/Snippets/Self",
        };

        FingerprintOf(sources, mesh).Should().NotBeNullOrEmpty();
    }

    /// <summary>
    /// 🚨 ORDER-STABILITY, the property phantom staleness comes from. The same content delivered in
    /// a different enumeration order — which is exactly what the mesh's <c>ImmutableDictionary</c>
    /// fold produced before #1707's sort, over per-process-randomised string hashes — must fold to
    /// the SAME value. That covers the include walk too: the closure is keyed by resolved path and
    /// sorted, so which root reached a shared snippet first cannot reach the result.
    /// </summary>
    [Fact]
    public void TheFingerprintIsIndependentOfDeliveryOrder()
    {
        var mesh = new Dictionary<string, string>
        {
            ["Widget/Snippets/Shared"] = "public static class Shared { }",
            ["Widget/Snippets/Only"] = "public static class Only { }",
        };
        var a = Code($"{TypePath}/Source/A", "public record A;\n\n@@Widget/Snippets/Shared");
        var b = Code($"{TypePath}/Source/B", "public record B;\n\n@@Widget/Snippets/Shared\n@@Widget/Snippets/Only");

        FingerprintOf([b, a], mesh).Should().Be(FingerprintOf([a, b], mesh),
            "a fingerprint that depends on traversal order changes when nothing changed");
    }

    // ── Inconclusive is not absent ────────────────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE FALSE-REFUSAL GUARD. A read that could not be COMPLETED must fault, not degrade to
    /// "the include is absent". The producer never stalls (its lookup is in-memory), so a consumer
    /// that quietly shortened its closure would hash differently from every good bundle and refuse
    /// it — on a <c>Modules:RequirePrebuilt</c> mesh, terminally. Note what the assertion measures:
    /// not merely that it faulted, but that the value it would otherwise have produced is a
    /// DIFFERENT one, i.e. that degrading really would have refused.
    /// </summary>
    [Fact]
    public void AnUnreadableInclude_IsInconclusive_NeverAbsent()
    {
        var sources = new[] { Code($"{TypePath}/Source/Thing", "public record Thing;\n\n@@Widget/Snippets/Greeting") };
        var mesh = new Dictionary<string, string>
        {
            ["Widget/Snippets/Greeting"] = "public static class Greeting { }",
        };

        var failure = FingerprintFailureOf(
            sources, mesh, new HashSet<string>(StringComparer.Ordinal) { "Widget/Snippets/Greeting" });

        failure.Should().BeOfType<SourceIncludeUnavailableException>(
            "the caller has to be able to tell an unreadable include from a genuine defect, so it "
            + "can leave the previous fingerprint standing instead of tearing down its watcher");

        // The counterfactual: had the stall been treated as absence, THIS is the value it would
        // have produced — and it is not the honest one, so the adoption would have been refused.
        var asIfAbsent = FingerprintOf(sources, new Dictionary<string, string>());
        asIfAbsent.Should().NotBe(FingerprintOf(sources, mesh),
            "if these were equal the guard above would be measuring nothing");
    }

    /// <summary>
    /// An include the mesh genuinely does not hold contributes NOTHING — and must, because it
    /// contributes nothing to the bytes either (the directive stays VERBATIM and Roslyn reports on
    /// the <c>@@</c> line). Both producer and consumer record nothing for it and therefore agree.
    /// </summary>
    [Fact]
    public void AnIncludeThatResolvesToNothing_ContributesNothing()
    {
        var sources = new[] { Code($"{TypePath}/Source/Thing", "public record Thing;\n\n@@Widget/Snippets/Missing") };

        FingerprintOf(sources, new Dictionary<string, string>())
            .Should().Be(
                NodeTypeSourceFingerprint.Compute(
                    sources, TypePath, ImmutableDictionary<string, string>.Empty),
                "an unresolvable include is an ordinary outcome, not a failure — the two sides must "
                + "both fold an empty closure for it");
    }

    // ── The two producer-side entry points agree, and the third one cannot under-cover ────────

    /// <summary>
    /// The bake hands the closure its own substitution collected
    /// (<c>NodeSetCompiler.CompileInputs.ResolvedIncludes</c>) instead of walking a second time.
    /// That value must equal what a live reader arrives at, or the producer and the consumer
    /// disagree and every bundle is refused.
    /// </summary>
    [Fact]
    public void ThePreResolvedOverload_AgreesWithTheReaderOverload()
    {
        var sources = new[] { Code($"{TypePath}/Source/Thing", "public record Thing;\n\n@@Widget/Snippets/Greeting") };
        const string greeting = "public static class Greeting { }";
        var mesh = new Dictionary<string, string> { ["Widget/Snippets/Greeting"] = greeting };

        NodeTypeSourceFingerprint.Compute(
                sources, TypePath,
                ImmutableSortedDictionary.CreateRange(
                    StringComparer.Ordinal,
                    new Dictionary<string, string> { ["Widget/Snippets/Greeting"] = greeting }))
            .Should().Be(FingerprintOf(sources, mesh));
    }

    /// <summary>
    /// 🚨 The include-less overload REFUSES a set that has an include, rather than hashing without
    /// it. A convenience overload that can silently under-cover is precisely how #2813 sat inert
    /// for months: the 7-argument <c>PrebuiltAssemblySeeder.Seed</c> hard-coded
    /// <c>sourceFingerprint: null</c> and both production callers took it, so the refusal was armed
    /// and unreachable. This one cannot be taken by accident.
    /// </summary>
    [Fact]
    public void TheIncludeLessOverload_RefusesASetThatHasAnInclude()
    {
        var sources = new[] { Code($"{TypePath}/Source/Thing", "public record Thing;\n\n@@Widget/Snippets/Greeting") };

        Action act = () => NodeTypeSourceFingerprint.Compute(sources, TypePath);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*@@ include*");
    }

    /// <summary>A set with no include at all still folds to exactly what it always did — so no
    /// fleet-wide fingerprint churn, and no refusal storm, for the types that have none.</summary>
    [Fact]
    public void ASetWithoutIncludes_FoldsIdenticallyThroughEveryOverload()
    {
        var sources = new[] { Code($"{TypePath}/Source/Thing", "public record Thing;") };

        var plain = NodeTypeSourceFingerprint.Compute(sources, TypePath);
        plain.Should().Be(FingerprintOf(sources, new Dictionary<string, string>()));
        plain.Should().Be(NodeTypeSourceFingerprint.Compute(
            sources, TypePath, ImmutableDictionary<string, string>.Empty));
    }

    // ── The consequence: the adoption verdict ─────────────────────────────────────────────────

    /// <summary>
    /// 🚨 THE ACCEPTANCE OF #2948, end to end through the decision: a bundle baked while the
    /// snippet said one thing, adopted by a mesh whose snippet now says another, must be REFUSED —
    /// not adopted as <see cref="BuildProvenance.AdoptedVerified"/>. Both fingerprints are REAL
    /// values from the function under test, not opaque strings, so this fails on main and passes
    /// here for the one reason the issue names.
    /// </summary>
    [Fact]
    public void ABundleBakedBeforeAnIncludedOnlyEdit_IsRefused_NotVerified()
    {
        var sources = new[]
        {
            Code($"{TypePath}/Source/Thing", "public record Thing;\n\n@@Widget/Snippets/Greeting"),
        };
        // What the producer hashed when it baked the assembly…
        var baked = FingerprintOf(sources, new Dictionary<string, string>
        {
            ["Widget/Snippets/Greeting"] = "public static class Greeting { public static bool Delete => false; }",
        });
        // …and what THIS mesh holds now. Only the included-only snippet changed: the matched
        // sources, their paths and their versions are byte-identical.
        var live = FingerprintOf(sources, new Dictionary<string, string>
        {
            ["Widget/Snippets/Greeting"] = "public static class Greeting { public static bool Delete => true; }",
        });

        var snapshot = ImmutableDictionary<string, long>.Empty
            .SetItem($"{TypePath}/Source/Thing", 638_000_000_000_000_000);

        var verdict = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            new NodeTypeDefinition
            {
                CompilationStatus = CompilationStatus.Ok,
                RequestedSourceStampAt = DateTimeOffset.UtcNow,
                AdoptedSourceFingerprint = baked,
                CurrentSourceFingerprint = live,
                CurrentSourceVersions = snapshot,
                CompiledSources = snapshot,
                LatestAssemblyCollection = "assemblies",
                LatestAssemblyPath = "adopted/Widget.Thing.dll",
            },
            snapshot,
            canCompileLocally: true);

        verdict.BuildProvenance.Should().Be(BuildProvenance.AdoptionRefused,
            "the only difference between the two builds is an included-only snippet, and that is "
            + "the whole of #2948 — before the fix these fingerprints were EQUAL and the verdict "
            + "was AdoptedVerified");
        verdict.CompilationStatus.Should().Be(CompilationStatus.Pending,
            "refusing is not enough: the live source has to actually get compiled");
    }

    /// <summary>
    /// 🚨 …and the OPPOSITE row still holds. A bundle whose include matches must still adopt as
    /// VERIFIED. Without this the test above is satisfiable by refusing everything, which is the
    /// outage the whole mechanism was designed around.
    /// </summary>
    [Fact]
    public void ABundleWhoseIncludeStillMatches_AdoptsAsVerified()
    {
        var sources = new[]
        {
            Code($"{TypePath}/Source/Thing", "public record Thing;\n\n@@Widget/Snippets/Greeting"),
        };
        var mesh = new Dictionary<string, string>
        {
            ["Widget/Snippets/Greeting"] = "public static class Greeting { }",
        };
        var fingerprint = FingerprintOf(sources, mesh);
        var snapshot = ImmutableDictionary<string, long>.Empty
            .SetItem($"{TypePath}/Source/Thing", 638_000_000_000_000_000);

        NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
                new NodeTypeDefinition
                {
                    CompilationStatus = CompilationStatus.Ok,
                    RequestedSourceStampAt = DateTimeOffset.UtcNow,
                    AdoptedSourceFingerprint = fingerprint,
                    CurrentSourceFingerprint = fingerprint,
                    CurrentSourceVersions = snapshot,
                },
                snapshot,
                canCompileLocally: true)
            .BuildProvenance.Should().Be(BuildProvenance.AdoptedVerified);
    }

    /// <summary>
    /// 🚨 AND <see cref="BuildProvenance.AdoptedUnverified"/> IS STILL NOT A REFUSAL. A legacy
    /// bundle carries no fingerprint at all; refusing those would recompile everything on every
    /// install and, on a <c>Modules:RequirePrebuilt</c> mesh, park every legacy type — the
    /// documented anti-outage property this change must not erode.
    /// </summary>
    [Fact]
    public void ALegacyBundleWithNoFingerprint_StillAdopts()
    {
        var snapshot = ImmutableDictionary<string, long>.Empty
            .SetItem($"{TypePath}/Source/Thing", 638_000_000_000_000_000);

        var verdict = NodeTypeCompilationHelpers.ApplyAdoptedSourceStamp(
            new NodeTypeDefinition
            {
                CompilationStatus = CompilationStatus.Ok,
                RequestedSourceStampAt = DateTimeOffset.UtcNow,
                AdoptedSourceFingerprint = null,
                CurrentSourceFingerprint = "whatever this mesh computed",
                CurrentSourceVersions = snapshot,
            },
            snapshot,
            canCompileLocally: true);

        verdict.BuildProvenance.Should().Be(BuildProvenance.AdoptedUnverified);
        verdict.CompilationStatus.Should().NotBe(CompilationStatus.Pending,
            "an unproven bundle is not a refused one — it must not dispatch a compile");
        verdict.CompiledSources.Should().NotBeNull("withholding the stamp parks it on a RequirePrebuilt mesh");
    }
}
