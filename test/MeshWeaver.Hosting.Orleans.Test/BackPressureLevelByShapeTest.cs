using Microsoft.Extensions.Logging;
using Xunit;

namespace MeshWeaver.Hosting.Orleans.Test;

/// <summary>
/// A gauge crossing is not an incident — and reporting one as <see cref="LogLevel.Critical"/> is
/// what flooded triage.
///
/// <para>The red-log ticketing path files an incident per Critical fingerprint. The routing
/// back-pressure line was unconditionally Critical, while its own text said <i>"0 means nothing is
/// waiting on anything, so read it as load"</i>. Measured 2026-09-21 over the crossings carried in
/// this site's open incidents: <b>17 with deepest = 0</b> (nothing blocked) against <b>4 with a real
/// head-of-line queue</b>. Four in five tickets reported that nothing was stuck.</para>
///
/// <para>This pins the discriminator, not the prose: the shape the snapshot can actually observe
/// decides the level.</para>
/// </summary>
public class BackPressureLevelByShapeTest
{
    /// <summary>
    /// 🚨 The load shape. Nothing waits on anything, so it must NOT open a ticket — but it must
    /// still be logged, which is why this asserts Warning rather than "not Critical".
    /// </summary>
    [Fact]
    public void NothingBlocked_IsLoad_AndDoesNotFileAnIncident()
        => Assert.Equal(LogLevel.Warning, RoutingGrain.SaturationLevel(0));

    /// <summary>
    /// 🚨 The actionable shape, and the one that must NOT be softened: a deepest of 1 already means
    /// a leg is waiting on a LEG — head-of-line blocking on one channel — which is the whole reason
    /// this report exists. Boundary included deliberately; 1 is the edge that decides.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(62)]
    public void ALegBlockedBehindALeg_StaysCritical(int deepest)
        => Assert.Equal(LogLevel.Critical, RoutingGrain.SaturationLevel(deepest));

    /// <summary>
    /// A negative value cannot occur (the snapshot counts queued legs), but a guard that silently
    /// promoted an impossible input to Critical would re-open the flood through the back door.
    /// </summary>
    [Fact]
    public void AnImpossibleNegativeDepth_IsNotTreatedAsBlocked()
        => Assert.Equal(LogLevel.Warning, RoutingGrain.SaturationLevel(-1));
}
