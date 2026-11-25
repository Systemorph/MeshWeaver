using System;
using System.IO;
using System.Linq;
using MeshWeaver.Compiler;
using MeshWeaver.Hosting;
using MeshWeaver.Mesh;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace MeshWeaver.Updates.Test;

/// <summary>
/// 🚨 A FOLLOWER on a line's stable build must serve the bundles the line's canary produced.
///
/// <para><b>The defect (measured 2026-11-25 on a follower running 4.0.14099):</b> the platform floor
/// stamped on a bundle is the build that PRODUCED it, so every bundle a newer patch of the same
/// compatibility key produced was declined WHOLE (<i>"its platform floor 4.0.14117 … is NEWER than the
/// running platform 4.0.14099"</i>) and the follower served stale adopted builds for as long as it
/// lagged the canary. The compatibility key (same major, same epoch) is the platform's backwards
/// compatibility promise, so the version comparison alone must not decide within it.</para>
///
/// <para><b>The rule since the fix:</b> within one key a newer-patch producer is adopted when the bytes
/// pass the MEASURED member-level link check against the running process
/// (<see cref="ModuleLinkOptions.WithMembers"/>); bytes that call a member the running platform lacks
/// are declined by that measurement, naming the member, and the sweep compiles instead. A different
/// key, a ceiling breach and <see cref="VersionStrictness.Exact"/> still decline on the comparison.</para>
/// </summary>
public class NewerPatchBundleAdoptionTest : IDisposable
{
    private const string Key = "c004e001";
    private const string Running = "4.0.14099";
    private const string Producer = "4.0.14117";

    private readonly string root = Path.Combine(Path.GetTempPath(), "mw-newer-patch-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates the per-test root.</summary>
    public NewerPatchBundleAdoptionTest() => Directory.CreateDirectory(root);

    /// <inheritdoc />
    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { /* temp cleanup */ }
        GC.SuppressFinalize(this);
    }

    private static PrebuiltAdoptionPolicy.Live Live { get; } = new(Key, Running, RequirePrebuilt: false);

    private static AdoptionDecision Decide(
        VersionStrictness strictness, string? producer, string? ceiling = null, string key = Key) =>
        PrebuiltAdoptionPolicy.Decide(
            strictness,
            new PrebuiltAdoptionPolicy.Candidate(key, Running, null)
            {
                ProducerPlatformVersion = producer,
                PlatformCeiling = ceiling,
            },
            Live);

    /// <summary>The bundle's producer is a newer patch of the same key: adopted, pending the measurement.</summary>
    [Theory]
    [InlineData(VersionStrictness.Family)]
    [InlineData(VersionStrictness.Minimum)]
    public void ANewerPatchOfTheSameKey_IsAdopted_PendingTheMemberLinkCheck(VersionStrictness strictness)
    {
        var decision = Decide(strictness, Producer);

        Assert.True(decision.Adopts, decision.Reason);
        Assert.True(decision.LinkCheckRequired, "the version comparison is replaced by a measurement");
        Assert.True(decision.MemberLinkRequired, "only a member-level check sees a method the platform lacks");
        Assert.True(decision.CrossesFloor);
        Assert.Contains(Producer, decision.Reason, StringComparison.Ordinal);
        Assert.Contains(Running, decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>The seed-level consequences of crossing the floor: members are measured and the record is stamped with the RUNNING build.</summary>
    [Fact]
    public void CrossingTheFloor_MeasuresMembers_AndStampsTheRunningBuild()
    {
        var crossing = Decide(VersionStrictness.Family, Producer);
        Assert.Same(ModuleLinkOptions.WithMembers, PrebuiltAdoptionPolicy.LinkOptionsFor(crossing));
        Assert.Equal(Running, PrebuiltAdoptionPolicy.StampedPlatformVersion(crossing, Running, Producer));

        var ordinary = Decide(VersionStrictness.Family, "4.0.14000");
        Assert.Same(ModuleLinkOptions.TypesOnly, PrebuiltAdoptionPolicy.LinkOptionsFor(ordinary));
        Assert.Equal("4.0.14000", PrebuiltAdoptionPolicy.StampedPlatformVersion(ordinary, Running, "4.0.14000"));
        Assert.Equal(Producer, PrebuiltAdoptionPolicy.StampedPlatformVersion(crossing, null, Producer));
    }

    /// <summary>Exact strictness keeps the producer-floor rule, with both versions named.</summary>
    [Fact]
    public void Exact_StillDeclinesANewerProducer_NamingBothVersions()
    {
        var decision = Decide(VersionStrictness.Exact, Producer);

        Assert.False(decision.Adopts);
        Assert.Contains("NEWER", decision.Reason, StringComparison.Ordinal);
        Assert.Contains(Producer, decision.Reason, StringComparison.Ordinal);
        Assert.Contains(Running, decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>An older or equal producer adopts with no link check, exactly as before.</summary>
    [Theory]
    [InlineData("4.0.14099")]
    [InlineData("4.0.14000")]
    [InlineData(null)]
    public void AnOlderOrEqualOrUnknownProducer_AdoptsWithoutALinkCheck(string? producer)
    {
        var decision = Decide(VersionStrictness.Family, producer);

        Assert.True(decision.Adopts, decision.Reason);
        Assert.False(decision.LinkCheckRequired);
        Assert.False(decision.CrossesFloor);
    }

    /// <summary>A different compatibility key (a declared break) is not a floor question: still declined.</summary>
    [Fact]
    public void ADifferentKey_IsNotRescuedByTheMeasurement()
    {
        var decision = Decide(VersionStrictness.Exact, Producer, key: "c003e001");
        Assert.False(decision.Adopts);
        Assert.False(decision.CrossesFloor);
    }

    /// <summary>A ceiling the running build is above still declines, newer producer or not.</summary>
    [Fact]
    public void ACeilingBreach_StillDeclines()
    {
        var decision = Decide(VersionStrictness.Family, Producer, ceiling: "4.0.14050");

        Assert.False(decision.Adopts);
        Assert.False(decision.CrossesFloor, "a ceiling breach is not rescued by the measurement");
    }

    /// <summary>
    /// A newer-patch bundle whose bytes call only members the running platform carries: the measurement
    /// passes and the adoption stands, the record to be stamped with the RUNNING build.
    /// </summary>
    [Fact]
    public void ANewerPatchBundle_WhoseMembersAllResolve_Serves()
    {
        var decision = Decide(VersionStrictness.Family, Producer);
        var path = Module("ResolvingModule", """
            using System;
            using MeshWeaver.Reactive;
            public static class Probe
            {
                public static IObservable<int> Both(IObservable<int> a, IObservable<int> b) => a.WhenBoth(b, (x, y) => x + y);
            }
            """);

        var link = ModulePlatformLink.Check(path, ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory),
            ModuleLinkOptions.WithMembers);
        var verdict = PrebuiltAdoptionPolicy.AfterLink(decision, link, Live);

        Assert.True(verdict.Adopts, verdict.Reason);
        Assert.Equal(AdoptionVerdict.Adopt, verdict.Verdict);
    }

    /// <summary>
    /// 🚨 The safety: a newer-patch bundle that calls a member the running platform does NOT carry is
    /// never bound. The record says why - the member - and the sweep compiles instead.
    /// </summary>
    [Fact]
    public void ANewerPatchBundle_ThatNeedsNewerSurface_IsDeclined_NamingTheMember()
    {
        var decision = Decide(VersionStrictness.Family, Producer);
        // A "newer" platform: the same assembly and type, with a method the running build lacks.
        var newerHub = Emit("MeshWeaver.Messaging.Hub", """
            [assembly: System.Reflection.AssemblyVersion("4.0.0.0")]
            namespace MeshWeaver.Reactive { public static class ParallelReadExtensions { public static int NewInThisBuild() => 1; } }
            """, References(excluding: "MeshWeaver.Messaging.Hub"));
        var path = Module("NeedsNewerModule", """
            public static class Probe { public static int Go() => MeshWeaver.Reactive.ParallelReadExtensions.NewInThisBuild(); }
            """, extra: MetadataReference.CreateFromImage(newerHub), excluding: "MeshWeaver.Messaging.Hub");

        var link = ModulePlatformLink.Check(path, ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory),
            ModuleLinkOptions.WithMembers);
        var verdict = PrebuiltAdoptionPolicy.AfterLink(decision, link, Live);

        Assert.False(verdict.Adopts);
        Assert.Equal(AdoptionVerdict.CompileInstead, verdict.Verdict);
        Assert.Contains("NewInThisBuild", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("compiles instead", verdict.Reason, StringComparison.Ordinal);
    }

    /// <summary>On a require-prebuilt mesh the same bundle is REFUSED loudly instead of compiled.</summary>
    [Fact]
    public void ANewerPatchBundle_ThatNeedsNewerSurface_IsRefused_OnARequirePrebuiltMesh()
    {
        var live = Live with { RequirePrebuilt = true };
        var decision = PrebuiltAdoptionPolicy.Decide(
            VersionStrictness.Family,
            new PrebuiltAdoptionPolicy.Candidate(Key, Running, null) { ProducerPlatformVersion = Producer },
            live);
        var newerHub = Emit("MeshWeaver.Messaging.Hub", """
            [assembly: System.Reflection.AssemblyVersion("4.0.0.0")]
            namespace MeshWeaver.Reactive { public static class ParallelReadExtensions { public static int NewInThisBuild() => 1; } }
            """, References(excluding: "MeshWeaver.Messaging.Hub"));
        var path = Module("NeedsNewerRefusedModule", """
            public static class Probe { public static int Go() => MeshWeaver.Reactive.ParallelReadExtensions.NewInThisBuild(); }
            """, extra: MetadataReference.CreateFromImage(newerHub), excluding: "MeshWeaver.Messaging.Hub");

        var link = ModulePlatformLink.Check(path, ModulePlatformSurface.OfRunningProcess(AppContext.BaseDirectory),
            ModuleLinkOptions.WithMembers);
        var verdict = PrebuiltAdoptionPolicy.AfterLink(decision, link, live);

        Assert.Equal(AdoptionVerdict.Refuse, verdict.Verdict);
        Assert.Contains("NewInThisBuild", verdict.Reason, StringComparison.Ordinal);
    }

    private static System.Collections.Generic.List<MetadataReference> References(string? excluding = null) =>
        [.. CompileReferences.Default.Where(r => r.Display is { } p && p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Path.GetFileNameWithoutExtension(p), excluding, StringComparison.OrdinalIgnoreCase))];

    private string Module(string name, string source, MetadataReference? extra = null, string? excluding = null)
    {
        var refs = References(excluding);
        if (extra is not null)
            refs.Add(extra);
        var bytes = Emit(name, source, refs);
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".dll");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Emit(string assemblyName, string source, System.Collections.Generic.IEnumerable<MetadataReference> references)
    {
        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var buffer = new MemoryStream();
        var result = compilation.Emit(buffer);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return buffer.ToArray();
    }
}
