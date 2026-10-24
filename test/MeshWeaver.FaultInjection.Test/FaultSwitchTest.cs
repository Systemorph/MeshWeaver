using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Testing.FaultInjection;
using Xunit;

namespace MeshWeaver.FaultInjection.Test;

/// <summary>
/// The limited <see cref="FaultSwitch"/> under its two races: a test resuming on the arrival must read
/// the spent fault as open, and a caller outrun between reservation and recording must still be
/// counted. No mesh, no clock — the arrival callback and the reservation seam fix the interleaving.
/// </summary>
public class FaultSwitchTest
{
    /// <summary>
    /// A one-shot fault's arrival is the signal a test resumes on; at that moment the switch must
    /// already read open. Negative control: publishing the arrival before the limit counts as spent
    /// (the switch released only after <c>NoteArrival</c>) makes the callback read it closed.
    /// </summary>
    [Fact]
    public void AOneShotFault_ReadsOpen_WhenItsArrivalIsPublished()
    {
        var fault = new FaultSwitch("fail one write", limit: 1);
        var closedAtArrival = (bool?)null;
        using var subscription = fault.Arrivals.Subscribe(_ => closedAtArrival = fault.IsClosed);

        fault.TryApply("write").Should().BeTrue("the one application is granted");

        closedAtArrival.Should().Be(false, "the arrival is published only after the one application spent the fault");
    }

    /// <summary>
    /// With <c>limit: 2</c>, caller A reserves application 1 and is outrun: caller B reserves, records
    /// and would release before A records. Both were faulted, so both must be counted and the switch
    /// must release only after A's record. Negative control: releasing on the LAST RESERVATION drops
    /// A's arrival — <c>ArrivalCount</c> reads 1.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public async Task AnOutrunApplication_IsStillCounted_AndTheSwitchReleasesAfterIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var fault = new FaultSwitch("fail two writes", limit: 2);
        var arrivals = ImmutableList<string>.Empty;
        using var subscription = fault.Arrivals.Subscribe(a => arrivals = arrivals.Add(a));
        var raced = 0;
        var bGranted = false;
        fault.AfterReservation = () =>
        {
            if (Interlocked.Exchange(ref raced, 1) == 0)
                bGranted = fault.TryApply("B");
        };

        fault.TryApply("A").Should().BeTrue("A reserved the first application");

        bGranted.Should().BeTrue("B reserved the second application inside A's window");
        fault.ArrivalCount.Should().Be(2, "both operations were faulted");
        string.Join(",", arrivals).Should().Be("B,A", "both arrivals are published, B first");
        fault.IsClosed.Should().BeFalse("the limit is spent");
        await fault.Released.Should().Within(System.TimeSpan.FromSeconds(5))
            .Emit("the switch released after the last record", cancellationToken: ct);
        fault.TryApply("C").Should().BeFalse("a spent fault applies no more");
    }
}
