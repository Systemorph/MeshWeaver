using System.Linq;
using System.Text.Json;
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
/// Pins <see cref="NodeTypeDeclarationSelfTypingValidator"/> — the write-boundary half of the fix
/// for #2160/#2161/#2162/#2245/#2358: a NodeType declaration (content IS a
/// <see cref="NodeTypeDefinition"/>) must never also claim, via its own
/// <see cref="MeshNode.NodeType"/>, to be an INSTANCE of a type.
///
/// <para>#2245 retyped the three known offenders (<c>User</c>, <c>VUser</c>, <c>Partition</c>) and
/// added a STATIC ratchet test (<c>NodeTypeDeclarationSelfTypingTest</c>) over every
/// statically-registered declaration. That ratchet cannot see a declaration created or patched at
/// RUNTIME — a repair path, a plugin-installed NodeType, a hand-authored MCP write. This validator
/// closes that gap at the one place every such write passes through: Create/Update validation.</para>
/// </summary>
public class NodeTypeDeclarationSelfTypingValidatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private NodeTypeDeclarationSelfTypingValidator Guard =>
        Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<NodeTypeDeclarationSelfTypingValidator>()
            .Single();

    private static MeshNode DeclarationNode(string nodeType, object? content) => new("Widget")
    {
        Name = "Widget",
        NodeType = nodeType,
        Content = content,
    };

    [Fact(Timeout = 30000)]
    public async Task ADeclaration_ClaimingToBeAnInstanceOfTheTypeItDeclares_IsRejected()
    {
        // The exact production shape: NodeType == the name the declaration itself introduces.
        var result = await Guard
            .Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                Node = DeclarationNode("Widget", new NodeTypeDefinition()),
            })
            .Should().Emit();

        result.IsValid.Should().BeFalse(
            "content is a NodeTypeDefinition — this node DECLARES 'Widget' — so it must not also "
            + "claim (via MeshNode.NodeType) to BE an instance of 'Widget', or anything else");
        result.ErrorMessage.Should().Contain("Widget");
    }

    /// <summary>
    /// 🚨 A declaration naming an UNRELATED type must be ACCEPTED. This test asserted the opposite
    /// until CI proved that wrong: the broad rule refused a shape MeshWeaver.Plugins actually ships
    /// — a package ROOT that is a <c>Space</c> whose content also happens to be a
    /// <c>NodeTypeDefinition</c> (the UWDeepfield shape) — which made the whole package
    /// un-installable. <c>NodeRepoInstanceOrderingTest</c> pins that install and went red.
    ///
    /// <para>The harm being guarded is a declaration polluting the instance query for the type IT
    /// DECLARES: the <c>User</c> declaration answering <c>nodeType:User</c> beside real accounts
    /// (355k+ production occurrences). A <c>Space</c> root answering <c>nodeType:Space</c> is
    /// simply a Space, correctly returned — no collision to prevent.</para>
    ///
    /// <para>The residual is acknowledged in the validator's own docs and deliberately NOT closed
    /// here: a <c>ContentAs&lt;Space&gt;()</c> on such a root degrades to null. Fixing that means
    /// changing the shipped package first, not refusing the write while the content still ships.</para>
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ADeclaration_NamingAnUnrelatedType_IsAccepted()
    {
        var result = await Guard
            .Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                // The UWDeepfield shape: a Space root whose content is a NodeTypeDefinition.
                Node = DeclarationNode("Space", new NodeTypeDefinition()),
            })
            .Should().Emit();

        result.IsValid.Should().BeTrue(
            "a declaration naming an UNRELATED type does not enrol itself in its own instance "
            + "query, and refusing it makes the shipped UWDeepfield package un-installable, "
            + $"got: {result.ErrorMessage}");
    }

    /// <summary>
    /// The self-typing check must match on the declaration's PATH too, not only its id — an
    /// instance references an in-package type by path (<c>nodeType:"Pack/Widget"</c>), so a
    /// declaration at that path naming it is the same self-enrolment as the root-level case.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ADeclaration_SelfTypedByItsFullPath_IsRejected()
    {
        var result = await Guard
            .Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                Node = new MeshNode("Widget", "Pack")
                {
                    Name = "Widget",
                    NodeType = "Pack/Widget",
                    Content = new NodeTypeDefinition(),
                },
            })
            .Should().Emit();

        result.IsValid.Should().BeFalse(
            "instances reference this type as 'Pack/Widget', so the declaration naming that path "
            + "puts itself in its own instance query — the id-only check would miss it");
    }

    [Fact(Timeout = 30000)]
    public async Task ADeclaration_TypedAsNodeTypePath_IsAccepted()
    {
        var result = await Guard
            .Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                Node = DeclarationNode(MeshNode.NodeTypePath, new NodeTypeDefinition()),
            })
            .Should().Emit();

        result.IsValid.Should().BeTrue(
            $"NodeType == '{MeshNode.NodeTypePath}' is exactly how a declaration should say what "
            + $"it is, got: {result.ErrorMessage}");
    }

    [Fact(Timeout = 30000)]
    public async Task ADeclaration_WithNoNodeTypeSet_IsAccepted()
    {
        var result = await Guard
            .Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                Node = DeclarationNode(null!, new NodeTypeDefinition()),
            })
            .Should().Emit();

        result.IsValid.Should().BeTrue("an unset NodeType is also legal for a declaration");
    }

    /// <summary>
    /// The degraded shape a cross-hub write can arrive in — a raw <see cref="JsonElement"/> whose
    /// <c>$type</c> is whatever the WRITING hub's registry named the CLR type. Same probe shape as
    /// <c>ContentDiscriminatorValidator</c>'s own <c>$type</c> read.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ACrossHubWrite_CarryingTheDegradedNodeTypeDefinitionShape_IsAlsoRejected()
    {
        var content = JsonSerializer.Deserialize<JsonElement>(
            """{"$type":"NodeTypeDefinition","description":"a widget type"}""");

        var result = await Guard
            .Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                Node = DeclarationNode("Widget", content),
            })
            .Should().Emit();

        result.IsValid.Should().BeFalse(
            "the degraded JsonElement shape must be recognised as declaration content too — a "
            + "cross-hub writer must not be able to smuggle the collision past the guard");
    }

    /// <summary>
    /// Hand-authored JSON (an MCP write, in particular) is not guaranteed to use the framework's
    /// own "Namespace.Type" $type convention — it may carry the full CLR AssemblyQualifiedName
    /// shape instead ("Namespace.Type, AssemblyName, Version=…"). Flagged in review (#2378): a
    /// naive EndsWith(".NodeTypeDefinition") check fails on this shape, since it ends with
    /// ", AssemblyName" — which would let the guard fail OPEN for exactly this kind of write.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ACrossHubWrite_CarryingTheAssemblyQualifiedDiscriminatorShape_IsAlsoRejected()
    {
        var content = JsonSerializer.Deserialize<JsonElement>(
            """
            {"$type":"MeshWeaver.Graph.Configuration.NodeTypeDefinition, MeshWeaver.Graph, Version=1.0.0.0",
             "description":"a widget type"}
            """);

        var result = await Guard
            .Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                Node = DeclarationNode("Widget", content),
            })
            .Should().Emit();

        result.IsValid.Should().BeFalse(
            "an assembly-qualified $type discriminator must be recognised as declaration content "
            + "too, or a hand-authored/MCP write in this shape slips the guard entirely");
    }

    /// <summary>The guard must not widen into rejecting ordinary instances of ordinary types.</summary>
    [Fact(Timeout = 30000)]
    public async Task AnOrdinaryInstanceWrite_IsUnaffected()
    {
        var result = await Guard
            .Validate(new NodeValidationContext
            {
                Operation = NodeOperation.Create,
                // Content is NOT a NodeTypeDefinition — an ordinary instance of "Widget".
                Node = DeclarationNode("Widget", new { name = "a widget" }),
            })
            .Should().Emit();

        result.IsValid.Should().BeTrue(
            $"content is not a NodeTypeDefinition, so this is an ordinary instance write and must "
            + $"pass, got: {result.ErrorMessage}");
    }

    /// <summary>
    /// End-to-end: the validator is actually WIRED into the mesh's Create/Update pipeline (not just
    /// unit-callable), so a genuine self-typed declaration write is refused by the running mesh —
    /// not merely by calling the class directly.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task TheGuard_IsWiredIntoTheLiveCreatePipeline()
    {
        var validators = Mesh.ServiceProvider.GetServices<INodeValidator>().ToList();
        validators.OfType<NodeTypeDeclarationSelfTypingValidator>().Should().ContainSingle(
            "AddGraph() must register the guard, or a runtime self-typed declaration write "
            + "reaches the mesh unchecked");
    }
}
