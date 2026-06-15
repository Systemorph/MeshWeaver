using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Xunit;

namespace MeshWeaver.Graph.Test;

public class UserNodeTypePermissionTest
{
    private static MessageHubConfiguration BuildUserHubConfig()
    {
        var config = new MessageHubConfiguration(null, new Address("User", "Alice"));
        var meshNode = UserNodeType.CreateMeshNode();
        Assert.NotNull(meshNode.HubConfiguration);
        return meshNode.HubConfiguration(config);
    }

    private static NodeValidationContext ReadContext(string id, string ns) =>
        new()
        {
            Operation = NodeOperation.Read,
            Node = new MeshNode(id, ns)
        };

    private static NodeValidationContext UpdateContext(string id, string ns) =>
        new()
        {
            Operation = NodeOperation.Update,
            Node = new MeshNode(id, ns)
        };

    #region Hub Permission Rules

    [Fact]
    public void AuthenticatedUser_HasHubReadPermission()
    {
        var config = BuildUserHubConfig();
        var ruleSet = config.Get<HubPermissionRuleSet>();
        ruleSet.Should().NotBeNull("UserNodeType should register hub permission rules");

        ruleSet!.HasPermission(Permission.Read, null!, "alice")
            .Should().BeTrue("any authenticated user should have Read permission");
    }

    [Fact]
    public void UnauthenticatedUser_DeniedHubReadPermission()
    {
        var config = BuildUserHubConfig();
        var ruleSet = config.Get<HubPermissionRuleSet>();
        ruleSet.Should().NotBeNull();

        ruleSet!.HasPermission(Permission.Read, null!, null)
            .Should().BeFalse("unauthenticated (null userId) should be denied");

        ruleSet.HasPermission(Permission.Read, null!, "")
            .Should().BeFalse("unauthenticated (empty userId) should be denied");
    }

    #endregion

    #region Node Access Rules (via NodeAccessRuleSet)

    [Fact]
    public async Task AuthenticatedUser_CanReadDirectUserNode()
    {
        var config = BuildUserHubConfig();
        var ruleSet = config.Get<NodeAccessRuleSet>();
        ruleSet.Should().NotBeNull("UserNodeType should register node access rules");

        var accessRule = ruleSet!.ToAccessRule(UserNodeType.NodeType);
        // Path = "User/Alice" (derived from ns/id)
        var context = ReadContext("Alice", "User");

        await accessRule.HasAccess(context, "bob")
            .Should().Be(true, "any authenticated user can read a direct User node");
    }

    [Fact]
    public async Task UnauthenticatedUser_CannotReadDirectUserNode()
    {
        var config = BuildUserHubConfig();
        var ruleSet = config.Get<NodeAccessRuleSet>();
        var accessRule = ruleSet!.ToAccessRule(UserNodeType.NodeType);

        var context = ReadContext("Alice", "User");

        await accessRule.HasAccess(context, null)
            .Should().Be(false, "unauthenticated user (null) should be denied");

        await accessRule.HasAccess(context, "")
            .Should().Be(false, "unauthenticated user (empty) should be denied");
    }

    [Fact]
    public async Task AuthenticatedUser_CannotReadChildNode()
    {
        var config = BuildUserHubConfig();
        var ruleSet = config.Get<NodeAccessRuleSet>();
        var accessRule = ruleSet!.ToAccessRule(UserNodeType.NodeType);

        // Path = "User/Alice/thread1" (child node)
        var context = ReadContext("thread1", "User/Alice");

        await accessRule.HasAccess(context, "bob")
            .Should().Be(false, "child nodes (threads, activities) should not be publicly readable");
    }

    [Fact]
    public async Task UserCanEditOwnNode()
    {
        var config = BuildUserHubConfig();
        var ruleSet = config.Get<NodeAccessRuleSet>();
        var accessRule = ruleSet!.ToAccessRule(UserNodeType.NodeType);

        var context = UpdateContext("Alice", "User");

        await accessRule.HasAccess(context, "Alice")
            .Should().Be(true, "users should be able to edit their own node");
    }

    [Fact]
    public async Task UserCannotEditOtherUserNode()
    {
        var config = BuildUserHubConfig();
        var ruleSet = config.Get<NodeAccessRuleSet>();
        var accessRule = ruleSet!.ToAccessRule(UserNodeType.NodeType);

        var context = UpdateContext("Alice", "User");

        await accessRule.HasAccess(context, "Bob")
            .Should().Be(false, "users should not be able to edit other users' nodes");
    }

    #endregion
}
