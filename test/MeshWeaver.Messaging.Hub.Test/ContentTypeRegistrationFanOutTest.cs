using System;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Services;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// Pins the cost of one content-type REGISTRATION against the armed late-retype waits
/// (Systemorph/MeshWeaver#5555 — the ~246k queued work items / 2→10 GiB surge).
///
/// <para>Every per-node hub activation of a runtime-compiled NodeType calls
/// <c>MeshDataSource.WithContentType</c>, which registers its content type here. Every node stream
/// whose content arrived untyped keeps a wait on <see cref="IMeshContentTypeRegistry.Registrations"/>
/// for as long as it stays degraded. Two defects multiplied those two populations:</para>
/// <list type="number">
/// <item>the registry hopped with <c>ObserveOn(TaskPoolScheduler.Default)</c>, which Rx serves with
/// <c>ObserveOnObserverLongRunning</c> — one DEDICATED thread per subscription, parked in
/// <c>Monitor.Wait</c> for the subscription's life and woken by every registration;</item>
/// <item>the waiter's filter ran AFTER that hop, so every registration in the mesh was dispatched to
/// every waiter, related or not — waits × activations, during a mass activation both large.</item>
/// </list>
/// </summary>
public class ContentTypeRegistrationFanOutTest
{
    private const int Waiters = 300;
    private const int UnrelatedRegistrations = 25;

    private static Type EmitType(string assemblyName, string typeName)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(
            new AssemblyName(assemblyName), AssemblyBuilderAccess.RunAndCollect);
        var module = assembly.DefineDynamicModule(assemblyName);
        return module.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class).CreateType();
    }

    /// <summary>
    /// An unrelated registration must be DROPPED on the registering thread: every waiter's
    /// predicate has run by the time <see cref="IMeshContentTypeRegistry.Register"/> returns, and
    /// nothing was dispatched anywhere. Before the fix the predicate ran after the hop, so at that
    /// instant it had run zero times on the registering thread and was still owed
    /// <c>Waiters × UnrelatedRegistrations</c> off-thread dispatches.
    /// </summary>
    [Fact]
    public void AnUnrelatedRegistration_CostsTheWaitersNothingButAPredicate()
    {
        TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
        var registry = new MeshContentTypeRegistry();
        var registeringThread = Environment.CurrentManagedThreadId;
        var onRegisteringThread = 0;
        var offRegisteringThread = 0;
        var delivered = 0;

        using var waits = new CompositeDisposable(Enumerable.Range(0, Waiters).Select(i =>
            registry
                .RegistrationsMatching(r =>
                {
                    if (Environment.CurrentManagedThreadId == registeringThread)
                        Interlocked.Increment(ref onRegisteringThread);
                    else
                        Interlocked.Increment(ref offRegisteringThread);
                    return string.Equals(r.NodeTypePath, $"Waited/Type{i}", StringComparison.OrdinalIgnoreCase);
                })
                .Subscribe(_ => Interlocked.Increment(ref delivered))));

        for (var r = 0; r < UnrelatedRegistrations; r++)
            registry.Register(EmitType($"FanOut_Unrelated{r}", $"Unrelated{r}"), $"Unrelated/Type{r}");

        Volatile.Read(ref onRegisteringThread).Should().Be(Waiters * UnrelatedRegistrations,
            because: "every waiter judges every registration where it is announced — a string compare on " +
                     "the registering thread — so the whole fan-out is settled when Register returns");
        Volatile.Read(ref offRegisteringThread).Should().Be(0,
            because: "an unrelated registration must not be shipped to a waiter just to be discarded there; " +
                     "that dispatch, once per waiter per activation, is the #5555 fan-out");
        Volatile.Read(ref delivered).Should().Be(0, because: "none of the registrations was the waited type");
    }

    /// <summary>
    /// A delivery is a task-pool work item, never a dedicated thread. Rx's
    /// <c>TaskPoolScheduler.Default</c> advertises long-running support, and <c>ObserveOn</c> then
    /// starts a raw thread per subscription that never returns until the subscription is disposed.
    /// The waits here are exactly the long-lived, numerous subscriptions that makes ruinous.
    /// </summary>
    [Fact]
    public async Task ADelivery_RunsOnThePool_NeverOnAThreadOfItsOwn()
    {
        var registry = new MeshContentTypeRegistry();
        var waited = EmitType("FanOut_Waited", "Waited");
        var deliveries = new ReplaySubject<bool>();

        using var waits = new CompositeDisposable(Enumerable.Range(0, Waiters).Select(_ =>
            registry
                .RegistrationsMatching(r => r.ContentType == waited)
                .Subscribe(_ => deliveries.OnNext(Thread.CurrentThread.IsThreadPoolThread))));

        registry.Register(waited, "Waited/Type");

        var onPool = await deliveries
            .Take(Waiters)
            .Aggregate(ImmutableList<bool>.Empty, (acc, x) => acc.Add(x))
            .Should().Within(TestTimeouts.Convergence).Emit(cancellationToken: TestContext.Current.CancellationToken);

        onPool.Count(x => !x).Should().Be(0,
            because: "a delivery must be an ordinary pool work item; a dedicated thread per waiter is a " +
                     "parked OS thread per degraded node stream, woken by every registration in the mesh");
    }

    /// <summary>The waited type is still delivered — off the registering thread (the registry's
    /// contract: <c>Register</c> runs inside a hub configuration build) and exactly once per
    /// registration.</summary>
    [Fact]
    public async Task TheWaitedType_IsStillDelivered_OffTheRegisteringThread()
    {
        var registry = new MeshContentTypeRegistry();
        var waited = EmitType("FanOut_Waited2", "Waited2");
        var registeringThread = Environment.CurrentManagedThreadId;

        var delivery = registry
            .RegistrationsMatching(r => string.Equals(r.NodeTypePath, "Waited/Type2", StringComparison.OrdinalIgnoreCase))
            .Select(r => (r.ContentType, Thread: Environment.CurrentManagedThreadId))
            .Replay(1);
        using var connection = delivery.Connect();

        registry.Register(EmitType("FanOut_Other", "Other"), "Other/Type");
        registry.Register(waited, "Waited/Type2");

        var (contentType, thread) = await delivery.Should().Within(TestTimeouts.Convergence)
            .Emit(cancellationToken: TestContext.Current.CancellationToken);
        contentType.Should().BeSameAs(waited);
        thread.Should().NotBe(registeringThread,
            because: "a waiter re-types a node and emits into whatever is bound to it; doing that on the " +
                     "registering thread would re-enter the hub construction that is registering the type");
    }
}
