using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Graph.Security;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Graph.Test;

/// <summary>
/// Pins that <c>Email</c> — the content type of the built-in <c>Email</c> NodeType — is registered
/// in the STATIC registry (<c>WithGraphTypes</c>), so a CROSS-HUB write of an email node passes
/// <see cref="ContentDiscriminatorValidator"/>.
///
/// <para><b>The production failure this fixes (2026-08-12).</b> The Store contact form's submit
/// pipeline (a compiled plugin) queued its notification emails as ordinary mesh writes:
/// <c>Content = new Email {…}</c>, serialized across the hub boundary as JSON carrying
/// <c>$type: "Email"</c>. Every OTHER built-in content type (MarkdownContent, Comment,
/// Notification, the security types…) is registered in <c>WithGraphTypes</c>; <c>Email</c> alone
/// was not — so the validator's strict built-in branch correctly refused the write, and the form's
/// "Notifying our team" phase failed on every mesh. The omission was invisible for as long as only
/// IN-PROCESS writers (EmailInboundProcessor) created email nodes, because typed in-process content
/// bypasses the guard entirely. The contract this test pins: <b>every content type a built-in
/// NodeType declares via <c>WithContentType</c> must be in the static registry</b> — the
/// validator's strict branch assumes exactly that.</para>
/// </summary>
public class EmailContentDiscriminatorTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private ContentDiscriminatorValidator Guard =>
        Mesh.ServiceProvider.GetServices<INodeValidator>()
            .OfType<ContentDiscriminatorValidator>()
            .Single();

    private static MeshNode EmailNode(string discriminator) => new("mail-0", "Sales/inq-1/_Email")
    {
        NodeType = EmailNodeType.NodeType,
        Name = "Email to a@b.c",
        // The exact degraded shape a cross-hub write arrives in: a raw JsonElement whose $type
        // is whatever the WRITING hub's registry named the CLR type.
        Content = JsonSerializer.Deserialize<JsonElement>(
            $$"""
            {"$type":"{{discriminator}}","direction":"Outbound","status":"New",
             "to":"a@b.c","from":"c@d.e","subject":"s","body":"<p>b</p>"}
            """),
    };

    [Fact(Timeout = 30000)]
    public async Task ACrossHubEmailWrite_CarryingTheEmailDiscriminator_IsAccepted()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<ITypeRegistry>();
        registry.TryGetType(nameof(Email), out var definition).Should().BeTrue(
            "Email is the content type of a built-in NodeType, so it must be in the static "
            + "registry like every other built-in content type — its absence is what broke the "
            + "contact form's notification phase in production");
        definition!.Type.Should().Be(typeof(Email));

        var result = await Guard
            .Validate(new NodeValidationContext
            { Operation = NodeOperation.Create, Node = EmailNode(nameof(Email)) })
            .Should().Emit();

        result.IsValid.Should().BeTrue(
            "a cross-hub writer (a compiled plugin queueing a notification) serializes the typed "
            + $"Email as JSON with $type 'Email'; the guard must resolve it, got: {result.ErrorMessage}");
    }

    /// <summary>The guard itself stays armed: a discriminator that names NOTHING is still refused
    /// on the same node type — registering Email must not have widened the gate.</summary>
    [Fact(Timeout = 30000)]
    public async Task ABogusDiscriminatorOnAnEmailNode_StaysRefused()
    {
        var result = await Guard
            .Validate(new NodeValidationContext
            { Operation = NodeOperation.Create, Node = EmailNode("EmailContent") })
            .Should().Emit();

        result.IsValid.Should().BeFalse(
            "'EmailContent' is registered nowhere — the untypeable-blob guard must keep refusing it");
        result.ErrorMessage.Should().Contain("EmailContent");
    }
}
