using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reactive.Disposables;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Blazor;
using MeshWeaver.Blazor.Components;
using MeshWeaver.Layout;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MeshWeaver.Hosting.Blazor.Test;

/// <summary>
/// #1308 — a view's subscription accumulator must tolerate a registration that arrives WHILE the
/// view is being disposed, because in this framework one routinely does.
///
/// <para><b>The production failure.</b> <c>MeshSearchView.Dispose()</c> threw
/// <c>InvalidOperationException: Collection was modified; enumeration operation may not execute</c>
/// out of the renderer's disposal queue, which fails the whole Blazor circuit (the user's
/// connection drops). The accumulator was a plain <c>List&lt;IDisposable&gt;</c> and the writer was
/// not the renderer: <c>ApplyResults</c> is called straight from the result stream's <c>OnNext</c>,
/// on a hub/pool thread, and it calls <c>ResolveDeletePermissions</c>, which registers a permission
/// probe. Circuit teardown and a query emission are independent events, so nothing ordered them.</para>
///
/// <para><b>Both halves are the same defect.</b> Throwing is the loud face. The quiet face is a
/// registration that lands just after the drain and is retained by a list nobody will ever read
/// again — an unowned subscription outliving its owner, which is how a dead circuit keeps a live
/// mesh subscription. A fix that only stopped the throw would leave that in place, so both are
/// pinned below.</para>
///
/// <para><b>Not a lock.</b> Idempotent, concurrency-safe disposal here is a property of the
/// accumulator (<see cref="CompositeDisposable"/>), not mutual exclusion bolted around it.</para>
/// </summary>
public class ViewDisposalConcurrencyTest
{
    /// <summary>
    /// Drives <see cref="BlazorView{TViewModel,TView}"/>'s REAL disposal path without a renderer —
    /// this test is about accumulator behaviour, not markup. Reuses the shape
    /// <c>MeshNodePickerDebounceOwnershipTest</c> established.
    /// </summary>
    private sealed class Probe : MeshNodePickerView
    {
        [SetsRequiredMembers]
        public Probe()
        {
            ViewModel = new MeshNodePickerControl(new object());
            Logger = NullLogger<MeshNodePickerView>.Instance;
        }

        /// <summary>Registers a component-lifetime disposable, exactly as component code does.</summary>
        public void Register(IDisposable d) => Disposables.Add(d);

        /// <summary>The live accumulator contents.</summary>
        public IEnumerable<IDisposable> Owned => Disposables;
    }

    /// <summary>
    /// The deterministic repro. A disposable that registers another one as it is being disposed
    /// reproduces the exact failing operation — the accumulator is mutated while it is being
    /// drained — with no thread scheduling to sample. Against the <c>List&lt;IDisposable&gt;</c>
    /// this replaces, the enumerator's version check throws precisely the production exception.
    /// </summary>
    [Fact]
    public async Task RegisteringWhileDisposalDrains_DoesNotThrow()
    {
        var probe = new Probe();
        var lateRegistered = new BooleanDisposable();

        probe.Register(Disposable.Create(() => probe.Register(lateRegistered)));
        probe.Register(new BooleanDisposable());

        var dispose = async () => await probe.DisposeAsync();

        await dispose.Should().NotThrowAsync(
            "a registration arriving during the drain is the normal case for a view whose "
            + "subscriptions emit off the renderer — it must not fail the circuit");
    }

    /// <summary>
    /// The quiet half: after teardown the accumulator is TERMINAL, so a registration that loses the
    /// race is disposed on arrival instead of being parked forever. Fails against the predecessor,
    /// which appended to a list that <c>DisposeAsync</c> had already walked past and cleared.
    /// </summary>
    [Fact]
    public async Task RegisteringAfterDisposal_DisposesImmediately_RatherThanLeaking()
    {
        var probe = new Probe();
        await probe.DisposeAsync();

        var late = new BooleanDisposable();
        probe.Register(late);

        late.IsDisposed.Should().BeTrue(
            "the view is gone; a subscription registered after teardown has no owner left to "
            + "release it, so the accumulator must release it on arrival");
        probe.Owned.Should().BeEmpty("a disposed accumulator must not retain anything");
    }

    /// <summary>Disposal is idempotent — the renderer may drain a component more than once.</summary>
    [Fact]
    public async Task DisposalIsIdempotent()
    {
        var probe = new Probe();
        var tracked = new BooleanDisposable();
        probe.Register(tracked);

        await probe.DisposeAsync();
        var second = async () => await probe.DisposeAsync();

        await second.Should().NotThrowAsync();
        tracked.IsDisposed.Should().BeTrue();
    }

    /// <summary>
    /// The genuine cross-thread shape, as a regression guard: writers hammering the accumulator
    /// while the renderer thread disposes it. This cannot PROVE the defect (a race can always be
    /// missed), which is why the deterministic repro above carries that burden — but it is the
    /// only assertion that exercises the actual concurrency, and it must never throw.
    /// </summary>
    [Fact]
    public async Task ConcurrentRegistrationDuringDisposal_NeverThrows()
    {
        var probe = new Probe();
        for (var i = 0; i < 200; i++)
            probe.Register(new BooleanDisposable());

        using var start = new ManualResetEventSlim(false);
        Exception? writerFault = null;

        var writer = Task.Run(() =>
        {
            start.Wait(TimeSpan.FromSeconds(10));
            try
            {
                for (var i = 0; i < 2_000; i++)
                    probe.Register(new BooleanDisposable());
            }
            catch (Exception ex)
            {
                writerFault = ex;
            }
        });

        var disposer = Task.Run(async () =>
        {
            start.Wait(TimeSpan.FromSeconds(10));
            await probe.DisposeAsync();
        });

        start.Set();
        await Task.WhenAll(writer, disposer).WaitAsync(TimeSpan.FromSeconds(30));

        writerFault.Should().BeNull("registering a subscription must never fault, disposal or not");
        probe.Owned.Should().BeEmpty(
            "every registration either made it into the drain or was disposed on arrival — "
            + "nothing may be left holding a mesh subscription for a dead view");
    }

    /// <summary>
    /// A type-level guard over the whole component surface. The reported crash was in
    /// <c>MeshSearchView</c>, but the shape — a bare <c>List&lt;IDisposable&gt;</c> accumulating
    /// subscriptions that a stream callback can append to — was copied across several views and
    /// into the base class. A per-site fix would leave the next copy to be found in production, so
    /// the pattern itself is what is pinned here.
    /// </summary>
    [Fact]
    public void NoComponentAccumulatesDisposablesInABareList()
    {
        var offenders = typeof(BlazorView<,>).Assembly
            .GetTypes()
            .SelectMany(t => t.GetFields(
                BindingFlags.Instance | BindingFlags.Public
                | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            .Where(f => IsBareDisposableList(f.FieldType))
            .Select(f => $"{f.DeclaringType!.FullName}.{f.Name}")
            .OrderBy(n => n)
            .ToArray();

        offenders.Should().BeEmpty(
            "a subscription accumulator must be a CompositeDisposable: its Add is thread-safe "
            + "against a concurrent drain, and an Add after disposal releases its argument "
            + "instead of stranding it. A List<IDisposable> gives neither, and both faces of "
            + "that showed up in production as #1308");
    }

    private static bool IsBareDisposableList(Type t) =>
        t.IsGenericType
        && (t.GetGenericTypeDefinition() == typeof(List<>)
            || t.GetGenericTypeDefinition() == typeof(System.Collections.Immutable.ImmutableList<>))
        && t.GetGenericArguments()[0] == typeof(IDisposable);
}
