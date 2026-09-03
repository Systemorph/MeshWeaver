using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Fixture;
using MeshWeaver.Mesh.Security;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// 🚨 <b>#3148 — a request outstanding when its hub goes down is a TEARDOWN, and it has to say so
/// in its TYPE.</b>
///
/// <para><c>MessageHub.CancelCallbacks</c> errors every pending response subject at disposal. It
/// used to raise a bare <see cref="ObjectDisposedException"/>, so
/// <see cref="HubDisposingException.IsHubDisposal"/> answered <c>false</c> for it and no caller
/// could tell "the hub went away underneath me" from a real defect. Every other teardown shape in
/// the framework carries that fact in its type precisely so each layer classifies it identically
/// instead of re-deriving it from a string.</para>
///
/// <para>The visible cost was small and the shape is not: a detached, fire-and-forget
/// activity-tracking write logged a full <c>fail</c>-level stack for an ordinary pod recycle, which
/// reads as a defect and is not one — the request cannot be answered, there is nothing to fix, and
/// the entry is lost either way.</para>
///
/// <para>🚨 <b>The message is asserted UNCHANGED.</b> Several classifiers match teardown on message
/// text (<c>AreaErrorClassifier.IsTransientHubFailure</c>,
/// <c>MeshNodeStreamCache.IsTransientOwnerFailure</c>) and log fingerprints group incidents by it,
/// so adding the type had to be additive. A future edit that "tidies" the wording would silently
/// re-classify live incidents — this test is what stops it.</para>
/// </summary>
public class HubDisposedBeforeResponseIsClassifiedTest(ITestOutputHelper output) : HubTestBase(output)
{
    private record NeverAnswered : IRequest<NeverAnsweredResponse>;

    private record NeverAnsweredResponse;

    /// <summary>
    /// A request that is accepted and deliberately never answered, so it is still outstanding when
    /// the hub is disposed — the exact state <c>CancelCallbacks</c> exists to terminate.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task ARequestOutstandingAtDisposal_FailsAsATypedTeardown()
    {
        var host = GetHost();
        var hub = host.GetHostedHub(
            new Address("disposed-before-response", "1"),
            c => c.WithHandler<NeverAnswered>((_, delivery) => delivery.Processed()));

        Exception? observed = null;
        // 🚨 An identity is required or PostPipeline refuses the post outright (fail-closed, by
        // design) — and the request would then never be OUTSTANDING, which is the state this test
        // is about. The scope is opened and closed synchronously around Subscribe: impersonation is
        // an AsyncLocal, so it must not be disposed on whichever thread the sequence terminates on.
        var access = hub.ServiceProvider.GetRequiredService<AccessService>();
        IDisposable sub;
        using (access.ImpersonateAsHub(hub))
            sub = hub.Observe<NeverAnsweredResponse>(
                    new NeverAnswered(), o => o.WithTarget(hub.Address))
                .Subscribe(_ => { }, ex => observed = ex);
        using var _sub = sub;

        hub.Dispose();
        await hub.DisposalCompleted.FirstOrDefaultAsync().Await().WaitAsync(TimeSpan.FromSeconds(120));

        Assert.NotNull(observed);

        Assert.True(
            HubDisposedBeforeResponseException.IsHubDisposedBeforeResponse(observed),
            "a caller must be able to classify 'the issuing hub was disposed with my request "
            + "outstanding' from the exception itself; while it was an untyped "
            + "ObjectDisposedException the only way to tell it from a real fault was to match the "
            + "message text");

        // Still an ObjectDisposedException, so every existing catch keeps working.
        Assert.IsAssignableFrom<ObjectDisposedException>(observed);

        // 🚨 The wording is contract — see the class remarks.
        Assert.Contains("was disposed before the response arrived", observed!.Message);
        Assert.Contains(nameof(NeverAnswered), observed.Message);
    }
}
