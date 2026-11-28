using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MeshWeaver.Data.Serialization;
using MeshWeaver.Fixture;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Data.Test;

/// <summary>
/// <c>SynchronizationStream.Reduce</c> dispatches the typed reducer by reflection, which wraps
/// whatever it throws in a <see cref="TargetInvocationException"/>. That wrapper hid a
/// <see cref="HubDisposingException"/> (a teardown) from every classifier and logged it as an
/// unclassified defect. The real cause must surface unwrapped, with its own stack.
/// </summary>
public class AReflectiveReduceThrowsTheRealCauseTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record Empty;

    [HubFact]
    public async Task ReduceOnADisposingHost_ThrowsTheRealCause_NotTheReflectionWrapper()
    {
        var host = GetHost();
        // Arranged on the LIVE host: the stream, and a reducer registered for the reference.
        var reduceManager = new ReduceManager<Empty>(host)
            .AddWorkspaceReference<EntityReference, object>((current, _, _) =>
                new ChangeItem<object>(new object(), current.StreamId, current.Version));
        var stream = new SynchronizationStream<Empty>(
            new StreamIdentity(host.Address, null), host, new EntityReference("X", "Y"), reduceManager, null);

        host.Dispose();
        await host.DisposalCompleted
            .Catch<Unit, Exception>(_ => Observable.Return(Unit.Default))
            .FirstOrDefaultAsync()
            .Timeout(10.Seconds())
            .Await(TestContext.Current.CancellationToken);

        Action act = () => stream.Reduce<object>(new EntityReference("X", "Z"));

        var ex = act.Should().Throw<Exception>().Which;
        ex.Should().NotBeOfType<TargetInvocationException>("the reflection wrapper carries no information");
        (ex is ObjectDisposedException).Should().BeTrue(
            "the real cause is the disposing host refusing the reduced stream (a HubDisposingException, or the "
            + "disposed container it resolves from) — both are ObjectDisposedException, and both must arrive unwrapped");
        ex.StackTrace.Should().Contain("ReduceManager",
            "the inner exception's own stack is preserved, not replaced by the rethrow site");
    }
}
