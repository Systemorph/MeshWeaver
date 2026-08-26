using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MeshWeaver.AI.Test;

/// <summary>
/// Address resolution over THREAD nodes and their satellites — a thread at
/// <c>User/{u}/_Thread/{id}</c> and a message beneath it.
///
/// <para>Moved out of <c>MeshWeaver.PathResolution.Test.AddressResolutionTest</c> (#2276). The
/// resolution mechanism itself is core and its coverage stays there; these two cases could not,
/// because their fixture is not incidental — they build <c>AI.Thread</c> CONTENT under a
/// <c>_Thread</c> satellite path, and the thing being resolved is that shape. Substituting a
/// non-AI node type would have changed the scenario rather than the fixture.</para>
/// </summary>
public class ThreadAddressResolutionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        // AddAI(), as the original fixture had: these cases resolve a thread AND a ThreadMessage
        // beneath it, and AddThreadType() alone leaves ThreadMessage unregistered. In AI's own
        // suite the full registration is the natural one.
        => ConfigureMeshBase(builder).AddAI();

    /// <summary>
    /// Verifies that the Thread node itself still resolves correctly
    /// when ThreadMessage children exist.
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ResolvePath_ThreadNode_ResolvesCorrectlyWithChildren()
    {
        var threadPath = "User/Roland/_Thread/test-parent-5678";
        var threadNode = new MeshNode("test-parent-5678", "User/Roland/_Thread")
        {
            Name = "Test Parent",
            NodeType = ThreadNodeType.NodeType,
            Content = new AI.Thread
            {
                Messages = ["m1"]
            }
        };
        await NodeFactory.CreateNode(threadNode).Should().Emit();

        var msgNode = new MeshNode("m1", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Order = 1,
            Content = new ThreadMessage
            {
                Role = "assistant",
                Text = "Response",
                Timestamp = System.DateTime.UtcNow,
                Type = ThreadMessageType.AgentResponse
            }
        };
        await NodeFactory.CreateNode(msgNode).Should().Emit();

        // Resolve the Thread path — should match the Thread node exactly
        var resolution = await PathResolver.ResolvePath(threadPath).Should().Emit();

        resolution.Should().NotBeNull();
        resolution!.Prefix.Should().Be(threadPath);
        resolution.Remainder.Should().BeNull();
    }

    /// <summary>
    /// Creates a Thread with a ThreadMessage child and verifies path resolution
    /// resolves the full ThreadMessage path (not the Thread path with remainder).
    /// </summary>
    [Fact(Timeout = 30000)]
    public async Task ResolvePath_ThreadMessageNode_ResolvesToFullMessagePath()
    {
        // Create Thread node
        var threadPath = "User/Roland/_Thread/test-resolution-1234";
        var threadNode = new MeshNode("test-resolution-1234", "User/Roland/_Thread")
        {
            Name = "Test Resolution",
            NodeType = ThreadNodeType.NodeType,
            Content = new AI.Thread
            {
                Messages = ["msg1"]
            }
        };
        await NodeFactory.CreateNode(threadNode).Should().Emit();

        // Create ThreadMessage child node
        var msgNode = new MeshNode("msg1", threadPath)
        {
            NodeType = ThreadMessageNodeType.NodeType,
            Order = 1,
            Content = new ThreadMessage
            {
                Role = "user",
                Text = "Hello",
                Timestamp = System.DateTime.UtcNow,
                Type = ThreadMessageType.ExecutedInput
            }
        };
        await NodeFactory.CreateNode(msgNode).Should().Emit();

        // Resolve the ThreadMessage path — should return the full message path, no remainder
        var resolution = await PathResolver.ResolvePath($"{threadPath}/msg1").Should().Emit();

        resolution.Should().NotBeNull("ThreadMessage node exists at {0}/msg1", threadPath);
        resolution!.Prefix.Should().Be($"{threadPath}/msg1",
            "should resolve to the full ThreadMessage path, not the Thread path with remainder");
        resolution.Remainder.Should().BeNull("exact match should have no remainder");
    }

}
