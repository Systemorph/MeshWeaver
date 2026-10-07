using System;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Hosting.Test;

/// <summary>
/// 🚨 A content-type registration probe that THROWS is a failure of this replica, never a fact
/// about the type. <c>ContentTypeRegistration.ProbeRegister</c> used to catch the exception at
/// Debug, so <see cref="DynamicContentTypeRegistrar"/> then found nothing in the registry and filed
/// the type under <see cref="ContentTypeRegistrationStatus.DeclaresNoContentType"/>. That bucket is
/// the one the registration summary does not name, so every node of that type read as untyped on
/// this replica and no log line in production said why.
/// </summary>
public class AFaultedRegistrationProbeIsNotADeclarationTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Fact]
    public void AProbeThatThrows_IsFaulted_AndNamesTheCause()
    {
        const string path = "Probe/ThrowingType";
        var result = new NodeCompilationResult(
            AssemblyLocation: "/loaded/ThrowingType.dll",
            NodeTypeConfigurations:
            [
                new NodeTypeConfiguration
                {
                    NodeType = path,
                    DataType = typeof(object),
                    HubConfiguration = _ => throw new InvalidOperationException("configuration could not be built"),
                },
            ]);

        var outcome = DynamicContentTypeRegistrar.Register(
            Mesh, Mesh.ServiceProvider.GetRequiredService<IMeshContentTypeRegistry>(), path, result, logger: null);

        outcome.Status.Should().Be(ContentTypeRegistrationStatus.Faulted,
            "a probe that threw decided nothing about the type — DeclaresNoContentType would be a wrong "
            + "verdict, filed where the summary never names it");
        outcome.Detail.Should().Contain("configuration could not be built");
    }
}
