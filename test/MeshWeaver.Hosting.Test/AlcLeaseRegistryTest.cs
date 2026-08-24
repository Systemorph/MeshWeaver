using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Runtime.Loader;
using MeshWeaver.Mesh.Threading;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// The unload gate. The behaviour under test is not "does it unload" — it is <b>does it REFUSE to
/// unload</b> when something is still inside the context, because that refusal is what stands
/// between a retired <see cref="AssemblyLoadContext"/> and a native use-after-unload SIGSEGV
/// (exit=139 with no managed stack; see <see cref="AlcLeaseRegistry"/> for the core-dump analysis).
///
/// <para><c>AssemblyLoadContext</c> has no public "was I unloaded" flag, so every assertion here
/// rides its <c>Unloading</c> event: it fires when — and only when — <c>Unload()</c> is called.
/// That makes "we did not unload" directly observable instead of inferred.</para>
/// </summary>
public class AlcLeaseRegistryTest
{
    private static (AssemblyLoadContext Context, Func<bool> Unloaded) Collectible(string name)
    {
        var context = new AssemblyLoadContext(name, isCollectible: true);
        var unloaded = 0;
        context.Unloading += _ => Interlocked.Exchange(ref unloaded, 1);
        return (context, () => Volatile.Read(ref unloaded) == 1);
    }

    [Fact]
    public void A_context_nobody_has_entered_is_quiesced_immediately()
    {
        var registry = new AlcLeaseRegistry();
        var (context, _) = Collectible(nameof(A_context_nobody_has_entered_is_quiesced_immediately));

        registry.InFlight(context).Should().Be(0);
        registry.Quiesced(context).Timeout(TimeSpan.FromSeconds(5)).Wait();
    }

    [Fact]
    public void Leases_count_up_and_down_and_a_double_dispose_cannot_fake_a_quiesce()
    {
        var registry = new AlcLeaseRegistry();
        var (context, _) = Collectible(nameof(Leases_count_up_and_down_and_a_double_dispose_cannot_fake_a_quiesce));

        var first = registry.Enter(context);
        var second = registry.Enter(context);
        registry.InFlight(context).Should().Be(2);

        // Disposing the same lease twice must not decrement twice — that would report 0 while the
        // other caller is still inside, which is precisely the false "safe to unload".
        first.Dispose();
        first.Dispose();
        registry.InFlight(context).Should().Be(1);

        second.Dispose();
        registry.InFlight(context).Should().Be(0);
    }

    [Fact]
    public void A_LEASED_context_is_NOT_unloaded_when_the_budget_expires()
    {
        var registry = new AlcLeaseRegistry();
        var (context, unloaded) = Collectible(nameof(A_LEASED_context_is_NOT_unloaded_when_the_budget_expires));

        using var _ = registry.Enter(context);   // somebody is inside, and never leaves

        var result = registry.UnloadWhenQuiesced(context, TimeSpan.FromMilliseconds(200))
            .Timeout(TimeSpan.FromSeconds(10))
            .Wait();

        result.Should().BeFalse("a context that never went quiet must be REPORTED, not unloaded");
        unloaded().Should().BeFalse(
            "unloading a context somebody can still enter is the crash this type exists to prevent — "
            + "leaking it until process exit is the deliberate trade");
    }

    [Fact]
    public void The_unload_happens_once_the_last_lease_is_released()
    {
        var registry = new AlcLeaseRegistry();
        var (context, unloaded) = Collectible(nameof(The_unload_happens_once_the_last_lease_is_released));

        var lease = registry.Enter(context);
        var pending = registry.UnloadWhenQuiesced(context, TimeSpan.FromSeconds(30))
            .Timeout(TimeSpan.FromSeconds(20))
            .ToTask();

        // Still held: the unload must be waiting, not done.
        unloaded().Should().BeFalse();

        lease.Dispose();

        pending.GetAwaiter().GetResult().Should().BeTrue();
        unloaded().Should().BeTrue("releasing the last lease is the positive signal the gate waits for");
    }

    [Fact]
    public void A_lease_taken_while_the_unload_is_pending_still_holds_it_off()
    {
        var registry = new AlcLeaseRegistry();
        var (context, unloaded) = Collectible(nameof(A_lease_taken_while_the_unload_is_pending_still_holds_it_off));

        var first = registry.Enter(context);
        var pending = registry.UnloadWhenQuiesced(context, TimeSpan.FromMilliseconds(400)).ToTask();

        // Hand over: a second caller enters before the first leaves, so the count never reaches 0.
        var second = registry.Enter(context);
        first.Dispose();

        pending.GetAwaiter().GetResult().Should().BeFalse();
        unloaded().Should().BeFalse("the count never reached zero, so no quiescence signal was ever earned");

        second.Dispose();
    }

    [Fact]
    public void Leases_on_different_contexts_do_not_gate_each_other()
    {
        var registry = new AlcLeaseRegistry();
        var (busy, busyUnloaded) = Collectible("busy");
        var (idle, idleUnloaded) = Collectible("idle");

        using var _ = registry.Enter(busy);

        registry.UnloadWhenQuiesced(idle, TimeSpan.FromSeconds(10))
            .Timeout(TimeSpan.FromSeconds(15)).Wait().Should().BeTrue();
        idleUnloaded().Should().BeTrue();
        busyUnloaded().Should().BeFalse();
    }
}
