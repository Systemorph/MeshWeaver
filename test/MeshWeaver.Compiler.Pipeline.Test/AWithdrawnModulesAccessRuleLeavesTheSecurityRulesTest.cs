using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// <b>A withdrawn module's security rule leaves the rule set — it never faults every write.</b> A module
/// registers an <see cref="INodeTypeAccessRule"/>, a platform interface, so the root holds a forwarding
/// proxy for it. When the module is WITHDRAWN at boot (a singleton of it cannot be built), that proxy stays
/// in the root's <c>IEnumerable&lt;INodeTypeAccessRule&gt;</c>, and building the security rules
/// (<see cref="NodeTypeAccessRuleSet"/>) read its node type through it and threw
/// <c>Module MeshWeaver.Test.BootBuilt holds no service scope in this mesh.</c>. Every write's
/// <c>RlsNodeValidator</c> takes that set, so every node write in the mesh — each package install writes
/// the package's nodes — failed while one unrelated module was withdrawn.
/// </summary>
public sealed class AWithdrawnModulesAccessRuleLeavesTheSecurityRulesTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => true;

    /// <inheritdoc />
    protected override bool RegistersAccessRule => true;

    [Fact(Timeout = 120_000)]
    public async Task TheRuleIsAbsent_AndAWriteSucceeds()
    {
        Contexts.Current(Module).Should().BeNull("the precondition: the module was withdrawn at boot");
        Incompatible.Should().ContainSingle("the withdrawal is reported where every module that failed to install is");

        var built = Record.Exception(() => Mesh.ServiceProvider.GetRequiredService<NodeTypeAccessRuleSet>());
        built.Should().BeNull("a withdrawn module's forwarder leaves the security rules; it never faults building them");
        Mesh.ServiceProvider.GetRequiredService<NodeTypeAccessRuleSet>()
            .Find(GuardedNodeType, NodeOperation.Read)
            .Should().BeNull("the withdrawn module's rule governs nothing — its type falls back to the standard check");

        // The write a package install makes: a node created through the mesh, past RlsNodeValidator.
        await NodeFactory.CreateNode(new MeshNode("after-withdrawal", "WithdrawnRuleProbe")
            {
                Name = "written while a module is withdrawn",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
            .Should().Within(60.Seconds())
            .Emit("a node write succeeds while an unrelated module is withdrawn",
                cancellationToken: TestContext.Current.CancellationToken);
    }
}

/// <summary>The negative control: the same module, built cleanly, contributes its rule and it governs its type.</summary>
public sealed class APublishedModulesAccessRuleGovernsItsTypeTest(ITestOutputHelper output)
    : ModuleSingletonsBuiltAtBootTestBase(output)
{
    /// <inheritdoc />
    protected override bool Broken => false;

    /// <inheritdoc />
    protected override bool RegistersAccessRule => true;

    [Fact(Timeout = 120_000)]
    public async Task TheRuleGovernsItsType_AndAWriteSucceeds()
    {
        Contexts.Current(Module).Should().NotBeNull();
        var rule = Mesh.ServiceProvider.GetRequiredService<NodeTypeAccessRuleSet>()
            .Find(GuardedNodeType, NodeOperation.Read);
        rule.Should().NotBeNull("a published module's rule is a member of the security rules — so the "
                                + "withdrawn twin's absence is the withdrawal, not a rule never registered");
        rule!.NodeType.Should().Be(GuardedNodeType, "the forwarder answers from the module's current generation");

        await NodeFactory.CreateNode(new MeshNode("published", "PublishedRuleProbe")
            {
                Name = "written while the module is published",
                NodeType = "Markdown",
                State = MeshNodeState.Active,
            })
            .Should().Within(60.Seconds())
            .Emit("a node write succeeds",
                cancellationToken: TestContext.Current.CancellationToken);
    }
}
