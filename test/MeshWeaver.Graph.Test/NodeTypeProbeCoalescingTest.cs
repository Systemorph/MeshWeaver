using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Graph.Configuration;
using Xunit;
using Probe = MeshWeaver.Graph.Configuration.NodeTypeEnrichmentHelpers.NodeTypeProbeCache;
using Outcome = MeshWeaver.Graph.Configuration.NodeTypeEnrichmentHelpers.ProbeOutcome;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// One in-flight existence probe per NodeType, shared by every waiter (memex 2026-11-23: 700 probes
/// for one benchmark NodeType, each timing out, starving the silo thread pool).
/// </summary>
public class NodeTypeProbeCoalescingTest
{
    private static Probe NewCache() => Probe.For(new object());

    [Fact]
    public void ConcurrentEnrichmentsOfOneType_CostOneProbe()
    {
        var cache = NewCache();
        var gate = new Subject<(Outcome, string?)>();
        var probes = 0;
        var answers = new List<Outcome>();
        for (var i = 0; i < 700; i++)
            cache.GetOrProbe("Benchmarks/BenchmarkRun", () => { probes++; return gate; })
                .Subscribe(r => answers.Add(r.Outcome));

        gate.OnNext((Outcome.Registered, null));
        gate.OnCompleted();

        probes.Should().Be(1);
        answers.Should().HaveCount(700).And.OnlyContain(o => o == Outcome.Registered);
    }

    [Fact]
    public void DistinctTypes_EachProbeOnce()
    {
        var cache = NewCache();
        var probes = 0;
        foreach (var t in new[] { "A/X", "B/Y", "C/Z" })
            for (var i = 0; i < 5; i++)
                cache.GetOrProbe(t, () => { probes++; return Observable.Return((Outcome.Registered, (string?)null)); })
                    .Subscribe();
        probes.Should().Be(3);
    }

    [Fact]
    public void FaultedProbe_IsSharedNotFannedOut()
    {
        var cache = NewCache();
        var probes = 0;
        var answers = new List<Outcome>();
        for (var i = 0; i < 50; i++)
            cache.GetOrProbe("A/X", () => { probes++; return Observable.Return((Outcome.Indeterminate, (string?)null)); })
                .Subscribe(r => answers.Add(r.Outcome));
        probes.Should().Be(1, "a faulted probe is answered once for the whole burst");
        answers.Should().HaveCount(50).And.OnlyContain(o => o == Outcome.Indeterminate);
    }

    [Fact]
    public void RegisteredIsCached_ForTheLifeOfTheCache()
    {
        var cache = NewCache();
        var probes = 0;
        for (var i = 0; i < 3; i++)
            cache.GetOrProbe("A/X", () => { probes++; return Observable.Return((Outcome.Registered, (string?)null)); }).Subscribe();
        probes.Should().Be(1);
    }

    [Fact]
    public void NegativeAnswer_ExpiresAfterTtl_SoALaterRegistrationIsFound()
    {
        var cache = NewCache();
        var probes = 0;
        IObservable<(Outcome, string?)> P() { probes++; return Observable.Return((Outcome.Missing, (string?)null)); }
        cache.GetOrProbe("A/X", P).Subscribe();
        cache.GetOrProbe("A/X", P).Subscribe();
        probes.Should().Be(1);
        Thread.Sleep((int)Probe.NegativeTtlMs + 200);
        cache.GetOrProbe("A/X", P).Subscribe();
        probes.Should().Be(2);
    }
}
