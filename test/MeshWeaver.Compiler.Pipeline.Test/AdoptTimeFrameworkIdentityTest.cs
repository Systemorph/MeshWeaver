using System;
using System.Collections.Immutable;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// 🔴 <b>The adopt-time framework-identity guard — Systemorph/MeshWeaver#3472.</b>
///
/// <para>On 2026-09-06 <c>Crm/Offer</c> and <c>Crm/Opportunity</c> on a client portal reported
/// <c>compilationStatus: Ok</c> for two and a half hours while every one of their per-instance hubs
/// was dead — one timing out on activation, the other answering "Area not found". Their records
/// named assemblies stamped <c>sc273ee39f…</c>; the replicas serving the portal ran
/// <c>s2f227642d…</c>. Every deal page and every offer page was dead, and the NodeType's own
/// self-report was green throughout.</para>
///
/// <para><b>The two properties this file pins</b>, which are the issue's two acceptance criteria:</para>
///
/// <list type="number">
///   <item>An assembly compiled for framework identity X is never adopted or served by a process
///     whose identity is Y — refused loudly, with a LOCAL COMPILE as the fallback.</item>
///   <item>🚨 <c>compilationStatus</c> must not read <c>Ok</c> for a type whose hubs cannot
///     activate. <c>Ok</c> is a claim SCOPED to
///     <see cref="NodeTypeDefinition.CompiledFrameworkVersion"/>, persisted as though it were
///     absolute; every instrument read the verdict and none of them read the scope.</item>
/// </list>
///
/// <para>These are unit pins on the pure functions that decide both, so the properties are
/// checkable with no mesh and no timing and a regression names itself.
/// <c>ForeignFrameworkBuildIsRefusedAndRebuiltTest</c> carries the end-to-end half on a real mesh:
/// a real compiled pack, its identity staged foreign, converging back to a live-identity build.</para>
/// </summary>
public class AdoptTimeFrameworkIdentityTest
{
    /// <summary>The identity the incident's serving replicas ran.</summary>
    private const string Live = "s2f227642d43f78eab13720c4af06a1be";

    /// <summary>The identity the incident's records named — a real, sealed-green CD set's
    /// identity, which is the point: nothing about these bytes says "from a bad build".</summary>
    private const string Foreign = "sc273ee39fdccbfc088f9aaf1fc548a9a";

    /// <summary><c>Crm/Offer</c>'s record as measured at 20:21Z on 2026-09-06, reduced to the
    /// fields that decide this: a successful compile, an assembly named, and the identity it was
    /// built for.</summary>
    private static NodeTypeDefinition TheOutageRecord() => new()
    {
        Configuration = "config => config.WithContentType<OfferContent>()",
        Sources = ["shared=@Crm/Source"],
        CompilationStatus = CompilationStatus.Ok,
        LastCompiledVersion = 22488,
        LatestReleasePath = "Crm/Offer/Release/20260906192804-abc",
        LatestAssemblyCollection = "local",
        LatestAssemblyPath = "Crm_Offer/v22488-sc273ee3-72a0cd75fee5.dll",
        CompiledFrameworkVersion = Foreign,
        CompiledSources = ImmutableDictionary<string, long>.Empty.Add("Crm/Source/Offer", 42),
    };

    private static NodeTypeDefinition Healthy() =>
        TheOutageRecord() with
        {
            LatestAssemblyPath = "Crm_Offer/v22488-s2f22764-72a0cd75fee5.dll",
            CompiledFrameworkVersion = Live,
        };

    // ── Criterion 1: the refusal ────────────────────────────────────────────────────────────────

    [Fact]
    public void AForeignBuildIsRefused_AndTheRefusalNamesBothIdentities()
    {
        var reason = NodeTypeBuildIdentity.RefusalReason(TheOutageRecord(), Live);

        reason.Should().NotBeNull(
            "an assembly compiled for one framework identity is not loadable by a process running "
            + "another — the failure surfaces as a TypeLoadException inside a collectible ALC at "
            + "activation, with no compile error and nothing to grep, and the loader records it as "
            + "an EMPTY configuration list. A hub resolves its configuration exactly once, so that "
            + "one refused load pins 'Area not found' for the grain's whole lifetime");
        reason.Should().Contain("sc273ee3").And.Contain("s2f22764",
            "BOTH identities, always. The pair is what makes a refusal checkable by hand against "
            + "the CD run that produced the bytes; the 2026-09-06 bake gate named only one "
            + "('regressed on this image') and sent three investigations to the wrong repository");
    }

    [Fact]
    public void TheProcessesOwnBuildIsNotRefused()
        => NodeTypeBuildIdentity.RefusalReason(Healthy(), Live).Should().BeNull(
            "THE CONTROL. Without it every assertion in this file would pass just as well against "
            + "a gate that refused everything — which would take every dynamic NodeType in the "
            + "mesh off its own bytes");

    [Fact]
    public void ARecordThatNamesNoAssemblyIsNotRefused()
    {
        var neverBuilt = TheOutageRecord() with
        {
            CompilationStatus = null,
            LatestAssemblyCollection = null,
            LatestAssemblyPath = null,
            CompiledFrameworkVersion = null,
        };

        NodeTypeBuildIdentity.RefusalReason(neverBuilt, Live).Should().BeNull(
            "there is nothing to load, so there is nothing to refuse. The caller's own "
            + "'no build recorded' branch is the right answer and it already exists at every "
            + "site; answering a refusal here would report an identity problem for a type that "
            + "has simply never been built");
    }

    [Fact]
    public void ARecordThatNamesAnAssemblyButNoIdentityIsRefused()
    {
        var unstamped = TheOutageRecord() with { CompiledFrameworkVersion = null };

        NodeTypeBuildIdentity.RefusalReason(unstamped, Live).Should().NotBeNull(
            "an absent identity cannot be shown ABI-compatible with anything. This is deliberately "
            + "the SAME clause NodeTypeCompilationHelpers.HasUsableBuild has applied since #464 on "
            + "the one load path that was guarded — extending it to the six that were not is "
            + "consistency, not a new policy");
    }

    [Fact]
    public void ANullDefinitionIsNotAVerdict()
        => NodeTypeBuildIdentity.RefusalReason(null, Live).Should().BeNull(
            "nothing was read, so nothing was compared. A probe that cannot reach a verdict must "
            + "not answer its scariest branch on its own inability to run (#890, #2901) — and "
            + "every load site already binds no assembly when the definition is unreadable");

    // ── Criterion 2: the status a process may honestly report ───────────────────────────────────

    [Fact]
    public void AForeignOkNeverReportsOk()
    {
        var def = TheOutageRecord();

        def.CompilationStatus.Should().Be(CompilationStatus.Ok,
            "the RECORD keeps saying what the compiler did, and it did succeed — for somebody "
            + "else. Nothing here rewrites it: a reader-relative verdict written into a shared "
            + "record is how #3395's ping-pong was made, two replicas each correctly overwriting "
            + "the other's answer forever");

        NodeTypeBuildIdentity.ReportedStatus(def, Live).Should().Be(CompilationStatus.Foreign,
            "🚨 THE REGRESSION. A status that says Ok for a type that cannot load is a lie with a "
            + "green tick, and a whole fleet of instruments then repeats it — the deploy sweep, "
            + "get_diagnostics, the compile-progress overlay's redirect and the NodeType overview "
            + "page all read this field, and all four read green through the outage");
    }

    [Fact]
    public void AnHonestOkStillReportsOk()
        => NodeTypeBuildIdentity.ReportedStatus(Healthy(), Live).Should().Be(CompilationStatus.Ok,
            "THE CONTROL for criterion 2. A gate that never says Ok would take every page in the "
            + "mesh off the air, and would pass the assertion above unchanged");

    [Fact]
    public void AnOkThatRecordsNoAssemblyStaysOk()
    {
        var marker = new NodeTypeDefinition
        {
            Description = "a marker type — nothing was built, so nothing foreign is claimed",
            CompilationStatus = CompilationStatus.Ok,
        };

        NodeTypeBuildIdentity.ReportedStatus(marker, Live).Should().Be(CompilationStatus.Ok,
            "a type that claims no build cannot be claiming a foreign one");
    }

    [Theory]
    [InlineData(CompilationStatus.Error)]
    [InlineData(CompilationStatus.Pending)]
    [InlineData(CompilationStatus.Compiling)]
    [InlineData(CompilationStatus.Unavailable)]
    [InlineData(CompilationStatus.Unknown)]
    public void EveryOtherStatusPassesThroughUnchanged(CompilationStatus status)
        => NodeTypeBuildIdentity.ReportedStatus(TheOutageRecord() with { CompilationStatus = status }, Live)
            .Should().Be(status,
                "each of these carries a DIFFERENT remedy and the scope does not change any of "
                + "them: Error is still 'correct the code', Pending/Compiling are still in "
                + "flight, Unavailable is still 'could not be determined'. Folding a framework "
                + "mismatch into Error would send an author to fix source that compiles fine — "
                + "the #641 defect, in the other direction");

    [Fact]
    public void ANullStatusStaysNull()
        => NodeTypeBuildIdentity.ReportedStatus(TheOutageRecord() with { CompilationStatus = null }, Live)
            .Should().BeNull(
                "a never-compiled record is what the first-build kickoff keys on; forging a "
                + "status here would suppress the compile it is waiting to start");

    // ── The WRITER half: the identity travels with the coordinates ──────────────────────────────

    /// <summary>
    /// 🔴 <b>The live defect behind criterion 2, and it needs no second image to reach.</b>
    ///
    /// <para><c>NodeTypeContractHandler.ApplyResolvedSuccess</c> wrote
    /// <c>CompilationStatus = Ok</c> on the HYDRATE path while carrying
    /// <c>CompiledFrameworkVersion</c> forward unchanged. A hydrate only succeeds when the assembly
    /// store RESOLVED bytes — under a key whose framework tag is this process's own — so the record
    /// ended up saying <c>Ok</c>, naming bytes the store had just handed over, and declaring those
    /// bytes unloadable. <c>HasUsableBuild</c> is then false forever, every per-instance activation
    /// takes the ABI-stale recompile path, and after <c>MaxRecompileAttempts</c> the instance binds
    /// the fallback configuration for the grain's whole life. That is "Area not found", under a
    /// green <c>compilationStatus</c> — and the contract handler RACES the activity write-back, so
    /// it re-imposes the foreign stamp over each correct one and the type never converges.</para>
    ///
    /// <para>The rule is the one <c>ResolvedStoreVersion</c> already argues for the store-key
    /// version: "the path and the version are ONE reference, so they must come from ONE source".
    /// The framework identity is the fourth member of that reference and was left out of it.</para>
    /// </summary>
    [Fact]
    public void ResolvedSuccessNeverLeavesOkStandingOverAForeignIdentity()
    {
        var def = TheOutageRecord();
        var node = new MeshNode("Offer", "Crm") { Content = def, NodeType = MeshNode.NodeTypePath };

        // The hydrate response: the store resolved bytes for (path, LastCompiledVersion) — which
        // it can only do under THIS process's framework tag — and the handler echoes the record's
        // own coordinates back as the reference.
        var hydrated = new GetCompilationPathResponse(
            Success: true,
            AssemblyLocation: "/cache/Crm_Offer/v22488.dll",
            Collection: "local",
            Version: "22488",
            Error: null,
            HubConfiguration: null)
        { ContentPath = def.LatestAssemblyPath };

        var stamped = NodeTypeContractHandler.ApplyResolvedSuccess(
            def, hydrated, freshCompile: false, node);

        stamped.CompiledFrameworkVersion.Should().Be(
            NodeTypeCompilationHelpers.FrameworkVersion,
            "the identity travels with the coordinates. This write took its assembly path and its "
            + "store key from the RESPONSE, so it is describing bytes this process resolved under "
            + "its own framework tag — stamping a foreign identity over them fabricates an "
            + "ABI-staleness the store has just disproved, and pins the type in a state nothing "
            + "converges out of");

        NodeTypeBuildIdentity.ReportedStatus(stamped).Should().Be(CompilationStatus.Ok,
            "and the whole point of fixing the writer is that the record it leaves behind is one "
            + "this process can honestly report Ok for");
    }

    /// <summary>
    /// The other half of the same rule, and the reason it is not simply "always stamp live": when
    /// the producer supplied NO reference (a <c>memory://</c> compile, a Null store, unreadable
    /// bytes) the coordinates are RETAINED — and a live identity over retained coordinates would
    /// be the same record-describes-a-build-it-is-not-serving defect reached the other way round.
    /// This is the only case where an ABI-staleness marker is real, and it must not be erased.
    /// </summary>
    [Fact]
    public void ResolvedSuccessOnARetainedReferenceKeepsTheStaleMarker()
    {
        var def = TheOutageRecord();
        var node = new MeshNode("Offer", "Crm") { Content = def, NodeType = MeshNode.NodeTypePath };

        var noReference = new GetCompilationPathResponse(
            Success: true,
            AssemblyLocation: null,
            Collection: null,
            Version: null,
            Error: null,
            HubConfiguration: null);

        var stamped = NodeTypeContractHandler.ApplyResolvedSuccess(
            def, noReference, freshCompile: false, node);

        stamped.LatestAssemblyPath.Should().Be(def.LatestAssemblyPath,
            "nothing was uploaded, so the persisted coordinates are retained — the existing rule");
        stamped.CompiledFrameworkVersion.Should().Be(Foreign,
            "and the identity is retained WITH them. The two fields are one reference and they "
            + "must come from one source; deciding 'came from the response' differently for the "
            + "path and for the identity is how a record starts describing a build that is not "
            + "the one being served (#1368)");
    }

    /// <summary>
    /// #2895's no-op, preserved. A hydrate of a record that already names this process's identity
    /// must still reproduce the persisted definition EXACTLY, so
    /// <c>MeshNodeStreamExtensions.UpdateOwn</c>'s <c>SerializedEquals</c> gate absorbs the whole
    /// write — no node version, no change-feed fan-out, no Postgres row. Kept here as well as in
    /// <c>HydrateIsNotACompileTest</c> because this change touches the very field that decides it.
    /// </summary>
    [Fact]
    public void AHydrateOfOurOwnBuildIsStillANoOp()
    {
        var def = Healthy() with { CompiledFrameworkVersion = NodeTypeCompilationHelpers.FrameworkVersion };
        var node = new MeshNode("Offer", "Crm") { Content = def, NodeType = MeshNode.NodeTypePath };

        var hydrated = new GetCompilationPathResponse(
            Success: true,
            AssemblyLocation: "/cache/Crm_Offer/v22488.dll",
            Collection: def.LatestAssemblyCollection,
            Version: "22488",
            Error: null,
            HubConfiguration: null)
        { ContentPath = def.LatestAssemblyPath };

        NodeTypeContractHandler.ApplyResolvedSuccess(def, hydrated, freshCompile: false, node)
            .Should().Be(def,
                "a hydrate that observed no new fact must mint no version. The identity stamp is "
                + "the live one either way here, so the write still reproduces what is persisted");
    }
}
