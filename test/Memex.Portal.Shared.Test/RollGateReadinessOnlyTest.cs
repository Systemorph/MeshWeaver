using Memex.Portal.ServiceDefaults;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// 🚨 <b>A roll gate can NEVER fail the startup probe</b> (policies <c>bake-gate-readiness-only</c>,
/// MeshWeaver#5544, and <c>required-modules-readiness-only</c>) — the platform-side pin.
///
/// <para>The facts that drive the REAL probe composition on the paths the CHART ships moved to
/// MeshWeaver.Plugins (src/Memex.Hosts.Test/Chart/), with the deployment chart it reads — the platform repository carries no deployment knowledge. What stays is the one fact that reads no chart.</para>
/// </summary>
public class RollGateReadinessOnlyTest
{
    /// <summary>
    /// Pins the CORE side only: the roll-gate allow-list names the literal the Plugins host
    /// registers today (<c>AddCheck&lt;RequiredModulesHealthCheck&gt;("required_modules")</c> in
    /// Memex.Portal.Distributed). It cannot see that host — if the host renames its registration,
    /// this stays green while <c>TagRollGates</c> stops matching. Closing that needs the host to
    /// register under <see cref="ProbeEndpoints.RequiredModulesCheckName"/>.
    /// </summary>
    [Fact]
    public void TheRollGateAllowList_NamesTheLiteralThePluginsHostRegistersToday() =>
        Assert.Equal("required_modules", ProbeEndpoints.RequiredModulesCheckName);
}
