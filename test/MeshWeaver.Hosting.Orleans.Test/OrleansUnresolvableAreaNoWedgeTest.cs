using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// Pins the 2026-06-28 home/side-panel wedge: an UNRESOLVABLE layout-area subscription must surface
/// an error FAST (a visible "Area not found" placeholder, or an <see cref="DeliveryFailureException"/>),
/// never leave the subscriber pending until the 60 s request timeout → spinner → resubscribe/re-render
/// storm → circuit wedge. The colon form <c>@@("area:Pinned")</c> was one way to land on an
/// unresolvable area; this test pins the underlying STREAM-HANDLING contract regardless of how the
/// area name was produced.
///
/// <para>Two distinct shapes, both observed live:</para>
/// <list type="bullet">
///   <item><b>Unknown area on a node that HAS a layout</b> (the home regions: the user node renders a
///     markdown page that embeds areas that turned out not to be registered).</item>
///   <item><b>Layout area on a non-existent child</b> (the side panel: <c>rbuergi/_Thread/hello-a763</c>
///     — the partition resolves, the thread child does not → the SubscribeRequest hung 60 s with
///     "No response received in hub … target hub was not found").</item>
/// </list>
///
/// <para>Bounded <c>WaitAsync(15 s)</c> turns the wedge into a deterministic <see cref="TimeoutException"/>
/// (RED); a surfaced placeholder/exception completes well inside the bound (GREEN). Runs on the real
/// Orleans routing — the same surface the prod wedge traversed.</para>
/// </summary>
public class OrleansUnresolvableAreaNoWedgeTest(ITestOutputHelper output) : OrleansSharedTestBase(output)
{
    /// <summary>
    /// Subscribing to an UNKNOWN area on a node that HAS a layout (here the seeded <c>TestUser</c> node,
    /// which has <c>AddDefaultLayoutAreas</c>) must surface the visible "Area not found" placeholder
    /// (<see cref="LayoutDefinition"/> renders it when no renderer matches) — fast, not a spin.
    /// The colon form is included because that is the exact home/Space embed shape.
    /// </summary>
    [Theory]
    [InlineData("area:Search")]   // the legacy colon form — the exact home/Space embed wedge shape
    [InlineData("area:Pinned")]
    [InlineData("NoSuchArea_xyz")] // plain non-existent area — baseline (no colon)
    public async Task UnknownArea_OnNodeWithLayout_SurfacesError_DoesNotWedge(string badArea)
    {
        // "TestUser" is seeded with AddDefaultLayoutAreas on the silo — a node that HAS a layout,
        // so the area host runs LayoutDefinition.Render and can answer "no renderer for this area".
        var reader = GetClient($"reader-{Guid.NewGuid():N}", "TestUser");
        await AssertAreaSurfacesError_DoesNotWedge(reader, new Address("TestUser"), badArea);
    }

    /// <summary>
    /// Subscribing to a layout area on a NON-EXISTENT CHILD (partition resolves, child does not) — the
    /// exact <c>rbuergi/_Thread/hello-a763</c> side-panel shape — must surface a proper error fast, not
    /// hang the full 60 s request timeout.
    /// </summary>
    [Fact]
    public async Task LayoutArea_OnNonexistentChild_SurfacesError_DoesNotWedge()
    {
        var reader = GetClient($"reader-{Guid.NewGuid():N}", "TestUser");
        // TestUser resolves; the _Thread child does not — non-empty remainder → must surface NotFound.
        var missingThread = new Address("TestUser", "_Thread", $"missing-{Guid.NewGuid():N}");
        await AssertAreaSurfacesError_DoesNotWedge(reader, missingThread, "Overview");
    }

    private async Task AssertAreaSurfacesError_DoesNotWedge(IMessageHub reader, Address address, string badArea)
    {
        Output.WriteLine($"[test] subscribing area '{badArea}' @ {address}");

        // GetControlStream emits the CONTROL rendered at /areas/{area} (the base "Building layout…"
        // progress frame carries empty areas and is intentionally skipped — Where(non-null)). The
        // area MUST surface either that control (the visible "Area not found" placeholder) OR OnError
        // within the bound. Timeout(15s) turns a hang into a loud RED (the spinner/wedge).
        Notification<object?> first;
        try
        {
            first = await reader
                .GetControlStream(address, badArea)
                .Materialize()
                .FirstAsync()
                .Timeout(15.Seconds())
                .ToTask();
        }
        catch (TimeoutException)
        {
            throw new Exception(
                $"WEDGE: subscribing to unresolvable area '{badArea}' @ {address} never surfaced a control "
                + "or error within 15s — only the 'Building layout…' progress frame ever arrived. The area "
                + "must show a visible 'Area not found' placeholder (or fail with a proper exception), not "
                + "spin forever (→ resubscribe/re-render storm → circuit wedge).");
        }

        if (first.Kind == NotificationKind.OnError)
        {
            first.Exception.Should().BeOfType<DeliveryFailureException>(
                "an unresolvable area must surface a proper DeliveryFailure the GUI can render, not a raw fault");
            first.Exception!.Message.Should().Contain("No node found",
                "the failure must carry the actionable description so the GUI shows a friendly error");
            Output.WriteLine($"[test] area '{badArea}' surfaced OnError fast: {first.Exception.Message}");
        }
        else
        {
            first.Kind.Should().Be(NotificationKind.OnNext, "an area subscription must emit a control or error, not complete empty");
            var asJson = JsonSerializer.Serialize(first.Value, reader.JsonSerializerOptions);
            asJson.Should().Contain("Area not found",
                "the surfaced control must be the visible NotFound placeholder so the user sees an error, not a spinner");
            Output.WriteLine($"[test] area '{badArea}' surfaced NotFound placeholder fast.");
        }
    }
}
