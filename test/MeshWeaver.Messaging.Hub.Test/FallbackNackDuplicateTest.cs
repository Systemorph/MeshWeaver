using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 A hub carrying an <see cref="UnhandledMessageNack"/> policy (the fallback/overlay hub that
/// stands in for a node whose NodeType did not compile) answers an unregistered inbound type with
/// its OWN typed <see cref="DeliveryFailure"/> — and must answer EXACTLY ONCE.
///
/// <para><b>The defect this pins.</b> <c>MessageService.DeserializeDelivery</c> posted the typed
/// NACK (<see cref="ErrorType.CompilationFailed"/> + NodeTypePath) and then returned a plain
/// <c>delivery.Failed(reason)</c>, so the on-target caller ran <c>ReportFailure(delivery)</c> with
/// its default <see cref="ErrorType.Unknown"/> and posted a SECOND <see cref="DeliveryFailure"/> for
/// the same request — same message text, different classification, no NodeTypePath. Both are
/// <c>ResponseFor</c> the one request, so a caller's <c>Observe(...).FirstAsync()</c> resolved on
/// whichever won the race.</para>
///
/// <para>That flip is what made <c>OrleansBrokenNodeTypeAccessTest</c> (and its Monolith mirror
/// <c>BrokenNodeTypeAccessTest</c>) fail intermittently on CI shard 0 with
/// "Expected value to be CompilationFailed, but found Unknown" — an ASSERTION, not a timeout, with
/// byte-identical message text in the passing and failing runs.</para>
///
/// <para><b>Why it is written as a COUNT.</b> Asserting the winner's <see cref="ErrorType"/> only
/// re-tests the symptom and passes whenever the race happens to fall the right way — which is how
/// the Orleans test passed 10/10 locally while flapping on CI. Counting the answers pins the CAUSE,
/// so it fails deterministically with no load, no CPU constraint and no Orleans cluster, and it
/// keeps holding for the sites that still returned an unmarked <c>Failed</c> after the first fix
/// (the generic registry-hint path and the DeliveryFailure ping-pong guard).</para>
/// </summary>
public class FallbackNackDuplicateTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string NodeTypePath = "type/BrokenNodeType";
    private const string NackReason = "NodeType 'type/BrokenNodeType' has no usable hub configuration";

    /// <summary>A type name deliberately absent from every hub's TypeRegistry, so the delivery
    /// reaches the host as <see cref="RawJson"/> that cannot be materialised — the fallback path.</summary>
    private const string UnregisteredType = "TotallyUnregisteredProbeRequest";

    // Replay so an assertion that subscribes after the first NACK landed still sees it.
    private readonly ReplaySubject<DeliveryFailure> nacks = new();

    /// <summary>The host stands in for a broken NodeType's instance: it can serve what its own
    /// configuration handles, and answers everything else with the policy's typed diagnosis.</summary>
    protected override MessageHubConfiguration ConfigureHost(MessageHubConfiguration configuration)
        => configuration.Set(new UnhandledMessageNack(NackReason, ErrorType.CompilationFailed, NodeTypePath));

    /// <summary>The client collects every NACK it is sent, so the test can assert on the COUNT.</summary>
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithHandler<DeliveryFailure>((_, delivery) =>
        {
            nacks.OnNext(delivery.Message);
            return delivery.Processed();
        });

    [Fact(Timeout = 60_000)]
    public async Task UnregisteredType_OnFallbackHub_IsNackedExactlyOnce_WithTheFallbackClassification()
    {
        GetHost();
        var client = GetClient();

        client.Post(new RawJson($$"""{"$type":"{{UnregisteredType}}"}"""),
            o => o.WithTarget(CreateHostAddress()));

        var first = await nacks.Should().Within(20.Seconds()).Emit(
            "the fallback hub owes the sender a terminal answer — silence is the prod wedge this policy exists to prevent");

        first.ErrorType.Should().Be(ErrorType.CompilationFailed,
            "the classification must be the one the FAILING SITE decided on (it knows the NodeType did not compile), "
            + "never the generic Unknown a downstream reporter would invent");
        first.NodeTypePath.Should().Be(NodeTypePath,
            "the NACK must name the broken NodeType so the caller can act on it");

        // THE ROOT CAUSE: a second NACK for the same request. It is posted in the same delivery
        // pass as the first, so a short window is enough — and it is the duplicate, not the
        // ordering, that makes the observed ErrorType non-deterministic for the caller.
        await nacks.Skip(1).Should().NotEmit(3.Seconds(),
            "the fallback hub already answered this request; a second DeliveryFailure makes the "
            + "caller's ErrorType depend on which answer wins the race");
    }
}

/// <summary>
/// The same "one request, one failure answer" contract on the hub that has NO
/// <see cref="UnhandledMessageNack"/> policy — the ORDINARY case of a message whose type this hub's
/// TypeRegistry does not know.
///
/// <para>That path answers from inside <c>UnpackIfNecessary</c>
/// (<c>return ReportFailure(delivery.Failed(failureMessage))</c>) and then hands a still-Failed
/// delivery back to the on-target caller, which reported it a SECOND time. Both answers carry the
/// same <see cref="ErrorType.Unknown"/>, so the duplicate is invisible in any assertion about the
/// winner — it shows up only as a count, and only on this policy-free hub, which is why the
/// fallback-hub fix did not reach it. A duplicate NACK on the unregistered-type path is a traffic
/// multiplier on a storm-prone route, not a spare log line.</para>
/// </summary>
public class UnregisteredTypeNackDuplicateTest(ITestOutputHelper output) : HubTestBase(output)
{
    private const string UnregisteredType = "TotallyUnregisteredProbeRequest";

    private readonly ReplaySubject<DeliveryFailure> nacks = new();

    // NOTE: no UnhandledMessageNack policy here — that is the point of this fixture.
    protected override MessageHubConfiguration ConfigureClient(MessageHubConfiguration configuration)
        => configuration.WithHandler<DeliveryFailure>((_, delivery) =>
        {
            nacks.OnNext(delivery.Message);
            return delivery.Processed();
        });

    [Fact(Timeout = 60_000)]
    public async Task UnregisteredType_WithNoFallbackPolicy_IsNackedExactlyOnce()
    {
        GetHost();
        var client = GetClient();

        client.Post(new RawJson($$"""{"$type":"{{UnregisteredType}}"}"""),
            o => o.WithTarget(CreateHostAddress()));

        await nacks.Should().Within(20.Seconds()).Emit(
            "an unregistered inbound type must fail closed — the sender is owed a terminal answer");

        await nacks.Skip(1).Should().NotEmit(3.Seconds(),
            "the unpack step already answered this request; reporting the still-Failed delivery "
            + "again sends the sender a second DeliveryFailure for the one request");
    }
}
