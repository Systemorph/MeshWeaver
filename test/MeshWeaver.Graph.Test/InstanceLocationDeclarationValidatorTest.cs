using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins <see cref="InstanceLocationDeclarationValidator"/> — the authoring gate of #3039: a NodeType
/// declaration may not say where its instances live when the type is one the permission fold
/// enumerates mesh-wide, and the refusal names the type and the reason. The set it refuses IS
/// <see cref="NeverNarrowedNodeTypes"/>, the one the storage planner refuses at query time; there
/// is deliberately no second list to keep in step.
/// </summary>
public class InstanceLocationDeclarationValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private InstanceLocationDeclarationValidator Guard =>
        Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<InstanceLocationDeclarationValidator>()
            .Single();

    private IMeshService MeshService => Mesh.ServiceProvider.GetRequiredService<IMeshService>();

    private static MeshNode Declaring(string id, string? ns, params string[] locations) => new(id, ns)
    {
        Name = id,
        NodeType = MeshNode.NodeTypePath,
        State = MeshNodeState.Active,
        Content = new NodeTypeDefinition { InstanceLocations = locations.Length == 0 ? null : locations },
    };

    [Theory]
    [InlineData("Role")]
    [InlineData("GroupMembership")]
    [InlineData("AccessAssignment")]
    [InlineData("PartitionAccessPolicy")]
    public async Task ADeclarationForAFoldType_IsRefused_WithTheReasonNamed(string foldType)
    {
        var result = await Guard
            .Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                Node = Declaring(foldType, null, "namespace:Admin"),
            })
            .Should().Emit();

        result.IsValid.Should().BeFalse(
            $"'{foldType}' is read mesh-wide by the permission fold; a short read there is a vanished grant or an open deny");
        result.Reason.Should().Be(NodeRejectionReason.ValidationFailed);
        result.ErrorMessage.Should().Contain($"'{foldType}'")
            .And.Contain("fold")
            .And.Contain("UnanchoredSecurityReads");
    }

    [Fact]
    public void TheRefusedSet_IsTheFoldsOwnFourTypes_AndNothingElseByDefault()
    {
        NeverNarrowedNodeTypes.Names.Should().BeEquivalentTo(
            new[] { "Role", "GroupMembership", "AccessAssignment", "PartitionAccessPolicy" },
            System.Text.Json.JsonSerializerOptions.Default);
        NeverNarrowedNodeTypes.Refuses("Markdown").Should().BeFalse();
        NeverNarrowedNodeTypes.Refuses("role").Should().BeTrue("node types compare case-insensitively");
    }

    /// <summary>
    /// Gates are declared per mesh (<c>ConfigureNodeTypeAccess</c>), so they reach the predicate as a
    /// set; a gated type is refused with the gate named as the reason.
    /// </summary>
    [Fact]
    public void ADeclarationForATypeDeclaredGate_IsRefused_NamingTheGate()
    {
        var gated = ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "Store/Plugin");

        var refusal = InstanceLocationDeclarationValidator.Refusal(
            Declaring("Plugin", "Store", "namespace:Store"), ["namespace:Store"], gated);

        refusal.Should().NotBeNull().And.Contain("'Store/Plugin'").And.Contain("gate");
    }

    [Fact]
    public async Task AFoldType_DeclaringNothing_IsAccepted()
    {
        var result = await Guard
            .Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Update,
                Node = Declaring("Role", null),
            })
            .Should().Emit();

        result.IsValid.Should().BeTrue("the gate is on the DECLARATION, not on the type");
    }

    [Fact]
    public async Task AnOrdinaryType_Declaring_IsAccepted()
    {
        var result = await Guard
            .Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                Node = Declaring("Widget", TestPartition, "namespace:Admin/Menu", "namespace:A|B|C"),
            })
            .Should().Emit();

        result.IsValid.Should().BeTrue("an ordinary type owns its declaration; over-statement is safe, the planner intersects");
    }

    /// <summary>
    /// End to end through the write pipeline: the create is refused, the message names the type, and
    /// nothing lands. The id branch of the predicate: <c>TestData/Role</c> shadows the built-in by id.
    /// </summary>
    [Fact(Timeout = 120000)]
    public async Task TheWritePipeline_RefusesTheDeclaration_AndNothingLands()
    {
        var node = Declaring("Role", TestPartition, "namespace:Admin");

        var failure = await Record.ExceptionAsync(() =>
            MeshService.CreateNode(node).Take(1).Timeout(60.Seconds()).Await());

        failure.Should().NotBeNull("a ValidationFailed result must surface on the IMeshService create");
        failure!.Message.Should().Contain("'Role'").And.Contain("UnanchoredSecurityReads");
    }
}
