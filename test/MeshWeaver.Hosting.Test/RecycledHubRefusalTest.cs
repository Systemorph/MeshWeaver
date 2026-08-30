using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 <b>A delivery that reaches a RECYCLED hub must be refused as TRANSIENT, so the caller
/// re-probes and lands on the fresh activation.</b> Issue #2727.
///
/// <para>Core#2438 made a hub's lifetime scope genuinely close. A delivery routed to an address
/// whose hub was recycled reaches <see cref="AccessControlPipeline"/> holding the OLD hub, so the
/// gate's first <c>GetRequiredService</c> throws <see cref="ObjectDisposedException"/> from the
/// closed scope. That was answered <c>Unavailable</c> with a sentence carrying NONE of the markers
/// the transient classifiers match — so the caller took a corpse's answer as final and never
/// re-probed: <c>RecycleSurvivesItsOwnDisposeTest</c> reads null for an address that is alive one
/// activation later, <c>SilentReadNackTest</c> gets a non-NACK, a render wedges. Before #2438 the
/// scope never actually closed and the same ordering resolved from a zombie scope, so the bug was
/// always there — the leak fix only exposed it.</para>
///
/// <para>These are the two halves of the fix, and both are contract: WHICH failures count as
/// "this hub is gone", and the WORDING the refusal carries — because the mesh classifies delivery
/// failures by message text, and a casual reword silently restores #2727 (nothing fails to
/// compile; the caller merely stops retrying).</para>
/// </summary>
public class RecycledHubRefusalTest : HubTestBase
{
    private readonly ITestOutputHelper output;

    public RecycledHubRefusalTest(ITestOutputHelper output) : base(output) => this.output = output;

    private static ObjectDisposedException DisposedScope() =>
        new("LifetimeScope",
            "Instances cannot be resolved and nested lifetimes cannot be created from this "
            + "LifetimeScope as it (or one of its parent scopes) has already been disposed.");

    /// <summary>
    /// The refusal must be recognised as TRANSIENT by the classifier the READ path consults —
    /// which is the entire point of the change: <c>MeshNodeStreamCache</c> rides a transient owner
    /// failure out and re-probes, and treats anything else as terminal.
    /// </summary>
    [Fact]
    public void TheRefusal_IsClassifiedTransient_SoTheCallerReProbes()
    {
        var refusal = AccessControlPipeline.RecyclingRefusal(
            new Address("TestData", "recycle-survivor"), "GetDataRequest", DisposedScope());

        output.WriteLine(refusal);

        MeshNodeStreamCache.IsTransientOwnerFailure(new InvalidOperationException(refusal))
            .Should().BeTrue(
                "a recycled address is coming BACK — the read path must ride this out and re-probe "
                + "the fresh activation. The pre-#2727 sentence ('Permission check unavailable … "
                + "the access gate could not run') matched no marker, so the caller stopped there.");

        MeshNodeStreamCache.IsMissingNodeFailure(new InvalidOperationException(refusal))
            .Should().BeFalse(
                "a recycling hub is NOT a provable absence — classifying it as one would poison "
                + "existence checks and the negative cache for a node that exists (#667)");
    }

    /// <summary>
    /// #2673: an UNDETERMINED permission outcome on a hub that is going away must be answered in
    /// the recycling vocabulary too, not as "we could not check".
    ///
    /// <para>The fold cannot reach a verdict while the owner is recycled, and the honest-sounding
    /// <c>Unavailable</c> answer carries no marker any transient classifier matches — so
    /// <c>GetMeshNode</c> took it as TERMINAL and resolved <b>null</b> for a node that exists at an
    /// address that reactivates (measured: "recovered in 62 ms: (null)"). The reason string is the
    /// only evidence available on that path, hence the string overload.</para>
    /// </summary>
    [Fact]
    public void AnUndeterminedCheck_OnAGoneHub_IsClassifiedTransientToo()
    {
        var refusal = AccessControlPipeline.RecyclingRefusal(
            new Address("TestData", "reprobe-recovers"), "GetDataRequest",
            "the permission query could not run");

        MeshNodeStreamCache.IsTransientOwnerFailure(new InvalidOperationException(refusal))
            .Should().BeTrue("otherwise GetMeshNode resolves null for a node that EXISTS — #2673");

        AccessControlPipeline.IsHubGone(GetHost(),
                "Instances cannot be resolved and nested lifetimes cannot be created from this "
                + "LifetimeScope as it (or one of its parent scopes) has already been disposed.")
            .Should().BeTrue("a disposed scope named in the REASON is the same fact as one thrown");
        AccessControlPipeline.IsHubGone(GetHost(), "the query returned no rows")
            .Should().BeFalse("an ordinary undetermined outcome on a LIVE hub keeps its honest "
                + "'we could not check' answer — calling that a recycle would make a real gate "
                + "failure retry forever");
    }

    /// <summary>
    /// The discrimination. A disposed SCOPE means the hub is gone; an unrelated
    /// <see cref="ObjectDisposedException"/> from something a handler touched on a LIVE hub is a
    /// real fault and must keep its honest "the gate could not run" classification — over-claiming
    /// here would convert genuine faults into silent retries.
    /// </summary>
    [Fact]
    public async Task OnlyAGoneHub_Qualifies_NotEveryObjectDisposedException()
    {
        var live = GetHost();

        AccessControlPipeline.IsHubGone(live, DisposedScope())
            .Should().BeTrue("the closed lifetime scope IS the evidence the hub is gone — after a "
                + "recycle the hub is no longer 'shutting down', it has finished, so this is all "
                + "that is left to recognise it by");

        AccessControlPipeline.IsHubGone(live, new ObjectDisposedException("SomeStream"))
            .Should().BeFalse("an ObjectDisposedException a handler caused on a LIVE hub is a real "
                + "fault; answering it 'retry, I am recycling' would hide it forever");

        AccessControlPipeline.IsHubGone(live, new InvalidOperationException("boom"))
            .Should().BeFalse("an ordinary gate fault stays UNAVAILABLE — honest about having "
                + "reached no verdict, and not retried as a recycle");

        live.Dispose();
        // Awaited DIRECTLY on the observable — no `.ToTask()` bridge anywhere, tests included
        // (maintainer, 2026-08-30). The Timeout keeps a hung disposal a failure, not a hang.
        await live.DisposalCompleted.FirstOrDefaultAsync().Timeout(TimeSpan.FromSeconds(30));
        AccessControlPipeline.IsHubGone(live, new InvalidOperationException("boom"))
            .Should().BeTrue("a hub that is shutting down qualifies whatever the failure was — its "
                + "services are going away underneath every check");

    }
}
