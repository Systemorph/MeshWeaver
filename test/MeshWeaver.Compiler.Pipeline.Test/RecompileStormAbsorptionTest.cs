using MeshWeaver.Graph.Configuration;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// One logical event must cost ONE compile (#2544).
///
/// <para>🚨 A release request was identified by WHEN it was asked, never by WHAT it asks for:
/// <c>RequestedReleaseAt</c> is a fresh <c>UtcNow</c> on every write, so two requests for identical
/// content were two distinct values that could never be recognised as one. A trigger arriving while
/// the type was Pending/Compiling was parked WITHOUT advancing the high-water — by design, so it
/// would not be lost — and therefore re-fired on the first compile's own terminal write-back. The
/// compile does not re-trigger itself; it is the CLOCK that releases the next queued trigger.</para>
///
/// <para>Measured in production on memex-cloud: compiles arriving in pairs 65 ms apart on
/// <c>Training/Tour</c>, three inside 2 s on <c>Publish/Deck</c>, and SEVEN for one merge on
/// <c>Store/Plugin</c> — each invalidating the type's instance hubs and raising the "newer build
/// available" adornment. Reported from the user side as a slide deck that kept recompiling while
/// being viewed.</para>
/// </summary>
public class RecompileStormAbsorptionTest
{
    private const string Token = "fw=sABC;mod=deadbeef;src=42";

    private static NodeTypeDefinition Def(
        string? dispatched, bool force = false) => new()
    {
        DispatchedBuildInputs = dispatched,
        RequestedReleaseForce = force,
    };

    /// <summary>The whole point: a second request for the SAME inputs is absorbed, not queued.</summary>
    [Fact]
    public void A_request_for_the_inputs_already_compiling_is_absorbed()
        => NodeTypeCompilationHelpers.IsSatisfiedByInFlightCompile(Def(Token), Token)
            .Should().BeTrue(
                "the in-flight compile produces byte-for-byte what this request asks for, so "
                + "re-running it buys nothing and costs an instance-hub invalidation plus a "
                + "'newer build available' adornment on every viewer");

    /// <summary>
    /// 🚨 Sources moved after the dispatch, so the in-flight compile will NOT produce what this
    /// request asks for. Absorbing here would silently drop the user's latest edits — the one
    /// failure worse than the storm this fixes.
    /// </summary>
    [Fact]
    public void A_request_for_DIFFERENT_inputs_still_compiles()
        => NodeTypeCompilationHelpers.IsSatisfiedByInFlightCompile(
                Def("fw=sABC;mod=deadbeef;src=41"), Token)
            .Should().BeFalse("the inputs changed — this request is not the one in flight");

    /// <summary>Force is the user's explicit escape hatch and must survive the coalescing.</summary>
    [Fact]
    public void A_FORCED_request_always_compiles()
        => NodeTypeCompilationHelpers.IsSatisfiedByInFlightCompile(Def(Token, force: true), Token)
            .Should().BeFalse("RequestedReleaseForce is the escape hatch every UI and MCP path sets");

    /// <summary>
    /// An UNSTAMPED in-flight compile parks exactly as before. Several kickoff paths flip straight
    /// to Pending without going through the release watcher, so nothing recorded what they were
    /// built for — absorbing against an unknown input set would be a guess, and the safe answer is
    /// the old behaviour.
    /// </summary>
    [Fact]
    public void An_unstamped_in_flight_compile_is_never_absorbed_against()
        => NodeTypeCompilationHelpers.IsSatisfiedByInFlightCompile(Def(dispatched: null), Token)
            .Should().BeFalse(
                "a direct Pending flip records no token; absorbing against an unknown input set "
                + "would risk dropping a request the in-flight compile does not satisfy");
}
