using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Fixture;
using MeshWeaver.Layout.Client;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Layout.Test;

/// <summary>
/// Regression cover for Systemorph/MeshWeaver#2701 — the delayed editor's control stream falling
/// permanently silent after a burst of <c>UpdatePointer</c> writes.
///
/// <para>The mechanism is version arithmetic, not timing. A subscriber stamps every outgoing
/// <see cref="PatchDataChangeRequest"/> with the version of the frame it last APPLIED
/// (<c>StandardReducers.PatchJsonElement</c>: <c>stream.Current?.Version</c>) — an optimistic
/// write is BY CONSTRUCTION based on an earlier owner frame. Whenever an owner frame is in
/// flight — a re-render that the writer has not received yet, and the delayed editor's render
/// takes 100 ms — that stamp is strictly below the owner's <c>Current.Version</c>, and the
/// owner's monotonicity guard in <c>SynchronizationStream.UpdateStream</c> dropped the write at
/// Debug with no rollback, no error and no signal to the writer. The edit was gone; the
/// re-render it should have produced never happened; the view sat silent forever.</para>
///
/// <para>These tests pin the contract by POSTING the wire message a client posts, stamped with a
/// base one frame behind the one the client holds — so they are deterministic and carry no
/// wall-clock dependency at all.</para>
/// </summary>
[Collection("EditorTests")]
public class StaleBaseSubscriberWriteTest(ITestOutputHelper output) : HubTestBase(output)
{
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => base.ConfigureClient(configuration).AddLayoutClient();

    /// <summary>The edited record — two doubles, so the rendered sum names the update that produced it.</summary>
    public record Calculator
    {
        /// <summary>The value the tests write.</summary>
        [Description("This is the X value")] public double X { get; init; }
        /// <summary>Stays zero; present so the editor renders two fields as in EditorTest.</summary>
        [Description("This is the Y value")] public double Y { get; init; }
    }

    private const string DataId = "staleBaseCalc";

    // Same shape as EditorTest.EditorWithDelayedResult: the render is slow enough that an owner
    // frame is reliably in flight while a client writes.
    private UiControl DelayedEditor(LayoutAreaHost host, RenderingContext ctx)
    {
        var editor = host.Hub.ServiceProvider.Edit(Observable.Return(new Calculator()), DataId);
        return Controls.Stack
            .WithView(editor)
            .WithView((h, _) => h.Stream.GetDataStream<Calculator>(DataId)
                .Select(c =>
                {
                    Thread.Sleep(100);
                    return (UiControl)Controls.Markdown($"{c.X + c.Y}");
                }));
    }

    /// <summary>
    /// The #2701 shape: a subscriber's write whose base is one owner frame behind must be MERGED
    /// by the owner, never dropped. The owner's own documentation states the contract — "a
    /// subscriber carries the BASE version it last observed so the owner can fast-forward
    /// (base == current) or merge (base &lt; current)" — and the monotonicity guard, which exists
    /// to protect a MIRROR from stale OWNER frames, was applying itself to the opposite direction.
    /// </summary>
    [Fact]
    public async Task ASubscriberWriteBasedOnAnEarlierOwnerFrame_IsMerged_NotDroppedAsStale()
    {
        var client = GetClient();
        var area = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(nameof(DelayedEditor)));

        var control = await area.GetControlStream(nameof(DelayedEditor))
            .Should().Within(TestTimeouts.Convergence).Match(x => x is not null);
        var stack = control.Should().BeOfType<StackControl>().Subject;
        control = await area.GetControlStream(stack.Areas.First().Area.ToString()!)
            .Should().Within(TestTimeouts.Convergence).Match(x => x is not null);
        var editor = control.Should().BeOfType<EditorControl>().Subject;
        var resultArea = stack.Areas.Last().Area.ToString()!;

        // Let the first render land, so the owner's clock has advanced and the client has applied
        // that frame. Everything after this point is version arithmetic, not timing.
        await area.GetControlStream(resultArea)
            .Should().Within(TestTimeouts.Convergence).Match(x => x is MarkdownControl);

        var appliedVersion = area.Current!.Version;

        // The write a client issues while the NEXT owner frame is still in flight: computed on the
        // previous frame, so stamped one version back. Byte-for-byte the message
        // JsonSynchronizationStream posts on the client's behalf (ToDataChanged →
        // PatchDataChangeRequest(ClientId, x.Version, patch, Patch, ChangedBy)).
        PostStaleBasedWrite(client, area, editor.DataContext!, value: 5, basedOn: appliedVersion - 1);

        // 🚨 The predicate must exclude the baseline render ("0"): a `Markdown: not null` match is
        // satisfied by the render that was already there and would pass without the write ever
        // having landed. The re-render the write produces is the ONLY positive signal here — and
        // its absence is #2701's exact signature, "the observable emitted nothing at all".
        var rendered = await area.GetControlStream(resultArea)
            .Should().Within(TestTimeouts.Convergence)
            .Match(x => x is MarkdownControl { Markdown: var m } && m!.ToString() != "0");

        rendered.Should().BeOfType<MarkdownControl>()
            .Which.Markdown!.ToString().Should().Be("5",
                "an optimistic write is based on an earlier owner frame by construction — the owner "
                + "must merge it, not drop it as stale");
    }

    /// <summary>
    /// The other half of the same role confusion: having APPLIED a subscriber's write, the owner
    /// must keep its own monotonic clock. Adopting the subscriber's base version rewound the
    /// owner's <c>Current.Version</c>, so the frame the owner then broadcast was stamped BELOW
    /// what every other subscriber already held — and their monotonicity guards (correctly, this
    /// time) dropped it. Asserting on the owner's clock rather than on a second subscriber keeps
    /// this deterministic.
    /// </summary>
    [Fact]
    public async Task ApplyingASubscriberWrite_DoesNotRewindTheOwnersClock()
    {
        var client = GetClient();
        var area = client.GetWorkspace().GetRemoteStream<JsonElement, LayoutAreaReference>(
            CreateHostAddress(), new LayoutAreaReference(nameof(DelayedEditor)));

        var control = await area.GetControlStream(nameof(DelayedEditor))
            .Should().Within(TestTimeouts.Convergence).Match(x => x is not null);
        var stack = control.Should().BeOfType<StackControl>().Subject;
        control = await area.GetControlStream(stack.Areas.First().Area.ToString()!)
            .Should().Within(TestTimeouts.Convergence).Match(x => x is not null);
        var editor = control.Should().BeOfType<EditorControl>().Subject;
        var resultArea = stack.Areas.Last().Area.ToString()!;

        await area.GetControlStream(resultArea)
            .Should().Within(TestTimeouts.Convergence).Match(x => x is MarkdownControl);

        var appliedVersion = area.Current!.Version;
        PostStaleBasedWrite(client, area, editor.DataContext!, value: 7, basedOn: appliedVersion - 1);

        // The re-render the write triggers reaches this client as a frame of the owner's own
        // making. Its version must still be ABOVE the frame the client already held: if the owner
        // adopted the write's stale base, every subscriber sitting on `appliedVersion` discards it.
        var frame = await area
            .Where(ci => ci.Value.TryGetProperty("areas", out _))
            .Should().Within(TestTimeouts.Convergence)
            .Match(ci => ci.Value.ToString()!.Contains("\"7\""));

        frame.Version.Should().BeGreaterThanOrEqualTo(appliedVersion,
            "a subscriber's write must not rewind the owner's clock — the frame that carries it "
            + "back out would then be dropped as stale by every other subscriber");
    }

    private static void PostStaleBasedWrite(
        IMessageHub client,
        ISynchronizationStream<JsonElement> area,
        string dataContext,
        int value,
        long basedOn)
    {
        var patch = JsonSerializer.Serialize(new[]
        {
            new { op = "replace", path = $"{dataContext}/x", value }
        });
        client.Post(
            new PatchDataChangeRequest(
                area.StreamId,
                basedOn,
                new RawJson(patch),
                ChangeType.Patch,
                area.ClientId),
            o => o.WithTarget(CreateHostAddress()));
    }

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddLayout(layout => layout.WithView(nameof(DelayedEditor), DelayedEditor));
}
