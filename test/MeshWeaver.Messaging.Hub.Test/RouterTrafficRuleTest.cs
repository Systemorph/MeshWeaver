#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The detector must fire on exactly the traffic that means "the router is doing work" — no more
/// (a false ERROR trains people to mute it) and no less (a missed one is how the wedge stayed
/// invisible).
///
/// <para>The first argument is the DELIVERY's target — the address it is addressed to — never the
/// hub currently handling it. <see cref="RouterTrafficDetectorTest"/> pins that the caller actually
/// passes it that; here the contract itself is spelled out, because reading the parameter as "the
/// receiving hub" is what turned every routing hop into a false ERROR.</para>
/// </summary>
public class RouterTrafficRuleTest
{
    private const string Mesh = "mesh";

    [Theory]
    [InlineData(Mesh, "portal", "target")]
    [InlineData("portal", Mesh, "sender")]
    [InlineData(Mesh, Mesh, "sender AND target")]
    public void RouterAsAnEnd_IsReported(string target, string sender, string expected) =>
        Assert.Equal(expected, RouterTrafficRule.RoleOf(target, sender, new object()));

    [Theory]
    [InlineData("portal", "client")]
    [InlineData("import", "mesh-ish")]   // near-miss must NOT match
    [InlineData("client", null)]
    public void TrafficBetweenOtherHubs_IsSilent(string target, string? sender) =>
        Assert.Null(RouterTrafficRule.RoleOf(target, sender, new object()));

    /// <summary>
    /// The HOP, stated at the predicate: a delivery from a client to <c>ApiToken/x</c> passes THROUGH
    /// the mesh hub — every hosted hub's non-local delivery is routed up via
    /// <c>parentHub.DeliverMessage</c> — but the router is an end of it in neither direction. The
    /// rule can only say so if it is asked about the DELIVERY's ends; ask it about the receiving hub
    /// and this same traffic answers "target".
    /// </summary>
    [Fact]
    public void ADeliveryOnlyRoutedThroughTheRouter_IsSilent()
    {
        Assert.Null(RouterTrafficRule.RoleOf("ApiToken", "client", new object()));
        // …and the mistake it must not be confused with: asking about the mesh hub that is merely
        // forwarding it reports a violation that is not there.
        Assert.Equal("target", RouterTrafficRule.RoleOf(Mesh, "client", new object()));
    }

    [Fact]
    public void HeartbeatsAreTheRoutersOwnJob_AndNeverReported()
    {
        Assert.Null(RouterTrafficRule.RoleOf(Mesh, "portal", new HeartBeatEvent()));
        Assert.Null(RouterTrafficRule.RoleOf(Mesh, Mesh, new HeartBeatEvent()));
    }

    [Fact]
    public void MatchIsExact_NotCaseInsensitiveOrPrefixed()
    {
        Assert.Null(RouterTrafficRule.RoleOf("Mesh", "portal", new object()));
        Assert.Null(RouterTrafficRule.RoleOf("meshx", "portal", new object()));
    }
}
