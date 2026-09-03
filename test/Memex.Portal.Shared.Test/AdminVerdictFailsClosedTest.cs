using System;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Memex.Portal.Shared.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 THE PLATFORM-ADMIN VERDICT FAILS CLOSED.
///
/// <para>The token mint now carries the server's <c>hub.IsGlobalAdmin()</c> answer so a browser
/// client can decide whether to issue the platform notification leg at all — today
/// <c>clients/portal-next</c> omits it, which is why no React viewer has ever seen a platform
/// notification (Systemorph/MeshWeaver.Plugins#1295).</para>
///
/// <para><b>The asymmetry is the whole design.</b> An admin who briefly sees no platform
/// notifications is a nuisance; a non-admin who sees them is a disclosure. So every way of NOT
/// knowing — a fault, an empty source — answers <c>false</c>.</para>
///
/// <para>🚨 <b>The control arm is not optional here.</b> A reduction that answered <c>false</c>
/// unconditionally would pass every fault case and be catastrophically wrong: no admin would ever
/// get the platform leg. The `true` row is what makes the others mean anything.</para>
/// </summary>
public class AdminVerdictFailsClosedTest
{
    private static Task<bool> Resolve(IObservable<bool> source) =>
        AdminVerdict.FailClosed(source, "rbuergi", NullLogger.Instance).FirstAsync().ToTask();

    /// <summary>THE CONTROL ARM — a real admin still gets true, or the fail-closed rows prove nothing.</summary>
    [Fact]
    public async Task AnAdmin_IsAnsweredTrue()
        => Assert.True(await Resolve(Observable.Return(true)));

    [Fact]
    public async Task ANonAdmin_IsAnsweredFalse()
        => Assert.False(await Resolve(Observable.Return(false)));

    /// <summary>A FAULTED lookup is "we do not know", which must read as not-admin.</summary>
    [Fact]
    public async Task AFaultedLookup_IsAnsweredFalse()
        => Assert.False(await Resolve(
            Observable.Throw<bool>(new InvalidOperationException("permission evaluator unavailable"))));

    /// <summary>
    /// A source that completes WITHOUT emitting is the same answer as one that faulted — and
    /// getting this wrong is worse than a wrong verdict: without the empty guard the mint's
    /// SelectMany would drop the response entirely, so the token request would never return.
    /// </summary>
    [Fact]
    public async Task AnEmptyLookup_IsAnsweredFalse_RatherThanNeverAnswering()
        => Assert.False(await Resolve(Observable.Empty<bool>()));

    /// <summary>Only the FIRST answer counts — a stream that later flips to true must not
    /// retroactively grant the platform view for a token already minted.</summary>
    [Fact]
    public async Task OnlyTheFirstAnswerCounts()
        => Assert.False(await Resolve(new[] { false, true }.ToObservable()));

    /// <summary>The response record's own default is not-admin: an unset field is never a grant.</summary>
    [Fact]
    public void TheResponseDefaultsToNotAdmin()
        => Assert.False(new CreateTokenResponse().IsGlobalAdmin);
}
