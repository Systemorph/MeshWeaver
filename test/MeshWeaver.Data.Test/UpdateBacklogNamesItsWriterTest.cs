using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// MeshWeaver#5799: a read stream on memex-cloud held 408,637 queued <c>UpdateStreamRequest</c>s
/// and nothing could say who posted them — the message carries only delegates. The stream now
/// names the writer once its backlog of not-yet-started writes reaches a reporting depth.
///
/// <para>The flood is reproduced deterministically in the shape that produces one: a write whose
/// own turn posts further writes. While that turn runs none of them can start (a stream's turns
/// are serial), so the backlog reaches exactly the number posted — no timing involved.</para>
/// </summary>
public class UpdateBacklogNamesItsWriterTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Counter(int Value);

    // The writer the report must name. A NAMED method, so the expected text is not a compiler-
    // generated lambda name.
    private static Counter? KeepWriting(Counter? current) => current;

    [HubFact]
    public async Task A_write_that_floods_its_stream_is_named_in_the_backlog_report()
    {
        var host = GetHost();
        var stream = new SynchronizationStream<Counter>(
            new StreamIdentity(host.Address, null),
            host,
            new EntityReference("X", "Y"),
            new ReduceManager<Counter>(host),
            null);
        var floodPosted = new AsyncSubject<Unit>();
        var posted = SyncStreamOptions.DefaultUpdateBacklogWarningDepth + 500;

        stream.Update(
            _ =>
            {
                for (var i = 0; i < posted; i++)
                    stream.Update(KeepWriting, _ => { });
                return null;
            },
            ex => floodPosted.OnError(ex),
            () => { floodPosted.OnNext(Unit.Default); floodPosted.OnCompleted(); });

        await floodPosted.Should().Within(TestTimeouts.Convergence)
            .Emit("the turn that posts the flood completes once it has posted every write");

        stream.LastUpdateBacklogReport.Should().NotBeNull(
            "{0} writes were queued behind one turn, past the reporting depth {1}",
            posted, SyncStreamOptions.DefaultUpdateBacklogWarningDepth);
        stream.LastUpdateBacklogReport!.Value.Depth.Should().Be(SyncStreamOptions.DefaultUpdateBacklogWarningDepth);
        stream.LastUpdateBacklogReport!.Value.Writer.Should().Be(
            $"{typeof(UpdateBacklogNamesItsWriterTest).FullName}.{nameof(KeepWriting)}",
            "the report names the code the CALLER wrote, never the stream's own wrapper lambda");
        stream.LastUpdateBacklogReport!.Value.Hub.Should().Be(stream.Hub.Address.ToString(),
            "the report names the sync hub's own address — the one the disposal-stall lines carry");
        stream.Dispose();
    }

    [HubFact]
    public async Task Writes_that_keep_up_are_never_reported()
    {
        var host = GetHost();
        var stream = new SynchronizationStream<Counter>(
            new StreamIdentity(host.Address, null),
            host,
            new EntityReference("X", "Y"),
            new ReduceManager<Counter>(host),
            null);
        // Negative control: the same number of writes, each posted only after the previous one
        // ran, keeps the backlog at one.
        var done = new AsyncSubject<Unit>();
        var remaining = SyncStreamOptions.DefaultUpdateBacklogWarningDepth + 500;
        void Next()
        {
            if (remaining-- == 0)
            {
                done.OnNext(Unit.Default);
                done.OnCompleted();
                return;
            }
            stream.Update(_ => null, ex => done.OnError(ex), Next);
        }
        Next();

        await done.Should().Within(TestTimeouts.Convergence)
            .Emit("each write starts only after the one before it ran");

        stream.LastUpdateBacklogReport.Should().BeNull("no write ever waited behind another");
        stream.Dispose();
    }

    [Theory]
    [InlineData(999, 0, 1000, null)]
    [InlineData(1000, 0, 1000, 1000)]
    [InlineData(1001, 1000, 1000, null)]
    [InlineData(10_000, 1000, 1000, 10_000)]
    [InlineData(408_637, 10_000, 1000, 100_000)]
    [InlineData(408_637, 100_000, 1000, null)]
    [InlineData(5000, 0, 0, null)]
    public void Reporting_depths_are_the_floor_and_each_tenfold_reported_once(
        int depth, int lastReported, int floor, int? expected)
        => UpdateBacklog.ReportAt(depth, lastReported, floor).Should().Be(expected);
}
