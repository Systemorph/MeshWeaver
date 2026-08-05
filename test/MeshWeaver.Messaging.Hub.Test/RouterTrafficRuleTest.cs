#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member

using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Messaging.Hub.Test;

/// <summary>
/// The detector must fire on exactly the traffic that means "the router is doing work" — no more
/// (a false ERROR trains people to mute it) and no less (a missed one is how the wedge stayed
/// invisible).
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
