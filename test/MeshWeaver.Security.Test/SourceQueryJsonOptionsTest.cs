using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Domain;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting;
using MeshWeaver.Hosting.Monolith;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Hosting.Security;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using MeshWeaver.Messaging.Serialization;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace MeshWeaver.Security.Test;

/// <summary>
/// Regression test for the "query for sources is broken" / "⚠ Compilation failed"
/// failure mode in Memex Portal Distributed (PROD): when a Code MeshNode's
/// content survives a JSON serialize/deserialize round-trip on a hub whose
/// <see cref="ITypeRegistry"/> doesn't have <see cref="CodeConfiguration"/>
/// registered, <see cref="ObjectPolymorphicConverter"/> falls back to returning
/// the content as <c>JsonElement</c>. The compile-side discovery in
/// <c>MeshNodeCompilationService.CompileCore</c> filters by
/// <c>n.Content is CodeConfiguration cf</c> — that <c>is</c> check fails for
/// <c>JsonElement</c>, the source is silently skipped, Roslyn produces no
/// assembly, and the activation overlays the generic
/// <c>"⚠ Compilation failed"</c> error message.
///
/// <para>The repro is JSON-shape only — no Postgres needed. We construct the
/// JSON the way a stored Code MeshNode looks, deserialize through a
/// <see cref="JsonSerializerOptions"/> equivalent to the running hub's, and
/// assert that the type registry's coverage actually resolves
/// <c>CodeConfiguration</c> instead of <c>JsonElement</c>.</para>
/// </summary>
public class SourceQueryJsonOptionsTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    private CancellationToken TestTimeout =>
        new CancellationTokenSource(10.Seconds()).Token;

    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => ConfigureMeshBase(builder).AddRowLevelSecurity();

    protected override Task SetupAccessRightsAsync() => Task.CompletedTask;

    /// <summary>
    /// Baseline: <see cref="IMessageHub.JsonSerializerOptions"/> on the
    /// mesh hub must know <see cref="CodeConfiguration"/> as a polymorphic
    /// type. Without this registration every Postgres-stored Code row
    /// deserializes to <c>JsonElement</c> on the read path and the compile
    /// service skips it.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public void MeshHubOptions_KnowCodeConfiguration()
    {
        var registry = Mesh.ServiceProvider.GetRequiredService<ITypeRegistry>();
        registry.TryGetType(nameof(CodeConfiguration), out var typeInfo)
            .Should().BeTrue(
                "the mesh hub's TypeRegistry must register CodeConfiguration so " +
                "ObjectPolymorphicConverter can resolve `$type=CodeConfiguration` " +
                "back to a CodeConfiguration instance — otherwise the source " +
                "query returns Code MeshNodes whose Content is JsonElement, " +
                "MeshNodeCompilationService skips them in the " +
                "`n.Content is CodeConfiguration cf` filter, and Roslyn " +
                "compiles an empty assembly.");
        typeInfo!.Type.Should().Be(typeof(CodeConfiguration));
    }

    /// <summary>
    /// End-to-end JSON round-trip: serialize a Code MeshNode via the hub's
    /// options (the way Postgres' write path does), then deserialize via the
    /// same options (the way <c>PostgreSqlStorageAdapter.ReadMeshNode</c>
    /// does). The deserialized Content must come back as a strongly-typed
    /// <see cref="CodeConfiguration"/>, not as <see cref="JsonElement"/>.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public void CodeMeshNode_RoundTripsThroughJsonOptions_AsCodeConfiguration()
    {
        var options = Mesh.JsonSerializerOptions;
        var code = new CodeConfiguration { Code = "public class X {}", Language = "csharp" };

        // Mirror PostgreSqlStorageAdapter.ReadMeshNode: serialize the
        // CONTENT (not the wrapping MeshNode), then deserialize as `object`
        // so polymorphism is the only resolution path.
        var contentJson = JsonSerializer.Serialize<object>(code, options);
        var roundTripped = JsonSerializer.Deserialize<object>(contentJson, options);

        roundTripped.Should().BeOfType<CodeConfiguration>(
            "PROD compile failure trace: when the round-trip yields " +
            "JsonElement instead of CodeConfiguration, the source-discovery " +
            "filter `n.Content is CodeConfiguration cf` drops the node, and " +
            "Roslyn sees zero source files → '⚠ Compilation failed' shows up " +
            "with no specific error.");
        ((CodeConfiguration)roundTripped!).Code.Should().Be("public class X {}");
    }

    /// <summary>
    /// The opposite assertion: if <see cref="CodeConfiguration"/> were NOT
    /// registered in the type registry, the round-trip would produce
    /// <see cref="JsonElement"/>. This locks in that the polymorphic
    /// converter's fallback IS JsonElement (the production failure surface),
    /// so a future regression that strips the registration would be caught
    /// by <see cref="CodeMeshNode_RoundTripsThroughJsonOptions_AsCodeConfiguration"/>.
    /// </summary>
    [Fact(Timeout = 10_000)]
    public void UnregisteredType_RoundTripsAs_JsonElement_NotStrongType()
    {
        // Use a stand-in registry that intentionally returns "not found" for
        // any type lookup — the production failure surface when a hub didn't
        // register CodeConfiguration.
        var emptyRegistry = Substitute.For<ITypeRegistry>();
        emptyRegistry.TryGetType(Arg.Any<string>(), out Arg.Any<ITypeDefinition?>())
            .Returns(call =>
            {
                call[1] = null;
                return false;
            });
        var options = new JsonSerializerOptions
        {
            Converters = { new ObjectPolymorphicConverter(emptyRegistry) }
        };

        // Wire the JSON manually with a $type the empty registry can't resolve.
        var contentJson = """
            {
              "$type": "CodeConfiguration",
              "code": "public class X {}",
              "language": "csharp"
            }
            """;
        var roundTripped = JsonSerializer.Deserialize<object>(contentJson, options);

        roundTripped.Should().BeOfType<JsonElement>(
            "with an unregistered $type, ObjectPolymorphicConverter falls " +
            "back to JsonElement — that's the exact production failure " +
            "shape this test pins.");
    }
}
