using System;
using System.Text.Json;
using MeshWeaver.AI;   // MeshOperations — its namespace is a frozen binary contract (#2370)
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 <b><c>get_diagnostics</c> must describe the BUILD, never claim what is being SERVED</b> —
/// Systemorph/MeshWeaver#2471.
///
/// <para>The <c>Ok</c> reply used to end <i>"The NodeType assembly was built without errors <b>and
/// is loaded</b>."</i> The first half is what this formatter knows: it reads the node's record. The
/// second half is a claim about a hub's runtime state that nothing in the read establishes — and on
/// memex, 2026-08-26, it was FALSE for over 30 minutes while the portal served the previous build
/// through two NodeType recycles, four instance recycles and a forced compile.</para>
///
/// <para>That is the worst shape a diagnostic can take: a success signal that is not measuring the
/// thing it names. It makes a merged, published, correct fix indistinguishable from one that is
/// live, and it silently invalidates "I fixed it and verified the compile is green" for every fix
/// on the portal. The same family as an MCP write reporting <c>Patched:</c> without landing (#2469)
/// and a cancelled CI run reading as a settled red one (#2470).</para>
///
/// <para>Pure formatter assertions — no mesh, no hub. The wording IS the contract here, exactly as
/// the <c>"Compile SUCCEEDED"</c> pin above it has always been.</para>
/// </summary>
public class DiagnosticsDescribesTheBuildNotTheServeTest
{
    private const string TypePath = "ReinsuranceDemo/Walkthrough";
    private const string Mvid = "0123456789abcdef0123456789abcdef";

    private static readonly DateTimeOffset CompiledAt =
        new(2026, 8, 26, 23, 4, 54, TimeSpan.Zero);

    private static string Ok(string? mvid) => MeshOperations.FormatDiagnostics(
        CompilationStatus.Ok, TypePath, error: null, startedAt: null,
        lastCompiledAt: CompiledAt, JsonSerializerOptions.Default, publishedAssemblyMvid: mvid);

    /// <summary>
    /// The claim that is not backed by anything must be gone. A reader acting on "is loaded" stops
    /// looking — which is why the #2471 session spent 30 minutes recycling instead of comparing
    /// builds.
    /// </summary>
    [Fact]
    public void TheOkReply_NoLongerClaimsTheAssemblyIsLoaded()
    {
        var reply = Ok(Mvid);

        reply.Should().NotContain("is loaded",
            "the formatter reads the NODE's record; whether any hub is executing those bytes is "
            + "not knowable from it, and asserting it is the #2471 lie with a green tick");
        reply.Should().Contain("Compile SUCCEEDED at",
            "the half it DOES know must still be stated as plainly as before");
        reply.Should().Contain("built without errors");
    }

    /// <summary>
    /// …and it must say WHICH build, so the claim can be checked rather than believed. Without the
    /// identity in the reply there is nothing for a caller to compare the served bytes against, and
    /// "verify by rendering the screen and counting what comes back" stays the only honest check.
    /// </summary>
    [Fact]
    public void TheOkReply_NamesTheBuildItIsAbout_AndPointsAtTheCheckThatIsAboutTheServedBytes()
    {
        var reply = Ok(Mvid);

        reply.Should().Contain(Mvid, "the reply must name the build the status is about");
        reply.Should().Contain("\"mvid\"",
            "as a FIELD, not only inside prose — a caller compares it, it does not read it");
        reply.Should().Contain("not what any hub is currently executing");
        reply.Should().Contain("$Banner",
            "the reader is pointed at the one check that IS about the served bytes");
    }

    /// <summary>
    /// A node stamped before the identity existed still answers, unchanged in substance: no
    /// fabricated mvid, and still no "is loaded". Degrading to silence about the identity is
    /// correct; degrading back to the overclaim would not be.
    /// </summary>
    [Fact]
    public void WithNoRecordedIdentity_ItStaysHonestRatherThanReverting()
    {
        var reply = Ok(null);

        reply.Should().Contain("Compile SUCCEEDED at");
        reply.Should().NotContain("is loaded");
        reply.Should().Contain("not what any hub is currently executing");
    }

    /// <summary>
    /// The pre-existing 6-argument overload must keep working and keep meaning what it meant — it
    /// is a public entry point, and an added optional parameter (rather than this overload) would
    /// be the binary break <c>scripts/check-record-signatures.py</c> exists to refuse, one shape
    /// over.
    /// </summary>
    [Fact]
    public void TheOriginalOverloadStillCompilesAndAnswers()
    {
        var reply = MeshOperations.FormatDiagnostics(
            CompilationStatus.Ok, TypePath, error: null, startedAt: null,
            lastCompiledAt: CompiledAt, JsonSerializerOptions.Default);

        reply.Should().Contain("Compile SUCCEEDED at");
        reply.Should().NotContain("is loaded");
    }

    /// <summary>The Error branch is untouched — the one case where "fix the source" IS the right
    /// instruction must not be blurred by this change.</summary>
    [Fact]
    public void TheErrorReplyIsUnchanged()
    {
        var reply = MeshOperations.FormatDiagnostics(
            CompilationStatus.Error, TypePath, error: "CS0246: not found", startedAt: null,
            lastCompiledAt: null, JsonSerializerOptions.Default, publishedAssemblyMvid: Mvid);

        reply.Should().Contain("Compile FAILED");
        reply.Should().Contain("CS0246: not found");
    }
}
