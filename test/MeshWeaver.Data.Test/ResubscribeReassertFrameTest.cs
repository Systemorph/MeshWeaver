using System.Linq;
using System.Reactive.Linq;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// Deterministic pin for the frame an owner serves a RESUBSCRIBING subscriber
/// (<c>JsonSynchronizationStream.BuildReassertFrame</c>) — the seam behind the concurrent
/// cross-hub write loss of issue #945.
///
/// <para>A resubscribe means "re-assert my stream", so the owner pushes its current state back
/// as a <see cref="ChangeType.Full"/> onto the stream it is ALREADY serving. Two properties of
/// that frame are load-bearing, and getting either wrong loses data silently:</para>
/// <list type="number">
///   <item><b>The value is the stream's CURRENT state, read when the frame is built.</b> The
///     builder runs inside the stream's update turn, so it must read <c>stream.Current</c> itself
///     rather than close over a snapshot bound before the turn was scheduled. Writing back a
///     pre-turn capture overwrites every frame that committed in between — the stream moves
///     BACKWARD, the owner's outbound JSON cursor is re-based on the rolled-back state, and the
///     erased entries are never re-sent. Later patches still apply on top, so the mirror follows
///     the owner forever at a CONSTANT deficit with no error anywhere. Measured on the #945 repro:
///     the mirror went 245 entries@v246 → 234 entries@v235 mid-burst and finished 11 entries short
///     of the owner while all 288 concurrent writes acked success.</item>
///   <item><b>The version is the state's OWN version — the owner's content clock — never
///     <c>stream.Hub.Version</c>.</b> Content frames carry the owner data source's clock
///     (<see cref="WorkspaceExtensions.ApplyChanges"/>); <c>Hub.Version</c> is this stream's
///     sync-hub message counter, an unrelated sequence. Mixing the two defeats the receive-side
///     monotonicity guard in both directions: a stale Full stamped with the higher sync clock
///     slips past the guard that exists to drop it, and a sync clock running ahead makes every
///     later content patch look stale so the mirror freezes. Same rule as
///     <c>StandardReducers.PatchJsonElement</c> ("CARRY the base version … NEVER
///     stream.Hub.Version").</item>
/// </list>
/// </summary>
public class ResubscribeReassertFrameTest(ITestOutputHelper output) : HubTestBase(output)
{
    /// <summary>A content version far outside any hub's message counter, so the version assertion
    /// below can only pass if the frame carries the CONTENT clock rather than the sync-hub clock.</summary>
    private const long SentinelContentVersion = 999_999;

    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => base.ConfigureHost(configuration)
            .AddData(data => data.AddSource(ds => ds
                .WithType<MyData>(t => t.WithKey(d => d.Id))));

    [HubFact]
    public async Task ReassertFrame_CarriesCurrentStateAtItsOwnVersion_NotTheSyncHubClock()
    {
        var host = GetHost();
        var workspace = host.GetWorkspace();
        var accessService = host.ServiceProvider.GetRequiredService<AccessService>();
        accessService.SetContext(new AccessContext { ObjectId = "alice", Name = "Alice" });

        var collectionName = workspace.DataContext.GetTypeSource(typeof(MyData))!.CollectionName;
        var stream = workspace.GetStream(new CollectionsReference(collectionName))!;

        // Real content frames first, so the builder is exercised against a live, populated stream.
        host.Post(
            new DataChangeRequest().WithUpdates(new MyData("v", "value-1")),
            o => o.WithAccessContext(accessService.Context!));

        await stream
            .Where(ci => ci.Value is not null
                         && ci.Value.Collections.GetValueOrDefault(collectionName)
                             ?.Instances.Values.OfType<MyData>()
                             .Any(d => d.Text == "value-1") == true)
            .Take(1)
            .Should().Within(15.Seconds())
            .Emit();

        // Re-assert the same state at a sentinel CONTENT version. This is the owner's own
        // "carry the version the state has" shape; it separates the content clock from the
        // sync-hub clock by a margin no message counter reaches in a test.
        stream.Update(
            current => new ChangeItem<EntityStore>(current!, stream.StreamId, SentinelContentVersion),
            _ => { });

        await stream
            .Where(ci => ci.Version == SentinelContentVersion)
            .Take(1)
            .Should().Within(15.Seconds())
            .Emit();

        var live = stream.Current!;
        var frame = JsonSynchronizationStream.BuildReassertFrame(stream);

        frame.Should().NotBeNull("a stream that holds state must have something to re-assert");
        frame!.ChangeType.Should().Be(ChangeType.Full,
            "a re-assert is the owner's complete authoritative state, so an orphaned mirror re-converges");

        // (1) The value is the state read AT BUILD TIME — never an earlier capture. Reference
        // identity with Current is what proves the builder read the stream, not a closure.
        frame.Value.Should().BeSameAs(live.Value,
            "the re-assert must carry the stream's CURRENT state; re-asserting a snapshot captured "
            + "before the update turn rolls the stream backward over frames that committed in "
            + "between, and those entries are never re-sent (#945)");

        // (2) The version is the content clock, NOT this stream's sync-hub message counter.
        frame.Version.Should().Be(SentinelContentVersion,
            "the re-assert must carry the version its state already has — the owner's content clock. "
            + "Stamping stream.Hub.Version mixes an unrelated counter into the stream's version "
            + "sequence, which either sneaks a stale Full past the receive-side monotonicity guard "
            + "or freezes the mirror by making every later content patch look stale (#945)");
    }
}
