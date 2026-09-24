using MeshWeaver.Messaging;
using Orleans.Serialization.Cloning;

namespace MeshWeaver.Connection.Orleans;

/// <summary>
/// 🚨 Orleans' deep copier for the mesh's delivery envelope: it hands the SAME instance across a
/// same-silo grain call instead of round-tripping it through JSON — issue #4824.
///
/// <para><b>What it replaces.</b> A grain call between two activations on one silo does not
/// serialise its arguments; Orleans DEEP-COPIES them (and the call's result) so caller and callee
/// never share state. The mesh registered <c>JsonCodec</c> as the copier for every type, so every
/// local <c>IRoutingGrain.RouteMessage</c>, <c>IMessageHubGrain.DeliverMessage</c> and
/// <c>IPodHubGrain.Deliver</c> copied its delivery by serialising it to JSON and parsing it back —
/// the whole payload, twice per hop (arguments, then result). That copy is where the production
/// out-of-memory stacks sit: <c>PooledResponseCopier → JsonCodec.DeepCopy</c> (#3045) and
/// <c>ObjectPolymorphicConverter.Write</c> under the Orleans deep-copy (#4824). #3045 stripped the
/// echoed body from the RESULT; the ARGUMENT copy stayed, and it is not needed at all.</para>
///
/// <para><b>Why sharing is sound.</b> A copy exists to isolate mutable state, and nothing here is
/// mutable: <c>MessageDelivery&lt;T&gt;</c> is a record whose every member is <c>init</c>-only, its
/// <c>Properties</c> an <c>ImmutableDictionary</c> and its <c>RoutingPath</c> an
/// <c>ImmutableList</c>; every change is a <c>with</c> that makes a new instance. The payload that
/// crosses a grain boundary is <c>RawJson</c> — <c>OrleansRoutingService</c> is reached through
/// <c>delivery.Package(…)</c>, which serialises the message into an immutable string first. And a
/// non-Orleans mesh already passes the very same instances between hubs in one process, so a
/// same-silo grain call now behaves exactly as every other in-process hop does. Cross-silo calls
/// are untouched: they SERIALISE, which is a different path and still needs <c>JsonCodec</c>.</para>
/// </summary>
internal sealed class MessageDeliveryCopier : IGeneralizedCopier
{
    /// <summary>Whether <paramref name="type"/> is a delivery envelope this copier owns — also the
    /// predicate that takes those types away from <c>JsonCodec</c>'s copying, so the two can never
    /// both claim one type.</summary>
    internal static bool Covers(Type type) => typeof(IMessageDelivery).IsAssignableFrom(type);

    /// <inheritdoc />
    public bool IsSupportedType(Type type) => Covers(type);

    /// <summary>Returns <paramref name="input"/> itself — see the class remarks for why an
    /// immutable envelope needs no copy.</summary>
    public object? DeepCopy(object? input, CopyContext context) => input;
}
