using MeshWeaver.Observability;
using Xunit;

namespace MeshWeaver.Observability.Test;

/// <summary>
/// Pins the identity function against the bursts that actually defeated the four earlier attempts on
/// memex.systemorph.com (2026-08-08/09). Every case here is a real production line, not a
/// hand-invented one — the previous rules all passed hand-invented input and failed these.
/// </summary>
public class StructuralIdentityTest
{
    private static LogBurst Burst(string category, int eventId, string message,
        string? exception = null, string? frame = null) =>
        new(category, eventId, LogSeverity.Error, message,
            LogLineParser.Normalize(message), exception, frame);

    /// <summary>
    /// Round 3's failure: one ROUTER_TRAFFIC defect, reported once per target hub, produced ~50
    /// incidents. The subject sits AFTER a label.
    /// </summary>
    [Theory]
    [InlineData("Claims")]
    [InlineData("Edu")]
    [InlineData("X")]
    [InlineData("ReinsuranceDemo")]
    public void OneDefect_ManyTargets_IsOneIncident(string target)
    {
        string Msg(string t) =>
            $"ROUTER_TRAFFIC: RawJson has the mesh hub as sender (sender: mesh/N-u6rl0oAUuc, target: {t}). "
            + "The mesh hub is the ROUTER and must not execute work.";

        var baseline = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Messaging.MessageHub", 0, Msg("Claims")));

        StructuralLogIncidentIdentity.Compute(Burst("MeshWeaver.Messaging.MessageHub", 0, Msg(target)))
            .Should().Be(baseline);
    }

    /// <summary>
    /// Round 4's failure — the one I stopped before patching: ~22 incidents for one reconcile defect,
    /// with the subject BEFORE the colon, where a `label: value` rule cannot see it.
    /// </summary>
    [Theory]
    [InlineData("Chess")]
    [InlineData("Edu")]
    [InlineData("Underwriting")]
    [InlineData("LinkedIn")]
    public void OneDefect_ManyPlugins_IsOneIncident(string plugin)
    {
        string Msg(string p) =>
            $"[PluginGating] {p}: reconcile is NOT CONVERGING — rewrote {p}/_Access/Public_Access, "
            + $"{p}/_Access/Anonymous_Access, which this hub already wrote.";

        var baseline = StructuralLogIncidentIdentity.Compute(Burst("PluginGating", 0, Msg("Chess")));

        StructuralLogIncidentIdentity.Compute(Burst("PluginGating", 0, Msg(plugin)))
            .Should().Be(baseline);
    }

    /// <summary>
    /// Round 1's failure, in the other direction: collapsing must NOT go so far that genuinely
    /// different faults share an incident. Different exceptions at the same site stay apart.
    /// </summary>
    [Fact]
    public void DifferentExceptions_AtTheSameSite_StayApart()
    {
        var a = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Hosting.Orleans.MessageHubGrain", 0, "call failed",
                exception: "System.TimeoutException"));
        var b = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Hosting.Orleans.MessageHubGrain", 0, "call failed",
                exception: "Orleans.Runtime.SiloUnavailableException"));

        b.Should().NotBe(a);
    }

    [Fact]
    public void DifferentTopFrames_ForTheSameException_StayApart()
    {
        var a = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Data.MeshDataSource", 0, "boom",
                exception: "System.InvalidOperationException", frame: "MeshDataSource.Apply(MeshNode)"));
        var b = StructuralLogIncidentIdentity.Compute(
            Burst("MeshWeaver.Data.MeshDataSource", 0, "boom",
                exception: "System.InvalidOperationException", frame: "MeshDataSource.Flush()"));

        b.Should().NotBe(a);
    }

    [Fact]
    public void DifferentEventIds_InTheSameCategory_StayApart()
    {
        // The event id is what the code assigns to a log SITE — two sites in one class are two faults.
        StructuralLogIncidentIdentity.Compute(Burst("PluginGating", 1, "reconcile is NOT CONVERGING"))
            .Should().NotBe(
                StructuralLogIncidentIdentity.Compute(Burst("PluginGating", 0, "reconcile is NOT CONVERGING")));
    }

    /// <summary>
    /// A burst with neither exception nor frame is identified by its log SITE alone — the message
    /// contributes nothing, so no wording change can fork it.
    /// </summary>
    [Fact]
    public void BareDiagnostics_AreIdentifiedByTheirLogSite()
    {
        var a = StructuralLogIncidentIdentity.Compute(
            Burst("DynamicTypePreWarmer", 0, "REFUSING READINESS — 3 NodeType(s) regressed on this image"));
        var b = StructuralLogIncidentIdentity.Compute(
            Burst("DynamicTypePreWarmer", 0, "REFUSING READINESS — 17 NodeType(s) regressed on this image"));

        b.Should().Be(a);

        // …and the same holds for a wholly different wording at the same site: prose is not part of
        // the key at all, which is the property four earlier rules lacked.
        StructuralLogIncidentIdentity.Compute(
                Burst("DynamicTypePreWarmer", 0, "something else entirely"))
            .Should().Be(a);
    }

    [Fact]
    public void Identity_IsShortAndStable()
    {
        var id = StructuralLogIncidentIdentity.Compute(Burst("Some.Category", 0, "a message"));

        id.Should().HaveLength(16);
        StructuralLogIncidentIdentity.Compute(Burst("Some.Category", 0, "a message")).Should().Be(id);
    }
}
