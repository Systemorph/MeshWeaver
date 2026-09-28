using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Mesh;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>The pre-flight watches answers, including successful answers represented by null.
/// A large bounded fan-out may take longer than one stage budget while still advancing.</summary>
public class DeletePreflightProgressTest
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    [Fact]
    public void HealthyAnswersKeepThePreflightAliveAcrossSeveralBudgets()
    {
        var scheduler = new TestScheduler();
        using var answers = new Subject<int?>();
        var received = false;
        int? verdict = -1;
        Exception? error = null;
        var completed = false;
        using var subscription = MeshExtensions.FinishDeletePreflight(
                answers, Budget, () => new TimeoutException("pre-flight stopped"), scheduler)
            .Subscribe(value => { received = true; verdict = value; }, ex => error = ex,
                () => completed = true);

        for (var i = 0; i < 3; i++)
        {
            scheduler.AdvanceBy(TimeSpan.FromSeconds(8).Ticks);
            answers.OnNext(null);
            Assert.Null(error);
            Assert.False(completed);
        }

        answers.OnCompleted();
        Assert.True(received);
        Assert.Null(verdict);
        Assert.Null(error);
        Assert.True(completed);
    }

    [Fact]
    public void ARealGapStillHitsTheSameStageBudget()
    {
        var scheduler = new TestScheduler();
        using var answers = new Subject<int?>();
        Exception? error = null;
        using var subscription = MeshExtensions.FinishDeletePreflight(
                answers, Budget, () => new TimeoutException("pre-flight stopped"), scheduler)
            .Subscribe(_ => { }, ex => error = ex);

        answers.OnNext(null);
        scheduler.AdvanceBy(Budget.Ticks);

        Assert.IsType<TimeoutException>(error);
        Assert.Equal("pre-flight stopped", error.Message);
    }
}
