using System;
using System.Linq;
using System.Threading;
using MeshWeaver.Mesh;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// <see cref="ActivityTracker.InFlight"/> is read by the portal's <c>/drain</c> endpoint while runs
/// start and finish on other threads (#8508). It used to enumerate the live dictionary twice (once for
/// the count, once to fill the array), so a run starting in between threw
/// <see cref="IndexOutOfRangeException"/> out of the endpoint. A snapshot must never throw and must be
/// ordered by start.
/// </summary>
public class ActivityTrackerInFlightConcurrencyTest
{
    [Fact]
    public void ReadingInFlight_WhileRunsStartAndFinish_NeverThrows_AndIsOrdered()
    {
        using var tracker = new ActivityTracker();
        var keep = Enumerable.Range(0, 64).Select(i => tracker.TrackRun($"seed-{i}", null)).ToList();
        var stop = 0;
        Exception? failure = null;
        var threads = Enumerable.Range(0, 4).Select(w => new Thread(() =>
        {
            for (var i = 0; i < 20_000 && Volatile.Read(ref stop) == 0; i++)
            {
                var a = tracker.TrackRun($"w{w}-{i}", null);
                var b = tracker.TrackRun($"w{w}-{i}b", null);
                a.Dispose();
                b.Dispose();
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        try
        {
            var reads = 0;
            while (threads.Any(t => t.IsAlive) && reads < 200_000)
            {
                var tickets = tracker.InFlight.Select(h => h.Ticket).ToList();
                for (var i = 1; i < tickets.Count; i++)
                    if (tickets[i] <= tickets[i - 1])
                        throw new InvalidOperationException("InFlight is not ordered by start");
                reads++;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            threads.ForEach(t => t.Join());
            keep.ForEach(h => h.Dispose());
        }
        Assert.Null(failure);
    }
}
