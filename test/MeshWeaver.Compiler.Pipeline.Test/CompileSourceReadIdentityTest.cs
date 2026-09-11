using System.Reflection;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using MeshWeaver.Fixture;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.Compiler.Pipeline.Test;

/// <summary>
/// Executes the compiler's actual cold reader against a real mesh. A successful infrastructure
/// read must leave both the subscribing flow and its downstream callback under their own identity.
/// The fallback case starts a second read from the first response, preserving the per-read scope.
/// </summary>
public class CompileSourceReadIdentityTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    [Theory(Timeout = 120_000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompileSourceRead_DoesNotGiveItsSystemIdentityToItsCaller(bool useFallback)
    {
        var path = "type/CompileReadIdentity" + Guid.NewGuid().ToString("N");
        await Mesh.ServiceProvider.GetRequiredService<IMeshService>().CreateNode(MeshNode.FromPath(path) with
            { NodeType = "Markdown", Content = "Compiler read identity probe" })
            .Should().Within(TestTimeouts.Convergence).Emit();
        var compiler = Mesh.ServiceProvider.GetRequiredService<IMeshNodeCompilationService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();
        using var caller = access.SwitchAccessContext(new AccessContext
            { ObjectId = "compile-read-caller", Name = "Compile read caller" });

        // Invoke the existing private boundary rather than duplicate its scoping idiom in a test.
        // Reflection keeps this regression from widening a production API solely for tests.
        IObservable<MeshNode?> read;
        if (useFallback)
        {
            var method = Assert.IsAssignableFrom<MethodInfo>(compiler.GetType().GetMethod(
                "ReadIncludeNode", BindingFlags.Instance | BindingFlags.NonPublic));
            var fallbackRead = Assert.IsAssignableFrom<IObservable<(MeshNode? Node, string Path)>>(
                method.Invoke(compiler, [path + "-absent", path]));
            read = fallbackRead.Do(found => found.Path.Should().Be(path)).Select(found => found.Node);
        }
        else
        {
            var method = Assert.IsAssignableFrom<MethodInfo>(compiler.GetType().GetMethod(
                "ReadCompileSourceNode", BindingFlags.Instance | BindingFlags.NonPublic));
            read = Assert.IsAssignableFrom<IObservable<MeshNode?>>(
                method.Invoke(compiler, [path, ReadTimeoutBehavior.Throw]));
        }

        using var outcome = new AsyncSubject<(MeshNode? Node, string? Identity)>();
        using var subscription = read.Select(node => (node, access.Context?.ObjectId)).Subscribe(outcome);
        access.Context?.ObjectId.Should().Be("compile-read-caller",
            "the synchronous Subscribe must close the infrastructure identity on this flow");
        var result = await outcome.Should().Within(TestTimeouts.Convergence).Emit();
        result.Node.Should().NotBeNull("the scoped infrastructure read still has to reach the node");
        result.Node!.Path.Should().Be(path);
        result.Identity.Should().Be("compile-read-caller",
            "a caller composed after the read must not inherit its system privilege");
    }
}
