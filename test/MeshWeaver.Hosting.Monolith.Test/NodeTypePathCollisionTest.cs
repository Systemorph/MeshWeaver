using System;
using System.Reactive.Linq;
using System.Threading.Tasks;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Hosting.Monolith.TestBase;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using MeshWeaver.Fixture;

namespace MeshWeaver.Hosting.Monolith.Test;

/// <summary>
/// 🚨 A node existing at a NodeType's path is not the same thing as a NodeType being REGISTERED
/// there — and the two diverge the moment something else claims the name.
///
/// <para>On memex-cloud a Store plugin installed its root at the bare path <c>Feedback</c>
/// (content <c>PluginContent</c>, nodeType <c>Store/Plugin</c>) while the plugin's actual NodeType
/// lived at <c>Feedback/Feedback</c>. An instance naming the bare <c>Feedback</c> passed
/// <c>EnrichWithNodeType</c>'s existence probe on the PLUGIN ROOT — the probe only counted rows —
/// and then spent the whole 60 s slow-path budget waiting for a <c>PluginContent</c> node to look
/// like a <see cref="NodeTypeDefinition"/>, ending on the "there was a compilation error, please
/// correct the code" overlay for source that is perfectly fine. The only evidence was one
/// unexplained <c>As&lt;NodeTypeDefinition&gt; for Feedback: value is PluginContent</c> line
/// (Systemorph/MeshWeaver#2230/#2231).</para>
///
/// <para>The fix is not a filter that silently skips the occupant: the probe now answers the
/// question it is actually asking, and a collision is reported by NAME — both sides of it — fast
/// enough that nobody waits a minute to be told the wrong thing.</para>
/// </summary>
public class NodeTypePathCollisionTest(ITestOutputHelper output) : MonolithMeshTestBase(output)
{
    /// <summary>The NodeType of the node that SQUATS on the path — the Store/Plugin analogue.</summary>
    private const string SquatterType = "Squatter";

    /// <summary>The bare path both the squatter and the (absent) NodeType want.</summary>
    private const string ContestedPath = "Contested";

    /// <summary>Content of the squatting node — a typed record that is NOT a NodeTypeDefinition.</summary>
    public record SquatterContent
    {
        /// <summary>Arbitrary payload, so the record is not empty.</summary>
        public string? Label { get; init; }
    }

    /// <inheritdoc />
    protected override MeshBuilder ConfigureMesh(MeshBuilder builder)
        => base.ConfigureMesh(builder)
            .AddMeshNodes(new MeshNode(SquatterType)
            {
                Name = "Squatter",
                NodeType = MeshNode.NodeTypePath,
                Content = new NodeTypeDefinition(),
                HubConfiguration = config => config
                    .AddMeshDataSource(source => source.WithContentType<SquatterContent>())
            })
            // The mesh hub must resolve the squatter's `$type` for itself, or the probe reads the
            // occupant's content as untyped JSON — which deserialises into a NodeTypeDefinition
            // quite happily and would make this test pass for the wrong reason.
            .ConfigureHub(config => config.WithType<SquatterContent>(nameof(SquatterContent)));

    /// <summary>
    /// 🚨 A FRESH mesh per [Fact]: the assertion is about a cold enrichment of one node, and a
    /// hub bound by a previous test would short-circuit on its existing HubConfiguration.
    /// </summary>
    protected override bool ShareMeshAcrossTests => false;

    /// <summary>
    /// An instance whose NodeType names a path occupied by something that is NOT a declaration
    /// must be told exactly that — naming the occupant — and must be told it inside the probe's
    /// budget, not the 60 s slow-path one.
    /// </summary>
    [Fact(Timeout = 120_000)]
    public async Task AnOccupiedNodeTypePath_IsReportedAsACollision_NotWaitedOutForAMinute()
    {
        var ct = TestContext.Current.CancellationToken;
        var meshService = Mesh.ServiceProvider.GetRequiredService<IMeshService>();
        var access = Mesh.ServiceProvider.GetRequiredService<AccessService>();

        using (access.ImpersonateAsSystem())
        {
            // The squatter: a real node, at the bare path, that is not a NodeType declaration.
            await meshService.CreateNode(MeshNode.FromPath(ContestedPath) with
            {
                Name = "Contested",
                NodeType = SquatterType,
                State = MeshNodeState.Active,
                Content = new SquatterContent { Label = "plugin root analogue" }
            }).Should().Within(60.Seconds()).Emit();
        }

        var instance = MeshNode.FromPath($"{TestPartition}/collides") with
        {
            Name = "Collides",
            NodeType = ContestedPath,
            State = MeshNodeState.Active,
        };

        var factory = Mesh.ServiceProvider.GetRequiredService<IMeshNodeHubFactory>();

        // 🚨 30 s is under EnrichWithNodeType's 60 s SlowPathTimeout ON PURPOSE. Before the fix
        // the probe accepted the squatter as a registration and the chain waited that whole budget
        // out; a passing run here is itself part of the assertion.
        var enriched = await factory.ResolveHubConfiguration(instance)
            .FirstAsync()
            .Timeout(30.Seconds())
            .Await(ct);

        enriched.HubConfiguration.Should().NotBeNull(
            "an unresolvable NodeType still yields the overlay configuration, never a null hub");

        var configuration = enriched.HubConfiguration!(
            new MessageHubConfiguration(Mesh.ServiceProvider, new Address("test", "collision")));
        var nack = configuration.Get<UnhandledMessageNack>();

        Output.WriteLine($"nack: {nack?.Reason ?? "(none)"}");

        nack.Should().NotBeNull(
            "the overlay sets an UnhandledMessageNack so typed requests to the instance fail fast "
            + "with the cause instead of parking");
        nack!.Reason.Should().Contain(ContestedPath);
        nack.Reason.Should().Contain(SquatterType,
            "the collision must name what actually occupies the path — the whole defect was that "
            + "the only trace was a bare `As<NodeTypeDefinition>` line with no second side to it");
        nack.Reason.Should().NotContain("compilation error",
            "nothing failed to compile — telling the author to correct their code sends them after "
            + "a defect that is not there");
    }
}
