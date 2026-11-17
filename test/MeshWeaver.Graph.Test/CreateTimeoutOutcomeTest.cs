using System;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Reactive.Testing;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// When the inner create of a create-or-update produces no verdict within
/// <c>MeshExtensions.InnerCreateVerdictBound</c>, the caller is refused with "the outcome is
/// unknown" — never "NOT applied", because the row may already be persisted.
///
/// <para>🚨 <b>The bound firing IS the behaviour under test, so it is driven on VIRTUAL time.</b>
/// The mesh registers a <see cref="CreateVerdictClock"/> backed by a virtual scheduler, and the test
/// advances it by exactly the production bound once the row is persisted and the reply is held. The
/// bound is never shortened, and nothing waits it out on the wall clock (it cost 30 s per CI run).</para>
/// </summary>
public class CreateTimeoutOutcomeTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private readonly HeldPostCreation handler = new();
    private readonly SerializedVirtualClock clock = new();

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder) =>
        base.ConfigureMesh(builder).ConfigureServices(s => s
            .AddSingleton<INodePostCreationHandler>(handler)
            .AddSingleton(new CreateVerdictClock(clock)));

    [Fact]
    [ExpectsTimeoutTerminal(TimeoutTerminalKinds.CreateVerdict,
        "the inner create's verdict bound, fired on the virtual CreateVerdictClock, answering 'outcome unknown' is the behaviour under test")]
    public async Task CreateTimeout_DoesNotClaimAnAlreadyPersistedNodeWasNotApplied()
    {
        var path = $"{TestPartition}/held-create-outcome";
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        try
        {
            var pending = access.RunAsSystem(() => ObserveNodeOperation(
                    new CreateOrUpdateNodeRequest(new MeshNode("held-create-outcome", TestPartition)
                    {
                        Name = "Persisted before the response", NodeType = "Markdown"
                    })))
                .FirstAsync().Await(TestContext.Current.CancellationToken);
            await handler.Entered.Should().Within(TestTimeouts.Convergence).Emit(
                "the post-creation handler holds the reply only after the row has been saved");
            var storage = Mesh.ServiceProvider.GetRequiredService<IStorageAdapter>();
            var persisted = await storage.Read(path, Mesh.JsonSerializerOptions)
                .FirstAsync().Await(TestContext.Current.CancellationToken);
            persisted.Should().NotBeNull();

            // Let the actual production verdict bound expire — on the mesh's virtual clock, by
            // exactly the production bound; never shortened for this test, never sat out.
            pending.IsCompleted.Should().BeFalse(
                "the reply is held and the virtual clock has not moved, so no verdict exists yet");
            clock.AdvanceBy(MeshExtensions.InnerCreateVerdictBound);
            // Quick (a third of the bound on a local run): a verdict bound that ignored the mesh's
            // clock and ran on the wall clock again could not answer inside it. The answer is posted
            // by the AdvanceBy above, so in practice it is already on its way.
            var answer = await pending.WaitAsync(TestTimeouts.Quick,
                TestContext.Current.CancellationToken);
            answer.Message.Success.Should().BeFalse();
            answer.Message.Error.Should().Contain("outcome is unknown");
            answer.Message.Error.Should().NotContain("NOT applied",
                "the row is already persisted and a missing reply cannot prove otherwise");
        }
        finally
        {
            handler.Release.OnNext(Unit.Default);
            handler.Release.OnCompleted();
        }
    }

    /// <summary>
    /// A <see cref="TestScheduler"/> made safe to share with a running mesh: every inner create in
    /// this mesh schedules its verdict bound here, from whichever thread handles it, while the test
    /// thread advances the clock — and a virtual-time scheduler's queue is not thread-safe on its
    /// own. Every call is serialised on one monitor; nothing here awaits, and the actions run under
    /// it only post (non-blocking), so it cannot park a hub.
    /// </summary>
    private sealed class SerializedVirtualClock : IScheduler
    {
        private readonly TestScheduler inner = new();
        private readonly object gate = new();

        public DateTimeOffset Now
        {
            get { lock (gate) return inner.Now; }
        }

        public void AdvanceBy(TimeSpan time)
        {
            lock (gate)
                inner.AdvanceBy(time.Ticks);
        }

        public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action)
        {
            lock (gate)
                return Guard(inner.Schedule(state, (_, s) => action(this, s)));
        }

        public IDisposable Schedule<TState>(TState state, TimeSpan dueTime,
            Func<IScheduler, TState, IDisposable> action)
        {
            lock (gate)
                return Guard(inner.Schedule(state, dueTime, (_, s) => action(this, s)));
        }

        public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime,
            Func<IScheduler, TState, IDisposable> action)
        {
            lock (gate)
                return Guard(inner.Schedule(state, dueTime, (_, s) => action(this, s)));
        }

        private IDisposable Guard(IDisposable scheduled)
            => System.Reactive.Disposables.Disposable.Create(() =>
            {
                lock (gate)
                    scheduled.Dispose();
            });
    }

    private sealed class HeldPostCreation : INodePostCreationHandler
    {
        public string NodeType => "Markdown";
        public AsyncSubject<Unit> Entered { get; } = new();
        public AsyncSubject<Unit> Release { get; } = new();

        public IObservable<Unit> Handle(MeshNode createdNode, string? createdBy)
        {
            if (createdNode.Id != "held-create-outcome")
                return Observable.Return(Unit.Default);
            Entered.OnNext(Unit.Default);
            Entered.OnCompleted();
            return Release;
        }
    }
}
