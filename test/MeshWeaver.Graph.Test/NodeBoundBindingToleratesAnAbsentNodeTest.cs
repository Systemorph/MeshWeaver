using System;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// 🚨 <b>A NODE-BOUND GUI BINDING MUST TOLERATE AN ABSENT NODE</b> — Systemorph/MeshWeaver#3517.
///
/// <para><b>What was observed.</b> 473 <c>fail:</c> lines over four days, on all five
/// <c>memex-cloud</c> pods, 3–5 within a 4 ms window per page render:
/// <c>MeshWeaver.Blazor.EntityViews.RadioGroupView[0] Loading 'delivery' in area 'SendDocument/7/2'
/// … DeliveryFailureException: No node found at 'rbuergi/_Draft/Event_…'</c>. Two unrelated spaces
/// produced it — a user's deleted email-draft node, and a course quiz's not-yet-written
/// <c>{learner}/_Answers/…</c> node.</para>
///
/// <para><b>Why the fix is in the seam and not at the call sites.</b> The "create the node before
/// you bind to it" discipline was ALREADY applied at the SendDocument site
/// (<c>EmailDraftNodeType.EnsureExists</c>) and the storm happened anyway, because the node was
/// deleted with the page still open — which no call-site care can prevent. And the quiz's answers
/// node is deliberately created by the first answer, so binding-before-create is the intended
/// state there. <c>MeshNodeBindingExtensions.Bind</c>'s own contract already named "the node is
/// genuinely ABSENT" as a case it handles; it simply assumed absence arrives as a filtered
/// <c>null</c> emission, when in fact routing mints an authoritative <c>NotFound</c> that
/// TERMINATES the stream and opens <c>MeshNodeStreamCache</c>'s storm-breaker window on the path.
/// </para>
///
/// <para><b>Both arms below fail on the pre-fix seam</b>, each on its first assertion: the binding
/// errored with <c>DeliveryFailureException</c> instead of emitting, so it could neither draw the
/// control empty nor ever pick the node up afterwards.</para>
///
/// <para>The storm-window assertion is the half a log flood hides. The breaker fast-fails WRITES on
/// a windowed path as well as reads (<c>MeshNodeStreamCache.UpdateRaw</c>), so a read that opens one
/// suppresses the very write the bound form is about to make. Asserting on
/// <c>IsStormWindowOpen</c> measures that directly rather than inferring it from the absence of an
/// exception.</para>
/// </summary>
public class NodeBoundBindingToleratesAnAbsentNodeTest(ITestOutputHelper output)
    : MonolithMeshTestBase(output)
{
    /// <summary>The whole-node binding shape the Settings "Display description" editor uses
    /// (<c>bindContent: false</c>, pointer <c>Description</c>) — no content typing in the way, so a
    /// failure here is about the binding and nothing else.</summary>
    private static readonly JsonPointerReference DescriptionPointer = new(nameof(MeshNode.Description));

    private MeshNodeStreamCache Cache =>
        (MeshNodeStreamCache)Mesh.ServiceProvider.GetRequiredService<IMeshNodeStreamCache>();

    private IObservable<object?> BindDescription(string path) =>
        MeshNodeBindingExtensions.Bind(Mesh, path, bindContent: false, subPath: null, DescriptionPointer);

    private static string NewId(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private static MeshNode Page(string id, string description) => new(id, TestPartition)
    {
        Name = id,
        NodeType = "Markdown",
        State = MeshNodeState.Active,
        Description = description,
    };

    private static string? Text(object? emission) =>
        emission is JsonElement { ValueKind: JsonValueKind.String } je ? je.GetString() : null;

    /// <summary>
    /// The COURSE-QUIZ shape: the node is deliberately not written until the learner acts. The
    /// binding must draw the control empty, leave no breaker window on the path, and — still
    /// subscribed, no <c>.Take(1)</c> — pick the node up the moment it appears.
    /// </summary>
    [Fact]
    public async Task ABindingOnANodeThatDoesNotExistYet_DrawsEmpty_LeavesNoStormWindow_AndPicksItUpOnCreate()
    {
        var id = NewId("not-yet-");
        var path = $"{TestPartition}/{id}";

        var bound = BindDescription(path).Replay();
        using var connection = bound.Connect();

        var first = await bound.Should().Within(TestTimeouts.Convergence).Emit(
            "a binding on an absent node draws the control EMPTY — pre-fix it errored with "
            + "DeliveryFailureException('No node found at …') instead, which is #3517's log flood");
        first.Should().BeNull("there is no node, so there is no field value — not a fault");

        Cache.IsStormWindowOpen(path).Should().BeFalse(
            "the gate means the NotFound is never minted, so the storm breaker never opens a window "
            + "on this path — and the breaker fast-fails WRITES too, so a window here would suppress "
            + "the write the bound form is about to make");

        await NodeFactory.CreateNode(Page(id, "Written on first answer")).Should().Within(TestTimeouts.Convergence).Emit();

        var live = await bound.Should().Within(TestTimeouts.Convergence).Match(
            v => Text(v) == "Written on first answer",
            "the binding stayed subscribed through the absent state, so the node appearing "
            + "populates the control with no re-render and no new subscription");
        Text(live).Should().Be("Written on first answer");
    }

    /// <summary>
    /// The SEND-DOCUMENT shape, reproduced exactly as production produced it: the node existed when
    /// the page was opened and was deleted underneath it, so every subsequent RENDER PASS re-binds
    /// against a path that is now gone. That re-bind is where the 3–5-lines-per-render flood came
    /// from, and it is what this arm measures.
    /// </summary>
    [Fact]
    public async Task ReBindingAfterTheNodeWasDeleted_DrawsEmpty_AndLeavesNoStormWindow()
    {
        var id = NewId("deleted-");
        var path = $"{TestPartition}/{id}";

        await NodeFactory.CreateNode(Page(id, "Before the delete")).Should().Within(TestTimeouts.Convergence).Emit();

        // Control: while the node is there the binding reads it, so a null below cannot be the
        // binding failing to read a node it can see.
        var before = await BindDescription(path).Should().Within(TestTimeouts.Convergence).Match(
            v => Text(v) == "Before the delete",
            "the binding reads the live node while it exists");
        Text(before).Should().Be("Before the delete");

        await NodeFactory.DeleteNode(path).Should().Within(TestTimeouts.Convergence).Emit();

        // A FRESH binding — one render pass after the delete. This is the subscription production
        // was creating 3–5 times per render and NotFound-storming on every one.
        var after = await BindDescription(path).Should().Within(TestTimeouts.Convergence).Emit(
            "a re-bind after the node was deleted draws the control EMPTY — pre-fix each render "
            + "pass minted another 'No node found at …' fail: line (#3517)");
        after.Should().BeNull("the node is gone, so the bound field has no value");

        Cache.IsStormWindowOpen(path).Should().BeFalse(
            "a deleted node's path must not carry a breaker window opened by the view that is "
            + "merely still showing it");
    }

    /// <summary>
    /// 🚨 <b>THE ONE WAY THIS FIX COULD BE WORSE THAN THE BUG</b>, and it would be SILENT: an
    /// existence gate that answers "absent" for a node that is plainly there blanks the control for
    /// every viewer, with nothing logged and nothing to grep. So the gate is asserted against the
    /// path shapes whose query routing is NOT uniform — the two that would have taken a whole
    /// family of editors down with them:
    /// <list type="bullet">
    ///   <item><b>A satellite path</b> (<c>{x}/_Comment/{id}</c> → the annotations table; the same
    ///     routing as <c>{x}/_Thread/{id}</c>, which <c>ThreadComposerView</c> binds). A query that
    ///     does not TARGET a satellite path has its satellite rows excluded by construction
    ///     (<c>StorageAdapterMeshQueryProvider.IsExcludedFromResults</c>, mirroring Postgres's
    ///     per-prefix tables) — so a gate that were not satellite-targeted would report every
    ///     thread composer's node missing.</item>
    ///   <item><b>A partition root</b> (the shape <c>SettingsLayoutArea</c> binds for a space's
    ///     Display name / description). A root is deliberately dropped from a
    ///     <c>scope:descendants</c> listing; an exact read keeps it, and this pins that the gate
    ///     gets the read it needs and not the listing.</item>
    /// </list>
    /// Each case proves the node exists through the OWNER's stream first, so a null from the
    /// binding can only be the gate's verdict and nothing else.
    /// </summary>
    [Fact]
    public async Task TheGateNeverBlanksANodeThatExists_OnASatellitePath_OrAPartitionRoot()
    {
        var docId = NewId("gated-");
        var docPath = $"{TestPartition}/{docId}";
        var satellitePath = $"{docPath}/_Comment/c1";

        await NodeFactory.CreateNode(Page(docId, "The document")).Should().Within(TestTimeouts.Convergence).Emit();
        await NodeFactory.CreateNode(MeshNode.FromPath(satellitePath) with
        {
            Name = "A comment",
            NodeType = "Comment",
            State = MeshNodeState.Active,
            Description = "Satellite description",
        }).Should().Within(TestTimeouts.Convergence).Emit();

        // Control: the OWNER says the satellite node is there. Anything null below is the gate.
        var owned = await Mesh.GetMeshNodeStream(satellitePath).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit("the satellite node exists");
        owned.Description.Should().Be("Satellite description");

        var satellite = await BindDescription(satellitePath).Should().Within(TestTimeouts.Convergence).Match(
            v => Text(v) == "Satellite description",
            "an exact-path gate TARGETS the satellite path, so satellite rows are not excluded — "
            + "a non-targeted query would report every thread composer's node missing");
        Text(satellite).Should().Be("Satellite description");

        // The partition root, read through the same seam.
        var root = await Mesh.GetMeshNodeStream(TestPartition).Where(n => n is not null)
            .Should().Within(TestTimeouts.Convergence).Emit("the partition root exists");
        root.Name.Should().NotBeNullOrEmpty();

        var boundRoot = await MeshNodeBindingExtensions
            .Bind(Mesh, TestPartition, bindContent: false, subPath: null,
                new JsonPointerReference(nameof(MeshNode.Name)))
            .Should().Within(TestTimeouts.Convergence).Match(
                v => Text(v) == root.Name,
                "a partition root is dropped from a descendants LISTING but kept by an exact read — "
                + "the gate must use the read, or every space's settings editor draws empty");
        Text(boundRoot).Should().Be(root.Name);
    }
}
